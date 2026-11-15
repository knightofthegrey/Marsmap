using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Navigation readout: heading, pitch, and where you are on Mars (latitude, longitude,
/// elevation of the ground under you).
///
/// Latitude / longitude come from the map Python sent (its centre is world x = z = 0, +Z is
/// north, 1 unit = 1 m); elevation is the real height of the ground from the same data. On the
/// random Perlin fallback there is no real place, so those lines show a dash.
///
/// With "Style Panel" on, the panel lays itself out at startup (top-right, dark, readable), so
/// the look doesn't depend on hand-tuned scene values.
/// </summary>
public class GetState : MonoBehaviour
{
    [Header("References")]
    [Tooltip("What the heading/pitch/position are read from (the active camera or the player)")]
    public Transform targetTransform;
    public TextMeshProUGUI angleText;
    [Tooltip("Optional second text: gets just the latitude / longitude line")]
    public TMP_Text coordinates;

    [Header("Look")]
    [Tooltip("Lay out this panel at startup: top-right corner, dark background, larger text")]
    public bool stylePanel = true;
    public Vector2 panelSize = new Vector2(190f, 112f);   // in Canvas reference pixels (800x600)
    public Vector2 panelMargin = new Vector2(10f, 10f);
    public Color panelColor = new Color(0.06f, 0.035f, 0.025f, 0.72f);
    public float fontSize = 12f;

    const string LabelColor = "#C9A68A";  // dusty orange labels, white values
    const string TitleColor = "#F0915A";

    Terraingen terraingen;
    bool searched;

    void Start()
    {
        if (stylePanel && angleText != null && transform is RectTransform) StylePanel();
    }

    void StylePanel()
    {
        // The panel: top-right corner (the compass is top-left, map labels bottom-left)
        var rt = (RectTransform)transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-panelMargin.x, -panelMargin.y);
        rt.sizeDelta = panelSize;
        if (TryGetComponent(out Image bg))
        {
            bg.color = panelColor;
            if (bg.sprite != null) bg.type = Image.Type.Sliced; // keeps the rounded corners
        }

        // The text fills the panel with some padding
        var tr = angleText.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.pivot = new Vector2(0.5f, 0.5f);
        tr.offsetMin = new Vector2(10f, 6f);
        tr.offsetMax = new Vector2(-10f, -6f);
        angleText.fontSize = fontSize;
        angleText.enableAutoSizing = false;
        angleText.alignment = TextAlignmentOptions.TopLeft;
        angleText.textWrappingMode = TextWrappingModes.NoWrap;
        angleText.richText = true;
        angleText.color = Color.white;
        angleText.lineSpacing = 4f;
    }

    void Update()
    {
        if (targetTransform == null || angleText == null) return;

        float heading = Mathf.Repeat(targetTransform.eulerAngles.y, 360f);
        int headingWhole = Mathf.RoundToInt(heading) % 360; // 359.7 shows as 000, not 360
        float rawPitch = targetTransform.eulerAngles.x;
        float pitchUp = -((rawPitch > 180f) ? rawPitch - 360f : rawPitch); // looking up = +
        Vector3 pos = targetTransform.position;

        string lat = "-", lon = "-", elev = "-";
        Terraingen tg = FindTerraingen();
        if (tg != null)
        {
            if (tg.TryGetLatLon(pos, out double la, out double lo))
            {
                lat = $"{System.Math.Abs(la):F5}° {(la >= 0 ? "N" : "S")}";
                lon = $"{lo:F5}° E";
            }
            if (tg.TryGetElevation(pos, out float m))
                elev = $"{m:N0} m";
        }

        angleText.text =
            $"<color={TitleColor}><size=85%><b>NAVIGATION</b></size></color>\n" +
            Row("Heading", $"{headingWhole:000}° {Cardinal(headingWhole)}") +
            Row("Pitch", $"{pitchUp:+0;-0;0}°") +
            Row("Lat", lat) +
            Row("Lon", lon) +
            Row("Elevation", elev, last: true);

        if (coordinates != null) coordinates.text = lat == "-" ? "" : $"{lat}  {lon}";
    }

    static string Row(string label, string value, bool last = false) =>
        $"<color={LabelColor}>{label}</color><pos=45%>{value}" + (last ? "" : "\n");

    static string Cardinal(float degrees)
    {
        string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        return names[Mathf.RoundToInt(degrees / 45f) % 8];
    }

    Terraingen FindTerraingen()
    {
        if (terraingen == null && !searched)
        {
            searched = true;
            terraingen = FindAnyObjectByType<Terraingen>();
        }
        return terraingen;
    }
}
