"""
Martian Map - terrain prep.

Turns one HiRISE terrain model (DTM) + its matching photo (ortho) into files
Unity can load:

  out/heightmap.raw   16-bit little-endian RAW heightmap (N x N) for Unity Terrain
  out/ortho.png       photo cropped to the same box, to drape on the terrain
  out/slope.png       slope preview (white = steep), for checking / slides
  out/cost.bin        float32 route-cost grid (G x G) for the A* pathfinder
  out/cost.png        cost preview (green ok, amber caution, red no-go, black no data)
  out/meta.json       sizes, elevation range and thresholds Unity needs

Inputs can be local files or https URLs (rasterio reads cloud-optimized
GeoTIFFs remotely and only downloads the box you ask for).

Usage:
  pip install rasterio numpy pillow pyproj

  # 1) Check a file first (projection, size, lat/lon coverage). No GDAL install needed.
  python prep_terrain.py --info --dtm <DTM url or path>

  # 2) Make the Unity files. Pick the 2 km box by its centre in lat/lon...
  python prep_terrain.py --dtm <DTM> --ortho <ORTHO> --center-lat -4.83 --center-lon 137.30
  #    ...or by map coordinates in the DTM's own units (metres):
  #       --bounds LEFT BOTTOM RIGHT TOP
  #    ...or give neither and it uses a box centred on the DTM.
  #    --size-m sets the box size (default 2000).
"""
import argparse
import json
import os

import numpy as np
import rasterio
from rasterio.enums import Resampling
from rasterio.windows import from_bounds
from PIL import Image

# Slope thresholds (degrees). Borrowed from Artemis lunar EVA planning, not a
# Mars-specific NASA limit - say so in the pitch.
CAUTION_DEG = 15.0
NOGO_DEG = 20.0
LOADED_NOGO_DEG = 15.0  # "carrying gear" toggle: tighter limit, applied in Unity


def _transformers(src):
    """Map coords <-> lat/lon on the file's own body (Mars), never Earth."""
    import pyproj
    proj = pyproj.CRS(src.crs.to_wkt())
    geo = proj.geodetic_crs
    to_map = pyproj.Transformer.from_crs(geo, proj, always_xy=True)
    to_geo = pyproj.Transformer.from_crs(proj, geo, always_xy=True)
    return to_map, to_geo


def print_info(path):
    with rasterio.open(path) as src:
        b = src.bounds
        _, to_geo = _transformers(src)
        lon0, lat0 = to_geo.transform(b.left, b.bottom)
        lon1, lat1 = to_geo.transform(b.right, b.top)
        info = {
            "size_px": [src.width, src.height],
            "pixel_m": [abs(src.res[0]), abs(src.res[1])],
            "bands": src.count, "dtype": src.dtypes[0], "nodata": src.nodata,
            "scale_offset": [src.scales[0], src.offsets[0]],
            "bounds_map_units": {"left": b.left, "bottom": b.bottom, "right": b.right, "top": b.top},
            "lon_range": [round(min(lon0, lon1), 5), round(max(lon0, lon1), 5)],
            "lat_range": [round(min(lat0, lat1), 5), round(max(lat0, lat1), 5)],
            "crs": src.crs.to_string() if src.crs else None,
        }
    print(json.dumps(info, indent=2))


def pick_bounds(src, size_m, center_lat=None, center_lon=None):
    """Square box of size_m metres, centred on lat/lon if given, else on the dataset."""
    b = src.bounds
    if center_lat is not None and center_lon is not None:
        to_map, _ = _transformers(src)
        cx, cy = to_map.transform(center_lon, center_lat)
        if not (b.left <= cx <= b.right and b.bottom <= cy <= b.top):
            raise SystemExit(f"That lat/lon is outside this file. Run --info to see its lat/lon range.")
    else:
        cx, cy = (b.left + b.right) / 2, (b.bottom + b.top) / 2
    half = min(size_m, b.right - b.left, b.top - b.bottom) / 2
    # Keep the box inside the file.
    cx = min(max(cx, b.left + half), b.right - half)
    cy = min(max(cy, b.bottom + half), b.top - half)
    return cx - half, cy - half, cx + half, cy + half


def read_box(src, bounds, size, bands=1):
    """Read the box resampled to size x size; NaN where there is no data."""
    win = from_bounds(*bounds, transform=src.transform)
    shape = (size, size) if bands == 1 else (bands, size, size)
    idx = 1 if bands == 1 else list(range(1, bands + 1))
    arr = src.read(idx, window=win, out_shape=shape,
                   resampling=Resampling.bilinear, masked=True, boundless=False)
    return arr


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dtm", required=True)
    ap.add_argument("--ortho")
    ap.add_argument("--info", action="store_true", help="print file details and exit")
    ap.add_argument("--bounds", nargs=4, type=float, metavar=("LEFT", "BOTTOM", "RIGHT", "TOP"))
    ap.add_argument("--center-lat", type=float)
    ap.add_argument("--center-lon", type=float, help="degrees EAST (Gale is about 137.4)")
    ap.add_argument("--size-m", type=float, default=2000)
    ap.add_argument("--heightmap-res", type=int, default=2049, help="Unity: 1025, 2049 or 4097")
    ap.add_argument("--ortho-res", type=int, default=4096)
    ap.add_argument("--cost-res", type=int, default=513, help="A* grid; smaller = faster")
    ap.add_argument("--out", default="out")
    a = ap.parse_args()
    if a.info:
        print_info(a.dtm)
        return
    if not a.ortho:
        raise SystemExit("--ortho is required unless you use --info")
    os.makedirs(a.out, exist_ok=True)

    # ---- Terrain model ---------------------------------------------------
    with rasterio.open(a.dtm) as src:
        bounds = (tuple(a.bounds) if a.bounds
                  else pick_bounds(src, a.size_m, a.center_lat, a.center_lon))
        z = read_box(src, bounds, a.heightmap_res).astype("float64")
        z = z.filled(np.nan)
        # Some products store integers + scale/offset. Convert to real metres.
        scale, offset = src.scales[0], src.offsets[0]
        if scale != 1.0 or offset != 0.0:
            z = z * scale + offset
        crs = src.crs.to_string() if src.crs else None

    box_w = bounds[2] - bounds[0]
    box_h = bounds[3] - bounds[1]
    nan_frac = float(np.isnan(z).mean())
    if nan_frac > 0.02:
        print(f"WARNING: {nan_frac:.0%} of the box has no data. Move the box (--bounds) "
              "inside the DTM's valid area or the terrain will have holes/cliffs.")

    # Slope from REAL heights (never from the exaggerated Unity heights).
    px = box_w / (a.heightmap_res - 1)
    dzdy, dzdx = np.gradient(z, px)
    slope = np.degrees(np.arctan(np.hypot(dzdx, dzdy)))

    # Heightmap: fill holes with the lowest valid value, normalise to 0..65535.
    zmin, zmax = float(np.nanmin(z)), float(np.nanmax(z))
    zf = np.where(np.isnan(z), zmin, z)
    h16 = np.round((zf - zmin) / max(zmax - zmin, 1e-6) * 65535).astype("<u2")
    h16.tofile(os.path.join(a.out, "heightmap.raw"))

    # Slope preview.
    s = np.nan_to_num(slope, nan=0.0)
    Image.fromarray(np.clip(s / 35.0 * 255, 0, 255).astype(np.uint8)).save(
        os.path.join(a.out, "slope.png"))

    # ---- Route cost grid (coarser, for A*) -------------------------------
    g = a.cost_res
    step = (a.heightmap_res - 1) / (g - 1)
    ii = np.round(np.arange(g) * step).astype(int)
    sg = slope[np.ix_(ii, ii)]
    cost = np.where(sg < CAUTION_DEG, 1.0, np.where(sg < NOGO_DEG, 5.0, np.inf))
    cost[np.isnan(sg)] = np.inf
    cost.astype("<f4").tofile(os.path.join(a.out, "cost.bin"))
    preview = np.zeros((g, g, 3), np.uint8)
    preview[cost == 1.0] = (99, 153, 34)
    preview[cost == 5.0] = (239, 159, 39)
    preview[np.isinf(cost)] = (226, 75, 74)
    preview[np.isnan(sg)] = (0, 0, 0)
    Image.fromarray(preview).save(os.path.join(a.out, "cost.png"))

    # ---- Photo (same box) ------------------------------------------------
    with rasterio.open(a.ortho) as osrc:
        if crs and osrc.crs and osrc.crs.to_string() != crs:
            print("WARNING: ortho and DTM use different projections; the crop may not line up.")
        nb = min(osrc.count, 3)
        img = read_box(osrc, bounds, a.ortho_res, bands=nb)
        img = img.filled(0).astype("float64")
    if nb == 1:
        img = img[np.newaxis, ...]
    # Stretch each band to 0..255 (HiRISE orthos are not always 8-bit).
    out = []
    for band in img:
        valid = band[band > 0]
        lo, hi = (np.percentile(valid, [1, 99]) if valid.size else (0, 1))
        out.append(np.clip((band - lo) / max(hi - lo, 1e-6) * 255, 0, 255))
    rgb = np.stack(out, axis=-1).astype(np.uint8)
    if rgb.shape[-1] == 1:
        rgb = rgb[..., 0]
    Image.fromarray(rgb).save(os.path.join(a.out, "ortho.png"))

    meta = {
        "crs": crs,
        "bounds_m": {"left": bounds[0], "bottom": bounds[1], "right": bounds[2], "top": bounds[3]},
        "box_size_m": [box_w, box_h],
        "heightmap": {"file": "heightmap.raw", "res": a.heightmap_res,
                      "format": "uint16 little-endian (Unity byte order: Windows)",
                      "elev_min_m": zmin, "elev_max_m": zmax,
                      "unity_terrain_height_m": zmax - zmin},
        "cost": {"file": "cost.bin", "res": g, "format": "float32 little-endian, row 0 = north edge",
                 "values": {"ok": 1.0, "caution": 5.0, "no_go": "inf"},
                 "caution_deg": CAUTION_DEG, "nogo_deg": NOGO_DEG,
                 "loaded_nogo_deg": LOADED_NOGO_DEG},
        "ortho": {"file": "ortho.png", "res": a.ortho_res},
        "no_data_fraction": nan_frac,
        "row_order_note": "Row 0 is the NORTH edge. If Unity shows the terrain mirrored, tick 'Flip Vertically' on RAW import.",
    }
    with open(os.path.join(a.out, "meta.json"), "w") as f:
        json.dump(meta, f, indent=2)
    print(json.dumps({"box_m": [round(box_w), round(box_h)], "elev_range_m": [round(zmin, 1), round(zmax, 1)],
                      "no_data": f"{nan_frac:.1%}", "out": a.out}, indent=2))


if __name__ == "__main__":
    main()
