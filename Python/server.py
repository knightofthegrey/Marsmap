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
    python server.py                      # Gale crater (DEFAULT_POLYGON below; cached tiles load fast)
    python server.py --site               # the Curiosity HiRISE site in Python/site/ (make_site.py)
    python server.py --demo               # demo heightmap (fake sine-wave hills)
    python server.py export.csv           # JMARS pixel export CSV, 513x513
    python server.py export.csv 1025      # custom resolution (2^n + 1)
    python server.py --host 0.0.0.0       # also accept other machines

  Polygon: pull real terrain for any area (lon,lat points; lat,lon is detected too):
    python server.py --polygon 77.3,18.3 77.6,18.3 77.6,18.6 77.3,18.6
    python server.py --polygon-file area.geojson     # GeoJSON, or a text file of lon,lat lines
  Heights: USGS CTX DTMs (20 m) where they exist, 200 m MOLA-HRSC everywhere else. Image:
  THEMIS + CTX photos, sent as the "photo" layer (outside the polygon is dimmed). Areas under
  ~80 km across get the full 20 m. Everything is cached in Python/imagery_cache/, so the
  same polygon loads instantly next time. Needs requirements-maps.txt (rasterio).
  Bigger areas are split into a grid of tiles (2049x2049, ~41 km each at 20 m) that Unity
  joins into one seamless terrain (TILE + TIMG messages). --max-tiles N (default 16) caps
  how many; past that the points get further apart instead.
  then, in a second terminal:
    python feeder.py                      # replays real RAD + REMS data (4 h EVA in 48 s)

Needs: .venv/bin/pip install -r Python/requirements.txt   (Windows: .venv\\Scripts\\pip ...)

Site message (JSON text, for a make_site.py site or a --polygon area):
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

Route planning (Unity's map, press M): Unity sends
    {"v":1, "type":"plan_route", "id":N, "x":[...], "z":[...], "loop":false, "return_to_start":false}
with points in Unity world metres (first = the astronaut), and gets back
    {"v":1, "type":"route", "id":N, "ok", "error", "x", "z", "elev", "tier", "legs",
     (each leg: from_wp, to_wp, ok, distance_m, minutes, start_index, end_index into x/z;
      from_wp "start" = the astronaut, to_wp "home" = Home Base, where the route began),
     "distance_m", "minutes", "gain_m", "loss_m", "max_climb_deg", "max_descent_deg",
     "max_side_deg", "difficulty", "score", "zone_pct", "hazards", "spacing_m"}
planned by route_planner.py with the A* / hazard / EVA-time rules from Mars_mapping(3).ipynb.

Tile message (--polygon too big for one terrain), one per tile, little-endian:
    4 bytes   magic "TILE"
    int32 x6  ix, iz (column from west, row from south), nx, nz, count (tiles being sent), res
    float32   min elevation (m), max elevation (m)  -- the SAME for every tile, so edges match
    float32   tile size X (m), tile size Z (m)
    res*res uint16 heights, row 0 = south; a tile's last row/column equals its neighbour's first
Tile image message, right after its tile:
    4 bytes   magic "TIMG"
    int32 x2  ix, iz
    rest      PNG covering exactly that tile, north up

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
import time

try:
    import numpy as np
    from websockets.asyncio.server import broadcast, serve
    from websockets.exceptions import ConnectionClosed
    import route_planner  # EVA routes for Unity's map (numpy only)
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

# What `python server.py` with no arguments builds: Gale crater and around (Curiosity's
# landing site), 257 x 184 km as 12 tiles. Points are lat,lon. Keep this text exactly as it
# is: the cached tiles (Python/imagery_cache/, shared as a zip) are found by it.
DEFAULT_POLYGON = "-3.797,135.484 -3.805,139.664 -6.773,139.734 -6.906,135.383"


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
        grid, sx, sz = demo_heightmap(res)
        set_route_dem(route_planner.WorldDEM.single(grid, sx, sz))
        return [build_message(grid, sx, sz)]
    if not os.path.isfile(csv_path):
        sys.exit(f"CSV not found: {os.path.abspath(csv_path)}\n"
                 "Paths are relative to the folder you run the command from.")
    site_msg, images = load_site(csv_path)
    grid, sx, sz = load_jmars_csv(csv_path, res)
    set_route_dem(route_planner.WorldDEM.single(grid, sx, sz))
    return ([site_msg] if site_msg else []) + [build_message(grid, sx, sz)] + images


def prepare_tiled_polygon(poly, max_tiles):
    """Polygon too big for one terrain: site info, then TILE (+ TIMG) per tile."""
    import dem_builder
    t = dem_builder.build_tiles(poly, max_tiles=max_tiles)
    lo, hi = t["lo"], t["hi"]
    span = (hi - lo) or 1.0
    (tx, tz), res, keys = t["tile_size"], t["res"], sorted(t["tiles"])
    west, south, east, north = dem_builder.polygon_box(poly)
    site = {"v": SCHEMA_VERSION, "type": "site",
            "name": f"Polygon lon {west:.3f}..{east:.3f}, lat {south:.3f}..{north:.3f} "
                    f"({len(keys)} tiles)",
            "sol": 0, "earth_date": "",
            "center_lat": t["center_lat"], "center_lon": t["center_lon"] % 360,  # = Unity origin
            "size_m": max(t["nx"] * tx, t["nz"] * tz), "vertical_exaggeration": 1.0,
            "layers": []}
    set_route_dem(route_planner.WorldDEM.tiles(t))
    msgs = [json.dumps(site)]
    for ix, iz in keys:
        tile = t["tiles"][(ix, iz)]
        raw = ((tile["grid"] - lo) / span * 65535).round().astype("<u2")
        msgs.append(b"TILE" + struct.pack("<6i4f", ix, iz, t["nx"], t["nz"], len(keys), res,
                                          lo, hi, tx, tz) + raw.tobytes())
        if tile["image"]:
            msgs.append(b"TIMG" + struct.pack("<2i", ix, iz) + tile["image"])
    print(f"Tiles: {len(keys)} x {res}x{res}, {t['nx'] * tx / 1000:.0f} x {t['nz'] * tz / 1000:.0f} km "
          f"in total, {lo:.0f} m to {hi:.0f} m")
    return msgs


def prepare_polygon(text, path, res, max_tiles=16):
    """--polygon / --polygon-file: same messages as a make_site.py site (site info, heightmap,
    one "photo" layer), built by dem_builder.py from USGS data and cached."""
    try:
        import dem_builder  # needs rasterio
        import rasterio  # noqa: F401
    except ModuleNotFoundError as e:
        sys.exit(f"--polygon needs {e.name}:  .venv/bin/pip install -r Python/requirements-maps.txt")
    try:
        poly = dem_builder.parse_polygon(text, path)
    except (ValueError, OSError, KeyError) as e:
        sys.exit(f"Bad polygon: {e}")
    if not res and dem_builder.needs_tiles(poly):
        return prepare_tiled_polygon(poly, max_tiles)
    r = dem_builder.build(poly, max_res=res or dem_builder.MAX_RES)

    west, south, east, north = r["bounds"]
    layers, images = [], []
    if r["image"]:
        layers.append({"name": "photo", "label": "Satellite image",
                       "credit": "THEMIS day IR 100 m (NASA/JPL/ASU) + CTX 6 m (NASA/JPL/MSSS), "
                                 "heights: CTX DTMs 20 m + MOLA-HRSC 200 m (USGS)"})
        images.append(b"TEXL" + struct.pack("<i", 5) + b"photo" + r["image"])
    site = {"v": SCHEMA_VERSION, "type": "site",
            "name": f"Polygon lon {west:.3f}..{east:.3f}, lat {south:.3f}..{north:.3f}",
            "sol": 0, "earth_date": "",
            "center_lat": (south + north) / 2, "center_lon": ((west + east) / 2) % 360,
            "size_m": max(r["size_x"], r["size_z"]), "vertical_exaggeration": 1.0,
            "layers": layers}
    set_route_dem(route_planner.WorldDEM.single(r["grid"], r["size_x"], r["size_z"]))
    return [json.dumps(site), build_message(r["grid"], r["size_x"], r["size_z"])] + images


def describe_terrain(messages):
    """e.g. 'site info + heightmap (0.53 MB) + 3 layer images (4.61 MB)'."""
    texts = [m for m in messages if isinstance(m, str)]
    hmap = [m for m in messages if isinstance(m, bytes) and m[:4] == b"HMAP"]
    imgs = [m for m in messages if isinstance(m, bytes) and m[:4] == b"TEXL"]
    tiles = [m for m in messages if isinstance(m, bytes) and m[:4] in (b"TILE", b"TIMG")]
    if tiles:
        n = sum(m[:4] == b"TILE" for m in tiles)
        return f"site info + {n} terrain tiles with images ({sum(map(len, tiles)) / 1e6:.0f} MB)"
    parts = (["site info"] if texts else []) + [f"heightmap ({sum(map(len, hmap)) / 1e6:.2f} MB)"]
    if imgs:
        parts.append(f"{len(imgs)} layer images ({sum(map(len, imgs)) / 1e6:.2f} MB)")
    return " + ".join(parts)


TERRAIN_MESSAGES = []   # sent in this order on request_heightmap (str = text frame, bytes = binary)
ROUTE_DEM = None        # the same terrain as heights in Unity world metres, for plan_route


def set_route_dem(dem):
    global ROUTE_DEM
    ROUTE_DEM = dem


async def handle_plan_route(ws, msg, peer):
    """Plan an EVA route for Unity's map on a worker thread (telemetry keeps flowing)."""
    reply = {"v": SCHEMA_VERSION, "type": "route", "id": msg.get("id", 0)}
    try:
        xs, zs = msg.get("x") or [], msg.get("z") or []
        if ROUTE_DEM is None:
            result = dict(ok=False, error="The server has no terrain to plan on")
        elif len(xs) != len(zs) or len(xs) < 2:
            result = dict(ok=False, error="Add at least one waypoint")
        else:
            t = time.time()
            result = await asyncio.to_thread(route_planner.plan, ROUTE_DEM,
                                             list(zip(xs, zs)), bool(msg.get("loop")),
                                             bool(msg.get("return_to_start")))
            if result.get("distance_m") is not None:
                print(f"    route for {peer}: {len(xs) - 1} waypoint(s), "
                      f"{result['distance_m'] / 1000:.2f} km, {result['minutes']:.0f} min, "
                      f"{result['difficulty']}, planned in {time.time() - t:.1f}s")
    except Exception as e:  # noqa: BLE001  (a bad request must not drop the connection)
        result = dict(ok=False, error=f"Route planning failed: {e}")
    reply.update(result)
    await ws.send(json.dumps(reply))


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

            if kind == "plan_route":
                await handle_plan_route(ws, msg, peer)
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
                    help="lat/lon/elevation CSV, e.g. a JMARS pixel export. With no CSV and no "
                         "--polygon / --site / --demo: the Gale crater DEFAULT_POLYGON")
    ap.add_argument("res", nargs="?", type=int, default=None,
                    help="heightmap resolution, 2^n + 1 (default 513; --polygon: picked automatically)")
    ap.add_argument("--demo", action="store_true", help="use the fake demo terrain")
    ap.add_argument("--site", action="store_true",
                    help="the HiRISE site in Python/site/ from make_site.py (Curiosity, sol 4560)")
    ap.add_argument("--host", default="localhost")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--polygon", metavar="POINTS",
                    help="area to pull, lon,lat points: --polygon 77.3,18.3 77.6,18.3 77.6,18.6")
    ap.add_argument("--max-tiles", type=int, default=16,
                    help="--polygon: most Unity terrain tiles (~41 km each) for a big area")
    ap.add_argument("--polygon-file", metavar="FILE",
                    help="area to pull, from GeoJSON or a text file with one lon,lat per line")
    # Let points be given without quotes: --polygon -4.0,135.7 -4.0,143.3 ...
    # (argparse would read "-4.0,135.7" as an option name, so join them up front)
    argv, i = sys.argv[1:], 0
    while i < len(argv):
        if argv[i] == "--polygon":
            j = i + 1
            while j < len(argv) and "," in argv[j]:
                j += 1
            argv[i:j] = ["--polygon=" + " ".join(argv[i + 1:j])]
        i += 1
    a = ap.parse_args(argv)
    if a.site:
        if not SITE_CSV.is_file():
            ap.error(f"--site: {SITE_CSV} not found (run make_site.py first)")
        a.csv = str(SITE_CSV)
    elif not (a.csv or a.demo or a.polygon or a.polygon_file):
        try:
            import rasterio  # noqa: F401  (needed to build the polygon)
            print("No arguments: building Gale crater (DEFAULT_POLYGON). Other options: --help")
            a.polygon = DEFAULT_POLYGON
        except ModuleNotFoundError:
            print("No arguments, but the default Gale crater map needs rasterio "
                  "(.venv/bin/pip install -r Python/requirements-maps.txt); using Python/site/")
    if a.polygon or a.polygon_file:
        if a.csv and a.csv.isdigit() and a.res is None:  # --polygon ... 2049
            a.res, a.csv = int(a.csv), None
        elif a.csv:
            ap.error(f"--polygon builds its own terrain; don't also give a CSV ({a.csv})")
        if a.max_tiles < 1:
            ap.error("--max-tiles must be at least 1")
        TERRAIN_MESSAGES = prepare_polygon(a.polygon, a.polygon_file, a.res, a.max_tiles)
    else:
        TERRAIN_MESSAGES = prepare_messages(a.csv, a.res or 513, a.demo)
    print(f"Terrain reply: {describe_terrain(TERRAIN_MESSAGES)}")
    try:
        asyncio.run(main(a.host, a.port))
    except KeyboardInterrupt:
        pass
