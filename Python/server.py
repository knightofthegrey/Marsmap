"""
Martian Map - WebSocket server.

    feeder.py --(telemetry, events)--> server.py --> Unity  PythonConnection.cs
                                           |
                                           +--(JMARS heightmap)--> Terraingen.cs

Two jobs:
  1. Terrain: when Unity sends {"type":"request_heightmap"}, reply with a binary
     heightmap built from a JMARS pixel-export CSV (or a demo terrain).
  2. Live data: every client starts as a "viewer". A client that sends
     {"type":"hello","from":"feeder"} becomes a data source, and everything it
     sends is relayed to all viewers. Viewers that join late are sent the last
     status and the latest telemetry so the HUD fills in immediately.

Usage:
    python server.py                      # demo heightmap
    python server.py export.csv           # JMARS pixel export CSV, 513x513
    python server.py export.csv 1025      # custom resolution (2^n + 1)
    python server.py --host 0.0.0.0       # also accept other machines
  then, in a second terminal:
    python feeder.py --speed 80           # replays real RAD + REMS data

Needs: .venv/bin/pip install -r Python/requirements.txt   (Windows: .venv\\Scripts\\pip ...)

Heightmap binary message layout (little-endian):
    4 bytes   magic "HMAP"
    int32     resolution (N)
    float32   min elevation (m)
    float32   max elevation (m)
    float32   size X (m, east-west)
    float32   size Z (m, north-south)
    N*N uint16 heights, row 0 = south, last row = north
"""
import os
import sys
from pathlib import Path

# If run with plain `python3`, re-launch with the project's .venv so numpy/websockets are found.
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
import json
import struct

try:
    import numpy as np
    from websockets.asyncio.server import broadcast, serve
    from websockets.exceptions import ConnectionClosed
except ModuleNotFoundError as e:
    sys.exit(f"Missing library: {e.name}\n"
             "Create the venv first (from the Marsmap folder):\n"
             "    python3 -m venv .venv\n"
             "    .venv/bin/pip install -r Python/requirements.txt")

SCHEMA_VERSION = 1
KM_PER_DEG = 59.29  # Mars: ~21,344 km circumference / 360


# ---- Terrain (JMARS CSV -> binary heightmap) ------------------------------

def resample(a, n_rows, n_cols):
    rows = np.linspace(0, a.shape[0] - 1, n_rows)
    cols = np.linspace(0, a.shape[1] - 1, n_cols)
    tmp = np.array([np.interp(cols, np.arange(a.shape[1]), row) for row in a])
    return np.array([np.interp(rows, np.arange(a.shape[0]), tmp[:, j])
                     for j in range(tmp.shape[1])]).T


def load_jmars_csv(path, res):
    with open(path) as f:
        header = [h.strip().lower() for h in f.readline().strip().split(",")]
    lat_i = next(i for i, h in enumerate(header) if "lat" in h)
    lon_i = next(i for i, h in enumerate(header) if "lon" in h)
    elev_i = next(i for i, h in enumerate(header)
                  if i not in (lat_i, lon_i) and "id" not in h)

    data = np.genfromtxt(path, delimiter=",", skip_header=1,
                         usecols=(lat_i, lon_i, elev_i))
    lat, lon, elev = data[:, 0], data[:, 1], data[:, 2]

    # Snap points to a regular grid. (Rounding lat/lon directly splits rows,
    # because floats like -4.55859375 sit right on a rounding tie.)
    def step(v):
        d = np.diff(np.sort(v))
        return np.median(d[d > 1e-4])  # exact pixel spacing, ignores float noise
    dlat, dlon = step(lat), step(lon)
    r = np.rint((lat - lat.min()) / dlat).astype(int)
    c = np.rint((lon - lon.min()) / dlon).astype(int)
    grid = np.full((r.max() + 1, c.max() + 1), np.nan)
    grid[r, c] = elev
    grid[np.isnan(grid)] = np.nanmean(grid)
    lats = np.array([lat.min(), lat.max()])
    lons = np.array([lon.min(), lon.max()])

    print(f"CSV: {len(elev)} points -> {grid.shape[1]} x {grid.shape[0]} grid "
          f"(~{1 / dlat:.0f} pixels/degree)")

    lat_mid = np.radians((lats[0] + lats[-1]) / 2)
    size_z = (lats[-1] - lats[0]) * KM_PER_DEG * 1000
    size_x = (lons[-1] - lons[0]) * KM_PER_DEG * 1000 * np.cos(lat_mid)
    return resample(grid, res, res), size_x, size_z


def demo_heightmap(res):
    """Smooth fake terrain so you can test the connection without JMARS."""
    y, x = np.mgrid[0:res, 0:res] / res * 2 * np.pi
    grid = (np.sin(x * 2) * np.cos(y * 3) * 500
            + np.sin(x * 5 + y * 4) * 150)
    return grid, 20000.0, 20000.0


def build_message(grid, size_x, size_z):
    res = grid.shape[0]
    lo, hi = float(grid.min()), float(grid.max())
    span = (hi - lo) or 1.0  # avoid divide-by-zero on perfectly flat data
    norm = (grid - lo) / span
    raw = (norm * 65535).astype("<u2")
    header = b"HMAP" + struct.pack("<i4f", res, lo, hi, size_x, size_z)
    print(f"Heightmap {res}x{res}, {lo:.0f} m to {hi:.0f} m, "
          f"{size_x:.0f} x {size_z:.0f} m")
    return header + raw.tobytes()


def prepare_message(csv_path, res):
    if csv_path:
        if not os.path.isfile(csv_path):
            sys.exit(f"CSV not found: {os.path.abspath(csv_path)}\n"
                     "Paths are relative to the folder you run the command from.")
        grid, sx, sz = load_jmars_csv(csv_path, res)
    else:
        print("No CSV given, using demo terrain")
        grid, sx, sz = demo_heightmap(res)
    return build_message(grid, sx, sz)


HEIGHTMAP_MESSAGE = None


# ---- Live data relay (feeder -> Unity) -------------------------------------

viewers = set()   # Unity clients: receive live data
feeders = set()   # data sources: their messages are relayed to viewers
latest = {}       # "status" / "telemetry" -> last raw message, replayed to late joiners


def describe(msg):
    """One short log line per message, so the console stays readable at 2 Hz."""
    t = msg.get("type")
    if t == "telemetry":
        return (f"telemetry t={msg.get('t_eva_s')}s lmst={msg.get('lmst_h')} "
                f"dose={msg.get('dose_total_mGy')}mGy T={msg.get('temp_c')}C P={msg.get('pressure_pa')}Pa")
    return json.dumps(msg)


async def handler(ws):
    peer = f"{ws.remote_address[0]}:{ws.remote_address[1]}" if ws.remote_address else "?"
    viewers.add(ws)
    print(f"[+] {peer} connected ({len(viewers)} viewer(s), {len(feeders)} feeder(s))")
    try:
        for raw in latest.values():
            await ws.send(raw)
        async for raw in ws:
            if isinstance(raw, bytes):
                continue  # clients only send JSON text
            try:
                msg = json.loads(raw)
            except json.JSONDecodeError:
                print(f"    {peer} sent non-JSON, ignored: {raw!r:.80}")
                continue
            if not isinstance(msg, dict):
                continue
            kind = msg.get("type")

            if kind == "hello":
                role = msg.get("from", "unity")
                if role == "feeder":
                    viewers.discard(ws)
                    feeders.add(ws)
                print(f"    {peer} is {role}")
                await ws.send(json.dumps({"v": SCHEMA_VERSION, "type": "welcome", "role": role}))
                continue

            if kind == "request_heightmap":
                await ws.send(HEIGHTMAP_MESSAGE)  # bytes -> binary frame
                print(f"    sent heightmap to {peer} ({len(HEIGHTMAP_MESSAGE) / 1e6:.1f} MB)")
                continue

            if ws in feeders:
                if kind in ("status", "telemetry"):
                    latest[kind] = raw
                if kind == "status" and msg.get("state") == "started":
                    latest.pop("telemetry", None)
                broadcast(viewers, raw)
                print(f"    -> {len(viewers)} viewer(s): {describe(msg)}")
            else:
                # Unity -> Python messages land here. Nothing consumes them yet.
                print(f"    from {peer}: {describe(msg)}")
    except ConnectionClosed:
        pass
    except Exception as e:
        print(f"    connection error from {peer}: {e!r}")
    finally:
        viewers.discard(ws)
        feeders.discard(ws)
        print(f"[-] {peer} disconnected")


async def main(host, port):
    async with serve(handler, host, port) as server:
        print(f"Server listening on ws://{host}:{port}")
        await server.serve_forever()


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Martian Map WebSocket server (terrain + live data)")
    ap.add_argument("csv", nargs="?", help="JMARS pixel export CSV (omit for demo terrain)")
    ap.add_argument("res", nargs="?", type=int, default=513, help="heightmap resolution, 2^n + 1")
    ap.add_argument("--host", default="localhost")
    ap.add_argument("--port", type=int, default=8765)
    a = ap.parse_args()
    HEIGHTMAP_MESSAGE = prepare_message(a.csv, a.res)
    try:
        asyncio.run(main(a.host, a.port))
    except KeyboardInterrupt:
        pass
