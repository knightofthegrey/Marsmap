"""
Martian Map - turn a map raster (GeoTIFF, ISIS .cub, local file or https URL) into a CSV grid.

One row per grid cell over a square box, so map layers can go into Unity the same way
as the JMARS CSVs. Remote files are read in place: only the box is downloaded.

Columns:
  row, col     grid position; row 0 = NORTH edge, col 0 = WEST edge
  east_m       metres east of the box's west edge (cell centre)   -> Unity x
  north_m      metres north of the box's south edge (cell centre) -> Unity z
  lat, lon     cell centre, degrees (planetocentric, east-positive)
  value        the raster's value (or, with --slope, see below)

Usage:
  # Thermal inertia around Gale (THEMIS, 100 m pixels; tile 30S120E covers Gale):
  python raster_to_csv.py --src https://asc-astropedia.s3.us-west-2.amazonaws.com/Mars/Odyssey/THEMIS-Global-Thermal-Inertia-Mosaic/Quantitative-32-Bit/THEMIS_TI_Mosaic_Quant_30S120E_100mpp.cub \\
      --center-lat -4.75 --center-lon 137.4 --cell-m 100 --name thermal_inertia --out thermal_inertia.csv

  # Slope / no-go grid from a HiRISE terrain model (DTM):
  python raster_to_csv.py --src <DTM url> --cell-m 10 --slope --out slope.csv
    --slope computes slope from the elevations at --slope-res-m (default 2 m) and keeps
    the STEEPEST value inside each cell (conservative for astronaut safety). Adds a
    "class" column: ok (< 15 deg), caution (15-20), no_go (>= 20 or no data).

  # Elevation grid (same DTM, plain values):
  python raster_to_csv.py --src <DTM url> --cell-m 10 --name elev_m --out elevation.csv

Box selection is the same as prep_terrain.py: --center-lat/--center-lon, or --bounds,
or neither (box centred on the file). --size-m sets the box (default 2000).
Use the same box for every layer so the CSVs line up with each other and the terrain.
"""
import argparse
import csv
import json

import numpy as np
import rasterio

from prep_terrain import CAUTION_DEG, NOGO_DEG, _transformers, pick_bounds, read_box


def main():
    ap = argparse.ArgumentParser(description="Map raster -> CSV grid")
    ap.add_argument("--src", required=True, help="GeoTIFF / ISIS cube, local path or https URL")
    ap.add_argument("--out", required=True)
    ap.add_argument("--bounds", nargs=4, type=float, metavar=("LEFT", "BOTTOM", "RIGHT", "TOP"),
                    help="box in the file's own map units (metres)")
    ap.add_argument("--center-lat", type=float)
    ap.add_argument("--center-lon", type=float, help="degrees EAST (Gale is about 137.4)")
    ap.add_argument("--size-m", type=float, default=2000)
    ap.add_argument("--cell-m", type=float, default=10, help="output cell size in metres")
    ap.add_argument("--name", default="value", help="name of the value column")
    ap.add_argument("--slope", action="store_true", help="input is elevation; output max slope per cell")
    ap.add_argument("--slope-res-m", type=float, default=2.0)
    a = ap.parse_args()

    with rasterio.open(a.src) as src:
        bounds = (tuple(a.bounds) if a.bounds
                  else pick_bounds(src, a.size_m, a.center_lat, a.center_lon))
        box = bounds[2] - bounds[0]
        n = max(1, int(round(box / a.cell_m)))
        cell = box / n
        native = abs(src.res[0])
        if cell < native * 0.99:
            print(f"NOTE: cells ({cell:.1f} m) are smaller than the source pixels ({native:.1f} m); "
                  "values are interpolated, not extra detail.")
        scale, offset = src.scales[0], src.offsets[0]

        if a.slope:
            k = max(1, int(round(cell / a.slope_res_m)))
            z = read_box(src, bounds, n * k).astype("float64").filled(np.nan) * scale + offset
            dzdy, dzdx = np.gradient(z, box / (n * k))
            fine = np.degrees(np.arctan(np.hypot(dzdx, dzdy)))
            blocks = fine.reshape(n, k, n, k)
            all_nan = np.isnan(blocks).all(axis=(1, 3))
            vals = np.nanmax(np.where(np.isnan(blocks), -np.inf, blocks), axis=(1, 3))
            vals[all_nan] = np.nan
            name = "slope_max_deg"
        else:
            vals = read_box(src, bounds, n).astype("float64").filled(np.nan) * scale + offset
            name = a.name
        _, to_geo = _transformers(src)

    cols = np.arange(n)
    xs = bounds[0] + (cols + 0.5) * cell                 # map x per column
    ys = bounds[3] - (cols + 0.5) * cell                 # map y per row (row 0 = north)
    X, Y = np.meshgrid(xs, ys)
    lon, lat = to_geo.transform(X.ravel(), Y.ravel())
    lon = (np.asarray(lon) + 360) % 360                  # east-positive 0..360

    header = ["row", "col", "east_m", "north_m", "lat", "lon", name] + (["class"] if a.slope else [])
    with open(a.out, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(header)
        for i in range(n):
            for j in range(n):
                v = vals[i, j]
                rec = [i, j, round((j + 0.5) * cell, 2), round(box - (i + 0.5) * cell, 2),
                       round(float(lat[i * n + j]), 6), round(float(lon[i * n + j]), 6),
                       "" if np.isnan(v) else round(float(v), 3)]
                if a.slope:
                    rec.append("no_go" if np.isnan(v) or v >= NOGO_DEG
                               else "caution" if v >= CAUTION_DEG else "ok")
                w.writerow(rec)

    valid = vals[~np.isnan(vals)]
    summary = {"out": a.out, "grid": f"{n} x {n}", "cell_m": round(cell, 2), "box_m": round(box, 1),
               "bounds_map_m": [round(b, 2) for b in bounds],
               "no_data": f"{1 - valid.size / vals.size:.1%}"}
    if valid.size:
        summary[name] = {"min": round(float(valid.min()), 2), "mean": round(float(valid.mean()), 2),
                         "max": round(float(valid.max()), 2)}
    if a.slope and valid.size:
        summary["no_go_cells"] = f"{(np.isnan(vals) | (vals >= NOGO_DEG)).mean():.1%}"
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
