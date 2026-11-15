using System.Collections.Generic;
using System.Text;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Press M: a hologram of the terrain appears in front of you (like an AR table map), coloured
/// by the EVA hazard zones. Turn it, zoom it, move it, and click on it to drop waypoints; the
/// route from where you stand through every waypoint is planned by Python/route_planner.py with
/// the team's rules from Mars_mapping(3).ipynb (A* within 18 deg climbs and 25 deg side slopes,
/// slope hazard zones, Tobler EVA time, EASY/MODERATE/HARD) and drawn on the hologram.
///
/// Hologram controls: drag sideways to turn, drag up/down to tilt (T: top-down), scroll zoom, WASD or right-drag move the area shown,
/// click add a waypoint, Shift+click remove one, Backspace undo, C clear, R return to Home Base on/off,
/// F centre on you, M close.
/// In a headset (Meta Quest) the Menu button opens it and the right controller is a laser
/// pointer; see MapViewVR.cs (VR/) for the controller layout and the VR panel.
///
/// On the ground only the leg you're on is shown: an arrowed line from where you are (or the last
/// waypoint) to the next waypoint, which is marked by a glowing beam and a ring showing its
/// arrival radius. Walk inside the ring and the next leg and waypoint appear. On the compass,
/// the red arrow points at the next waypoint and the green arrow at Home Base (where the route
/// began), which also keeps a green beam and label so you can always find your way back
/// (found by their arrowred / arrowgreen images). Home Base is where you're put on the map.
/// Adds itself next to Terraingen at startup, so nothing has to be set up in the scene.
/// </summary>
public partial class MapView : MonoBehaviour
{
    [Header("Hologram")]
    [Tooltip("Width of the hologram (m)")]
    public float hologramSize = 1.2f;
    [Tooltip("How far in front of you it appears (m)")]
    public float hologramDistance = 1.6f;
    [Tooltip("How far below your eyes it floats (m)")]
    public float hologramBelowEyes = 0.45f;
    [Tooltip("Area shown when it opens: this many metres from the centre to each edge")]
    public float startRadius = 2500f;
    [Tooltip("Open showing the whole map (centred on the map). Off: open at Start Radius around you.")]
    public bool startZoomedOut = true;
    [Tooltip("Grid points along each side of the hologram (more = finer, slower to rebuild)")]
    [Range(33, 257)] public int hologramResolution = 129;
    [Tooltip("Relief is exaggerated up to this much so small hills are visible (shown on the panel)")]
    public float maxVerticalExaggeration = 6f;

    [Header("Route on the ground")]
    [Tooltip("Width of the route line on the real ground (m); arrows along it show the way")]
    public float groundLineWidth = 0.3f;
    [Tooltip("The ground line follows the terrain with a point every this many metres")]
    public float groundLineStep = 5f;
    [Tooltip("The next waypoint's light beam on the real ground (m)")]
    public float beaconRadius = 0.3f, beaconHeight = 30f;
    [Tooltip("Plan the route back to Home Base too (R toggles it in the map)")]
    public bool roundTrip = true;
    [Tooltip("Reaching this close (m) to a waypoint shows the next one (the ring on the ground)")]
    public float arriveDistance = 25f;

    [Header("Home Base")]
    [Tooltip("Show an orange sphere at Home Base (display only, nothing collides with it)")]
    public bool showHomeSphere = true;
    [Tooltip("Size of the Home Base sphere (m across)")]
    public float homeSphereSize = 12f;
    [Tooltip("Home Base is this far in front of where you're put on the map, so its sphere " +
             "isn't on top of you (well inside the arrival radius)")]
    public float homeBaseAhead = 15f;

    [Header("Compass arrows")]
    [Tooltip("Where the red (next waypoint) and green (Home Base) arrows ride on the compass, " +
             "as a share of its radius (1 = its edge). Both use this.")]
    [Range(0.3f, 1.3f)] public float compassArrowRadius = 0.75f;

    // Hazard colours from Mars_mapping(3).ipynb, plus magenta for "no safe path"
    static readonly Color[] TierColors =
    {
        new Color32(0x2e, 0xcc, 0x71, 255), // 0-10 deg optimal
        new Color32(0xf1, 0xc4, 0x0f, 255), // 10-20 deg caution
        new Color32(0xe6, 0x7e, 0x22, 255), // 20-30 deg high risk
        new Color32(0xe7, 0x4c, 0x3c, 255), // > 30 deg impassable
        new Color32(0xff, 0x3c, 0xf0, 255), // no safe path (drawn straight)
    };
    static readonly string[] TierNames = { "optimal", "caution", "high risk", "impassable", "no safe path" };
    static readonly string[] TierHex = { "#2ecc71", "#f1c40f", "#e67e22", "#e74c3c", "#ff3cf0" };

    Terraingen terraingen;
    Transform player;
    CharacterMover mover;
    CinemachineInputAxisController lookInput;
    CinemachinePanTilt panTilt;   // the first-person look; aimed at the hologram while it's open
    float savedPan, savedTilt;    // where you were looking before opening it
    float holoCentreY;            // middle of the hologram's terrain block, above its base

    // ---- hologram
    bool open;
    GameObject holoRoot;          // placed in front of you, turned by mapYaw
    Transform holoContent;        // terrain mesh + pins + route, in hologram units
    MeshFilter holoMesh;
    MeshCollider holoCollider;
    Material holoMat, plateMat;
    Vector2 centre;               // world x/z at the hologram's middle (follows WASD / zoom at once)
    float radius;                 // world metres from the middle to an edge
    Vector2 builtCentre;          // ...and what the mesh on show was actually built with
    float builtRadius = 1f;
    bool reenableLookNextFrame;   // see Close()
    float mapYaw;                 // how far the hologram is turned
    float tilt, tiltTarget = -1f; // 0 = lying flat, 90 = standing up facing you (top-down view)
    float yawTarget = float.NaN;  // T also turns it so north ends up at the top
    Vector3 holoBase;             // where the hologram's centre sits when flat
    float holoGroundY;            // ground under it, so standing up it never goes into the hill
    Vector3 viewForward, viewRight; // your horizontal facing when it opened (it tilts toward you)
    float minElev, vScale = 1f, exaggeration = 1f; // hologram height = (elev - minElev) * vScale
    bool rebuildPending;
    float lastRebuild = -1f;
    Vector2 pressPos;
    bool leftDown, dragged;
    readonly List<GameObject> holoPins = new List<GameObject>();
    readonly List<LineRenderer> holoLines = new List<LineRenderer>();
    GameObject holoYou, holoHome;
    GameObject homeBeam;          // Home Base on the ground: always shown while there's a route
    GameObject homeSphere;        // ...and an orange sphere marking it (display only)
    Material homeMat, homePinMat;
    static readonly Color HomeGreen = new Color(0.2f, 1f, 0.45f);

    // ---- waypoints + route (world)
    readonly List<Vector3> waypoints = new List<Vector3>();
    GameObject routeRoot;
    GameObject targetBeam;        // the next waypoint on the ground: a light beam...
    LineRenderer targetRing;      // ...and a ring showing how close counts as reached
    Vector3 ringAt = new Vector3(float.NaN, 0f, 0f);
    readonly List<Vector2Int> legRanges = new List<Vector2Int>(); // route point range of each leg
    readonly List<LineRenderer> lines = new List<LineRenderer>();
    Material[] tierMats;
    Material beaconMat, nextMat, doneMat, youMat, beamMat, ringMat;
    float[] routeX, routeZ, routeElev;
    int[] routeTier;

    RouteMessage route;
    int routeOffset;              // waypoints already reached when this route was planned
    Vector3 routeStart;           // where the current plan starts (you, when it was planned)
    Vector3 tripStart;            // Home Base: where you were put on the map (green arrow, return)
    bool hasTrip;                 // Home Base is known
    int placementsSeen = -1;      // Terraingen.PlayerPlacements when Home Base was taken
    int pendingOffset;            // routeOffset of the plan being waited for
    Transform startTarget;        // what the green arrow points at
    PointAt redArrow, greenArrow; // compass arrows: next waypoint / start of the route
    bool redAdded;                // the red arrow had no PointAt: we added one
    Vector3 redRest;              // its rotation before we turned it...
    Vector2 redRestPos, redRestPivot; // ...and its spot (it rides around the compass when in use)
    float compassSearchedAt = -10f;
    float pendingAt;
    int requestId, pendingId = -1;
    float replanAt = -1f;
    string status = "";
    int nextWaypoint;
    Transform arrowTarget;
    readonly Dictionary<PointAt, Transform> originalDest = new Dictionary<PointAt, Transform>();
    PythonConnection subscribedTo;
    Rect helpRect, routeRect;
    GUIStyle boxStyle, smallStyle;

    // ---- read by Minimap.cs
    public bool IsOpen => open;
    /// <summary>True while any EVA map hologram is open (VRRig pauses snap turn then).</summary>
    public static bool AnyOpen { get; private set; }
    public int RouteVersion { get; private set; }  // changes whenever the route or leg changes
    public float[] RouteX => routeX;
    public float[] RouteZ => routeZ;
    public float[] RouteElev => routeElev;
    public int[] RouteTier => routeTier;
    public IReadOnlyList<Vector3> Waypoints => waypoints;
    public int NextWaypoint => nextWaypoint;
    public bool HasHomeBase => HasHome;
    public Vector3 HomeBase => tripStart;
    public Vector3 NextTarget => Target(nextWaypoint);
    public bool HasNextTarget => nextWaypoint < TargetCount;
    /// <summary>Route point range of the leg you're on, or false if none.</summary>
    public bool TryGetCurrentLeg(out int from, out int to)
    {
        from = to = 0;
        int leg = nextWaypoint - routeOffset;
        if (routeX == null || leg < 0 || leg >= legRanges.Count) return false;
        from = Mathf.Clamp(legRanges[leg].x, 0, routeX.Length - 1);
        to = Mathf.Clamp(legRanges[leg].y, from, routeX.Length - 1);
        return true;
    }

    const float HoloLift = 0.004f;        // route/pins float this far above the hologram terrain
    const float HoloPlate = 0.015f;       // the terrain sits on a plate this thick

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => AnyOpen = false; // fresh on every Play, even without a domain reload

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExists()
    {
        if (FindAnyObjectByType<MapView>() != null) return;
        Terraingen tg = FindAnyObjectByType<Terraingen>();
        if (tg != null) tg.gameObject.AddComponent<MapView>();
    }

    void Start()
    {
        terraingen = GetComponent<Terraingen>();
        if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
        player = terraingen != null ? terraingen.player : null;
        mover = player != null ? player.GetComponent<CharacterMover>() : FindAnyObjectByType<CharacterMover>();
        lookInput = FindAnyObjectByType<CinemachineInputAxisController>();
        panTilt = FindAnyObjectByType<CinemachinePanTilt>();

        tierMats = new Material[TierColors.Length];
        for (int i = 0; i < TierColors.Length; i++)
        {
            tierMats[i] = MakeMaterial(Color.white);
            tierMats[i].mainTexture = ArrowTexture(TierColors[i]); // tiled along each line
        }
        beaconMat = MakeMaterial(new Color(1f, 0.85f, 0.2f));
        nextMat = MakeMaterial(new Color(0.2f, 0.9f, 1f));
        doneMat = MakeMaterial(new Color(0.55f, 0.55f, 0.55f));
        youMat = MakeMaterial(new Color(0.2f, 0.9f, 1f));
        ringMat = MakeMaterial(new Color(0.2f, 0.9f, 1f));
        Shader holo = Resources.Load<Shader>("Hologram");
        beamMat = holo != null ? new Material(holo) : MakeMaterial(new Color(0.2f, 0.9f, 1f));
        if (holo != null)
        {
            beamMat.SetColor("_Tint", new Color(0.25f, 0.95f, 1f, 0.55f));
            beamMat.SetFloat("_Scan", 0.35f);
        }
        homeMat = holo != null ? new Material(holo) : MakeMaterial(HomeGreen);
        if (holo != null)
        {
            homeMat.SetColor("_Tint", new Color(HomeGreen.r, HomeGreen.g, HomeGreen.b, 0.45f));
            homeMat.SetColor("_Glow", HomeGreen);
            homeMat.SetFloat("_Scan", 0.35f);
        }
        homePinMat = MakeMaterial(HomeGreen);

        routeRoot = new GameObject("EVA Route");
        arrowTarget = new GameObject("EVA Next Waypoint").transform;
        startTarget = new GameObject("EVA Route Start").transform;
        radius = startRadius;
    }

    void OnDestroy()
    {
        if (subscribedTo != null)
        {
            subscribedTo.RouteReceived -= OnRoute;
            subscribedTo.ConnectionChanged -= OnConnectionChanged;
        }
        if (open) Close();
        AnyOpen = false;
        if (lookInput != null && !MarsmapInput.XRActive) lookInput.enabled = true; // VRRig keeps it off in VR
        DestroyVR();
        foreach (var kv in originalDest)
            if (kv.Key != null) kv.Key.dest = kv.Value; // arrows back to what they pointed at
        if (targetBeam != null) Destroy(targetBeam);
        if (homeBeam != null) Destroy(homeBeam);
        if (homeSphere != null) Destroy(homeSphere);
        if (targetRing != null) Destroy(targetRing.gameObject);
        if (routeRoot != null) Destroy(routeRoot);
        if (holoRoot != null) Destroy(holoRoot);
        if (arrowTarget != null) Destroy(arrowTarget.gameObject);
        if (startTarget != null) Destroy(startTarget.gameObject);
        if (redAdded && redArrow != null)
        {
            RestoreRed();
            Destroy(redArrow);
        }
    }

    // ---------------------------------------------------------------
    // Frame loop
    // ---------------------------------------------------------------
    void Update()
    {
        if (reenableLookNextFrame)
        {
            reenableLookNextFrame = false;
            if (!open && lookInput != null && !MarsmapInput.XRActive) lookInput.enabled = true;
        }
        Subscribe();
        TakeHomeBase();
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;

        bool vr = MarsmapInput.XRActive;
        if ((kb != null && kb.mKey.wasPressedThisFrame) || MarsmapInput.MapToggle.WasPressedThisFrame()
            || (!open && MarsmapInput.MapOpenAlt.WasPressedThisFrame()))
        {
            if (open) Close();
            else Open();
        }
        if (open)
        {
            if (openedInVR) VRControls();          // laser pointer + controller buttons
            else if (kb != null && mouse != null) HologramControls(kb, mouse);
            AnimateHologram();
        }
        // In a headset rebuild less often: a Quest is slower and the sticks zoom/move continuously
        if (open && rebuildPending && Time.unscaledTime - lastRebuild > (openedInVR ? 0.25f : 0.1f)) BuildHologram();
        if (open && !openedInVR) AimAtHologram();

        if (replanAt > 0f && Time.unscaledTime >= replanAt)
        {
            replanAt = -1f;
            RequestRoute();
        }
        if (pendingId >= 0 && Time.unscaledTime - pendingAt > 30f)
        {
            pendingId = -1;
            status = "No reply from server.py (old version, or the connection dropped): straight lines shown.";
            DrawStraight();
        }
        UpdateGuidance();
        UpdateGroundSizes();
        if (open && holoYou != null && player != null) PlacePin(holoYou, player.position, 1.3f);
        if (vr) UpdateVRLabels();
        else if (vrRouteLabel != null || vrPanel != null) DestroyVR(); // headset gone
    }

    // You always start at Home Base: wherever Terraingen put you on the map (again on a new map)
    void TakeHomeBase()
    {
        if (terraingen == null || player == null || !player.gameObject.activeInHierarchy) return;
        if (terraingen.PlayerPlacements == placementsSeen) return;
        placementsSeen = terraingen.PlayerPlacements;
        Vector3 ahead = Vector3.ProjectOnPlane(player.forward, Vector3.up);
        ahead = ahead.sqrMagnitude > 1e-4f ? ahead.normalized : Vector3.forward;
        float gap = Mathf.Max(homeBaseAhead, homeSphereSize * 0.5f + 5f); // clear of the sphere
        tripStart = player.position + ahead * gap; // in view, not on top of you
        hasTrip = true;
    }

    void Subscribe()
    {
        PythonConnection pc = PythonConnection.Instance;
        if (pc == subscribedTo) return;
        if (subscribedTo != null)
        {
            subscribedTo.RouteReceived -= OnRoute;
            subscribedTo.ConnectionChanged -= OnConnectionChanged;
        }
        subscribedTo = pc;
        if (pc != null)
        {
            pc.RouteReceived += OnRoute;
            pc.ConnectionChanged += OnConnectionChanged;
        }
    }

    // Server back: plan properly again. Server gone mid-request: straight lines now, not in 30 s.
    void OnConnectionChanged(bool connected)
    {
        if (connected && waypoints.Count > 0) ScheduleReplan();
        else if (!connected && pendingId >= 0)
        {
            pendingId = -1;
            status = "Lost the connection to server.py: straight lines shown.";
            DrawStraight();
        }
    }

    // ---------------------------------------------------------------
    // Open / close: the hologram appears in front of you, anchored in the world
    // ---------------------------------------------------------------
    void Open()
    {
        if (terraingen == null || !terraingen.TryGetMapBounds(out _))
        {
            status = "The map isn't ready yet (no terrain).";
            return;
        }
        Camera cam = Camera.main;
        Transform eye = cam != null ? cam.transform : player;
        if (eye == null) return;
        openedInVR = MarsmapInput.XRActive;
        float distance = openedInVR ? vrHologramDistance : hologramDistance;
        belowEyes = openedInVR ? vrHologramBelowEyes : hologramBelowEyes;

        if (holoRoot == null) CreateHologram();
        Vector3 forward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(eye.up, Vector3.up);
        forward.Normalize();
        Vector3 at = eye.position + forward * distance + Vector3.down * belowEyes;
        at.y = Mathf.Max(at.y, terraingen.GroundY(at) + 0.3f); // not inside a hill in front of you
        holoBase = at;
        holoGroundY = terraingen.GroundY(at);
        viewForward = forward;
        viewRight = Vector3.Cross(Vector3.up, forward);
        mapYaw = 0f; // north on the hologram = real north, so it lines up with what's around you
        tilt = 0f;
        tiltTarget = -1f;
        yawTarget = float.NaN;
        PoseHologram();
        holoRoot.SetActive(true);

        Vector3 here = player != null ? player.position : Vector3.zero;
        centre = new Vector2(here.x, here.z);
        radius = startRadius;
        if (startZoomedOut && terraingen.TryGetMapBounds(out Rect whole))
        {
            centre = whole.center;                              // the whole map fits...
            radius = Mathf.Max(whole.width, whole.height) * 0.5f; // ...and this is the furthest out zoom
        }
        open = true;
        AnyOpen = true;
        BuildHologram();

        if (mover != null) mover.inputLocked = true; // no walking, but gravity still works
        reenableLookNextFrame = false;
        if (openedInVR)
        {
            OpenVR(eye); // panel beside the hologram, laser on; the headset keeps looking freely
            return;
        }
        if (panTilt != null)
        {
            savedPan = panTilt.PanAxis.Value;
            savedTilt = panTilt.TiltAxis.Value;
        }
        if (lookInput != null) lookInput.enabled = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Close()
    {
        open = false;
        AnyOpen = false;
        if (holoRoot != null) holoRoot.SetActive(false);
        if (mover != null) mover.inputLocked = false;
        leftDown = dragged = false;
        if (openedInVR)
        {
            CloseVR();
            return;
        }
        if (panTilt != null)
        {
            panTilt.PanAxis.Value = savedPan;
            panTilt.TiltAxis.Value = savedTilt;
        }
        // Re-locking the cursor can report one big mouse jump: turn the look back on next frame
        reenableLookNextFrame = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void CreateHologram()
    {
        holoRoot = new GameObject("EVA Hologram");
        Shader sh = Resources.Load<Shader>("Hologram");
        holoMat = sh != null ? new Material(sh) : MakeMaterial(Color.white);
        plateMat = sh != null ? new Material(sh) : MakeMaterial(new Color(0.1f, 0.4f, 0.5f));
        if (sh != null)
        {
            plateMat.SetColor("_Tint", new Color(0.15f, 0.55f, 0.7f, 0.35f));
            plateMat.SetFloat("_Scan", 0.3f);
        }

        // Base plate under the terrain
        GameObject plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
        plate.name = "Plate";
        Destroy(plate.GetComponent<Collider>());
        plate.transform.SetParent(holoRoot.transform, false);
        plate.transform.localScale = new Vector3(hologramSize * 1.04f, HoloPlate, hologramSize * 1.04f);
        plate.transform.localPosition = new Vector3(0f, HoloPlate * 0.5f, 0f);
        SetupRenderer(plate.GetComponent<Renderer>(), plateMat);

        holoContent = new GameObject("Content").transform;
        holoContent.SetParent(holoRoot.transform, false);
        var terrainGo = new GameObject("Terrain");
        terrainGo.transform.SetParent(holoContent, false);
        holoMesh = terrainGo.AddComponent<MeshFilter>();
        holoMesh.sharedMesh = new Mesh   // 32-bit indices: up to 257 x 257 points
        {
            name = "Hologram terrain",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
        };
        SetupRenderer(terrainGo.AddComponent<MeshRenderer>(), holoMat);
        holoCollider = terrainGo.AddComponent<MeshCollider>(); // for clicks only

        holoHome = MakeBeacon(homePinMat, "Home Base (hologram)");
        holoHome.transform.SetParent(holoContent, false);
        holoYou = MakeBeacon(youMat, "You (hologram)");
        holoYou.transform.SetParent(holoContent, false);
    }

    // ---------------------------------------------------------------
    // Hologram mesh: the terrain around `centre`, coloured by hazard zone
    // ---------------------------------------------------------------
    void BuildHologram()
    {
        rebuildPending = false;
        lastRebuild = Time.unscaledTime;
        if (terraingen == null || !terraingen.TryGetMapBounds(out Rect area)) return;

        radius = Mathf.Clamp(radius, 100f, Mathf.Max(area.width, area.height) * 0.5f);
        centre.x = Mathf.Clamp(centre.x, area.xMin, area.xMax);
        centre.y = Mathf.Clamp(centre.y, area.yMin, area.yMax);

        builtCentre = centre;
        builtRadius = radius;
        int n = openedInVR ? Mathf.Min(hologramResolution, vrHologramResolution) : hologramResolution;
        float step = 2f * radius / (n - 1);
        var elev = new float[n * n];
        var outside = new bool[n * n];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var w = new Vector3(centre.x - radius + i * step, 0f, centre.y - radius + j * step);
                int k = j * n + i;
                outside[k] = !area.Contains(new Vector2(w.x, w.z));
                elev[k] = Elevation(w);
                lo = Mathf.Min(lo, elev[k]);
                hi = Mathf.Max(hi, elev[k]);
            }

        // Exaggerate the relief so ~12% of the width is used, but never more than the cap
        float span = Mathf.Max(hi - lo, 1f);
        exaggeration = Mathf.Clamp(0.12f * 2f * radius / span, 1f, maxVerticalExaggeration);
        minElev = lo;
        vScale = hologramSize / (2f * radius) * exaggeration;
        holoCentreY = HoloPlate + (hi - lo) * vScale * 0.5f;

        var verts = new Vector3[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                verts[k] = new Vector3((i / (float)(n - 1) - 0.5f) * hologramSize,
                                       HoloPlate + (elev[k] - lo) * vScale,
                                       (j / (float)(n - 1) - 0.5f) * hologramSize);
            }
        var tris = new int[(n - 1) * (n - 1) * 6];
        int t = 0;
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                tris[t++] = a; tris[t++] = c; tris[t++] = b;
                tris[t++] = b; tris[t++] = c; tris[t++] = d;
            }

        Mesh mesh = holoMesh.sharedMesh;
        mesh.Clear();
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        Vector3[] normals = mesh.normals;

        // Colour: hazard zone from the real slope (same edges as the notebook), with shading
        var cols = new Color[n * n];
        Vector3 light = new Vector3(-0.4f, 0.8f, 0.45f).normalized;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                float dx = (elev[j * n + Mathf.Min(i + 1, n - 1)] - elev[j * n + Mathf.Max(i - 1, 0)])
                           / (step * (Mathf.Min(i + 1, n - 1) - Mathf.Max(i - 1, 0)));
                float dz = (elev[Mathf.Min(j + 1, n - 1) * n + i] - elev[Mathf.Max(j - 1, 0) * n + i])
                           / (step * (Mathf.Min(j + 1, n - 1) - Mathf.Max(j - 1, 0)));
                float slope = Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg;
                int tier = slope < 10f ? 0 : slope < 20f ? 1 : slope < 30f ? 2 : 3;
                float shade = 0.45f + 0.55f * Mathf.Max(0f, Vector3.Dot(normals[k], light));
                Color c = TierColors[tier] * shade;
                c.a = outside[k] ? 0.15f : 1f; // past the edge of the map: faint
                cols[k] = c;
            }
        mesh.colors = cols;
        mesh.RecalculateBounds();
        holoCollider.sharedMesh = null;
        holoCollider.sharedMesh = mesh;

        DrawHologramRoute();
    }

    float Elevation(Vector3 world)
    {
        if (terraingen.TryGetElevation(world, out float m)) return m;
        return terraingen.GroundY(world); // Perlin fallback: Unity heights
    }

    // World x/z (+ elevation) -> hologram content space, and back
    // (These use the area the mesh on show was built with, not the one waiting to be built)
    Vector3 ToHolo(float x, float z, float elev, float lift = 0f) =>
        new Vector3((x - builtCentre.x) / (2f * builtRadius) * hologramSize,
                    HoloPlate + (elev - minElev) * vScale + lift,
                    (z - builtCentre.y) / (2f * builtRadius) * hologramSize);

    bool InsideHolo(float x, float z) =>
        Mathf.Abs(x - builtCentre.x) <= builtRadius && Mathf.Abs(z - builtCentre.y) <= builtRadius;

    Vector2 FromHolo(Vector3 local) =>
        new Vector2(builtCentre.x + local.x / hologramSize * 2f * builtRadius,
                    builtCentre.y + local.z / hologramSize * 2f * builtRadius);

    // ---------------------------------------------------------------
    // Hologram controls
    // ---------------------------------------------------------------
    void HologramControls(Keyboard kb, Mouse mouse)
    {
        Vector2 mp = mouse.position.ReadValue();
        Vector2 gui = new Vector2(mp.x, Screen.height - mp.y);
        bool overPanel = helpRect.Contains(gui) || routeRect.Contains(gui);

        // Left: drag to turn the hologram, click to add (Shift+click: remove) a waypoint
        if (mouse.leftButton.wasPressedThisFrame)
        {
            leftDown = !overPanel;
            pressPos = mp;
            dragged = false;
        }
        if (leftDown && mouse.leftButton.isPressed)
        {
            if ((mp - pressPos).sqrMagnitude > 36f) dragged = true;
            if (dragged)
            {
                Vector2 d = mouse.delta.ReadValue();
                mapYaw -= d.x * 0.3f;                          // sideways: turn
                tilt = Mathf.Clamp(tilt + d.y * 0.3f, 0f, 90f); // up: tip it toward you
                tiltTarget = -1f;
                yawTarget = float.NaN;
            }
        }
        if (leftDown && mouse.leftButton.wasReleasedThisFrame)
        {
            leftDown = false;
            if (!dragged)
            {
                if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) RemoveWaypointNear(mp);
                else AddWaypointAt(mp);
            }
        }
        if (kb.qKey.isPressed) { mapYaw += 60f * Time.unscaledDeltaTime; yawTarget = float.NaN; }
        if (kb.eKey.isPressed) { mapYaw -= 60f * Time.unscaledDeltaTime; yawTarget = float.NaN; }
        if (kb.tKey.wasPressedThisFrame) ToggleTopDown();

        // Move the area shown: WASD (relative to how the hologram faces you) or right-drag
        Vector2 move = Vector2.zero;
        float keys = radius * 0.8f * Time.unscaledDeltaTime;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += keys;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= keys;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += keys;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= keys;
        if (mouse.rightButton.isPressed) move -= mouse.delta.ReadValue() * (radius * 0.004f);
        MoveArea(move);
        if (kb.fKey.wasPressedThisFrame) CentreOnYou();

        // Scroll: zoom (one wheel notch is 120 on some systems, 1 on others)
        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0f && !overPanel) Zoom(Mathf.Clamp(Mathf.Abs(scroll) > 20f ? scroll / 120f : scroll, -3f, 3f));

        if (kb.backspaceKey.wasPressedThisFrame) UndoWaypoint();
        if (kb.cKey.wasPressedThisFrame) ClearWaypoints();
        if (kb.rKey.wasPressedThisFrame) ToggleRoundTrip();
    }

    // ---- Map actions shared by the desktop keys and the VR controllers

    // Swing between lying flat and standing up (top-down view, north at the top)
    void ToggleTopDown()
    {
        tiltTarget = tilt < 45f ? 90f : 0f;
        // Standing up shows the direction you face as "up"; turning it by your heading puts
        // north there instead. Lying back down, it turns back to north = real north.
        yawTarget = tiltTarget == 90f ? Mathf.Atan2(viewForward.x, viewForward.z) * Mathf.Rad2Deg : 0f;
    }

    // T's swing (and its turn to north-up), then the pose
    void AnimateHologram()
    {
        if (tiltTarget >= 0f)
        {
            tilt = Mathf.MoveTowards(tilt, tiltTarget, 180f * Time.unscaledDeltaTime);
            if (Mathf.Approximately(tilt, tiltTarget)) tiltTarget = -1f;
        }
        if (!float.IsNaN(yawTarget))
        {
            mapYaw = Mathf.MoveTowardsAngle(mapYaw, yawTarget, 180f * Time.unscaledDeltaTime);
            if (Mathf.Approximately(Mathf.DeltaAngle(mapYaw, yawTarget), 0f)) yawTarget = float.NaN;
        }
        PoseHologram();
    }

    // move: metres along your view when it opened (x right, y away from you)
    void MoveArea(Vector2 move)
    {
        if (move == Vector2.zero) return;
        // Screen directions -> the hologram's own axes (it can be turned).
        // Up/forward = towards the hologram's far edge whether it lies flat or stands up,
        // so only its turn (not its tilt) matters here
        Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, mapYaw, 0f))
                        * (viewRight * move.x + viewForward * move.y);
        centre += new Vector2(local.x, local.z);
        rebuildPending = true;
    }

    void CentreOnYou()
    {
        if (player == null) return;
        centre = new Vector2(player.position.x, player.position.z);
        rebuildPending = true;
    }

    // steps > 0: zoom in
    void Zoom(float steps)
    {
        radius *= Mathf.Pow(0.85f, steps);
        rebuildPending = true;
    }

    void UndoWaypoint()
    {
        if (waypoints.Count > 0) RemoveWaypoint(waypoints.Count - 1);
    }

    void ToggleRoundTrip()
    {
        roundTrip = !roundTrip;
        ScheduleReplan();
    }

    // Keep the hologram in the middle of your view: while it's open, the first-person look
    // (which you can't move then) smoothly turns to its centre, whatever it's tilted or zoomed to.
    void AimAtHologram()
    {
        Camera cam = Camera.main;
        if (panTilt == null || cam == null || holoRoot == null) return;
        Vector3 centre = holoRoot.transform.TransformPoint(new Vector3(0f, holoCentreY, 0f));
        Vector3 dir = centre - cam.transform.position;
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();
        float pan = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        float tiltDown = Mathf.Asin(Mathf.Clamp(-dir.y, -1f, 1f)) * Mathf.Rad2Deg; // + = looking down
        float k = 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime);
        panTilt.PanAxis.Value = Mathf.DeltaAngle(0f, Mathf.LerpAngle(panTilt.PanAxis.Value, pan, k)); // keep in -180..180
        panTilt.TiltAxis.Value = Mathf.Clamp(Mathf.Lerp(panTilt.TiltAxis.Value, tiltDown, k),
                                             panTilt.TiltAxis.Range.x, panTilt.TiltAxis.Range.y);
    }

    // Turn about its own up axis, then tip the far edge up toward you about a horizontal axis.
    // Tipping up, it rises to eye height (so all of it is in view) and its lower edge stays
    // clear of the ground.
    void PoseHologram()
    {
        Quaternion rot = Quaternion.AngleAxis(-tilt, viewRight) * Quaternion.Euler(0f, mapYaw, 0f);
        float up = Mathf.Sin(tilt * Mathf.Deg2Rad);
        Vector3 pos = holoBase + Vector3.up * (up * belowEyes);
        float lowestEdge = pos.y - up * hologramSize * 0.5f;
        if (lowestEdge < holoGroundY + 0.1f) pos.y += holoGroundY + 0.1f - lowestEdge;
        holoRoot.transform.SetPositionAndRotation(pos, rot);
    }

    // ---------------------------------------------------------------
    // Waypoints
    // ---------------------------------------------------------------
    void AddWaypointAt(Vector2 screen)
    {
        Camera cam = Camera.main;
        if (cam != null) AddWaypointAt(cam.ScreenPointToRay(screen));
    }

    // From the mouse (through the screen) or the VR laser
    void AddWaypointAt(Ray ray)
    {
        if (holoCollider == null) return;
        if (rebuildPending) BuildHologram(); // just zoomed/moved: click on the area you asked for
        Physics.SyncTransforms(); // the collider must match the pose drawn on screen (it may be turning)
        if (!holoCollider.Raycast(ray, out RaycastHit hit, 50f))
        {
            status = openedInVR ? "Point the laser at the hologram to add a waypoint."
                                : "Click on the hologram to add a waypoint.";
            return;
        }
        Vector2 xz = FromHolo(holoContent.InverseTransformPoint(hit.point));
        if (!terraingen.TryGetMapBounds(out Rect area) || !area.Contains(xz))
        {
            status = "That's past the edge of the map.";
            return;
        }
        if (nextWaypoint > waypoints.Count) nextWaypoint = waypoints.Count; // after a finished round trip
        var w = new Vector3(xz.x, 0f, xz.y);
        w.y = terraingen.GroundY(w);
        waypoints.Add(w);
        status = "";
        ScheduleReplan();
    }

    void RemoveWaypointNear(Vector2 screen)
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        int best = -1;
        float bestD = 30f * 30f; // pixels
        for (int i = 0; i < waypoints.Count && i < holoPins.Count; i++)
        {
            if (!holoPins[i].activeSelf) continue;
            Vector3 s = cam.WorldToScreenPoint(holoPins[i].transform.position);
            if (s.z <= 0f) continue;
            float d = ((Vector2)s - screen).sqrMagnitude;
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0) RemoveWaypoint(best);
    }

    void RemoveWaypoint(int i)
    {
        if (i < nextWaypoint) nextWaypoint--;
        waypoints.RemoveAt(i);
        if (waypoints.Count == 0) nextWaypoint = 0; // same as clearing
        ScheduleReplan();
    }

    void ClearWaypoints()
    {
        nextWaypoint = 0;
        waypoints.Clear();
        ScheduleReplan();
    }

    void ScheduleReplan()
    {
        pendingId = -1; // a reply to the request before this change no longer fits
        replanAt = Time.unscaledTime + 0.3f; // a quick burst of clicks plans once
        DrawHologramRoute();                 // pins move right away
    }

    // ---------------------------------------------------------------
    // Route
    // ---------------------------------------------------------------
    // Plans from where you stand through the waypoints you haven't reached yet
    void RequestRoute()
    {
        if (waypoints.Count == 0 || nextWaypoint >= TargetCount)
        {
            route = null;
            pendingId = -1;
            if (waypoints.Count == 0) status = "";
            SetRoute(null, null, null, null);
            return;
        }
        Vector3 here = player != null ? player.position : Vector3.zero;
        if (!hasTrip)
        {
            tripStart = here; // no spawn seen (shouldn't happen): use where you are
            hasTrip = true;
        }
        routeStart = here;
        pendingOffset = nextWaypoint;
        List<Vector3> pts = PlanPoints();
        var req = new RouteRequest { id = ++requestId, x = new float[pts.Count], z = new float[pts.Count],
                                     loop = false, return_to_start = roundTrip && hasTrip };
        for (int i = 0; i < pts.Count; i++)
        {
            req.x[i] = pts[i].x;
            req.z[i] = pts[i].z;
        }

        PythonConnection pc = PythonConnection.Instance;
        bool realMap = terraingen != null && terraingen.HasRealMap;
        if (realMap && pc != null && pc.RequestRoute(req))
        {
            pendingId = req.id;
            pendingAt = Time.unscaledTime;
            status = "Planning route...";
        }
        else
        {
            pendingId = -1;
            route = null;
            status = realMap
                ? "Route planning needs Python/server.py (not connected): straight lines shown."
                : "No real map (random Perlin terrain), so no route planning: straight lines shown.";
            DrawStraight();
        }
    }

    // You, then the waypoints still ahead, then (round trip) Home Base, where the route began.
    // The return point is sent as a normal point, so it stays the real start after re-plans.
    List<Vector3> PlanPoints()
    {
        var pts = new List<Vector3> { routeStart };
        for (int i = nextWaypoint; i < waypoints.Count; i++) pts.Add(waypoints[i]);
        if (roundTrip && hasTrip) pts.Add(tripStart);
        return pts;
    }

    // Straight lines through the remaining waypoints, when no planned route is available
    void DrawStraight()
    {
        route = null; // the panel mustn't show the old plan's distance/time/legs
        routeStart = player != null ? player.position : routeStart;
        routeOffset = nextWaypoint;
        var xs = new List<float>();
        var zs = new List<float>();
        foreach (Vector3 p in PlanPoints()) { xs.Add(p.x); zs.Add(p.z); }
        if (xs.Count < 2) { SetRoute(null, null, null, null); return; }
        var el = new float[xs.Count];
        var tiers = new int[xs.Count];
        for (int i = 0; i < el.Length; i++)
        {
            el[i] = Elevation(new Vector3(xs[i], 0f, zs[i]));
            tiers[i] = 4; // magenta: not a planned route
        }
        SetRoute(xs.ToArray(), zs.ToArray(), el, tiers);
    }

    void OnRoute(RouteMessage m)
    {
        if (m == null || m.id != pendingId) return; // an older request: ignore
        pendingId = -1;
        route = m;
        routeOffset = pendingOffset; // only now: until the reply, the old plan stays consistent
        status = m.ok ? "" : m.error;
        legRanges.Clear();
        bool hasIndices = false;
        if (m.legs != null)
            foreach (RouteLeg l in m.legs)
            {
                legRanges.Add(new Vector2Int(l.start_index, l.end_index));
                hasIndices |= l.end_index > l.start_index;
            }
        if (!hasIndices && m.x != null) // an older server.py without leg indices: show it all
            for (int i = 0; i < legRanges.Count; i++) legRanges[i] = new Vector2Int(0, m.x.Length - 1);
        if (m.x != null && m.x.Length > 1) SetRoute(m.x, m.z, m.elev, m.tier, keepLegs: true);
        else SetRoute(null, null, null, null);
    }

    // keepLegs: legRanges were set from the server's legs; otherwise every point pair is a leg
    // (the straight-line fallback goes waypoint to waypoint)
    void SetRoute(float[] xs, float[] zs, float[] elev, int[] tier, bool keepLegs = false)
    {
        if (!keepLegs)
        {
            legRanges.Clear();
            for (int i = 0; xs != null && i + 1 < xs.Length; i++) legRanges.Add(new Vector2Int(i, i + 1));
        }
        routeX = xs;
        routeZ = zs;
        routeElev = elev;
        routeTier = tier;
        DrawGroundRoute();
        DrawHologramRoute();
    }

    // On the real ground: only the leg you're on (you / the last waypoint -> the next one),
    // a flat arrowed line in hazard colours, one LineRenderer per hazard-tier run
    void DrawGroundRoute()
    {
        RouteVersion++;
        foreach (LineRenderer lr in lines) Destroy(lr.gameObject);
        lines.Clear();
        if (routeX == null || terraingen == null) return;
        int leg = nextWaypoint - routeOffset; // legs count from where the route was planned
        if (leg < 0 || leg >= legRanges.Count) return;
        int from = Mathf.Clamp(legRanges[leg].x, 0, routeX.Length - 1);
        int to = Mathf.Clamp(legRanges[leg].y, from, routeX.Length - 1);
        foreach (var (start, end, t) in TierRuns(routeTier, to + 1, from))
        {
            LineRenderer lr = MakeLine(routeRoot.transform, tierMats[t], $"Route {TierNames[t]}");
            // Flat on the ground like a painted trail (not a ribbon turned to face the camera)
            lr.useWorldSpace = true;
            lr.alignment = LineAlignment.TransformZ;
            lr.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            // Route points can be 30-100 m apart: add points every ~5 m that follow the ground,
            // or the straight pieces would cut through ridges
            var pts = new List<Vector3>();
            for (int k = start; k <= end; k++)
            {
                var a = new Vector3(routeX[k], 0f, routeZ[k]);
                int steps = 1;
                if (k < end)
                {
                    float len = new Vector2(routeX[k + 1] - routeX[k], routeZ[k + 1] - routeZ[k]).magnitude;
                    steps = Mathf.Clamp(Mathf.CeilToInt(len / groundLineStep), 1, 64);
                }
                for (int s = 0; s < (k < end ? steps : 1); s++)
                {
                    Vector3 p = k < end
                        ? Vector3.Lerp(a, new Vector3(routeX[k + 1], 0f, routeZ[k + 1]), s / (float)steps)
                        : a;
                    p.y = terraingen.GroundY(p) + 0.08f;
                    pts.Add(p);
                }
            }
            lr.positionCount = pts.Count;
            lr.SetPositions(pts.ToArray());
            lr.widthMultiplier = groundLineWidth;
            lr.textureScale = new Vector2(1f / (groundLineWidth * 3f), 1f); // one arrow per 3 widths
            lines.Add(lr);
        }
    }

    // The same route + pins on the hologram (only the part inside the area shown)
    void DrawHologramRoute()
    {
        if (holoContent == null) return;
        foreach (LineRenderer lr in holoLines) Destroy(lr.gameObject);
        holoLines.Clear();

        float lineWidth = hologramSize * 0.005f;
        if (routeX != null && routeElev != null)
            foreach (var (start, end, t) in TierRuns(routeTier, routeX.Length))
            {
                var pts = new List<Vector3>();
                for (int k = start; k <= end; k++)
                {
                    bool inside = InsideHolo(routeX[k], routeZ[k]);
                    if (inside) pts.Add(ToHolo(routeX[k], routeZ[k], routeElev[k], HoloLift));
                    if (inside && k < end) continue;
                    // Left the area shown (or the run ended): finish this piece of line
                    if (pts.Count > 1)
                    {
                        LineRenderer lr = MakeLine(holoContent, tierMats[t], "Hologram route");
                        lr.positionCount = pts.Count;
                        lr.SetPositions(pts.ToArray());
                        lr.widthMultiplier = lineWidth;
                        lr.textureScale = new Vector2(1f / (lineWidth * 3f), 1f);
                        holoLines.Add(lr);
                    }
                    pts.Clear();
                }
            }

        // Waypoint pins
        while (holoPins.Count < waypoints.Count)
        {
            GameObject pin = MakeBeacon(beaconMat, "Hologram pin");
            pin.transform.SetParent(holoContent, false);
            holoPins.Add(pin);
        }
        for (int i = 0; i < holoPins.Count; i++)
        {
            bool show = i < waypoints.Count && InsideHolo(waypoints[i].x, waypoints[i].z);
            holoPins[i].SetActive(show);
            if (show) PlacePin(holoPins[i], waypoints[i], 1f);
        }
        if (holoHome != null)
        {
            if (HasHome) PlacePin(holoHome, tripStart, 1.3f);
            else holoHome.SetActive(false);
        }
    }

    void PlacePin(GameObject pin, Vector3 world, float size)
    {
        bool show = InsideHolo(world.x, world.z);
        pin.SetActive(show && open);
        if (!show) return;
        float r = hologramSize * 0.007f * size, h = hologramSize * 0.06f * size;
        pin.transform.localScale = new Vector3(r * 2f, h * 0.5f, r * 2f); // cylinder is 2 tall
        pin.transform.localPosition = ToHolo(world.x, world.z, Elevation(world)) + Vector3.up * (h * 0.5f);
        pin.transform.localRotation = Quaternion.identity;
    }

    // Runs of consecutive points with the same hazard tier; neighbouring runs share a point
    static IEnumerable<(int start, int end, int tier)> TierRuns(int[] tier, int n, int first = 0)
    {
        int start = first;
        for (int i = first + 1; i <= n; i++)
        {
            int t = TierAt(tier, start);
            if (i < n && TierAt(tier, i) == t) continue;
            int end = Mathf.Min(i, n - 1);
            if (end > start) yield return (start, end, t);
            start = i;
        }
    }

    static int TierAt(int[] tier, int i) =>
        tier != null && i < tier.Length ? Mathf.Clamp(tier[i], 0, TierColors.Length - 1) : 0;

    // ---------------------------------------------------------------
    // Ground view: reaching waypoints, and the compass arrows
    // ---------------------------------------------------------------
    // Targets in order: the waypoints, then (round trip) back to Home Base
    int TargetCount => waypoints.Count + (roundTrip && waypoints.Count > 0 ? 1 : 0);
    Vector3 Target(int i) => i < waypoints.Count ? waypoints[i] : tripStart;

    void UpdateGuidance()
    {
        bool active = nextWaypoint < TargetCount && player != null;
        if (active)
        {
            Vector3 p = player.position, w = Target(nextWaypoint);
            if (new Vector2(p.x - w.x, p.z - w.z).magnitude < arriveDistance)
            {
                nextWaypoint++;
                DrawGroundRoute(); // the next leg appears
                status = nextWaypoint >= TargetCount ? (roundTrip ? "Back at Home Base: route complete!" : "Route complete!") :
                         nextWaypoint >= waypoints.Count ? "Last waypoint reached: head back to Home Base." :
                         $"Reached waypoint {nextWaypoint}.";
                active = nextWaypoint < TargetCount;
            }
        }

        SteerCompassArrows(active);
        if (active) arrowTarget.position = Target(nextWaypoint);

        for (int i = 0; i < holoPins.Count && i < waypoints.Count; i++)
            SetMaterial(holoPins[i], i < nextWaypoint ? doneMat : i == nextWaypoint ? nextMat : beaconMat);
    }

    // Red compass arrow -> the next waypoint; green -> Home Base. Found by their
    // images (arrowred / arrowgreen), so renaming the objects doesn't matter. Without a route
    // they're hidden (nothing to point at).
    void SteerCompassArrows(bool active)
    {
        if (Time.unscaledTime - compassSearchedAt > 2f && (redArrow == null || greenArrow == null))
        {
            compassSearchedAt = Time.unscaledTime;
            foreach (Image img in FindObjectsByType<Image>(FindObjectsInactive.Exclude))
            {
                string tex = img.sprite != null && img.sprite.texture != null ? img.sprite.texture.name : "";
                if (redArrow == null && tex.Contains("arrowred")) redArrow = ArrowOn(img, true);
                else if (greenArrow == null && tex.Contains("arrowgreen")) greenArrow = ArrowOn(img, false);
            }
        }

        if (redArrow != null) redArrow.orbitRadius = compassArrowRadius;
        if (greenArrow != null) greenArrow.orbitRadius = compassArrowRadius;
        // In a headset the compass turns with your head (VRRig), so the arrows must read it too;
        // VRRig only retargets arrows that existed when it set up, not the red one added here
        Transform facing = Facing;
        if (redArrow != null && redArrow.playerTransform == player) redArrow.playerTransform = facing;
        if (greenArrow != null && greenArrow.playerTransform == player) greenArrow.playerTransform = facing;

        // Shown only when they have something to point at
        if (redArrow != null)
        {
            redArrow.gameObject.SetActive(active);
            if (active)
            {
                redArrow.enabled = true;
                redArrow.dest = arrowTarget;
            }
        }
        if (greenArrow != null)
        {
            bool trip = HasHome; // always, route or not
            greenArrow.gameObject.SetActive(trip);
            if (trip)
            {
                startTarget.position = tripStart;
                greenArrow.enabled = true;
                greenArrow.dest = startTarget;
            }
        }
    }


    void RestoreRed()
    {
        var rt = (RectTransform)redArrow.compassDial;
        rt.pivot = redRestPivot;
        rt.anchoredPosition = redRestPos;
        rt.localEulerAngles = redRest;
    }

    // Home Base: a green beam where the route began, shown the whole time there's a route
    // (it's the target's own beam when you're heading back)
    bool HasHome => hasTrip;

    void UpdateHomeSphere()
    {
        bool show = showHomeSphere && HasHome && terraingen != null;
        if (!show)
        {
            if (homeSphere != null) homeSphere.SetActive(false);
            return;
        }
        if (homeSphere == null)
        {
            homeSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            homeSphere.name = "Home Base";
            Destroy(homeSphere.GetComponent<Collider>()); // display only
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            var mat = lit != null ? new Material(lit) : MakeMaterial(new Color(1f, 0.45f, 0.1f));
            var orange = new Color(1f, 0.45f, 0.1f);
            mat.color = orange;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", orange);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.35f);
            if (mat.HasProperty("_EmissionColor")) // a faint glow so it's easy to spot
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", orange * 0.25f);
            }
            homeSphere.GetComponent<Renderer>().sharedMaterial = mat;
        }
        homeSphere.SetActive(true);
        Vector3 at = tripStart;
        at.y = terraingen.GroundY(at) + homeSphereSize * 0.4f; // sits a little into the ground
        homeSphere.transform.position = at;
        homeSphere.transform.localScale = Vector3.one * homeSphereSize;
    }

    void UpdateHomeBeam()
    {
        UpdateHomeSphere();
        bool headingHome = waypoints.Count > 0 && nextWaypoint >= waypoints.Count && nextWaypoint < TargetCount;
        bool atHome = player != null &&
                      new Vector2(player.position.x - tripStart.x, player.position.z - tripStart.z).magnitude < arriveDistance;
        bool show = HasHome && terraingen != null && !headingHome && !atHome;
        if (!show)
        {
            if (homeBeam != null) homeBeam.SetActive(false);
            return;
        }
        if (homeBeam == null) homeBeam = MakeBeacon(homeMat, "EVA Home Base Beam");
        homeBeam.SetActive(true);
        Vector3 at = tripStart;
        at.y = terraingen.GroundY(at);
        homeBeam.transform.localScale = new Vector3(beaconRadius * 2f, beaconHeight * 0.5f, beaconRadius * 2f);
        homeBeam.transform.position = at + Vector3.up * (beaconHeight * 0.5f);
    }

    // The PointAt turning this arrow image; the red arrow has none in the scene, so add one
    PointAt ArrowOn(Image img, bool red)
    {
        PointAt pa = img.GetComponent<PointAt>();
        if (pa == null)
        {
            pa = img.gameObject.AddComponent<PointAt>();
            pa.compassDial = img.transform;
            pa.playerTransform = player;
            pa.enabled = false;
            if (red)
            {
                redAdded = true;
                redRest = img.transform.localEulerAngles;
                redRestPos = img.rectTransform.anchoredPosition;
                redRestPivot = img.rectTransform.pivot;
            }
        }
        else if (!originalDest.ContainsKey(pa)) originalDest[pa] = pa.dest;
        if (pa.playerTransform == null) pa.playerTransform = player;
        if (pa.compassDial == null) pa.compassDial = img.transform;
        pa.orbitCompass = true;
        return pa;
    }

    static void SetMaterial(GameObject go, Material m)
    {
        Renderer r = go.GetComponent<Renderer>();
        if (r.sharedMaterial != m) r.sharedMaterial = m;
    }

    // The next waypoint on the real ground: a light beam you can see from far away, and a
    // pulsing ring on the ground; step inside the ring and the next waypoint appears
    void UpdateGroundSizes()
    {
        UpdateHomeBeam();
        bool show = nextWaypoint < TargetCount && terraingen != null;
        if (!show)
        {
            if (targetBeam != null) targetBeam.SetActive(false);
            if (targetRing != null) targetRing.gameObject.SetActive(false);
            return;
        }
        if (targetBeam == null)
        {
            targetBeam = MakeBeacon(beamMat, "EVA Next Waypoint Beam");
            targetRing = MakeLine(null, ringMat, "EVA Next Waypoint Ring");
            targetRing.useWorldSpace = true;
            targetRing.loop = true;
            targetRing.alignment = LineAlignment.TransformZ;
            targetRing.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            targetRing.textureMode = LineTextureMode.Stretch;
        }
        targetBeam.SetActive(true);
        targetRing.gameObject.SetActive(true);

        Vector3 at = Target(nextWaypoint);
        at.y = terraingen.GroundY(at);
        targetBeam.transform.localScale = new Vector3(beaconRadius * 2f, beaconHeight * 0.5f, beaconRadius * 2f);
        targetBeam.transform.position = at + Vector3.up * (beaconHeight * 0.5f);

        if (float.IsNaN(ringAt.x) || (at - ringAt).sqrMagnitude > 0.01f) // new spot: lay the ring on the ground
        {
            ringAt = at;
            const int segments = 72;
            targetRing.positionCount = segments;
            for (int k = 0; k < segments; k++)
            {
                float a = k * Mathf.PI * 2f / segments;
                var p = at + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * arriveDistance;
                p.y = terraingen.GroundY(p) + 0.1f;
                targetRing.SetPosition(k, p);
            }
        }
        targetRing.widthMultiplier = 0.35f + 0.2f * Mathf.Sin(Time.unscaledTime * 4f); // pulse
    }

    // ---------------------------------------------------------------
    // Building blocks
    // ---------------------------------------------------------------
    static LineRenderer MakeLine(Transform parent, Material mat, string name)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.textureMode = LineTextureMode.Tile; // the arrow texture repeats along the line
        lr.sharedMaterial = mat;
        lr.numCapVertices = 2;
        lr.numCornerVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    static GameObject MakeBeacon(Material mat, string name)
    {
        GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        b.name = name;
        Destroy(b.GetComponent<Collider>()); // never blocks the player or clicks
        SetupRenderer(b.GetComponent<Renderer>(), mat);
        return b;
    }

    static void SetupRenderer(Renderer r, Material m)
    {
        r.sharedMaterial = m;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    // A tile of the route line: its hazard colour with a white ">" pointing along the line
    // (texture u runs from the first point to the last, so the arrows point the way to go)
    static Texture2D ArrowTexture(Color background)
    {
        const int w = 64, h = 32;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat };
        var px = new Color[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
                float tip = 0.5f - Mathf.Abs(v - 0.5f);  // 0.5 on the centre line, 0 at the edges
                float d = u - 0.2f - 0.5f * tip;         // ">" : the centre is furthest ahead
                bool arrow = d >= 0f && d < 0.22f && Mathf.Abs(v - 0.5f) < 0.42f;
                px[y * w + x] = arrow ? Color.white : background;
            }
        tex.SetPixels(px);
        tex.Apply(true, true);
        return tex;
    }

    static Material MakeMaterial(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh) { color = c };
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        return m;
    }

    // ---------------------------------------------------------------
    // Labels and panels
    // ---------------------------------------------------------------
    void OnGUI()
    {
        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = true };
            boxStyle.normal.textColor = Color.white;
            smallStyle = new GUIStyle(boxStyle) { fontSize = 12, alignment = TextAnchor.MiddleCenter, wordWrap = false };
        }
        helpRect = routeRect = Rect.zero;
        if (MarsmapInput.XRActive) return; // a headset doesn't draw IMGUI: MapViewVR.cs has world-space versions
        Camera cam = Camera.main;

        // Hologram open: a number on every pin
        if (cam != null && open)
            for (int i = 0; i < waypoints.Count && i < holoPins.Count; i++)
            {
                GameObject marker = holoPins[i];
                if (marker == null || !marker.activeInHierarchy) continue;
                Vector3 top = marker.transform.position + marker.transform.up * marker.transform.lossyScale.y;
                Vector3 s = cam.WorldToScreenPoint(top);
                if (s.z <= 0f) continue;
                string label = $"{i + 1}";
                Vector2 size = smallStyle.CalcSize(new GUIContent(label));
                var r = new Rect(s.x - size.x / 2f - 4f, Screen.height - s.y - size.y - 4f, size.x + 8f, size.y + 2f);
                DrawBox(r, i == nextWaypoint ? new Color(0f, 0.35f, 0.45f, 0.8f) : new Color(0f, 0f, 0f, 0.7f));
                GUI.Label(r, label, smallStyle);
            }

        // Home Base: on the hologram pin, or on the ground whenever it's in view
        if (cam != null && HasHome)
        {
            GameObject home = open ? holoHome : homeBeam;
            if (home != null && home.activeInHierarchy)
            {
                Vector3 top = home.transform.position + home.transform.up * home.transform.lossyScale.y;
                Vector3 s = cam.WorldToScreenPoint(top);
                if (s.z > 0f)
                {
                    string label = open ? "Home Base" : $"<b>HOME BASE</b>  {FormatDistance(FlatDistance(player, tripStart))}";
                    Vector2 size = smallStyle.CalcSize(new GUIContent(label));
                    var r = new Rect(s.x - size.x / 2f - 5f, Screen.height - s.y - size.y - 4f, size.x + 10f, size.y + 2f);
                    DrawBox(r, new Color(0.02f, 0.3f, 0.1f, 0.85f));
                    GUI.Label(r, label, smallStyle);
                }
            }
        }

        // On the ground: one big tag for the next waypoint, kept on screen (with an arrow at the
        // edge when it's off to the side or behind you)
        if (cam != null && !open && player != null && nextWaypoint < TargetCount) TargetTag(cam);

        if (open)
        {
            string help = $"<b><color=#F0915A>EVA MAP</color></b>   <size=11>{FormatDistance(2f * radius)} across, " +
                          $"height x{exaggeration:0.#}   ·   Drag sideways turn · Drag up/down tilt · T top-down · Scroll zoom · WASD / right-drag move · " +
                          "Click add waypoint · Shift+click remove · Backspace undo · C clear · " +
                          $"R return to Home Base ({(roundTrip ? "on" : "off")}) · F centre on you · M close</size>";
            float w = Mathf.Min(Screen.width - 20f, 760f);
            float h = boxStyle.CalcHeight(new GUIContent(help), w - 16f) + 10f;
            helpRect = new Rect((Screen.width - w) / 2f, 10f, w, h);
            DrawBox(helpRect, new Color(0f, 0f, 0f, 0.7f));
            GUI.Label(new Rect(helpRect.x + 8f, helpRect.y + 5f, w - 16f, h - 10f), help, boxStyle);
        }

        string text = RouteText();
        if (string.IsNullOrEmpty(text)) return;
        float pw = open ? 330f : 260f;
        float ph = boxStyle.CalcHeight(new GUIContent(text), pw - 16f) + 12f;
        routeRect = new Rect(Screen.width - pw - 10f, Screen.height - ph - 10f, pw, ph);
        DrawBox(routeRect, new Color(0.06f, 0.035f, 0.025f, 0.8f));
        GUI.Label(new Rect(routeRect.x + 8f, routeRect.y + 6f, pw - 16f, ph - 12f), text, boxStyle);
    }

    GUIStyle tagStyle;

    void TargetTag(Camera cam)
    {
        if (tagStyle == null)
        {
            tagStyle = new GUIStyle(boxStyle) { fontSize = 15, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            tagStyle.normal.textColor = Color.white;
        }
        Vector3 at = Target(nextWaypoint);
        at.y = terraingen != null ? terraingen.GroundY(at) + beaconHeight * 0.35f : at.y;
        bool home = nextWaypoint >= waypoints.Count;
        string name = home ? "HOME BASE" : $"WAYPOINT {nextWaypoint + 1}";
        string text = $"<b>{name}</b>\n{FormatDistance(new Vector2(player.position.x - at.x, player.position.z - at.z).magnitude)}";

        Vector3 s = cam.WorldToScreenPoint(at);
        bool behind = s.z < 0f;
        if (behind) { s.x = Screen.width - s.x; s.y = Screen.height - s.y; } // un-mirror: the arrow shows which way to turn
        float margin = 60f;
        bool offScreen = behind || s.x < margin || s.x > Screen.width - margin
                         || s.y < margin || s.y > Screen.height - margin;
        Vector2 p = new Vector2(Mathf.Clamp(s.x, margin, Screen.width - margin),
                                Mathf.Clamp(Screen.height - s.y, margin, Screen.height - margin));
        if (behind) p.y = Screen.height - margin; // behind you: along the bottom edge

        if (offScreen)
        {
            Vector2 c = new Vector2(Screen.width / 2f, Screen.height / 2f);
            Vector2 d = p - c;
            string arrow = Mathf.Abs(d.x) > Mathf.Abs(d.y) ? (d.x > 0 ? "▶" : "◀") : (d.y > 0 ? "▼" : "▲");
            text = Mathf.Abs(d.x) > Mathf.Abs(d.y) && d.x < 0 ? $"{arrow}  {text}" : $"{text}  {arrow}";
        }
        else text += "\n▼";

        Vector2 size = tagStyle.CalcSize(new GUIContent(text));
        var r = new Rect(p.x - size.x / 2f - 8f, p.y - size.y / 2f - 4f, size.x + 16f, size.y + 8f);
        float pulse = 0.75f + 0.15f * Mathf.Sin(Time.unscaledTime * 4f);
        r.x = Mathf.Clamp(r.x, 4f, Screen.width - r.width - 4f);
        r.y = Mathf.Clamp(r.y, 4f, Screen.height - r.height - 4f);
        if (r.Overlaps(Minimap.BottomLeftRect)) r.y = Minimap.BottomLeftRect.yMin - r.height - 4f; // above the minimap
        DrawBox(r, home ? new Color(0.02f, 0.35f, 0.12f, pulse) : new Color(0f, 0.3f, 0.4f, pulse));
        GUI.Label(r, text, tagStyle);
    }

    // withStatus false: leave the status line out (the VR panel shows it at the top instead)
    string RouteText(bool withStatus = true)
    {
        var sb = new StringBuilder();
        if (!open)
        {
            if (waypoints.Count == 0) return status;
            if (nextWaypoint < TargetCount && player != null)
                sb.Append((nextWaypoint < waypoints.Count
                              ? $"<b>Next: waypoint {nextWaypoint + 1} of {waypoints.Count}</b>  "
                              : "<b>Next: back to Home Base</b>  ") +
                          FormatDistance(new Vector2(player.position.x - Target(nextWaypoint).x,
                                                     player.position.z - Target(nextWaypoint).z).magnitude));
            else
                sb.Append("<b>Route complete</b>");
            if (route != null && route.distance_m > 0f)
                sb.Append($"\nRoute {route.distance_m / 1000f:F2} km · {FormatMinutes(route.minutes)} · " +
                          DifficultyTag(route.difficulty));
            if (!string.IsNullOrEmpty(status)) sb.Append($"\n<size=11>{status}</size>");
            sb.Append("\n<size=11>M: map</size>");
            return sb.ToString();
        }

        if (waypoints.Count == 0)
            return "<b>EVA ROUTE</b>\n" + (openedInVR ? "Point the laser at the hologram and pull the trigger to add waypoints."
                                                     : "Click on the hologram to add waypoints.") +
                   " The route starts where you stand.\n" +
                   "<size=11>Colours: " + Legend() + "</size>" +
                   (!withStatus || string.IsNullOrEmpty(status) ? "" : $"\n<size=11>{status}</size>");

        sb.Append($"<b>EVA ROUTE</b>  {waypoints.Count} waypoint{(waypoints.Count == 1 ? "" : "s")}" +
                  (roundTrip ? ", back to Home Base" : ""));
        if (route != null && route.distance_m > 0f)
        {
            sb.Append($"   {DifficultyTag(route.difficulty)}\n");
            sb.Append($"Distance <b>{route.distance_m / 1000f:F2} km</b>   EVA time <b>{FormatMinutes(route.minutes)}</b>\n");
            sb.Append($"Climb +{route.gain_m:F0} m / descent -{route.loss_m:F0} m\n");
            sb.Append($"Steepest {route.max_climb_deg:F1}° up, {route.max_descent_deg:F1}° down; " +
                      $"side slope up to {route.max_side_deg:F1}°\n");
            if (route.zone_pct != null)
            {
                sb.Append("Ground ");
                for (int t = 0; t < route.zone_pct.Length && t < TierHex.Length; t++)
                    if (route.zone_pct[t] >= 0.5f)
                        sb.Append($"<color={TierHex[t]}>■</color> {route.zone_pct[t]:F0}% {TierNames[t]}  ");
                sb.Append('\n');
            }
            if (route.legs != null)
                foreach (RouteLeg l in route.legs)
                    sb.Append($"<size=11>{(l.ok ? "" : "<color=#ff3cf0>")}{LegName(l.from_wp)} → {LegName(l.to_wp)}: " +
                              $"{l.distance_m / 1000f:F2} km, {FormatMinutes(l.minutes)}{(l.ok ? "" : " NO SAFE PATH</color>")}</size>\n");
        }
        else sb.Append('\n');
        if (route != null && route.hazards != null) // also when there's no route (e.g. all snapped)
            foreach (string h in route.hazards)
                sb.Append($"<size=11><color=#F0C080>! {h}</color></size>\n");
        if (withStatus && !string.IsNullOrEmpty(status)) sb.Append($"<size=11>{status}</size>");
        return sb.ToString().TrimEnd('\n');
    }

    // The server numbers the waypoints it was sent; add the ones already reached
    string LegName(string wp) =>
        !int.TryParse(wp, out int i) ? (wp == "start" ? "you" : wp == "home" ? "Home Base" : wp)
        : i + routeOffset > waypoints.Count ? "Home Base" : (i + routeOffset).ToString();

    static string Legend()
    {
        var sb = new StringBuilder();
        string[] ranges = { "0-10°", "10-20°", "20-30°", ">30°" };
        for (int t = 0; t < 4; t++) sb.Append($"<color={TierHex[t]}>■</color> {ranges[t]} {TierNames[t]}  ");
        return sb.ToString();
    }

    static float FlatDistance(Transform a, Vector3 b) =>
        a == null ? 0f : new Vector2(a.position.x - b.x, a.position.z - b.z).magnitude;

    static string DifficultyTag(string d)
    {
        string c = d == "EASY" ? "#2ecc71" : d == "MODERATE" ? "#f1c40f" : "#e74c3c";
        return $"<b><color={c}>{d}</color></b>";
    }

    static string FormatMinutes(float minutes)
    {
        int m = Mathf.RoundToInt(minutes);
        return m >= 60 ? $"{m / 60} h {m % 60:00} min" : $"{m} min";
    }

    static string FormatDistance(float metres) =>
        metres >= 1000f ? $"{metres / 1000f:F2} km" : $"{metres:F0} m";

    static void DrawBox(Rect r, Color c)
    {
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
    }
}
