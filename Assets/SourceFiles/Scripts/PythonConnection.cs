using System;
using System.Text;
using UnityEngine;
using NativeWebSocket;

public class PythonConnection : MonoBehaviour
{
    [SerializeField] private string url = "ws://localhost:8765";

    private WebSocket websocket;

    async void Start()
    {
        websocket = new WebSocket(url);

        websocket.OnOpen += () =>
        {
            Debug.Log("Connected to Python");
            SendJson("{\"type\":\"hello\",\"from\":\"unity\"}");
        };

        websocket.OnError += (e) => Debug.LogError("WebSocket error: " + e);

        websocket.OnClose += (code) => Debug.Log("Connection closed: " + code);

        websocket.OnMessage += (bytes) =>
        {
            string message = Encoding.UTF8.GetString(bytes);
            Debug.Log("From Python: " + message);
            // TODO: parse with JsonUtility.FromJson<YourClass>(message)
        };

        // Awaits until the connection closes
        await websocket.Connect();
    }

    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        // Required: dispatches received messages on the main thread
        websocket?.DispatchMessageQueue();
#endif
    }

    public async void SendJson(string json)
    {
        if (websocket != null && websocket.State == WebSocketState.Open)
        {
            await websocket.SendText(json);
        }
    }

    private async void OnApplicationQuit()
    {
        if (websocket != null) await websocket.Close();
    }
}