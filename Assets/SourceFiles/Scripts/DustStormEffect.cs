using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Dust gust: when feeder.py sends the "dust_gust" event, orange fog rolls in (nearby ground
/// stays visible, distant ground fades out past ~50-150 m), the sun dims, the screen takes a
/// dusty tint and dust streaks blow past the camera. After the gust it all clears again.
///
/// Created automatically if it isn't in the scene (like PythonConnection).
/// Press G in Play mode to trigger a test gust without the feeder.
/// </summary>
public class DustStormEffect : MonoBehaviour
{
    [Header("Timing (real seconds)")]
    public float fadeInSeconds = 3f;
    [Tooltip("Used when the event has no duration_s")]
    public float holdSeconds = 8f;
    public float fadeOutSeconds = 4f;

    [Header("Look")]
    public Color dustColor = new Color(0.66f, 0.45f, 0.28f);
    [Tooltip("Exponential-squared fog density at the peak. 0.03: ground ~10% visible at 50 m, hidden past ~80 m. Higher = thicker.")]
    public float peakFogDensity = 0.03f;
    [Range(0f, 1f)] public float peakScreenTint = 0.42f;
    [Range(0f, 1f)] [Tooltip("Sun brightness at the peak, as a fraction of normal")]
    public float sunAtPeak = 0.3f;
    public float maxDustPerSecond = 2200f;
    public Vector3 windDirection = new Vector3(1f, 0f, 0.3f);
    public float windSpeed = 22f;

    [Header("Testing")]
    public bool testKeyG = true;

    public bool IsActive => gustStart >= 0f;
    public float Intensity { get; private set; }   // 0..1, e.g. for a HUD

    float gustStart = -1f, gustHold;
    bool subscribed;

    // Scene settings saved before the gust and restored afterwards
    bool savedFog; FogMode savedFogMode; Color savedFogColor; float savedFogDensity;
    Light sun; float savedSunIntensity; Color savedSunColor;
    bool settingsSaved;

    ParticleSystem dust;
    Texture2D tintTexture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExists()
    {
        if (FindAnyObjectByType<DustStormEffect>() != null) return;
        new GameObject(nameof(DustStormEffect)).AddComponent<DustStormEffect>();
    }

    void OnDisable()
    {
        Unsubscribe();
        SetIntensity(0f);
        RestoreSettings();
    }

    void OnDestroy()
    {
        if (tintTexture != null) Destroy(tintTexture);
    }

    /// <summary>Start a gust now. totalSeconds &lt;= 0 uses fade in + holdSeconds + fade out.</summary>
    public void TriggerGust(float totalSeconds = 0f)
    {
        gustHold = totalSeconds > 0f
            ? Mathf.Max(0f, totalSeconds - fadeInSeconds - fadeOutSeconds)
            : holdSeconds;
        // If a gust is already blowing, keep the current strength instead of jumping back to 0.
        float alreadyIn = IsActive ? Intensity * fadeInSeconds : 0f;
        gustStart = Time.time - alreadyIn;
        Debug.Log($"[Dust] Gust for {fadeInSeconds + gustHold + fadeOutSeconds:0.#} s");
    }

    /// <summary>Clear the dust now (fades out from the current strength).</summary>
    public void StopGust()
    {
        if (!IsActive) return;
        // Jump to the matching point of the fade-out so there's no pop.
        gustStart = Time.time - fadeInSeconds - gustHold - (1f - Intensity) * fadeOutSeconds;
    }

    void Update()
    {
        if (!subscribed) TrySubscribe();

        if (testKeyG && Keyboard.current != null && Keyboard.current.gKey.wasPressedThisFrame)
            TriggerGust();

        if (!IsActive) return;

        float t = Time.time - gustStart;
        float k;
        if (t < fadeInSeconds) k = t / Mathf.Max(fadeInSeconds, 0.01f);
        else if (t < fadeInSeconds + gustHold) k = 1f;
        else k = 1f - (t - fadeInSeconds - gustHold) / Mathf.Max(fadeOutSeconds, 0.01f);
        k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(k));

        SetIntensity(k);
        if (t >= fadeInSeconds + gustHold + fadeOutSeconds)
        {
            gustStart = -1f;
            SetIntensity(0f);
            RestoreSettings();
        }
    }

    // ---------------------------------------------------------------
    // Events from PythonConnection (feeder.py)
    // ---------------------------------------------------------------
    void TrySubscribe()
    {
        var pc = PythonConnection.Instance;
        if (pc == null) return;
        pc.EventReceived += OnEvaEvent;
        pc.StatusReceived += OnStatus;
        subscribed = true;
    }

    void Unsubscribe()
    {
        if (!subscribed) return;
        var pc = PythonConnection.Instance;
        if (pc != null)
        {
            pc.EventReceived -= OnEvaEvent;
            pc.StatusReceived -= OnStatus;
        }
        subscribed = false;
    }

    void OnEvaEvent(EvaEventMessage ev)
    {
        if (ev == null) return;
        if (ev.id == "dust_gust") TriggerGust(ev.duration_s);
        else if (ev.id == "dust_clear") StopGust();
    }

    void OnStatus(StatusMessage status)
    {
        // A new replay starting clears any dust left from the previous run.
        if (status != null && status.state == "started" && IsActive)
        {
            gustStart = -1f;
            SetIntensity(0f);
            RestoreSettings();
        }
    }

    // ---------------------------------------------------------------
    // Applying the look
    // ---------------------------------------------------------------
    void SetIntensity(float k)
    {
        Intensity = k;
        if (k > 0f) SaveSettings();
        if (!settingsSaved) return;

        // Fog (works with URP Lit and Terrain Lit; the screen tint below covers the sky)
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        float baseDensity = savedFog ? savedFogDensity : 0f;
        RenderSettings.fogDensity = Mathf.Lerp(baseDensity, peakFogDensity, k);
        RenderSettings.fogColor = savedFog ? Color.Lerp(savedFogColor, dustColor, k) : dustColor;

        // Sun dims and turns orange
        if (sun != null)
        {
            sun.intensity = savedSunIntensity * Mathf.Lerp(1f, sunAtPeak, k);
            sun.color = Color.Lerp(savedSunColor, Color.Lerp(savedSunColor, dustColor, 0.6f), k);
        }

        // Dust streaks around the camera (never created while shutting down, only while dust blows)
        if (k > 0f) EnsureDust();
        if (dust != null)
        {
            var emission = dust.emission;
            emission.rateOverTime = maxDustPerSecond * k;
            if (k > 0f && !dust.isPlaying) dust.Play();
        }
    }

    void SaveSettings()
    {
        if (settingsSaved) return;
        savedFog = RenderSettings.fog;
        savedFogMode = RenderSettings.fogMode;
        savedFogColor = RenderSettings.fogColor;
        savedFogDensity = RenderSettings.fogDensity;

        sun = RenderSettings.sun;
        if (sun == null)
            foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
                if (l.type == LightType.Directional) { sun = l; break; }
        if (sun != null) { savedSunIntensity = sun.intensity; savedSunColor = sun.color; }
        settingsSaved = true;
    }

    void RestoreSettings()
    {
        if (!settingsSaved) return;
        RenderSettings.fog = savedFog;
        RenderSettings.fogMode = savedFogMode;
        RenderSettings.fogColor = savedFogColor;
        RenderSettings.fogDensity = savedFogDensity;
        if (sun != null) { sun.intensity = savedSunIntensity; sun.color = savedSunColor; }
        if (dust != null)
        {
            var emission = dust.emission;
            emission.rateOverTime = 0f;
        }
        settingsSaved = false;
    }

    void EnsureDust()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        if (dust != null)
        {
            if (dust.transform.parent != cam.transform) dust.transform.SetParent(cam.transform, false);
            return;
        }

        var go = new GameObject("Dust Particles");
        go.transform.SetParent(cam.transform, false);
        go.transform.localPosition = new Vector3(0f, 0f, 8f);
        dust = go.AddComponent<ParticleSystem>();
        dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = dust.main;
        main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
        main.startSpeed = 0f;                               // motion comes from velocityOverLifetime
        main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.18f);
        main.startColor = new Color(dustColor.r, dustColor.g, dustColor.b, 0.7f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 8000;

        var shape = dust.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(30f, 12f, 24f);          // a volume around and ahead of the camera

        var vel = dust.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        Vector3 w = windDirection.sqrMagnitude > 0f ? windDirection.normalized * windSpeed : Vector3.right * windSpeed;
        vel.x = new ParticleSystem.MinMaxCurve(w.x * 0.8f, w.x * 1.2f);
        vel.y = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);
        vel.z = new ParticleSystem.MinMaxCurve(w.z * 0.8f, w.z * 1.2f);

        var emission = dust.emission;
        emission.rateOverTime = 0f;

        var rend = dust.GetComponent<ParticleSystemRenderer>();
        rend.renderMode = ParticleSystemRenderMode.Stretch;  // streaks along the wind
        rend.velocityScale = 0.06f;
        rend.lengthScale = 1f;
        // Sprites/Default is transparent with vertex colours and renders in URP; URP's particle
        // shader is opaque unless reconfigured, so it's only the fallback.
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader != null)
        {
            var mat = new Material(shader) { color = Color.white };
            rend.material = mat;
        }
    }

    // Full-screen dusty tint: also covers the sky, which fog doesn't reach.
    void OnGUI()
    {
        if (Intensity <= 0f || peakScreenTint <= 0f || GreenScreen.On) return;
        if (tintTexture == null)
        {
            tintTexture = new Texture2D(1, 1);
            tintTexture.SetPixel(0, 0, Color.white);
            tintTexture.Apply();
        }
        Color prev = GUI.color;
        GUI.color = new Color(dustColor.r, dustColor.g, dustColor.b, peakScreenTint * Intensity);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tintTexture);
        GUI.color = prev;
    }
}
