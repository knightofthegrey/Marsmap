using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// Turns the desktop scene into a VR one when a headset is running (OpenXR active).
/// On desktop it does nothing and switches itself off after a few seconds.
///
/// In VR it:
///  - puts Main Camera under an "XR Rig" at the player's feet and lets the headset drive it
///    (TrackedPoseDriver); the Cinemachine brain and mouse look are switched off;
///  - right stick left/right = snap turn around your head (comfort: no smooth turning);
///  - turns Screen Space canvases (compass, nav panel) into a panel floating in front of you;
///  - points the compass, the direction arrows and the nav readout at your head, not your body;
///  - hides the player's own cube so it doesn't block the view;
///  - lowers terrain detail and the far clip a little so a Quest keeps its frame rate.
/// Walking with the left stick (where you look) is in CharacterMover.
/// Created automatically, like PythonConnection.
/// </summary>
public class VRRig : MonoBehaviour
{
    [Tooltip("Give up after this long if no headset starts (desktop)")]
    public float waitForXrSeconds = 3f;
    [Tooltip("Floating panel for the converted screen canvases: distance and width in metres")]
    public float panelDistance = 1.3f;
    public float panelWidth = 1.2f;
    [Tooltip("Eye height used only if the headset can't report floor height")]
    public float fallbackEyeHeight = 1.6f;
    [Tooltip("VR terrain detail: higher = fewer triangles (Unity default 5)")]
    public float terrainPixelError = 8f;
    public float vrFarClip = 4000f;

    float startTime;
    bool setUp;
    bool headTracked;   // stage 1 done: camera on the headset (player may still be switched off)
    CinemachineBrain brain;
    CharacterMover mover;
    CharacterController controller;
    Camera cam;
    Transform rig;
    bool turnArmed = true;
    float nextTune;
    readonly HashSet<Terrain> tuned = new HashSet<Terrain>();
    readonly List<Canvas> converted = new List<Canvas>();  // the floating HUD panel's canvases
    bool hudHidden;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExists()
    {
        if (FindAnyObjectByType<VRRig>() != null) return;
        new GameObject(nameof(VRRig)).AddComponent<VRRig>();
    }

    void Start() => startTime = Time.unscaledTime;

    void Update()
    {
        if (!setUp)
        {
            if (MarsmapInput.XRActive) SetUp();
            else if (Time.unscaledTime - startTime > waitForXrSeconds) enabled = false; // desktop
            return;
        }
        SnapTurn();
        // While the EVA map is open the hologram stands where the HUD floats (top-down view):
        // hide the HUD then rather than have the two cut through each other
        if (hudHidden != MapView.AnyOpen)
        {
            hudHidden = MapView.AnyOpen;
            foreach (Canvas c in converted) if (c != null) c.enabled = !hudHidden;
        }
        if (Time.unscaledTime >= nextTune)
        {
            nextTune = Time.unscaledTime + 1f;
            TuneForQuest();  // Terraingen builds terrains (and sets the far clip) after startup
        }
    }

    // ---------------------------------------------------------------
    // Setup, in two stages
    // ---------------------------------------------------------------
    // Stage 1 (as soon as the headset runs): the headset drives the camera, wherever it is,
    // so the loading view isn't frozen. Stage 2 waits until the player is switched on:
    // Terraingen keeps it off until the terrain has arrived (up to ~5 s), and the camera can't
    // be parented under an inactive player (that would switch the camera off too).
    void SetUp()
    {
        if (!headTracked)
        {
            cam = Camera.main;
            if (cam == null) return;  // try again next frame
            // Cinemachine would keep overwriting the camera; the headset drives it now.
            if (cam.TryGetComponent(out brain)) brain.enabled = false;
            foreach (var input in FindObjectsByType<CinemachineInputAxisController>(FindObjectsInactive.Include))
                input.enabled = false;
            AddTrackedPoseDriver();
            headTracked = true;
        }

        mover = FindAnyObjectByType<CharacterMover>(FindObjectsInactive.Include);
        if (mover != null && !mover.gameObject.activeInHierarchy) return;  // stage 2 later
        if (Camera.main != null && Camera.main != cam)
        {
            cam = Camera.main;  // the main camera changed while waiting; take it over too
            if (cam.TryGetComponent(out CinemachineBrain other)) { other.enabled = false; brain = other; }
        }
        setUp = true;
        Transform body = mover != null ? mover.transform : null;
        controller = body != null ? body.GetComponent<CharacterController>() : null;

        // Hide the player's own cube BEFORE the camera (and anything on it) moves under the player.
        if (body != null)
            foreach (Renderer r in body.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

        // Rig at the feet; camera offset; camera driven by the headset
        rig = new GameObject("XR Rig").transform;
        if (body != null)
        {
            rig.SetParent(body, false);
            float feet = controller != null ? controller.center.y - controller.height * 0.5f : -1f;
            rig.localPosition = new Vector3(0f, feet, 0f);
            rig.localRotation = Quaternion.identity;
        }
        else
        {
            rig.position = cam.transform.position - Vector3.up * fallbackEyeHeight;
        }
        bool floor = UseFloorOrigin();
        var offset = new GameObject("Camera Offset").transform;
        offset.SetParent(rig, false);
        offset.localPosition = new Vector3(0f, floor ? 0f : fallbackEyeHeight, 0f);

        cam.transform.SetParent(offset, false);
        cam.transform.localPosition = Vector3.zero;
        cam.transform.localRotation = Quaternion.identity;
        AddTrackedPoseDriver();  // no-op if stage 1 already added it to this camera

        int canvases = ConvertCanvases();
        int retargeted = RetargetNavigation(body);
        TuneForQuest();

        Debug.Log($"[VRRig] XR active: camera on the headset ({(floor ? "floor" : "device")} origin), " +
                  $"{(brain != null ? "Cinemachine off, " : "")}{canvases} screen canvas(es) -> floating panel, " +
                  $"{retargeted} nav script(s) follow the head, snap turn {MarsmapConfig.SnapTurnDegrees:0}°, " +
                  $"walk {MarsmapConfig.VrMoveSpeed:0.#} m/s", this);
    }

    void AddTrackedPoseDriver()
    {
        cam.nearClipPlane = 0.05f;
        if (cam.TryGetComponent(out TrackedPoseDriver existing)) return;
        var tpd = cam.gameObject.AddComponent<TrackedPoseDriver>();
        tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        tpd.ignoreTrackingState = true;  // no tracking-state action wired; always use the pose
        tpd.positionInput = new InputActionProperty(new InputAction("HMD position",
            expectedControlType: "Vector3", binding: "<XRHMD>/centerEyePosition"));
        tpd.rotationInput = new InputActionProperty(new InputAction("HMD rotation",
            expectedControlType: "Quaternion", binding: "<XRHMD>/centerEyeRotation"));
    }

    static bool UseFloorOrigin()
    {
        var subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        bool floor = false;
        foreach (XRInputSubsystem s in subsystems)
        {
            if ((s.GetSupportedTrackingOriginModes() & TrackingOriginModeFlags.Floor) != 0
                && s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor))
                floor = true;
        }
        return floor;
    }

    // Screen Space canvases aren't drawn in a headset: make them a world-space panel ahead.
    int ConvertCanvases()
    {
        int n = 0;
        foreach (Canvas c in FindObjectsByType<Canvas>(FindObjectsInactive.Include))
        {
            if (!c.isRootCanvas || c.renderMode == RenderMode.WorldSpace) continue;
            var rt = (RectTransform)c.transform;
            Vector2 size = rt.rect.size;  // the layout size the panels were anchored in
            if (size.x < 1f || size.y < 1f)
            {
                var scaler = c.GetComponent<CanvasScaler>();
                size = scaler != null ? scaler.referenceResolution : new Vector2(800f, 600f);
            }
            c.renderMode = RenderMode.WorldSpace;
            converted.Add(c);
            c.worldCamera = cam;
            rt.SetParent(cam.transform, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.localPosition = new Vector3(0f, 0f, panelDistance);
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one * (panelWidth / size.x);
            n++;
        }
        return n;
    }

    // Compass, direction arrows and the nav readout read the player's body. In VR the body only
    // turns with snap turn; the head turns freely, so point them at the head (the camera).
    int RetargetNavigation(Transform body)
    {
        if (body == null) return 0;
        int n = 0;
        foreach (var c in FindObjectsByType<Compass>(FindObjectsInactive.Include))
            if (c.playerTransform == body) { c.playerTransform = cam.transform; n++; }
        foreach (var p in FindObjectsByType<PointAt>(FindObjectsInactive.Include))
            if (p.playerTransform == body) { p.playerTransform = cam.transform; n++; }
        foreach (var g in FindObjectsByType<GetState>(FindObjectsInactive.Include))
            if (g.targetTransform == body) { g.targetTransform = cam.transform; n++; }
        return n;
    }

    // ---------------------------------------------------------------
    // Every frame
    // ---------------------------------------------------------------
    void SnapTurn()
    {
        if (MapView.AnyOpen)  // the right stick turns and zooms the EVA map hologram instead
        {
            turnArmed = false;  // re-armed once the stick is back in the middle
            return;
        }
        float x = MarsmapInput.Turn.ReadValue<Vector2>().x;
        if (turnArmed && Mathf.Abs(x) > 0.7f)
        {
            turnArmed = false;
            Turn(Mathf.Sign(x) * MarsmapConfig.SnapTurnDegrees);
        }
        else if (Mathf.Abs(x) < 0.3f)
        {
            turnArmed = true;
        }
    }

    void Turn(float degrees)
    {
        Transform body = mover != null ? mover.transform : rig;
        if (body == null || cam == null) return;
        Vector3 head = cam.transform.position;
        if (controller != null) controller.enabled = false;  // CharacterController ignores direct moves
        body.RotateAround(new Vector3(head.x, body.position.y, head.z), Vector3.up, degrees);
        Physics.SyncTransforms();
        if (controller != null) controller.enabled = true;
    }

    void TuneForQuest()
    {
        if (cam != null && cam.farClipPlane > vrFarClip) cam.farClipPlane = vrFarClip;  // depth precision
        foreach (Terrain t in Terrain.activeTerrains)
        {
            if (t == null || tuned.Contains(t)) continue;
            tuned.Add(t);
            t.heightmapPixelError = Mathf.Max(t.heightmapPixelError, terrainPixelError);
            t.basemapDistance = Mathf.Min(t.basemapDistance, 600f);
        }
    }
}
