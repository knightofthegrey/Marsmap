"""
Websocket server that sends a heightmap to Unity.

Usage:
    python server.py                      # sends a demo heightmap
    python server.py export.csv           # JMARS pixel export CSV, 513x513
    python server.py export.csv 1025      # custom resolution (2^n + 1)

Needs: pip install websockets numpy

Binary message layout (little-endian):
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
if sys.prefix == sys.base_prefix and _VENV_PY.exists():
    os.execv(str(_VENV_PY), [str(_VENV_PY), *sys.argv])

import asyncio
import json
import struct

try:
    import numpy as np
    from websockets.asyncio.server import serve
except ModuleNotFoundError as e:
    sys.exit(f"Missing library: {e.name}\n"
             "Create the venv first (from the Marsmap folder):\n"
             "    python3 -m venv .venv\n"
             "    .venv/bin/pip install -r Python/requirements.txt")
KM_PER_DEG = 59.29  # Mars: ~21,344 km circumference / 360


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


def prepare_message():
    csv_path = sys.argv[1] if len(sys.argv) > 1 else None
    res = int(sys.argv[2]) if len(sys.argv) > 2 else 513
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


async def handler(websocket):
    print("Unity connected")
    try:
        async for message in websocket:
            try:
                data = json.loads(message)
            except (json.JSONDecodeError, TypeError):
                continue
            print("Received:", data)
            if data.get("type") in ("hello", "request_heightmap"):
                await websocket.send(HEIGHTMAP_MESSAGE)  # bytes -> binary frame
                print("Sent heightmap")
    except Exception as e:
        print("Connection error:", e)
    finally:
        print("Unity disconnected")


async def main():
    global HEIGHTMAP_MESSAGE
    HEIGHTMAP_MESSAGE = prepare_message()
    async with serve(handler, "localhost", 8765, max_size=None):
        print("Server listening on ws://localhost:8765")
        await asyncio.Future()  # run forever


if __name__ == "__main__":
    asyncio.run(main())
