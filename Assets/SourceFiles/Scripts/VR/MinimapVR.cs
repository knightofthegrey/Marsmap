using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The minimap in a headset, laid out like the desktop one (Minimap.cs builds the images) in the
/// bottom-left corner of the head-locked HUD sheet (VRHud.cs):
///
///    [legend: hazard colours]
///    ( round map )  [upcoming hills]
///
/// The map is round and heading-up (the way you look is up), with north on its rim and the scale
/// in its bottom-left corner; the "upcoming hills" box sits to its right along the bottom edge.
/// Right stick up / down zooms (snap turn keeps left / right). Only shown in a headset, and
/// hidden while the EVA map is open. TerrainOverlay's layer label sits above it (VRStackHeight).
/// </summary>
public partial class Minimap
{
    // Layout in HUD sheet units (800 x 450, 1 unit ≈ 1.75 mm at 1.5 m), from the bottom-left corner
    const float VRMargin = 10f;          // from the left and bottom edges, like the desktop margin
    const float VRMapSize = 120f;        // the round map, across
    const float VRLegendGap = 2f;        // between the map and the legend above it
    const float VRLegendHeight = 16f;
    const float VRProfileGap = 8f;       // between the map and the hill profile box
    const float VRProfileWidth = 160f, VRProfileHeight = 74f;
    const float VRCaptionHeight = 27f;   // the profile's caption: two lines of small text

    /// <summary>Sheet units from the bottom edge of the VR HUD to the top of the minimap's legend,
    /// so the layer label can sit just above it (like BottomLeftHeight on desktop); 0 while the
    /// VR minimap isn't shown.</summary>
    public static float VRStackHeight { get; private set; }

    GameObject vrRoot;
    RectTransform vrTurn, vrMapRect, vrNorth, vrProfileBox;
    RawImage vrMap, vrProfile;
    Text vrScale, vrProfileText, vrTerrainLegend;
    GameObject vrHazardLegend;
    Texture2D vrDisc;
    float vrScaleRadius = -1f;   // builtRadius the scale text was made for
    string vrCaptionFrom;        // profileText the caption was made from
    bool vrZoomArmed = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetVRStatics() => VRStackHeight = 0f; // fresh on every Play, even without a domain reload

    void LateUpdate()
    {
        bool mapOpen = MapView.AnyOpen || mapView != null && mapView.IsOpen;
        bool show = MarsmapInput.XRActive && mapTex != null && player != null && !mapOpen;
        VRStackHeight = show ? VRMargin + VRMapSize + VRLegendGap + VRLegendHeight : 0f;
        if (!show)
        {
            if (vrRoot != null && vrRoot.activeSelf) vrRoot.SetActive(false);
            return;
        }
        if (vrRoot == null) BuildVR();
        if (!vrRoot.activeSelf) vrRoot.SetActive(true);

        // Same as the desktop drawing: shift by how far you've walked since the image was made,
        // then turn it so the way you face is up (UI turns counter-clockwise: north goes left when you face east)
        float heading = Heading();
        vrTurn.localEulerAngles = new Vector3(0f, 0f, heading);
        float unitsPerMetre = VRMapSize / (2f * builtRadius);
        vrMapRect.anchoredPosition = new Vector2(builtCentre.x - player.position.x, builtCentre.y - player.position.z) * unitsPerMetre;
        if (vrMap.texture != mapTex) vrMap.texture = mapTex;
        float a = heading * Mathf.Deg2Rad, nr = VRMapSize * 0.44f;
        vrNorth.anchoredPosition = new Vector2(-nr * Mathf.Sin(a), nr * Mathf.Cos(a));

        // Scale: metres from you to the edge of the map (only changes when you zoom)
        if (vrScaleRadius != builtRadius)
        {
            vrScaleRadius = builtRadius;
            vrScale.text = FormatDistance(builtRadius);
        }

        // Legend: hazard zones, or the plain terrain colours (H on a keyboard switches)
        if (vrHazardLegend.activeSelf != showHazards)
        {
            vrHazardLegend.SetActive(showHazards);
            vrTerrainLegend.gameObject.SetActive(!showHazards);
        }

        bool profile = profileTex != null && !string.IsNullOrEmpty(profileText);
        if (vrProfileBox.gameObject.activeSelf != profile) vrProfileBox.gameObject.SetActive(profile);
        if (profile)
        {
            if (vrProfile.texture != profileTex) vrProfile.texture = profileTex;
            if (!ReferenceEquals(vrCaptionFrom, profileText))
            {
                vrCaptionFrom = profileText;
                vrProfileText.text = TwoLines(profileText);
            }
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
        // The map's square, anchored bottom-left on the sheet; everything else is placed from it
        RectTransform block = VRHud.AddPanel("Minimap (VR)", Vector2.zero, new Vector2(VRMargin, VRMargin),
                                             new Vector2(VRMapSize, VRMapSize));
        vrRoot = block.gameObject;
        Vector2 centre = new Vector2(0.5f, 0.5f);

        // Dark disc behind the map, so it reads against bright ground
        RawImage disc = AddRawImage(block, "Backdrop");
        Place(disc.rectTransform, centre, Vector2.zero, Vector2.one * (VRMapSize + 6f));
        if (vrDisc != null) Destroy(vrDisc); // from an earlier build whose sheet went away
        disc.texture = vrDisc = Disc(64);
        disc.color = new Color(0f, 0f, 0f, 0.55f);

        // Turned to your heading every frame; the map image inside it is shifted by how far you've walked
        vrTurn = Place(VRHud.NewRect("Turn", block), centre, Vector2.zero, Vector2.one * VRMapSize);
        vrMap = AddRawImage(vrTurn, "Map");
        vrMapRect = Place(vrMap.rectTransform, centre, Vector2.zero, Vector2.one * VRMapSize);

        // You, in the middle, pointing up
        Text you = VRHud.AddText(block, "You", 14, FontStyle.Bold, TextAnchor.MiddleCenter);
        Place(you.rectTransform, centre, Vector2.zero, new Vector2(20f, 20f));
        you.text = "▲";
        you.color = NextColor;

        // North, on the rim (moved every frame)
        Image northBg = VRHud.AddImage(block, "North", new Color(0f, 0f, 0f, 0.7f));
        vrNorth = Place(northBg.rectTransform, centre, Vector2.zero, new Vector2(14f, 14f));
        Text n = VRHud.AddText(vrNorth, "N", VRHud.SmallFont, FontStyle.Bold, TextAnchor.MiddleCenter);
        VRHud.Stretch(n.rectTransform, Vector2.zero, Vector2.zero);
        n.horizontalOverflow = HorizontalWrapMode.Overflow;
        n.text = "N";

        // Scale in the map's bottom-left corner, on a dark box as wide as the text
        Image scaleBg = VRHud.AddImage(block, "Scale", new Color(0f, 0f, 0f, 0.6f));
        FitRow(Place(scaleBg.rectTransform, Vector2.zero, new Vector2(2f, 2f), new Vector2(40f, 14f)), 4, 0f);
        vrScale = RowText(scaleBg.transform, "Text", "");
        vrScaleRadius = -1f;

        // Legend just above the map: a swatch and name per hazard zone, or the terrain colours
        Image legendBg = VRHud.AddImage(block, "Legend", new Color(0f, 0f, 0f, 0.6f));
        RectTransform legend = Place(legendBg.rectTransform, Vector2.zero, new Vector2(0f, VRMapSize + VRLegendGap),
                                     new Vector2(VRMapSize, VRLegendHeight));
        FitRow(legend, 5, 0f);
        RectTransform zones = VRHud.NewRect("Hazard zones", legend);
        Row(zones, 0, 7f);
        string[] names = { "0-10°", "10-20°", "20-30°", ">30°" };
        for (int t = 0; t < names.Length; t++)
        {
            RectTransform zone = VRHud.NewRect(names[t], zones);
            Row(zone, 0, 3f);
            Image swatch = VRHud.AddImage(zone, "Colour", TierColors[t]);
            var swatchSize = swatch.gameObject.AddComponent<LayoutElement>();
            swatchSize.minWidth = swatchSize.preferredWidth = 8f;
            swatchSize.minHeight = swatchSize.preferredHeight = 8f;
            RowText(zone, "Name", names[t]);
        }
        vrHazardLegend = zones.gameObject;
        vrTerrainLegend = RowText(legend, "Terrain colours", "Terrain: dark low, pale high");
        vrHazardLegend.SetActive(showHazards);
        vrTerrainLegend.gameObject.SetActive(!showHazards);

        // Upcoming hills, to the right of the map along the bottom edge (as on desktop):
        // the caption on top, the profile image under it
        Image box = VRHud.AddImage(block, "Upcoming hills", VRHud.PanelColor);
        vrProfileBox = Place(box.rectTransform, Vector2.zero, new Vector2(VRMapSize + VRProfileGap, 0f),
                             new Vector2(VRProfileWidth, VRProfileHeight));
        vrProfileText = VRHud.AddText(vrProfileBox, "Caption", VRHud.SmallFont);
        vrProfileText.horizontalOverflow = HorizontalWrapMode.Overflow; // two set lines, never wrapped
        RectTransform caption = vrProfileText.rectTransform;
        caption.anchorMin = new Vector2(0f, 1f);
        caption.anchorMax = Vector2.one;
        caption.pivot = new Vector2(0.5f, 1f);
        caption.offsetMin = new Vector2(5f, -2f - VRCaptionHeight);
        caption.offsetMax = new Vector2(-5f, -2f);
        vrProfile = AddRawImage(vrProfileBox, "Profile");
        VRHud.Stretch(vrProfile.rectTransform, new Vector2(4f, 4f), new Vector2(-4f, -3f - VRCaptionHeight));
        vrCaptionFrom = null;
        box.gameObject.SetActive(false); // shown once there's a profile

        vrRoot.SetActive(false);
    }

    void DestroyVR()
    {
        VRStackHeight = 0f;
        if (vrRoot != null) Destroy(vrRoot);
        if (vrDisc != null) Destroy(vrDisc);
        vrRoot = null;
        vrDisc = null;
    }

    // The desktop caption is one long line ("ROUTE AHEAD 2 km   +40 m / -12 m   steepest 9°");
    // the box here is narrower, so the climb / descent / steepest numbers go on a second line
    static string TwoLines(string s)
    {
        int i = s.IndexOf("   ", System.StringComparison.Ordinal);
        return i < 0 ? s : s.Substring(0, i) + "\n" + s.Substring(i + 3).Replace("   ", "  ");
    }

    // Anchor, pivot, position and size of a piece on the sheet, all from the same corner / centre
    static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    // A RawImage (for the map and profile textures), drawn on top like the rest of the sheet
    static RawImage AddRawImage(Transform parent, string name)
    {
        RawImage img = VRHud.NewRect(name, parent).gameObject.AddComponent<RawImage>();
        img.raycastTarget = false;
        VRHud.OnTop(img);
        return img;
    }

    // Lays its children out left to right, vertically centred, each as wide as it needs
    static void Row(RectTransform row, int padX, float spacing)
    {
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(padX, padX, 0, 0);
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
    }

    // A Row that is also as wide as its children (it grows away from its pivot); its height stays as set
    static void FitRow(RectTransform row, int padX, float spacing)
    {
        Row(row, padX, spacing);
        var fit = row.gameObject.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    // One line of small text in a Row (never wrapped)
    static Text RowText(Transform row, string name, string text)
    {
        Text t = VRHud.AddText(row, name, VRHud.SmallFont, FontStyle.Normal, TextAnchor.MiddleLeft);
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.text = text;
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
