using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Environment alerts and readings on screen.
///
///  - Alerts (top centre): the dust / radiation / UV alerts Python/alerts.py sends, most severe
///    first, coloured by level (advisory yellow, warning orange, danger red and pulsing). A dust
///    warning counts its estimated arrival down on the EVA clock. Each card says whether it comes
///    from real data or a simulated (scripted) event. A short beep plays when an alert appears or
///    gets worse.
///  - Banners under them for a few seconds: EVA events (dust gust, navigation degraded), notes
///    from Earth, alerts that cleared.
///
/// Created automatically (like DustStormEffect); nothing to add to the scene.
/// </summary>
public class EnvironmentHUD : MonoBehaviour
{
    [Header("Layout (screen pixels)")]
    public float alertWidth = 520f;
    public int fontSize = 13;
    [Tooltip("Seconds that event, Earth-note and 'cleared' banners stay up")]
    public float bannerSeconds = 6f;

    [Header("Sound")]
    public bool playSounds = true;
    [Range(0f, 1f)] public float volume = 0.35f;

    static readonly Color Advisory = new Color(0.95f, 0.80f, 0.20f);
    static readonly Color Warning = new Color(1.00f, 0.55f, 0.15f);
    static readonly Color Danger = new Color(1.00f, 0.25f, 0.20f);
    static readonly Color ClearColor = new Color(0.35f, 0.85f, 0.45f);
    static readonly Color EarthColor = new Color(0.45f, 0.70f, 1.00f);
    static readonly Color InfoColor = new Color(0.80f, 0.80f, 0.80f);

    class ActiveAlert { public AlertMessage msg; public float shownAt; public float arrivalEvaS = -1f; }
    class Banner { public string title, text; public Color color; public float until; }

    readonly Dictionary<string, ActiveAlert> active = new Dictionary<string, ActiveAlert>();
    readonly List<Banner> banners = new List<Banner>();
    readonly List<ActiveAlert> sorted = new List<ActiveAlert>();
    TelemetryMessage telemetry;
    bool subscribed;

    MapView mapView; float nextSearch;
    AudioSource audioSource; AudioClip beepAdvisory, beepWarning, beepDanger;
    Texture2D white;
    GUIStyle titleStyle, textStyle, tagStyle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExists()
    {
        if (FindAnyObjectByType<EnvironmentHUD>() != null) return;
        new GameObject(nameof(EnvironmentHUD)).AddComponent<EnvironmentHUD>();
    }

    void OnDisable() => Unsubscribe();

    void OnDestroy()
    {
        if (white != null) Destroy(white);
        if (beepAdvisory != null) Destroy(beepAdvisory);
        if (beepWarning != null) Destroy(beepWarning);
        if (beepDanger != null) Destroy(beepDanger);
    }

    void Update()
    {
        if (!subscribed) TrySubscribe();
        if (Time.unscaledTime >= nextSearch && mapView == null)
        {
            nextSearch = Time.unscaledTime + 1f; // it appears once the terrain has loaded
            mapView = FindAnyObjectByType<MapView>();
        }
    }

    // ---------------------------------------------------------------
    // Messages from PythonConnection
    // ---------------------------------------------------------------
    void TrySubscribe()
    {
        var pc = PythonConnection.Instance;
        if (pc == null) return;
        pc.TelemetryReceived += OnTelemetry;
        pc.AlertReceived += OnAlert;
        pc.EventReceived += OnEvent;
        pc.EarthNoteReceived += OnEarthNote;
        pc.StatusReceived += OnStatus;
        subscribed = true;
    }

    void Unsubscribe()
    {
        if (!subscribed) return;
        var pc = PythonConnection.Instance;
        if (pc != null)
        {
            pc.TelemetryReceived -= OnTelemetry;
            pc.AlertReceived -= OnAlert;
            pc.EventReceived -= OnEvent;
            pc.EarthNoteReceived -= OnEarthNote;
            pc.StatusReceived -= OnStatus;
        }
        subscribed = false;
    }

    void OnTelemetry(TelemetryMessage t) => telemetry = t;

    void OnAlert(AlertMessage a)
    {
        if (a == null || string.IsNullOrEmpty(a.category)) return;
        if (a.level == "clear")
        {
            if (active.Remove(a.category)) AddBanner(a.title, a.text, ClearColor);
            return;
        }
        bool isNew = !active.TryGetValue(a.category, out ActiveAlert cur);
        bool worse = isNew || Rank(a.level) > Rank(cur.msg.level);
        active[a.category] = new ActiveAlert
        {
            msg = a,
            shownAt = worse ? Time.unscaledTime : cur.shownAt,
            arrivalEvaS = a.eta_min > 0f ? a.t_eva_s + a.eta_min * 60f : -1f,
        };
        if (worse) Beep(a.level);
    }

    void OnEvent(EvaEventMessage ev)
    {
        if (ev == null) return;
        string title = string.IsNullOrEmpty(ev.id) ? "EVENT" : ev.id.Replace('_', ' ').ToUpperInvariant();
        AddBanner(title, ev.text, ev.severity == "warning" ? Warning : Advisory);
        Beep("advisory");
    }

    void OnEarthNote(EarthNoteMessage note)
    {
        if (note == null) return;
        string delay = note.delay_s > 0f ? $"  ·  sent {note.delay_s / 60f:0} min ago (light delay)" : "";
        AddBanner("NOTE FROM EARTH" + delay, note.text, EarthColor, bannerSeconds + 2f);
    }

    void OnStatus(StatusMessage status)
    {
        if (status == null) return;
        if (status.state == "started")
        {
            active.Clear();      // a new replay starts clean
            banners.Clear();
        }
        else if (status.state == "finished")
        {
            active.Clear();      // the EVA is over: no alerts left on screen
            banners.Clear();
            AddBanner("EVA COMPLETE", $"Radiation dose this EVA: {status.dose_total_mGy:0.000} mGy.", InfoColor);
        }
    }

    void AddBanner(string title, string text, Color color, float seconds = -1f)
    {
        banners.Add(new Banner
        {
            title = title, text = text, color = color,
            until = Time.unscaledTime + (seconds > 0f ? seconds : bannerSeconds),
        });
        if (banners.Count > 4) banners.RemoveAt(0);
    }

    // ---------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------
    void OnGUI()
    {
        if (active.Count == 0 && banners.Count == 0) return;
        EnsureStyles();
        GUI.depth = -10; // above the dust tint
        banners.RemoveAll(b => Time.unscaledTime > b.until);

        bool mapOpen = mapView != null && mapView.IsOpen;
        float w = Mathf.Min(alertWidth, Screen.width - 40f);
        float x = (Screen.width - w) / 2f;
        float y = mapOpen ? 90f : 10f; // the open map puts its help bar at the top

        sorted.Clear();
        sorted.AddRange(active.Values);
        sorted.Sort((a, b) => Rank(b.msg.level).CompareTo(Rank(a.msg.level)));
        foreach (ActiveAlert a in sorted) y = DrawAlert(a, x, y, w) + 6f;
        foreach (Banner b in banners) y = DrawCard(x, y, w, b.color, b.title, b.text, null, 1f, 0f) + 6f;
    }

    float DrawAlert(ActiveAlert a, float x, float y, float w)
    {
        string body = a.msg.text;
        if (a.arrivalEvaS > 0f && telemetry != null)
        {
            float left = (a.arrivalEvaS - telemetry.t_eva_s) / 60f;
            body += left >= 1f ? $"\n<b>Estimated arrival in ~{Mathf.RoundToInt(left)} min</b> (EVA clock)"
                               : "\n<b>Arriving now</b>";
        }
        bool danger = a.msg.level == "danger";
        float pulse = danger ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f) : 1f;
        float fresh = Mathf.Clamp01(1f - (Time.unscaledTime - a.shownAt) / 2f); // flash when new
        float glow = Mathf.Max(fresh, danger ? 0.35f * pulse : 0f);
        return DrawCard(x, y, w, LevelColor(a.msg.level), a.msg.title, body, SourceTag(a.msg.category),
                        danger ? Mathf.Lerp(0.35f, 1f, pulse) : 1f, glow);
    }

    float DrawCard(float x, float y, float w, Color c, string title, string body, string tag,
                   float stripeAlpha, float glow)
    {
        float inner = w - 26f;
        var titleContent = new GUIContent($"<b>{title}</b>");
        float th = titleStyle.CalcHeight(titleContent, inner);
        float bh = string.IsNullOrEmpty(body) ? 0f : textStyle.CalcHeight(new GUIContent(body), inner);
        var r = new Rect(x, y, w, th + bh + 12f);

        DrawRect(r, new Color(0.05f, 0.03f, 0.02f, 0.86f));
        if (glow > 0f) DrawRect(r, new Color(c.r, c.g, c.b, 0.28f * glow));
        DrawRect(new Rect(x, y, 6f, r.height), new Color(c.r, c.g, c.b, stripeAlpha));

        titleStyle.normal.textColor = c;
        GUI.Label(new Rect(x + 16f, y + 5f, inner, th), titleContent, titleStyle);
        if (!string.IsNullOrEmpty(tag))
            GUI.Label(new Rect(x + 16f, y + 6f, inner, th), tag, tagStyle);
        if (bh > 0f) GUI.Label(new Rect(x + 16f, y + 5f + th, inner, bh), body, textStyle);
        return r.yMax;
    }

    // Real data or a scripted event? Shown on each alert card so nobody mistakes one for the other.
    string SourceTag(string category)
    {
        string s = telemetry != null ? telemetry.scenario ?? "" : "";
        switch (category)
        {
            case "dust": return "SIMULATED DUST · REAL UV";
            case "radiation": return s.Contains("solar_particle_event") ? "SIMULATED SOLAR EVENT · REAL RAD BASELINE" : "REAL RAD DATA";
            case "uv": return s.Contains("dust_front") ? "REAL REMS UV · DIMMED BY SIM DUST" : "REAL REMS UV DATA";
            default: return null;
        }
    }

    void EnsureStyles()
    {
        if (titleStyle != null) return;
        white = new Texture2D(1, 1);
        white.SetPixel(0, 0, Color.white);
        white.Apply();
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize + 2, richText = true, wordWrap = true };
        textStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize, richText = true, wordWrap = true };
        textStyle.normal.textColor = Color.white;
        tagStyle = new GUIStyle(GUI.skin.label) { fontSize = fontSize - 3, alignment = TextAnchor.UpperRight };
        tagStyle.normal.textColor = new Color(0.75f, 0.7f, 0.65f, 0.85f);
    }

    void DrawRect(Rect r, Color c)
    {
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, white);
        GUI.color = prev;
    }

    static int Rank(string level) =>
        level == "danger" ? 3 : level == "warning" ? 2 : level == "advisory" ? 1 : 0;

    static Color LevelColor(string level) =>
        level == "danger" ? Danger : level == "warning" ? Warning : level == "advisory" ? Advisory : ClearColor;

    // ---------------------------------------------------------------
    // Sound: short synthesized beeps (no audio files needed)
    // ---------------------------------------------------------------
    void Beep(string level)
    {
        if (!playSounds || volume <= 0f) return;
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            beepAdvisory = Tone("Alert advisory", 660f, 1, 0.18f, 0.08f);
            beepWarning = Tone("Alert warning", 880f, 2, 0.14f, 0.07f);
            beepDanger = Tone("Alert danger", 1040f, 3, 0.11f, 0.05f);
        }
        AudioClip clip = level == "danger" ? beepDanger : level == "warning" ? beepWarning : beepAdvisory;
        audioSource.PlayOneShot(clip, volume);
    }

    static AudioClip Tone(string name, float freq, int beeps, float beepSeconds, float gapSeconds)
    {
        const int rate = 44100;
        float period = beepSeconds + gapSeconds;
        int n = Mathf.CeilToInt(rate * period * beeps);
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float local = t % period;
            if (local >= beepSeconds) continue;
            float envelope = Mathf.Min(1f, local / 0.01f) * Mathf.Min(1f, (beepSeconds - local) / 0.02f);
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * envelope * 0.6f;
        }
        AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
