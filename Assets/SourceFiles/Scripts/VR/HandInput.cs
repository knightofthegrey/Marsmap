using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Tracked hands (Quest hand tracking, through Unity's XR Hands package and the OpenXR
/// "Hand Tracking Subsystem" feature), as a few points in the world plus what they're doing.
/// Put the controllers down and the Quest switches to hands by itself.
/// Used by the EVA map (VR/MapViewHands.cs).
/// </summary>
public static class HandInput
{
    public struct Hand
    {
        public Vector3 wrist, palm, thumbTip, indexTip;
        /// <summary>The index finger's base knuckle: barely moves when you pinch (hand rays start here).</summary>
        public Vector3 indexKnuckle;
        /// <summary>Direction the palm faces (world).</summary>
        public Vector3 palmNormal;
        /// <summary>Gap between the thumb and index fingertips (m): ~0 = pinching.</summary>
        public float pinchGap;
        /// <summary>Halfway between the thumb and index fingertips.</summary>
        public Vector3 pinchPoint;
        /// <summary>Index, middle and ring fingers all straight (an open hand).</summary>
        public bool fingersOpen;
    }

    static readonly List<XRHandSubsystem> found = new List<XRHandSubsystem>();
    static XRHandSubsystem subsystem;
    static float nextSearch;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        subsystem = null;
        nextSearch = 0f;
    }

    /// <summary>The left or right hand, if it's tracked right now.</summary>
    public static bool TryGet(bool left, out Hand hand)
    {
        hand = default;
        XRHandSubsystem s = Subsystem();
        if (s == null) return false;
        XRHand h = left ? s.leftHand : s.rightHand;
        if (!h.isTracked) return false;

        // Joint poses are in tracking space, like the headset's: the camera's parent (VRRig's
        // Camera Offset) takes them to the world
        Camera cam = Camera.main;
        Transform space = cam != null ? cam.transform.parent : null;
        if (!Joint(h, XRHandJointID.Wrist, space, out Vector3 wrist)
            || !Joint(h, XRHandJointID.Palm, space, out Vector3 palm)
            || !Joint(h, XRHandJointID.ThumbTip, space, out Vector3 thumbTip)
            || !Joint(h, XRHandJointID.IndexTip, space, out Vector3 indexTip)
            || !Joint(h, XRHandJointID.IndexProximal, space, out Vector3 indexBase)
            || !Joint(h, XRHandJointID.MiddleTip, space, out Vector3 middleTip)
            || !Joint(h, XRHandJointID.MiddleProximal, space, out Vector3 middleBase)
            || !Joint(h, XRHandJointID.RingTip, space, out Vector3 ringTip)
            || !Joint(h, XRHandJointID.RingProximal, space, out Vector3 ringBase)
            || !Joint(h, XRHandJointID.LittleProximal, space, out Vector3 littleBase))
            return false;

        hand.wrist = wrist;
        hand.palm = palm;
        hand.thumbTip = thumbTip;
        hand.indexTip = indexTip;
        hand.indexKnuckle = indexBase;
        hand.pinchGap = Vector3.Distance(thumbTip, indexTip);
        hand.pinchPoint = (thumbTip + indexTip) * 0.5f;
        // Palm direction from the knuckles, so it doesn't depend on how the runtime orients its
        // joint axes. Left hand palm down, fingers ahead: index knuckle to the right of the
        // little one, and (index - wrist) x (little - wrist) points down, out of the palm. The
        // right hand is the mirror image, so the other way round.
        Vector3 n = Vector3.Cross(indexBase - wrist, littleBase - wrist);
        hand.palmNormal = (left ? n : -n).normalized;
        hand.fingersOpen = Straight(wrist, indexBase, indexTip) && Straight(wrist, middleBase, middleTip)
                           && Straight(wrist, ringBase, ringTip);
        return true;
    }

    // A finger is straight when its tip is well beyond its knuckle, seen from the wrist
    // (straight: about twice as far; curled into the palm: about as far)
    static bool Straight(Vector3 wrist, Vector3 knuckle, Vector3 tip) =>
        (tip - wrist).sqrMagnitude > 1.5f * 1.5f * (knuckle - wrist).sqrMagnitude;

    static bool Joint(XRHand hand, XRHandJointID id, Transform space, out Vector3 world)
    {
        world = default;
        if (!hand.GetJoint(id).TryGetPose(out Pose pose)) return false;
        world = space != null ? space.TransformPoint(pose.position) : pose.position;
        return true;
    }

    static XRHandSubsystem Subsystem()
    {
        if (subsystem != null && subsystem.running) return subsystem;
        subsystem = null;
        if (Time.unscaledTime < nextSearch) return null;
        nextSearch = Time.unscaledTime + 1f; // not every frame while there are no hands
        SubsystemManager.GetSubsystems(found);
        foreach (XRHandSubsystem s in found)
            if (s.running) { subsystem = s; break; }
        return subsystem;
    }
}
