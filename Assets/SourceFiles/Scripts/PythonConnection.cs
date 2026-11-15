using System.Text;
using UnityEngine;
using NativeWebSocket;

// Connects to Python/server.py and hands heightmap messages to Terraingen.
// Terraingen adds this component automatically if it isn't in the scene.
public class PythonConnection : MonoBehaviour
{
    [SerializeField] private string url = "ws://127.0.0.1:8765";
    [SerializeField] private Terraingen terraingen; // optional: found automatically

    private WebSocket websocket;

    async void Start()
    {
        if (terraingen == null) terraingen = FindFirstObjectByType<Terraingen>();

        Debug.Log($"PythonConnection: connecting to {url}");
        websocket = new WebSocket(url);

        websocket.OnOpen += () =>
        {
            Debug.Log("Connected to Python");
            SendJson("{\"type\":\"request_heightmap\"}");
        };

        websocket.OnError += (e) => Debug.LogWarning("WebSocket error (is server.py running?): " + e);
        websocket.OnClose += (code) => Debug.Log("Connection closed: " + code);

        websocket.OnMessage += (bytes) =>
        {
            // Heightmap messages start with the magic bytes "HMAP"
            if (bytes.Length > 24 && bytes[0] == 'H' && bytes[1] == 'M'
                && bytes[2] == 'A' && bytes[3] == 'P')
            {
                if (terraingen != null) terraingen.ApplyHeightmap(bytes);
                else Debug.LogError("PythonConnection: got a heightmap but there is no Terraingen in the scene");
                return;
            }
            Debug.Log("From Python: " + Encoding.UTF8.GetString(bytes));
        };

        await websocket.Connect();
    }

    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        websocket?.DispatchMessageQueue();
#endif
    }

    public async void SendJson(string json)
    {
        if (websocket != null && websocket.State == WebSocketState.Open)
            await websocket.SendText(json);
    }

    private async void OnApplicationQuit()
    {
        if (websocket != null) await websocket.Close();
    }
}
