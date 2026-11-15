"""
Martian Map - WebSocket server.

    feeder.py --(telemetry, events)--> server.py --> Unity  PythonConnection.cs
                                           |
                                           +--(JMARS heightmap)--> Terraingen.cs

Two jobs:
  1. Terrain: when Unity sends {"type":"request_heightmap"}, reply with a binary
     heightmap built from a CSV of lat/lon/elevation points (a JMARS pixel export,
     or the HiRISE site made by make_site.py), or a demo terrain. For a make_site.py
     site (a site.json next to the CSV) the reply is, in this order:
         "site" JSON text -> HMAP heightmap -> one TEXL image per map layer
  2. Live data: every client starts as a "viewer". A client that sends
     {"type":"hello","from":"feeder"} becomes a data source, and everything it
     sends is relayed to all viewers. Viewers that join late are sent the last
     status and the latest telemetry so the HUD fills in immediately.

Usage:
    python server.py                      # Python/site/terrain.csv if it exists, else demo
    python server.py --demo               # demo heightmap (fake sine-wave hills)
    python server.py export.csv           # JMARS pixel export CSV, 513x513
    python server.py export.csv 1025      # custom resolution (2^n + 1)
    python server.py --host 0.0.0.0       # also accept other machines
  then, in a second terminal:
    python feeder.py                      # replays real RAD + REMS data (4 h EVA in 48 s)

Needs: .venv/bin/pip install -r Python/requirements.txt   (Windows: .venv\\Scripts\\pip ...)

Site message (JSON text, only for a make_site.py site):
    {"v":1, "type":"site", "name", "sol", "earth_date", "center_lat", "center_lon",
     "size_m", "vertical_exaggeration", "layers":[{"name","label","credit"}, ...]}

Heightmap binary message layout (little-endian):
    4 bytes   magic "HMAP"
    int32     resolution (N)
    float32   min elevation (m)
    float32   max elevation (m)
    float32   size X (m, east-west)
    float32   size Z (m, north-south)
    N*N uint16 heights, row 0 = south, last row = north

Layer image binary message (little-endian), one per site.json layer, in that order:
    4 bytes   magic "TEXL"
    int32     L = byte length of the layer name
    L bytes   layer name, UTF-8 ("photo", "slope", "thermal")
    rest      the image file as-is (JPEG/PNG; top row = north, left column = west,
              covering exactly the heightmap's area)
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
                         usecols=(lat_i, lon_i, elev_i), ndmin=2)
    lat, lon, elev = data[:, 0], data[:, 1], data[:, 2]
    bad = np.isnan(lat) | np.isnan(lon)
    if bad.all():
        sys.exit(f"{path}: no numeric latitude/longitude values found.")
    if bad.any():
        print(f"WARNING: skipping {bad.sum()} rows without a numeric latitude/longitude")
        lat, lon, elev = lat[~bad], lon[~bad], elev[~bad]

    # Snap points to a regular grid. (Rounding lat/lon directly splits rows,
    # because floats like -4.55859375 sit right on a rounding tie.)
    def step(v):
        d = np.diff(np.unique(v))
        if d.size == 0:
            sys.exit(f"{path}: every point has the same latitude or longitude; not a grid.")
        # Grid spacing. Gaps far smaller than the biggest one are float noise between
        # copies of the same row/column, so this works for 4 m (or 1 m) grids too.
        return np.median(d[d > d.max() * 1e-3])
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


SITE_CSV = Path(__file__).resolve().parent / "site" / "terrain.csv"  # made by make_site.py


def load_site(csv_path):
    """make_site.py writes site.json next to terrain.csv. Returns the "site" JSON message and
    one TEXL message per layer image, or (None, []) for a plain JMARS CSV."""
    path = Path(csv_path).resolve().parent / "site.json"
    if not path.is_file():
        return None, []
    try:
        site = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        print(f"WARNING: can't read {path} ({e}); sending the heightmap only")
        return None, []
    layers, images = [], []
    for layer in site.get("layers", []):
        name, file = layer.get("name"), layer.get("file")
        img = path.parent / str(file)
        if not name or not file or not img.is_file():
            print(f"WARNING: layer {name!r}: image {img} not found, skipped")
            continue
        data = img.read_bytes()
        name_b = name.encode("utf-8")
        images.append(b"TEXL" + struct.pack("<i", len(name_b)) + name_b + data)
        layers.append({"name": name, "label": layer.get("label", name),
                       "credit": layer.get("credit", "")})
    msg = {"v": SCHEMA_VERSION, "type": "site", "name": site.get("name", ""),
           "sol": site.get("sol"), "earth_date": site.get("earth_date", ""),
           "center_lat": site.get("center_lat"), "center_lon": site.get("center_lon"),
           "size_m": site.get("size_m"),
           "vertical_exaggeration": site.get("vertical_exaggeration", 1.0),
           "layers": layers}
    print(f"Site: {msg['name']} ({msg['earth_date']}), layers: "
          + (", ".join(f"{l['name']} {len(m) / 1e6:.2f} MB" for l, m in zip(layers, images))
             or "none"))
    return json.dumps(msg), images


def prepare_messages(csv_path, res, demo=False):
    """Everything sent in reply to request_heightmap, in sending order."""
    if not csv_path and not demo:
        if SITE_CSV.is_file():
            csv_path = str(SITE_CSV)
            print(f"No CSV given, using the HiRISE site from make_site.py: {csv_path}")
        else:
            print("No CSV given and no Python/site/terrain.csv (see make_site.py), using demo terrain")
    if demo or not csv_path:
        if demo:
            print("--demo: using demo terrain")
        return [build_message(*demo_heightmap(res))]
    if not os.path.isfile(csv_path):
        sys.exit(f"CSV not found: {os.path.abspath(csv_path)}\n"
                 "Paths are relative to the folder you run the command from.")
    site_msg, images = load_site(csv_path)
    grid, sx, sz = load_jmars_csv(csv_path, res)
    return ([site_msg] if site_msg else []) + [build_message(grid, sx, sz)] + images


def describe_terrain(messages):
    """e.g. 'site info + heightmap (0.53 MB) + 3 layer images (4.61 MB)'."""
    texts = [m for m in messages if isinstance(m, str)]
    hmap = [m for m in messages if isinstance(m, bytes) and m[:4] == b"HMAP"]
    imgs = [m for m in messages if isinstance(m, bytes) and m[:4] == b"TEXL"]
    parts = (["site info"] if texts else []) + [f"heightmap ({sum(map(len, hmap)) / 1e6:.2f} MB)"]
    if imgs:
        parts.append(f"{len(imgs)} layer images ({sum(map(len, imgs)) / 1e6:.2f} MB)")
    return " + ".join(parts)


TERRAIN_MESSAGES = []   # sent in this order on request_heightmap (str = text frame, bytes = binary)


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
                for m in TERRAIN_MESSAGES:  # str -> text frame, bytes -> binary frame
                    await ws.send(m)
                print(f"    sent {describe_terrain(TERRAIN_MESSAGES)} to {peer}")
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
    ap.add_argument("csv", nargs="?",
                    help="lat/lon/elevation CSV: a JMARS pixel export, or omit to use "
                         "Python/site/terrain.csv from make_site.py (demo terrain if missing)")
    ap.add_argument("res", nargs="?", type=int, default=513, help="heightmap resolution, 2^n + 1")
    ap.add_argument("--demo", action="store_true", help="use the fake demo terrain")
    ap.add_argument("--host", default="localhost")
    ap.add_argument("--port", type=int, default=8765)
    a = ap.parse_args()
    TERRAIN_MESSAGES = prepare_messages(a.csv, a.res, a.demo)
    print(f"Terrain reply: {describe_terrain(TERRAIN_MESSAGES)}")
    try:
        asyncio.run(main(a.host, a.port))
    except KeyboardInterrupt:
        pass
