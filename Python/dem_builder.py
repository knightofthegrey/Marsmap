"""
Builds the best available elevation (and imagery) for a polygon on Mars.

Elevation:
  1. Base: USGS MOLA-HRSC blended DEM, 200 m/px, whole planet.
  2. On top: every USGS CTX DTM (20 m/px) that overlaps the polygon, from the
     USGS STAC catalog (~44,000 DTMs). Each one is levelled to the base (offset + tilt),
     noisy ones are skipped, and edges are feathered so there are no steps.
Imagery:
  THEMIS day IR (100 m/px) as the base, with the CTX orthoimages (6 m/px) that belong
  to the DTMs on top, brightness-matched to THEMIS.

All data are Cloud-Optimized GeoTIFFs; rasterio only downloads the pieces it needs.
Results are cached in Python/imagery_cache/: the same polygon (and resolution) a second time
loads in under a second, and THEMIS downloads are cached per box.

Used by server.py --polygon. Needs rasterio:  .venv/bin/pip install -r Python/requirements-maps.txt
"""
import builtins
import hashlib
import json
import queue
import threading
import math
import time
import urllib.request
import zlib
import struct
from pathlib import Path

import numpy as np

UA = "wms-heightmap-client/1.0"
MARS_RADIUS = 3396190.0
M_PER_DEG = MARS_RADIUS * math.pi / 180  # ~59,274 m
STAC_SEARCH = "https://stac.astrogeology.usgs.gov/api/search"
CTX_COLLECTION = "mro_ctx_controlled_usgs_dtms"
BASE_DEM_URL = ("https://asc-pds-services.s3.us-west-2.amazonaws.com/mosaic/Mars/"
                "HRSC_MOLA_Blend/Mars_HRSC_MOLA_BlendDEM_Global_200mp_v2.tif")
CACHE_DIR = Path(__file__).resolve().parent / "imagery_cache"

MAX_RES = 4097          # Unity's largest heightmap
TARGET_SPACING = 20.0   # metres, CTX DTM resolution
MAX_CTX_DTMS = 60       # safety limit on downloads
MAX_RESIDUAL_STD = 60.0  # m; CTX DTMs noisier than this vs. the base are skipped
MAX_SPIKE = 400.0        # m; CTX pixels this far from the base are treated as bad data

GDAL_ENV = dict(GDAL_HTTP_USERAGENT=UA, GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR",
                GDAL_HTTP_MAX_RETRY="3", GDAL_HTTP_RETRY_DELAY="2",
                CPL_VSIL_CURL_ALLOWED_EXTENSIONS=".tif")


# ---------------------------------------------------------------
# Polygon input
# ---------------------------------------------------------------
def parse_polygon(text=None, path=None):
    """Polygon as [(lon, lat), ...] in degrees east, -180..180.
    text: "lon,lat lon,lat ..." or "lon,lat; lon,lat; ..."
    path: GeoJSON (Polygon / Feature / FeatureCollection) or a text/CSV file with
          one "lon,lat" per line (a header line is ignored)."""
    pts = []
    if path:
        raw = Path(path).read_text()
        if raw.lstrip().startswith("{"):
            g = json.loads(raw)
            if g.get("type") == "FeatureCollection":
                g = g["features"][0]
            if g.get("type") == "Feature":
                g = g["geometry"]
            ring = g["coordinates"][0] if g["type"] == "Polygon" else g["coordinates"][0][0]
            pts = [(float(p[0]), float(p[1])) for p in ring]
        else:
            for line in raw.splitlines():
                parts = line.replace(";", ",").replace("\t", ",").split(",")
                try:
                    pts.append((float(parts[0]), float(parts[1])))
                except (ValueError, IndexError):
                    continue  # header or blank line
    elif text:
        for chunk in text.replace(";", " ").split():
            lon, lat = chunk.split(",")
            pts.append((float(lon), float(lat)))

    if len(pts) > 1 and pts[0] == pts[-1]:
        pts = pts[:-1]
    if len(pts) < 3:
        raise ValueError("A polygon needs at least 3 points")

    # lat,lon or lon,lat? Latitude can't be beyond +-90, so if the SECOND numbers
    # go past 90 the points must be lat,lon (e.g. "-4.25,355.68").
    if any(abs(b) > 90 for _, b in pts):
        if any(abs(a) > 90 for a, _ in pts):
            raise ValueError("Neither number in the points looks like a latitude (-90..90)")
        pts = [(b, a) for a, b in pts]
        print("Polygon: points read as lat,lon")
    else:
        print("Polygon: points read as lon,lat (if that's wrong, put a longitude above 90 "
              "first, e.g. 0-360 E)")

    pts = [((lon + 180) % 360 - 180, lat) for lon, lat in pts]  # 0-360 -> -180..180
    lons = [p[0] for p in pts]
    if max(lons) - min(lons) > 180:
        raise ValueError("Polygons crossing the 180 E meridian aren't supported")

    # Points must go around the edge in order. If the edges cross (e.g. corners given
    # as top-left, top-right, bottom-left, bottom-right), sort them around the centre.
    if self_intersects(pts):
        cx = sum(p[0] for p in pts) / len(pts)
        cy = sum(p[1] for p in pts) / len(pts)
        pts.sort(key=lambda p: math.atan2(p[1] - cy, p[0] - cx))
        print("Polygon: edges crossed, so the points were re-ordered around the centre")
    return pts


def self_intersects(pts):
    """True if any two non-adjacent edges of the polygon cross."""
    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    def hit(p1, p2, p3, p4):
        d1, d2 = cross(p3, p4, p1), cross(p3, p4, p2)
        d3, d4 = cross(p1, p2, p3), cross(p1, p2, p4)
        return (d1 > 0) != (d2 > 0) and (d3 > 0) != (d4 > 0)

    n = len(pts)
    edges = [(pts[i], pts[(i + 1) % n]) for i in range(n)]
    for i in range(n):
        for j in range(i + 2, n):
            if i == 0 and j == n - 1:
                continue  # these two share the first point
            if hit(*edges[i], *edges[j]):
                return True
    return False


def polygon_mask(poly, lons, lats):
    """Boolean grid [lat, lon]: True inside the polygon (even-odd ray casting)."""
    X, Y = np.meshgrid(lons, lats)
    inside = np.zeros(X.shape, bool)
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if y1 == y2:
            continue
        crosses = (Y >= min(y1, y2)) & (Y < max(y1, y2))
        xint = x1 + (Y - y1) * (x2 - x1) / (y2 - y1)
        inside ^= crosses & (X < xint)
    return inside


# ---------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------
def box_blur(a, r):
    """Mean filter with radius r (pixels), via cumulative sums. Edges use what's there."""
    if r < 1:
        return a.copy()
    out = a.astype(np.float64)
    for axis in (0, 1):
        c = np.cumsum(np.pad(out, [(r + 1, r) if ax == axis else (0, 0) for ax in (0, 1)],
                             mode="edge"), axis=axis)
        hi = np.take(c, np.arange(2 * r + 1, c.shape[axis]), axis=axis)
        lo = np.take(c, np.arange(0, c.shape[axis] - 2 * r - 1), axis=axis)
        out = (hi - lo) / (2 * r + 1)
    return out


_OUT = threading.local()  # _OUT.quiet = True silences this thread (tiles built in parallel)


def print(*args, **kwargs):  # noqa: A001  (module-level: lets parallel tile builds stay quiet)
    if not getattr(_OUT, "quiet", False):
        builtins.print(*args, **kwargs)


READ_THREADS = 8  # COG downloads at once (they're slow because of network latency, not size)


def stac_search(poly):
    """CTX DTMs overlapping `poly`, from the USGS catalogue. Cached on disk (it can take
    15 s and return 1000 items over busy places like Gale crater)."""
    ring = [[lon, lat] for lon, lat in poly] + [[poly[0][0], poly[0][1]]]
    cache = CACHE_DIR / f"stac_{hashlib.sha1(json.dumps(ring).encode()).hexdigest()[:16]}.json"
    if cache.exists():
        return json.loads(cache.read_text())
    items = _stac_search(ring)
    CACHE_DIR.mkdir(exist_ok=True)
    cache.write_text(json.dumps(items))
    return items


def missing_file(e):
    """True when a download failed because the file doesn't exist (the catalogue lists a few
    that were never uploaded). That's permanent, so unlike a timeout it is no reason to
    leave the result uncached and try again next time."""
    return "404" in str(e) or "No such file" in str(e) or "does not exist" in str(e)


def read_warped(href, crs, transform, res, resampling, spacing, zero_is_nodata=False):
    """One Cloud-Optimized GeoTIFF warped onto our grid, as float32 (NaN = no data).
    When the grid is coarser than the file, reads one of its built-in smaller copies
    (overviews) instead of the full resolution: far less to download. Thread-safe."""
    import rasterio
    from rasterio.vrt import WarpedVRT
    with rasterio.Env(**GDAL_ENV):  # GDAL settings are per thread
        with rasterio.open("/vsicurl/" + href) as src:
            px = abs(src.res[0]) * (M_PER_DEG if src.crs and src.crs.is_geographic else 1)
            level = None
            for i, f in enumerate(src.overviews(1)):
                if px * f <= spacing * 1.01:
                    level = i
            nodata = (src.nodata or 0) if zero_is_nodata else src.nodata
        with rasterio.open("/vsicurl/" + href, overview_level=level) as src, \
                WarpedVRT(src, crs=crs, transform=transform, width=res, height=res,
                          resampling=resampling, src_nodata=nodata, nodata=np.nan,
                          dtype="float32") as v:
            return v.read(1)


def _stac_search(ring):
    body = {"collections": [CTX_COLLECTION],
            "intersects": {"type": "Polygon", "coordinates": [ring]}, "limit": 200}
    items, url, data = [], STAC_SEARCH, json.dumps(body).encode()
    while url and len(items) < 1000:
        req = urllib.request.Request(url, data=data,
                                     headers={"User-Agent": UA, "Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=60) as r:
            page = json.load(r)
        items += page.get("features", [])
        nxt = next((l for l in page.get("links", []) if l.get("rel") == "next"), None)
        if not nxt:
            break
        url = nxt["href"]
        data = json.dumps(nxt["body"]).encode() if nxt.get("body") else None
    return items


def asset_href(item, *keys):
    for k in keys:
        if k in item["assets"]:
            return item["assets"][k]["href"]
    return None


def progress(label, done, total, start=None):
    """One-line text progress bar that redraws itself: Label [######----] 60% 12/20 (8s)"""
    import sys
    if getattr(_OUT, "quiet", False):
        return
    width = 30
    frac = done / total if total else 1
    bar = "#" * int(frac * width) + "-" * (width - int(frac * width))
    took = f" ({time.time() - start:.0f}s)" if start else ""
    sys.stdout.write(f"\r{label} [{bar}] {frac * 100:3.0f}% {done}/{total}{took}   ")
    if done >= total:
        sys.stdout.write("\n")
    sys.stdout.flush()


_BASE_CONNS = queue.LifoQueue()  # open HTTPS connections to the base DEM, reused


def read_base(west, south, east, north, res, threads=48):
    """Base elevation [res, res] (row 0 = north) from the 200 m MOLA-HRSC DEM.

    The file is 11 GB, uncompressed, one row per block, no overviews, and in plain
    lat/lon. So instead of a generic warp we read only the rows we need with plain HTTP
    range requests, 48 at a time (~20x faster than going through GDAL), and interpolate
    ourselves. Each request only downloads the columns inside the box."""
    import http.client
    import urllib.parse
    from concurrent.futures import ThreadPoolExecutor, as_completed
    import rasterio

    url = "/vsicurl/" + BASE_DEM_URL
    with rasterio.open(url) as src:
        T, nodata = src.transform, src.nodata
        width, height = src.width, src.height
        # Where each row starts in the file. Rows are evenly spaced; check that holds.
        off0 = int(src.get_tag_item("BLOCK_OFFSET_0_0", "TIFF", bidx=1))
        off1 = int(src.get_tag_item("BLOCK_OFFSET_0_1", "TIFF", bidx=1))
        offn = int(src.get_tag_item(f"BLOCK_OFFSET_0_{height - 1}", "TIFF", bidx=1))
        dtype = np.dtype(src.dtypes[0]).newbyteorder("<")
    stride = off1 - off0
    if stride != width * dtype.itemsize or offn != off0 + (height - 1) * stride:
        raise RuntimeError("Unexpected base DEM file layout")
    # Fractional source row/col of every destination point (pixel centres)
    lons = np.linspace(west, east, res)
    lats = np.linspace(north, south, res)
    fc = (lons - T.c) / T.a - 0.5
    fr = (lats - T.f) / T.e - 0.5
    c0 = max(int(np.floor(fc.min())), 0)
    c1 = min(int(np.floor(fc.max())) + 1, width - 1)
    r_lo = max(int(np.floor(fr.min())), 0)
    r_hi = min(int(np.floor(fr.max())) + 1, height - 1)

    # Fine grid (<= ~2 source rows per output row): read every row, interpolate in both
    # directions. Coarse grid: read only the 2 rows around each output row.
    rows = sorted({min(max(r, r_lo), r_hi) for f in fr for r in (int(np.floor(f)), int(np.floor(f)) + 1)})
    if r_hi - r_lo + 1 <= len(rows) * 1.2:
        rows = list(range(r_lo, r_hi + 1))
    ncols = c1 - c0 + 1
    mb = len(rows) * ncols * 2 / 1e6
    print(f"Elevation: reading 200 m MOLA-HRSC base, {len(rows)} rows x {ncols} columns "
          f"(~{mb:.0f} MB)")

    u = urllib.parse.urlparse(BASE_DEM_URL)

    def read_row(r):
        a = off0 + r * stride + c0 * dtype.itemsize
        b = a + ncols * dtype.itemsize - 1
        for attempt in range(4):
            # Connections are kept open and shared between calls (and tiles): a new secure
            # connection costs ~0.5 s, a request on an open one ~0.1 s
            try:
                conn = _BASE_CONNS.get_nowait()
            except queue.Empty:
                conn = http.client.HTTPSConnection(u.netloc, timeout=60)
            try:
                conn.request("GET", u.path, headers={"Range": f"bytes={a}-{b}", "User-Agent": UA})
                resp = conn.getresponse()
                body = resp.read()
                if resp.status == 206 and len(body) == b - a + 1:
                    _BASE_CONNS.put(conn)
                    return r, np.frombuffer(body, dtype)
                raise OSError(f"HTTP {resp.status}")
            except (OSError, http.client.HTTPException):
                conn.close()
                time.sleep(1 + attempt)
        raise OSError(f"Could not download row {r} of the base DEM")

    data, start = {}, time.time()
    with ThreadPoolExecutor(min(threads, len(rows))) as pool:
        futures = [pool.submit(read_row, r) for r in rows]
        for i, f in enumerate(as_completed(futures), 1):
            r, row = f.result()
            row = row.astype(np.float32)
            if nodata is not None:
                row[row == nodata] = np.nan  # missing data stays missing through interpolation
            data[r] = row
            if i % 50 == 0 or i == len(rows):
                progress("  base", i, len(rows), start)

    # Bilinear interpolation from the rows we have
    def row_of(r):
        return data[min(max(r, r_lo), r_hi)] if min(max(r, r_lo), r_hi) in data else None
    out = np.empty((res, res), np.float32)
    cc = np.clip(fc - c0, 0, ncols - 1)
    ci = np.minimum(np.floor(cc).astype(int), ncols - 2) if ncols > 1 else np.zeros(res, int)
    cw = (cc - ci) if ncols > 1 else np.zeros(res)
    for i, f in enumerate(fr):
        r = int(np.floor(f))
        a, b = row_of(r), row_of(r + 1)
        a = a if a is not None else b
        b = b if b is not None else a
        w = min(max(f - r, 0), 1)
        line = a * (1 - w) + b * w
        out[i] = line[ci] * (1 - cw) + line[np.minimum(ci + 1, ncols - 1)] * cw
    return out


def png_gray(img):
    """Minimal greyscale PNG encoder (no Pillow needed). img: 2D uint8, row 0 = top."""
    h, w = img.shape
    raw = b"".join(b"\x00" + img[y].tobytes() for y in range(h))

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    data = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 0, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))
    return data


WMS_URL = "https://planetarymaps.usgs.gov/cgi-bin/mapserv?map=/maps/mars/mars_simp_cyl.map"
WMS_MAX_SIZE = 4096  # the server refuses anything larger


def fetch_themis(bounds, width, layer="THEMIS"):
    """THEMIS day IR (100 m/px) PNG for the box (west, south, east, north) from the USGS map
    server, or None if the download fails. Cached in CACHE_DIR."""
    import urllib.parse
    west, south, east, north = bounds
    aspect = (east - west) / (north - south)  # pixels are square in degrees
    width = min(width, WMS_MAX_SIZE)
    height = round(width / aspect)
    if height > WMS_MAX_SIZE:
        height = WMS_MAX_SIZE
        width = round(height * aspect)

    CACHE_DIR.mkdir(exist_ok=True)
    cache = CACHE_DIR / f"{layer}_{west:.5f}_{south:.5f}_{east:.5f}_{north:.5f}_{width}x{height}.png"
    if cache.exists():
        print(f"Imagery {layer}: using cached {cache.name}")
        return cache.read_bytes()

    params = {"SERVICE": "WMS", "VERSION": "1.1.1", "REQUEST": "GetMap", "LAYERS": layer,
              "STYLES": "", "SRS": "EPSG:4326", "BBOX": f"{west},{south},{east},{north}",
              "WIDTH": width, "HEIGHT": height, "FORMAT": "image/png"}
    url = WMS_URL + "&" + urllib.parse.urlencode(params)
    print(f"Imagery {layer}: downloading {width}x{height} from USGS...")
    try:
        # The server returns HTTP 403 for Python's default User-Agent
        req = urllib.request.Request(url, headers={"User-Agent": UA})
        with urllib.request.urlopen(req, timeout=180) as r:
            total = int(r.headers.get("Content-Length") or 0)
            chunks, got = [], 0
            while chunk := r.read(256 * 1024):
                chunks.append(chunk)
                got += len(chunk)
                print(f"\r  downloading imagery... {got / 1e6:.1f}"
                      + (f"/{total / 1e6:.1f}" if total else "") + " MB   ", end="", flush=True)
            print()
            data, ctype = b"".join(chunks), r.headers.get("Content-Type", "")
    except OSError as e:
        print(f"Imagery download failed ({e}). Continuing without THEMIS.")
        return None
    if not ctype.startswith("image/"):
        print("Imagery download failed, the server said:\n" + data.decode("utf-8", "replace")[:500])
        return None
    cache.write_bytes(data)
    return data


# ---------------------------------------------------------------
# Main entry point
# ---------------------------------------------------------------
def build(poly, imagery=True, fetch_themis=fetch_themis, max_res=MAX_RES,
          box=None, res=None, size=None, ctx_stats=None):
    """Elevation + image for `poly`. For one tile of a big polygon (see build_tiles) pass
    box=(west, south, east, north), res and size=(size_x, size_z) explicitly, plus one shared
    ctx_stats dict so each CTX photo gets the same brightness match in every tile.
    Nothing is cached if a download failed (so the next run tries again).
    Returns dict with:
       grid   [res, res] float32 metres, row 0 = south
       mask   [res, res] bool, True inside the polygon (row 0 = south)
       size_x, size_z  metres
       bounds (west, south, east, north)
       image  PNG bytes or None (north at top)"""
    import rasterio
    from rasterio.enums import Resampling
    from rasterio.transform import from_bounds
    from rasterio.vrt import WarpedVRT

    tile = box is not None
    if tile:
        west, south, east, north = box
        size_x, size_z = size
        spacing = max(size_x, size_z) / (res - 1)
    else:
        west, south, east, north = polygon_box(poly)
        size_x, size_z = box_size(west, south, east, north)
        longest = max(size_x, size_z)
        res = MAX_RES
        for r in (257, 513, 1025, 2049, 4097):
            if r >= longest / TARGET_SPACING + 1:
                res = r
                break
        res = min(res, max_res)
        spacing = longest / (res - 1)
        print(f"Polygon: {len(poly)} points, box {size_x / 1000:.1f} x {size_z / 1000:.1f} km, "
              f"lon {west:.4f}..{east:.4f}, lat {south:.4f}..{north:.4f}")
        print(f"Grid: {res}x{res}, {spacing:.0f} m between points"
              + ("" if spacing <= TARGET_SPACING + 0.5 else
                 f"  (polygon too big for {TARGET_SPACING:.0f} m in one Unity terrain; "
                 f"draw a smaller polygon for full CTX detail)"))

    key_parts = [poly, res, imagery] + ([list(box), list(size), "v2"] if tile else [])
    key = hashlib.sha1(json.dumps(key_parts).encode()).hexdigest()[:16]
    CACHE_DIR.mkdir(exist_ok=True)
    cache = CACHE_DIR / f"polygon_{key}.npz"
    img_cache = CACHE_DIR / f"polygon_{key}_img{IMAGE_VERSION}.png"
    old_img = CACHE_DIR / f"polygon_{key}.png"  # photos made before IMAGE_VERSION existed
    search_ring = poly if not tile else [(west, south), (east, south), (east, north), (west, north)]
    status = {"degraded": False}  # a download failed: don't cache this result
    if ctx_stats is None:
        ctx_stats = {}
    if cache.exists():
        print(f"Using cached terrain {cache.name}")
        z = np.load(cache)
        if ctx_stats is not None and "ctx_stats" in z.files:
            for k, v in json.loads(str(z["ctx_stats"])).items():
                ctx_stats.setdefault(k, v)
        image = None
        if imagery:
            if img_cache.exists():
                image = img_cache.read_bytes()
            elif "ctx_stats" in z.files:
                # Heights are cached but the photo was made by older code: remake just the
                # photo (THEMIS + the same CTX images; no elevation downloads)
                ids = list(json.loads(str(z["ctx_stats"])))
                print(f"Remaking the photo with the current image code ({len(ids)} CTX images)")
                try:
                    by_id = {it["id"]: it for it in stac_search(search_ring)}
                    with rasterio.Env(**GDAL_ENV):
                        with rasterio.open("/vsicurl/" + BASE_DEM_URL) as src:
                            crs = src.crs
                        image = build_imagery(
                            [by_id[i] for i in ids if i in by_id], crs,
                            from_bounds(west, south, east, north, res, res), res,
                            (west, south, east, north), fetch_themis, Resampling, WarpedVRT,
                            rasterio, z["mask"][::-1], ctx_stats=ctx_stats, status=status,
                            spacing=spacing)
                    if image is not None and not status["degraded"]:
                        img_cache.write_bytes(image)
                except Exception as e:  # noqa: BLE001  (fall back to the old photo)
                    print(f"  could not remake it ({e}); using the old one")
            if image is None and old_img.exists():
                image = old_img.read_bytes()
        return dict(grid=z["grid"], mask=z["mask"], size_x=float(z["size_x"]),
                    size_z=float(z["size_z"]), bounds=(west, south, east, north), image=image,
                    cover=None)

    # Destination grid: pixel CENTRES run exactly from west..east and south..north
    dx, dy = (east - west) / (res - 1), (north - south) / (res - 1)
    transform = from_bounds(west - dx / 2, south - dy / 2, east + dx / 2, north + dy / 2, res, res)
    lons = np.linspace(west, east, res)
    lats_n = np.linspace(north, south, res)  # rasterio rows go north -> south
    inside = polygon_mask(poly, lons, lats_n)

    t0 = time.time()
    with rasterio.Env(**GDAL_ENV):
        # 1. 200 m base
        with rasterio.open("/vsicurl/" + BASE_DEM_URL) as src:
            crs = src.crs
        base = read_base(west, south, east, north, res)
        if np.isnan(base).all():
            raise RuntimeError("The base DEM has no data for this polygon")
        base[np.isnan(base)] = np.nanmean(base)

        # 2. CTX DTMs, biggest overlap with the polygon first.
        #    Pointless when the grid is coarser than the 200 m base itself.
        items = []
        if spacing >= 200:
            print(f"Elevation: skipping 20 m CTX DTMs, points are {spacing:.0f} m apart "
                  f"(draw a polygon under ~80 km across to use them)")
        else:
            try:
                # A tile only needs the DTMs that touch it
                items = stac_search(search_ring)
            except OSError as e:
                status["degraded"] = True
                print(f"CTX catalogue search failed ({e}); using the 200 m base only")
        poly_area = max((east - west) * (north - south), 1e-12)

        def overlap(it):
            b = it["bbox"]
            w = max(0, min(east, b[2]) - max(west, b[0]))
            h = max(0, min(north, b[3]) - max(south, b[1]))
            return w * h / poly_area
        items.sort(key=overlap, reverse=True)
        if items:
            print(f"Elevation: {len(items)} CTX DTMs overlap the polygon")

        # Weighted average of all useful DTMs: each one fades out at its edges,
        # so overlaps and gap-fills blend instead of leaving seams.
        num = np.zeros((res, res), np.float64)     # sum of weight * height
        weight = np.zeros((res, res), np.float32)  # sum of weights
        n_inside = max(int(inside.sum()), 1)
        used, stale = [], 0
        yy, xx = np.mgrid[0:res, 0:res].astype(np.float32) / res
        feather = max(2, int(round(400 / spacing)))  # ~400 m blend at DTM edges

        # Choosing which DTMs to download (each costs 2-10 s): every catalogue entry has the
        # DTM's real outline. On a coarse grid, always download next the DTM that covers the
        # most polygon not covered yet (counting downloads already under way), and stop when
        # none adds anything. Over busy places (1000 candidates) that's a handful of downloads.
        C = 128
        ci = np.linspace(0, res - 1, C).astype(int)
        inside_c = inside[np.ix_(ci, ci)]
        lons_c = np.linspace(west, east, C)
        lats_c = np.linspace(north, south, C)
        n_in_c = max(int(inside_c.sum()), 1)

        def footprint(it):
            g = it.get("geometry") or {}
            polys = ([g["coordinates"]] if g.get("type") == "Polygon" else
                     g.get("coordinates", []) if g.get("type") == "MultiPolygon" else [])
            m = np.zeros((C, C), bool)
            for rings in polys:
                ring = [(x - 360 if x > 180 else x, y) for x, y in rings[0]]
                m |= polygon_mask(ring, lons_c, lats_c)
            if not polys:  # no outline: use the bounding box
                b = it.get("bbox") or [west, south, east, north]
                m = ((lons_c[None, :] >= b[0]) & (lons_c[None, :] <= b[2])
                     & (lats_c[:, None] >= b[1]) & (lats_c[:, None] <= b[3]))
            return m & inside_c

        cand = [it for it in items if asset_href(it, "dtm", "geoid_adjusted_dem", "dem")]
        F = (np.array([footprint(it).ravel() for it in cand], np.float32)
             if cand else np.zeros((0, C * C), np.float32))
        tried = np.zeros(len(cand), bool)

        from collections import deque
        from concurrent.futures import ThreadPoolExecutor
        pending = deque()   # (index, future) being downloaded, best first
        n_read = 0
        t_dtm = time.time()

        def refill(pool):
            nonlocal n_read
            while len(pending) < READ_THREADS and n_read < MAX_CTX_DTMS and len(cand):
                claimed = weight[np.ix_(ci, ci)] > 0.5
                for j, _ in pending:
                    claimed |= F[j].reshape(C, C) > 0
                gains = F @ (inside_c & ~claimed).ravel().astype(np.float32)
                gains[tried] = -1
                j = int(np.argmax(gains))
                if gains[j] / n_in_c < 0.002:
                    return  # nothing left that adds coverage
                tried[j] = True
                n_read += 1
                href = asset_href(cand[j], "dtm", "geoid_adjusted_dem", "dem")
                pending.append((j, pool.submit(read_warped, href, crs, transform, res,
                                               Resampling.bilinear, spacing)))

        pool = ThreadPoolExecutor(READ_THREADS)
        try:
            refill(pool)
            while pending:
                j, fut = pending.popleft()
                it = cand[j]
                print(f"  CTX DTMs: {n_read} of {len(cand)} downloaded, "
                      f"{time.time() - t_dtm:.0f}s", end="\r", flush=True)
                if stale >= 6:
                    break  # several in a row added nothing (their outlines promised more)
                try:
                    ctx = fut.result()
                except Exception as e:
                    status["degraded"] |= not missing_file(e)
                    print(f"  skip {it['id'][:30]}: {e}")
                    refill(pool)
                    continue
                ok = np.isfinite(ctx) & inside
                if ok.sum() < 100:
                    refill(pool)
                    continue
                # Level it to the base: fit offset + tilt to (ctx - base)
                d = (ctx - base)[ok]
                A = np.c_[np.ones(d.size), xx[ok], yy[ok]]
                coef, *_ = np.linalg.lstsq(A, d, rcond=None)
                plane = coef[0] + coef[1] * xx + coef[2] * yy
                resid = ctx - plane - base
                if np.nanstd(resid[ok]) > MAX_RESIDUAL_STD:
                    print(f"  skip {it['id'][:30]}: too noisy (std {np.nanstd(resid[ok]):.0f} m)")
                    refill(pool)
                    continue
                ok &= np.abs(resid) < MAX_SPIKE
                ctx = ctx - plane

                # Feathered weight: 1 well inside the DTM, fading to 0 at its edges
                w = box_blur(ok.astype(np.float32), feather)
                w = np.clip((w - 0.5) * 2, 0, 1).astype(np.float32) * ok
                gain = ((weight < 0.5) & (w > 0.5) & inside).sum() / n_inside
                if used and gain < 0.002:
                    stale += 1
                    refill(pool)
                    continue  # covers only what we already have
                stale = 0
                num += w * np.where(ok, ctx, 0)
                weight += w
                used.append(it)
                covered = (weight[inside] > 0.5).mean() * 100
                print(f"  + {it['id'][:44]}  ({covered:.0f}% of polygon now CTX)              ")
                refill(pool)
        finally:
            pool.shutdown(wait=False, cancel_futures=True)  # don't wait for unneeded downloads
        if items:
            print(f"  CTX DTMs: downloaded {n_read} of {len(cand)} candidates, {len(used)} used, "
                  f"{time.time() - t_dtm:.0f}s                    ")

        ctx_avg = np.where(weight > 0, num / np.maximum(weight, 1e-6), 0)
        cw = np.clip(weight, 0, 1)
        best = (base * (1 - cw) + ctx_avg * cw).astype(np.float32)
        cover = (weight[inside] > 0.5).mean() * 100
        print(f"Elevation: done in {time.time() - t0:.0f}s"
              + (f", CTX 20 m covers {cover:.0f}% of the polygon, the rest is 200 m" if items else ""))

        # 3. Imagery: THEMIS base + CTX orthoimages on top
        image = None
        if imagery:
            # Image pixels edge-to-edge over the box (like the THEMIS download), which is how
            # Unity drapes it: one texture stretched from the first to the last height point
            img_transform = from_bounds(west, south, east, north, res, res)
            image = build_imagery(used, crs, img_transform, res, (west, south, east, north),
                                  fetch_themis, Resampling, WarpedVRT, rasterio, inside,
                                  ctx_stats=ctx_stats, status=status, spacing=spacing)

    grid = best[::-1].astype(np.float32)    # row 0 = south
    mask = inside[::-1]
    if status["degraded"]:
        print("Some downloads failed: not caching this area, so the next run tries them again")
    else:
        if image is not None:  # image first: a cache hit expects it when imagery is on
            img_cache.write_bytes(image)
        np.savez_compressed(cache, grid=grid, mask=mask, size_x=size_x, size_z=size_z,
                            ctx_stats=json.dumps({k: ctx_stats[k] for k in
                                                  (it["id"] for it in used) if k in ctx_stats}))
    return dict(grid=grid, mask=mask, size_x=size_x, size_z=size_z,
                bounds=(west, south, east, north), image=image,
                cover=float(cover) if items else 0.0)


def masked_blur(a, ok, r):
    """Mean of `a` over the `ok` pixels within radius r (NaN where there are none)."""
    num = box_blur(np.where(ok, a, 0).astype(np.float32), r)
    den = box_blur(ok.astype(np.float32), r)
    return np.where(den > 1e-3, num / np.maximum(den, 1e-3), np.nan)


def build_imagery(items, crs, transform, res, bounds, fetch_themis, Resampling, WarpedVRT, rasterio,
                  inside=None, ctx_stats=None, status=None, spacing=20.0):
    """Greyscale image on the same grid as the terrain (north at top), as PNG bytes.
    ctx_stats {CTX id: [gain, offset]}: brightness match per CTX photo, reused when given and
    filled in otherwise, so a photo spanning several tiles looks the same in all of them."""
    ctx_stats = {} if ctx_stats is None else ctx_stats
    status = {} if status is None else status
    themis = None
    if fetch_themis:
        png = fetch_themis(bounds, res)
        if not png:
            status["degraded"] = True
        if png:
            # Decode the THEMIS PNG into the grid via GDAL (it can read PNG bytes from memory)
            import warnings
            from rasterio.io import MemoryFile
            from rasterio.errors import NotGeoreferencedWarning
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", NotGeoreferencedWarning)
                with MemoryFile(png) as mf, mf.open() as src:
                    a = src.read(1, out_shape=(res, res), resampling=Resampling.bilinear)
                    themis = a.astype(np.float32)
    img = themis if themis is not None else np.full((res, res), np.nan, np.float32)

    # Download the photos in parallel (blended below in priority order)
    from concurrent.futures import ThreadPoolExecutor
    pool = ThreadPoolExecutor(READ_THREADS)
    futures = [(it, pool.submit(read_warped, href, crs, transform, res, Resampling.average,
                                spacing, True))
               for it in items if (href := asset_href(it, "orthoimage"))]
    pool.shutdown(wait=False)
    for n_it, (it, fut) in enumerate(futures, 1):
        print(f"  CTX image {n_it}/{len(futures)}...", end="\r", flush=True)
        try:
            o = fut.result()
        except Exception as e:
            status["degraded"] |= not missing_file(e)
            print(f"  skip CTX image {it['id'][:30]}: {e}")
            continue
        ok = np.isfinite(o) & (o > 0)
        if ok.sum() < 100:
            continue
        # Match brightness/contrast to what's underneath so patches don't stand out
        under = img[ok]
        good = np.isfinite(under)
        if it["id"] in ctx_stats:
            gain, offset = ctx_stats[it["id"]]
            o = o * gain + offset
        elif good.sum() > 100:
            o_m, o_s = o[ok][good].mean(), o[ok][good].std() + 1e-6
            u_m, u_s = under[good].mean(), under[good].std() + 1e-6
            gain = float(u_s / o_s)
            offset = float(u_m - o_m * gain)
            ctx_stats[it["id"]] = [gain, offset]
            o = o * gain + offset
        else:
            lo, hi = np.percentile(o[ok], [1, 99])
            o = (o - lo) / max(hi - lo, 1e-6) * 255
        # Then match brightness locally too: fine detail from the CTX photo, broad brightness
        # from what's underneath (THEMIS). Photos taken in different seasons / sun angles then
        # line up with each other, also across tiles, instead of leaving bright/dark strips.
        under_ok = ok & np.isfinite(img)
        if under_ok.sum() > 100:
            r = max(4, res // 40)
            o_lp = masked_blur(o, ok, r)
            u_lp = masked_blur(img, under_ok, r)
            o = np.where(np.isfinite(u_lp), o - o_lp + u_lp, o)
        w = box_blur(ok.astype(np.float32), max(2, res // 200))
        w = np.clip((w - 0.5) * 2, 0, 1) * ok
        o = np.where(ok, o, 0)  # NaN * 0 is still NaN, so zero it outside the photo
        img = np.where(np.isfinite(img), img * (1 - w) + o * w, np.where(ok, o, img))
        print(f"  + CTX image {it['id'][:44]}")

    if np.isnan(img).all():
        return None
    img[np.isnan(img)] = np.nanmean(img)
    if inside is not None:
        img = np.where(inside, img, img * 0.5)  # dim outside the polygon so its outline shows
    img8 = np.clip(img, 0, 255).astype(np.uint8)
    print(f"Imagery: {res}x{res} (THEMIS{' + CTX' if items else ''})")
    return png_gray(img8)


# ---------------------------------------------------------------
# Big polygons: a grid of Unity terrains that join seamlessly
# ---------------------------------------------------------------
IMAGE_VERSION = 2  # bump when the photo code changes: cached photos are remade, heights kept
TILE_WORKERS = 3   # tiles built at the same time
TILE_RES = 2049    # heightmap size of each tile: 2048 cells x 20 m = ~41 km per tile
MAX_TILES = 16     # more tiles = more detail for huge areas, but more memory in Unity


def polygon_box(poly):
    lons = [p[0] for p in poly]
    lats = [p[1] for p in poly]
    return min(lons), min(lats), max(lons), max(lats)


def box_size(west, south, east, north):
    """(east-west, north-south) size in metres, measured at the box's middle latitude."""
    lat_mid = math.radians((south + north) / 2)
    return (east - west) * M_PER_DEG * math.cos(lat_mid), (north - south) * M_PER_DEG


def box_touches_polygon(poly, box):
    """Exact test: does the polygon overlap the box (west, south, east, north)?"""
    w, s, e, n = box
    if any(w <= x <= e and s <= y <= n for x, y in poly):        # a vertex inside the box
        return True
    if polygon_mask(poly, np.array([(w + e) / 2]), np.array([(s + n) / 2])).any():
        return True                                               # box centre inside polygon

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    def hit(p1, p2, p3, p4):
        d1, d2 = cross(p3, p4, p1), cross(p3, p4, p2)
        d3, d4 = cross(p1, p2, p3), cross(p1, p2, p4)
        return (d1 > 0) != (d2 > 0) and (d3 > 0) != (d4 > 0)

    corners = [(w, s), (e, s), (e, n), (w, n)]
    for i in range(len(poly)):                                    # an edge crosses the box
        p1, p2 = poly[i], poly[(i + 1) % len(poly)]
        if any(hit(p1, p2, corners[j], corners[(j + 1) % 4]) for j in range(4)):
            return True
    return False


def _try(f, *a):
    try:
        return f(*a)
    except Exception:  # noqa: BLE001  (the tile's own search will retry and report it)
        return None


def needs_tiles(poly):
    """True when one 4097 terrain can't hold the polygon at the full 20 m."""
    sx, sz = box_size(*polygon_box(poly))
    return max(sx, sz) / (MAX_RES - 1) > TARGET_SPACING + 0.5


def stitch(tiles, nx, nz, blend=32):
    """Make neighbouring tiles share identical edge rows (each tile was levelled on its own,
    so their edges can differ by a few metres). The difference is spread over `blend` cells
    on both sides, then the edges are copied exactly, so there is no step and no crack.
    tiles: {(ix, iz): grid [res, res], row 0 = south, column 0 = west}"""
    for _ in range(4):  # repeated, so what one pass leaves at a corner shrinks to nothing
        _blend_edges(tiles, blend)
    # Exact copies, in order, so shared edges are identical
    for iz in range(nz):
        for ix in range(nx):
            t = tiles.get((ix, iz))
            if t is None:
                continue
            if (ix - 1, iz) in tiles:
                t[:, 0] = tiles[(ix - 1, iz)][:, -1]
            if (ix, iz - 1) in tiles:
                t[0, :] = tiles[(ix, iz - 1)][-1, :]
    # Grid corners (shared by up to 4 tiles, some maybe missing): one value for all of them
    for cz in range(1, nz):
        for cx in range(1, nx):
            spots = [((cx - 1, cz - 1), -1, -1), ((cx, cz - 1), -1, 0),
                     ((cx - 1, cz), 0, -1), ((cx, cz), 0, 0)]
            spots = [(tiles[k], r, c) for k, r, c in spots if k in tiles]
            if len(spots) > 1:
                v = sum(t[r, c] for t, r, c in spots) / len(spots)
                for t, r, c in spots:
                    t[r, c] = v


def _blend_edges(tiles, blend):
    """Spread each shared edge's difference over `blend` cells on both sides."""
    for (ix, iz), a in tiles.items():          # east neighbour
        b = tiles.get((ix + 1, iz))
        if b is None:
            continue
        d = (b[:, 0] - a[:, -1]) / 2           # meet in the middle
        k = min(blend, a.shape[1] - 1)
        ramp = np.linspace(0, 1, k + 1)[1:]    # 0 -> 1 towards the edge
        a[:, -k:] += d[:, None] * ramp[None, :]
        b[:, :k] -= d[:, None] * ramp[::-1][None, :]
    for (ix, iz), a in tiles.items():          # north neighbour
        b = tiles.get((ix, iz + 1))
        if b is None:
            continue
        d = (b[0, :] - a[-1, :]) / 2
        k = min(blend, a.shape[0] - 1)
        ramp = np.linspace(0, 1, k + 1)[1:]
        a[-k:, :] += d[None, :] * ramp[:, None]
        b[:k, :] -= d[None, :] * ramp[::-1][:, None]


def build_tiles(poly, imagery=True, fetch_themis=fetch_themis, tile_res=TILE_RES,
                max_tiles=MAX_TILES):
    """Split a big polygon into nx x nz tiles, each tile_res x tile_res, as close to 20 m as
    max_tiles allows. Tiles that don't touch the polygon are left out.
    Returns dict: nx, nz, res, tile_size (x, z) m, lo, hi (m, over all tiles),
                  tiles {(ix, iz): {"grid": [res, res] row 0 = south, "image": PNG or None}}"""
    max_tiles = max(1, int(max_tiles))
    west, south, east, north = polygon_box(poly)
    size_x, size_z = box_size(west, south, east, north)
    cells = tile_res - 1
    spacing = TARGET_SPACING
    while True:
        nx = max(1, math.ceil(size_x / (cells * spacing)))
        nz = max(1, math.ceil(size_z / (cells * spacing)))
        if nx * nz <= max_tiles:
            break
        spacing *= 1.1
    # Tiles are all the same size, so the grid can be a little bigger than the polygon's box
    tile_x, tile_z = cells * spacing, cells * spacing
    deg_x = (east - west) * tile_x / size_x
    deg_z = (north - south) * tile_z / size_z
    cx, cz = (west + east) / 2, (south + north) / 2
    w0, s0 = cx - nx * deg_x / 2, cz - nz * deg_z / 2
    # Keep the grid on the map (-180..180 E, -90..90 N)
    w0 = min(max(w0, -180.0), 180.0 - nx * deg_x)
    s0 = min(max(s0, -90.0), 90.0 - nz * deg_z)

    print(f"Polygon: {len(poly)} points, box {size_x / 1000:.1f} x {size_z / 1000:.1f} km, "
          f"lon {west:.4f}..{east:.4f}, lat {south:.4f}..{north:.4f}")
    print(f"Too big for one Unity terrain at {TARGET_SPACING:.0f} m: {nx} x {nz} tiles of "
          f"{tile_res}x{tile_res}, each {tile_x / 1000:.1f} km, {spacing:.0f} m between points"
          + ("" if spacing <= TARGET_SPACING + 0.5 else
             f" (raise --max-tiles above {max_tiles} for {TARGET_SPACING:.0f} m)"))

    # Which tiles touch the polygon (checked on a coarse grid)
    todo = []
    for iz in range(nz):
        for ix in range(nx):
            b = (w0 + ix * deg_x, s0 + iz * deg_z, w0 + (ix + 1) * deg_x, s0 + (iz + 1) * deg_z)
            if box_touches_polygon(poly, b):
                todo.append(((ix, iz), b))
    print(f"{len(todo)} of {nx * nz} tiles touch the polygon")

    tiles, t0, ctx_stats = {}, time.time(), {}
    if spacing < 200 and todo:
        # Catalogue searches take ~15 s each: do them all at once (cached for the tiles)
        from concurrent.futures import ThreadPoolExecutor
        print(f"Searching the CTX catalogue for {len(todo)} tiles at once...")
        rings = [[(b[0], b[1]), (b[2], b[1]), (b[2], b[3]), (b[0], b[3])] for _, b in todo]
        with ThreadPoolExecutor(min(6, len(rings))) as pool:
            for r in pool.map(lambda ring: _try(stac_search, ring), rings):
                pass
    from concurrent.futures import ThreadPoolExecutor, as_completed

    def one(ixz, b):
        _OUT.quiet = True  # several tiles at once: their step-by-step output would interleave
        t = time.time()
        r = build(poly, imagery=imagery, fetch_themis=fetch_themis,
                  box=b, res=tile_res, size=(tile_x, tile_z), ctx_stats=ctx_stats)
        return ixz, r, time.time() - t

    print(f"Building {len(todo)} tiles, {TILE_WORKERS} at a time "
          f"(finished tiles are cached, so an interrupted run picks up where it stopped)")
    with ThreadPoolExecutor(TILE_WORKERS) as pool:
        futures = [pool.submit(one, ixz, b) for ixz, b in todo]
        for n, f in enumerate(as_completed(futures), 1):
            (ix, iz), r, took = f.result()
            tiles[(ix, iz)] = {"grid": r["grid"].astype(np.float32), "image": r["image"]}
            how = ("cached" if r["cover"] is None else
                   f"{took:.0f}s, CTX 20 m on {r['cover']:.0f}% of it" if r["cover"] else f"{took:.0f}s")
            print(f"  tile {n}/{len(todo)} done (column {ix + 1}, row {iz + 1}): {how}  "
                  f"[{(time.time() - t0) / 60:.1f} min]")

    grids = {k: v["grid"] for k, v in tiles.items()}
    stitch(grids, nx, nz)
    lo = float(min(g.min() for g in grids.values()))
    hi = float(max(g.max() for g in grids.values()))
    print(f"\nAll {len(tiles)} tiles done in {(time.time() - t0) / 60:.1f} min, "
          f"{lo:.0f} m to {hi:.0f} m, edges stitched")
    # Centre of the tile grid = Unity's world origin. Usually the polygon box's centre, but
    # the grid is shifted to stay on the map near 180 E / the poles.
    return dict(nx=nx, nz=nz, res=tile_res, tile_size=(tile_x, tile_z), lo=lo, hi=hi,
                tiles=tiles, spacing=spacing,
                center_lon=w0 + nx * deg_x / 2, center_lat=s0 + nz * deg_z / 2)
