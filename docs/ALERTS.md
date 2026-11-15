# Environment alert system

The heads-up display (HUD) warns the astronaut about three hazards: **dust storms** (early warning), **radiation** and **ultraviolet (UV)**.

| Part | File | Job |
|---|---|---|
| Scenarios | `Python/events.json` (`"type": "scenario"`) | Scripted events that change the replayed readings |
| Detector | `Python/alerts.py` (`AlertDetector`) | Watches the telemetry only and decides the alerts |
| Feeder | `Python/feeder.py` | Runs both and sends `telemetry` + `alert` messages |
| HUD | `Assets/SourceFiles/Scripts/EnvironmentHUD.cs` | Alert cards, event banners, beeps (desktop) |
| Virtual reality (VR) HUD | `Assets/SourceFiles/Scripts/VR/EnvironmentHUDVR.cs` | The same in a headset, plus the environment panel *(branch `vr-quest`)* |
| Dust storm | `Assets/SourceFiles/Scripts/DustStormEffect.cs` | Fog, orange tint and blowing dust on the `dust_gust` event (G = test) |

Preview the whole demo without Unity or a server:

```
python Python/feeder.py --timeline
```

## What is real and what is simulated

The replayed sol (Curiosity sol 4560, 09:00–13:00 Mars time) was calm. There was no dust storm and no solar storm.

**Real data (sol 4560):**
- **Radiation Assessment Detector (RAD) dose rate:** about 6 µGy/h.
- **Rover Environmental Monitoring Station (REMS) pressure and temperature.**
- **REMS UV**, six bands, taken from the calibrated RNV product.
  - `fetch_sol.py` now downloads these too.
  - The RMD product used for temperature and pressure marks midday UV as unknown (−999). That is why UV comes from RNV instead.

**Simulated (scripted in `events.json`):**
- **A local dust front.** It dims the real UV, makes the pressure dip, and cools the air.
- **A solar particle event (SPE).** It adds to the real dose rate.

Both are shaped on measured Mars events (sources below). While one of them is changing the data, the telemetry has `"scenario": "dust_front"` or `"solar_particle_event"`. Each alert card then carries a **SIMULATED** tag (and the VR environment panel a **SIMULATED** line).

**The detector never sees the script.** It compares each reading with what the real sol measured at the same local time, the way a suit computer would compare against a forecast. Run on the real data alone, it raises only the UV alerts. It raises no dust or radiation alerts, so there are no false alarms.

## Demo timeline (default speed: 300×, the 4-hour EVA in 48 s)

| Real s | EVA (extravehicular activity) clock | Alert | Driven by |
|---|---|---|---|
| 0 | 0:00 | UV BEYOND EARTH RECORD (UV index ~45) | real UV |
| 8 | 0:40 | Note from Earth | script (existing) |
| 15.5 | 1:17 | **DUST WATCH**: UV 6% below normal, dust building | detector on simulated dust |
| 17 | 1:25 | DUST WATCH update: 3.7 Pa pressure dip, dust devil lifting dust | detector |
| 18–19.5 | 1:30–1:37 | **DUST WARNING**, with the estimated storm arrival counted down | detector |
| 20–20.5 | 1:40 | Dust gust (fog and particles); **DUST STORM** (UV −46%) | script plus detector |
| 31 | 2:35 | **UV DANGER**: the dust clears and UV index jumps to ~165 | real UV |
| 33 | 2:45 | DUST CLEARED | detector |
| 35 | 2:55 | Navigation degraded | script (existing) |
| 38 | 3:10 | **RADIATION RISING**: 1.36× normal | detector on simulated SPE |
| 39 | 3:15 | **SOLAR PARTICLE EVENT** confirmed: end the EVA | detector |
| 44.5 | 3:42 | **RADIATION: SHELTER NOW**: 179 µGy/h | detector |

The early warning gives about 23 EVA minutes from DUST WATCH to the storm. Because the timeline is compressed, that is about 5 seconds of the demo.

Change the timing in `events.json` and the thresholds at the top of `alerts.py`. Then run `--timeline` again to check the result.

## How each alert works

### Dust: early warning

**Opacity estimate.** Dust dims sunlight, so the detector turns "UV lower than normal for this time of day" into an estimate of the extra dust opacity:

`extra τ = ln(UV_normal / UV_measured) / 0.4`

It uses the median of the last 5 EVA minutes, which ignores one-off dips. A linear fit over the last 20 minutes gives how fast the opacity is rising, and from that the detector predicts when storm conditions will arrive.

| Level | Trigger | Basis |
|---|---|---|
| DUST WATCH | extra τ ≥ 0.10 and rising, or a pressure dip ≥ 2.5 Pa | 0.10 above baseline is the "large" short-dust-event criterion at Gale (Smith et al., NTRS 20190001691; NTRS is the National Aeronautics and Space Administration's (NASA) Technical Reports Server). A dip of more than 2.5 Pa means a dust devil is likely lifting dust (Mars 2020 Mars Environmental Dynamics Analyzer (MEDA) studies). |
| DUST WARNING | UV down ≥ 30% (τ ≈ +0.9), or storm projected within 30 EVA min | Dust-driven UV changes of more than 30% were seen at Gale (Viúdez-Moreiras et al. 2025, Proceedings of the National Academy of Sciences (PNAS)) |
| DUST STORM | extra τ ≥ 1.5 (total τ ≈ 2, UV down about 45%) | τ ≈ 2 is a localized storm (Cockell et al. 2000). Jezero's 2022 regional storm peaked near 2. |
| Cleared | extra τ < 0.10 for 5 EVA min | |

**UV vs dust.** The relation `UV ∝ exp(−0.4 × extra τ)` is a fit to REMS UV during the 2018 global dust storm:
- UV-ABC fell about 95–97% while τ went from about 0.7 to 8.5.
- That gives k ≈ 0.35–0.46.
- The fit is ours, made from published endpoints.

A total-flux k is smaller than the direct-beam value of about 1 because scattered skylight partly makes up for the lost direct beam.

**The scripted precursor** (`dust_front` in `events.json`):
- Opacity grows quadratically for 35 EVA minutes before the front arrives, reaching +1.5 at arrival and +2.0 at its peak.
- Pressure drops gently by up to 2 Pa.
- Two dust-devil pressure dips occur: 0.8 Pa at 25 minutes before arrival and 3.2 Pa at 15 minutes before. At Gale, dust-devil dips are 0.3–2.9 Pa; Jezero has seen up to about 6.5 Pa.
- The air cools by 3 °C.

These magnitudes follow observed values. **The timing is compressed:** real Gale storm onsets build over sols, not 35 minutes. Sol-scale precursors, such as stronger pressure tides, were not modelled.

**No wind.** The REMS wind sensor has returned no data since about sol 1500, so there is no wind reading.

### Radiation

**Baseline.** The real sol's RAD detector E dose rate at the same time, about 6 µGy/h. Detector E is close to human tissue.

| Level | Trigger | Basis |
|---|---|---|
| RADIATION RISING | dose rate ≥ 1.25× normal | The RAD team's SPE trigger at the Mars surface: 15-min detector E data, 1.25× background (Löwe et al. 2025, arXiv:2502.02469) |
| SOLAR PARTICLE EVENT | still ≥ 1.25× after 5 EVA min (confirmed), or ≥ 2× normal | Löwe et al.: requiring two consecutive samples gives 0% false alarms. The 2× level is our demo choice, based on Sept 2017, when the rate doubled within about 7 h (Zeitlin et al. 2018). |
| SHELTER NOW | ≥ 150 µGy/h (2.5 µGy/min) | The Orion shelter analysis by NASA's Space Radiation Analysis Group, SRAG (Semones, at WRMISS-26, the 26th Workshop on Radiation Monitoring for the International Space Station): crews stay sheltered above this |
| Cleared | < 1.25× normal for 10 EVA min | |

**The scripted SPE** (`solar_particle_event`) is shaped on **20 May 2024**, the largest event Curiosity has measured at the surface:
- **Peak:** about 340 µGy/h (8,100 µGy/day), reached 53 minutes after onset (RAD team, Hassler et al. WRMISS-27; Löwe et al.).
- **Duration:** it stays elevated for about 42 hours.
- **Total dose:** about 1.7–2 mGy.

The script reaches a 334 µGy/h peak 53 minutes after onset, then declines slowly. The SRAG and Office of the Chief Health and Medical Officer (OCHMO) guidance behind the alert texts:
- shelter set-up within about 30 minutes;
- ending an EVA within the first 1–2 hours avoids most of an event's dose;
- stay within 30 minutes of shelter.

**Honest context:**
- **Dose is not near any limit.** Even the May 2024 event was about 1% of NASA's 250 mSv short-term SPE limit (NASA-STD-3001). At the Mars surface, the alerts follow ALARA (keep dose As Low As Reasonably Achievable); they are not about reaching a limit.
- **The 150 µGy/h level is borrowed.** It is a deep-space level for crews inside Orion, used here as an analogy.

### UV

**Estimated UV index.** This is our estimate; there is no published "Mars UV index".
1. Convert each REMS RNV channel to its full-band value. The factors are UV-C ×2.2, UV-B ×3.2 and UV-A ×5.5. We got them by comparing this sol's readings with the corrected Gale band averages in Viúdez-Moreiras et al. 2025 (PNAS). They are not a published calibration.
2. Weight each band by the International Commission on Illumination (CIE) sunburn (erythemal) action spectrum: UV-C ×1.0, UV-B ×0.5, UV-A ×0.002.
3. UV index = 40 × the weighted sum in W/m² (World Health Organization (WHO) definition).

The result for Gale is about 45 at 09:00 and about 175 at 11:00. That is consistent with Cockell et al. 2000, who found Mars sunburn UV about 25× Earth's.

| Level | Trigger | Basis |
|---|---|---|
| UV EXTREME | UV index ≥ 11 | WHO "extreme" |
| UV BEYOND EARTH RECORD | ≥ 43 | Highest UV index recorded on Earth: 43.3 at Licancabur, 2003. The record is disputed. |
| UV DANGER | ≥ 150 | Gale's typical midday level. Bare skin gets 1 sunburn dose (100 J/m²) in under 30 s. |

**Hysteresis.** The level steps down only after UV stays below 80% of the threshold for 20 EVA minutes.

**Inside the suit.** An intact suit blocks this UV, so the alert texts are about visor filters, seals and tears.

**UV-C.** It never reaches the ground on Earth but does on Mars, and the HUD says so.

## On screen

**Desktop** (`EnvironmentHUD.cs`):
- **Alert cards** sit at the top centre, one per hazard, most severe first.
  - **Colours:** advisory yellow, warning orange, danger red and pulsing.
  - **Flash:** a card flashes for 2 s when it appears or gets worse, and a short beep plays at the same moment.
  - **Tag:** each card says whether it comes from **real data** or a **simulated** event.
  - **Countdown:** a dust warning counts the storm's arrival down on the EVA clock.
- **Banners** under the cards show EVA events, notes from Earth and cleared alerts for 6 s.
- **Environment panel**, under the navigation panel (top-right). On `main` this desktop panel was removed (`740b21e`); the VR HUD on `vr-quest` keeps it:
  - shows the EVA clock and Mars time, radiation (and its normal level), EVA dose, UV index, dust, pressure and air temperature;
  - each value is coloured by its alert level;
  - a **SIMULATED** line appears while a scenario is changing the data;
  - it's hidden while the EVA map (M) is open.
- **When the EVA finishes**, every alert card and banner clears. An "EVA COMPLETE" banner shows the dose for 6 s.

**In VR** (`VR/EnvironmentHUDVR.cs`, branch `vr-quest`): a headset doesn't draw the desktop's Immediate Mode Graphical User Interface (IMGUI), so the same cards, banners and panel are rebuilt on the head-locked VR HUD sheet, in the same places (top centre; under the navigation panel). The cards have to stay above the centre of your view, so they're fitted in this order:
1. **Title lines first.** Every alert and banner gets at least its title line.
2. **"+N more" always fits.** Anything that doesn't fit is counted in a "+N more" line, and the least important title lines make way for it.
3. **The newest beep opens first.** Cards then open up in full: the alert that just beeped, then the most severe, the banners, the rest.
4. **The top card shrinks to make room.** While others are waiting, it's cut to three lines.

A beep is never left without something on screen. See [VR_QUEST.md](VR_QUEST.md).

**Dust storm visuals** (`DustStormEffect.cs`): when the `dust_gust` event arrives (20 s into the demo, lasting 11 s):
- orange fog rolls in: density 0.03, so ground beyond about 80 m is hidden;
- the screen takes a 42% orange tint;
- dust streaks blow past the camera, up to 2,200 per second (fewer in VR).

## Messages (WebSocket, JSON)

**Telemetry fields added:**
- `uv_a_wm2`, `uv_b_wm2`, `uv_c_wm2`, `uv_abc_wm2`, `uv_d_wm2`, `uv_e_wm2`: real REMS bands, dimmed when the dust scenario is active.
- From the detector, these are estimates: `uv_index`, `dust_tau`, `pressure_anomaly_pa`, `dose_expected_uGy_h`.
- `scenario`: the active scripted scenarios, comma-separated.

**New message:**

```json
{"v":1, "type":"alert", "t_eva_s":5100.0, "category":"dust", "level":"warning",
 "title":"DUST WARNING", "text":"Dust storm approaching: ...", "update":false,
 "value":0.69, "eta_min":24}
```

- `level` is `advisory`, `warning`, `danger` or `clear`. `clear` removes the card.
- `update: true` means the level is the same with new details, so no beep.
- `eta_min` is the predicted storm arrival in EVA minutes from `t_eva_s`. The HUD counts it down on the EVA clock.

Event `duration_min` (EVA minutes) is converted to `duration_s` (real seconds) for Unity. That way the dust gust lasts as long as the scripted dust at any replay speed.

**Replay speed.** Each telemetry sample covers `--tick × speed / 60` EVA minutes, which is 2.5 at the default. At very high `--speed` the detector sees few samples and may skip a level, so lower `--tick` when using high speeds.

## Sources

**Radiation**
- NASA/Jet Propulsion Laboratory (JPL), *NASA Watches Mars Light Up During Epic Solar Storm* (June 2024). https://www.jpl.nasa.gov/news/nasa-watches-mars-light-up-during-epic-solar-storm
- Hassler et al., *A Solar Cycle of Radiation Measurements on the Surface of Mars* (WRMISS-27 slides). Source of the May 2024 peak of about 8,100 µGy/day. https://wrmiss.org/workshops/twentyseventh/Hassler.pdf
- Löwe et al. 2025, *Nowcasting Solar Energetic Particle Events for Mars Missions* (preprint). Source of the 1.25× trigger, the 53-minute time to peak and the 30-minute rule. https://arxiv.org/abs/2502.02469
- Zeitlin et al. 2018, Geophysical Research Letters (GRL): the September 2017 SPE at Gale. https://elib.dlr.de/120101/1/ME-SBA-2018-Zeitlin_et_al-2018-Geophysical_Research_Letters.pdf
- Semones (NASA SRAG), WRMISS-26: Orion Hybrid Electronic Radiation Assessor (HERA) shelter criteria analysis. https://wrmiss.org/workshops/twentysixth/Semones.pdf
- NASA SRAG model requirements: SPE definitions and EVA response. https://srag.jsc.nasa.gov/SRAGFiles/Publications/SRAG_Model_Requirements.pdf

**UV and dust**
- REMS reduced data record (RDR) software interface specification (SIS): UV band definitions. https://atmos.nmsu.edu/PDS/data/mslrem_1001/DOCUMENT/REMS_RDR_SIS.TXT
- Viúdez-Moreiras et al. 2025, PNAS: UV irradiance at Gale. https://pmc.ncbi.nlm.nih.gov/articles/PMC12130865/
- Cockell et al. 2000, Icarus: the Martian UV environment. https://faculty.washington.edu/dcatling/papers_mine/Cockell_Catling2000.pdf
- CIE erythemal action spectrum. https://woudc.org/archive/Documentation/www/action_spectra_e.html
- Smith et al.: REMS UV dust opacity and short dust events. https://ntrs.nasa.gov/api/citations/20190001691/downloads/20190001691.pdf
- Viúdez-Moreiras et al.: the 2018 global dust storm at Gale. https://www.inta.es/export/sites/default/.galleries/cpess6-Descarga/Viudez-Moreiras-D_The-Impact-of-the-MY34-2018-Global-Dust-Storm-on-the-near-surface-atmosphere-in-Gale-Crater-Mars.pdf
- Dust devils and storms seen by Mars 2020 MEDA: https://pmc.ncbi.nlm.nih.gov/articles/PMC9132482/ and https://pmc.ncbi.nlm.nih.gov/articles/PMC9540647/
