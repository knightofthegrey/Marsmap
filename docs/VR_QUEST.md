# Running Marsmap on a Meta Quest 3

> **Status:** done. The team recorded its demo from the `vr-quest` branch, in VR on a **Meta Quest 3**, built on a Mac. The steps below are the ones that worked, including the pitfalls we hit. On desktop (no headset) everything behaves as before; the scripts switch to VR by themselves when a headset is running.

## Contents

1. [What changes in a headset](#what-changes-in-a-headset)
2. [Controls](#controls)
3. [Setup (Quest 3 + Mac), step by step](#setup-quest-3--mac-step-by-step)
4. [Running the demo](#running-the-demo)
5. [Settings file](#settings-file)
6. [Handy commands](#handy-commands)
7. [Troubleshooting](#troubleshooting)
8. [Quest 1 (untested)](#quest-1-untested)

## What changes in a headset

- **Camera:** the headset moves it; Cinemachine and mouse look switch off. (`VR/VRRig.cs`)
- **Movement:** the left stick walks where you're looking (left grip to sprint, 2×), and the right stick snap-turns 30°. Jumping is off by default, for comfort. (`CharacterMover.cs`, `VR/VRRig.cs`)
- **HUD:** IMGUI and Screen Space canvases aren't drawn inside a headset, so VR has its own versions on one head-locked **HUD sheet**. It sits 1.5 m in front of you, spans ±25° (16:9), and is laid out like the desktop screen (`VR/VRHud.cs`):

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
  - **Alert cards** (dust, radiation, UV) work like desktop, with the same beeps (`VR/EnvironmentHUDVR.cs`):
    - at most 3 alerts and 2 banners at once;
    - each gets at least its title line;
    - the alert that just beeped opens up first;
    - "+N more" counts any that don't fit;
    - they clear when the EVA ends.
  - **Environment panel:** radiation, UV, dust, pressure and temperature, under the navigation panel.
  - **Minimap:** bottom-left with the "upcoming hills" profile to its right (`VR/MinimapVR.cs`). The layer label sits above it (`TerrainOverlay.cs`).
  - **Drawing order:** everything on the sheet draws on top of the world, so nearby ground never cuts through it. The dust storm has a VR screen tint (`DustStormEffect.cs`).
- **EVA map:** a laser from the right controller drives the hologram (`VR/MapViewVR.cs`); see below.
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
   - In Unity Hub: Installs → 6000.6.9f1 → ⋯ → **Add modules** → **Android Build Support**, with **OpenJDK** and **Android SDK & NDK Tools** ticked.
   - **Restart Unity** afterwards. Until then, Build Profiles shows "No Android module loaded" and the Android, Meta Quest and Android XR platforms are greyed out.
2. **Developer mode on the headset.**
   - In the Meta Horizon phone app: Devices → your Quest 3 → Headset settings → **Developer mode** on. This needs a free Meta developer account.
   - Plug the headset into the Mac with USB-C, put it on, and accept **Allow USB debugging**.

**In Unity:** these settings are already committed on this branch. Check them if you start from a fresh copy or a different branch.

3. **Packages:** **XR Plug-in Management** and **OpenXR**. They're in `Packages/manifest.json`; install them through the Package Manager if they're missing.
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
   - Texture compression: **ASTC**.
7. **Player Settings → Android → Other Settings:**

   | Section | Setting | Value |
   |---|---|---|
   | Configuration | Scripting Backend | **IL2CPP** |
   | Configuration | Active Input Handling | Input System Package (New) |
   | Identification | Package Name | `com.marsmap.eva` |
   | Identification | Minimum API Level | Android 10 (API 29) |
   | Identification | Target API Level | Automatic |
   | Android Application Configuration (scroll down) | Target Architectures | **ARM64** only |
   | Android Application Configuration | Internet Access | **Require** (needed for server mode) |
   | Rendering | Graphics APIs and Color Space | Defaults are fine (Vulkan first, Linear) |

8. **Project Validation** (XR Plug-in Management → Project Validation, Android tab): click **Fix All** until nothing is left.

**Every build:**

9. Plug in the headset. In **File → Build Profiles**, click **Build And Run** (Cmd + B). Save the `.apk` to a folder outside `Assets`, e.g. `Builds/`.
   - The first build takes 5–15 minutes; later builds take a few.
   - The app launches on the headset when the build finishes. Later you'll find it under **App Library → Unknown Sources**.

## Running the demo

### Offline (no laptop needed)

The bundle in `Assets/StreamingAssets/Marsmap/` (about 5 MB) is packed into the app. It holds the HiRISE site (terrain plus the photo, slope and thermal layers) and the 48-second EVA replay.

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
| `ar_passthrough` | false (true on this branch) | AR on a Quest 3: the room through the passthrough camera, with only the HUD and the EVA map drawn. See [AR mode](#ar-mode-quest-3-passthrough) |

## AR mode (Quest 3 passthrough)

With `"ar_passthrough": true` the headset shows your real room, and only Marsmap's UI floats in it:

- **Shown:** the head-locked HUD (compass, navigation, alerts, minimap, EVA route).
- **Shown when the map is open:** the EVA map hologram with its pins and labels, the panel beside it, and the laser.
- **Hidden:** the Mars terrain, sky, dust, and the beams and route drawn on the ground.

The EVA keeps running underneath, so the compass, waypoints and alerts work as in VR.

The EVA map works exactly as in VR:
- **Menu** on the left controller opens and closes it (or click the left stick to open).
- Point the laser at the hologram and tap the trigger to add a waypoint.
- Hold the trigger and drag to turn and tilt it; the right stick turns and zooms it.
- The left stick moves the area shown.
- The panel buttons work as in VR.

In AR the hologram is kept 30 cm above your real floor, not above the hidden Mars hills.

### Hands instead of controllers

Put the controllers down and the Quest switches to hand tracking. This works in AR and in VR, though in VR you can't see your hands.

Everything on the map works with hands:

| Do | Hands |
|---|---|
| Open or close the map | **Left hand palm up**, fingers open, look at it and hold ~0.7 s. A tag on your palm fills up; drop the pose before it can toggle again. (Not a pinch: palm-toward-you + pinch is the Quest's own menu gesture.) |
| Point | A laser comes out of your **right hand** (the left if the right isn't tracked), aimed from your shoulder through your knuckles, like the Quest's own hand rays |
| Add a waypoint | Point at the hologram and **pinch** (thumb + index), then let go |
| Remove a waypoint | Point at its pin (the laser turns red) and pinch |
| Panel buttons | Point at one and pinch: Top-down, Centre on me, Undo, Clear, Return to Home Base, Close |
| Turn and tilt | **Pinch and hold** with either hand, then move it: sideways turns (30 cm ≈ 90°), up or down tilts |
| Zoom | Pinch with **both hands** and spread them apart to zoom in, or bring them together to zoom out |
| Slide the area shown | Pinch with both hands and move them together: the terrain moves with them |

A pinch you let go before the hand moves 2 cm is a tap (add, remove, press); otherwise it's a drag, like the controller trigger.

The speeds are on the MapView component: `handGestureSeconds`, `handTurnDegreesPerMetre`, `handTiltDegreesPerMetre`.

Hand tracking needs two settings, which the editor sets automatically (or via **Marsmap → AR: Set Up Quest Passthrough**):
- OpenXR → Android → **Hand Tracking Subsystem** on;
- Meta XR → Project Config → **Hand Tracking Support = Controllers And Hands**, which adds the hand-tracking permission to the manifest.

Hand tracking must also be on in the headset: Settings → Movement tracking → Hand and body tracking.

**How it works:**
- `ARView.cs` starts Meta's passthrough: an `OVRManager` with Insight Passthrough on, plus an `OVRPassthroughLayer` underlay (`AR/Meta/MetaPassthrough.cs`). It uses yours if the scene already has them.
- Every camera then clears to see-through black and draws only the UI layer. The HUD and map objects are moved onto that layer as they appear.
- HDR is switched off on those cameras, because URP's 32-bit HDR buffer has no alpha channel.
- **Fallback:** if passthrough can't start, the app stays in normal VR, and the log says why (`adb logcat -s Unity`, look for `[ARView]`). This happens if the SDK or a setting is missing, or the headset has no passthrough camera (on a Quest 2, AR runs with its greyscale passthrough).

**Setup (once, in the editor):**
1. `Packages/manifest.json` already lists the **Meta XR Core SDK** (`com.meta.xr.sdk.core` 207.0.1, from Meta's registry `npm.developer.oculus.com`). Unity installs it when the editor next gets focus.
2. When it has installed, the editor sets the two required settings automatically and logs `[Marsmap AR]` in the Console:
   - **OpenXR → Android → Meta XR Feature** on;
   - **Meta XR → Project Config → Passthrough Support = Supported**, which adds the passthrough feature (`com.oculus.feature.PASSTHROUGH`) to the Android manifest.
   To apply them again: **Marsmap → AR: Set Up Quest Passthrough**.
3. If Meta's **Project Setup Tool** (Edit → Project Settings → Meta XR) lists fixes for Android, apply them.
4. **Player Settings → Android:** Minimum API Level **32** (Meta's SDK requires it), package `com.marsmap.eva`, Internet Access **Require**. These are already set on this branch.
5. Build and run on the Quest 3 as usual.

**Testing in the editor:** on a Mac there's no Quest Link, so AR can only be tried in an APK. On desktop Play mode AR mode does nothing, and the game runs as before.

The AR code only compiles when the Meta SDK is installed (`AR/Meta/*.asmdef`), so a checkout without it still builds and runs in VR.
The desktop editor also reads this address. With it pointing at another machine, desktop Play only gets the real terrain if that server is running with `--host 0.0.0.0`.

## Settings file

`Assets/Resources/marsmap_config.json`, read at startup (in the editor and in headset builds):

| Key | Default in code | Committed on this branch | Meaning |
|---|---|---|---|
| `server_url` | `ws://127.0.0.1:8765` | `ws://10.0.200.213:8765` | Server address; in a headset, the laptop's Wi-Fi IP |
| `mode` | `auto` | `auto` | `auto`: the server if it answers within `offline_after_s`, else the bundle (the fallback only happens in a headset). `server`: always the server. `offline`: always the bundle, on desktop too |
| `offline_after_s` | 2.5 | 2.5 | Auto mode: seconds to wait for the server |
| `offline_auto_start_s` | 0 | 0 | Offline: 0 = start the EVA with X / Enter; otherwise start automatically this many seconds after the terrain loads |
| `vr_move_speed` | 1.5 | **3** | Walking speed in VR (m/s); fast smooth movement can cause motion sickness |
| `vr_sprint_multiplier` | 2 | 2 | Left-grip sprint multiplier |
| `snap_turn_degrees` | 30 | 30 | Right-stick snap turn |
| `allow_jump_in_vr` | false | false | A button jumps |

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
| Stutter | Quest GPU | Lower the URP render scale; turn off shadows and post-processing |
| AR: Mars shows instead of the room | Passthrough didn't start, so it fell back to VR | Look for `[ARView] Staying in VR:` in `adb logcat -s Unity`; run **Marsmap → AR: Set Up Quest Passthrough** and rebuild |
| AR: black instead of the room | Something draws an opaque background | Turn post-processing off on the camera; check no other camera draws to the headset |

## Quest 1 (untested)

The team only used a Quest 3.
- **Support:** Unity 6 and Meta's current SDKs officially dropped the original Quest. People report Unity 6 + OpenXR apps crashing at startup on it, or losing their controllers.
- **What one developer did:** they got Unity 6.0 running with OpenXR 1.13.2, but their URP project crashed.
- **If you try:** make a separate build whose target-device list is Quest 1 only (a list that includes Quest 3 was reported to crash a Quest 1). Keep any older OpenXR version off this branch.

Sources: [Unity and Quest 1 deprecation](https://discussions.unity.com/t/unity-and-quest-1-deprecation/932329) · [Can't build for Quest 1 with Unity 6](https://discussions.unity.com/t/cant-build-my-game-for-quest-1-with-unity-6/1554923) · [OpenXR 1.12 crash on Quest](https://discussions.unity.com/t/openxr-v-1-12-app-crash-at-start-on-meta-quest/1538670)
