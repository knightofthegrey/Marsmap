"""
Martian Map - build a real terrain "site" for Unity from HiRISE data.

Writes Python/site/ (next to this script):

  terrain.csv   Latitude,Longitude,Elevation grid: 501 x 501 points, 4 m apart, over a
                2 km box. server.py turns it into the Unity heightmap, like a JMARS export.
  photo.jpg     HiRISE colour photo of exactly the same box (top = north, left = west)
  slope.jpg     the photo with amber (15-20 deg, caution) and red (20+ deg, no-go) slopes
  thermal.jpg   the photo with THEMIS thermal inertia (blue = finer/looser, red = rockier)
  site.json     name, sol, Earth date, centre, size, layer labels/credits, source URLs

The box is centred on Curiosity's real position on --sol (from NASA's MSL waypoint list),
so the terrain is where the rover was on the sol of the weather/radiation replay
(sol_samples.csv). Remote files are read in place: only the box is downloaded (a few tens of MB).

Usage (from the Marsmap folder):
  .venv/bin/pip install -r Python/requirements-maps.txt     # once
  .venv/bin/python Python/make_site.py                       # sol 4560, 2 km box, ~20 s
  .venv/bin/python Python/make_site.py --sol 4600            # where the rover was on another sol
  .venv/bin/python Python/make_site.py --center-lat -4.83 --center-lon 137.39 --size-m 1000 --cell-m 2
  python3 Python/server.py                                   # serves Python/site/ to Unity

Alignment (the contract with server.py and Unity; nothing is ever flipped):
  * Everything is computed in the terrain model's own map projection. The box is
    [cx - size/2, cx + size/2] x [cy - size/2, cy + size/2] around the centre.
  * terrain.csv points run from edge to edge: the first and last rows/columns lie exactly
    on the box edges, so the CSV's lat/lon extent is the box. Heights are sampled
    (bilinear) exactly at those points; the script checks this and stops if it is off.
  * The images' outer pixel edges are the box edges; row 0 = north, column 0 = west.
  * Slope is measured in real ground metres from the unexaggerated heights.
  * The Gale photo mosaic and the HiRISE terrain model are tied to different base maps and
    disagree by ~50 m here. The script matches the photo against the terrain model's own
    orthoimage and shifts the terrain onto the photo (the Gale base map, which the rover
    positions are plotted on), so ridges, slope colours and the rover line up.
    --no-align turns this off. The thermal layer (100 m pixels) is not shifted.
    The shift is a translation only: the photo is also rotated ~0.3 deg relative to the
    terrain model, so the match is within ~1 m at the rover (box centre) but up to ~10 m
    off at the corners of a 2 km box.
"""
import argparse
import json
import math
import ssl
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlencode, urlparse
from urllib.request import Request, urlopen

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))  # so prep_terrain imports from any working directory

try:
    import numpy as np
    import pyproj
    import rasterio
    from PIL import Image
    from rasterio.enums import Resampling
    from rasterio.fill import fillnodata
    from rasterio.transform import Affine
    from rasterio.warp import reproject, transform as warp_points, transform_bounds
    from rasterio.windows import Window, from_bounds
except ModuleNotFoundError as e:
    sys.exit(f"Missing library: {e.name}\n"
             "Install the map libraries (from the Marsmap folder):\n"
             "    .venv/bin/pip install -r Python/requirements-maps.txt")

from prep_terrain import CAUTION_DEG, NOGO_DEG, _transformers

# ---- Data sources ------------------------------------------------------------
WAYPOINTS_URL = "https://mars.nasa.gov/mmgis-maps/MSL/Layers/json/MSL_waypoints.json"
DTM_URL = ("https://astrogeo-ard.s3-us-west-2.amazonaws.com/mars/mro/hirise/controlled/dtm/"
           "ESP_019698_1750_ESP_019988_1750/DTEEC_019698_1750_019988_1750_U01.tif")
PHOTO_URL = ("https://asc-pds-services.s3.us-west-2.amazonaws.com/mosaic/Mars/MSL/"
             "MSL_Gale_HiRISE-LRGB_78quads_sharp_cog.tif")
THERMAL_URL = ("https://asc-astropedia.s3.us-west-2.amazonaws.com/Mars/Odyssey/"
               "THEMIS-Global-Thermal-Inertia-Mosaic/Quantitative-32-Bit/"
               "THEMIS_TI_Mosaic_Quant_30S120E_100mpp.cub")
STAC_ITEMS_URL = "https://stac.astrogeology.usgs.gov/api/collections/mro_hirise_socet_dtms/items"
HEADERS = {"User-Agent": "marsmap-make-site/1.0"}
GDAL_ENV = {
    "GDAL_DISABLE_READDIR_ON_OPEN": "EMPTY_DIR",   # don't list S3 folders on open
    "GDAL_HTTP_MAX_RETRY": "4", "GDAL_HTTP_RETRY_DELAY": "2",
    "GDAL_HTTP_MULTIRANGE": "YES", "GDAL_HTTP_MERGE_CONSECUTIVE_RANGES": "YES",
    "VSI_CACHE": "TRUE",
}

# ---- Look ------------------------------------------------------------------------
JPEG_QUALITY = 88
CAUTION_RGB, CAUTION_ALPHA = (255, 170, 0), 0.45     # amber
NOGO_RGB, NOGO_ALPHA = (230, 30, 30), 0.55           # red
THERMAL_ALPHA = 0.5
# Thermal inertia colour ramp, low -> high: blue, cyan, pale yellow, orange, red.
TI_RAMP = [(0.00, (20, 110, 235)), (0.30, (0, 190, 225)), (0.55, (235, 225, 120)),
           (0.80, (245, 140, 30)), (1.00, (210, 35, 25))]

# ---- Curiosity sol -> Earth date (used when sol_samples.meta.json is for another sol)
MSL_LANDING_UTC = datetime(2012, 8, 6, 5, 17, 57, tzinfo=timezone.utc)
SOL_S = 88775.244          # one Mars solar day in SI seconds
LANDING_LMST_H = 15.0      # Curiosity landed about 3 pm local time, on sol 0


def _ssl_context():
    """python.org builds of Python ship without CA certificates; certifi comes with rasterio."""
    try:
        import certifi
        return ssl.create_default_context(cafile=certifi.where())
    except ImportError:
        return ssl.create_default_context()


SSL_CONTEXT = _ssl_context()


def get_json(url, timeout=90):
    with urlopen(Request(url, headers=HEADERS), timeout=timeout, context=SSL_CONTEXT) as r:
        return json.load(r)


def file_label(url):
    return Path(urlparse(url).path).name or url


# ---- Where and when ------------------------------------------------------------

def rover_position(sol):
    """Curiosity's lat / east lon at the end of a sol, from NASA's MSL waypoint list."""
    feats = get_json(WAYPOINTS_URL)["features"]
    pts = []
    for f in feats:
        p = f.get("properties") or {}
        try:
            pts.append((int(p["sol"]), float(p["lat"]), float(p["lon"])))
        except (KeyError, TypeError, ValueError):
            continue
    if not pts:
        raise SystemExit(f"No waypoints found in {WAYPOINTS_URL}")
    same = [p for p in pts if p[0] == sol]
    if same:
        s, lat, lon = same[-1]
        note = (f"{len(same)} waypoints on sol {sol}, using the last" if len(same) > 1
                else f"waypoint for sol {sol}")
    else:
        before = sorted((p for p in pts if p[0] < sol), key=lambda p: p[0])  # stable: keeps file order
        if not before:
            raise SystemExit(f"Sol {sol} is before the first waypoint (sol {pts[0][0]}).")
        s, lat, lon = before[-1]
        note = f"no waypoint on sol {sol}; using the last one before it (sol {s})"
    return lat, lon % 360, note


def earth_date(sol):
    """UTC date on which the sol starts (the date NASA lists for a Curiosity sol)."""
    try:
        meta = json.loads((HERE / "sol_samples.meta.json").read_text())
        if int(meta.get("sol", -1)) == sol and meta.get("earth_date_utc"):
            return meta["earth_date_utc"], "sol_samples.meta.json"
    except (OSError, ValueError):
        pass
    start = MSL_LANDING_UTC + timedelta(seconds=(sol - LANDING_LMST_H / 24) * SOL_S)
    return start.date().isoformat(), "computed from the landing time"


# ---- Raster helpers ----------------------------------------------------------------

def to_map_xy(src, lat, lon):
    """Projected x, y of lat / east lon, trying both 0..360 and -180..180 longitudes."""
    to_map, _ = _transformers(src)
    b = src.bounds
    best = None
    for lo in {lon % 360, ((lon + 180) % 360) - 180}:
        x, y = to_map.transform(lo, lat)
        d = max(b.left - x, x - b.right, b.bottom - y, y - b.top, 0)  # 0 = inside the file
        if best is None or d < best[0]:
            best = (d, x, y)
    return best[1], best[2]


def box_around(src, lat, lon, size):
    cx, cy = to_map_xy(src, lat, lon)
    h = size / 2
    return (cx - h, cy - h, cx + h, cy + h)


def aligned_window(src, bounds, margin, multiple=1, clip=True):
    """Whole-pixel window covering bounds + margin, with offsets/sizes divisible by `multiple`
    (so a 1/multiple overview read lines up exactly). Returns (window, clipped?).
    With clip=False the window may reach past the file (read it with boundless=True)."""
    w = from_bounds(bounds[0] - margin, bounds[1] - margin, bounds[2] + margin,
                    bounds[3] + margin, transform=src.transform)
    c0 = math.floor(w.col_off / multiple) * multiple
    r0 = math.floor(w.row_off / multiple) * multiple
    c1 = math.ceil((w.col_off + w.width) / multiple) * multiple
    r1 = math.ceil((w.row_off + w.height) / multiple) * multiple
    clipped = c0 < 0 or r0 < 0 or c1 > src.width or r1 > src.height
    if not clip:
        return Window(c0, r0, c1 - c0, r1 - r0), clipped
    c0, r0 = max(c0, 0), max(r0, 0)
    c1 = min(c1, src.width // multiple * multiple)
    r1 = min(r1, src.height // multiple * multiple)
    if c1 <= c0 or r1 <= r0:
        return None, True
    return Window(c0, r0, c1 - c0, r1 - r0), clipped


def warp(arr, src_tf, src_crs, dst_tf, dst_shape, dst_crs, nodata=np.nan, dtype="float32"):
    """Bilinear resample arr onto a destination grid (pixel centres are the sample points).
    When shrinking, GDAL widens the bilinear kernel, so fine detail is averaged, not aliased."""
    shape = (arr.shape[0], *dst_shape) if arr.ndim == 3 else tuple(dst_shape)
    dst = np.full(shape, 0 if nodata is None else nodata, dtype=dtype)
    reproject(arr, dst, src_transform=src_tf, src_crs=src_crs, src_nodata=nodata,
              dst_transform=dst_tf, dst_crs=dst_crs, dst_nodata=nodata,
              resampling=Resampling.bilinear, num_threads=4)
    return dst


def check_sampling(src_shape, src_tf, src_crs, dst_tf, dst_shape, dst_crs, what, tol=0.05):
    """Prove the warp samples exactly at the destination pixel centres: warp two ramps that hold
    each source pixel's own map x / y and compare with where each destination pixel should be.
    A linear ramp survives bilinear (and widened bilinear) filtering unchanged, so any offset
    is a real misalignment (wrong half-pixel, edge truncation, projection mix-up)."""
    h, w = src_shape
    ox, oy = src_tf.c, src_tf.f                      # local origin keeps float32 precise
    xs = (np.arange(w) + 0.5) * src_tf.a             # pixel-centre x minus ox (north-up rasters)
    ys = (np.arange(h) + 0.5) * src_tf.e
    rx = warp(np.broadcast_to(xs[None, :], (h, w)).astype("float32"), src_tf, src_crs,
              dst_tf, dst_shape, dst_crs)
    ry = warp(np.broadcast_to(ys[:, None], (h, w)).astype("float32"), src_tf, src_crs,
              dst_tf, dst_shape, dst_crs)
    step = max(1, dst_shape[0] // 256)
    ii, jj = np.mgrid[0:dst_shape[0]:step, 0:dst_shape[1]:step]
    px = dst_tf.c + (jj + 0.5) * dst_tf.a
    py = dst_tf.f + (ii + 0.5) * dst_tf.e
    ex, ey = warp_points(dst_crs, src_crs, px.ravel(), py.ravel())
    ex = np.asarray(ex).reshape(px.shape) - ox
    ey = np.asarray(ey).reshape(py.shape) - oy
    err = np.hypot(rx[ii, jj] - ex, ry[ii, jj] - ey)
    worst = float(np.nanmax(err)) if np.isfinite(err).any() else float("inf")
    if not np.isfinite(err).all() or worst > tol:
        raise SystemExit(f"Alignment check failed for {what}: samples are off by up to "
                         f"{worst:.3f} m (or fall outside the data). Not writing a bad site.")
    print(f"    alignment check ({what}): samples within {worst * 1000:.1f} mm of target points")
    return worst


def valid_fraction_fast(url, lat, lon, size):
    """Rough share of the box a DTM covers (reads a small overview)."""
    try:
        with rasterio.open(url) as src:
            box = box_around(src, lat, lon, size)
            want = from_bounds(*box, transform=src.transform)
            win, _ = aligned_window(src, box, 0)
            if win is None:
                return 0.0
            f = 8
            arr = src.read(1, window=win, masked=True,
                           out_shape=(max(1, win.height // f), max(1, win.width // f)))
            inside = (win.width * win.height) / max(want.width * want.height, 1)
            return float(arr.count()) / arr.size * min(inside, 1.0)
    except rasterio.errors.RasterioIOError as e:
        print(f"    could not open {file_label(url)}: {e}")
        return 0.0


def stac_items(lat, lon, size):
    """HiRISE terrain-model items in the USGS STAC catalogue whose footprint touches the box."""
    dlat = size / 2 / 59290.0                                   # ~59.29 km per degree
    dlon = dlat / max(math.cos(math.radians(lat)), 0.05)
    for lo in dict.fromkeys([((lon + 180) % 360) - 180, lon % 360]):
        q = urlencode({"bbox": f"{lo - dlon},{lat - dlat},{lo + dlon},{lat + dlat}", "limit": 50})
        try:
            items = get_json(f"{STAC_ITEMS_URL}?{q}").get("features", [])
        except (OSError, ValueError) as e:
            print(f"    STAC search failed: {e}")
            continue
        if items:
            return items
    return []


def stac_dtms(lat, lon, size):
    hrefs = [(f.get("assets", {}).get("dtm") or {}).get("href") for f in stac_items(lat, lon, size)]
    return list(dict.fromkeys(h for h in hrefs if h))


def dtm_orthos(dtm_url, lat, lon, size):
    """Panchromatic (RED) orthoimages made from the same stereo pair as the DTM, so they sit
    exactly in the DTM's frame. 1 m versions first (smaller reads)."""
    stem = Path(file_label(dtm_url)).stem
    for item in stac_items(lat, lon, size):
        assets = item.get("assets", {})
        if (assets.get("dtm") or {}).get("href") != dtm_url and item.get("id") != stem:
            continue
        hrefs = [a.get("href", "") for k, a in assets.items()
                 if "ortho" in k.lower() and not any(w in k.lower() for w in ("label", "thumb"))]
        hrefs = [h for h in hrefs if "_RED_" in h and h.lower().endswith(".tif")]
        return sorted(hrefs, key=lambda h: "_RED_C_" not in h)
    return []


def read_gray(url, box, crs, grid_tf, n, band=1):
    """One band of an image, resampled onto an n x n grid (for co-registration)."""
    with rasterio.open(url) as src:
        native = abs(src.res[0])
        f = 2 ** max(0, round(math.log2(max(grid_tf.a / 2 / native, 1))))
        sb = transform_bounds(crs, src.crs, *box, densify_pts=21)
        win, _ = aligned_window(src, sb, 10 * native * f, f)
        if win is None:
            return None
        arr = src.read(band, window=win, masked=True, resampling=Resampling.average,
                       out_shape=(win.height // f, win.width // f))
        arr = arr.astype("float32").filled(np.nan)
        arr[arr <= 0] = np.nan                                  # 0 = no data in these images
        return warp(arr, src.window_transform(win) * Affine.scale(f), src.crs, grid_tf, (n, n), crs)


def image_shift(a, b, cell):
    """Where b's features appear in a: (east_m, north_m, peak correlation). Cross-correlates
    high-passed, windowed copies (lighting/colour differences mostly cancel out)."""
    def prep(x):
        x = np.where(np.isfinite(x), x, np.nanmean(x)).astype("float64")
        fy, fx = np.fft.fftfreq(x.shape[0])[:, None], np.fft.fftfreq(x.shape[1])[None, :]
        low = np.real(np.fft.ifft2(np.fft.fft2(x) * np.exp(-2 * (np.pi * 8) ** 2 * (fx ** 2 + fy ** 2))))
        return (x - low) * np.outer(np.hanning(x.shape[0]), np.hanning(x.shape[1]))
    A, B = prep(a), prep(b)
    xc = np.real(np.fft.ifft2(np.fft.fft2(A) * np.conj(np.fft.fft2(B))))
    xc /= np.linalg.norm(A) * np.linalg.norm(B)
    k = np.unravel_index(np.argmax(xc), xc.shape)

    def peak(axis):                                             # sub-pixel parabola fit
        n = xc.shape[axis]
        lo, hi = list(k), list(k)
        lo[axis], hi[axis] = (k[axis] - 1) % n, (k[axis] + 1) % n
        ym, y0, yp = xc[tuple(lo)], xc[k], xc[tuple(hi)]
        d = 0.5 * (ym - yp) / (ym - 2 * y0 + yp) if (ym - 2 * y0 + yp) else 0.0
        v = k[axis] + d
        return v if v < n / 2 else v - n
    return peak(1) * cell, -peak(0) * cell, float(xc[k])      # rows run south


def measure_dtm_shift(dtm_url, photo_url, lat, lon, box, crs, size):
    """The photo mosaic and the DTM are tied to different base maps and can disagree by tens of
    metres. Compare the photo with the DTM's own orthoimage and return the (east, north) shift
    that moves DTM features onto the photo's (the rover positions use the photo's frame)."""
    cell = 2.0
    n = int(round(size / cell))
    grid_tf = Affine(cell, 0, box[0], 0, -cell, box[3])
    orthos = dtm_orthos(dtm_url, lat, lon, size)
    if not orthos:
        print("    no orthoimage for this terrain model in STAC; not aligning (use with care)")
        return 0.0, 0.0, None
    photo = read_gray(photo_url, box, crs, grid_tf, n)
    for href in orthos[:3]:
        ortho = read_gray(href, box, crs, grid_tf, n)
        if ortho is None or np.isfinite(ortho).mean() < 0.8:
            continue
        de, dn, corr = image_shift(photo, ortho, cell)
        print(f"    photo vs {file_label(href)}: features match {math.hypot(de, dn):.1f} m apart "
              f"(east {de:+.1f} m, north {dn:+.1f} m, correlation {corr:.2f})")
        if corr < 0.25 or math.hypot(de, dn) > size / 4:
            print("    WARNING: match too weak to trust; not aligning")
            return 0.0, 0.0, None
        return de, dn, {"east_m": round(de, 2), "north_m": round(dn, 2), "correlation": round(corr, 3),
                        "matched_against": href}
    print("    WARNING: no orthoimage covers the box; not aligning")
    return 0.0, 0.0, None


def choose_dtm(url, lat, lon, size):
    """Use url if it fully covers the box, else the STAC terrain model covering most of it."""
    cov = valid_fraction_fast(url, lat, lon, size)
    print(f"    {file_label(url)}: covers {cov:.1%} of the box")
    if cov >= 0.999:
        return url
    print("    Not fully covered; searching USGS STAC for HiRISE terrain models here...")
    best = (cov, url)
    for href in stac_dtms(lat, lon, size)[:10]:
        if href == url:
            continue
        c = valid_fraction_fast(href, lat, lon, size)
        print(f"    {file_label(href)}: covers {c:.1%}")
        if c > best[0]:
            best = (c, href)
    if best[0] < 0.99:
        raise SystemExit(f"No HiRISE terrain model covers this {size:.0f} m box (best: {best[0]:.1%}). "
                         "Try a smaller --size-m or another centre.")
    return best[1]


def ground_scale(crs, to_geo, x, y):
    """Ground metres per projected metre (east-west, north-south) at x, y. In Mars
    equirectangular maps with standard parallel 0, east-west map metres are stretched by
    1/cos(lat), so slope must be measured in ground metres, not map metres."""
    geod = pyproj.CRS(crs.to_wkt()).get_geod()
    lo0, la0 = to_geo.transform(x - 50, y)
    lo1, la1 = to_geo.transform(x + 50, y)
    kx = geod.inv(lo0, la0, lo1, la1)[2] / 100
    lo0, la0 = to_geo.transform(x, y - 50)
    lo1, la1 = to_geo.transform(x, y + 50)
    ky = geod.inv(lo0, la0, lo1, la1)[2] / 100
    return kx, ky


# ---- Images ----------------------------------------------------------------------------

def stretch_photo(rgb):
    """1-99 % stretch with ONE range for all bands, so colours stay natural."""
    valid = rgb.max(axis=0) > 0
    v = rgb.astype("float32")
    if not valid.any():
        return np.zeros((*rgb.shape[1:], 3), np.float32)
    lo, hi = np.percentile(v[:, valid], [1, 99])
    out = np.clip((v - lo) / max(hi - lo, 1.0), 0, 1) * 255
    return np.moveaxis(out, 0, -1)                     # H x W x 3


def ti_colours(t):
    """0..1 -> RGB along TI_RAMP."""
    stops = np.array([s for s, _ in TI_RAMP])
    cols = np.array([c for _, c in TI_RAMP], dtype="float32")
    t = np.clip(t, 0, 1)
    return np.stack([np.interp(t, stops, cols[:, k]) for k in range(3)], axis=-1)


def blend(base, rgb, alpha):
    a = alpha[..., None].astype("float32")
    return base * (1 - a) + np.asarray(rgb, dtype="float32") * a


def save_jpeg(rgb, path):
    Image.fromarray(np.clip(np.rint(rgb), 0, 255).astype(np.uint8)).save(
        path, "JPEG", quality=JPEG_QUALITY, optimize=True)


def read_photo(url, box, crs, tex_tf, tex_px):
    with rasterio.open(url) as src:
        native = abs(src.res[0])
        want = (box[2] - box[0]) / tex_px / 2          # read at ~half a texture pixel
        f = 2 ** max(0, round(math.log2(max(want / native, 1))))
        sb = transform_bounds(crs, src.crs, *box, densify_pts=21)
        win, clipped = aligned_window(src, sb, 4 * native * f, f)
        if win is None:
            raise SystemExit(f"The photo {file_label(url)} does not cover this box.")
        nb = min(src.count, 3)
        arr = src.read(list(range(1, nb + 1)), window=win, resampling=Resampling.average,
                       out_shape=(nb, win.height // f, win.width // f))
        tf = src.window_transform(win) * Affine.scale(f)
        scrs = src.crs
    if nb < 3:
        arr = np.repeat(arr[:1], 3, axis=0)
    print(f"    read {arr.shape[2]} x {arr.shape[1]} px at {native * f:.2f} m"
          + ("  (box reaches the photo's edge)" if clipped else ""))
    check_sampling(arr.shape[1:], tf, scrs, tex_tf, (tex_px, tex_px), crs, "photo -> texture")
    rgb = warp(arr, tf, scrs, tex_tf, (tex_px, tex_px), crs, nodata=None, dtype="uint8")
    valid = float((rgb.max(axis=0) > 0).mean())
    if valid < 0.99:
        print(f"    WARNING: the photo covers only {valid:.1%} of the box (black elsewhere).")
    return stretch_photo(rgb), valid


def read_thermal(url, box, crs, tex_tf, tex_px):
    with rasterio.open(url) as src:
        native = abs(src.res[0])
        sb = transform_bounds(crs, src.crs, *box, densify_pts=21)
        win, _ = aligned_window(src, sb, 3 * native)
        if win is None:
            return None, None, native
        arr = src.read(1, window=win, masked=True).astype("float64").filled(np.nan)
        arr = arr * src.scales[0] + src.offsets[0]
        tf, scrs = src.window_transform(win), src.crs
    arr[~(np.isfinite(arr) & (arr > 0) & (arr < 1e5))] = np.nan   # ISIS special pixels
    rows, cols = np.mgrid[0:arr.shape[0], 0:arr.shape[1]]
    cx, cy = tf.c + (cols + 0.5) * tf.a, tf.f + (rows + 0.5) * tf.e
    inside = (cx >= sb[0]) & (cx <= sb[2]) & (cy >= sb[1]) & (cy <= sb[3])
    native_vals = arr[inside & np.isfinite(arr)]
    if native_vals.size == 0:
        return None, None, native
    check_sampling(arr.shape, tf, scrs, tex_tf, (tex_px, tex_px), crs, "thermal -> texture")
    ti = warp(arr.astype("float32"), tf, scrs, tex_tf, (tex_px, tex_px), crs)
    return ti, native_vals, native


# ---- Main ------------------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--sol", type=int, default=4560, help="Curiosity sol: centre on the rover (default 4560)")
    ap.add_argument("--center-lat", type=float, help="override the centre (degrees, planetocentric)")
    ap.add_argument("--center-lon", type=float, help="override the centre (degrees EAST)")
    ap.add_argument("--name", help="site name shown in Unity (default: from the sol)")
    ap.add_argument("--size-m", type=float, default=2000.0, help="box size in metres (default 2000)")
    ap.add_argument("--cell-m", type=float, default=4.0, help="terrain.csv point spacing (default 4)")
    ap.add_argument("--tex-px", type=int, default=2048, help="image size in pixels (default 2048)")
    ap.add_argument("--slope-m", type=float, default=2.0, help="slope measured over this spacing (default 2)")
    ap.add_argument("--dtm", default=DTM_URL, help="HiRISE terrain model (GeoTIFF URL or path)")
    ap.add_argument("--photo", default=PHOTO_URL, help="colour photo (GeoTIFF URL or path)")
    ap.add_argument("--thermal", default=THERMAL_URL, help="THEMIS thermal inertia (URL or path)")
    ap.add_argument("--out", default=str(HERE / "site"), help="output folder (default Python/site)")
    ap.add_argument("--no-align", action="store_true",
                    help="don't shift the terrain model to line up with the photo")
    a = ap.parse_args()

    if (a.center_lat is None) != (a.center_lon is None):
        ap.error("give both --center-lat and --center-lon, or neither")
    if a.size_m <= 0 or a.cell_m <= 0 or a.slope_m <= 0:
        ap.error("--size-m, --cell-m and --slope-m must be positive")
    n = int(round(a.size_m / a.cell_m)) + 1
    if not 3 <= n <= 2049:
        ap.error(f"--size-m / --cell-m gives {n} points per side; keep it between 3 and 2049")
    if not 256 <= a.tex_px <= 8192:
        ap.error("--tex-px must be between 256 and 8192")
    cell = a.size_m / (n - 1)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    t0 = time.time()

    # 1) Where -------------------------------------------------------------------------
    date, date_src = earth_date(a.sol)
    sources = []
    if a.center_lat is None:
        print(f"1/6 Curiosity's position on sol {a.sol} ({date})...")
        lat, lon, note = rover_position(a.sol)
        print(f"    {lat:.6f}, {lon:.6f} E  ({note})")
        sources.append({"what": f"Curiosity position, sol {a.sol} (NASA/JPL MSL waypoints)",
                        "url": WAYPOINTS_URL})
        name = a.name or (f"Curiosity at Mount Sharp, sol {a.sol}" if a.sol >= 750
                          else f"Curiosity in Gale Crater, sol {a.sol}")
    else:
        lat, lon = a.center_lat, a.center_lon % 360
        print(f"1/6 Centre given: {lat:.6f}, {lon:.6f} E")
        name = a.name or f"Gale site {lat:.4f}, {lon:.4f} E"

    with rasterio.Env(**GDAL_ENV):
        # 2) Terrain model -> terrain.csv -------------------------------------------------
        print("2/6 HiRISE terrain model...")
        dtm_url = choose_dtm(a.dtm, lat, lon, a.size_m)
        with rasterio.open(dtm_url) as src:
            crs = src.crs
            box = box_around(src, lat, lon, a.size_m)
            left, bottom, right, top = box
        if a.no_align:
            de, dn, shift_info = 0.0, 0.0, None
        else:
            de, dn, shift_info = measure_dtm_shift(dtm_url, a.photo, lat, lon, box, crs, a.size_m)
        with rasterio.open(dtm_url) as src:
            native = abs(src.res[0])
            margin = 3 * max(cell, a.slope_m) + 3 * native   # room for the resampling kernels
            # The DTM data for our box sits at box - shift in the DTM's own coordinates.
            win, clipped = aligned_window(src, (left - de, bottom - dn, right - de, top - dn),
                                          margin, clip=False)
            dem = src.read(1, window=win, masked=True, boundless=clipped)
            dem = dem.astype("float64").filled(np.nan) * src.scales[0] + src.offsets[0]
            dem_tf = Affine.translation(de, dn) * src.window_transform(win)   # into the photo's frame
            _, to_geo = _transformers(src)
        print(f"    {file_label(dtm_url)}: read {dem.shape[1]} x {dem.shape[0]} px at {native:.2f} m"
              + (f", shifted {de:+.1f} m east, {dn:+.1f} m north to match the photo" if shift_info else ""))
        # Share of DTM pixels inside the box (not the margin) that have no height.
        cx = dem_tf.c + (np.arange(dem.shape[1]) + 0.5) * dem_tf.a
        cy = dem_tf.f + (np.arange(dem.shape[0]) + 0.5) * dem_tf.e
        core = dem[np.ix_((cy >= bottom) & (cy <= top), (cx >= left) & (cx <= right))]
        missing = float(np.isnan(core).mean())
        if missing > 0.01:
            raise SystemExit(f"{missing:.1%} of the box has no elevation data (limit 1%). "
                             "Pick another centre / smaller --size-m, or a --dtm that covers it.")
        dem = dem.astype("float32")
        if np.isnan(dem).any():
            print(f"    filling {np.isnan(dem).mean():.2%} missing pixels (box + margin) from neighbours")
            dem = fillnodata(dem, mask=(~np.isnan(dem)).astype("uint8"), max_search_distance=400)
            dem[np.isnan(dem)] = np.nanmean(dem)

        grid_tf = Affine(cell, 0, left - cell / 2, 0, -cell, top + cell / 2)  # centres on the points
        check_sampling(dem.shape, dem_tf, crs, grid_tf, (n, n), crs, "terrain.csv points")
        z = warp(dem, dem_tf, crs, grid_tf, (n, n), crs)                       # row 0 = north
        if np.isnan(z).any():
            raise SystemExit("Elevation grid has gaps after resampling; widen the margin.")
        xs = np.linspace(left, right, n)                 # first/last exactly on the box edges
        ys = np.linspace(top, bottom, n)
        X, Y = np.meshgrid(xs, ys)
        lons, lats = to_geo.transform(X.ravel(), Y.ravel())
        lons = np.mod(np.asarray(lons), 360.0)
        lats = np.asarray(lats)
        np.savetxt(out / "terrain.csv", np.column_stack([lats, lons, z.ravel()]),
                   fmt=["%.8f", "%.8f", "%.2f"], delimiter=",",
                   header="Latitude,Longitude,Elevation", comments="")
        print(f"    terrain.csv: {n} x {n} points, {cell:g} m apart, "
              f"{z.min():.1f} to {z.max():.1f} m")

        # Texture grid: outer pixel edges = box edges, row 0 = north, column 0 = west.
        tpx = a.tex_px
        tex_tf = Affine(a.size_m / tpx, 0, left, 0, -a.size_m / tpx, top)

        # 3) Slope (real ground metres, unexaggerated) ----------------------------------------
        print(f"3/6 Slope at {a.slope_m:g} m...")
        kx, ky = ground_scale(crs, to_geo, (left + right) / 2, (bottom + top) / 2)
        s = a.slope_m
        ns = int(math.ceil(a.size_m / s)) + 4
        slope_tf = Affine(s, 0, left - 2 * s, 0, -s, top + 2 * s)
        zs = warp(dem, dem_tf, crs, slope_tf, (ns, ns), crs).astype("float64")
        dz_drow, dz_dcol = np.gradient(zs, s * ky, s * kx)
        slope = np.degrees(np.arctan(np.hypot(dz_dcol, dz_drow))).astype("float32")
        check_sampling(slope.shape, slope_tf, crs, tex_tf, (tpx, tpx), crs, "slope -> texture")
        slope_tex = warp(slope, slope_tf, crs, tex_tf, (tpx, tpx), crs)
        nogo = ~(slope_tex < NOGO_DEG)                    # NaN counts as no-go
        caution = (slope_tex >= CAUTION_DEG) & ~nogo
        print(f"    ground metres per map metre: east-west {kx:.4f}, north-south {ky:.4f}")

        # 4) Photo -------------------------------------------------------------------------
        print("4/6 Colour photo...")
        photo, photo_valid = read_photo(a.photo, box, crs, tex_tf, tpx)

        # 5) Thermal inertia ---------------------------------------------------------------
        print("5/6 THEMIS thermal inertia...")
        ti, ti_native, ti_res = read_thermal(a.thermal, box, crs, tex_tf, tpx)

    # 6) Images + site.json --------------------------------------------------------------------
    print("6/6 Writing images...")
    save_jpeg(photo, out / "photo.jpg")
    hazard = blend(photo, CAUTION_RGB, caution * CAUTION_ALPHA)    # ok ground stays untouched
    save_jpeg(blend(hazard, NOGO_RGB, nogo * NOGO_ALPHA), out / "slope.jpg")

    photo_credit = ("HiRISE colour mosaic of Gale (NASA/JPL/UArizona, USGS), 0.25 m"
                    if a.photo == PHOTO_URL else f"Photo: {file_label(a.photo)}")
    layers = [
        {"name": "photo", "file": "photo.jpg", "label": "Photo", "credit": photo_credit},
        {"name": "slope", "file": "slope.jpg", "label": "Slope hazard",
         "credit": (f"Slope from HiRISE terrain at {a.slope_m:g} m: amber {CAUTION_DEG:g}-{NOGO_DEG:g} deg "
                    f"caution, red {NOGO_DEG:g}+ deg no-go (limits borrowed from Artemis lunar EVA planning)")},
    ]
    ti_summary = None
    if ti is not None:
        lo, hi = np.nanpercentile(ti, [2, 98])
        lo, hi = math.floor(lo / 10) * 10, math.ceil(hi / 10) * 10
        if hi - lo < 50:
            mid = (hi + lo) / 2
            lo, hi = mid - 25, mid + 25
        t = (ti - lo) / (hi - lo)
        ok = np.isfinite(ti)
        save_jpeg(blend(photo, ti_colours(np.nan_to_num(t)), np.where(ok, THERMAL_ALPHA, 0.0)),
                  out / "thermal.jpg")
        layers.append({"name": "thermal", "file": "thermal.jpg", "label": "Thermal inertia",
                       "credit": (f"THEMIS thermal inertia, {ti_res:g} m pixels: blue {lo:g} = finer/looser "
                                  f"ground, red {hi:g} = rockier (J m-2 K-1 s-1/2)")})
        ti_summary = {"native_min": round(float(ti_native.min())), "native_mean": round(float(ti_native.mean())),
                      "native_max": round(float(ti_native.max())), "colour_scale": [lo, hi],
                      "pixels_in_box": int(ti_native.size)}
    else:
        print(f"    WARNING: {file_label(a.thermal)} has no data in this box; thermal layer skipped.")
        (out / "thermal.jpg").unlink(missing_ok=True)

    sources += [
        {"what": f"Elevation: HiRISE terrain model {Path(file_label(dtm_url)).stem} "
                 f"({native:.2f} m, NASA/JPL/UArizona, USGS)", "url": dtm_url},
        {"what": "Photo: " + photo_credit, "url": a.photo},
    ]
    if ti is not None:
        sources.append({"what": f"Thermal inertia: Mars Odyssey THEMIS mosaic ({ti_res:g} m, ASU, USGS)",
                        "url": a.thermal})
    lon_c, lat_c = to_geo.transform((left + right) / 2, (bottom + top) / 2)   # always_xy: lon first
    site = {
        "name": name, "sol": a.sol, "earth_date": date,
        "center_lat": round(float(lat_c), 8), "center_lon": round(float(lon_c) % 360, 8),
        "size_m": a.size_m, "cell_m": round(cell, 6), "vertical_exaggeration": 1.0,
        "layers": layers, "sources": sources,
        # Extra details for people / other tools (not sent to Unity):
        "grid_points": n, "tex_px": tpx, "crs": crs.to_proj4(),
        "bounds_map_m": [round(v, 3) for v in box],
        "lat_range": [round(float(lats.min()), 8), round(float(lats.max()), 8)],
        "lon_range": [round(float(lons.min()), 8), round(float(lons.max()), 8)],
        "elev_range_m": [round(float(z.min()), 2), round(float(z.max()), 2)],
        "dtm_shift_to_photo": shift_info,
    }
    (out / "site.json").write_text(json.dumps(site, indent=2) + "\n")

    # Summary ------------------------------------------------------------------------------------
    sizes = {p.name: f"{p.stat().st_size / 1e6:.2f} MB"
             for p in sorted(out.iterdir()) if p.suffix in (".csv", ".jpg", ".json")}
    summary = {
        "site": name, "sol": a.sol, "earth_date": f"{date} ({date_src})",
        "centre": [round(float(lat_c), 6), round(float(lon_c) % 360, 6)],
        "box_map_m": [round(v, 1) for v in box],
        "box_ground_m": [round(a.size_m * kx, 1), round(a.size_m * ky, 1)],
        "lat_range": site["lat_range"], "lon_range": site["lon_range"],
        "terrain": {"grid": f"{n} x {n}", "cell_m": round(cell, 3),
                    "elev_min_m": round(float(z.min()), 1), "elev_max_m": round(float(z.max()), 1),
                    "relief_m": round(float(z.max() - z.min()), 1),
                    "dtm": file_label(dtm_url), "dtm_missing": f"{missing:.2%}",
                    "shifted_to_photo_m": [round(de, 1), round(dn, 1)] if shift_info else None},
        "slope": {"max_deg": round(float(np.nanmax(slope_tex)), 1),
                  "caution_15_20": f"{caution.mean():.1%}", "no_go_20plus": f"{nogo.mean():.1%}"},
        "photo_valid": f"{photo_valid:.1%}",
        "thermal_inertia": ti_summary,
        "files": sizes, "out": str(out), "seconds": round(time.time() - t0),
    }
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
