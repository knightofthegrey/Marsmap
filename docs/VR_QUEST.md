# Running Marsmap on a Meta Quest

> **Status (branch `vr-quest`):** the VR code is written and compiles, and the offline bundle matches the live server byte for byte. **Nothing has run on a headset yet.** On desktop (no headset) everything behaves as before.

## What changes in a headset

When a headset is running, the scripts switch to VR on their own; there's nothing to set in the scene:

- **Camera:** the headset moves it. Cinemachine and mouse look are switched off. (`VRRig.cs`)
- **Movement:**
  - left stick walks where you're looking, at 1.5 m/s (left grip to sprint, 2×);
  - right stick snap-turns 30°;
  - jumping is off by default, for comfort. (`CharacterMover.cs`, `VRRig.cs`)
- **HUD:**
  - the compass, arrows and nav readout (Screen Space canvases) become a panel floating 1.3 m in front of you, and follow your head instead of your body;
  - the map-layer label and the dust tint have VR versions. (`VRRig.cs`, `VRHud.cs`, `TerrainOverlay.cs`, `DustStormEffect.cs`)
- **Offline mode:** the terrain, map images and EVA replay are packed into the app, so the headset needs no laptop or Wi-Fi. (`OfflineReplay.cs`)
- **Performance:** terrain detail is a bit lower, the view distance is capped at 4 km, and there are fewer dust particles.

## Controls

| Action | Quest controller | Desktop |
|---|---|---|
| Walk | Left stick | W A S D / arrow keys |
| Look | Turn your head | Mouse |
| Turn | Right stick left/right (30° snaps) | Mouse |
| Sprint | Hold left grip | Hold Left Shift |
| Next map layer | B | L |
| Test dust gust | Y | G |
| Start EVA replay (offline mode) | X | Enter |
| Jump | A (only if `allow_jump_in_vr` is true) | Space |

## Quest 1 warning

Quest 1 is **officially unsupported** by Meta's current SDKs and by Unity 6. Builds can still be sideloaded (installed over USB, not through Meta's store), but:

- People report Unity 6 + OpenXR apps **crashing at startup**, or controllers **not being detected**, on a Quest 1.
- One developer got Unity 6.0 working with **OpenXR 1.13.2**, the **Meta Quest Support** feature group and the **Oculus Touch** controller profile. The same developer reported that the **Universal 3D (URP) template crashed** on their Quest 1, and this project is URP.
- A build whose supported-devices list includes Quest 3 can crash a Quest 1. You may need a separate Quest 1 build.

**Fallbacks:**
- **Any Quest 2, 3, 3S or Pro:** same code, officially supported.
- **PC VR over Quest Link:** needs a Windows PC.
- **The desktop demo.**

Sources:
- [Unity and Quest 1 deprecation](https://discussions.unity.com/t/unity-and-quest-1-deprecation/932329)
- [Can't build for Quest 1 with Unity 6](https://discussions.unity.com/t/cant-build-my-game-for-quest-1-with-unity-6/1554923)
- [OpenXR 1.12 crash on Quest](https://discussions.unity.com/t/openxr-v-1-12-app-crash-at-start-on-meta-quest/1538670)
- [Unity XR support packages](https://docs.unity.com/en-us/engine/6000.6/manual/xr/support/support-packages)
- [Meta: XR Plugin Management](https://developers.meta.com/vr/documentation/unity/unity-xr-plugin/)

## Quest 3 and Quest 1: same code, separate builds

The scripts are identical for every Quest. What differs is the build:

| | Quest 3 / 3S (and 2, Pro) | Quest 1 |
|---|---|---|
| Support | Official (Unity 6 + current OpenXR) | Unofficial: may crash or lose controllers |
| OpenXR package | Latest is fine | May need an older version (one report: 1.13.2) |
| Controller profile | Oculus Touch; optionally also Meta Quest Touch Plus | Oculus Touch |
| Supported devices in the build | Quest 3 / 3S (add 2 / Pro if wanted) | **Quest 1 only.** A list that includes Quest 3 was reported to crash a Quest 1 |
| Performance | Plenty of headroom; render scale 1.0 or higher | Needs the cuts already in the code (render scale 0.8–1.0) |

- **Make one build per headset**, with its own supported-devices setting.
- **Keep the package versions apart.** If the Quest 1 needs an older OpenXR version, keep that change local or on its own branch, not on `vr-quest`, so it doesn't break the Quest 3 build.
- **Get the Quest 3 working first.** It proves the VR code; a Quest 1 failure after that is a Quest 1 support problem, not the project.

## Setup

### Step 0: smoke test (do this first, ~30 min)

Prove the headset can run *any* Unity 6 build before debugging this project:

1. **Unity Hub:** Installs → Unity 6000.6.9f1 → Add modules → **Android Build Support**, including OpenJDK, Android SDK & NDK.
2. **Headset:** turn on **developer mode** (Meta Horizon phone app → your headset → Developer mode; needs a free Meta developer account). Connect it to the computer with USB-C and accept the "Allow USB debugging" prompt in the headset.
3. Do the Unity steps below on an empty scene and **Build And Run**. If it crashes or the controllers don't work, try an older OpenXR package version before going further.

### Unity project settings

1. **Package Manager:** install **XR Plug-in Management** and **OpenXR**. (Not added to this branch yet, so the project still opens without them.)
2. **Project Settings → XR Plug-in Management → Android tab:**
   - tick **OpenXR**;
   - under OpenXR: enable the **Meta Quest Support** feature group, and if it lists target devices, include the headset you have;
   - interaction profiles: add **Oculus Touch Controller Profile**.
3. **File → Build Profiles:** switch to **Android**.
4. **Player Settings → Android:**
   - Scripting Backend **IL2CPP**, Target Architecture **ARM64**;
   - **Internet Access: Require**, needed for server mode;
   - texture compression **ASTC**.
5. **URP quality for a Quest:** in the URP asset used on Android:
   - post-processing off, HDR off;
   - MSAA 4×;
   - shadows off or low;
   - render scale 0.8–1.0.
6. Open `Assets/Scenes/GetStarted_Scene.unity` and **Build And Run** with the headset connected.

## Where the data comes from

The settings file is `Assets/Resources/marsmap_config.json`.

### Offline mode (recommended for a demo)

The bundle in `Assets/StreamingAssets/Marsmap/` is packed into the app, about 5 MB:
- the HiRISE site: terrain plus the photo, slope and thermal layers;
- the 48-second EVA replay.

**In the headset:**
1. If no server answers within 2.5 s (`"mode": "auto"`), the app uses the bundle.
2. Press **X** to start the EVA replay.
3. The dust gust comes about 20 s in.

**After changing the site, sol data or events**, regenerate the bundle, then rebuild the app:

```
python3 Python/export_offline.py              # the HiRISE site (default; right size for a Quest)
python3 Python/export_offline.py --demo       # or the fake demo terrain
```

The big tiled Gale crater map (`server.py` with no arguments) can be exported too, with `--polygon`. It is hundreds of MB and much too heavy for a Quest 1. On a tiled map, **B** switches between photo and height colours, but there's no floating layer label for tiles in VR yet.

### Server mode (live from the laptop)

1. Start the server listening to the network:
   ```
   python3 Python/server.py --site --host 0.0.0.0
   ```
   It prints the address to use, e.g. `ws://192.168.0.48:8765`.
2. Put that address in `server_url` in `marsmap_config.json`, then rebuild.
3. **Network:** the laptop and headset must be on the same Wi-Fi (a phone hotspot is safest; venue Wi-Fi often blocks devices from talking to each other), and macOS must allow incoming connections for Python.
4. Start `python3 Python/feeder.py` to begin the EVA.

### Settings file

| Key | Default | Meaning |
|---|---|---|
| `server_url` | `ws://127.0.0.1:8765` | Server address. In a headset, use the laptop's Wi-Fi IP |
| `mode` | `auto` | `auto`: server if it answers, else the bundle (the bundle fallback only happens in a headset). `server`: always the server. `offline`: always the bundle, on desktop too |
| `offline_after_s` | 2.5 | Auto mode: how long to wait for the server before using the bundle |
| `offline_auto_start_s` | 0 | Offline: 0 = start the EVA with X / Enter; otherwise start automatically after this many seconds |
| `vr_move_speed` | 1.5 | Walking speed in VR (m/s) |
| `vr_sprint_multiplier` | 2 | Left-grip sprint multiplier in VR |
| `snap_turn_degrees` | 30 | Right-stick snap turn |
| `allow_jump_in_vr` | false | A button jumps |

## Troubleshooting

| Symptom | Likely cause | Try |
|---|---|---|
| App crashes on launch (Quest 1) | Unity 6 / OpenXR support for Quest 1 | Older OpenXR version; supported-devices list with only Quest 1; or use a newer Quest |
| Controllers do nothing | Missing interaction profile | Add Oculus Touch Controller Profile under OpenXR |
| View doesn't follow the head | OpenXR not running | Check that OpenXR is ticked for Android; look for `[VRRig] XR active` in the logs (`adb logcat -s Unity`) |
| Random hills instead of Mount Sharp | No server and no bundle | Run `export_offline.py` and rebuild, or check `server_url` / Wi-Fi |
| No dust or events | EVA not started | Offline: press X. Server mode: run `feeder.py` |
| Stutter | Quest GPU | Lower the URP render scale; turn off shadows and post-processing |
