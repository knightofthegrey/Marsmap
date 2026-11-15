using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Replaces the Earth-blue tutorial sky with a Mars daytime sky: butterscotch near the
/// horizon, deeper tan-brown overhead (an artistic approximation of how the sky looks in
/// Curiosity's images, not measured data). Also switches the ambient light to matching
/// warm colours, so the blue sky no longer tints the terrain, and warms the sun slightly.
///
/// Created automatically if it isn't in the scene. Scene settings are restored when Play stops.
/// </summary>
public class MarsSky : MonoBehaviour
{
    [Header("Sky gradient")]
    public Color zenith = new Color(0.58f, 0.42f, 0.30f);    // overhead: deeper tan-brown
    public Color horizon = new Color(0.86f, 0.69f, 0.52f);   // near the horizon: bright dusty butterscotch
    public Color ground = new Color(0.33f, 0.23f, 0.16f);    // below the horizon (rarely seen)
    [Range(0.2f, 3f)] public float exposure = 1.1f;

    [Header("Light")]
    public Color sunColor = new Color(1.0f, 0.92f, 0.80f);
    [Range(0f, 2f)] public float ambientStrength = 1.0f;
    [Range(0f, 1f)] [Tooltip("Lower = less sky colour reflected by shiny surfaces")]
    public float reflectionIntensity = 0.4f;

    Material skyMaterial;
    Texture2D skyTexture;

    // Saved scene settings, restored on destroy
    Material savedSkybox; AmbientMode savedAmbientMode;
    Color savedAmbSky, savedAmbEquator, savedAmbGround; float savedAmbIntensity, savedReflIntensity;
    Light sun; Color savedSunColor;
    Camera fallbackCamera; CameraClearFlags savedClear; Color savedBackground;
    bool applied;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExists()
    {
        if (FindAnyObjectByType<MarsSky>() != null) return;
        new GameObject(nameof(MarsSky)).AddComponent<MarsSky>();
    }

    void Start() => Apply();

    [ContextMenu("Apply Mars sky")]
    public void Apply()
    {
        if (!applied)
        {
            savedSkybox = RenderSettings.skybox;
            savedAmbientMode = RenderSettings.ambientMode;
            savedAmbSky = RenderSettings.ambientSkyColor;
            savedAmbEquator = RenderSettings.ambientEquatorColor;
            savedAmbGround = RenderSettings.ambientGroundColor;
            savedAmbIntensity = RenderSettings.ambientIntensity;
            savedReflIntensity = RenderSettings.reflectionIntensity;
            sun = FindSun();
            if (sun != null) savedSunColor = sun.color;
        }

        // Sky: a latitude-longitude gradient on the built-in panoramic skybox shader.
        Shader pano = Shader.Find("Skybox/Panoramic");
        if (pano != null)
        {
            BuildSkyTexture();
            if (skyMaterial == null) skyMaterial = new Material(pano) { name = "Mars Sky (runtime)" };
            skyMaterial.SetTexture("_MainTex", skyTexture);
            skyMaterial.SetFloat("_Mapping", 1f);       // latitude-longitude layout
            skyMaterial.SetFloat("_ImageType", 0f);     // full 360 degrees
            skyMaterial.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
            skyMaterial.DisableKeyword("_MAPPING_6_FRAMES_LAYOUT");
            skyMaterial.SetFloat("_Exposure", exposure);
            skyMaterial.SetColor("_Tint", new Color(0.5f, 0.5f, 0.5f)); // neutral (0.5 = no tint)
            RenderSettings.skybox = skyMaterial;
        }
        else
        {
            // Shader not available (e.g. stripped from a build): plain Mars-sky background.
            fallbackCamera = Camera.main;
            if (fallbackCamera != null)
            {
                if (!applied) { savedClear = fallbackCamera.clearFlags; savedBackground = fallbackCamera.backgroundColor; }
                fallbackCamera.clearFlags = CameraClearFlags.SolidColor;
                fallbackCamera.backgroundColor = Color.Lerp(horizon, zenith, 0.4f);
            }
            Debug.LogWarning("[MarsSky] Skybox/Panoramic shader not found; using a solid sky colour.");
        }

        // Ambient light in Mars colours (otherwise the old blue sky keeps lighting the ground).
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = zenith * ambientStrength;
        RenderSettings.ambientEquatorColor = horizon * 0.8f * ambientStrength;
        RenderSettings.ambientGroundColor = ground * 0.6f * ambientStrength;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.reflectionIntensity = reflectionIntensity;

        if (sun != null) sun.color = sunColor;

        DynamicGI.UpdateEnvironment();
        applied = true;
        Debug.Log("[MarsSky] Mars sky and lighting applied");
    }

    void BuildSkyTexture()
    {
        const int h = 256;
        if (skyTexture == null)
            skyTexture = new Texture2D(4, h, TextureFormat.RGBA32, false)
            {
                name = "Mars Sky Gradient",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

        var px = new Color[4 * h];
        for (int y = 0; y < h; y++)
        {
            // v = 0 straight down, 0.5 horizon, 1 straight up
            float v = (y + 0.5f) / h;
            float elev = (v - 0.5f) * 2f; // -1 .. 1
            Color c;
            if (elev >= 0f)
            {
                // bright dusty band near the horizon, deeper colour overhead
                float t = Mathf.Pow(elev, 0.55f);
                c = Color.Lerp(horizon, zenith, t);
            }
            else
            {
                float t = Mathf.Clamp01(-elev * 6f);
                c = Color.Lerp(horizon * 0.9f, ground, t);
            }
            c.a = 1f;
            for (int x = 0; x < 4; x++) px[y * 4 + x] = c;
        }
        skyTexture.SetPixels(px);
        skyTexture.Apply(false, false);
    }

    static Light FindSun()
    {
        if (RenderSettings.sun != null) return RenderSettings.sun;
        foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
            if (l.type == LightType.Directional) return l;
        return null;
    }

    void OnDestroy()
    {
        if (applied)
        {
            RenderSettings.skybox = savedSkybox;
            RenderSettings.ambientMode = savedAmbientMode;
            RenderSettings.ambientSkyColor = savedAmbSky;
            RenderSettings.ambientEquatorColor = savedAmbEquator;
            RenderSettings.ambientGroundColor = savedAmbGround;
            RenderSettings.ambientIntensity = savedAmbIntensity;
            RenderSettings.reflectionIntensity = savedReflIntensity;
            if (sun != null) sun.color = savedSunColor;
            if (fallbackCamera != null) { fallbackCamera.clearFlags = savedClear; fallbackCamera.backgroundColor = savedBackground; }
        }
        if (skyMaterial != null) Destroy(skyMaterial);
        if (skyTexture != null) Destroy(skyTexture);
    }
}
