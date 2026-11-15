using System.Collections.Generic;
using Marsmap.AR;
using UnityEngine;

/// <summary>
/// AR mode for a Quest 3: the headset's passthrough camera shows the real room, and only our
/// UI floats in it. The Mars world (terrain, sky, dust, markers on the ground) is not drawn;
/// what stays:
///  - the head-locked HUD (VRHud.Sheet): compass, navigation, alerts, minimap, EVA route;
///  - the EVA map (MapView), opened as in VR (Menu on the left controller) and used the same way:
///    the hologram, its pins and labels, the panel beside it, the laser.
///
/// Switched on with "ar_passthrough": true in marsmap_config.json; needs the Meta XR Core SDK
/// (com.meta.xr.sdk.core), whose OVRManager + OVRPassthroughLayer are set up in AR/Meta.
/// The game itself still runs underneath (you still stand on, and walk over, the invisible
/// terrain), so the compass, the waypoints and the map all work as in VR.
///
/// Until passthrough is actually running the normal VR view stays, so a headset that can't do
/// passthrough (or a build missing a setting) shows Mars rather than black; the log says why.
///
/// How the world is hidden: every camera drawing to the headset clears to see-through black
/// (where the eye buffer is see-through the compositor shows passthrough behind it) and draws
/// only the UI layer. The HUD and map objects are moved onto that layer as they appear.
/// HDR is switched off on those cameras: URP's 32-bit HDR buffer has no alpha channel, which
/// would make the whole view opaque.
/// Created automatically, like VRRig.
/// </summary>
public class ARView : MonoBehaviour
{
    /// <summary>Unity's built-in "UI" layer: the only layer the camera draws in AR.</summary>
    public const int ARLayer = 5;

    /// <summary>True while the real world is showing and Mars is hidden.</summary>
    public static bool Active { get; private set; }

    [Tooltip("Give up after this long if no headset starts (desktop)")]
    public float waitForXrSeconds = 3f;
    [Tooltip("If passthrough isn't running this long after asking, stay in VR")]
    public float startTimeoutSeconds = 10f;

    enum Phase { WaitingForHeadset, Starting, On, Off }
    Phase phase;
    float startTime, askedAt;
    IPassthroughBackend backend;
    readonly List<GameObject> visible = new List<GameObject>();
    MapView[] maps = new MapView[0];
    float nextMapSearch;
    Camera[] cameras = new Camera[4];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Active = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExists()
    {
        if (!MarsmapConfig.ArPassthrough || FindAnyObjectByType<ARView>() != null) return;
        new GameObject(nameof(ARView)).AddComponent<ARView>();
    }

    void Start() => startTime = Time.unscaledTime;

    void Update()
    {
        switch (phase)
        {
            case Phase.WaitingForHeadset:
                if (MarsmapInput.XRActive) Ask();
                else if (Time.unscaledTime - startTime > waitForXrSeconds) // desktop: nothing to do
                    Stop(Application.platform == RuntimePlatform.Android
                        ? $"no headset session started within {waitForXrSeconds:0} s" : null);
                break;
            case Phase.Starting:
                PassthroughState state = backend.State;
                if (state == PassthroughState.Running) TurnOn();
                else if (state == PassthroughState.Failed) Stop("passthrough failed: " + backend.Problem);
                else if (Time.unscaledTime - askedAt > startTimeoutSeconds)
                    Stop($"passthrough didn't start within {startTimeoutSeconds:0} s");
                break;
        }
    }

    // After everything else has run this frame (MapView adds pins and tags in Update), so
    // nothing new is drawn for a frame on the hidden layer
    void LateUpdate()
    {
        if (phase != Phase.On) return;
        if (!MarsmapInput.XRActive)
        {
            Stop("the headset stopped");
            return;
        }
        HideWorld();
        ShowOurObjects();
    }

    void Ask()
    {
        backend = Passthrough.Backend;
        if (backend == null)
        {
            Stop("\"ar_passthrough\" is on in marsmap_config.json, but the Meta XR Core SDK " +
                 "(com.meta.xr.sdk.core) isn't installed in this project");
            return;
        }
        backend.Start();
        askedAt = Time.unscaledTime;
        phase = Phase.Starting;
    }

    void TurnOn()
    {
        phase = Phase.On;
        Active = true;
        RenderSettings.fog = false; // nothing it would apply to is drawn; keep it off the hologram
        HideWorld();
        ShowOurObjects();
        // A map opened while passthrough was starting was placed over the Mars hills: re-place it
        foreach (MapView map in FindObjectsByType<MapView>(FindObjectsInactive.Exclude)) map.ReanchorForAR();
        Debug.Log("[ARView] AR on: passthrough running, Mars hidden; HUD and EVA map shown.", this);
    }

    // why == null: quietly (desktop). Puts back every camera AR changed and switches
    // passthrough off, so what's left is the plain VR view.
    void Stop(string why)
    {
        if (why != null) Debug.LogWarning("[ARView] Staying in VR: " + why + ".", this);
        backend?.Stop();
        RestoreCameras();
        phase = Phase.Off;
        Active = false;
        enabled = false;
    }

    struct SavedCamera { public CameraClearFlags clear; public Color background; public int mask; public bool hdr; }
    readonly Dictionary<Camera, SavedCamera> saved = new Dictionary<Camera, SavedCamera>();

    void RestoreCameras()
    {
        foreach (KeyValuePair<Camera, SavedCamera> kv in saved)
        {
            Camera cam = kv.Key;
            if (cam == null) continue;
            cam.clearFlags = kv.Value.clear;
            cam.backgroundColor = kv.Value.background;
            cam.cullingMask = kv.Value.mask;
            cam.allowHDR = kv.Value.hdr;
        }
        saved.Clear();
    }

    // Every camera drawing to the headset: clear to see-through black, draw only the AR layer
    void HideWorld()
    {
        int n = Camera.allCamerasCount;
        if (cameras.Length < n) cameras = new Camera[n * 2];
        n = Camera.GetAllCameras(cameras);
        for (int i = 0; i < n; i++)
        {
            Camera cam = cameras[i];
            if (cam == null || cam.targetTexture != null) continue;
            if (!saved.ContainsKey(cam))
                saved[cam] = new SavedCamera { clear = cam.clearFlags, background = cam.backgroundColor,
                                               mask = cam.cullingMask, hdr = cam.allowHDR };
            if (cam.clearFlags != CameraClearFlags.SolidColor) cam.clearFlags = CameraClearFlags.SolidColor;
            if (cam.backgroundColor != Color.clear) cam.backgroundColor = Color.clear;
            if (cam.cullingMask != 1 << ARLayer) cam.cullingMask = 1 << ARLayer;
            if (cam.allowHDR) cam.allowHDR = false;
        }
        for (int i = 0; i < n; i++) cameras[i] = null; // don't keep destroyed cameras alive
    }

    // The HUD sheet and the EVA map's objects onto the AR layer (they're created as you go)
    void ShowOurObjects()
    {
        visible.Clear();
        RectTransform sheet = VRHud.SheetIfBuilt;
        if (sheet != null) visible.Add(sheet.gameObject);
        if (Time.unscaledTime >= nextMapSearch)
        {
            nextMapSearch = Time.unscaledTime + 2f;
            maps = FindObjectsByType<MapView>(FindObjectsInactive.Exclude);
        }
        foreach (MapView map in maps)
            if (map != null) map.CollectARVisible(visible);
        foreach (GameObject go in visible)
            if (go != null) SetLayer(go.transform);
    }

    static void SetLayer(Transform t)
    {
        if (t.gameObject.layer != ARLayer) t.gameObject.layer = ARLayer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i));
    }

    /// <summary>Height of the real floor in the world (the XR rig sits on it), for placing the
    /// hologram in AR where there's no visible ground. Falls back to 1.6 m below the eyes.</summary>
    public static float FloorY(Transform eye)
    {
        Transform rig = VRRig.Rig;
        if (rig != null) return rig.position.y;
        return eye != null ? eye.position.y - 1.6f : 0f;
    }
}
