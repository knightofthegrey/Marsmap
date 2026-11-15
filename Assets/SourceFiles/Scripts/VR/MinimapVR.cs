using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The minimap in a headset: the same round, heading-up map and "upcoming hills" profile as on
/// desktop (Minimap.cs builds both images), on a small panel in the lower right of your view.
/// Right stick up / down zooms it (snap turn keeps left / right). Hidden while the EVA map is open.
/// </summary>
public partial class Minimap
{
    [Header("VR (headset only)")]
    [Tooltip("Where the minimap floats: metres right / up of the view centre, and ahead")]
    public Vector3 vrOffset = new Vector3(0.4f, -0.27f, 1.3f);
    [Tooltip("Width of the minimap in a headset (m)")]
    public float vrSize = 0.26f;

    const float VRPixels = 260f; // canvas units across the map (font sizes below are in these)

    GameObject vrRoot;
    RectTransform vrTurn, vrMapRect, vrNorth, vrProfileBox;
    RawImage vrMap, vrProfile;
    Text vrScale, vrProfileText, vrLegend;
    Camera vrAttached;
    Texture2D vrDisc;
    bool vrZoomArmed = true;
    bool vrLegendHazards;

    void LateUpdate()
    {
        bool show = MarsmapInput.XRActive && mapTex != null && player != null && !(mapView != null && mapView.IsOpen);
        if (!show)
        {
            if (vrRoot != null && vrRoot.activeSelf) vrRoot.SetActive(false);
            return;
        }
        if (vrRoot == null) BuildVR();
        if (!vrRoot.activeSelf) vrRoot.SetActive(true);
        if (!VRHud.Follow(vrRoot.transform, vrOffset, ref vrAttached)) return;

        // Same as the desktop drawing: shift by how far you've walked since the image was made,
        // then turn it so the way you face is up (UI turns counter-clockwise: north goes left when you face east)
        float heading = Heading();
        vrTurn.localEulerAngles = new Vector3(0f, 0f, heading);
        float pxPerMetre = VRPixels / (2f * builtRadius);
        vrMapRect.anchoredPosition = new Vector2(builtCentre.x - player.position.x, builtCentre.y - player.position.z) * pxPerMetre;
        if (vrMap.texture != mapTex) vrMap.texture = mapTex;
        float a = heading * Mathf.Deg2Rad, nr = VRPixels * 0.44f;
        vrNorth.anchoredPosition = new Vector2(-nr * Mathf.Sin(a), nr * Mathf.Cos(a));

        string scale = FormatDistance(builtRadius);
        if (vrScale.text != scale) vrScale.text = scale;
        if (vrLegendHazards != showHazards || string.IsNullOrEmpty(vrLegend.text))
        {
            vrLegendHazards = showHazards;
            vrLegend.text = showHazards
                ? "<color=#2ecc71>■</color> 0-10°  <color=#f1c40f>■</color> 10-20°  <color=#e67e22>■</color> 20-30°  <color=#e74c3c>■</color> >30°"
                : "Terrain: dark low, pale high";
        }

        bool profile = profileTex != null && !string.IsNullOrEmpty(profileText);
        vrProfileBox.gameObject.SetActive(profile);
        if (profile)
        {
            if (vrProfile.texture != profileTex) vrProfile.texture = profileTex;
            if (vrProfileText.text != profileText) vrProfileText.text = profileText;
        }
    }

    // Right stick up / down: zoom in / out one step per push (left / right is VRRig's snap turn)
    void VRZoom()
    {
        Vector2 s = MarsmapInput.Turn.ReadValue<Vector2>();
        if (vrZoomArmed && Mathf.Abs(s.y) > 0.7f && Mathf.Abs(s.y) > Mathf.Abs(s.x))
        {
            vrZoomArmed = false;
            radius = s.y > 0f ? Mathf.Max(150f, radius / 1.5f) : Mathf.Min(20000f, radius * 1.5f);
        }
        else if (Mathf.Abs(s.y) < 0.3f) vrZoomArmed = true;
    }

    void BuildVR()
    {
        vrRoot = new GameObject("Minimap (VR)", typeof(RectTransform));
        var canvas = vrRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 80;
        var root = (RectTransform)vrRoot.transform;
        root.sizeDelta = new Vector2(VRPixels, VRPixels);
        root.localScale = Vector3.one * (vrSize / VRPixels);

        // Dark disc behind the map, so it reads against bright ground
        RawImage disc = NewUI<RawImage>("Backdrop", root, Vector2.zero, new Vector2(VRPixels + 12f, VRPixels + 12f));
        disc.texture = vrDisc = Disc(64);
        disc.color = new Color(0f, 0f, 0f, 0.55f);

        vrTurn = NewUI<RectTransform>("Turn", root, Vector2.zero, new Vector2(VRPixels, VRPixels));
        vrMap = NewUI<RawImage>("Map", vrTurn, Vector2.zero, new Vector2(VRPixels, VRPixels));
        vrMapRect = vrMap.rectTransform;

        Text you = NewText("You", root, Vector2.zero, new Vector2(30f, 30f), 24, TextAnchor.MiddleCenter);
        you.text = "▲";
        you.color = NextColor;
        you.fontStyle = FontStyle.Bold;

        Image northBg = NewUI<Image>("North", root, Vector2.zero, new Vector2(26f, 26f));
        northBg.color = new Color(0f, 0f, 0f, 0.7f);
        vrNorth = northBg.rectTransform;
        Text n = NewText("N", vrNorth, Vector2.zero, new Vector2(26f, 26f), 18, TextAnchor.MiddleCenter);
        n.text = "N";
        n.fontStyle = FontStyle.Bold;

        vrScale = NewText("Scale", root, new Vector2(-VRPixels * 0.36f, -VRPixels * 0.47f), new Vector2(110f, 22f), 15, TextAnchor.MiddleLeft);
        vrLegend = NewText("Legend", root, new Vector2(0f, -VRPixels * 0.5f - 18f), new Vector2(VRPixels * 1.3f, 22f), 14, TextAnchor.MiddleCenter);

        // Upcoming hills, above the map
        float pw = VRPixels * 1.35f, ph = VRPixels * 0.42f;
        Image box = NewUI<Image>("Upcoming hills", root, new Vector2(0f, VRPixels * 0.5f + 14f + (ph + 22f) * 0.5f), new Vector2(pw, ph + 22f));
        box.color = new Color(0.06f, 0.035f, 0.025f, 0.8f);
        vrProfileBox = box.rectTransform;
        vrProfileText = NewText("Text", vrProfileBox, new Vector2(0f, (ph + 22f) * 0.5f - 11f), new Vector2(pw - 12f, 20f), 13, TextAnchor.MiddleLeft);
        vrProfileText.horizontalOverflow = HorizontalWrapMode.Overflow;
        vrProfile = NewUI<RawImage>("Profile", vrProfileBox, new Vector2(0f, -10f), new Vector2(pw - 8f, ph - 2f));
        vrRoot.SetActive(false);
    }

    void DestroyVR()
    {
        if (vrRoot != null) Destroy(vrRoot);
        if (vrDisc != null) Destroy(vrDisc);
        vrRoot = null;
        vrDisc = null;
    }

    static T NewUI<T>(string name, Transform parent, Vector2 pos, Vector2 size) where T : Component
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        if (typeof(T) == typeof(RectTransform)) return rt as T;
        T c = go.AddComponent<T>();
        if (c is Graphic g) g.raycastTarget = false;
        return c;
    }

    static Text NewText(string name, Transform parent, Vector2 pos, Vector2 size, int fontSize, TextAnchor anchor)
    {
        Text t = NewUI<Text>(name, parent, pos, size);
        t.font = VRHud.BuiltinFont();
        t.fontSize = fontSize;
        t.color = Color.white;
        t.alignment = anchor;
        t.supportRichText = true;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    // A soft-edged white disc
    static Texture2D Disc(int n)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01((1f - Mathf.Sqrt(dx * dx + dy * dy)) * 20f));
            }
        tex.SetPixels(px);
        tex.Apply(false, true);
        return tex;
    }
}
