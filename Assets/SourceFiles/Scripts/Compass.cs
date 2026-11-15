using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Turns the compass dial with the player's heading.
///
/// With "Style Panel" on (default) the compass rose is placed in the top-left corner and made
/// easier to read on Mars: its lines are redrawn in a light sand colour with a soft shadow behind
/// them (no background, it stands free). Two arrows ride round it (PointAt): red = the next
/// waypoint, green = Home Base (MapView sets them).
/// Off: the dial stays exactly as placed in the scene.
/// </summary>
public class Compass : MonoBehaviour
{
    public Transform playerTransform;
    public Transform compassDial;

    [Header("Look")]
    [Tooltip("Place and restyle the compass rose in the top-left corner at startup")]
    public bool stylePanel = true;
    [Tooltip("Width of the compass rose, in Canvas reference pixels (800x600)")]
    public float size = 120f;
    [Tooltip("Gap from the top-left corner (Canvas reference pixels)")]
    public Vector2 margin = new Vector2(22f, 22f);
    [Tooltip("Colour of the compass lines")]
    public Color lineColor = new Color(0.97f, 0.92f, 0.82f, 1f);

    RectTransform shadow;

    void Start()
    {
        if (stylePanel && compassDial is RectTransform dial) Build(dial);
    }

    void Update()
    {
        if (playerTransform == null || compassDial == null) return;

        float headingAngle = playerTransform.eulerAngles.y;
        float dialZ = headingAngle;

        compassDial.localEulerAngles = new Vector3(0f, 0f, dialZ);

        if (shadow != null) shadow.localEulerAngles = compassDial.localEulerAngles; // turns with the rose
    }

    // ---------------------------------------------------------------
    // Free-standing compass rose, light lines + soft shadow
    // ---------------------------------------------------------------
    void Build(RectTransform dial)
    {
        Canvas canvas = dial.GetComponentInParent<Canvas>();
        if (canvas == null) return;
        canvas = canvas.rootCanvas;

        // Holder in the top-left corner (Canvas reference units, so it scales with the screen)
        var root = NewRect("Compass HUD", canvas.transform);
        root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
        root.sizeDelta = new Vector2(size, size);
        root.anchoredPosition = new Vector2(margin.x + size * 0.5f, -(margin.y + size * 0.5f));
        root.SetAsFirstSibling(); // behind the arrows, which ride round it

        Image rose = dial.GetComponent<Image>();
        if (rose != null && rose.sprite != null)
        {
            Texture2D shadowTex = null;
            Texture2D light = Restyle(rose.sprite, 512, lineColor, out shadowTex);
            if (light != null)
            {
                // Shadow first, so it's drawn behind the rose
                var sh = NewRect("Compass shadow", root);
                sh.sizeDelta = new Vector2(size * 1.03f, size * 1.03f);
                var shImg = sh.gameObject.AddComponent<Image>();
                shImg.sprite = ToSprite(shadowTex);
                shImg.raycastTarget = false;
                shadow = sh;
                rose.sprite = ToSprite(light);
                rose.color = Color.white;
            }
        }

        // The scene's dial (the rose) moves into the corner and keeps turning with your heading
        dial.SetParent(root, false);
        dial.anchorMin = dial.anchorMax = dial.pivot = new Vector2(0.5f, 0.5f);
        dial.anchoredPosition = Vector2.zero;
        dial.localScale = Vector3.one;
        dial.sizeDelta = new Vector2(size, size);

        // Only two arrows: red (next waypoint) and green (Home Base). Hide any other.
        foreach (Image img in FindObjectsByType<Image>(FindObjectsInactive.Exclude))
        {
            string tex = img.sprite != null && img.sprite.texture != null ? img.sprite.texture.name : "";
            if (tex.StartsWith("arrow") && !tex.Contains("red") && !tex.Contains("green"))
                img.gameObject.SetActive(false);
        }
    }

    // A light copy of the rose (its shape, in lineColor) and a soft dark shadow of it.
    // The image can't be read directly (not "Read/Write" in its import settings), so it's
    // copied through the GPU first.
    static Texture2D Restyle(Sprite sprite, int n, Color line, out Texture2D shadow)
    {
        shadow = null;
        Texture src = sprite.texture;
        Rect r = sprite.textureRect;
        RenderTexture rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(src, rt, new Vector2(r.width / src.width, r.height / src.height),
                      new Vector2(r.x / src.width, r.y / src.height));
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        var read = new Texture2D(n, n, TextureFormat.RGBA32, false);
        read.ReadPixels(new Rect(0, 0, n, n), 0, 0);
        read.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        Color[] px = read.GetPixels();
        Destroy(read);
        var alpha = new float[n * n];
        for (int i = 0; i < px.Length; i++) alpha[i] = px[i].a;

        var lightPx = new Color[n * n];
        for (int i = 0; i < lightPx.Length; i++) lightPx[i] = new Color(line.r, line.g, line.b, alpha[i] * line.a);

        // Shadow: the same shape, blurred, dark and see-through
        float[] soft = Blur(Blur(alpha, n, 4), n, 4);
        var shadowPx = new Color[n * n];
        for (int i = 0; i < shadowPx.Length; i++) shadowPx[i] = new Color(0f, 0f, 0f, Mathf.Clamp01(soft[i] * 1.6f) * 0.75f);

        shadow = MakeTexture(n, shadowPx);
        return MakeTexture(n, lightPx);
    }

    // Box blur (radius r pixels) in both directions
    static float[] Blur(float[] a, int n, int r)
    {
        var tmp = new float[n * n];
        var outA = new float[n * n];
        float w = 1f / (2 * r + 1);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float s = 0f;
                for (int k = -r; k <= r; k++) s += a[y * n + Mathf.Clamp(x + k, 0, n - 1)];
                tmp[y * n + x] = s * w;
            }
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float s = 0f;
                for (int k = -r; k <= r; k++) s += tmp[Mathf.Clamp(y + k, 0, n - 1) * n + x];
                outA[y * n + x] = s * w;
            }
        return outA;
    }

    static Texture2D MakeTexture(int n, Color[] px)
    {
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels(px);
        tex.Apply(true, true);
        return tex;
    }

    static Sprite ToSprite(Texture2D tex) =>
        Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        return rt;
    }
}
