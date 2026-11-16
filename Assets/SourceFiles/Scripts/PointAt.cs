using UnityEngine;

// A HUD arrow (drawn pointing up) that shows which way `dest` is, relative to where the player
// is facing: straight up = ahead, right = turn right.
//
// With "Orbit Compass" on (default) it rides around the rim of the compass (Compass.cs, found
// automatically) at that angle, pointing outward, so it turns together with the compass as you
// turn. Off: it just turns in place, as before.
public class PointAt : MonoBehaviour
{
    public Transform playerTransform;
    public Transform dest;
    public Transform compassDial;

    [Tooltip("Ride around the rim of the compass (found automatically) instead of turning in place")]
    public bool orbitCompass = true;
    [Tooltip("Where the arrow rides, as a share of the compass's radius (1 = its edge). For the red " +
             "and green arrows MapView sets this from its Compass Arrow Radius (0.75).")]
    public float orbitRadius = 0.75f;
    [Tooltip("Optional: the compass dial to orbit. Empty: the one Compass.cs turns.")]
    public RectTransform orbitAround;

    bool searched;

    void Update()
    {
        if (playerTransform == null || compassDial == null || dest == null) return;

        // The ground is the X/Z plane (Y is up), so use X and Z, not X and Y
        Vector3 toDest = dest.position - playerTransform.position;

        // Compass bearing to the destination, clockwise from +Z (north), like the player's yaw
        float bearing = Mathf.Atan2(toDest.x, toDest.z) * Mathf.Rad2Deg;

        // UI rotation is counter-clockwise, so a target to the right needs a negative angle
        float dialZ = playerTransform.eulerAngles.y - bearing;
        compassDial.localEulerAngles = new Vector3(0f, 0f, dialZ);

        // Ride around the compass: same angle, measured clockwise from the top of the dial
        RectTransform arrow = compassDial as RectTransform;
        RectTransform dial = FindDial();
        if (!orbitCompass || arrow == null || dial == null) return;
        arrow.pivot = new Vector2(0.5f, 0.5f); // turn about its own middle, we place it ourselves
        Vector3 centre = dial.TransformPoint(dial.rect.center); // doesn't move as the dial turns
        float rimRadius = dial.rect.width * 0.5f * dial.lossyScale.x * orbitRadius;
        float clockwise = -dialZ * Mathf.Deg2Rad;
        // In the canvas's own plane: on screen that's x/y, but in a headset the canvas floats in
        // front of your face and turns with it (VRRig), so world x/y would drift off the dial
        Transform plane = dial.parent != null ? dial.parent : dial;
        arrow.position = centre + (plane.right * Mathf.Sin(clockwise) + plane.up * Mathf.Cos(clockwise)) * rimRadius;
    }

    RectTransform FindDial()
    {
        if (orbitAround == null && !searched)
        {
            searched = true;
            Compass compass = FindAnyObjectByType<Compass>();
            if (compass != null) orbitAround = compass.compassDial as RectTransform;
        }
        return orbitAround;
    }
}
