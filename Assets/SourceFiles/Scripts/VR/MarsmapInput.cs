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
/// Walking (left stick) and jumping (A) are added to CharacterMover's own actions.
/// </summary>
public static class MarsmapInput
{
    public const string LeftStickPath = "<XRController>{LeftHand}/{Primary2DAxis}";
    public const string RightAPath = "<XRController>{RightHand}/{PrimaryButton}";

    /// <summary>True while a headset is running (OpenXR active), false on desktop.</summary>
    public static bool XRActive => UnityEngine.XR.XRSettings.isDeviceActive;

    static InputAction nextLayer, testGust, startEva, sprint, turn;

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
        foreach (InputAction a in new[] { nextLayer, testGust, startEva, sprint, turn })
            a?.Dispose();
        nextLayer = testGust = startEva = sprint = turn = null;
    }
}
