using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Screenshot helper: press F8 to turn the whole 3D view pure green (#00FF00) so only the UI
/// shows (compass, minimap, nav panel, alerts...). Press F8 again to bring the world back.
/// Handy for cutting the UI out of a screenshot with a chroma key.
///
/// Every camera that draws to the screen stops drawing objects and clears to green; cameras
/// that draw into a texture are left alone. The dust storm's full-screen tint is hidden too.
/// Created automatically, nothing to add to the scene.
/// </summary>
public class GreenScreen : MonoBehaviour
{
    public static bool On { get; private set; }
    public static readonly Color Green = new Color(0f, 1f, 0f, 1f);

    struct Saved { public Camera cam; public CameraClearFlags clear; public Color background; public int mask; }
    readonly List<Saved> saved = new List<Saved>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        On = false;
        var go = new GameObject(nameof(GreenScreen));
        DontDestroyOnLoad(go);
        go.AddComponent<GreenScreen>();
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.f8Key.wasPressedThisFrame) Set(!On);
        if (On) Grab(); // any camera that turned on since
    }

    void Set(bool on)
    {
        On = on;
        if (on) { Grab(); return; }
        foreach (Saved s in saved)
        {
            if (s.cam == null) continue;
            s.cam.clearFlags = s.clear;
            s.cam.backgroundColor = s.background;
            s.cam.cullingMask = s.mask;
        }
        saved.Clear();
    }

    void Grab()
    {
        foreach (Camera cam in Camera.allCameras)
        {
            if (cam.targetTexture != null || saved.Exists(s => s.cam == cam)) continue;
            saved.Add(new Saved { cam = cam, clear = cam.clearFlags, background = cam.backgroundColor, mask = cam.cullingMask });
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Green;
            cam.cullingMask = 0;
        }
    }

    void OnDestroy() { if (On) Set(false); }
}
