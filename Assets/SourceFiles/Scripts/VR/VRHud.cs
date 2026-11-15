using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// HUD pieces for a headset. IMGUI (OnGUI) and Screen Space canvases are not drawn inside a
/// headset, so VR needs world-space versions.
///
/// THE HUD SHEET (VRHud.Sheet): one head-locked world-space canvas 1.5 m ahead, ±25° wide,
/// 16:9. It's laid out in the same "desktop units" as the scene's Canvas (reference 800x600,
/// matched on width: 800 units across), so anything anchored top-right on desktop is anchored
/// top-right here too, and the VR layout matches the desktop one:
///
///    compass (top-left)        alerts (top centre)        navigation + environment (top-right)
///
///                          (centre kept clear for walking)
///
///    layer label (left, above the minimap)
///    minimap + upcoming hills (bottom-left)                      EVA route (bottom-right)
///
/// Scale: 1 unit ≈ 1.75 mm at 1.5 m ≈ 0.067°. Font sizes for a Quest: 15 titles, 13 body,
/// 11 the smallest (smaller is hard to read in a headset). Everything on the sheet draws on top
/// of the world (terrain never hides it) and of the dust tint.
///
/// Also here: a full-view tint (dust), and text tags that sit at a point in the world.
/// All of them re-attach themselves if Camera.main changes and never throw without a camera.
/// </summary>
public static class VRHud
{
    public const float SheetDistance = 1.5f;     // metres in front of the eyes
    public const float SheetHalfAngle = 25f;     // degrees from the view centre to the left/right edge
    public const float SheetUnitsWide = 800f;    // the scene Canvas's reference width
    public const float SheetUnitsHigh = 450f;    // 16:9
    public const int SheetSortingOrder = 100;    // above world tags (90), the hologram's panel (60), dust

    // Font sizes in sheet units (see the class comment)
    public const int TitleFont = 15, BodyFont = 13, SmallFont = 11;

    /// <summary>Background of the desktop panels (dark, warm, see-through).</summary>
    public static readonly Color PanelColor = new Color(0.06f, 0.035f, 0.025f, 0.8f);

    public static float SheetWidthMetres => 2f * SheetDistance * Mathf.Tan(SheetHalfAngle * Mathf.Deg2Rad);
    public static float MetresPerUnit => SheetWidthMetres / SheetUnitsWide;

    static RectTransform sheet;
    static Material onTop;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        sheet = null;
        onTop = null;
    }

    /// <summary>The head-locked HUD canvas (created on first use; only use it in a headset).
    /// Children are RectTransforms in sheet units: anchor them like on the desktop Canvas.</summary>
    public static RectTransform Sheet
    {
        get
        {
            if (sheet == null) BuildSheet();
            return sheet;
        }
    }

    static void BuildSheet()
    {
        var go = new GameObject("VR HUD", typeof(RectTransform));
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = SheetSortingOrder;
        // World-space canvases rasterise text at fontSize x this: 4 keeps 11-15 unit text sharp
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 4f;
        sheet = (RectTransform)go.transform;
        sheet.anchorMin = sheet.anchorMax = sheet.pivot = new Vector2(0.5f, 0.5f);
        sheet.sizeDelta = new Vector2(SheetUnitsWide, SheetUnitsHigh);
        sheet.localScale = Vector3.one * MetresPerUnit;
        go.AddComponent<VRHudSheet>().Attach();
    }

    // ---------------------------------------------------------------
    // Building blocks for the sheet
    // ---------------------------------------------------------------

    /// <summary>A panel on the sheet. anchor = where on the sheet (0,0 bottom-left .. 1,1 top-right),
    /// also used as the pivot so the panel grows away from that edge/corner; anchoredPos and
    /// size in sheet units (like a desktop panel's).</summary>
    public static RectTransform AddPanel(string name, Vector2 anchor, Vector2 anchoredPos, Vector2 size)
    {
        RectTransform rt = NewRect(name, Sheet);
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        return rt;
    }

    public static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent != null ? parent.gameObject.layer : go.layer;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>Text on the sheet: legacy font, rich text, wraps, drawn on top, no raycasts.</summary>
    public static Text AddText(Transform parent, string name, int size, FontStyle style = FontStyle.Normal,
                               TextAnchor anchor = TextAnchor.UpperLeft)
    {
        Text t = NewRect(name, parent).gameObject.AddComponent<Text>();
        t.font = BuiltinFont();
        t.fontSize = size;
        t.fontStyle = style;
        t.color = Color.white;
        t.alignment = anchor;
        t.supportRichText = true;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        OnTop(t);
        return t;
    }

    /// <summary>A plain coloured rectangle, drawn on top, no raycasts.</summary>
    public static Image AddImage(Transform parent, string name, Color color)
    {
        Image img = NewRect(name, parent).gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        OnTop(img);
        return img;
    }

    /// <summary>Stretch a RectTransform over its parent, with padding (left/bottom, right/top).</summary>
    public static void Stretch(RectTransform rt, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
    }

    /// <summary>Draw this UI graphic on top of the world: nearby terrain (walking up a slope,
    /// looking down) would otherwise cut through a HUD 1.5 m away.</summary>
    public static void OnTop(Graphic g)
    {
        if (g == null) return;
        if (onTop == null)
        {
            onTop = new Material(Canvas.GetDefaultCanvasMaterial()) { name = "VR HUD (on top)" };
            onTop.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        }
        g.material = onTop;
    }

    /// <summary>Where a UI element is on the sheet, in sheet units with (0,0) at the sheet's
    /// centre; Rect.zero if there's no sheet or it isn't on it (e.g. switched off).</summary>
    public static Rect SheetRectOf(RectTransform rt)
    {
        if (sheet == null || rt == null || !rt.gameObject.activeInHierarchy || !rt.IsChildOf(sheet)) return Rect.zero;
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        Vector2 lo = sheet.InverseTransformPoint(corners[0]); // bottom-left
        Vector2 hi = sheet.InverseTransformPoint(corners[2]); // top-right
        return Rect.MinMaxRect(Mathf.Min(lo.x, hi.x), Mathf.Min(lo.y, hi.y), Mathf.Max(lo.x, hi.x), Mathf.Max(lo.y, hi.y));
    }

    // ---------------------------------------------------------------
    // Ready-made pieces
    // ---------------------------------------------------------------

    /// <summary>A text panel on the sheet (bold title + smaller detail lines), as tall as its text.
    /// anchor = where on the sheet (e.g. (0,0) bottom-left, (1,0) bottom-right); anchoredPos and
    /// widthUnits in sheet units.</summary>
    public static VRHudLabel CreateLabel(string name, Vector2 anchor, Vector2 anchoredPos, float widthUnits)
    {
        RectTransform rt = AddPanel(name, anchor, anchoredPos, new Vector2(widthUnits, 40f));
        var label = rt.gameObject.AddComponent<VRHudLabel>();
        label.Build();
        return label;
    }

    /// <summary>A coloured, see-through quad covering the whole view (e.g. a dust tint).</summary>
    public static VRHudTint CreateTint(string name)
    {
        var go = new GameObject(name);
        var tint = go.AddComponent<VRHudTint>();
        tint.Build();
        return tint;
    }

    /// <summary>A text tag at a point in the world that always faces you (e.g. a waypoint's number).
    /// Not attached to the camera: set its position every frame.</summary>
    /// sortingOrder: transparent things draw in this order (then far to near). Markers far out in the
    /// world want a low one so the head-locked HUD stays on top; ones on the hologram a high one.
    public static VRWorldTag CreateWorldTag(string name, int sortingOrder = 90)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var tag = go.AddComponent<VRWorldTag>();
        tag.Build(sortingOrder);
        return tag;
    }

    internal static Font BuiltinFont()
    {
        Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return f != null ? f : Font.CreateDynamicFontFromOSFont("Arial", 16);
    }

    /// <summary>Keep `t` attached to Camera.main at the given local pose. Returns false if
    /// there is no camera yet.</summary>
    internal static bool Follow(Transform t, Vector3 localPos, ref Camera attached)
    {
        Camera cam = Camera.main;
        if (cam == null) return false;
        if (cam != attached || t.parent != cam.transform)
        {
            t.SetParent(cam.transform, false);
            t.localPosition = localPos;
            t.localRotation = Quaternion.identity;
            attached = cam;
        }
        return true;
    }

    internal static float CanvasScale => 1f / 1000f;     // VRWorldTag: canvas units per metre
}

/// <summary>Keeps the HUD sheet 1.5 m in front of the eyes.</summary>
public class VRHudSheet : MonoBehaviour
{
    Camera attached;

    internal void Attach() => Follow();

    void LateUpdate() => Follow();

    void Follow() => VRHud.Follow(transform, new Vector3(0f, 0f, VRHud.SheetDistance), ref attached);
}

/// <summary>A panel on the HUD sheet: bold title, smaller detail lines below it; as tall as its
/// text (it grows away from the sheet edge it's anchored to).</summary>
public class VRHudLabel : MonoBehaviour
{
    Text title, detail;
    RectTransform rect;

    public RectTransform Rect => rect;

    internal void Build()
    {
        rect = (RectTransform)transform;
        Image bg = gameObject.AddComponent<Image>();
        bg.color = VRHud.PanelColor;
        bg.raycastTarget = false;
        VRHud.OnTop(bg);

        var layout = gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 5, 6);
        layout.spacing = 1f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        var fit = gameObject.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        title = VRHud.AddText(transform, "Title", VRHud.BodyFont + 1, FontStyle.Bold);
        detail = VRHud.AddText(transform, "Detail", VRHud.SmallFont);
        detail.color = new Color(0.85f, 0.85f, 0.85f);
    }

    public void Set(string titleText, string detailText)
    {
        titleText = titleText ?? "";
        detailText = detailText ?? "";
        if (title != null && title.text != titleText) title.text = titleText;
        if (detail != null)
        {
            if (detail.text != detailText) detail.text = detailText;
            bool show = detailText.Length > 0;
            if (detail.gameObject.activeSelf != show) detail.gameObject.SetActive(show);
        }
    }

    public void SetVisible(bool visible)
    {
        if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
    }

    /// <summary>Move it (sheet units, from its anchor), e.g. to sit above the minimap.</summary>
    public void SetPosition(Vector2 anchoredPos)
    {
        if (rect != null && rect.anchoredPosition != anchoredPos) rect.anchoredPosition = anchoredPos;
    }
}

public class VRHudTint : MonoBehaviour
{
    const float Distance = 0.4f;   // just in front of the eyes (camera near clip is ~0.05 in VR)
    Material material;
    Renderer quad;
    Camera attached;

    internal void Build()
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(q.GetComponent<Collider>());
        q.name = "Tint quad";
        q.transform.SetParent(transform, false);
        q.transform.localScale = new Vector3(3f, 3f, 1f);  // wider than any headset's view at 0.4 m
        quad = q.GetComponent<Renderer>();
        Shader shader = Shader.Find("Sprites/Default");     // transparent, vertex colour, works in URP
        if (shader != null)
        {
            material = new Material(shader) { renderQueue = 4000 };  // drawn after the world
            quad.sharedMaterial = material;
        }
        quad.shadowCastingMode = ShadowCastingMode.Off;
        quad.receiveShadows = false;
        SetColor(Color.clear);
        VRHud.Follow(transform, new Vector3(0f, 0f, Distance), ref attached);
    }

    void LateUpdate() => VRHud.Follow(transform, new Vector3(0f, 0f, Distance), ref attached);

    /// <summary>alpha = strength; 0 hides the quad.</summary>
    public void SetColor(Color c)
    {
        if (material != null) material.color = c;
        if (quad != null) quad.enabled = c.a > 0.001f;
    }

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }
}

public class VRWorldTag : MonoBehaviour
{
    const int FontSize = 36;
    const float LinePixels = 46f;   // one line of text, in canvas units

    [Tooltip("Height of one line of text (m), when Angular Size is 0")]
    public float lineHeight = 0.025f;
    [Tooltip("> 0: keep one line this many degrees tall whatever the distance (far-away markers)")]
    public float angularSize = 0f;

    Canvas canvas;
    Image background;
    Text text;
    RectTransform rect;

    internal void Build(int sortingOrder)
    {
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = sortingOrder;
        rect = (RectTransform)transform;
        rect.pivot = new Vector2(0.5f, 0f);  // the position is the tag's bottom middle

        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(transform, false);
        var bgRect = (RectTransform)bg.transform;
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = bgRect.offsetMax = Vector2.zero;
        background = bg.GetComponent<Image>();
        background.raycastTarget = false;

        var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(transform, false);
        var tr = (RectTransform)go.transform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = new Vector2(10f, 4f);
        tr.offsetMax = new Vector2(-10f, -4f);
        text = go.GetComponent<Text>();
        text.font = VRHud.BuiltinFont();
        text.fontSize = FontSize;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.supportRichText = true;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        Set("", new Color(0f, 0f, 0f, 0.7f));
    }

    public void Set(string label, Color backgroundColor)
    {
        if (background != null) background.color = backgroundColor;
        if (text == null || text.text == label) return;
        text.text = label;
        rect.sizeDelta = new Vector2(Mathf.Max(text.preferredWidth + 20f, LinePixels),
                                     Mathf.Max(text.preferredHeight + 8f, LinePixels));
    }

    public void SetVisible(bool visible)
    {
        if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
    }

    void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 d = transform.position - cam.transform.position;
        float dist = d.magnitude;
        if (dist < 1e-3f) return;
        transform.rotation = Quaternion.LookRotation(d / dist, Vector3.up);  // face you, upright
        float line = angularSize > 0f ? 2f * dist * Mathf.Tan(angularSize * 0.5f * Mathf.Deg2Rad) : lineHeight;
        transform.localScale = Vector3.one * (line / LinePixels);
    }
}
