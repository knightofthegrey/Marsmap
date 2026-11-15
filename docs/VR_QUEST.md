# Running Marsmap on a Meta Quest 3

> **Branches:** the virtual reality (VR) code is on **`vr-quest`**, and augmented reality (AR) mode is on **`vr-quest-ar`** (`vr-quest` plus passthrough). Both are being merged into `main`; until then, check out one of them to build for the headset.
>
> **Status:** done. The team recorded its demo from the `vr-quest` branch, in VR on a **Meta Quest 3**, built on a Mac. The steps below are the ones that worked, including the pitfalls we hit. On desktop (no headset) everything behaves as before; the scripts switch to VR by themselves when a headset is running.

## Contents

1. [What changes in a headset](#what-changes-in-a-headset)
2. [Controls](#controls)
3. [Setup (Quest 3 + Mac), step by step](#setup-quest-3--mac-step-by-step)
4. [Running the demo](#running-the-demo)
5. [AR mode (passthrough)](#ar-mode-passthrough)
6. [Settings file](#settings-file)
7. [Handy commands](#handy-commands)
8. [Troubleshooting](#troubleshooting)
9. [Quest 1 (untested)](#quest-1-untested)

## What changes in a headset

- **Camera:** the headset moves it; Cinemachine and mouse look switch off. (`VR/VRRig.cs`)
- **Movement:** the left stick walks where you're looking (left grip to sprint, 2×), and the right stick snap-turns 30°. Jumping is off by default, for comfort. (`CharacterMover.cs`, `VR/VRRig.cs`)
- **Heads-up display (HUD):** Immediate Mode Graphical User Interface (IMGUI) and Screen Space canvases aren't drawn inside a headset, so VR has its own versions on one head-locked **HUD sheet**. It sits 1.5 m in front of you, spans ±25° (16:9), and is laid out like the desktop screen (`VR/VRHud.cs`):

  ```text
   compass              alert cards              navigation
   (top-left)           (top centre)             environment panel
                                                 (top-right)

                   (centre kept clear for walking)

   layer label
   minimap + upcoming hills                      EVA route
   (bottom-left)                                 (bottom-right)
  ```

  - **Compass and nav:** the compass, its arrows and the nav readout follow your head, not your body.
  - **Alert cards** (dust, radiation, ultraviolet (UV)) work like desktop, with the same beeps (`VR/EnvironmentHUDVR.cs`):
    - at most 3 alerts and 2 banners at once;
    - each gets at least its title line;
    - the alert that just beeped opens up first;
    - "+N more" counts any that don't fit;
    - they clear when the extravehicular activity (EVA) ends.
  - **Environment panel:** radiation, UV, dust, pressure and temperature, under the navigation panel.
  - **Minimap:** bottom-left with the "upcoming hills" profile to its right (`VR/MinimapVR.cs`). The layer label sits above it (`TerrainOverlay.cs`).
  - **Drawing order:** everything on the sheet draws on top of the world, so nearby ground never cuts through it. The dust storm has a VR screen tint (`DustStormEffect.cs`).
- **EVA map:** a laser from the right controller drives the hologram (`VR/MapViewVR.cs`); see below.
- **AR mode:** click the right stick to hide the 3D world and see your real room behind the HUD (passthrough); the EVA map still works. See [AR mode (passthrough)](#ar-mode-passthrough). (`VR/PassthroughView.cs`)
- **Offline mode:** the terrain, map images and EVA replay can be packed into the app, so the headset needs no laptop. (`OfflineReplay.cs`, `Python/export_offline.py`)
- **Performance:** terrain detail is a bit lower, the view distance is capped at 4 km, and there are fewer dust particles.

## Controls

| Action | Quest 3 controller | Desktop |
|---|---|---|
| Walk | Left stick (where you look) | W A S D / arrow keys |
| Look | Turn your head | Mouse |
| Turn | Right stick left/right (30° snaps) | Mouse |
| Sprint | Hold left grip | Hold Left Shift |
| Next map layer | B | L |
| Test dust gust | Y | G |
| Start EVA replay (offline mode) | X | Enter |
| Jump | A (only if `allow_jump_in_vr` is true) | Space |
| Minimap zoom | Right stick up / down | + / − |
| Open / close the EVA map | Menu button (left controller); clicking the left stick also opens it | M |
| AR mode on / off: the HUD over the real room, no 3D world ([more](#ar-mode-passthrough)) | Click the right stick (with the stick at rest) | P (black background) |

### EVA map (route planning) in the headset

Press the **Menu** button on the left controller.
- **The hologram:** the terrain appears about 1.3 m in front of you at table height.
- **The panel:** to its left, it shows the route numbers, help and buttons.
- **The laser:** the right controller casts it, and a dot shows where it lands.

The trigger works like the mouse on desktop: **tap** to add a waypoint, **hold and drag** to turn and tilt the hologram.

| Action | Quest 3 controller | Desktop |
|---|---|---|
| Add a waypoint | Point at the hologram, tap the right trigger (pull and let go) | Click |
| Turn / tilt the hologram | Point at it, **hold the right trigger and drag**: sweep sideways to turn (the part you grabbed follows the laser), up / down to tilt. A 30° flick of the wrist turns it about 90° | Drag |
| Remove a waypoint | Point at its pin (the laser turns red), press A | Shift + click |
| Turn / zoom | Right stick left-right / up-down | Q E / scroll |
| Move the area shown | Left stick | W A S D / right-drag |
| Top-down view, Centre on me, Undo last, Clear all, Return home on/off, Close map | Point at the panel button, pull the trigger | T, F, Backspace, C, R, M |

- **Tap or drag:** a pull counts as a drag once the laser turns more than 2.5°, or where it lands slides 3 cm sideways. Let go before that and it's a tap. The waypoint goes where you first pointed, so a wobble as you let go doesn't move it.
- **While the map is open:** you can't walk and snap turn is paused, because the sticks drive the map. The compass, nav panel, minimap and route label hide to make room for the hologram; the alert cards stay up.
- **Route planning:** the A* planner runs in `Python/server.py`. In **server mode** the headset gets planned routes. In **offline mode** there is no server, so the map draws straight lines between waypoints, in magenta.

## Setup (Quest 3 + Mac), step by step

**One time:**

1. **Android module.**
   - In Unity Hub: Installs → 6000.6.9f1 → ⋯ → **Add modules** → **Android Build Support**, with **OpenJDK** (an open-source Java Development Kit) and **Android SDK & NDK Tools** (the Android software and native development kits) ticked.
   - **Restart Unity** afterwards. Until then, Build Profiles shows "No Android module loaded" and the Android, Meta Quest and Android XR (extended reality) platforms are greyed out.
2. **Developer mode on the headset.**
   - In the Meta Horizon phone app: Devices → your Quest 3 → Headset settings → **Developer mode** on. This needs a free Meta developer account.
   - Plug the headset into the Mac with USB-C (Universal Serial Bus Type-C), put it on, and accept **Allow USB debugging**.

**In Unity:** these settings are already committed on this branch. Check them if you start from a fresh copy or a different branch.

3. **Packages:** **XR Plug-in Management** and **OpenXR (the open standard for VR and AR devices)**. They're in `Packages/manifest.json`; install them through the Package Manager if they're missing.
4. **Turn OpenXR on for Android.** This is the step that's easy to miss.
   - Go to **Edit → Project Settings → XR Plug-in Management** (the top-level page, not the "OpenXR" page under it).
   - On the **Android tab** (robot icon), tick **OpenXR** under *Plug-in Providers*. Also tick **Initialize XR on Startup**.
   - If OpenXR isn't ticked here, the app starts as a **flat 2D window**: no head tracking, the sticks and buttons do nothing, and the desktop UI shows. You can still configure the OpenXR sub-page without it being on, which is why it's easy to miss.
5. **OpenXR settings (Android tab):**
   - **Meta Quest Support:** tick it, then click its gear icon and make sure **Quest 3** is a target device.
   - **Enabled Interaction Profiles:** click the **+** under the list and add **Oculus Touch Controller Profile** and **Meta Quest Touch Plus Controller Profile**.
6. **Build Profiles** (File → Build Profiles):
   - Select **Android** (or **Meta Quest**), then click **Switch Platform**.
   - Set **Run Device** to your Quest 3; click Refresh if it isn't listed.
   - Texture compression: **Adaptive Scalable Texture Compression (ASTC)**.
7. **Player Settings → Android → Other Settings:**

   | Section | Setting | Value |
   |---|---|---|
   | Configuration | Scripting Backend | **IL2CPP** (Intermediate Language To C++) |
   | Configuration | Active Input Handling | Input System Package (New) |
   | Identification | Package Name | `com.marsmap.eva` |
   | Identification | Minimum API Level (API: application programming interface) | Android 10 (API 29); **Android 12L (API 32)** for [AR mode](#ar-mode-passthrough) |
   | Identification | Target API Level | Automatic |
   | Android Application Configuration (scroll down) | Target Architectures | **ARM64 (the 64-bit ARM processor architecture)** only |
   | Android Application Configuration | Internet Access | **Require** (needed for server mode) |
   | Rendering | Graphics APIs and Color Space | Defaults are fine (Vulkan first, Linear) |

8. **Project Validation** (XR Plug-in Management → Project Validation, Android tab): click **Fix All** until nothing is left.

**Every build:**

9. Plug in the headset. In **File → Build Profiles**, click **Build And Run** (Cmd + B). Save the `.apk` to a folder outside `Assets`, e.g. `Builds/`.
   - The first build takes 5–15 minutes; later builds take a few.
   - The app launches on the headset when the build finishes. Later you'll find it under **App Library → Unknown Sources**.

## Running the demo

### Offline (no laptop needed)

The bundle in `Assets/StreamingAssets/Marsmap/` (about 5 MB (megabytes)) is packed into the app. It holds the High Resolution Imaging Science Experiment (HiRISE) site (terrain plus the photo, slope and thermal layers) and the 48-second EVA replay.

1. Open the app. With `"mode": "auto"` it waits 2.5 s for the server, then uses the bundle. `"mode": "offline"` skips the wait.
2. Press **X** to start the EVA.

> **Regenerate the bundle before you build.** The bundle committed on this branch was exported before the alert system, so its replay has no alerts and no UV readings. After any change to the site, the sol data or `events.json`, export it again, then rebuild:
>
> ```bash
> python3 Python/export_offline.py              # the HiRISE site (default; right size for a Quest)
> python3 Python/export_offline.py --demo       # or the fake demo terrain
> ```
>
> The big tiled Gale map can be exported too, with `--polygon`, but it is hundreds of MB. On a tiled map, **B** switches between photo and height colours. There's no floating layer label for tiles in VR.

### Live from the laptop (server mode)

This mode gives you route planning and the latest alerts and readings.

1. **Find the laptop's address** on the network you'll use:
   - run `ipconfig getifaddr en0`, or
   - read the line `server.py` prints at startup (`Headset on the same Wi-Fi: set server_url … to ws://…:8765`).
2. **Put it in `Assets/Resources/marsmap_config.json`** as `"server_url": "ws://<address>:8765"`, then **rebuild**. The config is packed into the app, so a new address needs a new build.
3. **Same network.**
   - Put the laptop and headset on the same Wi-Fi. A phone hotspot is safest, because venue Wi-Fi often blocks devices from reaching each other.
   - Click **Allow** when macOS asks whether Python can accept incoming connections.
4. **Start the server before opening the app.**
   ```bash
   python3 Python/server.py --site --host 0.0.0.0
   ```
   - **Why the order matters:** in auto mode the app waits 2.5 s for the server, then **stays offline for that run**. If you started the server late, quit the app and open it again (no rebuild needed).
   - **Why `--host 0.0.0.0`:** without it the server only accepts connections from the laptop itself.
5. **Open the app**, wait until you see Mount Sharp, then start the EVA:
   ```bash
   python3 Python/feeder.py
   ```

**The committed config points at the event laptop.** `marsmap_config.json` on this branch has `server_url` set to `ws://10.0.200.213:8765`, the laptop used at the event. Change it for your own setup.

The desktop editor also reads this address. With it pointing at another machine, desktop Play only gets the real terrain if that server is running with `--host 0.0.0.0`.

## AR mode (passthrough)

*(branch `vr-quest-ar`)*

AR mode hides the 3D Mars world and keeps the overlay. In the Quest you see **your real room** (the headset's colour passthrough) with the HUD on top. You can still open the EVA map and plan a route on the hologram. On desktop the same mode shows the HUD over a **black** background, as a preview. (`VR/PassthroughView.cs`)

### Turning it on

| How | What happens |
|---|---|
| Click the **right stick** (Quest), with the stick at rest | Switches between AR mode and the 3D world at any time, even with the EVA map open. A click while the stick is pushed (snap turn, zoom) is ignored, so a hard push can't switch it by accident |
| **P** (desktop) | The same, over black |
| `"passthrough": true` in `Assets/Resources/marsmap_config.json` | The app **starts** in AR mode (in the editor too). Rebuild after changing it |

The Unity log says `[PassthroughView] 3D world hidden, HUD only: …` when it turns on and `3D world shown again` when it turns off.

### What's hidden and what stays

| Hidden (comes back when you switch off) | Stays |
|---|---|
| The terrain (drawing only: its data and collider stay, so the minimap, the readouts and the hologram still work, and you still walk on it) | The whole HUD sheet: compass, navigation, environment panel, alert cards, minimap and upcoming hills, layer label, route summary |
| The Mars sky and the fog | The EVA map: hologram, side panel, laser, pins and their numbers |
| Scene objects (e.g. `SampleWaypoint`) | The dust storm's tint and streaks, which blow over the room |
| The markers on the ground: the route line, the next waypoint's beam and ring, Home Base's beam and sphere, and their tags (`MapView`) | Alerts, beeps, the EVA replay and the server connection |

- **Walking:** the left stick still walks you across the (invisible) Mars terrain, so the compass, minimap and readouts follow, but the room doesn't move. The hologram sits at table height above the floor under your feet.
- **Camera:** while AR mode is on, the camera clears to transparent black (0,0,0,0) with high dynamic range (HDR) and post-processing off. The Quest only shows the room where the picture's alpha is below 1, and HDR (R11G11B10, no alpha) and post-processing both drop the alpha. Everything is put back when you switch off.

### One-time setup (headset)

The code is in place. The Quest only shows the room once these project settings are done; until then AR mode shows the HUD over black.

1. **Packages.** `com.unity.xr.arfoundation` **6.6.2** and `com.unity.xr.meta-openxr` **2.6.1** (Unity OpenXR: Meta) are in `Packages/manifest.json`. Both ship inside Unity 6000.6.9f1, so Unity installs them the next time the project opens, even offline. Meta OpenXR also brings `com.unity.xr.compositionlayers` 2.6.1. Leave OpenXR at 1.18.0 and XR Plug-in Management at 4.7.0: these exact versions are what Meta OpenXR 2.6.1 asks for. If they're missing, add them in **Window → Package Manager → + → Add package by name**.
2. **Edit → Project Settings → XR Plug-in Management**, **Android tab**: keep **OpenXR** ticked and also tick **Meta Quest feature group** under it.
3. **XR Plug-in Management → OpenXR**, **Android tab**, the **Meta Quest** feature group:

   | Feature | Setting | Why |
   |---|---|---|
   | Meta Quest Support | on (already) | |
   | **Composition Layers Support** | **on** | Passthrough is a composition layer behind our picture; without it the background stays black |
   | **Meta Quest: Session** | **on** | Required by the camera feature (a Project Validation error otherwise) |
   | **Meta Quest: Camera (Passthrough)** | **on** | The passthrough itself. In its gear menu leave *Passthrough Pre Splash Screen* and camera image support off (image support adds camera permissions) |
   | Meta Quest: Anchors, Planes, Bounding Boxes, Raycasts, Meshing, Occlusion, Colocation Discovery | **off** | Ticking the group switches some on. They aren't used, and they add scene / anchor permissions to the app |
   | Oculus Touch / Meta Quest Touch Plus Controller Profile | on (already) | |

4. **Player Settings → Android → Other Settings → Minimum API Level: Android 12L (API level 32).** It was 29; the camera feature needs 32.
5. **Project Validation** (XR Plug-in Management → Project Validation, Android tab): **Fix All** until nothing is left. The camera feature checks the API level, the Session feature and Vulkan as the first graphics API (it already is).
6. Build and run as usual. No manifest editing is needed: Meta OpenXR adds the `com.oculus.feature.PASSTHROUGH` entry itself.

At runtime, the first time AR mode turns on in a headset, `PassthroughView` adds AR Foundation's **AR Session** (its own object) and **AR Camera Manager** (on the camera) and switches the camera manager on and off with AR mode. It finds them by name, so the project also compiles and runs without the packages. `Assets/link.xml` keeps them in IL2CPP builds. Nothing is added on desktop.

**Play mode over Quest Link** (Windows only) would need the same features on the **Windows** tab and passthrough over Link turned on in the Meta Quest Link app. On a Mac, test AR mode in a build.

### Troubleshooting AR mode

Read the log with `adb logcat -s Unity | grep -i -E "passthrough|ARFoundation|XRCamera" | grep -v TryGetFrame`.

| Symptom | Likely cause | Try |
|---|---|---|
| HUD over black in the headset; log: `Passthrough needs AR Foundation + Unity OpenXR: Meta` | The packages aren't installed | Open the project in Unity so it installs them (step 1), rebuild |
| HUD over black; log: `No passthrough camera …` or `No active UnityEngine.XR.ARSubsystems.XRCameraSubsystem` | The OpenXR features are off | Step 3: **Meta Quest: Camera (Passthrough)**, **Meta Quest: Session** and **Composition Layers Support** on the **Android** tab, rebuild |
| Build fails, or Project Validation shows errors | Minimum API level below 32 | Step 4, then Fix All |
| Black background, log says the passthrough camera is running | Something still draws an opaque background: another camera, or a new object in the world | Only `Camera.main` is set up; check for a second camera drawing to the screen. Scene objects are hidden, but things made at runtime have to hide themselves (see `PassthroughView.Changed`) |
| The 3D world is gone and nothing switches it back | The app started in AR mode | Click the right stick with the stick centred (or P); set `"passthrough": false` and rebuild |
| Log: "TryGetFrame returned false because camera image support is not enabled" | Expected: passthrough doesn't need camera images | Ignore it. Leave **Camera Image Support** off (turning it on adds a headset-camera permission request for nothing) |
| The guardian boundary keeps appearing | Normal for passthrough apps | Optional: the **Meta Quest: Boundary Visibility** feature can suppress it while passthrough shows (not used by the code yet) |

## Settings file

`Assets/Resources/marsmap_config.json`, read at startup (in the editor and in headset builds):

| Key | Default in code | Committed on this branch | Meaning |
|---|---|---|---|
| `server_url` | `ws://127.0.0.1:8765` | `ws://10.0.200.213:8765` | Server address; in a headset, the laptop's Wi-Fi IP (Internet Protocol) address |
| `mode` | `auto` | `auto` | `auto`: the server if it answers within `offline_after_s`, else the bundle (the fallback only happens in a headset). `server`: always the server. `offline`: always the bundle, on desktop too |
| `offline_after_s` | 2.5 | 2.5 | Auto mode: seconds to wait for the server |
| `offline_auto_start_s` | 0 | 0 | Offline: 0 = start the EVA with X / Enter; otherwise start automatically this many seconds after the terrain loads |
| `vr_move_speed` | 1.5 | **3** | Walking speed in VR (m/s); fast smooth movement can cause motion sickness |
| `vr_sprint_multiplier` | 2 | 2 | Left-grip sprint multiplier |
| `snap_turn_degrees` | 30 | 30 | Right-stick snap turn |
| `allow_jump_in_vr` | false | false | A button jumps |
| `passthrough` | false | false | `true`: start in [AR mode](#ar-mode-passthrough) (HUD over the real room on a Quest, black on desktop). P / right-stick click switches either way at any time |

## Handy commands

**adb.** Unity installs its own copy of `adb` (the Android Debug Bridge), but it isn't on your PATH on a Mac. Add it once:

```bash
echo 'export PATH="$PATH:/Applications/Unity/Hub/Editor/6000.6.9f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools"' >> ~/.zshrc
source ~/.zshrc
```

Then, with the headset plugged in:

```bash
adb devices                                 # is the headset connected?
adb shell am force-stop com.marsmap.eva     # quit the app
adb shell monkey -p com.marsmap.eva 1       # open it again (e.g. after starting the server)
adb logcat -s Unity                         # live Unity log; look for "[VRRig] XR active"
adb pull /sdcard/Oculus/Screenshots ~/Desktop/quest-screenshots
```

**In the headset:**
- **Quit:** press the **Meta button** (right controller) → **Quit**.
- **Screenshot:** hold the **Meta button** and press a **trigger**.

**Meta Quest Developer Hub** (Mac app) can also install builds, show logs, and cast the headset view to the Mac for an audience.

## Troubleshooting

| Symptom | Likely cause | Try |
|---|---|---|
| The app is a flat window in front of you; no head tracking; sticks and B do nothing; desktop labels like "L: next layer" | OpenXR isn't turned on for Android | XR Plug-in Management → **Android tab** → tick **OpenXR** (setup step 4), save, rebuild |
| Build Profiles says "No Android module loaded" | Android module missing | Install it from Unity Hub (setup step 1), restart Unity |
| Controllers do nothing (but the view follows your head) | No interaction profile | OpenXR (Android tab) → Enabled Interaction Profiles → **+** → Oculus Touch / Meta Quest Touch Plus |
| `zsh: command not found: adb` | adb isn't on the PATH | Use the full path, or add it ([Handy commands](#handy-commands)) |
| Mount Sharp loads but `feeder.py` changes nothing | The app went offline: the server started late, the IP is wrong, or the Wi-Fi blocks devices | Quit and reopen the app with the server running; check `server_url` (rebuild after changing it); use a phone hotspot. Offline, press X |
| Worked yesterday, not today | The laptop's IP changed (new network) | Update `server_url`, rebuild |
| Random hills instead of Mount Sharp | No server and no bundle | Run `export_offline.py` and rebuild, or check `server_url` / Wi-Fi |
| Alerts beep but nothing shows | An old build (before the VR HUD) | Rebuild from this branch: alerts and the environment panel now show in VR |
| No alerts at all offline | The committed bundle predates the alerts | Run `python3 Python/export_offline.py`, rebuild |
| Stutter | Quest graphics processing unit (GPU) | Lower the Universal Render Pipeline (URP) render scale; turn off shadows and post-processing |

## Quest 1 (untested)

The team only used a Quest 3.
- **Support:** Unity 6 and Meta's current SDKs officially dropped the original Quest. People report Unity 6 + OpenXR apps crashing at startup on it, or losing their controllers.
- **What one developer did:** they got Unity 6.0 running with OpenXR 1.13.2, but their URP project crashed.
- **If you try:** make a separate build whose target-device list is Quest 1 only (a list that includes Quest 3 was reported to crash a Quest 1). Keep any older OpenXR version off this branch.

Sources: [Unity and Quest 1 deprecation](https://discussions.unity.com/t/unity-and-quest-1-deprecation/932329) · [Can't build for Quest 1 with Unity 6](https://discussions.unity.com/t/cant-build-my-game-for-quest-1-with-unity-6/1554923) · [OpenXR 1.12 crash on Quest](https://discussions.unity.com/t/openxr-v-1-12-app-crash-at-start-on-meta-quest/1538670)
