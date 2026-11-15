"""
Martian Map - environment alerts.

Two separate parts, so the alerts come from the data and not from a script:

1. Scenarios (scripted, in events.json as "type": "scenario"). The real sol was calm: no dust
   storm, no solar storm. A scenario changes the replayed readings the way a real event would
   (dust dims the UV and makes the pressure dip; a solar particle event raises the dose rate),
   shaped on measured events (docs/ALERTS.md has the sources). Telemetry carries a "scenario"
   field while one is changing the data, so the HUD can say so.

2. AlertDetector. Sees only the telemetry the feeder sends, the way a suit computer would, and
   compares it with what the real sol measured at the same local time (its "expected" curve):
     dust       UV lower than expected -> estimated extra dust opacity and how fast it rises
                -> DUST WATCH, then DUST WARNING with an arrival estimate (the early warning),
                then DUST STORM. Pressure dips (dust devils lifting dust) back it up.
     radiation  dose rate above 1.25x the sol's normal -> possible solar particle event,
                confirmed -> end the EVA, at/above the shelter dose rate -> shelter.
     uv         an estimated UV index from the REMS UV bands -> fixed UV index limits.
   Each alert is a JSON message: {"type": "alert", "category", "level", "title", "text", ...}
   with level advisory | warning | danger | clear (clear removes it from the HUD).

Only the standard library. feeder.py drives it; `python feeder.py --timeline` prints the alerts
the demo will show, without a server.
"""
import math

LEVELS = ("clear", "advisory", "warning", "danger")
RANK = {name: i for i, name in enumerate(LEVELS)}

# ---------------------------------------------------------------------------------------------
# Thresholds. docs/ALERTS.md explains each number and where it comes from.
# ---------------------------------------------------------------------------------------------

# Dust. "Opacity" = extra dust optical depth (tau) above the sol's own, estimated from UV.
DUST = dict(
    k_uv=0.4,               # total UV ~ normal x exp(-0.4 x extra tau)  (fit to REMS in the 2018 storm)
    min_expected_uv=0.5,    # W/m^2 (UV_ABC): below this the sun is too low to judge
    median_min=5.0,         # opacity = median of the last 5 EVA min (ignores one-off dips)
    trend_window_min=20.0,  # EVA minutes of opacity history for the rising trend
    watch_tau=0.10,         # advisory: a "large" short dust event (Smith et al.) ...
    watch_rate_per_h=0.2,   #   ... and still rising
    lifting_dip_pa=2.5,     # advisory also: a pressure dip this deep = dust devil lifting dust
    devil_dip_pa=0.5,       # dips counted as dust-devil activity (REMS detection threshold)
    warning_uv_drop=30.0,   # warning: UV down this many % (~+0.9 opacity) ...
    warning_tau=0.25,       #   ... or this much dust and ...
    eta_warning_min=30.0,   #   ... storm conditions projected within this many EVA minutes
    storm_tau=1.5,          # danger: total opacity ~2 = local dust storm (UV down ~45%)
    clear_tau=0.10,         # back below this for clear_hold_min = clear
    clear_hold_min=5.0,
    eta_resend_min=5.0,     # re-send the warning when the predicted arrival moves this much
)

# Radiation: dose rate in micrograys per hour (RAD detector E, close to tissue).
RADIATION = dict(
    onset_ratio=1.25,       # advisory: 25% above normal (RAD team's SEP trigger, Lowe et al.)
    confirm_min=5.0,        # warning: still above it this many EVA minutes later (confirmed) ...
    warning_ratio=2.0,      #   ... or twice normal
    danger_uGy_h=150.0,     # danger: NASA/SRAG Orion shelter level (2.5 uGy/min)
    clear_hold_min=10.0,    # back under onset_ratio this long = clear
)

# UV: estimated UV index (WHO scale: 40 x sunburn-weighted UV in W/m^2).
UV = dict(
    # REMS RNV channel -> full-band irradiance (our comparison with Viudez-Moreiras 2025, PNAS)
    band_scale=dict(uv_c_wm2=2.2, uv_b_wm2=3.2, uv_a_wm2=5.5),
    # Sunburn (CIE erythemal) weight averaged over each band
    weights=dict(uv_c_wm2=1.0, uv_b_wm2=0.5, uv_a_wm2=0.002),
    advisory_index=11.0,    # WHO "extreme"
    warning_index=43.0,     # above the highest UV index recorded on Earth (43.3, Licancabur 2003)
    danger_index=150.0,     # Gale's typical midday level (bare skin: a sunburn dose in < 30 s)
    hysteresis=0.8,         # drop a level only below 80% of its threshold ...
    down_hold_min=20.0,     #   ... for this many EVA minutes
)


# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------

def interp(rows, t_min, key):
    """Linear interpolation of one column of the sol's samples at EVA minute t_min."""
    if t_min <= rows[0]["t_eva_min"]:
        return rows[0][key]
    for a, b in zip(rows, rows[1:]):
        if a["t_eva_min"] <= t_min <= b["t_eva_min"]:
            span = b["t_eva_min"] - a["t_eva_min"] or 1.0
            w = (t_min - a["t_eva_min"]) / span
            return a[key] + w * (b[key] - a[key])
    return rows[-1][key]


def erythemal_wm2(row):
    """Sunburn-weighted UV (W/m^2) from the REMS bands, or None if they aren't there."""
    if not all(k in row for k in UV["weights"]):
        return None
    return sum(row[k] * UV["band_scale"][k] * w for k, w in UV["weights"].items())


def uv_index(row):
    e = erythemal_wm2(row)
    return None if e is None else max(0.0, 40.0 * e)


def smoothstep(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3.0 - 2.0 * x)


def _median(values):
    v = sorted(values)
    n = len(v)
    return v[n // 2] if n % 2 else 0.5 * (v[n // 2 - 1] + v[n // 2])


def _slope(pts):
    """Least-squares slope (y per x) of [(x, y), ...]."""
    n = len(pts)
    mx = sum(p[0] for p in pts) / n
    my = sum(p[1] for p in pts) / n
    sxx = sum((p[0] - mx) ** 2 for p in pts)
    return 0.0 if sxx == 0 else sum((p[0] - mx) * (p[1] - my) for p in pts) / sxx


# ---------------------------------------------------------------------------------------------
# 1. Scenarios
# ---------------------------------------------------------------------------------------------

class Scenarios:
    """The "type": "scenario" entries of events.json, applied to each telemetry sample."""

    def __init__(self, events):
        self.items = [e for e in events if e.get("type") == "scenario"]
        for s in self.items:
            if s.get("id") not in ("dust_front", "solar_particle_event"):
                raise ValueError(f"Unknown scenario id {s.get('id')!r} in events.json")

    @staticmethod
    def dust_tau(s, t):
        """Extra dust opacity of a dust_front at EVA minute t."""
        start, arrive = s["t_eva_min"], s["arrive_min"]
        clear, end = s["clear_min"], s["end_min"]
        at_arrival, peak = s["tau_at_arrival"], s["tau_peak"]
        if t <= start or t >= end:
            return 0.0
        if t < arrive:                       # the front builds, faster and faster
            return at_arrival * ((t - start) / (arrive - start)) ** 2
        ramp = s.get("ramp_min", 10.0)
        if t < arrive + ramp:                # the gust front itself
            return at_arrival + (peak - at_arrival) * smoothstep((t - arrive) / ramp)
        if t < clear:
            return peak
        return peak * (1.0 - smoothstep((t - clear) / (end - clear)))

    @staticmethod
    def pressure_change(s, t):
        """Pa added to the pressure: a shallow drop as the front nears, plus short dust-devil
        dips ("dips": [[minutes before arrival, depth Pa], ...], each ~3 min wide)."""
        f = Scenarios.dust_tau(s, t) / s["tau_peak"]
        dp = -s.get("pressure_drop_pa", 0.0) * f
        for before, depth in s.get("dips", []):
            if abs(t - (s["arrive_min"] - before)) <= 1.5:
                dp -= depth
        return dp

    @staticmethod
    def spe_extra(s, t):
        """Extra dose rate (uGy/h) of a solar particle event at EVA minute t: a cubic rise to
        the peak (slow at first, like the real onsets), then a slow exponential decline."""
        onset, peak_t = s["t_eva_min"], s["peak_min"]
        if t <= onset:
            return 0.0
        if t < peak_t:
            return s["peak_uGy_h"] * ((t - onset) / (peak_t - onset)) ** 3
        return s["peak_uGy_h"] * math.exp(-(t - peak_t) / s["decay_min"])

    def apply(self, t, row):
        """Returns (changed copy of row, [ids of scenarios changing the data now])."""
        out, active = dict(row), []
        for s in self.items:
            if s["id"] == "dust_front":
                dtau = self.dust_tau(s, t)
                dp = self.pressure_change(s, t)
                if dtau <= 0 and dp == 0:
                    continue
                active.append(s["id"])
                dim = math.exp(-DUST["k_uv"] * dtau)
                for k in list(out):
                    if k.startswith("uv_") and k.endswith("_wm2"):
                        out[k] = row[k] * dim
                if "pressure_pa" in out:
                    out["pressure_pa"] = row["pressure_pa"] + dp
                if "temp_c" in out:
                    out["temp_c"] = row["temp_c"] - s.get("temp_drop_c", 0.0) * dtau / s["tau_peak"]
            elif s["id"] == "solar_particle_event":
                extra = self.spe_extra(s, t)
                if extra <= 0.01:
                    continue
                active.append(s["id"])
                out["dose_rate_uGy_per_h"] = row["dose_rate_uGy_per_h"] + extra
        return out, active


# ---------------------------------------------------------------------------------------------
# 2. Detector
# ---------------------------------------------------------------------------------------------

class AlertDetector:
    """Feed it each telemetry message (dict with t_eva_s, in order). It adds its estimates to
    the message (uv_index, dust_tau, pressure_anomaly_pa, dose_expected_uGy_h) and returns the
    alert messages to send, if any."""

    def __init__(self, expected):
        self.expected = expected            # the real sol's samples (never changed by scenarios)
        self.level = {"dust": "clear", "radiation": "clear", "uv": "clear"}
        self.tau_raw = []                   # (t_min, opacity estimate from one sample)
        self.tau_hist = []                  # (t_min, median opacity)
        self.below_since = {}               # category -> EVA minute it went back to normal
        self.above_since = None             # EVA minute the dose rate went above onset_ratio
        self.last_arrival = None            # EVA minute the dust storm was last predicted for
        self.lifting_noted = False
        self.uv_low_since = None            # EVA minute UV fell below the current level
        self.has_uv = "uv_abc_wm2" in expected[0]

    def update(self, msg):
        t = msg["t_eva_s"] / 60.0
        return self._dust(t, msg) + self._radiation(t, msg) + self._uv(t, msg)

    # --- helpers ---
    def _alert(self, msg, category, level, title, text, update=False, **extra):
        changed = level != self.level[category]
        self.level[category] = level
        a = {"type": "alert", "t_eva_s": msg["t_eva_s"], "category": category, "level": level,
             "title": title, "text": text, "update": update and not changed}
        a.update({k: v for k, v in extra.items() if v is not None})
        return a

    def _cleared(self, category, t, normal, hold):
        """True once readings have been normal for `hold` EVA minutes in a row."""
        if not normal:
            self.below_since.pop(category, None)
            return False
        return t - self.below_since.setdefault(category, t) >= hold

    # --- dust ---
    def _dust(self, t, msg):
        p_anom = (msg["pressure_pa"] - interp(self.expected, t, "pressure_pa")
                  if "pressure_pa" in msg else 0.0)
        msg["pressure_anomaly_pa"] = round(p_anom, 2)
        if not self.has_uv or "uv_abc_wm2" not in msg:
            return []
        exp_uv = interp(self.expected, t, "uv_abc_wm2")
        if exp_uv < DUST["min_expected_uv"]:
            return []
        one = max(0.0, math.log(exp_uv / max(msg["uv_abc_wm2"], 1e-6)) / DUST["k_uv"])
        self.tau_raw = [(a, b) for a, b in self.tau_raw if t - a < DUST["median_min"]] + [(t, one)]
        tau = _median([b for _, b in self.tau_raw])
        msg["dust_tau"] = round(tau, 2)
        self.tau_hist = [(a, b) for a, b in self.tau_hist if t - a <= DUST["trend_window_min"]] + [(t, tau)]
        rate = _slope(self.tau_hist) * 60.0 if len(self.tau_hist) >= 3 else 0.0  # per hour
        uv_drop = 100.0 * (1.0 - math.exp(-DUST["k_uv"] * tau))
        eta = (DUST["storm_tau"] - tau) / (rate / 60.0) if rate > 0.05 and tau < DUST["storm_tau"] else None
        lifting = -p_anom >= DUST["lifting_dip_pa"]
        cur = self.level["dust"]

        if tau >= DUST["storm_tau"] or (cur == "danger" and tau >= DUST["clear_tau"]):
            new = "danger"                  # stays until the dust has nearly gone (no flicker)
        elif uv_drop >= DUST["warning_uv_drop"] or (
                tau >= DUST["warning_tau"] and eta is not None and eta <= DUST["eta_warning_min"]):
            new = "warning"
        elif (tau >= DUST["watch_tau"] and rate >= DUST["watch_rate_per_h"]) or lifting:
            new = "advisory"
        elif cur != "clear" and tau >= DUST["clear_tau"]:
            new = cur                       # keep it while the dust is still there
        else:
            new = "clear"

        if new == "clear":
            if cur == "clear" or not self._cleared("dust", t, True, DUST["clear_hold_min"]):
                return []
            self.last_arrival = None
            return [self._alert(msg, "dust", "clear", "DUST CLEARED",
                                "UV is back to normal for this time of day.")]
        self._cleared("dust", t, False, 0)
        if RANK[new] < RANK[cur]:
            new = cur                       # never step down while dust is still around

        dips = ""
        if lifting:
            dips = f" Pressure dip {p_anom:.1f} Pa: a dust devil is lifting dust nearby."
        elif -p_anom >= DUST["devil_dip_pa"]:
            dips = f" Pressure {abs(p_anom):.1f} Pa below normal."
        if new == "danger":
            if cur == "danger":
                return []
            return [self._alert(msg, "dust", "danger", "DUST STORM",
                                f"UV down {uv_drop:.0f}% (dust opacity +{tau:.1f}): local dust storm, "
                                "visibility low. Follow the home bearing and stay on the planned route.",
                                value=round(tau, 2))]
        if new == "warning":
            eta_min = None if eta is None else max(1, round(eta))
            arrival = None if eta_min is None else t + eta_min
            # The HUD counts the arrival down itself; re-send only on a level change or when
            # the predicted arrival moves by eta_resend_min or more
            if cur == "warning" and (arrival is None or self.last_arrival is None
                                     or abs(arrival - self.last_arrival) < DUST["eta_resend_min"]):
                return []
            self.last_arrival = arrival
            return [self._alert(msg, "dust", "warning", "DUST WARNING",
                                f"Dust storm approaching: UV down {uv_drop:.0f}% from normal, opacity "
                                f"+{tau:.2f} and rising ({rate:+.1f}/h).{dips} Head toward Home Base.",
                                update=True, value=round(tau, 2), eta_min=eta_min)]
        if cur == "advisory":
            if not lifting or self.lifting_noted:
                return []
            self.lifting_noted = True       # same level, new detail: update the card
            return [self._alert(msg, "dust", "advisory", "DUST WATCH",
                                f"UV {uv_drop:.0f}% below normal, opacity +{tau:.2f}.{dips}",
                                update=True, value=round(tau, 2))]
        self.lifting_noted = lifting
        return [self._alert(msg, "dust", "advisory", "DUST WATCH",
                            f"UV {uv_drop:.0f}% below normal for this time of day: dust building "
                            f"(opacity +{tau:.2f}, {rate:+.1f}/h).{dips}", value=round(tau, 2))]

    # --- radiation ---
    def _radiation(self, t, msg):
        rate = msg.get("dose_rate_uGy_h")
        if rate is None:
            return []
        normal = interp(self.expected, t, "dose_rate_uGy_per_h")
        msg["dose_expected_uGy_h"] = round(normal, 2)
        ratio = rate / max(normal, 1e-6)
        dose = msg.get("dose_total_mGy", 0.0)
        cur = self.level["radiation"]

        above = ratio >= RADIATION["onset_ratio"]
        if above and self.above_since is None:
            self.above_since = t
        elif not above:
            self.above_since = None
        confirmed = above and t - self.above_since >= RADIATION["confirm_min"]

        if rate >= RADIATION["danger_uGy_h"]:
            new = "danger"
        elif confirmed or ratio >= RADIATION["warning_ratio"]:
            new = "warning"
        elif above:
            new = "advisory"
        else:
            new = "clear"

        if new == "clear":
            if cur == "clear" or not self._cleared("radiation", t, True, RADIATION["clear_hold_min"]):
                return []
            return [self._alert(msg, "radiation", "clear", "RADIATION NORMAL",
                                f"Dose rate {rate:.1f} uGy/h, back to normal.")]
        self._cleared("radiation", t, False, 0)
        if RANK[new] <= RANK[cur]:          # only escalate while it's still high
            return []
        per_day = rate * 24.0 / 1000.0
        text = {
            "advisory": f"Dose rate {rate:.1f} uGy/h, {ratio:.2f}x normal: a solar particle event may "
                        "be starting. Get ready to end the EVA; stay within 30 min of shelter.",
            "warning": f"Solar particle event confirmed: {rate:.0f} uGy/h, {ratio:.1f}x normal and "
                       "rising. End the EVA and head back now: most of an event's dose comes after "
                       "the first hour.",
            "danger": f"Dose rate {rate:.0f} uGy/h ({ratio:.0f}x normal, {per_day:.1f} mGy/day at "
                      f"this rate), above NASA's shelter level of {RADIATION['danger_uGy_h']:.0f} "
                      f"uGy/h. EVA dose {dose:.3f} mGy. Get into shelter.",
        }[new]
        title = {"advisory": "RADIATION RISING", "warning": "SOLAR PARTICLE EVENT",
                 "danger": "RADIATION: SHELTER NOW"}[new]
        return [self._alert(msg, "radiation", new, title, text, value=round(rate, 1))]

    # --- UV ---
    def _uv(self, t, msg):
        uvi = uv_index(msg)
        if uvi is None:
            return []
        msg["uv_index"] = round(uvi, 1)
        cur = self.level["uv"]
        th = {"advisory": UV["advisory_index"], "warning": UV["warning_index"], "danger": UV["danger_index"]}
        new = "clear"
        for lv in ("advisory", "warning", "danger"):
            # Climb at the threshold; hold the current level until well below it
            if uvi >= th[lv] or (RANK[cur] >= RANK[lv] and uvi >= th[lv] * UV["hysteresis"]):
                new = lv
        if RANK[new] < RANK[cur]:
            # Step down only after UV has stayed lower for down_hold_min (no flicker)
            self.uv_low_since = t if self.uv_low_since is None else self.uv_low_since
            if t - self.uv_low_since < UV["down_hold_min"]:
                return []
        self.uv_low_since = None
        if new == cur:
            return []
        if new == "clear":
            return [self._alert(msg, "uv", "clear", "UV NORMAL", f"UV index ~{uvi:.0f}.")]
        sed_s = 100.0 / (uvi / 40.0)        # seconds for bare skin to get 1 sunburn dose (100 J/m^2)
        burn = f"{sed_s:.0f} s" if sed_s < 120 else f"{sed_s / 60:.0f} min"
        uvc = msg.get("uv_c_wm2")
        uvc_note = " Includes UV-C, which never reaches the ground on Earth." if uvc else ""
        title = {"advisory": "UV EXTREME", "warning": "UV BEYOND EARTH RECORD", "danger": "UV DANGER"}[new]
        if RANK[new] < RANK[cur]:
            return [self._alert(msg, "uv", new, title, f"UV index down to ~{uvi:.0f}.", value=round(uvi, 1))]
        text = {
            "advisory": f"UV index ~{uvi:.0f} (11+ is 'extreme' on Earth). Keep the visor's UV filter "
                        f"down.{uvc_note}",
            "warning": f"UV index ~{uvi:.0f}, above the highest ever recorded on Earth (43). Bare skin "
                       f"would get a sunburn dose in ~{burn}. Visor filter down, check glove and neck "
                       f"seals.{uvc_note}",
            "danger": f"UV index ~{uvi:.0f}: bare skin would get a sunburn dose in ~{burn}. The suit "
                      f"blocks it; any tear or open seal would not. Minimise time facing the sun."
                      f"{uvc_note}",
        }[new]
        return [self._alert(msg, "uv", new, title, text, value=round(uvi, 1))]
