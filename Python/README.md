# Martian Map - Python side

```
fetch_sol.py --> sol_samples.csv --> feeder.py --ws--> server.py --ws--> Unity  PythonConnection.cs
                                                          ^   |
          make_site.py --> site/ (terrain.csv + images) --+   +-- heightmap --> Terraingen.cs
                         (or a JMARS export.csv)              +-- photo / slope / thermal images --> TerrainOverlay
raster_to_csv.py / prep_terrain.py --> extra map layers (CSV / files), not sent over the socket
```

| File | What it does |
|---|---|
| `fetch_sol.py` | Downloads one sol of real Curiosity RAD (radiation) + REMS (temperature, pressure) data from NASA PDS and writes `sol_samples.csv` + `sol_samples.meta.json` |
| `feeder.py` | Replays `sol_samples.csv` and the scripted `events.json` onto the EVA clock, sends JSON to the server |
| `server.py` | WebSocket server on `ws://localhost:8765`, two jobs: (1) sends Unity the terrain when asked: `site/` if it exists, else a JMARS CSV you name, else a demo terrain; (2) relays everything the feeder sends to every Unity client, and late joiners get the last status + telemetry |
| `make_site.py` | **Real terrain**: HiRISE terrain model + colour photo + slope + thermal inertia around Curiosity's sol-4560 position -> `site/` |
| `site/` | Made by `make_site.py`: `terrain.csv`, `photo.jpg`, `slope.jpg`, `thermal.jpg`, `site.json` (about 13 MB) |
| `requirements.txt` | Server, feeder and `fetch_sol.py` (numpy, websockets) |
| `requirements-maps.txt` | Adds rasterio, pyproj, pillow for `make_site.py`, `raster_to_csv.py` and `prep_terrain.py` only |
| `raster_to_csv.py` | Any map raster (GeoTIFF / ISIS cube, local or URL) -> CSV grid over a 2 km box: thermal inertia, elevation, or slope with ok / caution / no-go classes |
| `prep_terrain.py` | HiRISE terrain model + photo -> Unity heightmap, photo, slope, route-cost grid, meta.json |
| `sol_samples.csv` | **Real data**: sol 4560 (2025-06-03), 09:00-13:00 Mars local time. Regenerate with `fetch_sol.py` |
| `sol_samples.meta.json` | Exactly which NASA files the CSV came from; the feeder reads its `source` for the HUD credit |
| `events.json` | Scripted demo events (dust gust, nav degraded, Earth note). Hand-written, not from a dataset |

## Run the demo

One-time setup, from the Marsmap folder:

```
python3 -m venv .venv
.venv/bin/pip install -r Python/requirements.txt
.venv/bin/pip install -r Python/requirements-maps.txt   # only if you run the map scripts
```

Then (plain `python3` is fine: `server.py` and `feeder.py` re-launch themselves with `.venv`):

```
python3 Python/server.py                      # terminal 1: real terrain from Python/site/ (demo if it's missing)
python3 Python/server.py --demo               #   ...or the fake demo terrain
python3 Python/server.py export.csv 1025      #   ...or a JMARS pixel export
python3 Python/feeder.py                      # terminal 2: 4-hour EVA in 48 s (demo pace)
python3 Python/feeder.py --speed 80           #   ...or slower, 3 minutes
```

The feeder finds `sol_samples.csv` and `events.json` next to itself, whatever folder you run it from.

Demo timeline at the default speed (start the feeder once Unity is in Play mode):

| Real time | Event | In Unity |
|---|---|---|
| ~8 s | Earth note | Console |
| ~20 s | Dust gust (`duration_s` 13) | Orange fog, dimmed sun, dusty tint, blowing dust; clears by ~33 s (`DustStormEffect`, press G to test) |
| ~35 s | Navigation degraded | Console |
| ~48 s | EVA finished | Console |

Press Play in Unity at any point. `PythonConnection` retries every 2 s until the server is up,
asks for the heightmap once (Terraingen falls back to Perlin terrain if none arrives in 5 s), and
logs events / Earth notes to the Console. Tick **Log Messages** on the component to see every
message. For a quick test run use `--speed 3600 --tick 0.05` (4 seconds).

## Real terrain + photo overlays (HiRISE)

`make_site.py` builds a 2 km x 2 km site centred on **Curiosity's real position on sol 4560**
(lat -4.79985, lon 137.38354 E, from NASA's MSL waypoint list), the same sol as the weather and
radiation replay. It writes `Python/site/`:

| File | What it is |
|---|---|
| `terrain.csv` | `Latitude,Longitude,Elevation` (m), 501 x 501 points 4 m apart, from a HiRISE terrain model. Same format as a JMARS export, so `server.py` loads it the same way |
| `photo.jpg` | HiRISE natural-colour photo of exactly the same box, 2048 px (~1 m per pixel) |
| `slope.jpg` | The photo with amber = 15-20 deg (caution) and red = 20+ deg (no-go) slopes |
| `thermal.jpg` | The photo with THEMIS thermal inertia: blue = finer, looser ground, red = rockier (relative, 100 m pixels) |
| `site.json` | Name, sol, date, centre, size, the layer labels/credits and the exact source URLs |

```
.venv/bin/pip install -r Python/requirements-maps.txt   # once
.venv/bin/python Python/make_site.py                     # ~20 s, downloads only the box (tens of MB)
python3 Python/server.py                                 # picks up Python/site/ automatically
```

When Unity asks for the terrain, the server sends the site info, then the heightmap, then the three
images. Terraingen builds the terrain at true height (`vertical_exaggeration` 1, so the
~500 m of relief is real), and `TerrainOverlay` drapes the photo (or the slope / thermal version) on it,
north up. Options: `--sol N` (where the rover was on another sol), `--center-lat/--center-lon`,
`--size-m`, `--cell-m`, `--tex-px`; `--help` lists them all.

The photo mosaic and the terrain model are tied to different base maps and disagree by about 49 m here.
The script measures the offset against the terrain model's own orthoimage and moves the terrain
onto the photo (`dtm_shift_to_photo` in `site.json`), so ridges, slope colours and the rover position
line up. The shift is a translation only, and the photo is also rotated about 0.3° against the terrain
model, so the match is within ~1 m at the rover (box centre) but up to ~10 m off at the box corners.
It also checks that every sample lands on its grid point and stops if one doesn't.

Sources (credits are in `site.json` too):
- Rover position: [MSL waypoints](https://mars.nasa.gov/mmgis-maps/MSL/Layers/json/MSL_waypoints.json), NASA/JPL
- Elevation: HiRISE terrain model DTEEC_019698_1750_019988_1750_U01, 1 m, NASA/JPL/University of Arizona, via [USGS STAC](https://stac.astrogeology.usgs.gov/api/collections/mro_hirise_socet_dtms) (if a site isn't covered, the script searches STAC for another model)
- Photo: [USGS HiRISE colour mosaic of Gale](https://asc-pds-services.s3.us-west-2.amazonaws.com/mosaic/Mars/MSL/MSL_Gale_HiRISE-LRGB_78quads_sharp_cog.tif), 0.25 m
- Thermal inertia: Mars Odyssey THEMIS 100 m mosaic, ASU / USGS (same tile as below)
- Slope limits: 15 / 20 deg, borrowed from Artemis lunar EVA planning, not a Mars-specific NASA limit

## Data sources

| Data | From | Script | Used for |
|---|---|---|---|
| Radiation dose rate | Curiosity RAD, [PDS PPI node](https://pds-ppi.igpp.ucla.edu/data/MSL-M-RAD-3-RDR-V1.0/) | `fetch_sol.py` | HUD dose + accumulated dose |
| Air temperature, pressure | Curiosity REMS, [PDS Atmospheres node](https://atmos.nmsu.edu/PDS/data/mslrem_1001/) | `fetch_sol.py` | HUD weather |
| Thermal inertia (sand vs rock) | Mars Odyssey THEMIS, [USGS tile 30S120E](https://astrogeology.usgs.gov/search/map/themis_thermal_inertia_mosaic_quantitative_30s120e_100mpp) | `make_site.py`, `raster_to_csv.py` | Thermal layer, hazard layer |
| Elevation, slope | MRO HiRISE terrain models, [USGS STAC](https://stac.astrogeology.usgs.gov/api/collections/mro_hirise_socet_dtms) | `make_site.py`, `raster_to_csv.py`, `prep_terrain.py` | Terrain, no-go areas, route |
| Photo | MRO HiRISE colour mosaic of Gale (USGS) / HiRISE orthoimages | `make_site.py`, `prep_terrain.py` | Texture on the terrain |
| Rover position | NASA MSL waypoints | `make_site.py` | Centre of the site |

**No wind.** Curiosity's REMS wind sensor has returned no data for years, so wind is not in the CSV,
the telemetry, or Unity's `TelemetryMessage`. Don't show a wind reading on the HUD.

**Archive lag.** PDS releases trail the rover by months; both archives currently stop at sol 4843.
The boxwork era starts around sol 4534.

### Real weather and radiation for another sol

```
python3 Python/fetch_sol.py --sol 4560 --start-lmst 9:00 --eva-min 240   # standard library only
```

Takes about a minute. RAD files are ~70 MB per sol, so the script downloads only the bytes it needs.
It warns if the window has long gaps between real readings.

### Map layers as CSV

Use the same box for every layer so they line up with each other and with the terrain.
These need `requirements-maps.txt` installed, and run with the venv's Python from the Marsmap folder:

```
# Thermal inertia around Gale (100 m cells -> 20 x 20 grid)
.venv/bin/python Python/raster_to_csv.py --src https://asc-astropedia.s3.us-west-2.amazonaws.com/Mars/Odyssey/THEMIS-Global-Thermal-Inertia-Mosaic/Quantitative-32-Bit/THEMIS_TI_Mosaic_Quant_30S120E_100mpp.cub \
    --center-lat -4.75 --center-lon 137.4 --cell-m 100 --name thermal_inertia --out thermal_inertia.csv

# Slope with ok / caution / no_go classes from a HiRISE terrain model (10 m cells -> 200 x 200)
.venv/bin/python Python/raster_to_csv.py --src <DTM url> --center-lat -4.75 --center-lon 137.4 --cell-m 10 --slope --out slope.csv

# Elevation from the same terrain model
.venv/bin/python Python/raster_to_csv.py --src <DTM url> --center-lat -4.75 --center-lon 137.4 --cell-m 10 --name elev_m --out elevation.csv
```

CSV columns: `row, col` (row 0 = north), `east_m, north_m` (metres from the box's south-west corner,
i.e. Unity x and z), `lat, lon`, then the value. `--slope` keeps the steepest point in each cell
(conservative for safety) and adds a `class` column using 15 deg caution / 20 deg no-go
(borrowed from Artemis lunar EVA planning, not a Mars-specific NASA limit).

## Using the data in Unity

```csharp
void Start() => PythonConnection.Instance.TelemetryReceived += OnTelemetry;
void OnDestroy() { if (PythonConnection.Instance) PythonConnection.Instance.TelemetryReceived -= OnTelemetry; }
void OnTelemetry(TelemetryMessage t) => doseText.text = $"{t.dose_total_mGy:F3} mGy";
```

Also available: `EventReceived`, `EarthNoteReceived`, `StatusReceived`, `ConnectionChanged`,
`LatestTelemetry`, `IsConnected`, and `SendJson(...)` for Unity -> Python messages (the server logs them).

## Message schema

Documented at the top of `feeder.py`; the matching C# classes are at the top of `PythonConnection.cs`.
Telemetry carries `t_eva_s, lmst_h` (Mars local time in hours), `dose_rate_uGy_h, dose_total_mGy,
temp_c, pressure_pa, source`. Agree changes with the server and Unity owners, and bump `"v"` on
both sides if the shape changes.
