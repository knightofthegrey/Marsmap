using UnityEngine;

/// <summary>
/// The whole EVA map (MapView) with hands instead of controllers (Quest hand tracking,
/// HandInput). Put the controllers down and the Quest switches to hands by itself.
///
///   Open / close the map   left hand: palm up, fingers open, look at it and hold still
///                          ~0.7 s (a tag on your palm fills up; drop the pose before it can
///                          toggle again)
///   Point                  a laser comes out of your right hand (the left one if the right
///                          isn't tracked), aimed from your shoulder through your knuckles
///   Add a waypoint         point at the hologram and pinch (thumb + index), let go
///   Remove a waypoint      point at its pin (the laser turns red) and pinch
///   Panel buttons          point at one and pinch (Top-down, Centre on me, Undo, Clear,
///                          Return to Home Base, Close)
///   Turn / tilt            pinch and hold (either hand), then move the hand: sideways turns
///                          the hologram, up / down tilts it
///   Zoom / slide the area  pinch with BOTH hands: spread them apart to zoom in, together to
///                          zoom out; move both together to slide the map along
///
/// A pinch is a tap (add / remove / press) if you let go before the hand moves 2 cm, else a
/// drag, like the trigger on the controller. The open gesture is an open hand on purpose: a
/// pinch with the palm toward you is the Quest's own menu gesture.
/// </summary>
public partial class MapView
{
    [Header("Hands (headset hand tracking)")]
    [Tooltip("How long to hold the left palm up (fingers open, looking at it) to open / close the map")]
    public float handGestureSeconds = 0.7f;
    [Tooltip("Degrees the hologram turns per metre you move a pinching hand sideways (0.3 m = 90°)")]
    public float handTurnDegreesPerMetre = 300f;
    [Tooltip("Degrees it tilts per metre you move a pinching hand up or down")]
    public float handTiltDegreesPerMetre = 200f;

    // Pinch: closes under 1.5 cm between the fingertips, lets go over 3.5 cm (no flicker between)
    const float PinchOn = 0.015f, PinchOff = 0.035f;
    const float PinchMoveStart = 0.02f;   // a pinch moving this far is a drag, not a tap
    const float HandLostGrace = 0.25f;    // hand tracking drops a frame now and then: wait this long
    const float ShoulderDrop = 0.15f, ShoulderWidth = 0.15f; // where hand rays start from, below / beside the eyes

    static readonly Color HandTagIdle = new Color(0.03f, 0.05f, 0.07f, 0.75f);
    static readonly Color HandTagFull = new Color(0.15f, 0.65f, 0.75f, 0.95f);

    // ---- open gesture
    float palmHeld;               // how long the open-map pose has been held
    float palmLost;               // how long it's been gone (a short blip keeps the progress)
    bool palmArmed = true;        // false after a toggle until the pose is let go
    bool palmTagShown;            // the tag is showing the palm's "Hold: ..." prompt

    // ---- pinches (index 0 = left hand, 1 = right)
    enum HandMode { None, One, Two, Done }   // Done: used up (a button press, or two hands), wait for all to let go
    HandMode handMode;
    readonly bool[] pinched = new bool[2];
    readonly float[] handLostFor = new float[2];
    readonly HandInput.Hand[] hands = new HandInput.Hand[2];
    readonly Vector3[] rayDir = new Vector3[2];
    readonly bool[] handResync = new bool[2]; // tracking came back: take its new position as the start, don't jump
    readonly bool[] needOpen = new bool[2];   // its pinch ended by losing tracking: open the fingers before the next counts
    int pointerHand = -1;         // whose laser it is (kept through a pinch, so pinching with the other hand doesn't move it)
    float handTurnSign = -1f;     // +1 / -1: which way a drag turns, so the part you grabbed follows (as BeginHold)
    int pinchHand = -1;           // HandMode.One: which hand
    Vector3 pinchStart, pinchLast;
    bool pinchMoving;
    // the pointing hand's laser when the pinch went down: a tap acts on what it pointed at then
    bool tapCounts;
    Ray tapRay;
    bool tapOnHolo;
    int tapPin;
    // HandMode.Two
    Vector3 twoMidLast;
    float twoGapLast;

    VRWorldTag handTag;

    // ---------------------------------------------------------------
    // Open / close: left palm up, held
    // ---------------------------------------------------------------

    // True once when held long enough (then the pose has to be let go first)
    bool HandMapGesture()
    {
        float dt = Time.unscaledDeltaTime;
        Vector3 at = default;
        bool pose = HandInput.TryGet(true, out HandInput.Hand h) && OpenPalmUp(h, out at);
        if (!pose)
        {
            // Only really letting go of the pose re-arms it (not a pinch with the other hand)
            palmLost += dt;
            if (palmLost > HandLostGrace)
            {
                palmHeld = 0f;
                palmArmed = true;
                HidePalmTag();
            }
            return false;
        }
        palmLost = 0f;
        // Pinching (either hand) or just toggled: wait, without filling up
        if (!palmArmed || handMode != HandMode.None || pinched[0] || pinched[1])
        {
            palmHeld = 0f;
            HidePalmTag();
            return false;
        }
        palmHeld += dt;
        float done = Mathf.Clamp01(palmHeld / Mathf.Max(0.05f, handGestureSeconds));
        ShowHandTag(true, at, open ? "Hold: close map" : "Hold: open map", done);
        palmTagShown = true;
        if (done < 1f) return false;
        palmArmed = false;
        palmHeld = 0f;
        HidePalmTag();
        return true;
    }

    void HidePalmTag()
    {
        if (!palmTagShown) return; // leave a pinch's tag alone
        palmTagShown = false;
        ShowHandTag(false, default, null, 0f);
    }

    // Palm facing up, fingers open, not pinching, and in front of your eyes (so a hand resting
    // palm-up in your lap doesn't open the map). at = where the tag goes, just above the palm.
    static bool OpenPalmUp(HandInput.Hand h, out Vector3 at)
    {
        at = h.palm + Vector3.up * 0.06f;
        if (Vector3.Dot(h.palmNormal, Vector3.up) < 0.7f || !h.fingersOpen || h.pinchGap < 0.04f) return false;
        Camera cam = Camera.main;
        return cam != null && Vector3.Angle(cam.transform.forward, h.palm - cam.transform.position) < 45f;
    }

    // ---------------------------------------------------------------
    // While the map is open in a headset (after VRControls, which handles the controllers)
    // ---------------------------------------------------------------
    void HandControls()
    {
        if (holding) // the controller's trigger drag has it
        {
            ResetHands();
            return;
        }
        float dt = Time.unscaledDeltaTime;
        bool[] down = { false, false };
        bool[] tracked = { false, false };
        bool[] lostRelease = { false, false };
        for (int i = 0; i < 2; i++)
        {
            bool was = pinched[i];
            if (HandInput.TryGet(i == 0, out HandInput.Hand h))
            {
                tracked[i] = true;
                hands[i] = h;
                handLostFor[i] = 0f;
                if (needOpen[i])
                {
                    // Came back still pinched after a loss: not a new pinch until the fingers open
                    if (h.pinchGap > PinchOff) needOpen[i] = false;
                    pinched[i] = false;
                }
                else pinched[i] = was ? h.pinchGap < PinchOff : h.pinchGap < PinchOn;
            }
            else
            {
                handLostFor[i] += dt; // keep the last pose for a moment; let go after that
                handResync[i] = true;
                rayDir[i] = Vector3.zero; // its laser starts fresh when it's back
                if (handLostFor[i] > HandLostGrace && pinched[i])
                {
                    pinched[i] = false;
                    needOpen[i] = true;
                    lostRelease[i] = true; // lost, not let go: that's no tap
                }
            }
            down[i] = !was && pinched[i];
        }

        // The pointing hand: the right one, else the left. Kept through a pinch (a left-hand drag
        // while the right points leaves the laser on the right), unless it loses tracking.
        int pointer = pointerHand;
        if (handMode == HandMode.None || pointer < 0 || !tracked[pointer])
            pointer = tracked[1] ? 1 : tracked[0] ? 0 : -1;
        if (pointer != pointerHand && pointer >= 0) rayDir[pointer] = Vector3.zero; // snap, don't swing in
        pointerHand = pointer;
        Ray ray = default;
        bool haveRay = pointer >= 0 && HandRay(pointer, out ray);
        int button = -1, pin = -1;
        bool onHolo = false, onPanel = false;
        RaycastHit holoHit = default;
        if (haveRay)
        {
            Aim(ray, out button, out pin, out onHolo, out onPanel, out holoHit, out float length);
            bool turning = pinchMoving || handMode == HandMode.Two;
            if (turning) // like the controller drag: no red pins, no panel; on the hologram or full length
                ShowLaser(true, ray, onHolo ? holoHit.distance : vrLaserLength, true, false, true);
            else
                ShowLaser(true, ray, length, onHolo || onPanel || pin >= 0, pin >= 0, false);
            UpdateButtons(handMode == HandMode.None ? button : -1);
        }

        switch (handMode)
        {
            case HandMode.One:
                if (pinched[0] && pinched[1]) StartTwoHands();
                else if (!pinched[pinchHand]) // let go (or lost)
                {
                    bool tap = !pinchMoving && tapCounts && !lostRelease[pinchHand];
                    handMode = lostRelease[pinchHand] ? HandMode.Done : HandMode.None;
                    ShowHandTag(false, default, null, 0f);
                    pinchMoving = false;
                    if (tap) HandTap();
                }
                else if (tracked[pinchHand]) OneHandDrag(pinchHand);
                break;
            case HandMode.Two:
                if (!pinched[0] || !pinched[1])
                {
                    handMode = HandMode.Done; // the other hand doesn't start turning it now
                    ShowHandTag(false, default, null, 0f);
                    EndTwoHands();
                }
                else if (tracked[0] && tracked[1]) TwoHandZoomSlide();
                break;
            case HandMode.Done:
                if (!pinched[0] && !pinched[1]) handMode = HandMode.None;
                break;
        }

        if (handMode != HandMode.None) return;
        if (pinched[0] && pinched[1] && (down[0] || down[1]))
        {
            StartTwoHands();
            return;
        }
        for (int i = 1; i >= 0; i--)
        {
            if (!down[i]) continue;
            // A panel button acts at once (like the trigger); the panel itself does nothing
            if (i == pointer && haveRay && onPanel)
            {
                handMode = HandMode.Done;
                if (button >= 0)
                {
                    vrButtons[button].press();
                    return; // "Close map" may have closed it
                }
                return;
            }
            handMode = HandMode.One;
            pinchHand = i;
            pinchStart = pinchLast = hands[i].indexKnuckle;
            handResync[i] = false;
            pinchMoving = false;
            palmHeld = 0f;
            tapCounts = i == pointer && haveRay; // only the pointing hand's tap adds / removes
            tapRay = ray;
            tapOnHolo = onHolo;
            tapPin = pin;
            handTurnSign = i == pointer && haveRay && onHolo ? TurnSignAt(ray, holoHit) : -1f;
            return;
        }
    }

    // What the laser is on, as with the controller: a panel button, the panel, a pin (nearer
    // than what the laser lands on), the hologram; length = how far to draw it
    void Aim(Ray ray, out int button, out int pin, out bool onHolo, out bool onPanel, out RaycastHit hit, out float length)
    {
        button = PanelHit(ray, out float panelDistance);
        hit = default;
        onHolo = holoCollider != null && holoCollider.Raycast(ray, out hit, vrLaserLength) && hit.distance < panelDistance;
        float nearest = Mathf.Min(onHolo ? hit.distance : vrLaserLength, panelDistance);
        pin = PinOnRay(ray, nearest, out float pinDistance);
        if (pin >= 0)
        {
            onHolo = false;
            button = -1;
        }
        onPanel = pin < 0 && !onHolo && button != -1;
        if (!onPanel) button = -1;
        length = pin >= 0 ? pinDistance : onHolo ? hit.distance : onPanel ? panelDistance : vrLaserLength;
        if (button < 0) button = -1; // -2 = on the panel but not on a button
    }

    // A pinch let go without moving: add a waypoint / remove the pin it pointed at
    void HandTap()
    {
        if (tapPin >= 0)
        {
            if (tapPin < waypoints.Count) RemoveWaypoint(tapPin);
        }
        else if (tapOnHolo) AddWaypointAt(tapRay);
        else status = "Point at the hologram (the laser shows where) and pinch to add a waypoint.";
    }

    // One hand pinching and moving: sideways turns, up / down tilts (like the trigger drag).
    // Follows the index knuckle, not the fingertips: they shift as you open them to let go,
    // which would nudge the map every time.
    void OneHandDrag(int i)
    {
        Vector3 p = hands[i].indexKnuckle;
        if (handResync[i])
        {
            handResync[i] = false;
            pinchLast = p;
        }
        if (!pinchMoving && (p - pinchStart).magnitude > PinchMoveStart) pinchMoving = true;
        if (pinchMoving)
        {
            Vector3 moved = p - pinchLast;
            // Sideways as you see it: the part you grabbed follows the hand (near edge by default)
            mapYaw += handTurnSign * Vector3.Dot(moved, LevelRight()) * handTurnDegreesPerMetre;
            // Raising the hand lifts the far edge toward you (as raising the laser does)
            tilt = Mathf.Clamp(tilt + moved.y * handTiltDegreesPerMetre, 0f, 90f);
            tiltTarget = -1f;
            yawTarget = float.NaN; // cancels a Top-down swing in progress, like any drag
            ShowHandTag(true, hands[i].pinchPoint + Vector3.up * 0.04f, "Turning", 1f);
        }
        pinchLast = p;
    }

    // Which way a sideways drag should turn the hologram so the point the laser grabbed moves
    // with the hand, worked out as the controller's BeginHold does. Near the middle: -1
    // (the near half follows, like the desktop drag).
    float TurnSignAt(Ray ray, RaycastHit hit)
    {
        if (holoRoot == null) return -1f;
        Vector3 right = Vector3.Cross(Vector3.up, ray.direction);
        if (right.sqrMagnitude < 1e-4f) return -1f;
        Vector3 pivot = holoRoot.transform.position;
        float follows = Vector3.Dot(Vector3.Cross(holoRoot.transform.up, hit.point - pivot), right.normalized);
        return Mathf.Abs(follows) > 0.05f * hologramSize ? Mathf.Sign(follows) : -1f;
    }

    void StartTwoHands()
    {
        handMode = HandMode.Two;
        pinchMoving = false;
        tapCounts = false;
        twoMidLast = (hands[0].indexKnuckle + hands[1].indexKnuckle) * 0.5f;
        twoGapLast = Vector3.Distance(hands[0].indexKnuckle, hands[1].indexKnuckle);
        handResync[0] = handResync[1] = false;
    }

    // Both hands pinching: the gap between them zooms (apart = in), moving both slides the map
    // along with them (as if holding the terrain)
    void TwoHandZoomSlide()
    {
        Vector3 a = hands[0].indexKnuckle, b = hands[1].indexKnuckle; // steady while pinching (see OneHandDrag)
        Vector3 mid = (a + b) * 0.5f;
        float gap = Vector3.Distance(a, b);
        if (handResync[0] || handResync[1])
        {
            handResync[0] = handResync[1] = false;
            twoMidLast = mid;
            twoGapLast = gap;
        }
        if (twoGapLast > 0.05f && gap > 0.05f) radius *= twoGapLast / gap;
        Vector3 moved = mid - twoMidLast;
        if (holoContent != null)
        {
            // The hand movement in the hologram's own units (turned / tilted with it, and with
            // the not-yet-rebuilt zoom below), then in map metres; the area shown moves the
            // other way, so the terrain follows the hands
            Vector3 local = holoContent.InverseTransformVector(moved);
            float metresPerUnit = 2f * builtRadius / hologramSize;
            centre -= new Vector2(local.x, local.z) * metresPerUnit;
        }
        if (terraingen != null && terraingen.TryGetMapBounds(out Rect area)) // as BuildHologram limits them
        {
            radius = Mathf.Clamp(radius, 100f, Mathf.Max(area.width, area.height) * 0.5f);
            centre.x = Mathf.Clamp(centre.x, area.xMin, area.xMax);
            centre.y = Mathf.Clamp(centre.y, area.yMin, area.yMax);
        }
        twoGapLast = gap;
        twoMidLast = mid;
        ShowUnbuiltView();
        // Rebuilding the terrain is slow on a Quest: only once it has moved / zoomed noticeably
        // (the view above shows the change meanwhile), not for every millimetre of hand jitter
        if (builtRadius > 0f && ((centre - builtCentre).magnitude > 0.06f * builtRadius
                                 || Mathf.Abs(radius / builtRadius - 1f) > 0.06f))
            rebuildPending = true;
        ShowHandTag(true, mid + Vector3.up * 0.06f, "Zoom / slide", 1f);
    }

    // Shows a zoom / slide that isn't rebuilt yet by scaling and shifting the hologram's content
    // (terrain, pins, route) straight away, so it follows the hands. BuildHologram puts it back.
    void ShowUnbuiltView()
    {
        if (holoContent == null || builtRadius <= 0f || radius <= 0f) return;
        float s = builtRadius / radius;
        Vector2 shift = (centre - builtCentre) / (2f * builtRadius) * hologramSize * s;
        holoContent.localScale = new Vector3(s, 1f, s);
        holoContent.localPosition = new Vector3(-shift.x, 0f, -shift.y);
    }

    // Both-hand pinch over: build what it ended on
    void EndTwoHands()
    {
        if (centre != builtCentre || !Mathf.Approximately(radius, builtRadius)) rebuildPending = true;
    }

    // A hand's pointing ray: from a point near that shoulder through the index knuckle (which
    // stays put while you pinch, so pinching doesn't jog the laser), smoothed a little
    bool HandRay(int i, out Ray ray)
    {
        ray = default;
        Camera cam = Camera.main;
        if (cam == null) return false;
        Vector3 head = cam.transform.position;
        Vector3 shoulder = head + Vector3.down * ShoulderDrop + LevelRight() * (i == 1 ? ShoulderWidth : -ShoulderWidth);
        Vector3 origin = hands[i].indexKnuckle;
        Vector3 dir = origin - shoulder;
        if (dir.sqrMagnitude < 1e-4f) return false;
        dir.Normalize();
        float k = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
        rayDir[i] = rayDir[i].sqrMagnitude > 0.5f ? Vector3.Slerp(rayDir[i], dir, k).normalized : dir;
        ray = new Ray(origin, rayDir[i]);
        return true;
    }

    void ResetHands()
    {
        if (handMode == HandMode.Two) EndTwoHands();
        handMode = HandMode.None;
        rayDir[0] = rayDir[1] = Vector3.zero;
        needOpen[0] = needOpen[1] = false;
        pointerHand = -1;
        palmTagShown = false;
        pinched[0] = pinched[1] = false;
        pinchHand = -1;
        pinchMoving = false;
        tapCounts = false;
        ShowHandTag(false, default, null, 0f);
    }

    Vector3 LevelRight()
    {
        Camera cam = Camera.main;
        Vector3 r = cam != null ? Vector3.ProjectOnPlane(cam.transform.right, Vector3.up) : viewRight;
        return r.sqrMagnitude > 1e-4f ? r.normalized : viewRight;
    }

    // The tag over your hand: label, and its background fills from dark to bright as `done` goes 0..1
    void ShowHandTag(bool show, Vector3 at, string label, float done)
    {
        if (!show)
        {
            if (handTag != null) handTag.SetVisible(false);
            return;
        }
        if (handTag == null) handTag = MakeTag("EVA Map Hand Tag (VR)", 0.018f, 0f, 120);
        handTag.SetVisible(true);
        handTag.transform.position = at;
        handTag.Set(label, Color.Lerp(HandTagIdle, HandTagFull, done));
    }
}
