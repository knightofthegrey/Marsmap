using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// HUD pieces that float in front of the eyes in a headset. IMGUI (OnGUI) and Screen Space
/// canvases are not drawn inside a headset, so VR needs world-space versions that follow
/// the camera. Both kinds re-attach themselves if Camera.main changes and never throw when
/// there is no camera yet.
/// </summary>
public static class VRHud
{
    public const float LabelDistance = 1.2f;   // metres in front of the eyes
    const float PixelsPerMetre = 1000f;        // canvas units per metre (sizes below are in "pixels")

    /// <summary>A text panel. viewOffset = metres right/up of the view centre at 1.2 m,
    /// e.g. (-0.35, -0.28) = lower-left.</summary>
    public static VRHudLabel CreateLabel(string name, Vector2 viewOffset, float widthMeters = 0.8f)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var label = go.AddComponent<VRHudLabel>();
        label.Build(viewOffset, widthMeters);
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

    internal static float CanvasScale => 1f / PixelsPerMetre;
    internal static float Pixels(float metres) => metres * PixelsPerMetre;
}

public class VRHudLabel : MonoBehaviour
{
    Vector2 offset;
    Text title, detail;
    Canvas canvas;
    Camera attached;

    internal void Build(Vector2 viewOffset, float widthMeters)
    {
        offset = viewOffset;
        canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 100;
        var rt = (RectTransform)transform;
        float w = VRHud.Pixels(widthMeters), h = VRHud.Pixels(0.2f);
        rt.sizeDelta = new Vector2(w, h);
        rt.localScale = Vector3.one * VRHud.CanvasScale;

        var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bg.transform.SetParent(transform, false);
        Stretch((RectTransform)bg.transform, 0f);
        bg.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);

        title = MakeText("Title", 40, FontStyle.Bold, Color.white, new Vector2(0f, 0.55f), new Vector2(1f, 1f));
        detail = MakeText("Detail", 26, FontStyle.Normal, new Color(0.85f, 0.85f, 0.85f),
                          new Vector2(0f, 0f), new Vector2(1f, 0.55f));
        Follow();
    }

    Text MakeText(string name, int size, FontStyle style, Color color, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(transform, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = new Vector2(16f, 4f);
        rt.offsetMax = new Vector2(-16f, -4f);
        var t = go.GetComponent<Text>();
        t.font = VRHud.BuiltinFont();
        t.fontSize = size;
        t.fontStyle = style;
        t.color = color;
        t.alignment = TextAnchor.MiddleLeft;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Truncate;
        t.resizeTextForBestFit = true;  // long credits shrink instead of being cut off
        t.resizeTextMinSize = 14;
        t.resizeTextMaxSize = size;
        return t;
    }

    static void Stretch(RectTransform rt, float pad)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
    }

    void Follow() => VRHud.Follow(transform, new Vector3(offset.x, offset.y, VRHud.LabelDistance), ref attached);

    void LateUpdate() => Follow();

    public void Set(string titleText, string detailText)
    {
        if (title != null) title.text = titleText ?? "";
        if (detail != null) detail.text = detailText ?? "";
    }

    public void SetVisible(bool visible)
    {
        if (canvas != null) canvas.enabled = visible;
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
            material = new Material(shader) { renderQueue = 4000 };  // drawn after everything else
            quad.sharedMaterial = material;
        }
        quad.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
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
