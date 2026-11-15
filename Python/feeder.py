"""
Martian Map - telemetry feeder.

Replays real Curiosity data (RAD radiation + REMS weather from one sol) and
scripted EVA events onto the EVA clock, and sends them as JSON messages to the
team's WebSocket server (server.py). Unity listens to that server and updates the HUD.

Static map data (heightmap, photo, cost grid, stops) does NOT go over the
socket - Unity loads those files at startup. The socket carries only what
would be "live" on a real Marswalk.

Inputs:
  sol_samples.csv : made by fetch_sol.py from real RAD + REMS data. Columns:
                    t_eva_min, dose_rate_uGy_per_h (required), then any other numeric
                    columns (lmst_h, temp_c, pressure_pa, ...) which are sent as-is.
  sol_samples.meta.json : written next to the CSV by fetch_sol.py; supplies "source".
  events.json     : list of {"t_eva_min", "type", ...} scripted events (hand-written)

Usage:
  .venv/bin/pip install -r Python/requirements.txt   (once, from the Marsmap folder)
  python feeder.py                       # demo pace: 4-hour EVA in 48 s
  python feeder.py --speed 80            # slower: 3 minutes
    --speed is EVA minutes per real minute. At the default 300 the events land at
    ~8 s (Earth note), ~20 s (dust gust, lasts duration_s) and ~35 s (nav degraded).

Message format (agree this with whoever owns the server and the Unity side;
the Unity classes live in Assets/SourceFiles/Scripts/PythonConnection.cs):
  every message is JSON with "v" (schema version) and "type".
  hello     : {"v":1,"type":"hello","from":"feeder"}  (first message; marks this client as a data source)
  telemetry : {"v":1,"type":"telemetry","t_eva_s":..,"lmst_h":..,"dose_rate_uGy_h":..,
               "dose_total_mGy":..,"temp_c":..,"pressure_pa":..,
               "source":"MSL RAD+REMS sol NNNN (YYYY-MM-DD), replayed"}
              lmst_h = Mars local mean solar time in hours (10.5 = 10:30).
              No wind: Curiosity's wind sensor has no data for these sols.
  event     : {"v":1,"type":"event","t_eva_s":..,"id":"dust_gust",..}
  earth_note: {"v":1,"type":"earth_note","t_eva_s":..,"text":"..","delay_s":..}
  status    : {"v":1,"type":"status","state":"started"|"finished"}
  alert     : {"v":1,"type":"alert","t_eva_s":..,"category":"dust"|"radiation"|"uv",
               "level":"advisory"|"warning"|"danger"|"clear","title":..,"text":..,
               "update":bool,"value":..,"eta_min":..}   (from alerts.py; see docs/ALERTS.md)
  telemetry also carries the REMS UV bands (uv_*_wm2), the detector's estimates (uv_index,
  dust_tau, pressure_anomaly_pa, dose_expected_uGy_h) and "scenario" (non-empty while a
  scripted event in events.json is changing the data).

  python feeder.py --timeline   prints every alert/event the replay will send (no server needed)
"""
import os
import sys
from pathlib import Path

# If run with plain `python3`, re-launch with the project's .venv (same as server.py).
_VENV_PY = Path(__file__).resolve().parent.parent / ".venv" / (
    "Scripts/python.exe" if os.name == "nt" else "bin/python")
if (sys.prefix == sys.base_prefix and _VENV_PY.exists()
        and not os.environ.get("MARSMAP_VENV_RELAUNCH")):   # guard: never relaunch twice
    os.environ["MARSMAP_VENV_RELAUNCH"] = "1"
    if os.name == "nt":  # Windows execv doesn't replace the process or quote paths with spaces
        import subprocess
        sys.exit(subprocess.call([str(_VENV_PY), *sys.argv]))
    os.execv(str(_VENV_PY), [str(_VENV_PY), *sys.argv])

import argparse
import asyncio
import csv
import json
import time

from alerts import AlertDetector, Scenarios, interp

try:
    import websockets
except ModuleNotFoundError:
    sys.exit("Missing library: websockets\n"
             "    python3 -m venv .venv\n"
             "    .venv/bin/pip install -r Python/requirements.txt")

SCHEMA_VERSION = 1
# CSV column -> telemetry key, where they differ.
RENAME = {"dose_rate_uGy_per_h": "dose_rate_uGy_h"}


def load_samples(path):
    rows = []
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            rows.append({k: float(v) for k, v in r.items()})
    rows.sort(key=lambda r: r["t_eva_min"])
    return rows


def timeline(samples, events, speed, tick_s, eva_min, source):
    """Every message of the replay, in order, as [(t_eva_min, message dict), ...].
    Deterministic, so the live replay and --timeline show exactly the same thing."""
    scenarios = Scenarios(events)
    detector = AlertDetector(samples)
    pending = sorted((e for e in events if e.get("type") != "scenario"), key=lambda e: e["t_eva_min"])
    out = [(0.0, {"type": "status", "state": "started", "speed": speed})]
    step = tick_s * speed / 60.0               # EVA minutes per tick
    dose_mGy, last_t, i = 0.0, 0.0, 0
    while True:
        t_min = min(i * step, eva_min)
        row = {col: interp(samples, t_min, col) for col in samples[0] if col != "t_eva_min"}
        row, active = scenarios.apply(t_min, row)
        rate = row["dose_rate_uGy_per_h"]
        dose_mGy += rate * ((t_min - last_t) / 60.0) / 1000.0  # uGy/h * h -> mGy
        last_t = t_min
        msg = {"type": "telemetry", "t_eva_s": round(t_min * 60, 1)}
        for col, v in row.items():
            msg[RENAME.get(col, col)] = round(v, 4 if col.startswith("uv_") else 3)
        msg["dose_total_mGy"] = round(dose_mGy, 4)
        msg["scenario"] = ",".join(active)        # non-empty = a scripted event is changing the data
        msg["source"] = source
        alerts = detector.update(msg)             # adds uv_index, dust_tau, ... to msg
        out.append((t_min, msg))
        out += [(t_min, a) for a in alerts]
        while pending and pending[0]["t_eva_min"] <= t_min:
            out.append((t_min, event_message(pending.pop(0), speed)))
        if t_min >= eva_min:
            break
        i += 1
    out.append((eva_min, {"type": "status", "state": "finished", "dose_total_mGy": round(dose_mGy, 4)}))
    return out


def event_message(ev, speed):
    """events.json entry -> the message Unity gets. duration_min (EVA minutes) becomes
    duration_s (real seconds at this replay speed), so effects last as long as the scenario."""
    ev = dict(ev)
    ev["t_eva_s"] = round(ev.pop("t_eva_min") * 60, 1)
    if "duration_min" in ev:
        ev["duration_s"] = round(ev.pop("duration_min") * 60.0 / speed, 1)
    return ev


def describe(t_min, msg, speed):
    """One line for the console / --timeline."""
    when = f"{t_min * 60 / speed:5.1f} s  (EVA {int(t_min) // 60}:{int(t_min) % 60:02d})"
    if msg["type"] == "alert":
        eta = f"  [storm ETA ~{msg['eta_min']} EVA min]" if "eta_min" in msg else ""
        return f"{when}  ALERT {msg['category']:<9} {msg['level']:<8} {msg['title']}: {msg['text']}{eta}"
    if msg["type"] == "event":
        return f"{when}  EVENT {msg.get('id')}: {msg.get('text', '')}"
    if msg["type"] == "earth_note":
        return f"{when}  EARTH {msg.get('text', '')}"
    return f"{when}  {msg['type'].upper()} {msg.get('state', '')}"


async def run(url, messages, speed, eva_min):
    async with websockets.connect(url) as ws:
        send = lambda msg: ws.send(json.dumps({"v": SCHEMA_VERSION, **msg}))
        await send({"type": "hello", "from": "feeder"})
        print(f"Connected to {url}; replaying {eva_min:g} EVA minutes at {speed:g}x "
              f"(ends after {eva_min * 60 / speed:.0f} s)")
        start = time.monotonic()
        for t_min, msg in messages:
            wait = start + t_min * 60.0 / speed - time.monotonic()
            if wait > 0:
                await asyncio.sleep(wait)
            await send(msg)
            if msg["type"] in ("alert", "event", "earth_note"):
                print(describe(t_min, msg, speed))
        print(f"Finished. Total dose {messages[-1][1].get('dose_total_mGy', 0):.4f} mGy")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="ws://localhost:8765")
    here = Path(__file__).resolve().parent  # defaults work from any folder
    ap.add_argument("--samples", default=str(here / "sol_samples.csv"))
    ap.add_argument("--events", default=str(here / "events.json"))
    ap.add_argument("--speed", type=float, default=300.0,
                    help="EVA minutes per real minute (300 = 4-hour EVA in 48 s, for the 2-minute demo)")
    ap.add_argument("--tick", type=float, default=0.5, help="seconds between telemetry messages")
    ap.add_argument("--eva-min", type=float, default=240.0, help="EVA length in minutes")
    ap.add_argument("--source", help="data credit shown on the HUD (default: from <samples>.meta.json)")
    ap.add_argument("--timeline", action="store_true",
                    help="print the events and alerts the replay will send, then exit (no server needed)")
    a = ap.parse_args()
    samples = load_samples(a.samples)
    if "dose_rate_uGy_per_h" not in samples[0]:
        raise SystemExit(f"{a.samples} needs a dose_rate_uGy_per_h column (run fetch_sol.py)")
    if not a.source:
        meta_path = os.path.splitext(a.samples)[0] + ".meta.json"
        try:
            with open(meta_path) as f:
                a.source = json.load(f)["source"]
        except (OSError, KeyError, ValueError):
            a.source = "unknown source (no .meta.json next to the CSV)"
    print(f"Data: {a.source}")
    with open(a.events) as f:
        events = json.load(f)
    messages = timeline(samples, events, a.speed, a.tick, a.eva_min, a.source)
    if a.timeline:
        for t_min, msg in messages:
            if msg["type"] != "telemetry":
                print(describe(t_min, msg, a.speed))
        return
    try:
        asyncio.run(run(a.url, messages, a.speed, a.eva_min))
    except OSError as e:
        raise SystemExit(f"Can't reach {a.url} ({e}). Is server.py running?")
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
