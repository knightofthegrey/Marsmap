"""
EVA route planning on the terrain the server sends to Unity.

The algorithms are the team's, from Python/Mars_mapping(3).ipynb ("Map between spots" and the
"Martian Trail Recommender"), with the same numbers:
  - hazard zones by ground tilt: 0-10 deg optimal, 10-20 caution, 20-30 high risk, >30 impassable
  - A* over the 8 neighbouring cells; a step may not climb or drop more than 18 deg
    (MAX_FORWARD_DEG) and ground tilted more than 25 deg (MAX_SIDESLOPE_DEG) is off limits;
    climbing costs more than descending
  - EVA time from Tobler's hiking function, scaled for Mars
  - difficulty score -> EASY / MODERATE / HARD
The notebook ran them on the 1 m Gale DEM file. Here they run on the same heights Unity shows,
in Unity world metres (x = east, z = north, map centre at 0,0), so any map the server builds
(the Gale tiles, a --polygon, the HiRISE site, a JMARS CSV) can be planned on.

Only needs numpy.
"""
import heapq
import math

import numpy as np

# --- From Mars_mapping(3).ipynb -------------------------------------------------------------
MAX_FORWARD_DEG = 18.0      # steepest step up or down
MAX_SIDESLOPE_DEG = 25.0    # steepest ground tilt allowed to stand on
ZONE_EDGES = [10.0, 20.0, 30.0]  # optimal | caution | high risk | impassable
ZONE_NAMES = ["optimal", "caution", "high risk", "impassable"]
SMOOTH_SIGMA = 0.8          # cells; the notebook's gaussian_filter(sigma=0.8)
WINDOW_PAD_M = 600.0        # the notebook padded its window by 600 px of its 1 m DEM

# --- Ours: keeps planning to a few seconds on any map size ----------------------------------
MAX_CELLS_ACROSS = 800      # the planning grid is at most this many cells wide/tall
SNAP_RADIUS_CELLS = 25      # a waypoint on unsafe ground moves to safe ground within this


class WorldDEM:
    """The terrain Unity shows, as height patches in Unity world metres.

    Each patch is a grid (row 0 = south, column 0 = west) whose [0, 0] point sits at world
    (x0, z0) and whose points are dx (east) and dz (north) metres apart. A single terrain is one
    patch; a tile grid is one patch per tile (they share their edge rows)."""

    def __init__(self, patches):
        self.patches = patches
        self.spacing = min(min(p["dx"], p["dz"]) for p in patches)
        self.x_min = min(p["x0"] for p in patches)
        self.z_min = min(p["z0"] for p in patches)
        self.x_max = max(p["x0"] + (p["grid"].shape[1] - 1) * p["dx"] for p in patches)
        self.z_max = max(p["z0"] + (p["grid"].shape[0] - 1) * p["dz"] for p in patches)

    @classmethod
    def single(cls, grid, size_x, size_z):
        """One terrain centred on the origin (Terraingen's recenterTerrainAtOrigin)."""
        rows, cols = grid.shape
        return cls([dict(grid=np.asarray(grid, np.float32), x0=-size_x / 2, z0=-size_z / 2,
                         dx=size_x / (cols - 1), dz=size_z / (rows - 1))])

    @classmethod
    def tiles(cls, t):
        """dem_builder.build_tiles() output, laid out like Terraingen.ApplyTile does."""
        (tx, tz), nx, nz = t["tile_size"], t["nx"], t["nz"]
        patches = []
        for (ix, iz), tile in t["tiles"].items():
            g = np.asarray(tile["grid"], np.float32)
            patches.append(dict(grid=g, x0=-tx * nx / 2 + ix * tx, z0=-tz * nz / 2 + iz * tz,
                                dx=tx / (g.shape[1] - 1), dz=tz / (g.shape[0] - 1)))
        return cls(patches)

    def sample(self, xs, zs):
        """Heights (m) at world points (bilinear), NaN where there is no terrain."""
        xs, zs = np.asarray(xs, np.float64), np.asarray(zs, np.float64)
        out = np.full(xs.shape, np.nan, np.float32)
        for p in self.patches:
            g = p["grid"]
            rows, cols = g.shape
            fc = (xs - p["x0"]) / p["dx"]
            fr = (zs - p["z0"]) / p["dz"]
            m = (fc >= 0) & (fc <= cols - 1) & (fr >= 0) & (fr <= rows - 1) & np.isnan(out)
            if not m.any():
                continue
            c, r = fc[m], fr[m]
            c0 = np.minimum(np.floor(c).astype(int), cols - 2)
            r0 = np.minimum(np.floor(r).astype(int), rows - 2)
            ac, ar = c - c0, r - r0
            out[m] = (g[r0, c0] * (1 - ac) * (1 - ar) + g[r0, c0 + 1] * ac * (1 - ar)
                      + g[r0 + 1, c0] * (1 - ac) * ar + g[r0 + 1, c0 + 1] * ac * ar)
        return out


def _gaussian(a, sigma):
    """The notebook's scipy gaussian_filter(sigma=0.8) in plain numpy, NaN-aware like the
    notebook (fill NaN with the mean, smooth, put the NaN back)."""
    nan = np.isnan(a)
    b = np.where(nan, np.nanmean(a) if (~nan).any() else 0.0, a).astype(np.float64)
    r = max(1, int(4.0 * sigma + 0.5))  # scipy's truncate=4.0
    k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma) ** 2)
    k /= k.sum()
    for axis in (0, 1):
        pad = [(r, r) if ax == axis else (0, 0) for ax in (0, 1)]
        bp = np.pad(b, pad, mode="symmetric")  # = scipy's mode="reflect"
        b = sum(k[i] * np.take(bp, np.arange(i, i + b.shape[axis]), axis=axis)
                for i in range(len(k)))
    b[nan] = np.nan
    return b.astype(np.float32)


def _zones(tilt):
    z = np.digitize(tilt, ZONE_EDGES)
    return np.where(np.isnan(tilt), 3, z)


def _astar(Z, passable, H, W, sr, sc, er, ec, spacing):
    """The notebook's run_astar_leg, on flat lists for speed. Returns [(r, c), ...] or None."""
    if (sr, sc) == (er, ec):
        return [(sr, sc)]
    d1, d2 = spacing, spacing * math.sqrt(2)
    nbrs = [(-1, 0, d1), (1, 0, d1), (0, -1, d1), (0, 1, d1),
            (-1, -1, d2), (-1, 1, d2), (1, -1, d2), (1, 1, d2)]
    zf = Z.ravel().tolist()
    ok = passable.ravel().tolist()
    g = [math.inf] * (H * W)
    parent = [-1] * (H * W)
    closed = bytearray(H * W)
    tan_max = math.tan(math.radians(MAX_FORWARD_DEG))
    start, end = sr * W + sc, er * W + ec
    g[start] = 0.0
    heap = [(math.hypot(sc - ec, sr - er) * spacing, 0.0, sr, sc)]
    atan, degrees, hypot = math.atan, math.degrees, math.hypot
    while heap:
        _, gc, r, c = heapq.heappop(heap)
        i = r * W + c
        if closed[i]:
            continue
        closed[i] = 1
        if i == end:
            path = []
            while i != -1:
                path.append(divmod(i, W))
                if i == start:
                    break
                i = parent[i]
            return path[::-1]
        zc = zf[i]
        for dr, dc, hd in nbrs:
            nr, nc = r + dr, c + dc
            if nr < 0 or nc < 0 or nr >= H or nc >= W:
                continue
            j = nr * W + nc
            if closed[j] or not ok[j]:
                continue
            dz = zf[j] - zc
            if abs(dz) > tan_max * hd:           # same as |forward_deg| > MAX_FORWARD_DEG
                continue
            fwd = degrees(atan(dz / hd))
            step = hd * ((1.0 + (fwd / 8.0) ** 2.5) if fwd >= 0 else (1.0 + (-fwd / 12.0) ** 2.0))
            ng = gc + step
            if ng < g[j]:
                g[j] = ng
                parent[j] = i
                heapq.heappush(heap, (ng + hypot(nc - ec, nr - er) * spacing, ng, nr, nc))
    return None


def plan(dem, points, loop=False, return_to_start=False):
    """Plan an EVA route through `points` [(x, z), ...] in Unity world metres (the first one is
    usually the astronaut). loop: come back to the first point. return_to_start: the last point
    already is the route's start (Unity sends it); only changes the wording.
    Returns a JSON-ready dict; see the "route" message in server.py."""
    pts = [(float(x), float(z)) for x, z in points]
    if loop and len(pts) > 1:
        pts.append(pts[0])
    back_to_start = (loop or return_to_start) and len(pts) > 2
    if len(pts) < 2:
        return dict(ok=False, error="Add at least one waypoint")

    # 1. Planning window: the waypoints plus a margin, on a grid of at most MAX_CELLS_ACROSS
    xs, zs = [p[0] for p in pts], [p[1] for p in pts]
    extent = max(max(xs) - min(xs), max(zs) - min(zs))
    pad = max(WINDOW_PAD_M, 0.15 * extent, 20 * dem.spacing)
    x0, x1 = max(min(xs) - pad, dem.x_min), min(max(xs) + pad, dem.x_max)
    z0, z1 = max(min(zs) - pad, dem.z_min), min(max(zs) + pad, dem.z_max)
    spacing = max(dem.spacing, max(x1 - x0, z1 - z0) / (MAX_CELLS_ACROSS - 1))
    W = int((x1 - x0) / spacing) + 1
    H = int((z1 - z0) / spacing) + 1
    gx = x0 + np.arange(W) * spacing
    gz = z0 + np.arange(H) * spacing
    XX, ZZ = np.meshgrid(gx, gz)                    # row 0 = south
    Z = dem.sample(XX, ZZ)
    if np.isnan(Z).all():
        return dict(ok=False, error="The waypoints are outside the map")

    # 2. Smooth + ground tilt + what's walkable (notebook steps 3 and 4)
    Zs = _gaussian(Z, SMOOTH_SIGMA)
    dzdz, dzdx = np.gradient(np.nan_to_num(Zs, nan=float(np.nanmean(Zs))), spacing, spacing)
    tilt = np.degrees(np.arctan(np.hypot(dzdx, dzdz))).astype(np.float32)
    tilt[np.isnan(Zs)] = np.nan
    passable = ~np.isnan(Zs) & (tilt <= MAX_SIDESLOPE_DEG)

    # 3. Waypoints -> cells; one on unsafe ground moves to the nearest safe cell
    hazards, cells = [], []
    for k, (x, z) in enumerate(pts):
        r = int(np.clip(round((z - z0) / spacing), 0, H - 1))
        c = int(np.clip(round((x - x0) / spacing), 0, W - 1))
        if not passable[r, c]:
            rr, cc = np.nonzero(passable[max(0, r - SNAP_RADIUS_CELLS):r + SNAP_RADIUS_CELLS + 1,
                                         max(0, c - SNAP_RADIUS_CELLS):c + SNAP_RADIUS_CELLS + 1])
            if rr.size:
                rr += max(0, r - SNAP_RADIUS_CELLS)
                cc += max(0, c - SNAP_RADIUS_CELLS)
                best = int(np.argmin((rr - r) ** 2 + (cc - c) ** 2))
                moved = math.hypot(rr[best] - r, cc[best] - c) * spacing
                r, c = int(rr[best]), int(cc[best])
                if k == 0:
                    name, where = "Your position", "the route starts"
                elif back_to_start and k == len(pts) - 1:
                    name, where = "Home Base", "the route ends"
                else:
                    name, where = f"Waypoint {k}", "the route goes"
                hazards.append(f"{name} is on ground steeper than {MAX_SIDESLOPE_DEG:.0f} deg: "
                               f"{where} {moved:.0f} m away on safe ground")
        cells.append((r, c))
    for k in range(len(cells) - 1):
        if cells[k] == cells[k + 1] and pts[k] != pts[k + 1]:
            a = "your position" if k == 0 else f"waypoint {k}"
            b = "Home Base" if back_to_start and k + 1 == len(pts) - 1 else f"Waypoint {k + 1}"
            hazards.append(f"{b} lands on the same safe spot as {a}: move it")

    # 4. A* leg by leg
    path, legs = [], []
    for k in range(len(cells) - 1):
        leg = _astar(Zs, passable, H, W, *cells[k], *cells[k + 1], spacing)
        a = "start" if k == 0 else f"{k}"
        b = "home" if (back_to_start and k + 1 == len(cells) - 1) else f"{k + 1}"  # Home Base
        if leg is None:
            # No safe path: draw it straight and flag it
            n = max(2, int(math.hypot(cells[k + 1][0] - cells[k][0],
                                      cells[k + 1][1] - cells[k][1])) + 1)
            rr = np.linspace(cells[k][0], cells[k + 1][0], n).round().astype(int)
            cc = np.linspace(cells[k][1], cells[k + 1][1], n).round().astype(int)
            leg_pts = [(int(r), int(c), True) for r, c in zip(rr, cc)]
            hazards.append(f"Leg {a} -> {b}: NO SAFE PATH (every way needs a step over "
                           f"{MAX_FORWARD_DEG:.0f} deg or ground over {MAX_SIDESLOPE_DEG:.0f} deg). "
                           "Shown straight in magenta.")
        else:
            leg_pts = [(r, c, False) for r, c in leg]
        legs.append(dict(from_wp=a, to_wp=b, ok=leg is not None, first=len(path) - (1 if path else 0)))
        path += leg_pts[1:] if path else leg_pts

    if len(path) < 2:
        return dict(ok=False, hazards=hazards,
                    error="The waypoints all land on the same safe spot (the ground around them "
                          f"is steeper than {MAX_SIDESLOPE_DEG:.0f} deg): move them")

    # 5. Trail metrics (notebook step 5 + the trail recommender's difficulty)
    pr = np.array([p[0] for p in path])
    pc = np.array([p[1] for p in path])
    blocked = np.array([p[2] for p in path])
    px, pz = x0 + pc * spacing, z0 + pr * spacing
    elev = np.nan_to_num(Zs[pr, pc], nan=float(np.nanmean(Zs)))
    d = np.hypot(np.diff(px), np.diff(pz))
    d = np.maximum(d, 1e-6)
    de = np.diff(elev)
    grades = np.degrees(np.arctan(de / d))
    speed_kmh = 3.0 * np.exp(-3.5 * np.abs(de / d + 0.05))
    speed_ms = np.maximum(speed_kmh * (1000.0 / 3600.0), 0.1)
    step_s = d / speed_ms
    total_m = float(d.sum())
    gain = float(de[de > 0].sum())
    loss = float(-de[de < 0].sum())
    max_climb = max(0.0, float(grades.max())) if grades.size else 0.0      # all downhill: 0
    max_descent = max(0.0, float(-grades.min())) if grades.size else 0.0   # all uphill: 0
    side = np.nan_to_num(tilt[pr, pc], nan=90.0)
    max_side = float(side[~blocked].max()) if (~blocked).any() else 0.0
    minutes = float(step_s.sum() / 60.0)
    score = ((total_m / 1000.0) * 1.5 + (gain / 50.0) * 2.0
             + (max(max_climb, 0.0) / 10.0) * 1.5 + (max_side / 15.0) * 1.0)
    if score < 3.0 and max_climb < 10.0:
        difficulty = "EASY"
    elif score < 6.5 and max_climb < 15.0:
        difficulty = "MODERATE"
    else:
        difficulty = "HARD"

    tier = _zones(side)
    tier = np.where(blocked, 4, tier)                # 4 = no safe path (drawn straight)
    seg_len = np.concatenate([[0.0], d])             # distance share per point
    zone_pct = [float(seg_len[tier == z].sum() / max(total_m, 1e-6) * 100) for z in range(5)]

    # Hazard notes in plain words
    if max_climb > 0 and grades.size:
        i = int(np.argmax(grades))
        hazards.append(f"Steepest climb {max_climb:.1f} deg at {np.cumsum(d)[i] / 1000:.2f} km")
    if max_descent > 0 and grades.size:
        i = int(np.argmin(grades))
        hazards.append(f"Steepest descent {max_descent:.1f} deg at {np.cumsum(d)[i] / 1000:.2f} km")
    pct = lambda v: f"{v:.0f}%" if v >= 1 else "under 1%"
    if zone_pct[2] > 0:
        hazards.append(f"{pct(zone_pct[2])} of the route crosses high-risk ground (20-30 deg tilt)")
    if zone_pct[1] > 0:
        hazards.append(f"{pct(zone_pct[1])} crosses caution ground (10-20 deg tilt)")
    if spacing > 25:
        hazards.append(f"Planned on a {spacing:.0f} m grid: small rocks and craters aren't seen")

    # Per-leg distance and time
    cum_m = np.concatenate([[0.0], np.cumsum(d)])
    cum_s = np.concatenate([[0.0], np.cumsum(step_s)])
    for k, leg in enumerate(legs):
        a = leg.pop("first")
        b = legs[k + 1]["first"] if k + 1 < len(legs) else len(path) - 1
        leg["start_index"], leg["end_index"] = int(a), int(b)  # this leg = x[a..b], z[a..b]
        leg["distance_m"] = round(float(cum_m[b] - cum_m[a]), 1)
        leg["minutes"] = round(float((cum_s[b] - cum_s[a]) / 60.0), 1)

    r6 = lambda a: [round(float(v), 2) for v in a]
    return dict(
        ok=all(l["ok"] for l in legs), error="" if all(l["ok"] for l in legs) else
        "Part of the route has no safe path",
        x=r6(px), z=r6(pz), elev=r6(elev), tier=[int(t) for t in tier],
        legs=legs, distance_m=round(total_m, 1), minutes=round(minutes, 1),
        gain_m=round(gain, 1), loss_m=round(loss, 1), max_climb_deg=round(max_climb, 1),
        max_descent_deg=round(max_descent, 1), max_side_deg=round(max_side, 1),
        score=round(score, 2), difficulty=difficulty,
        zone_pct=[round(p, 1) for p in zone_pct], hazards=hazards, spacing_m=round(spacing, 1))
