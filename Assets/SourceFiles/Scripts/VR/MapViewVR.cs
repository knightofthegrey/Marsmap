using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The EVA map (MapView) in a headset (Meta Quest). Only runs while a headset is active
/// (MarsmapInput.XRActive); the desktop map is unchanged.
///
///   Open / close the map    Menu button (left controller); clicking the left stick also opens it
///   Point                   the right controller casts a laser; a dot shows where it lands
///   Add a waypoint          point at the hologram and pull the right trigger
///   Remove a waypoint       point at its pin (the laser turns red) and press A
///   Turn / tilt             hold the right grip and move your hand sideways / up and down
///   Turn / zoom             right stick left-right / up-down
///   Move the area shown     left stick
///   Panel buttons           point and pull the trigger: Top-down, Centre on me, Undo, Clear,
///                           Return to Home Base on/off, Close
///
/// IMGUI (OnGUI) isn't drawn in a headset, so here the panel beside the hologram (route numbers
/// and help), the pin numbers, the next-waypoint and Home Base tags on the ground and the route
/// summary in the corner of your view are world-space instead.
/// </summary>
public partial class MapView
{
    [Header("VR (headset only)")]
    [Tooltip("How far in front of your eyes the hologram appears in a headset (m)")]
    public float vrHologramDistance = 1.3f;
    [Tooltip("How far below your eyes it floats in a headset (m): about table height")]
    public float vrHologramBelowEyes = 0.5f;
    [Tooltip("Grid points along each side in a headset (lower = faster rebuilds on a Quest)")]
    [Range(33, 257)] public int vrHologramResolution = 97;
    [Tooltip("Longest reach of the laser (m)")]
    public float vrLaserLength = 5f;
    [Tooltip("Degrees the hologram turns / tilts per metre your hand moves while holding the grip")]
    public float vrGrabDegreesPerMetre = 250f;

    static readonly Color LaserColor = new Color(0.25f, 0.95f, 1f);
    static readonly Color LaserRemoveColor = new Color(1f, 0.3f, 0.25f);
    static readonly Color PanelColor = new Color(0.03f, 0.05f, 0.07f, 0.88f);
    static readonly Color ButtonColor = new Color(0.08f, 0.22f, 0.28f, 0.95f);
    static readonly Color ButtonHoverColor = new Color(0.2f, 0.6f, 0.7f, 1f);
    const float PanelWidth = 560f, PanelHeight = 800f, PanelPixelsPerMetre = 1000f; // 0.56 x 0.8 m
    const float ButtonHeight = 66f, ButtonGap = 10f, PanelPad = 18f;
    const int VRFontSize = 24;

    bool openedInVR;              // this opening is driven by the controllers, not mouse + keys
    float belowEyes = 0.45f;      // the hologram's height below the eyes for this opening

    // ---- laser
    LineRenderer laser;
    Transform reticle;
    Renderer reticleRenderer;
    Material laserMat, laserRemoveMat;

    // ---- panel beside the hologram
    GameObject vrPanel;
    RectTransform vrPanelRect;
    Text vrPanelText, vrHelpText;
    string vrPanelShown;
    readonly List<VRButton> vrButtons = new List<VRButton>();

    // ---- grip-drag, stick zoom
    bool grabbing;
    Vector3 grabLast;
    int zoomDir;
    float zoomRepeatAt;

    // ---- labels
    readonly List<VRWorldTag> pinTags = new List<VRWorldTag>();
    VRWorldTag holoHomeTag, groundTargetTag, groundHomeTag;
    VRHudLabel vrRouteLabel;

    static readonly Regex SizeTag = new Regex(@"<size=\d+>");

    class VRButton
    {
        public RectTransform rect;
        public Image background;
        public Text label;
        public System.Func<string> text;
        public System.Action press;
    }

    /// <summary>What "the way you face" is: your head in a headset (the body only turns with
    /// snap turn there), the player otherwise.</summary>
    Transform Facing => MarsmapInput.XRActive && Camera.main != null ? Camera.main.transform : player;

    // ---------------------------------------------------------------
    // Open / close
    // ---------------------------------------------------------------
    void OpenVR(Transform eye)
    {
        if (vrPanel == null) BuildVRPanel();
        // To the left of the hologram, turned to face you, so it's never behind the hologram
        // when that stands up (top-down view)
        float half = PanelWidth / PanelPixelsPerMetre * 0.5f;
        Vector3 at = holoBase - viewRight * (hologramSize * 0.5f + 0.08f + half) + Vector3.up * 0.3f;
        Vector3 look = at - eye.position;
        vrPanel.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look.sqrMagnitude > 1e-4f ? look : viewForward, Vector3.up));
        vrPanel.SetActive(true);
        vrPanelShown = null;
        grabbing = false;
        zoomDir = 0;
        EnsureLaser();
    }

    void CloseVR()
    {
        if (vrPanel != null) vrPanel.SetActive(false);
        if (laser != null) laser.gameObject.SetActive(false);
        if (reticle != null) reticle.gameObject.SetActive(false);
        foreach (VRWorldTag t in pinTags) if (t != null) t.SetVisible(false);
        if (holoHomeTag != null) holoHomeTag.SetVisible(false);
        grabbing = false;
    }

    void DestroyVR()
    {
        if (laser != null) Destroy(laser.gameObject);
        if (reticle != null) Destroy(reticle.gameObject);
        if (laserMat != null) Destroy(laserMat);
        if (laserRemoveMat != null) Destroy(laserRemoveMat);
        if (vrPanel != null) Destroy(vrPanel);
        foreach (VRWorldTag t in pinTags) if (t != null) Destroy(t.gameObject);
        pinTags.Clear();
        foreach (VRWorldTag t in new[] { holoHomeTag, groundTargetTag, groundHomeTag })
            if (t != null) Destroy(t.gameObject);
        if (vrRouteLabel != null) Destroy(vrRouteLabel.gameObject);
        laser = null;
        reticle = null;
        reticleRenderer = null;
        laserMat = laserRemoveMat = null;
        vrPanel = null;
        vrButtons.Clear();
        holoHomeTag = groundTargetTag = groundHomeTag = null;
        vrRouteLabel = null;
    }

    // ---------------------------------------------------------------
    // Controls (every frame while the map is open in a headset)
    // ---------------------------------------------------------------
    void VRControls()
    {
        float dt = Time.unscaledDeltaTime;
        Physics.SyncTransforms(); // the hologram's collider where it's drawn (it may be turning)

        // What the laser is on: a panel button, the panel, the hologram (whichever is nearer)
        bool haveRay = TryGetHandRay(out Ray ray);
        int button = -1;
        float panelDistance = float.PositiveInfinity;
        RaycastHit hit = default;
        bool onHolo = false;
        if (haveRay)
        {
            button = PanelHit(ray, out panelDistance);
            onHolo = holoCollider != null && holoCollider.Raycast(ray, out hit, vrLaserLength)
                     && hit.distance < panelDistance;
        }
        // A pin the laser passes close to, nearer than what it lands on (pins have no colliders,
        // and the laser comes in low: aiming at a pin it lands on the terrain well behind it)
        float nearest = Mathf.Min(onHolo ? hit.distance : vrLaserLength, panelDistance);
        float pinDistance = 0f;
        int pin = haveRay ? PinOnRay(ray, nearest, out pinDistance) : -1;
        if (pin >= 0)
        {
            onHolo = false;
            button = -1;
        }
        bool onPanel = haveRay && pin < 0 && !onHolo && button != -1;
        if (!onPanel) button = -1;
        float length = pin >= 0 ? pinDistance : onHolo ? hit.distance : onPanel ? panelDistance : vrLaserLength;
        ShowLaser(haveRay, ray, length, onHolo || onPanel || pin >= 0, pin >= 0);
        UpdateButtons(button);

        // Trigger: press a button, or drop a waypoint where the laser lands
        if (MarsmapInput.MapSelect.WasPressedThisFrame())
        {
            if (onPanel)
            {
                if (button >= 0)
                {
                    vrButtons[button].press();
                    return; // "Close map" may have closed it
                }
            }
            else if (onHolo) AddWaypointAt(ray);
            else if (pin >= 0) status = $"That's waypoint {pin + 1}: press A to remove it, or point at open ground to add one.";
            else if (haveRay) status = "Point the laser at the hologram to add a waypoint.";
        }
        // A: remove the waypoint whose pin you point at
        if (MarsmapInput.MapRemove.WasPressedThisFrame())
        {
            if (pin >= 0) RemoveWaypoint(pin);
            else if (waypoints.Count > 0) status = "Point at a waypoint's pin (the laser turns red) and press A to remove it.";
        }

        // Grip held: move your hand sideways to turn the hologram, up/down to tilt it
        if (haveRay && MarsmapInput.MapGrab.IsPressed())
        {
            if (grabbing)
            {
                Vector3 d = ray.origin - grabLast;
                mapYaw -= Vector3.Dot(d, viewRight) * vrGrabDegreesPerMetre;
                tilt = Mathf.Clamp(tilt + d.y * vrGrabDegreesPerMetre, 0f, 90f);
                tiltTarget = -1f;
                yawTarget = float.NaN;
            }
            grabbing = true;
            grabLast = ray.origin;
        }
        else grabbing = false;

        // Right stick: left/right turns it smoothly, up/down zooms in steps (held: repeats)
        Vector2 rs = MarsmapInput.Turn.ReadValue<Vector2>();
        int dir = 0;
        if (Mathf.Abs(rs.x) > Mathf.Abs(rs.y))
        {
            if (Mathf.Abs(rs.x) > 0.2f)
            {
                mapYaw -= rs.x * 90f * dt;
                yawTarget = float.NaN;
            }
        }
        else dir = rs.y > 0.6f ? 1 : rs.y < -0.6f ? -1 : 0;
        if (dir != 0 && (dir != zoomDir || Time.unscaledTime >= zoomRepeatAt))
        {
            Zoom(dir * 1.5f);
            zoomRepeatAt = Time.unscaledTime + 0.35f;
        }
        zoomDir = dir;

        // Left stick: move the area shown (walking is locked while the map is open)
        Vector2 ls = MarsmapInput.LeftStick.ReadValue<Vector2>();
        if (ls.sqrMagnitude > 0.04f) MoveArea(Vector2.ClampMagnitude(ls, 1f) * (radius * 0.8f * dt));
    }

    // The right controller's pointing ray in the world, or false while it isn't tracked
    bool TryGetHandRay(out Ray ray)
    {
        ray = default;
        Camera cam = Camera.main;
        if (cam == null) return false;
        if (!ReadPose(MarsmapInput.RightAimPosition, MarsmapInput.RightAimRotation, out Vector3 p, out Quaternion q)
            && !ReadPose(MarsmapInput.RightGripPosition, MarsmapInput.RightGripRotation, out p, out q))
            return false;
        // Poses are in tracking space, like the headset's: the camera's parent (VRRig's Camera
        // Offset) takes them to the world
        Transform space = cam.transform.parent;
        if (space != null)
        {
            p = space.TransformPoint(p);
            q = space.rotation * q;
        }
        ray = new Ray(p, q * Vector3.forward);
        return true;
    }

    static bool ReadPose(InputAction position, InputAction rotation, out Vector3 p, out Quaternion q)
    {
        p = position.ReadValue<Vector3>();
        q = rotation.ReadValue<Quaternion>();
        float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
        if (position.controls.Count == 0 || rotation.controls.Count == 0 || n < 0.25f) return false; // none / not tracked
        n = Mathf.Sqrt(n);
        q = new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n);
        return true;
    }

    // -1 = not on the panel, -2 = on the panel but not on a button, else the button's index
    int PanelHit(Ray ray, out float distance)
    {
        distance = float.PositiveInfinity;
        if (vrPanel == null || !vrPanel.activeInHierarchy) return -1;
        var plane = new Plane(vrPanelRect.forward, vrPanelRect.position);
        if (!plane.Raycast(ray, out float enter) || enter > vrLaserLength) return -1;
        Vector3 p = ray.GetPoint(enter);
        if (!vrPanelRect.rect.Contains((Vector2)vrPanelRect.InverseTransformPoint(p))) return -1;
        distance = enter;
        for (int i = 0; i < vrButtons.Count; i++)
        {
            RectTransform b = vrButtons[i].rect;
            if (b.rect.Contains((Vector2)b.InverseTransformPoint(p))) return i;
        }
        return -2;
    }

    // The waypoint whose pin the laser passes within ~2 cm of, before `maxDistance` along it
    // (the nearest such pin), or -1. distance = how far along the laser that is.
    int PinOnRay(Ray ray, float maxDistance, out float distance)
    {
        distance = maxDistance;
        int found = -1;
        const float reach = 0.02f;
        for (int i = 0; i < waypoints.Count && i < holoPins.Count; i++)
        {
            GameObject pin = holoPins[i];
            if (pin == null || !pin.activeInHierarchy) continue;
            Vector3 bottom = pin.transform.position - pin.transform.up * pin.transform.lossyScale.y;
            Vector3 top = PinTop(pin) + pin.transform.up * 0.02f; // the number tag above it counts too
            if (ClosestRaySegment(ray, bottom, top, out float t) < reach && t < distance)
            {
                distance = t;
                found = i;
            }
        }
        return found;
    }

    // Distance between a ray and the segment a-b; t = where along the ray that is closest
    static float ClosestRaySegment(Ray ray, Vector3 a, Vector3 b, out float t)
    {
        Vector3 o = ray.origin, d1 = ray.direction, d2 = b - a;
        float e = Vector3.Dot(d2, d2);
        // Closest point on the segment to the ray's line (s in 0..1 along a-b), then the
        // ray point nearest that (t >= 0), then the segment point nearest that again
        float s = 0f;
        if (e > 1e-9f)
        {
            Vector3 n = Vector3.Cross(d1, Vector3.Cross(d2, d1)); // in the plane of both, across the ray
            float den = Vector3.Dot(d2, n);
            s = Mathf.Abs(den) > 1e-9f ? Mathf.Clamp01(Vector3.Dot(o - a, n) / den)
                                       : Mathf.Clamp01(Vector3.Dot(o - a, d2) / e); // parallel
        }
        t = Mathf.Max(0f, Vector3.Dot(a + d2 * s - o, d1));
        if (e > 1e-9f) s = Mathf.Clamp01(Vector3.Dot(o + d1 * t - a, d2) / e);
        return (o + d1 * t - (a + d2 * s)).magnitude;
    }

    void EnsureLaser()
    {
        if (laser != null) return;
        laserMat = MakeMaterial(LaserColor);
        laserRemoveMat = MakeMaterial(LaserRemoveColor);
        laser = MakeLine(null, laserMat, "EVA Map Laser (VR)");
        laser.useWorldSpace = true;
        laser.positionCount = 2;
        laser.widthMultiplier = 0.004f;
        laser.numCapVertices = 0;
        laser.textureMode = LineTextureMode.Stretch;
        GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dot.name = "EVA Map Laser Dot (VR)";
        Destroy(dot.GetComponent<Collider>());
        reticleRenderer = dot.GetComponent<Renderer>();
        SetupRenderer(reticleRenderer, laserMat);
        reticle = dot.transform;
        laser.gameObject.SetActive(false);
        dot.SetActive(false);
    }

    void ShowLaser(bool on, Ray ray, float length, bool hitting, bool removing)
    {
        EnsureLaser();
        laser.gameObject.SetActive(on);
        reticle.gameObject.SetActive(on && hitting);
        if (!on) return;
        Vector3 end = ray.GetPoint(length);
        laser.SetPosition(0, ray.origin + ray.direction * 0.03f);
        laser.SetPosition(1, end);
        Material m = removing ? laserRemoveMat : laserMat;
        if (laser.sharedMaterial != m) laser.sharedMaterial = m;
        if (reticleRenderer.sharedMaterial != m) reticleRenderer.sharedMaterial = m;
        reticle.position = end;
        reticle.localScale = Vector3.one * (0.01f + 0.004f * length); // about the same size at any distance
    }

    // ---------------------------------------------------------------
    // The panel: route numbers, help, buttons
    // ---------------------------------------------------------------
    void BuildVRPanel()
    {
        vrPanel = new GameObject("EVA Map Panel (VR)", typeof(RectTransform));
        var canvas = vrPanel.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 60;
        vrPanelRect = (RectTransform)vrPanel.transform;
        vrPanelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        vrPanelRect.localScale = Vector3.one / PanelPixelsPerMetre;

        RectTransform bg = UIRect("Background", vrPanelRect);
        Stretch(bg, Vector2.zero, Vector2.zero);
        Image bgImage = bg.gameObject.AddComponent<Image>();
        bgImage.color = PanelColor;
        bgImage.raycastTarget = false;

        // Fixed help just above the buttons; the route info fills the rest and shrinks to fit
        float buttonsTop = PanelPad + 3f * ButtonHeight + 2f * ButtonGap;
        const float helpHeight = 96f;
        RectTransform help = UIRect("Help", vrPanelRect);
        help.anchorMin = new Vector2(0f, 0f);
        help.anchorMax = new Vector2(1f, 0f);
        help.offsetMin = new Vector2(PanelPad, buttonsTop + 8f);
        help.offsetMax = new Vector2(-PanelPad, buttonsTop + 8f + helpHeight);
        vrHelpText = MakeText(help, 19, TextAnchor.LowerLeft);
        vrHelpText.color = new Color(0.62f, 0.83f, 0.86f);
        vrHelpText.horizontalOverflow = HorizontalWrapMode.Wrap;
        vrHelpText.verticalOverflow = VerticalWrapMode.Truncate;
        vrHelpText.resizeTextForBestFit = true;
        vrHelpText.resizeTextMinSize = 12;
        vrHelpText.resizeTextMaxSize = 19;
        vrHelpText.text = "Trigger: add a waypoint · A: remove the pin you point at (laser red)\n" +
                          "Grip + move your hand: turn / tilt · Right stick: turn, zoom\n" +
                          "Left stick: move the area · Menu: close";

        RectTransform info = UIRect("Info", vrPanelRect);
        Stretch(info, new Vector2(PanelPad, buttonsTop + 16f + helpHeight), new Vector2(-PanelPad, -PanelPad));
        vrPanelText = MakeText(info, VRFontSize, TextAnchor.UpperLeft);
        vrPanelText.horizontalOverflow = HorizontalWrapMode.Wrap;
        vrPanelText.verticalOverflow = VerticalWrapMode.Truncate;
        vrPanelText.resizeTextForBestFit = true; // long routes shrink instead of losing lines
        vrPanelText.resizeTextMinSize = 12;
        vrPanelText.resizeTextMaxSize = VRFontSize;

        AddButton(0, 0, () => tilt < 45f ? "Top-down view" : "Lay it flat", ToggleTopDown);
        AddButton(1, 0, () => "Centre on me", CentreOnYou);
        AddButton(0, 1, () => "Undo last", UndoWaypoint);
        AddButton(1, 1, () => "Clear all", ClearWaypoints);
        AddButton(0, 2, () => roundTrip ? "Return home: ON" : "Return home: OFF", ToggleRoundTrip);
        AddButton(1, 2, () => "Close map", Close);
        vrPanel.SetActive(false);
    }

    // col 0/1, row 0 = top
    void AddButton(int col, int row, System.Func<string> text, System.Action press)
    {
        float w = (PanelWidth - 2f * PanelPad - ButtonGap) / 2f;
        RectTransform rt = UIRect("Button", vrPanelRect);
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero; // bottom-left
        rt.sizeDelta = new Vector2(w, ButtonHeight);
        rt.anchoredPosition = new Vector2(PanelPad + col * (w + ButtonGap),
                                          PanelPad + (2 - row) * (ButtonHeight + ButtonGap));
        var b = new VRButton { rect = rt, text = text, press = press };
        b.background = rt.gameObject.AddComponent<Image>();
        b.background.color = ButtonColor;
        b.background.raycastTarget = false;
        RectTransform lr = UIRect("Label", rt);
        Stretch(lr, new Vector2(8f, 4f), new Vector2(-8f, -4f));
        b.label = MakeText(lr, VRFontSize, TextAnchor.MiddleCenter);
        b.label.fontStyle = FontStyle.Bold;
        vrButtons.Add(b);
    }

    void UpdateButtons(int hovered)
    {
        for (int i = 0; i < vrButtons.Count; i++)
        {
            VRButton b = vrButtons[i];
            Color c = i == hovered ? ButtonHoverColor : ButtonColor;
            if (b.background.color != c) b.background.color = c;
            string t = b.text();
            if (b.label.text != t) b.label.text = t;
        }
    }

    void UpdateVRPanel()
    {
        if (vrPanelText == null) return;
        // Status first (it's the latest news); no <size> tags, so best fit can shrink everything
        string text =
            $"<b><color=#F0915A>EVA MAP</color></b>   {FormatDistance(2f * radius)} across, height x{exaggeration:0.#}\n" +
            (string.IsNullOrEmpty(status) ? "" : $"<color=#F0C080>{status}</color>\n") +
            StripSizes(RouteText(withStatus: false));
        if (text == vrPanelShown) return;
        vrPanelShown = text;
        vrPanelText.text = text;
    }

    // RouteText() sizes lines for the desktop panel; <size> is absolute, which would stop best
    // fit from shrinking those lines, so drop the tags here
    static string StripSizes(string s) =>
        string.IsNullOrEmpty(s) ? "" : SizeTag.Replace(s, "").Replace("</size>", "");

    // ---------------------------------------------------------------
    // World-space labels (every frame in a headset, map open or not)
    // ---------------------------------------------------------------
    void UpdateVRLabels()
    {
        bool showHolo = open && openedInVR;
        if (showHolo) UpdateVRPanel();

        // Hologram: a number over every pin, "Home Base" over its pin
        for (int i = 0; i < holoPins.Count || i < pinTags.Count; i++)
        {
            bool show = showHolo && i < waypoints.Count && i < holoPins.Count && holoPins[i] != null
                        && holoPins[i].activeInHierarchy;
            if (!show)
            {
                if (i < pinTags.Count) pinTags[i].SetVisible(false);
                continue;
            }
            while (pinTags.Count <= i) pinTags.Add(MakeTag($"Pin label {pinTags.Count + 1} (VR)", 0.022f));
            VRWorldTag tag = pinTags[i];
            tag.SetVisible(true);
            tag.Set($"{i + 1}", i == nextWaypoint ? new Color(0f, 0.35f, 0.45f, 0.85f) : new Color(0f, 0f, 0f, 0.75f));
            tag.transform.position = PinTop(holoPins[i]);
        }
        bool homePin = showHolo && HasHome && holoHome != null && holoHome.activeInHierarchy;
        if (homePin && holoHomeTag == null) holoHomeTag = MakeTag("Home Base label (VR)", 0.02f);
        if (holoHomeTag != null)
        {
            holoHomeTag.SetVisible(homePin);
            if (homePin)
            {
                holoHomeTag.Set("Home Base", new Color(0.02f, 0.3f, 0.1f, 0.85f));
                holoHomeTag.transform.position = PinTop(holoHome);
            }
        }

        // On the ground: the next waypoint's tag above its beam, and Home Base's above its sphere
        bool target = !open && player != null && terraingen != null && nextWaypoint < TargetCount;
        if (target && groundTargetTag == null) groundTargetTag = MakeTag("Next waypoint label (VR)", 0f, 2.4f, -10);
        if (groundTargetTag != null)
        {
            groundTargetTag.SetVisible(target);
            if (target)
            {
                Vector3 at = Target(nextWaypoint);
                bool home = nextWaypoint >= waypoints.Count;
                string name = home ? "HOME BASE" : $"WAYPOINT {nextWaypoint + 1}";
                groundTargetTag.Set($"<b>{name}</b>\n{FormatDistance(FlatDistance(player, at))}",
                                    home ? new Color(0.02f, 0.35f, 0.12f, 0.85f) : new Color(0f, 0.3f, 0.4f, 0.85f));
                at.y = terraingen.GroundY(at) + beaconHeight * 0.35f;
                groundTargetTag.transform.position = at;
            }
        }
        bool homeTag = !open && HasHome && player != null && terraingen != null
                       && homeBeam != null && homeBeam.activeInHierarchy;
        if (homeTag && groundHomeTag == null) groundHomeTag = MakeTag("Home Base label (VR)", 0f, 2.2f, -10);
        if (groundHomeTag != null)
        {
            groundHomeTag.SetVisible(homeTag);
            if (homeTag)
            {
                groundHomeTag.Set($"<b>HOME BASE</b>\n{FormatDistance(FlatDistance(player, tripStart))}",
                                  new Color(0.02f, 0.3f, 0.1f, 0.85f));
                Vector3 at = tripStart;
                at.y = terraingen.GroundY(at) + (showHomeSphere ? homeSphereSize : 0f) + 2f;
                groundHomeTag.transform.position = at;
            }
        }

        UpdateVRRouteLabel();
    }

    // Lower right of your view while walking: the next target and the route's numbers
    void UpdateVRRouteLabel()
    {
        if (vrRouteLabel == null) vrRouteLabel = VRHud.CreateLabel("EVA route (VR)", new Vector2(0.42f, -0.5f), 0.5f);
        vrRouteLabel.SetVisible(!open);
        if (open) return;
        string title, detail;
        if (waypoints.Count == 0)
        {
            title = "EVA map";
            detail = (string.IsNullOrEmpty(status) ? "" : status + "\n") +
                     "Menu button (left controller): open the map and plot a route";
        }
        else
        {
            title = nextWaypoint >= TargetCount || player == null ? "Route complete"
                  : (nextWaypoint < waypoints.Count ? $"Next: waypoint {nextWaypoint + 1} of {waypoints.Count}"
                                                    : "Next: back to Home Base") +
                    $"  {FormatDistance(FlatDistance(player, Target(nextWaypoint)))}";
            detail = "";
            if (route != null && route.distance_m > 0f)
                detail = $"Route {route.distance_m / 1000f:F2} km · {FormatMinutes(route.minutes)} · {DifficultyTag(route.difficulty)}\n";
            if (!string.IsNullOrEmpty(status)) detail += status + "\n";
            detail += "Menu: map";
        }
        vrRouteLabel.Set(title, detail);
    }

    static Vector3 PinTop(GameObject pin) =>
        pin.transform.position + pin.transform.up * (pin.transform.lossyScale.y + 0.004f);

    // Ground markers draw before the head-locked HUD (low order), hologram ones after it
    static VRWorldTag MakeTag(string name, float lineHeight, float angularSize = 0f, int sortingOrder = 90)
    {
        VRWorldTag t = VRHud.CreateWorldTag(name, sortingOrder);
        t.lineHeight = lineHeight;
        t.angularSize = angularSize;
        return t;
    }

    // ---------------------------------------------------------------
    // UI building blocks
    // ---------------------------------------------------------------
    static RectTransform UIRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    static void Stretch(RectTransform rt, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
    }

    static Text MakeText(RectTransform rt, int size, TextAnchor anchor)
    {
        Text t = rt.gameObject.AddComponent<Text>();
        t.font = VRHud.BuiltinFont();
        t.fontSize = size;
        t.color = Color.white;
        t.alignment = anchor;
        t.supportRichText = true;
        t.raycastTarget = false;
        return t;
    }
}
