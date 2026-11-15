using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Bottom-left HUD:
///  - a round minimap that turns with you (the way you face is up): shaded terrain so hills and
///    craters stand out, contour lines, coloured by the EVA hazard zones (green 0-10 deg,
///    yellow 10-20, orange 20-30, red > 30, as in Mars_mapping(3).ipynb; H: plain terrain
///    colours instead), the route still ahead in
///    hazard colours, the remaining waypoints, Home Base and you. + / - zoom.
///  - "upcoming hills": the elevation profile of the next stretch of your route (or, without a
///    route, of the ground straight ahead), with the climb, descent and steepest grade.
/// Reads the terrain from Terraingen and the route from MapView. Adds itself at startup.
/// In a headset it sits bottom-left on the VR HUD sheet, as on desktop (MinimapVR.cs, VR/).
/// </summary>
public partial class Minimap : MonoBehaviour
{
    [Tooltip("Metres from your position to the edge of the minimap (+ / - change it)")]
    public float radius = 600f;
    [Tooltip("Pixels across the minimap image (more = sharper, slower to update)")]
    [Range(64, 256)] public int resolution = 128;
    [Tooltip("How far ahead the hill profile looks (m)")]
    public float profileAhead = 2000f;
    [Tooltip("Height of the minimap as a share of the screen height")]
    public float sizeOfScreen = 0.26f;
    [Tooltip("Colour the ground by hazard zone (H toggles); off = plain terrain colours")]
    public bool showHazards = true;

    /// <summary>Screen pixels the minimap takes at the bottom-left, so other labels can sit above it.</summary>
    public static float BottomLeftHeight { get; private set; }
    /// <summary>Screen area the minimap + hill profile cover (GUI coordinates), or empty.</summary>
    public static Rect BottomLeftRect { get; private set; }

    static readonly Color[] TierColors =
    {
        new Color32(0x2e, 0xcc, 0x71, 255), new Color32(0xf1, 0xc4, 0x0f, 255),
        new Color32(0xe6, 0x7e, 0x22, 255), new Color32(0xe7, 0x4c, 0x3c, 255),
        new Color32(0xff, 0x3c, 0xf0, 255),
    };
    static readonly Color Low = new Color(0.30f, 0.14f, 0.08f), High = new Color(0.93f, 0.78f, 0.58f);
    static readonly Color WaypointColor = new Color(1f, 0.85f, 0.2f), NextColor = new Color(0.2f, 0.9f, 1f);
    static readonly Color HomeColor = new Color(0.2f, 1f, 0.45f);

    Terraingen terraingen;
    MapView mapView;
    Transform player;

    Texture2D mapTex, profileTex;
    Color[] mapPx, profilePx;
    float[] elev;                 // heights for the minimap image, reused
    Vector2 builtCentre;
    float builtRadius, lastBuild = -10f, lastProfile = -10f;
    int builtVersion = -1;
    float contourStep;
    string profileText = "";
    GUIStyle textStyle, iconStyle;

    const int ProfileW = 256, ProfileH = 80;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureExists()
    {
        if (FindAnyObjectByType<Minimap>() != null) return;
        Terraingen tg = FindAnyObjectByType<Terraingen>();
        if (tg != null) tg.gameObject.AddComponent<Minimap>();
    }

    void Start()
    {
        terraingen = GetComponent<Terraingen>();
        if (terraingen == null) terraingen = FindAnyObjectByType<Terraingen>();
        player = terraingen != null ? terraingen.player : null;
    }

    void OnDisable()
    {
        BottomLeftHeight = 0f;
        BottomLeftRect = Rect.zero;
    }

    void OnDestroy()
    {
        BottomLeftHeight = 0f;
        BottomLeftRect = Rect.zero;
        if (mapTex != null) Destroy(mapTex);
        if (profileTex != null) Destroy(profileTex);
        DestroyVR();
    }

    void Update()
    {
        if (mapView == null) mapView = FindAnyObjectByType<MapView>();
        Keyboard kb = Keyboard.current;
        bool mapOpen = mapView != null && mapView.IsOpen;
        if (kb != null && !mapOpen)
        {
            if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame) radius = Mathf.Max(150f, radius / 1.5f);
            if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame) radius = Mathf.Min(20000f, radius * 1.5f);
            if (kb.hKey.wasPressedThisFrame)
            {
                showHazards = !showHazards;
                lastBuild = -10f; // redraw now
                builtVersion = -1;
            }
        }
        if (MarsmapInput.XRActive && !mapOpen) VRZoom();
        if (mapOpen || player == null || terraingen == null || !terraingen.TryGetMapBounds(out _)) return;

        Vector2 here = new Vector2(player.position.x, player.position.z);
        int version = mapView != null ? mapView.RouteVersion : 0;
        bool moved = (here - builtCentre).magnitude > radius * 0.05f;
        if (Time.unscaledTime - lastBuild > 0.25f &&
            (moved || builtRadius != radius || version != builtVersion || Time.unscaledTime - lastBuild > 30f))
            BuildMap(here, version);
        if (Time.unscaledTime - lastProfile > 0.5f) BuildProfile();
    }

    // ---------------------------------------------------------------
    // Minimap image (north up; it's turned when drawn)
    // ---------------------------------------------------------------
    void BuildMap(Vector2 centre, int version)
    {
        lastBuild = Time.unscaledTime;
        builtCentre = centre;
        builtRadius = radius;
        builtVersion = version;
        int n = resolution;
        if (mapTex == null || mapTex.width != n)
        {
            if (mapTex != null) Destroy(mapTex);
            mapTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            mapPx = new Color[n * n];
        }
        float step = 2f * radius / (n - 1);

        // Heights
        if (elev == null || elev.Length != n * n) elev = new float[n * n];
        float lo = float.MaxValue, hi = float.MinValue;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float e = Elevation(new Vector3(centre.x - radius + i * step, 0f, centre.y - radius + j * step));
                elev[j * n + i] = e;
                lo = Mathf.Min(lo, e);
                hi = Mathf.Max(hi, e);
            }
        float span = Mathf.Max(hi - lo, 1f);
        contourStep = NiceStep(span / 8f);

        // Shaded relief, contours, steep ground tinted, round with a soft edge
        Vector3 light = new Vector3(-1f, 1.2f, 1f).normalized; // from the north-west, like paper maps
        float half = (n - 1) * 0.5f;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                float ex = elev[j * n + Mathf.Min(i + 1, n - 1)] - elev[j * n + Mathf.Max(i - 1, 0)];
                float ez = elev[Mathf.Min(j + 1, n - 1) * n + i] - elev[Mathf.Max(j - 1, 0) * n + i];
                float gx = ex / (2f * step), gz = ez / (2f * step);
                float slope = Mathf.Atan(Mathf.Sqrt(gx * gx + gz * gz)) * Mathf.Rad2Deg;
                Vector3 normal = new Vector3(-gx * 3f, 1f, -gz * 3f).normalized; // x3: hills read clearly
                float shade = 0.35f + 0.65f * Mathf.Max(0f, Vector3.Dot(normal, light));
                Color c;
                if (showHazards)
                {
                    // Hazard zone of this spot's slope, shaded so the hills still read
                    int tier = slope < 10f ? 0 : slope < 20f ? 1 : slope < 30f ? 2 : 3;
                    c = TierColors[tier] * (0.45f + 0.55f * shade);
                }
                else
                {
                    c = Color.Lerp(Low, High, (elev[k] - lo) / span) * shade;
                    if (slope > 20f) c = Color.Lerp(c, TierColors[slope > 30f ? 3 : 2], 0.45f);
                }
                if (i > 0 && j > 0 && Band(elev[k]) != Band(elev[k - 1]) || j > 0 && Band(elev[k]) != Band(elev[k - n]))
                    c *= 0.7f; // contour line
                float d = Mathf.Sqrt((i - half) * (i - half) + (j - half) * (j - half)) / half;
                c.a = Mathf.Clamp01((1f - d) * 25f);
                if (d > 0.94f && d <= 1f) c = new Color(0.05f, 0.05f, 0.05f, c.a); // rim
                mapPx[k] = c;
            }

        // The route still ahead, the remaining waypoints and Home Base
        if (mapView != null)
        {
            float[] rx = mapView.RouteX, rz = mapView.RouteZ;
            int[] tier = mapView.RouteTier;
            if (rx != null && mapView.TryGetCurrentLeg(out int from, out _))
                for (int k = from; k + 1 < rx.Length; k++)
                {
                    int t = tier != null && k < tier.Length ? Mathf.Clamp(tier[k], 0, 4) : 0;
                    DrawSegment(Pixel(rx[k], rz[k], step), Pixel(rx[k + 1], rz[k + 1], step), TierColors[t], 1.6f);
                }
            IReadOnlyList<Vector3> wps = mapView.Waypoints;
            for (int i = mapView.NextWaypoint; i < wps.Count; i++)
                DrawDot(Pixel(wps[i].x, wps[i].z, step), i == mapView.NextWaypoint ? NextColor : WaypointColor, 3.2f);
            if (mapView.HasHomeBase)
                DrawDot(Pixel(mapView.HomeBase.x, mapView.HomeBase.z, step), HomeColor, 3.6f);
        }

        mapTex.SetPixels(mapPx);
        mapTex.Apply(false);
    }

    int Band(float e) => Mathf.FloorToInt(e / contourStep);

    static float NiceStep(float raw)
    {
        float p = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(Mathf.Max(raw, 0.01f))));
        float m = raw / p;
        return (m < 2f ? 1f : m < 5f ? 2f : 5f) * p;
    }

    Vector2 Pixel(float x, float z, float step) =>
        new Vector2((x - (builtCentre.x - builtRadius)) / step, (z - (builtCentre.y - builtRadius)) / step);

    // Draws only the part of a->b that's on the image (a long leg can cross it from far away)
    void DrawSegment(Vector2 a, Vector2 b, Color c, float width)
    {
        int n = resolution;
        float lo = -width, hi = n - 1 + width, t0 = 0f, t1 = 1f;
        Vector2 d = b - a;
        for (int ax = 0; ax < 2; ax++)
        {
            float p = a[ax], dp = d[ax];
            if (Mathf.Abs(dp) < 1e-6f)
            {
                if (p < lo || p > hi) return;
                continue;
            }
            float ta = (lo - p) / dp, tb = (hi - p) / dp;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = Mathf.Max(t0, ta);
            t1 = Mathf.Min(t1, tb);
            if (t0 > t1) return;
        }
        Vector2 a2 = a + d * t0, b2 = a + d * t1;
        int steps = Mathf.Max(1, Mathf.CeilToInt((b2 - a2).magnitude));
        for (int s = 0; s <= steps; s++) Stamp(Vector2.Lerp(a2, b2, s / (float)steps), c, width);
    }

    void DrawDot(Vector2 p, Color c, float r)
    {
        Stamp(p, Color.black, r + 1.2f);
        Stamp(p, c, r);
    }

    void Stamp(Vector2 p, Color c, float r)
    {
        int n = resolution;
        float half = (n - 1) * 0.5f;
        for (int y = Mathf.FloorToInt(p.y - r); y <= Mathf.CeilToInt(p.y + r); y++)
            for (int x = Mathf.FloorToInt(p.x - r); x <= Mathf.CeilToInt(p.x + r); x++)
            {
                if (x < 0 || y < 0 || x >= n || y >= n) continue;
                if ((x - p.x) * (x - p.x) + (y - p.y) * (y - p.y) > r * r) continue;
                if ((x - half) * (x - half) + (y - half) * (y - half) > half * half * 0.88f) continue; // inside the rim
                c.a = 1f;
                mapPx[y * n + x] = c;
            }
    }

    // ---------------------------------------------------------------
    // Upcoming hills: elevation along the route ahead (or straight ahead without one)
    // ---------------------------------------------------------------
    void BuildProfile()
    {
        lastProfile = Time.unscaledTime;
        var dist = new List<float>();
        var elev = new List<float>();
        var tiers = new List<int>();
        float markAt = -1f; // where the next waypoint is, along the profile
        string what;

        float[] rx = mapView != null ? mapView.RouteX : null;
        if (rx != null && mapView.TryGetCurrentLeg(out int from, out int to) && to > from)
        {
            float[] rz = mapView.RouteZ, re = mapView.RouteElev;
            int[] rt = mapView.RouteTier;
            int Tier(int k) => rt != null && k < rt.Length ? Mathf.Clamp(rt[k], 0, 4) : 0;
            // Where you are on the leg: the closest point on its segments
            var me = new Vector2(player.position.x, player.position.z);
            int seg0 = from;
            float t0 = 0f, best = float.MaxValue;
            for (int k = from; k < to; k++)
            {
                var a = new Vector2(rx[k], rz[k]);
                Vector2 ab = new Vector2(rx[k + 1], rz[k + 1]) - a;
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(me - a, ab) / ab.sqrMagnitude) : 0f;
                float dd = (a + ab * t - me).sqrMagnitude;
                if (dd < best) { best = dd; seg0 = k; t0 = t; }
            }
            // Walk the route from there, sampling the ground every ~20 m so hills show even
            // on long straight pieces (e.g. the fallback lines when the server is offline)
            float sample = Mathf.Max(10f, profileAhead / 100f);
            Vector2 prev = Vector2.Lerp(new Vector2(rx[seg0], rz[seg0]), new Vector2(rx[seg0 + 1], rz[seg0 + 1]), t0);
            float acc = 0f;
            dist.Add(0f);
            elev.Add(Elevation(new Vector3(prev.x, 0f, prev.y)));
            tiers.Add(Tier(seg0));
            for (int k = seg0 + 1; k < rx.Length && acc < profileAhead; k++)
            {
                var p = new Vector2(rx[k], rz[k]);
                float len = (p - prev).magnitude;
                int sub = Mathf.Max(1, Mathf.CeilToInt(len / sample));
                for (int s = 1; s < sub && acc + len * s / sub < profileAhead; s++)
                {
                    Vector2 q = Vector2.Lerp(prev, p, s / (float)sub);
                    dist.Add(acc + len * s / sub);
                    elev.Add(Elevation(new Vector3(q.x, 0f, q.y)));
                    tiers.Add(Tier(k - 1));
                }
                acc += len;
                dist.Add(acc);
                elev.Add(re != null && k < re.Length ? re[k] : Elevation(new Vector3(p.x, 0f, p.y)));
                tiers.Add(Tier(k));
                if (k == to) markAt = acc;
                prev = p;
            }
            what = "ROUTE AHEAD";
        }
        else
        {
            // No route: the ground straight ahead of you
            Vector3 fwd = Quaternion.Euler(0f, Heading(), 0f) * Vector3.forward;
            float step = Mathf.Max(10f, profileAhead / 100f);
            float prev = 0f;
            for (float d = 0f; d <= profileAhead; d += step)
            {
                float e = Elevation(player.position + fwd * d);
                float grade = d > 0f ? Mathf.Abs(Mathf.Atan((e - prev) / step) * Mathf.Rad2Deg) : 0f;
                dist.Add(d);
                elev.Add(e);
                tiers.Add(grade < 10f ? 0 : grade < 20f ? 1 : grade < 30f ? 2 : 3);
                prev = e;
            }
            for (int k = 0; k + 1 < tiers.Count; k++) tiers[k] = tiers[k + 1]; // grade of k -> k+1
            what = "GROUND AHEAD";
        }
        if (dist.Count < 2) { profileText = ""; return; }

        // Numbers for the label
        float climb = 0f, descent = 0f, steepest = 0f;
        for (int k = 1; k < elev.Count; k++)
        {
            float de = elev[k] - elev[k - 1], dd = Mathf.Max(dist[k] - dist[k - 1], 0.01f);
            if (de > 0f) climb += de; else descent -= de;
            steepest = Mathf.Max(steepest, Mathf.Abs(Mathf.Atan(de / dd) * Mathf.Rad2Deg));
        }
        float total = dist[dist.Count - 1];
        profileText = $"<b>{what}</b> {FormatDistance(total)}   <color=#9fe>+{climb:F0} m</color> / " +
                      $"<color=#fc9>-{descent:F0} m</color>   steepest {steepest:F0}°";

        // Filled area, coloured by hazard tier, with the next waypoint marked
        if (profileTex == null)
        {
            profileTex = new Texture2D(ProfileW, ProfileH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            profilePx = new Color[ProfileW * ProfileH];
        }
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (float e in elev) { lo = Mathf.Min(lo, e); hi = Mathf.Max(hi, e); }
        float pad = Mathf.Max((hi - lo) * 0.15f, 10f);
        lo -= pad;
        hi += pad;
        int seg = 0;
        for (int x = 0; x < ProfileW; x++)
        {
            float d = x / (float)(ProfileW - 1) * total;
            while (seg < dist.Count - 2 && dist[seg + 1] < d) seg++;
            float t = Mathf.InverseLerp(dist[seg], dist[seg + 1], d);
            float e = Mathf.Lerp(elev[seg], elev[seg + 1], t);
            int top = Mathf.RoundToInt((e - lo) / (hi - lo) * (ProfileH - 1));
            Color fill = TierColors[tiers[seg]] * 0.75f;
            bool mark = markAt >= 0f && Mathf.Abs(d - markAt) < total / ProfileW * 1.5f;
            for (int y = 0; y < ProfileH; y++)
            {
                Color c = y < top ? fill : y <= top + 1 ? Color.white : new Color(0f, 0f, 0f, 0f);
                if (mark && y > top) c = NextColor; // the next waypoint
                if (x < 2 && y <= top + 1) c = NextColor; // you
                profilePx[y * ProfileW + x] = c;
            }
        }
        profileTex.SetPixels(profilePx);
        profileTex.Apply(false);
    }

    // The way you face, as a compass heading: your head in a headset (the body only turns with
    // snap turn there), the player otherwise
    float Heading()
    {
        Camera cam = MarsmapInput.XRActive ? Camera.main : null;
        Transform t = cam != null ? cam.transform : player;
        Vector3 f = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (f.sqrMagnitude < 1e-6f) f = Vector3.ProjectOnPlane(t.up, Vector3.up); // looking straight down
        return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
    }

    float Elevation(Vector3 world) =>
        terraingen.TryGetElevation(world, out float m) ? m : terraingen.GroundY(world);

    // ---------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------
    void OnGUI()
    {
        bool mapOpen = mapView != null && mapView.IsOpen;
        if (mapTex == null || player == null || mapOpen || MarsmapInput.XRActive) // no IMGUI in a headset
        {
            BottomLeftHeight = 0f;
            BottomLeftRect = Rect.zero;
            return;
        }
        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true, wordWrap = false };
            textStyle.normal.textColor = Color.white;
            iconStyle = new GUIStyle(textStyle) { fontSize = 16, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        }

        const float margin = 10f;
        float size = Mathf.Clamp(Screen.height * sizeOfScreen, 130f, 340f);
        var rect = new Rect(margin, Screen.height - margin - size, size, size);
        Vector2 centre = rect.center;
        float heading = Heading();
        const float legendH = 18f;
        BottomLeftHeight = size + margin + 4f + legendH;
        BottomLeftRect = Rect.MinMaxRect(rect.xMin, rect.yMin - legendH - 2f, rect.xMax, rect.yMax);

        // You may have walked a little since the image was made: shift it, then turn it
        float pxPerMetre = size / (2f * builtRadius);
        Vector2 offset = new Vector2(builtCentre.x - player.position.x, -(builtCentre.y - player.position.z)) * pxPerMetre;
        Matrix4x4 saved = GUI.matrix;
        GUIUtility.RotateAroundPivot(-heading, centre); // the way you face points up
        GUI.DrawTexture(new Rect(rect.position + offset, rect.size), mapTex);
        GUI.matrix = saved;

        // You, north, scale
        iconStyle.normal.textColor = NextColor;
        GUI.Label(new Rect(centre.x - 10f, centre.y - 11f, 20f, 20f), "▲", iconStyle);
        float a = -heading * Mathf.Deg2Rad, nr = size * 0.44f;
        // Up on the image is north; turned by -heading it ends up here (facing east: on the left)
        var north = new Vector2(centre.x + nr * Mathf.Sin(a), centre.y - nr * Mathf.Cos(a));
        iconStyle.normal.textColor = Color.white;
        DrawBox(new Rect(north.x - 9f, north.y - 9f, 18f, 18f), new Color(0f, 0f, 0f, 0.7f));
        GUI.Label(new Rect(north.x - 9f, north.y - 10f, 18f, 18f), "N", iconStyle);
        // Legend
        var legend = new Rect(rect.x, rect.y - legendH - 2f, size * 1.2f, legendH);
        DrawBox(legend, new Color(0f, 0f, 0f, 0.6f));
        if (showHazards)
        {
            string[] names = { "0-10°", "10-20°", "20-30°", ">30°" };
            float x = legend.x + 4f, w = (legend.width - 40f) / 4f;
            for (int t = 0; t < 4; t++)
            {
                DrawBox(new Rect(x, legend.y + 4f, 10f, 10f), TierColors[t]);
                GUI.Label(new Rect(x + 12f, legend.y, w, legendH), names[t], textStyle);
                x += w;
            }
            GUI.Label(new Rect(legend.xMax - 26f, legend.y, 26f, legendH), "(H)", textStyle);
        }
        else GUI.Label(new Rect(legend.x + 4f, legend.y, legend.width, legendH), "Terrain: dark low, pale high  (H: hazards)", textStyle);

        string scale = $"{FormatDistance(builtRadius)}  (+/-)";
        Vector2 ss = textStyle.CalcSize(new GUIContent(scale));
        DrawBox(new Rect(rect.x + 2f, rect.yMax - ss.y - 2f, ss.x + 8f, ss.y), new Color(0f, 0f, 0f, 0.6f));
        GUI.Label(new Rect(rect.x + 6f, rect.yMax - ss.y - 2f, ss.x, ss.y), scale, textStyle);

        // Upcoming hills, to the right of the minimap
        if (profileTex != null && !string.IsNullOrEmpty(profileText))
        {
            float pw = size * 1.35f, ph = size * 0.42f;
            var box = new Rect(rect.xMax + 8f, rect.yMax - ph - 22f, pw, ph + 22f);
            DrawBox(box, new Color(0.06f, 0.035f, 0.025f, 0.75f));
            BottomLeftRect = Rect.MinMaxRect(rect.xMin, Mathf.Min(rect.yMin - legendH - 2f, box.yMin), box.xMax, rect.yMax);
            GUI.Label(new Rect(box.x + 6f, box.y + 2f, pw - 12f, 18f), profileText, textStyle);
            GUI.DrawTexture(new Rect(box.x + 4f, box.y + 20f, pw - 8f, ph - 2f), profileTex);
        }
    }

    static string FormatDistance(float m) => m >= 1000f ? $"{m / 1000f:0.#} km" : $"{m:F0} m";

    static void DrawBox(Rect r, Color c)
    {
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
    }
}
