using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The EVA map (MapView) in AR (ARView): it works exactly as in VR, but ARView only draws what
/// is listed here, so this lists every piece of the map that belongs in the room with you.
/// Markers on the Mars ground (the beams, the route drawn on the terrain, the next-waypoint and
/// Home Base tags out there) are left out: the terrain they stand on isn't shown.
/// </summary>
public partial class MapView
{
    /// <summary>Adds the map's objects to show in AR: the hologram (terrain, pins, route, you,
    /// Home Base), its number and Home Base tags, the panel beside it, the laser and its dot.
    /// The route summary is on the HUD sheet, which ARView shows anyway.</summary>
    internal void CollectARVisible(List<GameObject> into)
    {
        if (holoRoot != null) into.Add(holoRoot);
        if (vrPanel != null) into.Add(vrPanel);
        if (laser != null) into.Add(laser.gameObject);
        if (reticle != null) into.Add(reticle.gameObject);
        foreach (VRWorldTag t in pinTags) if (t != null) into.Add(t.gameObject);
        if (holoHomeTag != null) into.Add(holoHomeTag.gameObject);
        if (handTag != null) into.Add(handTag.gameObject); // hand gestures' tag (MapViewHands)
    }

    /// <summary>AR just came on with the map open: Open() kept it clear of the (now hidden) Mars
    /// hills; put it at the usual height above the real floor instead, panel beside it.</summary>
    internal void ReanchorForAR()
    {
        if (!open || !openedInVR || holoRoot == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        Transform eye = cam.transform;
        float ground = ARView.FloorY(eye);
        holoBase.y = Mathf.Max(eye.position.y - belowEyes, ground + 0.3f); // as Open() does
        holoGroundY = ground;
        PoseHologram();
        OpenVR(eye);
    }
}
