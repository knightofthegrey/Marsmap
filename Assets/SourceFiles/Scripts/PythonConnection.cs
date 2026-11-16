using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using NativeWebSocket;

// ---------------------------------------------------------------------------
// Message classes. These must match the schema at the top of Python/feeder.py.
// JsonUtility maps JSON keys to field names exactly, so keep the snake_case.
// ---------------------------------------------------------------------------

[Serializable]
public class WsEnvelope
{
    public int v;
    public string type;
}

[Serializable]
public class TelemetryMessage
{
    public float t_eva_s;
    public float lmst_h;           // Mars local time in hours (10.5 = 10:30)
    public float dose_rate_uGy_h;  // RAD
    public float dose_total_mGy;   // dose accumulated since EVA start
    public float temp_c;           // REMS ambient air temperature
    public float pressure_pa;      // REMS
    public string source;          // data credit, e.g. "MSL RAD+REMS sol 4560 (2025-06-03), replayed"
    // No wind: Curiosity's REMS wind sensor returns no data for these sols.

    // REMS UV sensor bands (W/m^2). 0 when the samples have no UV.
    public float uv_a_wm2, uv_b_wm2, uv_c_wm2, uv_abc_wm2;

    // Added by the alert detector (Python/alerts.py); estimates, not sensor readings.
    public float uv_index;             // estimated UV index (WHO scale) from the UV bands
    public float dust_tau;             // extra dust opacity, estimated from UV vs the sol's normal
    public float pressure_anomaly_pa;  // pressure minus the sol's normal at this local time
    public float dose_expected_uGy_h;  // the sol's normal dose rate at this local time
    public string scenario;            // non-empty while a scripted event changes the data
}

/// <summary>An environment alert from Python/alerts.py. Level "clear" removes it.</summary>
[Serializable]
public class AlertMessage
{
    public float t_eva_s;
    public string category;  // "dust" | "radiation" | "uv"
    public string level;     // "advisory" | "warning" | "danger" | "clear"
    public string title;     // e.g. "DUST WARNING"
    public string text;
    public bool update;      // same level, new details (no new sound)
    public float value;      // the number behind it (opacity, uGy/h, UV index)
    public float eta_min;    // dust warning: estimated arrival, EVA minutes after t_eva_s (0 = none)
}

[Serializable]
public class EvaEventMessage
{
    public float t_eva_s;
    public string id;        // e.g. "dust_gust", "nav_degraded"
    public string severity;  // "caution" | "warning"
    public string text;
    public float duration_s; // optional: how long the effect lasts in real seconds (0 = default)
}

[Serializable]
public class EarthNoteMessage
{
    public float t_eva_s;
    public string text;
    public float delay_s;    // one-way light delay the note "travelled" with
}

[Serializable]
public class StatusMessage
{
    public string state;     // "started" | "finished"
    public float speed;
    public float dose_total_mGy;
}

// Sent by server.py right before the heightmap when it serves a Python/site/ folder.
[Serializable]
public class SiteLayer
{
    public string name;    // "photo" | "slope" | "thermal"; matches the TEXL message's layer name
    public string label;   // short UI label
    public string credit;  // credit / legend line
}

[Serializable]
public class RouteRequest
{
    public int v = PythonConnection.SchemaVersion;
    public string type = "plan_route";
    public int id;
    public float[] x;      // Unity world metres; the first point is where the astronaut is
    public float[] z;
    public bool loop;      // come back to the first point
    public bool return_to_start; // the last point is the route's start: only changes the wording
}

[Serializable]
public class RouteLeg
{
    public string from_wp;   // "start", "1", "2", ...
    public string to_wp;
    public bool ok;          // false = no safe path on this leg
    public float distance_m;
    public float minutes;
    public int start_index;  // this leg is points x[start_index..end_index] of the route
    public int end_index;
}

// Reply to RouteRequest, planned by Python/route_planner.py (rules from Mars_mapping(3).ipynb)
[Serializable]
public class RouteMessage
{
    public int v;
    public string type;
    public int id;
    public bool ok;
    public string error;
    public float[] x;        // route points, Unity world metres
    public float[] z;
    public float[] elev;     // real elevation (m)
    public int[] tier;       // 0 optimal, 1 caution, 2 high risk, 3 impassable, 4 no safe path
    public RouteLeg[] legs;
    public float distance_m;
    public float minutes;    // EVA time (Tobler's hiking function for Mars)
    public float gain_m;
    public float loss_m;
    public float max_climb_deg;
    public float max_descent_deg;
    public float max_side_deg;
    public string difficulty; // EASY / MODERATE / HARD
    public float score;
    public float[] zone_pct;  // share of the distance in each tier
    public string[] hazards;
    public float spacing_m;   // planning grid spacing
}

[Serializable]
public class SiteMessage
{
    public int v;
    public string type;
    public string name;
    public int sol;
    public string earth_date;
    public float center_lat;
    public float center_lon;   // east-positive, 0..360
    public float size_m;
    public float vertical_exaggeration;
    public SiteLayer[] layers;
}

/// <summary>
/// WebSocket link to Python/server.py. Keeps reconnecting until the server is up.
///  - Binary "HMAP" messages (JMARS heightmap) go to Terraingen.ApplyHeightmap.
///  - Binary "TILE" / "TIMG" messages (a --polygon too big for one terrain, sent as a grid of
///    tiles + their images) go to Terraingen.ApplyTile / ApplyTileImage.
///  - Binary "TEXL" messages (a named site image: photo, slope, thermal) and the "site" JSON
///    message go to TerrainOverlay, which drapes the images over the terrain (L cycles them).
///  - JSON messages (telemetry, events, Earth notes, status from feeder.py) are parsed
///    and raised as typed C# events on the main thread.
///
/// Other scripts use it like:
///   PythonConnection.Instance.TelemetryReceived += t => doseText.text = $"{t.dose_total_mGy:F3} mGy";
///
/// Terraingen adds this component if it isn't in the scene; if there is no Terraingen
/// either, one is created automatically at startup.
/// </summary>
public class PythonConnection : MonoBehaviour
{
    public const int SchemaVersion = 1;
    const int HeightmapHeaderSize = 24; // "HMAP" + int32 res + 4 floats (see server.py)

    public static PythonConnection Instance { get; private set; }

    [SerializeField] private string url = "ws://127.0.0.1:8765";
    [SerializeField] private Terraingen terraingen; // optional: found automatically
    [SerializeField] private float reconnectDelaySeconds = 2f;
    [Tooltip("Log every incoming JSON message to the Console (telemetry arrives ~2x per second).")]
    [SerializeField] private bool logMessages = false;
    [Tooltip("Use the vertical exaggeration the server's site.json asks for (1 = true scale) " +
             "instead of Terraingen's own setting.")]
    [SerializeField] private bool useSiteVerticalExaggeration = true;

    public event Action<bool> ConnectionChanged;
    public event Action<SiteMessage> SiteReceived;
    public event Action<TelemetryMessage> TelemetryReceived;
    public event Action<EvaEventMessage> EventReceived;
    public event Action<EarthNoteMessage> EarthNoteReceived;
    public event Action<StatusMessage> StatusReceived;
    public event Action<RouteMessage> RouteReceived;
    public event Action<AlertMessage> AlertReceived;

    public bool IsConnected => websocket != null && websocket.State == WebSocketState.Open;
    public TelemetryMessage LatestTelemetry { get; private set; }
    public StatusMessage LatestStatus { get; private set; }
    public SiteMessage LatestSite { get; private set; }

    private WebSocket websocket;
    private TerrainOverlay overlay;
    private bool running;
    private bool warnedUnreachable;
    private bool wasOpen;
    private bool everConnected;
    private int loopId;             // only the newest socket loop may keep running
    private OfflineReplay offline;  // set while the offline bundle is in use

    const string DefaultUrl = "ws://127.0.0.1:8765";

    /// <summary>True when data comes from the offline bundle instead of the server.</summary>
    public bool IsOffline => offline != null;

    // Reset statics when "Enter Play Mode Options" skips the domain reload.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void EnsureExists()
    {
        if (FindAnyObjectByType<PythonConnection>() != null) return;
        // Our own object, so it is safe to keep it alive across scene loads. When Terraingen
        // adds this component to the Terrain instead, that object is left alone.
        var go = new GameObject(nameof(PythonConnection));
        DontDestroyOnLoad(go);
        go.AddComponent<PythonConnection>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
        // Assets/Resources/marsmap_config.json sets the address (a headset needs the laptop's
        // Wi-Fi IP); a value typed into the Inspector still wins.
        if (url == DefaultUrl) url = MarsmapConfig.ServerUrl;

        switch (MarsmapConfig.Mode)
        {
            case "offline":
                StartOffline("mode is \"offline\" in marsmap_config.json");
                return;
            case "server":
                break;
            default:  // "auto": the server if it answers soon, else the offline bundle
                Invoke(nameof(AutoOfflineCheck), Mathf.Max(0.5f, MarsmapConfig.OfflineAfterSeconds));
                break;
        }
        RunSocketLoop();
    }

    // Auto mode falls back to the bundle only in a headset; on desktop it keeps waiting for the
    // server as before (set "mode": "offline" to use the bundle on desktop).
    void AutoOfflineCheck()
    {
        if (!everConnected && offline == null && MarsmapInput.XRActive)
            StartOffline($"no server at {url} after {MarsmapConfig.OfflineAfterSeconds:0.#} s");
    }

    // Stop the socket and play the bundle in StreamingAssets/Marsmap instead (export_offline.py).
    void StartOffline(string reason)
    {
        running = false;
        websocket?.CancelConnection();
        offline = gameObject.AddComponent<OfflineReplay>();
        offline.Begin(this, terraingen, reason);
    }

    /// <summary>Called by OfflineReplay when there is no bundle: go back to the server.</summary>
    internal void OfflineUnavailable()
    {
        if (offline != null) Destroy(offline);
        offline = null;
        if (!running) RunSocketLoop();
    }

    /// <summary>Handle bytes exactly as if they had arrived over the WebSocket (offline mode).</summary>
    public void InjectMessage(byte[] bytes) => HandleMessage(bytes);

    private async void RunSocketLoop()
    {
        // An older loop may still be asleep in Task.Delay after StartOffline; the id makes it
        // stop when it wakes, so two loops never run at once (no doubled messages).
        int id = ++loopId;
        Debug.Log($"[PythonConnection] Connecting to {url}");
        running = true;
        while (running && id == loopId)
        {
            websocket = new WebSocket(url);
            websocket.OnOpen += HandleOpen;
            websocket.OnMessage += HandleMessage;
            websocket.OnError += HandleError;
            websocket.OnClose += HandleClose;

            // Returns only when the connection fails or closes.
            await websocket.Connect();

            if (!running || id != loopId) break;
            await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds));
        }
    }

    private void HandleOpen()
    {
        if (offline != null)  // the offline bundle took over; don't mix two data sources
        {
            websocket?.CancelConnection();
            return;
        }
        warnedUnreachable = false;
        wasOpen = true;
        everConnected = true;
        Debug.Log($"[PythonConnection] Connected to {url}");
        SendJson($"{{\"v\":{SchemaVersion},\"type\":\"hello\",\"from\":\"unity\"}}");
        // Only while Terraingen is still waiting: respects its "Connect To Python" toggle, and
        // once the heightmap or the Perlin fallback is in, a reconnect never rebuilds the
        // terrain (or moves the player) mid-demo.
        if (terraingen != null && terraingen.WaitingForHeightmap)
            SendJson("{\"type\":\"request_heightmap\"}");
        ConnectionChanged?.Invoke(true);
    }

    private void HandleError(string error)
    {
        if (warnedUnreachable || offline != null) return;  // offline: we cancelled it ourselves
        warnedUnreachable = true;
        Debug.LogWarning($"[PythonConnection] Can't reach {url} ({error}). " +
                         $"Retrying every {reconnectDelaySeconds:0.#}s - is Python/server.py running?");
    }

    private void HandleClose(WebSocketCloseCode code)
    {
        if (!wasOpen) return;  // a failed connect attempt, already reported by HandleError
        wasOpen = false;
        Debug.Log($"[PythonConnection] Disconnected ({code})");
        ConnectionChanged?.Invoke(false);
    }

    private void HandleMessage(byte[] bytes)
    {
        // Never let an exception escape into NativeWebSocket: its dispatcher stops delivering
        // ALL later messages on this connection if one handler throws (bad heightmap size,
        // a HUD script's bug, ...). Log it and keep the telemetry flowing.
        try
        {
            DispatchMessage(bytes);
        }
        catch (Exception e)
        {
            Debug.LogException(e, this);
        }
    }

    private void DispatchMessage(byte[] bytes)
    {
        // Heightmap messages are binary and start with the magic bytes "HMAP".
        if (bytes.Length > HeightmapHeaderSize && bytes[0] == 'H' && bytes[1] == 'M'
            && bytes[2] == 'A' && bytes[3] == 'P')
        {
            if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
            if (terraingen != null) terraingen.ApplyHeightmap(bytes);
            else Debug.LogError("[PythonConnection] Got a heightmap but there is no Terraingen in the scene");
            return;
        }

        // Big --polygon areas: one "TILE" heightmap (+ "TIMG" image) per piece of a tile grid.
        bool tile = bytes.Length > 44 && bytes[0] == 'T' && bytes[1] == 'I' && bytes[2] == 'L' && bytes[3] == 'E';
        bool tileImage = bytes.Length > 12 && bytes[0] == 'T' && bytes[1] == 'I' && bytes[2] == 'M' && bytes[3] == 'G';
        if (tile || tileImage)
        {
            if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
            if (terraingen == null) Debug.LogError("[PythonConnection] Got a terrain tile but there is no Terraingen in the scene");
            else if (tile) terraingen.ApplyTile(bytes);
            else terraingen.ApplyTileImage(bytes);
            return;
        }

        // Site images (photo / slope / thermal) are binary and start with "TEXL".
        if (bytes.Length >= 4 && bytes[0] == 'T' && bytes[1] == 'E' && bytes[2] == 'X' && bytes[3] == 'L')
        {
            HandleLayerImage(bytes);
            return;
        }

        string json = Encoding.UTF8.GetString(bytes);
        if (logMessages) Debug.Log("[PythonConnection] " + json);

        WsEnvelope envelope;
        try
        {
            envelope = JsonUtility.FromJson<WsEnvelope>(json);
        }
        catch (ArgumentException)
        {
            Debug.LogWarning("[PythonConnection] Ignored non-JSON message: " + json);
            return;
        }
        if (envelope == null || string.IsNullOrEmpty(envelope.type)) return;
        if (envelope.v != SchemaVersion)
            Debug.LogWarning($"[PythonConnection] Message schema v{envelope.v}, expected v{SchemaVersion}: {json}");

        switch (envelope.type)
        {
            case "telemetry":
                LatestTelemetry = JsonUtility.FromJson<TelemetryMessage>(json);
                TelemetryReceived?.Invoke(LatestTelemetry);
                break;
            case "event":
                var ev = JsonUtility.FromJson<EvaEventMessage>(json);
                Debug.Log($"[EVA {ev.severity}] {ev.text}");
                EventReceived?.Invoke(ev);
                break;
            case "alert":
                var alert = JsonUtility.FromJson<AlertMessage>(json);
                if (!alert.update) Debug.Log($"[Alert {alert.category} {alert.level}] {alert.title}: {alert.text}");
                AlertReceived?.Invoke(alert);
                break;
            case "earth_note":
                var note = JsonUtility.FromJson<EarthNoteMessage>(json);
                Debug.Log($"[Earth note] {note.text}");
                EarthNoteReceived?.Invoke(note);
                break;
            case "status":
                LatestStatus = JsonUtility.FromJson<StatusMessage>(json);
                Debug.Log($"[PythonConnection] EVA {LatestStatus.state}");
                StatusReceived?.Invoke(LatestStatus);
                break;
            case "site":
                HandleSite(JsonUtility.FromJson<SiteMessage>(json));
                break;
            case "route":
                RouteReceived?.Invoke(JsonUtility.FromJson<RouteMessage>(json));
                break;
            case "welcome":
                break;
            default:
                if (!logMessages) Debug.Log("[PythonConnection] Unhandled message: " + json);
                break;
        }
    }

    // Arrives right before the heightmap, so the exaggeration applies to that heightmap.
    private void HandleSite(SiteMessage site)
    {
        if (site == null) return;
        LatestSite = site;

        if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
        if (useSiteVerticalExaggeration && terraingen != null && site.vertical_exaggeration > 0f)
            terraingen.verticalExaggeration = site.vertical_exaggeration;

        int layerCount = site.layers != null ? site.layers.Length : 0;
        Debug.Log($"[PythonConnection] Site \"{site.name}\": sol {site.sol} ({site.earth_date}), " +
                  $"lat {site.center_lat:F5}, lon {site.center_lon:F5} E, {site.size_m:F0} m box, " +
                  $"{layerCount} image layer(s), vertical x" +
                  (terraingen != null ? terraingen.verticalExaggeration.ToString("0.##") : "?"));

        GetOverlay().SetSite(site); // before the event, so a subscriber's exception can't skip it
        SiteReceived?.Invoke(site);
    }

    // "TEXL" | int32 LE name length N | N bytes UTF-8 layer name | JPEG/PNG file bytes
    private void HandleLayerImage(byte[] bytes)
    {
        const int header = 8;
        if (bytes.Length < header)
        {
            Debug.LogWarning($"[PythonConnection] Ignored TEXL message: only {bytes.Length} bytes");
            return;
        }
        int nameLength = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24);
        if (nameLength <= 0 || nameLength >= bytes.Length - header) // also needs >= 1 image byte
        {
            Debug.LogWarning($"[PythonConnection] Ignored TEXL message: name length {nameLength} " +
                             $"in a {bytes.Length}-byte message");
            return;
        }

        string layerName = Encoding.UTF8.GetString(bytes, header, nameLength);
        int imageLength = bytes.Length - header - nameLength;
        byte[] image = new byte[imageLength];
        Buffer.BlockCopy(bytes, header + nameLength, image, 0, imageLength);
        GetOverlay().AddLayerImage(layerName, image);
    }

    // Prefer one already in the scene, else put it next to the Terraingen, else on this object.
    private TerrainOverlay GetOverlay()
    {
        if (overlay != null) return overlay;
        overlay = FindAnyObjectByType<TerrainOverlay>();
        if (overlay != null) return overlay;
        if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
        overlay = (terraingen != null ? terraingen.gameObject : gameObject).AddComponent<TerrainOverlay>();
        return overlay;
    }

    /// <summary>Ask the server to plan an EVA route (reply: RouteReceived). False if offline.</summary>
    public bool RequestRoute(RouteRequest request)
    {
        if (!IsConnected) return false;
        SendJson(JsonUtility.ToJson(request));
        return true;
    }

    /// <summary>Send a raw JSON string to the server. Dropped silently if not connected.</summary>
    public async void SendJson(string json)
    {
        if (IsConnected) await websocket.SendText(json);
    }

    private async void OnDestroy()
    {
        if (Instance == this) Instance = null;
        running = false;
        if (websocket == null) return;
        try
        {
            if (websocket.State == WebSocketState.Open) await websocket.Close();
            else websocket.CancelConnection();
        }
        catch (Exception e)
        {
            Debug.Log("[PythonConnection] Close: " + e.Message);
        }
    }
}
