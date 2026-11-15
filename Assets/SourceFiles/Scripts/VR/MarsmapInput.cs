using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The demo's buttons, each bound to a keyboard key AND a Quest Touch controller button, so the
/// same code runs on desktop and in a headset. Bindings use OpenXR usage names, which every
/// Quest controller (Touch, Touch Pro, Touch Plus) provides.
///
///   Next map layer   L        B (right)
///   Test dust gust   G        Y (left)
///   Start EVA        Enter    X (left)     offline mode
///   Sprint           L Shift  left grip
///   Snap turn        -        right stick left/right
///   EVA map          M        Menu (left), or click the left stick
/// While the map is open (MapView): right-hand laser, trigger adds a waypoint / presses a panel
/// button, A removes the pin you point at, grip + move your hand turns and tilts the hologram,
/// right stick turns / zooms it, left stick moves the area shown.
/// Walking (left stick) and jumping (A) are added to CharacterMover's own actions.
/// </summary>
public static class MarsmapInput
{
    public const string LeftStickPath = "<XRController>{LeftHand}/{Primary2DAxis}";
    public const string RightAPath = "<XRController>{RightHand}/{PrimaryButton}";

    /// <summary>True while a headset is running (OpenXR active), false on desktop.</summary>
    public static bool XRActive => UnityEngine.XR.XRSettings.isDeviceActive;

    public const string RightStickPath = "<XRController>{RightHand}/{Primary2DAxis}";

    static InputAction nextLayer, testGust, startEva, sprint, turn;
    static InputAction mapToggle, mapOpenAlt, mapSelect, mapRemove, mapGrab, leftStick;
    static InputAction aimPos, aimRot, gripPos, gripRot;

    public static InputAction NextLayer => nextLayer ??= Button("Next layer", "<Keyboard>/l",
        "<XRController>{RightHand}/{SecondaryButton}");

    public static InputAction TestGust => testGust ??= Button("Test gust", "<Keyboard>/g",
        "<XRController>{LeftHand}/{SecondaryButton}");

    public static InputAction StartEva => startEva ??= Button("Start EVA", "<Keyboard>/enter",
        "<Keyboard>/numpadEnter", "<XRController>{LeftHand}/{PrimaryButton}");

    public static InputAction Sprint => sprint ??= Button("Sprint", "<Keyboard>/leftShift",
        "<XRController>{LeftHand}/{GripButton}");

    public static InputAction Turn
    {
        get
        {
            if (turn != null) return turn;
            turn = new InputAction("Snap turn", InputActionType.Value, expectedControlType: "Vector2");
            turn.AddBinding("<XRController>{RightHand}/{Primary2DAxis}");
            turn.Enable();
            return turn;
        }
    }

    // ---- EVA map (MapView) in a headset. XR bindings only: the desktop keys are read directly.
    public static InputAction MapToggle => mapToggle ??= Button("EVA map",
        "<XRController>{LeftHand}/{MenuButton}");

    /// <summary>Opens the map only (the left stick pans it once open, so a click mustn't close it).</summary>
    public static InputAction MapOpenAlt => mapOpenAlt ??= Button("EVA map (stick click)",
        "<XRController>{LeftHand}/{Primary2DAxisClick}");

    public static InputAction MapSelect => mapSelect ??= Button("Map select",
        "<XRController>{RightHand}/{TriggerButton}");

    public static InputAction MapRemove => mapRemove ??= Button("Map remove",
        "<XRController>{RightHand}/{PrimaryButton}");

    public static InputAction MapGrab => mapGrab ??= Button("Map grab",
        "<XRController>{RightHand}/{GripButton}");

    public static InputAction LeftStick => leftStick ??= Value("Left stick", "Vector2", LeftStickPath);

    // Right controller pose, in tracking space (the same space as the headset's pose, so it goes
    // through the camera's parent to reach the world). Aim pose = where the controller points;
    // grip pose is the fallback for runtimes without one.
    public static InputAction RightAimPosition => aimPos ??= Value("Right aim position", "Vector3",
        "<XRController>{RightHand}/pointerPosition");
    public static InputAction RightAimRotation => aimRot ??= Value("Right aim rotation", "Quaternion",
        "<XRController>{RightHand}/pointerRotation");
    public static InputAction RightGripPosition => gripPos ??= Value("Right grip position", "Vector3",
        "<XRController>{RightHand}/devicePosition");
    public static InputAction RightGripRotation => gripRot ??= Value("Right grip rotation", "Quaternion",
        "<XRController>{RightHand}/deviceRotation");

    // PassThrough: always the bound control's current value (no "actuation" needed, which poses
    // and sticks at rest wouldn't pass)
    static InputAction Value(string name, string controlType, string path)
    {
        var action = new InputAction(name, InputActionType.PassThrough, path, expectedControlType: controlType);
        action.Enable();
        return action;
    }

    static InputAction Button(string name, params string[] paths)
    {
        var action = new InputAction(name, InputActionType.Button);
        foreach (string p in paths) action.AddBinding(p);  // added before Enable, as required
        action.Enable();
        return action;
    }

    // With "Enter Play Mode Options" (no domain reload) statics survive between plays:
    // dispose the old actions so each play starts with fresh, enabled ones.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        foreach (InputAction a in new[] { nextLayer, testGust, startEva, sprint, turn, mapToggle, mapOpenAlt, mapSelect,
                                          mapRemove, mapGrab, leftStick, aimPos, aimRot, gripPos, gripRot })
            a?.Dispose();
        nextLayer = testGust = startEva = sprint = turn = null;
        mapToggle = mapOpenAlt = mapSelect = mapRemove = mapGrab = leftStick = null;
        aimPos = aimRot = gripPos = gripRot = null;
    }
}
