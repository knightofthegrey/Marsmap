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


def interp(rows, t_min, key):
    """Linear interpolation of one column at EVA minute t_min."""
    if t_min <= rows[0]["t_eva_min"]:
        return rows[0][key]
    for a, b in zip(rows, rows[1:]):
        if a["t_eva_min"] <= t_min <= b["t_eva_min"]:
            span = b["t_eva_min"] - a["t_eva_min"] or 1.0
            w = (t_min - a["t_eva_min"]) / span
            return a[key] + w * (b[key] - a[key])
    return rows[-1][key]


async def run(url, samples, events, speed, tick_s, eva_min, source):
    async with websockets.connect(url) as ws:
        send = lambda msg: ws.send(json.dumps({"v": SCHEMA_VERSION, **msg}))
        await send({"type": "hello", "from": "feeder"})
        print(f"Connected to {url}; replaying {eva_min:g} EVA minutes at {speed:g}x "
              f"(ends after {eva_min * 60 / speed:.0f} s)")
        for e in sorted(events, key=lambda e: e["t_eva_min"]):
            print(f"  at {e['t_eva_min'] * 60 / speed:5.1f} s: {e.get('id') or e['type']}")
        await send({"type": "status", "state": "started", "speed": speed})
        pending = sorted(events, key=lambda e: e["t_eva_min"])
        dose_mGy = 0.0
        start = time.monotonic()
        last_t = 0.0
        while True:
            t_min = (time.monotonic() - start) * speed / 60.0
            if t_min > eva_min:
                break
            rate = interp(samples, t_min, "dose_rate_uGy_per_h")
            dose_mGy += rate * ((t_min - last_t) / 60.0) / 1000.0  # uGy/h * h -> mGy
            last_t = t_min
            msg = {"type": "telemetry", "t_eva_s": round(t_min * 60, 1)}
            for col in samples[0]:
                if col != "t_eva_min":
                    msg[RENAME.get(col, col)] = round(interp(samples, t_min, col), 3)
            msg["dose_total_mGy"] = round(dose_mGy, 4)
            msg["source"] = source
            await send(msg)
            while pending and pending[0]["t_eva_min"] <= t_min:
                ev = dict(pending.pop(0))
                ev["t_eva_s"] = round(ev.pop("t_eva_min") * 60, 1)
                await send(ev)
            await asyncio.sleep(tick_s)
        await send({"type": "status", "state": "finished", "dose_total_mGy": round(dose_mGy, 4)})
        print(f"Finished. Total dose {dose_mGy:.4f} mGy")


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
    try:
        asyncio.run(run(a.url, samples, events, a.speed, a.tick, a.eva_min, a.source))
    except OSError as e:
        raise SystemExit(f"Can't reach {a.url} ({e}). Is server.py running?")
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
