using System;
using UnityEngine;

/// <summary>
/// Settings that differ between a desktop run and a headset build, read once from
/// Assets/Resources/marsmap_config.json (a TextAsset, so it is packed into builds too).
/// A missing file or field just means the default.
/// </summary>
public static class MarsmapConfig
{
    [Serializable]
    class Data
    {
        public string server_url = "ws://127.0.0.1:8765";
        public string mode = "auto";
        public float offline_after_s = 2.5f;
        public float offline_auto_start_s = 0f;
        public float vr_move_speed = 1.5f;
        public float vr_sprint_multiplier = 2f;
        public float snap_turn_degrees = 30f;
        public bool allow_jump_in_vr = false;
    }

    static Data data;

    /// <summary>WebSocket address of Python/server.py. On a headset this must be the laptop's
    /// Wi-Fi address (127.0.0.1 would be the headset itself).</summary>
    public static string ServerUrl => Load().server_url;

    /// <summary>"auto" (server, else the offline bundle), "server" or "offline".</summary>
    public static string Mode => Load().mode;

    /// <summary>Auto mode: switch to the offline bundle if no server connection opened by then.
    /// Must stay below Terraingen's 5 s Perlin fallback.</summary>
    public static float OfflineAfterSeconds => Load().offline_after_s;

    /// <summary>Offline mode: start the EVA replay this many seconds after the terrain is in;
    /// 0 = wait for the Start EVA button (X / Enter).</summary>
    public static float OfflineAutoStartSeconds => Load().offline_auto_start_s;

    public static float VrMoveSpeed => Load().vr_move_speed;
    public static float VrSprintMultiplier => Load().vr_sprint_multiplier;
    public static float SnapTurnDegrees => Load().snap_turn_degrees;
    public static bool AllowJumpInVR => Load().allow_jump_in_vr;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => data = null;  // re-read on every Play, even without a domain reload

    static Data Load()
    {
        if (data != null) return data;
        data = new Data();
        TextAsset file = Resources.Load<TextAsset>("marsmap_config");
        if (file == null) return data;
        try
        {
            JsonUtility.FromJsonOverwrite(file.text, data);  // keeps defaults for missing fields
        }
        catch (ArgumentException e)
        {
            Debug.LogWarning("[MarsmapConfig] marsmap_config.json is not valid JSON, using defaults: " + e.Message);
            data = new Data();
        }
        data.mode = string.IsNullOrEmpty(data.mode) ? "auto" : data.mode.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(data.server_url)) data.server_url = "ws://127.0.0.1:8765";
        return data;
    }
}
