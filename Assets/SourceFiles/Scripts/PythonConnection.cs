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
}

[Serializable]
public class EvaEventMessage
{
    public float t_eva_s;
    public string id;        // e.g. "dust_gust", "nav_degraded"
    public string severity;  // "caution" | "warning"
    public string text;
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

/// <summary>
/// WebSocket link to Python/server.py. Keeps reconnecting until the server is up.
///  - Binary "HMAP" messages (JMARS heightmap) go to Terraingen.ApplyHeightmap.
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

    public event Action<bool> ConnectionChanged;
    public event Action<TelemetryMessage> TelemetryReceived;
    public event Action<EvaEventMessage> EventReceived;
    public event Action<EarthNoteMessage> EarthNoteReceived;
    public event Action<StatusMessage> StatusReceived;

    public bool IsConnected => websocket != null && websocket.State == WebSocketState.Open;
    public TelemetryMessage LatestTelemetry { get; private set; }
    public StatusMessage LatestStatus { get; private set; }

    private WebSocket websocket;
    private bool running;
    private bool warnedUnreachable;
    private bool wasOpen;

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

    private async void Start()
    {
        if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
        Debug.Log($"[PythonConnection] Connecting to {url}");

        running = true;
        while (running)
        {
            websocket = new WebSocket(url);
            websocket.OnOpen += HandleOpen;
            websocket.OnMessage += HandleMessage;
            websocket.OnError += HandleError;
            websocket.OnClose += HandleClose;

            // Returns only when the connection fails or closes.
            await websocket.Connect();

            if (!running) break;
            await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds));
        }
    }

    private void HandleOpen()
    {
        warnedUnreachable = false;
        wasOpen = true;
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
        if (warnedUnreachable) return;
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
            case "welcome":
                break;
            default:
                if (!logMessages) Debug.Log("[PythonConnection] Unhandled message: " + json);
                break;
        }
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
