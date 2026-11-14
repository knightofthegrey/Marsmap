"""
Martian Map - build sol_samples.csv from real Curiosity data.

Downloads one sol of:
  RAD   radiation dose rate         PDS PPI node, MSL-M-RAD-3-RDR-V1.0
  REMS  air temperature + pressure  PDS Atmospheres node, mslrem_1001 (processed "RMD" product)
and writes the EVA window as sol_samples.csv for feeder.py, plus sol_samples.meta.json
recording exactly which files the numbers came from.

RAD files are ~70 MB per sol, so only the bytes holding each observation's time and
dose are downloaded (HTTP range requests). REMS is ~10 MB per sol, downloaded whole.

Usage:
  python fetch_sol.py --sol 4560 --start-lmst 9:00
    --sol         Curiosity mission sol. Boxwork era is about 4534 onward; the PDS
                  archives currently stop a few months behind the rover.
    --start-lmst  Mars local time the EVA starts (24 h clock).
    --eva-min     EVA length in minutes (default 240).

Not available: wind. Curiosity's REMS wind sensor has returned no data for years,
so the CSV has no wind column and the HUD should not show wind.
"""
import argparse
import csv
import io
import json
import re
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from urllib.request import Request, urlopen

RAD_BASE = "https://pds-ppi.igpp.ucla.edu/data/MSL-M-RAD-3-RDR-V1.0/DATA/"
REMS_BASE = "https://atmos.nmsu.edu/PDS/data/mslrem_1001/DATA/"
LMST_HOUR_S = 88775.244 / 24  # one Mars hour in SI seconds
HEADERS = {"User-Agent": "marsmap-fetch-sol/1.0"}


# ---- HTTP helpers --------------------------------------------------------

def get(url, byte_range=None, timeout=120):
    req = Request(url, headers=dict(HEADERS))
    if byte_range:
        req.add_header("Range", "bytes=%d-%d" % byte_range)
    with urlopen(req, timeout=timeout) as r:
        if byte_range and r.status != 206:
            raise SystemExit(f"{url} ignored the byte-range request; refusing to download the whole file.")
        return r.read()


def list_dir(url):
    html = get(url).decode("utf-8", "replace")
    return re.findall(r'href="([^"?/][^"]*)"', html)


def find_range_dir(base, sol):
    """Archives group sols into folders like SOL_04489_04612/."""
    for name in list_dir(base):
        m = re.fullmatch(r"SOL_(\d+)_(\d+)/", name)
        if m and int(m[1]) <= sol <= int(m[2]):
            return base + name
    raise SystemExit(f"Sol {sol} is not in {base} yet (PDS releases lag the rover by months).")


def parse_lmst(sol, text_sol, h, m, s):
    """Hours after midnight LMST on `sol` (can exceed 24 if the reading is on the next sol)."""
    return (int(text_sol) - sol) * 24 + int(h) + int(m) / 60 + float(s) / 3600


# ---- RAD -----------------------------------------------------------------

def fetch_rad(sol, detector):
    folder = find_range_dir(RAD_BASE, sol)
    labels = sorted(n for n in list_dir(folder)
                    if re.fullmatch(rf"RAD_RDR_\d{{4}}_\d{{3}}_\d\d_\d\d_0*{sol}_V\d\d\.LBL", n))
    if not labels:
        raise SystemExit(f"No RAD file for sol {sol} in {folder}")
    lbl_url = folder + labels[-1]          # highest version
    txt_url = lbl_url[:-4] + ".TXT"
    lbl = get(lbl_url).decode("ascii", "replace")
    m = re.search(r"START_TIME\s*=\s*(\d{4})-(\d{3})T", lbl)
    earth_date = (datetime.strptime(f"{m[1]}-{m[2]}", "%Y-%j").date().isoformat() if m else None)

    # Byte offsets (1-based) of every object in the TXT, grouped by observation.
    first, dose = {}, {}
    for m in re.finditer(r'\^OBS(\d+)_(\w+?)\s*=\s*\(\s*"[^"]+"\s*,\s*(\d+)\s*<BYTES>\s*\)', lbl):
        obs, obj, off = int(m[1]), m[2], int(m[3])
        first[obs] = min(first.get(obs, off), off)
        if obj == f"TOT_DOSE_{detector}_ELEMENT":
            dose[obs] = off

    def one(obs):
        # The "[OBSERVATION: NN]" header with its Mars time sits just before its first object.
        start = max(0, first[obs] - 1 - 2000)
        head = get(txt_url, (start, first[obs] - 2)).decode("ascii", "replace")
        hdrs = list(re.finditer(rf"\[OBSERVATION:\s*0*{obs}\](.*?)(?=\[|\Z)", head, re.S))
        if not hdrs:
            return None
        h = hdrs[-1][1]
        t = re.search(r'START_OBS_MARS="(\d+) (\d+):(\d+):(\d+)"', h)
        dur = re.search(r"OBS_DURATION=(\d+)", h)
        val = get(txt_url, (dose[obs] - 1, dose[obs] + 39)).decode("ascii", "replace")
        v = re.search(r"-?\d+\.\d+", val)
        if not (t and v):
            return None
        mid = parse_lmst(sol, *t.groups()) + (int(dur[1]) if dur else 0) / 2 / LMST_HOUR_S
        return mid, float(v[0])

    with ThreadPoolExecutor(8) as pool:
        pts = [p for p in pool.map(one, sorted(dose)) if p and p[1] > 0]
    if not pts:
        raise SystemExit(f"Couldn't read any RAD dose values from {txt_url}")
    return sorted(pts), {"label": lbl_url, "data": txt_url, "earth_date": earth_date,
                         "observations": len(pts), "detector": detector}


# ---- REMS ----------------------------------------------------------------

def fetch_rems(sol, bin_min=10):
    folder = find_range_dir(REMS_BASE, sol) + f"SOL{sol:05d}/"
    tabs = sorted(n for n in list_dir(folder) if re.fullmatch(r"RME_\d+RMD\d+_*P\d+\.TAB", n))
    if not tabs:
        raise SystemExit(f"No REMS RMD (processed) file for sol {sol} in {folder}")
    bins = {}
    for name in tabs:
        url = folder + name
        xml = get(url[:-4] + ".xml").decode("utf-8", "replace")
        col = {}
        for blk in re.findall(r"<Field_(?:Character|Delimited)>(.*?)</Field_(?:Character|Delimited)>", xml, re.S):
            nm = re.search(r"<name>([^<]*)</name>", blk)
            num = re.search(r"<field_number>(\d+)</field_number>", blk)
            if nm and num:
                col[nm[1].strip()] = int(num[1]) - 1
        need = ("LMST", "AMBIENT_TEMP", "PRESSURE")
        if not all(k in col for k in need):
            raise SystemExit(f"{url}: expected columns {need}, label has {sorted(col)}")
        text = get(url, timeout=300).decode("ascii", "replace")
        for r in csv.reader(io.StringIO(text), skipinitialspace=True):
            t = re.fullmatch(r"(\d+)M(\d+):(\d+):([\d.]+)", r[col["LMST"]].strip())
            if not t:
                continue
            h = parse_lmst(sol, *t.groups())
            b = bins.setdefault(int(h * 60 // bin_min), {"temp": [], "pres": []})
            temp, pres = float(r[col["AMBIENT_TEMP"]]), float(r[col["PRESSURE"]])
            if temp > 0:            # -999 = no data
                b["temp"].append(temp)
            if pres > 0:
                b["pres"].append(pres)
    centre = lambda k: (k + 0.5) * bin_min / 60
    mean = lambda v: sum(v) / len(v)
    temp = [(centre(k), mean(b["temp"]) - 273.15) for k, b in sorted(bins.items()) if b["temp"]]
    pres = [(centre(k), mean(b["pres"])) for k, b in sorted(bins.items()) if b["pres"]]
    if not temp or not pres:
        raise SystemExit(f"REMS had no valid temperature/pressure for sol {sol}")
    return temp, pres, {"files": [folder + n for n in tabs]}


# ---- Resample onto the EVA clock -----------------------------------------

def interp(pts, x):
    if x <= pts[0][0]:
        return pts[0][1]
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        if x0 <= x <= x1:
            return y0 + (x - x0) / ((x1 - x0) or 1) * (y1 - y0)
    return pts[-1][1]


def gap_h(pts, x):
    return min(abs(p[0] - x) for p in pts)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--sol", type=int, required=True)
    ap.add_argument("--start-lmst", default="9:00", help="EVA start, Mars local time HH:MM")
    ap.add_argument("--eva-min", type=float, default=240)
    ap.add_argument("--step-min", type=float, default=5, help="CSV row spacing in EVA minutes")
    ap.add_argument("--detector", choices=["B", "E"], default="E",
                    help="RAD detector: E (plastic, closer to human tissue) or B (silicon)")
    ap.add_argument("--out", default="sol_samples.csv")
    a = ap.parse_args()
    hh, mm = a.start_lmst.split(":")
    start_h = int(hh) + int(mm) / 60

    print(f"Sol {a.sol}: fetching RAD (dose) ...")
    dose, rad_meta = fetch_rad(a.sol, a.detector)
    print(f"  {rad_meta['observations']} observations, Earth date {rad_meta['earth_date']}")
    print(f"Sol {a.sol}: fetching REMS (temperature, pressure) ...")
    temp, pres, rems_meta = fetch_rems(a.sol)
    print(f"  {len(temp)} ten-minute bins with data")

    rows, worst_gap = [], 0.0
    n = int(a.eva_min // a.step_min)
    for i in range(n + 1):
        t = min(i * a.step_min, a.eva_min)
        h = start_h + t / 60
        worst_gap = max(worst_gap, gap_h(dose, h), gap_h(temp, h), gap_h(pres, h))
        rows.append({
            "t_eva_min": round(t, 2),
            "lmst_h": round(h, 4),
            "dose_rate_uGy_per_h": round(interp(dose, h), 3),
            "temp_c": round(interp(temp, h), 2),
            "pressure_pa": round(interp(pres, h), 2),
        })
    if worst_gap > 1.5:
        print(f"WARNING: up to {worst_gap:.1f} Mars hours between real readings in this window; "
              "values there are interpolated. Try another --start-lmst or --sol.")

    with open(a.out, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0]))
        w.writeheader()
        w.writerows(rows)
    meta = {
        "sol": a.sol,
        "earth_date_utc": rad_meta["earth_date"],
        "eva_start_lmst": a.start_lmst,
        "eva_min": a.eva_min,
        "source": f"MSL RAD+REMS sol {a.sol} ({rad_meta['earth_date']}), replayed",
        "rad": rad_meta,
        "rems": rems_meta,
        "not_available": {"wind_ms": "REMS wind sensor returns no data for these sols"},
        "max_gap_between_readings_h": round(worst_gap, 2),
        "generated_by": "fetch_sol.py",
    }
    meta_path = re.sub(r"\.csv$", "", a.out) + ".meta.json"
    with open(meta_path, "w") as f:
        json.dump(meta, f, indent=2)

    col = lambda k: [r[k] for r in rows]
    print(f"Wrote {a.out} ({len(rows)} rows) and {meta_path}")
    print(f"  dose  {min(col('dose_rate_uGy_per_h')):.2f}-{max(col('dose_rate_uGy_per_h')):.2f} uGy/h")
    print(f"  temp  {min(col('temp_c')):.1f} to {max(col('temp_c')):.1f} C")
    print(f"  press {min(col('pressure_pa')):.0f}-{max(col('pressure_pa')):.0f} Pa")


if __name__ == "__main__":
    main()
