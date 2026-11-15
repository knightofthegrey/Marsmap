using UnityEngine;

// Turns a HUD arrow (drawn pointing up) so it points at `dest` relative to where the player
// is facing: up = straight ahead, right = turn right. Works like Compass.cs, which turns the
// dial by the player's heading.
public class PointAt : MonoBehaviour
{
    public Transform playerTransform;
    public Transform dest;
    public Transform compassDial;

    // Update is called once per frame
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
    }
}
