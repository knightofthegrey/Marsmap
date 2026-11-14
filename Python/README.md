# Martian Map - Python side

```
fetch_sol.py  -->  sol_samples.csv  -->  feeder.py  --ws-->  server.py  --ws-->  Unity (PythonConnection.cs)
raster_to_csv.py / prep_terrain.py  -->  map files Unity loads at startup (not sent over the socket)
```

| File | What it does |
|---|---|
| `fetch_sol.py` | Downloads one sol of real Curiosity RAD (radiation) + REMS (temperature, pressure) data from NASA PDS and writes `sol_samples.csv` + `sol_samples.meta.json` |
| `feeder.py` | Replays `sol_samples.csv` and the scripted `events.json` onto the EVA clock, sends JSON to the server |
| `server.py` | WebSocket hub on `ws://localhost:8765`. Relays everything the feeder sends to every Unity client; late joiners get the last status + telemetry |
| `raster_to_csv.py` | Any map raster (GeoTIFF / ISIS cube, local or URL) -> CSV grid over a 2 km box: thermal inertia, elevation, or slope with ok / caution / no-go classes |
| `prep_terrain.py` | HiRISE terrain model + photo -> Unity heightmap, photo, slope, route-cost grid, meta.json |
| `sol_samples.csv` | **Real data**: sol 4560 (2025-06-03), 09:00-13:00 Mars local time. Regenerate with `fetch_sol.py` |
| `sol_samples.meta.json` | Exactly which NASA files the CSV came from; the feeder reads its `source` for the HUD credit |
| `events.json` | Scripted demo events (dust gust, nav degraded, Earth note). Hand-written, not from a dataset |

## Run the demo

```
cd Python
pip install -r requirements.txt
python server.py                              # terminal 1
python feeder.py --speed 80                   # terminal 2: 4-hour EVA in 3 minutes
```

Press Play in Unity at any point. `PythonConnection` creates itself if it isn't in the scene,
retries every 2 s until the server is up, and logs events / Earth notes to the Console.
Tick **Log Messages** on the component to see every message. For a quick test run use
`python feeder.py --speed 3600 --tick 0.05` (4 seconds).

## Data sources

| Data | From | Script | Used for |
|---|---|---|---|
| Radiation dose rate | Curiosity RAD, [PDS PPI node](https://pds-ppi.igpp.ucla.edu/data/MSL-M-RAD-3-RDR-V1.0/) | `fetch_sol.py` | HUD dose + accumulated dose |
| Air temperature, pressure | Curiosity REMS, [PDS Atmospheres node](https://atmos.nmsu.edu/PDS/data/mslrem_1001/) | `fetch_sol.py` | HUD weather |
| Thermal inertia (sand vs rock) | Mars Odyssey THEMIS, [USGS tile 30S120E](https://astrogeology.usgs.gov/search/map/themis_thermal_inertia_mosaic_quantitative_30s120e_100mpp) | `raster_to_csv.py` | Hazard layer |
| Elevation, slope | MRO HiRISE terrain models, [USGS STAC](https://stac.astrogeology.usgs.gov/api/collections/mro_hirise_socet_dtms) | `raster_to_csv.py`, `prep_terrain.py` | Terrain, no-go areas, route |
| Photo | MRO HiRISE orthoimage | `prep_terrain.py` | Texture on the terrain |

**No wind.** Curiosity's REMS wind sensor has returned no data for years, so wind is not in the CSV,
the telemetry, or Unity's `TelemetryMessage`. Don't show a wind reading on the HUD.

**Archive lag.** PDS releases trail the rover by months; both archives currently stop at sol 4843.
The boxwork era starts around sol 4534.

### Real weather and radiation for another sol

```
python fetch_sol.py --sol 4560 --start-lmst 9:00 --eva-min 240
```

Takes about a minute. RAD files are ~70 MB per sol, so the script downloads only the bytes it needs.
It warns if the window has long gaps between real readings.

### Map layers as CSV

Use the same box for every layer so they line up with each other and with the terrain:

```
# Thermal inertia around Gale (100 m cells -> 20 x 20 grid)
python raster_to_csv.py --src https://asc-astropedia.s3.us-west-2.amazonaws.com/Mars/Odyssey/THEMIS-Global-Thermal-Inertia-Mosaic/Quantitative-32-Bit/THEMIS_TI_Mosaic_Quant_30S120E_100mpp.cub \
    --center-lat -4.75 --center-lon 137.4 --cell-m 100 --name thermal_inertia --out thermal_inertia.csv

# Slope with ok / caution / no_go classes from a HiRISE terrain model (10 m cells -> 200 x 200)
python raster_to_csv.py --src <DTM url> --center-lat -4.75 --center-lon 137.4 --cell-m 10 --slope --out slope.csv

# Elevation from the same terrain model
python raster_to_csv.py --src <DTM url> --center-lat -4.75 --center-lon 137.4 --cell-m 10 --name elev_m --out elevation.csv
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
