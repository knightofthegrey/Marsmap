# Marsmap

**NASA Space Apps Challenge 2026 · Seattle · November 14–15, 2026**
**Challenge:** [Interplanetary Survival Guide: Martian Map](https://www.spaceappschallenge.org/2026/challenges/interplanetary-survival-guide-martian-map/)

A first-person **Marswalk** on real NASA terrain at Mount Sharp, Gale crater, with a helmet-style heads-up display:
- map layers;
- a compass and navigation readout;
- a route planner that steers around steep ground;
- live radiation, UV and weather readings replayed from the Curiosity rover, with alerts when conditions turn dangerous.

It runs on a desktop and in VR on a **Meta Quest 3**. An **AR mode** shows the HUD and the map over your real room through the headset's passthrough cameras.

> **Demo:** recorded from the **`vr-quest`** branch, in VR on a Meta Quest 3.
> Video: *(add link)*

---

## Branches

The VR and AR work is being merged into `main`. Until then:

| Branch | What's on it |
|---|---|
| `main` | The desktop version: terrain, map layers, navigation, route planning, alerts, dust storm. Also the latest teammate tweaks: F8 green screen, 9 m waypoint arrival, the licence |
| `vr-quest` | Everything on `main` as of the demo **plus the Meta Quest 3 version**: XR rig, controller input, the VR HUD, offline mode, Quest 3 build settings. **The demo was recorded from this branch** |
| `vr-quest-ar` | `vr-quest` **plus AR mode** (passthrough): the real room instead of the Mars terrain, with the HUD and the EVA map on top |

The [VR guide](docs/VR_QUEST.md) covers both the VR and AR builds.

---

## What it does

**Terrain and map layers**
- **Where you start:** where Curiosity actually stood on **sol 4560** (4 June 2025). The ground is a 2 km patch of Mount Sharp built from a HiRISE terrain model, with the HiRISE colour photo draped over it.
- **Layers:** switch between photo → slope hazards (caution 15–20°, no-go 20°+) → THEMIS thermal inertia (sand vs. rock) → height colours.
- **Bigger area:** load the whole Gale crater region instead, as a seamless grid of terrain tiles (USGS MOLA-HRSC and CTX elevation, THEMIS and CTX imagery).

**Navigation and route planning**
- **Compass:** arrows to the next waypoint (red) and Home Base (green), plus a readout of heading, pitch, latitude, longitude and elevation.
- **Minimap:** it turns with you, is coloured by slope hazard zones, and has an "upcoming hills" elevation profile of the ground ahead.
- **EVA map:** a table-top hologram of the terrain. Drop waypoints on it, and an A* planner routes you around ground steeper than 18° (climbs) or 25° (side slopes).
  - **Route details:** distance, a Tobler hiking-time estimate, and an EASY / MODERATE / HARD rating.
  - **On the ground:** a beam marks the next waypoint and a sphere marks Home Base. On reaching a waypoint, the route quietly re-plans from where you stand.

**Live conditions and alerts**
- **Real readings:** radiation (RAD), temperature, pressure and UV (REMS) from sol 4560, replayed at 300× (a 4-hour EVA in 48 seconds).
- **Alerts with beeps:**
  - **dust storm early warning** from UV dimming and pressure dips;
  - **radiation**, using the RAD team's own solar-storm trigger and NASA's shelter level;
  - **UV index**, which on Mars is already above Earth's record when the EVA starts at 09:00 local time.
- **Environment panel** with the live readings, in the headset HUD.
- **A visible dust storm:** fog, orange tint and blowing dust.

**VR (Meta Quest 3)** (branch `vr-quest`)
- Head tracking, left-stick walking, snap turn.
- The whole HUD on one head-locked panel, laid out like the desktop.
- **EVA map with a controller laser:** tap the trigger to add a waypoint, hold and drag to turn the hologram.
- **Offline mode:** the headset needs no laptop.

**AR mode** (branch `vr-quest-ar`)
- **What you see:** your real room through the Quest 3's colour passthrough, with the HUD on top. The Mars terrain, sky and ground markers are hidden.
- **What stays:** the EVA map hologram floats at table height, and route planning, the compass arrows, the minimap and the alerts all keep working.
- **Switching:** click the right stick to switch between Mars and AR at any time. Set `"passthrough": true` in `Assets/Resources/marsmap_config.json` to start in AR mode.

### Real data vs. simulated

| | Source |
|---|---|
| Terrain, photo, slope, thermal, rover position | **Real** NASA / USGS data (HiRISE, THEMIS, MSL waypoints; CTX and MOLA-HRSC for the big map) |
| Radiation, temperature, pressure, UV | **Real** Curiosity RAD + REMS readings from sol 4560, time-compressed |
| Dust front and solar particle event | **Simulated**: scripted changes to the real readings, shaped on measured Mars events. The HUD labels them **SIMULATED** |
| Alert thresholds | From published sources: [docs/ALERTS.md](docs/ALERTS.md) |
| Earth message, navigation failure | Scripted demo events |

---

## Quick start (desktop)

You need **Unity 6000.6.9f1** (Unity 6.6), **Python 3.10+** and **Git LFS** (GitHub Desktop includes it).

```bash
# once, from the repo folder
python3 -m venv .venv
.venv/bin/pip install -r Python/requirements.txt
.venv/bin/pip install -r Python/requirements-maps.txt   # only for the big Gale map / regenerating data
```

1. **Open the scene:** open the project in Unity, then open `Assets/Scenes/GetStarted_Scene.unity`.
2. **Start the server** (terminal 1). Pick one:
   ```bash
   python3 Python/server.py --site        # the 2 km HiRISE site at Curiosity (used for the demo)
   python3 Python/server.py               # the whole Gale crater region, as terrain tiles
   ```
   The big map downloads data the first time and caches it. To skip that, unzip `gale_terrain_tiles.zip` in the repo folder: it holds a pre-built `Python/imagery_cache/`.
3. **Press Play** in Unity.
4. **Start the EVA replay** (terminal 2): `python3 Python/feeder.py`. Alerts and the dust storm arrive within 48 seconds.
   - To preview them without Unity: `python3 Python/feeder.py --timeline`.

The scripts relaunch themselves inside `.venv`, so plain `python3` works.

## Quick start (VR and AR, Meta Quest 3)

Check out `vr-quest` (VR) or `vr-quest-ar` (VR + AR mode). Build the Android app and install it over USB with Unity's **Build And Run**.

The full, tested setup is in **[docs/VR_QUEST.md](docs/VR_QUEST.md)**, including the steps that are easy to miss.
- **AR mode** needs a few more settings: two packages, three OpenXR features and Android API 32.
- **Offline:** the terrain and the EVA replay are packed into the app, so the headset needs no laptop. Press **X** to start the EVA.
- **Live from the laptop:** start the server with `--host 0.0.0.0` *before* opening the app, and put the laptop's Wi-Fi address in `Assets/Resources/marsmap_config.json`.

## Controls

| Action | Desktop | Quest 3 |
|---|---|---|
| Walk | W A S D / arrow keys | Left stick (where you look) |
| Look / turn | Mouse | Head; right stick snap-turns 30° |
| Sprint | Hold Left Shift | Hold left grip |
| Jump | Space | Off by default (`allow_jump_in_vr`) |
| Next map layer | L | B |
| Test dust gust | G | Y |
| Start EVA (offline mode) | Enter | X |
| Minimap zoom / hazard colours | + / −, H | Right stick up / down |
| EVA map open / close | M | Menu button (left controller) |
| AR mode on / off (`vr-quest-ar`) | P (preview over black) | Click the right stick |
| Green screen for screenshots (`main`) | F8 | |

**On the EVA map:**

| Action | Desktop | Quest 3 |
|---|---|---|
| Add waypoint | Click | Tap the trigger |
| Turn / tilt | Drag; Q / E | Hold the trigger and drag; right stick |
| Remove waypoint | Shift + click | Point at the pin, A |
| Zoom | Scroll | Right stick up / down |
| Move the area | W A S D / right-drag | Left stick |
| Top-down, centre on me, undo, clear, return home | T, F, Backspace, C, R | Panel buttons |

---

## How it works

```mermaid
flowchart LR
  subgraph Prep["Python: one-time data prep"]
    MS["make_site.py<br/>HiRISE + THEMIS site"]
    FS["fetch_sol.py<br/>RAD + REMS for one sol"]
    DB["dem_builder.py<br/>Gale tiles"]
  end
  subgraph Run["Python: during the demo"]
    SRV["server.py<br/>WebSocket :8765<br/>terrain, route planning, relay"]
    FD["feeder.py + alerts.py<br/>replay + alert detector"]
  end
  subgraph Unity["Unity (desktop, Quest 3 VR or AR)"]
    PC["PythonConnection"] --> T["Terraingen + TerrainOverlay"]
    PC --> HUD["Compass, nav, minimap,<br/>EVA map, alerts, dust"]
    VR["VR rig + VR HUD<br/>AR: passthrough"]
  end
  MS --> SRV
  DB --> SRV
  FS --> FD
  FD -- "telemetry, alerts, events" --> SRV
  SRV -- "terrain + images, routes, live data" --> PC
```

- **Python** prepares NASA data and runs a local WebSocket server. It sends Unity the terrain and map images, plans routes, and relays the replayed telemetry and alerts.
- **Unity** builds the terrain at runtime and draws the HUD.
  - **In a headset,** the same scene switches to VR by itself.
  - **In AR mode,** the terrain stays loaded (for the map and readouts) but isn't drawn, so the passthrough room shows behind the HUD.
- **For the headset,** `Python/export_offline.py` packs the server's messages into the app, for offline use.

### Repo map

| Path | What's there |
|---|---|
| `Assets/Scenes/GetStarted_Scene.unity` | The one scene |
| `Assets/SourceFiles/Scripts/` | Unity scripts: connection, terrain, layers, movement, compass, nav, minimap, EVA map, alerts, dust, sky, green screen |
| `Assets/SourceFiles/Scripts/VR/` | (`vr-quest`) VR rig, controller input, VR HUD, VR versions of the minimap / map / alerts, config; (`vr-quest-ar`) `PassthroughView` for AR mode |
| `Assets/Resources/marsmap_config.json` | (`vr-quest`) Server address and VR / AR settings |
| `Assets/StreamingAssets/Marsmap/` | (`vr-quest`) Offline bundle for the headset (terrain + EVA replay) |
| `Python/` | Server, replay, alerts, route planner, data-prep scripts, data files ([Python/README.md](Python/README.md)) |
| `Python/Mars_mapping*.ipynb` | The team's route-planning and trail-recommender notebooks |
| `docs/` | Project report, alert system, VR / AR guide |

---

## Data sources

- **HiRISE terrain model** `DTEEC_019698_1750_019988_1750_U01` (MRO; USGS Astrogeology): [STAC catalogue](https://stac.astrogeology.usgs.gov/browser-dev/#/api/collections/mro_hirise_socet_dtms)
- **HiRISE colour Gale mosaic** (MSL Gale merged orthophoto mosaic, USGS): [product page](https://astrogeology.usgs.gov/search/map/mars_msl_gale_merged_orthophoto_mosaic_25cm)
- **THEMIS thermal inertia** (Mars Odyssey): [mosaic 30S120E](https://astrogeology.usgs.gov/search/map/themis_thermal_inertia_mosaic_quantitative_30s120e_100mpp)
- **Curiosity position** (MSL waypoints, NASA/JPL MMGIS): [MSL_waypoints.json](https://mars.nasa.gov/mmgis-maps/MSL/Layers/json/MSL_waypoints.json)
- **Curiosity RAD** radiation data: [NASA PDS PPI](https://pds-ppi.igpp.ucla.edu/data/MSL-M-RAD-3-RDR-V1.0/)
- **Curiosity REMS** temperature, pressure and UV: [NASA PDS Atmospheres](https://atmos.nmsu.edu/PDS/data/mslrem_1001/)
- **Big Gale map:**
  - [MOLA-HRSC blended DEM, 200 m](https://asc-pds-services.s3.us-west-2.amazonaws.com/mosaic/Mars/HRSC_MOLA_Blend/Mars_HRSC_MOLA_BlendDEM_Global_200mp_v2.tif)
  - USGS CTX DTMs and orthoimages (via the [USGS STAC API](https://stac.astrogeology.usgs.gov/api/search))
  - THEMIS day IR
- **Alert thresholds and event shapes:** published RAD, REMS, Perseverance MEDA and NASA SRAG studies, listed in [docs/ALERTS.md](docs/ALERTS.md#sources)
- **[JMARS](https://jmars.asu.edu/)** (Arizona State University): scouting and CSV terrain exports

## The challenge, and how we answered it

NASA asked for a map, tool or interface a Mars astronaut could use to plan and carry out a Marswalk, built from NASA data.

| NASA's suggestion | How we met it |
|---|---|
| Start from a science goal | Mount Sharp's boxwork-era terrain, at Curiosity's real position |
| Layer at least three data types | Optical, topographic, thermal, radiation, atmospheric |
| Astronaut safety | Slope hazard zones, routes that avoid steep ground, radiation, UV and dust alerts with actions to take |
| Pick a format | A helmet HUD: on desktop, in a VR headset, and as AR over the real world |
| Look beyond Mars | Slope limits borrowed from Artemis lunar EVA planning |

The full checklist, with what's done and what isn't, is in [docs/PROJECT_REPORT.md §11](docs/PROJECT_REPORT.md#11-challenge-requirements-checklist).

## Documentation

- **[docs/PROJECT_REPORT.md](docs/PROJECT_REPORT.md):** how everything works and connects, every data source, the message protocol, the challenge checklist, known limitations.
- **[docs/ALERTS.md](docs/ALERTS.md):** the alert system: thresholds, the demo timeline, sources.
- **[docs/VR_QUEST.md](docs/VR_QUEST.md):** building and running on a Meta Quest 3, including AR mode.
- **[Python/README.md](Python/README.md):** the Python scripts, data files and commands.

## Use of AI

We used generative AI as a development aid during the hackathon, and we want to be clear about where.

- **Code:** team members used AI coding assistants, including Anthropic's Claude through Claude Code. They helped write and debug parts of:
  - the Python data pipeline and WebSocket server;
  - the environment alert system;
  - the Unity HUD, VR and AR code;
  - the setup and build steps.
- **Research:** AI assistants helped find and summarise NASA data sources and published studies (for example the alert thresholds in [docs/ALERTS.md](docs/ALERTS.md)). The sources are linked so every number can be checked against the original.
- **Documentation:** this README and the files in `docs/` were drafted with AI help and reviewed by the team.
- **What AI did not do:** it didn't generate or alter any data. All terrain, imagery, radiation and weather values come from the NASA / USGS sources listed above. The two simulated events are scripted, documented and labelled **SIMULATED** in the app.
- **The team:** the team chose the idea, the design and the scope, tested the app on desktop and on the Quest 3, and is responsible for the result.

## Team

Nate · Ahrial · Aiden · Aidan

## License

[MIT](LICENSE). NASA and USGS data are public domain; credit the sources above when reusing them.
