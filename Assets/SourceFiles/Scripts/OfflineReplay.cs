using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Offline mode: plays the bundle that Python/export_offline.py wrote to
/// Assets/StreamingAssets/Marsmap/ (packed into the app), so a headset needs no laptop or Wi-Fi.
///
/// Every message goes through PythonConnection.InjectMessage, the same path as a message from
/// server.py / feeder.py, so terrain, map layers, telemetry, events and the dust gust behave
/// exactly as in a live run.
///  1. The terrain reply (site info, heightmap, map images) is applied as soon as it loads.
///  2. The EVA replay starts on X (left controller) / Enter, or automatically after
///     offline_auto_start_s in marsmap_config.json. Pressing it again restarts the replay.
/// PythonConnection adds this component; it isn't meant to be put in the scene.
/// </summary>
public class OfflineReplay : MonoBehaviour
{
#pragma warning disable 0649  // filled in by JsonUtility
    [Serializable] class Manifest { public int version; public string[] terrain; public string replay; }
    [Serializable] class ReplayFile { public int version; public float speed; public float tick_s; public float eva_min; public string source; public Item[] messages; }
    [Serializable] class Item { public float t_eva_min; public string json; }
#pragma warning restore 0649

    PythonConnection connection;
    Terraingen terraingen;
    ReplayFile replay;
    bool loaded, playing;
    float playStart, autoStartAt = -1f;
    int next;

    public bool IsPlaying => playing;

    public void Begin(PythonConnection pc, Terraingen tg, string reason)
    {
        connection = pc;
        terraingen = tg;
        StartCoroutine(Load(reason));
    }

    // Application.streamingAssetsPath is a plain folder in the editor / on desktop, and a
    // jar:file://...apk!/assets URL on Android; UnityWebRequest reads both as URLs.
    static string Url(string file)
    {
        string path = Application.streamingAssetsPath + "/Marsmap/" + file;
        return path.Contains("://") ? path : new Uri(path).AbsoluteUri;
    }

    static IEnumerator Fetch(string file, Action<UnityWebRequest> done)
    {
        using (UnityWebRequest req = UnityWebRequest.Get(Url(file)))
        {
            yield return req.SendWebRequest();
            done(req);
        }
    }

    IEnumerator Load(string reason)
    {
        Manifest manifest = null;
        string error = null;
        yield return Fetch("manifest.json", r =>
        {
            if (r.result != UnityWebRequest.Result.Success) { error = r.error; return; }
            try { manifest = JsonUtility.FromJson<Manifest>(r.downloadHandler.text); }
            catch (ArgumentException e) { error = "manifest.json: " + e.Message; }
        });
        if (manifest == null || manifest.terrain == null)
        {
            Debug.LogWarning($"[Offline] No offline bundle in StreamingAssets/Marsmap ({error ?? "bad manifest"}); " +
                             "run Python/export_offline.py. Using the server instead.", this);
            connection.OfflineUnavailable();
            yield break;
        }

        // Load every terrain message first, then apply them in order (never half a terrain).
        var terrain = new byte[manifest.terrain.Length][];
        for (int i = 0; i < manifest.terrain.Length && error == null; i++)
        {
            int k = i;
            yield return Fetch(manifest.terrain[i], r =>
            {
                if (r.result == UnityWebRequest.Result.Success) terrain[k] = r.downloadHandler.data;
                else error = $"{manifest.terrain[k]}: {r.error}";
            });
        }
        yield return Fetch(string.IsNullOrEmpty(manifest.replay) ? "replay.json" : manifest.replay, r =>
        {
            if (r.result != UnityWebRequest.Result.Success) { error = error ?? $"replay: {r.error}"; return; }
            try { replay = JsonUtility.FromJson<ReplayFile>(r.downloadHandler.text); }
            catch (ArgumentException e) { error = error ?? "replay.json: " + e.Message; }
        });
        if (error != null)
        {
            Debug.LogWarning($"[Offline] Offline bundle incomplete ({error}); using the server instead.", this);
            connection.OfflineUnavailable();
            yield break;
        }

        // Respect Terraingen's "Connect To Python" switch. Otherwise apply the terrain even if the
        // Perlin fallback already ran (a slow load on a headset): in offline mode nothing else can
        // have delivered real terrain.
        bool applyTerrain = terraingen == null || terraingen.connectToPython;
        if (applyTerrain)
            foreach (byte[] msg in terrain) connection.InjectMessage(msg);

        loaded = true;
        float seconds = replay != null && replay.speed > 0 ? replay.eva_min * 60f / replay.speed : 0f;
        Debug.Log($"[Offline] Using the offline bundle ({reason}): " +
                  (applyTerrain ? $"terrain applied ({terrain.Length} message(s)), " : "terrain skipped (Connect To Python is off), ") +
                  $"EVA replay {seconds:0} s ready. Press X (left controller) or Enter to start it.", this);
        if (MarsmapConfig.OfflineAutoStartSeconds > 0f) autoStartAt = Time.time + MarsmapConfig.OfflineAutoStartSeconds;
    }

    void Update()
    {
        if (!loaded || replay == null || replay.messages == null) return;

        if (MarsmapInput.StartEva.WasPressedThisFrame() || (autoStartAt > 0f && Time.time >= autoStartAt))
        {
            autoStartAt = -1f;
            playing = true;
            playStart = Time.time;
            next = 0;
            Debug.Log($"[Offline] EVA replay started ({replay.eva_min * 60f / Mathf.Max(replay.speed, 0.001f):0} s)", this);
        }
        if (!playing) return;

        // EVA minutes elapsed; send every message that is due (Update-driven, no per-message timers)
        float evaMinutes = (Time.time - playStart) * replay.speed / 60f;
        while (next < replay.messages.Length && replay.messages[next].t_eva_min <= evaMinutes)
        {
            connection.InjectMessage(Encoding.UTF8.GetBytes(replay.messages[next].json));
            next++;
        }
        if (next >= replay.messages.Length)
        {
            playing = false;
            Debug.Log("[Offline] EVA replay finished. Press X / Enter to play it again.", this);
        }
    }
}
