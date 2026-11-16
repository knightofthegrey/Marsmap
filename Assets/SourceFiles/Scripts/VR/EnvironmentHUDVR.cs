using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The environment alerts and panel in a headset. A headset doesn't draw IMGUI (OnGUI), so
/// there you would hear the alert beeps but see nothing. These are world-space versions on the
/// HUD sheet (VRHud.Sheet), in the same places as on desktop:
///
///  - Alerts (top centre, between the compass and the navigation panel): the same cards in the
///    same order (most severe first, then banners), but compact: at most 3 alert cards and the
///    2 newest banners, and never lower than 70 units above the view centre (that's where you
///    look while walking). When they don't all fit, the most severe alert always shows in full,
///    then the banners (they're only up for a few seconds), then the other alerts, which shrink
///    to their title line before they're left out. "+N more" says how many aren't shown (when
///    there's room for that line).
///  - Environment panel (right, under the navigation panel): the same rows as on desktop, in two
///    columns (names left, readings right). Hidden while the EVA map is open.
///
/// Banners come and go on the same clock as on desktop, and the end of the EVA clears the
/// alerts here too (both read the same state). Only used while a headset is running
/// (MarsmapInput.XRActive); nothing is created on desktop.
/// </summary>
public partial class EnvironmentHUD
{
    // ---- layout on the HUD sheet, in sheet units (see VRHud)
    const float VRAlertWidth = 320f;    // x -160..160: clear of the compass (left) and nav panel (from x 200)
    const float VRAlertTop = 8f;        // gap between the sheet's top edge and the first card
    const float VRAlertLowest = 70f;    // the stack never reaches lower than this above the view centre
    const float VRAlertGap = 4f;        // between cards
    const float VRStripe = 4f;          // the coloured stripe on a card's left edge
    const int VRMaxAlertCards = 3, VRMaxBanners = 2;
    const int CardPadLeft = 12, CardPadRight = 8, CardPadTop = 4, CardPadBottom = 5;
    const float VRPanelGap = 6f;        // between the navigation panel and the environment panel
    const float VRPanelMinWidth = 200f;
    const int VRPanelPad = 8;
    static readonly Vector2 VRPanelFallback = new Vector2(-10f, -128f);  // right under where the nav panel sits

    static readonly Color CardColor = new Color(0.05f, 0.03f, 0.02f, 0.86f);
    static readonly Color TagColor = new Color(0.75f, 0.7f, 0.65f, 0.85f);
    static readonly Color VRLabelColor = new Color32(0xC9, 0xA6, 0x8A, 0xFF);   // #C9A68A, as on desktop
    static readonly Color VRTitleColor = new Color32(0xF0, 0x91, 0x5A, 0xFF);   // #F0915A
    static readonly Color VRSimulatedColor = new Color32(0x7F, 0xB2, 0xFF, 0xFF);
    static readonly Color VRSourceColor = new Color32(0x9A, 0x8A, 0x7A, 0xFF);

    // Panel rows (the UV and dust rows only when the telemetry has UV, as on desktop)
    const string RowsBasic = "EVA clock\nRadiation\nEVA dose\nPressure\nAir temp";
    const string RowsUV = "EVA clock\nRadiation\nEVA dose\nUV index\nPressure\nAir temp";
    const string RowsUVDust = "EVA clock\nRadiation\nEVA dose\nUV index\nDust\nPressure\nAir temp";

    // How much of a card is shown
    const int Hidden = 0, TitleOnly = 1, Full = 2;

    /// <summary>One card of the alert stack (an alert or a banner). Made once and reused.</summary>
    class VRCard
    {
        public GameObject go;
        public Image glow, stripe;
        public Text title, tag, body, arrival;
        public LayoutElement tagLayout, bodyLayout;
        // What the texts say now (only assigned when they change)
        public string titleShown = "", tagShown = "", bodyShown = "", arrivalShown = "";
        public int arrivalKey = -1;      // dust arrival: minutes left, 0 = arriving now, -1 = none
        // Heights in sheet units, measured whenever the text changes
        public bool measured;
        public bool tagInline;           // the source tag fits on the title line
        public float fullHeight, titleHeight, bodyHeight;
        public float lineHeight;         // one line of body text
        public float bodyCap = -1f;      // >= 0: the body is cut to this height (see LayOutVRAlerts)
    }

    // ---- alert stack
    RectTransform vrAlerts;
    readonly VRCard[] vrCards = new VRCard[VRMaxAlertCards + VRMaxBanners];  // alerts first, then banners
    readonly int[] vrPriority = new int[VRMaxAlertCards + VRMaxBanners];
    readonly int[] vrState = new int[VRMaxAlertCards + VRMaxBanners];
    readonly List<ActiveAlert> vrSorted = new List<ActiveAlert>();
    GameObject vrMore;
    Text vrMoreText;
    float vrMoreHeight;
    int vrMoreShown = -1;
    int vrAlertCount = -1, vrBannerCount = -1, vrExtra = -1;  // what the stack was last laid out for
    float vrFreshUntil = float.PositiveInfinity;  // when an alert that just beeped stops counting as new

    // ---- environment panel
    RectTransform vrPanel;
    Text vrPanelLabels, vrPanelValues, vrPanelSimulated, vrPanelSource;
    TelemetryMessage vrPanelFor;         // the telemetry the panel shows
    int vrPanelLevels = -1;              // and the alert levels it was coloured with
    float vrPanelPlaceAt, vrLabelsWidth;
    string vrLabelsShown, vrScenarioShown, vrSourceShown;
    readonly StringBuilder vrValues = new StringBuilder(512);
    float vrTextScale;                   // see SheetTextScale

    void LateUpdate()
    {
        if (!MarsmapInput.XRActive)
        {
            HideVR();  // only does something if a headset was running before
            return;
        }
        // OnGUI does this on desktop; it returns early in a headset
        for (int i = banners.Count - 1; i >= 0; i--)
            if (Time.unscaledTime > banners[i].until) banners.RemoveAt(i);

        UpdateVRAlerts();
        UpdateVRPanel();
    }

    void HideVR()
    {
        if (vrAlerts != null) Show(vrAlerts.gameObject, false);
        if (vrPanel != null) Show(vrPanel.gameObject, false);
    }

    void DestroyVR()
    {
        if (vrAlerts != null) Destroy(vrAlerts.gameObject);
        if (vrPanel != null) Destroy(vrPanel.gameObject);
        vrAlerts = null;
        vrPanel = null;
        for (int i = 0; i < vrCards.Length; i++) vrCards[i] = null;
        vrMore = null;
        vrTextScale = 0f;
    }

    // ---------------------------------------------------------------
    // Alerts and banners (top centre)
    // ---------------------------------------------------------------
    void UpdateVRAlerts()
    {
        if (active.Count == 0 && banners.Count == 0)
        {
            if (vrAlerts != null) Show(vrAlerts.gameObject, false);
            return;
        }
        if (vrAlerts == null) BuildVRAlerts();  // also if the sheet was destroyed with it
        Show(vrAlerts.gameObject, true);

        // Most severe first, like OnGUI (a stable insertion sort: never more than three)
        vrSorted.Clear();
        foreach (ActiveAlert a in active.Values) vrSorted.Add(a);
        for (int i = 1; i < vrSorted.Count; i++)
        {
            ActiveAlert a = vrSorted[i];
            int j = i - 1;
            for (; j >= 0 && Rank(vrSorted[j].msg.level) < Rank(a.msg.level); j--) vrSorted[j + 1] = vrSorted[j];
            vrSorted[j + 1] = a;
        }

        int nAlerts = Mathf.Min(vrSorted.Count, VRMaxAlertCards);
        int nBanners = Mathf.Min(banners.Count, VRMaxBanners);
        int extra = vrSorted.Count - nAlerts + banners.Count - nBanners;  // left out whatever the room
        float now = Time.unscaledTime;

        // Alert cards: same colours, flash and pulse as DrawAlert
        for (int i = 0; i < nAlerts; i++)
        {
            ActiveAlert a = vrSorted[i];
            VRCard c = vrCards[i];
            SetCardText(c, a.msg.title, SourceTag(a.msg.category), a.msg.text, ArrivalKey(a));
            bool danger = a.msg.level == "danger";
            float pulse = danger ? 0.5f + 0.5f * Mathf.Sin(now * 6f) : 1f;
            float fresh = Mathf.Clamp01(1f - (now - a.shownAt) / 2f); // flash when new
            float glow = Mathf.Max(fresh, danger ? 0.35f * pulse : 0f);
            SetCardColors(c, LevelColor(a.msg.level), danger ? Mathf.Lerp(0.35f, 1f, pulse) : 1f, glow);
        }
        // Banners: the newest ones, oldest of them on top (as on desktop)
        int firstBanner = banners.Count - nBanners;
        for (int i = 0; i < nBanners; i++)
        {
            Banner b = banners[firstBanner + i];
            VRCard c = vrCards[VRMaxAlertCards + i];
            SetCardText(c, b.title, null, b.text, -1);
            SetCardColors(c, b.color, 1f, 0f);
        }

        // Measure what changed; lay the stack out again only then
        bool changed = nAlerts != vrAlertCount || nBanners != vrBannerCount || extra != vrExtra
                       || now >= vrFreshUntil;  // an alert that just beeped is no longer new
        for (int i = 0; i < vrCards.Length; i++)
        {
            bool inUse = i < VRMaxAlertCards ? i < nAlerts : i - VRMaxAlertCards < nBanners;
            if (inUse && !vrCards[i].measured)
            {
                MeasureVRCard(vrCards[i]);
                changed = true;
            }
        }
        if (changed) LayOutVRAlerts(nAlerts, nBanners, extra);
    }

    // The dust arrival countdown as a number, so its line is only rebuilt when it changes
    int ArrivalKey(ActiveAlert a)
    {
        if (a.arrivalEvaS <= 0f || telemetry == null) return -1;
        float left = (a.arrivalEvaS - telemetry.t_eva_s) / 60f;
        return left >= 1f ? Mathf.RoundToInt(left) : 0;
    }

    void SetCardText(VRCard c, string title, string tag, string body, int arrivalKey)
    {
        title = title ?? "";
        tag = tag ?? "";
        body = body ?? "";
        if (c.titleShown != title) { c.titleShown = title; c.title.text = title; c.measured = false; }
        if (c.tagShown != tag) { c.tagShown = tag; c.tag.text = tag; c.measured = false; }
        if (c.bodyShown != body) { c.bodyShown = body; c.body.text = body; c.measured = false; }
        if (c.arrivalKey != arrivalKey)
        {
            // Same line as DrawAlert's
            c.arrivalKey = arrivalKey;
            c.arrivalShown = arrivalKey < 0 ? ""
                           : arrivalKey >= 1 ? $"<b>Estimated arrival in ~{arrivalKey} min</b> (EVA clock)"
                           : "<b>Arriving now</b>";
            c.arrival.text = c.arrivalShown;
            c.measured = false;
        }
    }

    // Graphic.color only redraws when the colour really changes
    static void SetCardColors(VRCard c, Color color, float stripeAlpha, float glow)
    {
        c.title.color = color;
        c.stripe.color = new Color(color.r, color.g, color.b, stripeAlpha);
        bool glowing = glow > 0f;  // off once a flash is over (a danger pulse never quite reaches 0)
        if (c.glow.enabled != glowing) c.glow.enabled = glowing;
        if (glowing) c.glow.color = new Color(color.r, color.g, color.b, 0.28f * glow);
    }

    // How tall the card is in full and as just its title line, for LayOutVRAlerts. The layout
    // groups size the cards themselves; this only decides what fits.
    void MeasureVRCard(VRCard c)
    {
        c.measured = true;
        float inner = VRAlertWidth - CardPadLeft - CardPadRight;
        float pads = CardPadTop + CardPadBottom;
        c.titleHeight = pads + TextHeight(c.title, c.titleShown, inner);

        // The source tag goes on the title line, at the right (like desktop) when it fits there,
        // otherwise on its own line right under the title
        float tagHeight = 0f;
        c.tagInline = false;
        if (c.tagShown.Length > 0)
        {
            float tagWidth = TextWidth(c.tag, c.tagShown);
            c.tagInline = TextWidth(c.title, c.titleShown) + 10f + tagWidth <= inner;
            c.tagLayout.ignoreLayout = c.tagInline;
            if (c.tagInline)
            {
                RectTransform r = c.tag.rectTransform;
                r.anchorMin = r.anchorMax = r.pivot = Vector2.one;
                r.anchoredPosition = new Vector2(-CardPadRight, -(CardPadTop + 3.5f)); // on the title's baseline
                r.sizeDelta = new Vector2(tagWidth + 4f, TextHeight(c.tag, c.tagShown, tagWidth + 4f));
                c.tag.alignment = TextAnchor.UpperRight;
            }
            else
            {
                c.tag.alignment = TextAnchor.UpperLeft;  // the layout group places it
                tagHeight = TextHeight(c.tag, c.tagShown, inner);
            }
        }
        c.bodyHeight = TextHeight(c.body, c.bodyShown, inner);
        c.lineHeight = TextHeight(c.body, "Ag", inner);
        c.fullHeight = c.titleHeight + tagHeight + c.bodyHeight + TextHeight(c.arrival, c.arrivalShown, inner);
    }

    // Which cards show in full, which only as their title line, and which not at all, so the
    // stack ends at least VRAlertLowest above the view centre. The rules make sure a beep never
    // comes without something new on screen:
    //  1. every alert and banner gets at least its title line: the alerts most severe first,
    //     then the banners newest first. Whatever doesn't fit is counted in a "+N more" line,
    //     which always gets its room (the least important title lines make way for it);
    //  2. then the cards open up in full, most urgent first: alerts that just appeared or got
    //     worse (the ones that just beeped), the most severe alert, the banners, the rest. A card
    //     whose text doesn't all fit shows as many lines as do (at least two; one for the first);
    //  3. while other cards are waiting, the top alert's text is cut to three lines.
    // It runs again when an alert stops being new (bannerSeconds after it beeped).
    void LayOutVRAlerts(int nAlerts, int nBanners, int extra)
    {
        vrAlertCount = nAlerts;
        vrBannerCount = nBanners;
        vrExtra = extra;
        float now = Time.unscaledTime;
        vrFreshUntil = float.PositiveInfinity;
        for (int i = 0; i < vrState.Length; i++)
        {
            vrState[i] = Hidden;
            vrCards[i].bodyCap = -1f;
        }

        // 1. Title lines
        int n = 0;
        for (int i = 0; i < nAlerts; i++) vrPriority[n++] = i;
        for (int i = nBanners - 1; i >= 0; i--) vrPriority[n++] = VRMaxAlertCards + i;
        float room = VRHud.SheetUnitsHigh * 0.5f - VRAlertTop - VRAlertLowest;
        float used = 0f;
        int shown = 0, notShown = extra;
        for (int k = 0; k < n; k++)
        {
            int i = vrPriority[k];
            float add = (shown > 0 ? VRAlertGap : 0f) + vrCards[i].titleHeight;
            if (k == 0 || used + add <= room)
            {
                vrState[i] = TitleOnly;
                used += add;
                shown++;
            }
            else notShown++;
        }
        float moreLine = VRAlertGap + vrMoreHeight;
        for (int k = n - 1; k > 0 && notShown > 0 && used + moreLine > room; k--)
        {
            int i = vrPriority[k];
            if (vrState[i] == Hidden) continue;
            vrState[i] = Hidden;                          // k > 0: it came with a gap
            used -= VRAlertGap + vrCards[i].titleHeight;
            shown--;
            notShown++;
        }
        if (notShown > 0) used += moreLine;

        // 2. Open them up, most urgent first
        n = 0;
        for (int i = 0; i < nAlerts; i++)
            if (now - vrSorted[i].shownAt < bannerSeconds)
            {
                vrPriority[n++] = i;
                vrFreshUntil = Mathf.Min(vrFreshUntil, vrSorted[i].shownAt + bannerSeconds);
            }
        if (nAlerts > 0 && now - vrSorted[0].shownAt >= bannerSeconds) vrPriority[n++] = 0;
        for (int i = nBanners - 1; i >= 0; i--) vrPriority[n++] = VRMaxAlertCards + i;
        for (int i = 1; i < nAlerts; i++)
            if (now - vrSorted[i].shownAt >= bannerSeconds) vrPriority[n++] = i;

        bool othersWaiting = shown + notShown > 1;
        for (int k = 0; k < n; k++)
        {
            int i = vrPriority[k];
            if (vrState[i] != TitleOnly) continue;
            VRCard c = vrCards[i];
            float opens = c.fullHeight - c.titleHeight;   // what showing it in full adds
            float fixedPart = opens - c.bodyHeight;        // its tag line and arrival line
            float bodyRoom = room - used - fixedPart;
            if (i == 0 && othersWaiting) bodyRoom = Mathf.Min(bodyRoom, 3f * c.lineHeight + 1f); // 3.
            if (bodyRoom >= c.bodyHeight - 0.5f)
            {
                vrState[i] = Full;
                used += opens;
            }
            else if (bodyRoom >= (k == 0 ? 1f : 2f) * c.lineHeight)
            {
                vrState[i] = Full;
                c.bodyCap = bodyRoom;
                used += fixedPart + bodyRoom;
            }
        }
        for (int i = 0; i < vrCards.Length; i++) ApplyCardState(vrCards[i], vrState[i]);

        // 1. "+N more": its room was kept above
        bool more = notShown > 0;
        if (more && notShown != vrMoreShown)
        {
            vrMoreShown = notShown;
            vrMoreText.text = $"+{notShown} more";
        }
        Show(vrMore, more);
    }

    static void ApplyCardState(VRCard c, int state)
    {
        Show(c.go, state != Hidden);
        if (state == Hidden) return;
        bool full = state == Full;
        float cap = full ? c.bodyCap : -1f;
        Show(c.tag.gameObject, c.tagShown.Length > 0 && (full || c.tagInline));
        Show(c.body.gameObject, full && c.bodyShown.Length > 0 && !(cap >= 0f && cap < VRHud.SmallFont));
        Show(c.arrival.gameObject, full && c.arrivalShown.Length > 0);
        // Cut the text short only when this card has to be shorter than its text
        if (c.bodyLayout.preferredHeight != cap) c.bodyLayout.preferredHeight = cap;
        VerticalWrapMode wrap = cap >= 0f ? VerticalWrapMode.Truncate : VerticalWrapMode.Overflow;
        if (c.body.verticalOverflow != wrap) c.body.verticalOverflow = wrap;
    }

    void BuildVRAlerts()
    {
        vrAlerts = VRHud.AddPanel("Environment alerts (VR)", new Vector2(0.5f, 1f), new Vector2(0f, -VRAlertTop),
                                  new Vector2(VRAlertWidth, 0f));
        var stack = vrAlerts.gameObject.AddComponent<VerticalLayoutGroup>();
        stack.spacing = VRAlertGap;
        stack.childControlWidth = stack.childControlHeight = true;
        stack.childForceExpandWidth = true;
        stack.childForceExpandHeight = false;
        vrAlerts.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        for (int i = 0; i < vrCards.Length; i++) vrCards[i] = BuildVRCard(i < VRMaxAlertCards ? "Alert" : "Banner");

        // "+N more": what didn't fit, on a small dark strip under the cards
        RectTransform more = VRHud.NewRect("More", vrAlerts);
        AddBackground(more.gameObject, CardColor);
        var line = more.gameObject.AddComponent<VerticalLayoutGroup>();
        line.padding = new RectOffset(CardPadLeft, CardPadRight, 1, 2);
        line.childControlWidth = line.childControlHeight = true;
        line.childForceExpandWidth = true;
        line.childForceExpandHeight = false;
        vrMoreText = VRHud.AddText(more, "Text", VRHud.SmallFont);
        vrMoreText.color = TagColor;
        vrMoreHeight = 3f + TextHeight(vrMoreText, "+9 more", VRAlertWidth - CardPadLeft - CardPadRight);
        vrMore = more.gameObject;
        vrMore.SetActive(false);

        vrAlertCount = vrBannerCount = vrExtra = vrMoreShown = -1;
    }

    VRCard BuildVRCard(string name)
    {
        var c = new VRCard();
        RectTransform rt = VRHud.NewRect(name, vrAlerts);
        c.go = rt.gameObject;
        AddBackground(c.go, CardColor);
        var layout = c.go.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(CardPadLeft, CardPadRight, CardPadTop, CardPadBottom);
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        // Glow over the whole card (flash when new, pulse for danger), then the stripe, then the text
        c.glow = VRHud.AddImage(rt, "Glow", Color.clear);
        VRHud.Stretch(c.glow.rectTransform, Vector2.zero, Vector2.zero);
        c.glow.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        c.glow.enabled = false;

        c.stripe = VRHud.AddImage(rt, "Stripe", Color.white);
        RectTransform s = c.stripe.rectTransform;
        s.anchorMin = Vector2.zero;
        s.anchorMax = new Vector2(0f, 1f);
        s.pivot = new Vector2(0f, 0.5f);
        s.anchoredPosition = Vector2.zero;
        s.sizeDelta = new Vector2(VRStripe, 0f);
        c.stripe.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

        c.title = VRHud.AddText(rt, "Title", VRHud.TitleFont, FontStyle.Bold);
        c.tag = VRHud.AddText(rt, "Source", VRHud.SmallFont);
        c.tag.color = TagColor;
        c.tagLayout = c.tag.gameObject.AddComponent<LayoutElement>();
        c.body = VRHud.AddText(rt, "Text", VRHud.BodyFont - 1);
        c.bodyLayout = c.body.gameObject.AddComponent<LayoutElement>();
        c.arrival = VRHud.AddText(rt, "Arrival", VRHud.BodyFont - 1);
        c.go.SetActive(false);
        return c;
    }

    // ---------------------------------------------------------------
    // Environment panel (right, under the navigation panel)
    // ---------------------------------------------------------------
    void UpdateVRPanel()
    {
        if (telemetry == null || MapView.AnyOpen)
        {
            if (vrPanel != null) Show(vrPanel.gameObject, false);
            return;
        }
        if (vrPanel == null) BuildVRPanel();
        Show(vrPanel.gameObject, true);

        // The navigation panel is laid out once (and moves onto the sheet when VRRig sets up),
        // so looking for it once a second is plenty
        if (Time.unscaledTime >= vrPanelPlaceAt)
        {
            vrPanelPlaceAt = Time.unscaledTime + 1f;
            PlaceVRPanel();
        }

        int levels = LevelKey();
        if (telemetry != vrPanelFor || levels != vrPanelLevels)
        {
            vrPanelFor = telemetry;
            vrPanelLevels = levels;
            FillVRPanel(telemetry);
        }
    }

    // Top edge 6 units under the navigation panel, same right edge, at least as wide
    void PlaceVRPanel()
    {
        Rect nav = navPanel != null && navPanel.isActiveAndEnabled
            ? VRHud.SheetRectOf(navPanel.transform as RectTransform) : Rect.zero;
        Vector2 pos;
        float width;
        if (nav.width > 0f && nav.height > 0f)
        {
            width = Mathf.Max(nav.width, VRPanelMinWidth);
            // anchoredPosition is from the sheet's top-right corner; the rect is from its centre
            pos = new Vector2(nav.xMax - VRHud.SheetUnitsWide * 0.5f, nav.yMin - VRPanelGap - VRHud.SheetUnitsHigh * 0.5f);
        }
        else
        {
            width = VRPanelMinWidth;
            pos = VRPanelFallback;
        }
        if (vrPanel.anchoredPosition != pos) vrPanel.anchoredPosition = pos;
        if (!Mathf.Approximately(vrPanel.sizeDelta.x, width))
        {
            vrPanel.sizeDelta = new Vector2(width, vrPanel.sizeDelta.y);
            vrPanelFor = null;  // fill it again: what fits on a row depends on the width
        }
    }

    // The alert level of each category the rows are coloured by, as one number
    int LevelKey() => LevelOf("radiation") * 25 + LevelOf("uv") * 5 + LevelOf("dust");

    int LevelOf(string category) =>
        active.TryGetValue(category, out ActiveAlert a) ? Rank(a.msg.level) + 1 : 0;

    // Same rows as DrawPanel. Readings that come with a note ("normal", "vs normal", "from UV")
    // drop it when the row would be too long for the panel.
    void FillVRPanel(TelemetryMessage t)
    {
        bool uv = t.uv_index > 0f || t.uv_abc_wm2 > 0f;
        bool dust = t.uv_abc_wm2 > 0f;
        string rows = dust ? RowsUVDust : uv ? RowsUV : RowsBasic;
        if (rows != vrLabelsShown)
        {
            vrLabelsShown = rows;
            vrPanelLabels.text = rows;
            vrLabelsWidth = TextWidth(vrPanelLabels, rows);
        }
        float room = vrPanel.sizeDelta.x - 2 * VRPanelPad - vrLabelsWidth - 8f;  // for the readings

        int evaS = Mathf.Max(0, Mathf.RoundToInt(t.t_eva_s));
        int lmstMin = Mathf.RoundToInt(t.lmst_h * 60f);
        string clock = $"{evaS / 3600}:{evaS / 60 % 60:00}";
        string mars = $"{lmstMin / 60 % 24:00}:{lmstMin % 60:00}";
        vrValues.Clear();
        AppendValue(Fit($"{clock}   Mars time {mars}", $"{clock}   Mars {mars}", room), null);
        string rate = $"{t.dose_rate_uGy_h:0.0} µGy/h";
        AppendValue(t.dose_expected_uGy_h > 0f ? Fit($"{rate}  (normal {t.dose_expected_uGy_h:0.0})", rate, room) : rate, "radiation");
        AppendValue($"{t.dose_total_mGy:0.000} mGy", "radiation");
        if (uv)
        {
            string index = $"~{t.uv_index:0}";
            AppendValue(Fit($"{index}   UV-C {t.uv_c_wm2:0.00} W/m²", index, room), "uv");
        }
        if (dust)
        {
            AppendValue(t.dust_tau >= 0.05f
                ? Fit($"+{t.dust_tau:0.00} opacity (from UV)", $"+{t.dust_tau:0.00} opacity", room)
                : Fit("clear (UV normal)", "clear", room), "dust");
        }
        string pressure = $"{t.pressure_pa:0} Pa";
        if (Mathf.Abs(t.pressure_anomaly_pa) >= 0.3f)
        {
            string anomaly = $"{t.pressure_anomaly_pa:+0.0;-0.0}";
            pressure = Fit($"{pressure}  ({anomaly} vs normal)", $"{pressure}  ({anomaly})", room);
        }
        AppendValue(pressure, "dust");
        AppendValue($"{t.temp_c:0} °C", null);
        vrValues.Length -= 1;  // the last newline
        string values = vrValues.ToString();
        if (vrPanelValues.text != values) vrPanelValues.text = values;

        string scenario = t.scenario ?? "";
        if (scenario != vrScenarioShown)
        {
            vrScenarioShown = scenario;
            if (scenario.Length > 0)
                vrPanelSimulated.text = "<b>SIMULATED:</b> " + scenario.Replace("solar_particle_event", "solar particle event")
                                                                     .Replace("dust_front", "dust front").Replace(",", ", ");
            Show(vrPanelSimulated.gameObject, scenario.Length > 0);
        }
        string source = t.source ?? "";
        if (source != vrSourceShown)
        {
            vrSourceShown = source;
            vrPanelSource.text = source;
            Show(vrPanelSource.gameObject, source.Length > 0);
        }
    }

    // A reading in its row's colour: the category's alert level, white when there's no alert
    void AppendValue(string value, string category)
    {
        Color c = category != null && active.TryGetValue(category, out ActiveAlert a) ? LevelColor(a.msg.level) : Color.white;
        vrValues.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append('>')
                .Append(value).Append("</color>\n");
    }

    string Fit(string full, string brief, float room) => TextWidth(vrPanelValues, full) <= room ? full : brief;

    void BuildVRPanel()
    {
        vrPanel = VRHud.AddPanel("Environment (VR)", Vector2.one, VRPanelFallback, new Vector2(VRPanelMinWidth, 0f));
        AddBackground(vrPanel.gameObject, VRHud.PanelColor);
        var layout = vrPanel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(VRPanelPad, VRPanelPad, 5, 6);
        layout.spacing = 1f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        vrPanel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        Text title = VRHud.AddText(vrPanel, "Title", VRHud.SmallFont + 1, FontStyle.Bold);
        title.text = "ENVIRONMENT";
        title.color = VRTitleColor;

        // Two columns with the same lines: names on the left, readings right-aligned over them
        vrPanelLabels = VRHud.AddText(vrPanel, "Names", VRHud.SmallFont);
        vrPanelLabels.color = VRLabelColor;
        vrPanelLabels.horizontalOverflow = HorizontalWrapMode.Overflow;
        vrPanelValues = VRHud.AddText(vrPanelLabels.transform, "Readings", VRHud.SmallFont, FontStyle.Normal, TextAnchor.UpperRight);
        VRHud.Stretch(vrPanelValues.rectTransform, Vector2.zero, Vector2.zero);
        vrPanelValues.horizontalOverflow = HorizontalWrapMode.Overflow;

        vrPanelSimulated = VRHud.AddText(vrPanel, "Simulated", VRHud.SmallFont);
        vrPanelSimulated.color = VRSimulatedColor;
        vrPanelSource = VRHud.AddText(vrPanel, "Source", VRHud.SmallFont - 1);
        vrPanelSource.color = VRSourceColor;
        vrPanelSimulated.gameObject.SetActive(false);
        vrPanelSource.gameObject.SetActive(false);

        vrPanelFor = null;
        vrPanelLevels = -1;
        vrPanelPlaceAt = 0f;
        vrLabelsShown = vrScenarioShown = vrSourceShown = null;
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------
    static void AddBackground(GameObject go, Color color)
    {
        Image bg = go.AddComponent<Image>();
        bg.color = color;
        bg.raycastTarget = false;
        VRHud.OnTop(bg);
    }

    static void Show(GameObject go, bool on)
    {
        if (go != null && go.activeSelf != on) go.SetActive(on);
    }

    // Size of a text in sheet units as `t` will draw it on the sheet (the same sums as
    // Text.preferredHeight / preferredWidth). The sheet's text scale is used rather than the
    // text's own: a switched-off text doesn't know its canvas, and would measure at scale 1.
    float TextHeight(Text t, string s, float width)
    {
        if (string.IsNullOrEmpty(s)) return 0f;
        float scale = SheetTextScale();
        TextGenerationSettings g = t.GetGenerationSettings(new Vector2(width, 0f));
        g.scaleFactor = scale;
        return t.cachedTextGeneratorForLayout.GetPreferredHeight(s, g) / scale;
    }

    float TextWidth(Text t, string s)
    {
        if (string.IsNullOrEmpty(s)) return 0f;
        float scale = SheetTextScale();
        TextGenerationSettings g = t.GetGenerationSettings(Vector2.zero);
        g.scaleFactor = scale;
        return t.cachedTextGeneratorForLayout.GetPreferredWidth(s, g) / scale;
    }

    // World-space canvases draw text at its size x CanvasScaler.dynamicPixelsPerUnit
    float SheetTextScale()
    {
        if (vrTextScale <= 0f)
            vrTextScale = VRHud.Sheet.TryGetComponent(out CanvasScaler s) && s.dynamicPixelsPerUnit > 0f ? s.dynamicPixelsPerUnit : 1f;
        return vrTextScale;
    }
}
