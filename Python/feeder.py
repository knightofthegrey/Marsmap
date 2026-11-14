"""
Martian Map - telemetry feeder.

Replays real Curiosity data (RAD radiation + REMS weather from one sol) and
scripted EVA events onto the EVA clock, and sends them as JSON messages to the
team's WebSocket server (server.py). Unity listens to that server and updates the HUD.

Static map data (heightmap, photo, cost grid, stops) does NOT go over the
socket - Unity loads those files at startup. The socket carries only what
would be "live" on a real Marswalk.

Inputs (you create these by hand from PDS data; see the example files):
  sol_samples.csv : t_eva_min, dose_rate_uGy_per_h, temp_c, pressure_pa, wind_ms
  events.json     : list of {"t_eva_min", "type", ...} scripted events

Usage:
  pip install -r requirements.txt
  python feeder.py --url ws://localhost:8765 --speed 80
    --speed 80 squeezes a 4-hour EVA into 3 minutes for the demo.

Message format (agree this with whoever owns the server and the Unity side;
the Unity classes live in Assets/SourceFiles/Scripts/PythonConnection.cs):
  every message is JSON with "v" (schema version) and "type".
  hello     : {"v":1,"type":"hello","from":"feeder"}  (first message; marks this client as a data source)
  telemetry : {"v":1,"type":"telemetry","t_eva_s":..,"dose_rate_uGy_h":..,
               "dose_total_mGy":..,"temp_c":..,"pressure_pa":..,"wind_ms":..,
               "source":"MSL RAD+REMS sol NNNN (replayed)"}
  event     : {"v":1,"type":"event","t_eva_s":..,"id":"dust_gust",..}
  earth_note: {"v":1,"type":"earth_note","t_eva_s":..,"text":"..","delay_s":..}
  status    : {"v":1,"type":"status","state":"started"|"finished"}
"""
import argparse
import asyncio
import csv
import json
import time

import websockets

SCHEMA_VERSION = 1


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
        print(f"Connected to {url}; replaying {eva_min:g} EVA minutes at {speed:g}x")
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
            await send({
                "type": "telemetry",
                "t_eva_s": round(t_min * 60, 1),
                "dose_rate_uGy_h": round(rate, 2),
                "dose_total_mGy": round(dose_mGy, 4),
                "temp_c": round(interp(samples, t_min, "temp_c"), 1),
                "pressure_pa": round(interp(samples, t_min, "pressure_pa"), 1),
                "wind_ms": round(interp(samples, t_min, "wind_ms"), 1),
                "source": source,
            })
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
    ap.add_argument("--samples", default="sol_samples.csv")
    ap.add_argument("--events", default="events.json")
    ap.add_argument("--speed", type=float, default=80.0, help="EVA minutes per real minute")
    ap.add_argument("--tick", type=float, default=0.5, help="seconds between telemetry messages")
    ap.add_argument("--eva-min", type=float, default=240.0, help="EVA length in minutes")
    ap.add_argument("--source", default="MSL RAD+REMS (replayed); set the sol number here")
    a = ap.parse_args()
    samples = load_samples(a.samples)
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
