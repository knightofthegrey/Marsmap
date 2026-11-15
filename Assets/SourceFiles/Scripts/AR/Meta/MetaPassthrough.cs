using UnityEngine;

namespace Marsmap.AR
{
    /// <summary>
    /// Quest passthrough through the Meta XR Core SDK: an OVRManager with Insight Passthrough on,
    /// and an OVRPassthroughLayer (always an underlay: the camera image goes behind everything
    /// Unity draws, showing wherever the eye buffer is see-through).
    ///
    /// Uses the scene's OVRManager / OVRPassthroughLayer if there are any, otherwise adds its own.
    /// Only compiled when the package com.meta.xr.sdk.core is installed (see the asmdef).
    /// ARView (main scripts) decides when to start it and what to hide.
    /// </summary>
    public class MetaPassthrough : IPassthroughBackend
    {
        // OVRManager only starts if the Meta runtime (OVRPlugin) is up, which needs the
        // "Meta XR Feature" in the OpenXR settings. Give it this long before calling it a failure.
        const float ManagerTimeoutSeconds = 3f;

        bool started;
        float startedAt;
        GameObject host;
        OVRPassthroughLayer layer;
        string problem;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register() => Passthrough.Backend = new MetaPassthrough();

        public string Problem => problem;

        public void Start()
        {
            if (started) return;
            started = true;
            startedAt = Time.unscaledTime;

            OVRManager manager = OVRManager.instance != null
                ? OVRManager.instance
                : Object.FindAnyObjectByType<OVRManager>(FindObjectsInactive.Include);
            if (manager == null)
            {
                // Added switched off, so passthrough is already on when its Awake runs
                host = new GameObject("Passthrough (AR)");
                host.SetActive(false);
                manager = host.AddComponent<OVRManager>();
                manager.isInsightPassthroughEnabled = true;
                host.SetActive(true);
            }
            else
            {
                manager.isInsightPassthroughEnabled = true; // OVRManager switches it on in its Update
            }

            layer = Object.FindAnyObjectByType<OVRPassthroughLayer>(FindObjectsInactive.Include);
            if (layer == null)
            {
                if (host == null) host = new GameObject("Passthrough (AR)");
                layer = host.AddComponent<OVRPassthroughLayer>();
            }
            layer.hidden = false;
            layer.textureOpacity = 1f;
            layer.enabled = true;
            if (!layer.gameObject.activeSelf) layer.gameObject.SetActive(true);
        }

        // So the passthrough cameras and the underlay don't keep running behind the VR view
        public void Stop()
        {
            if (!started) return;
            if (OVRManager.instance != null) OVRManager.instance.isInsightPassthroughEnabled = false;
            if (layer != null)
            {
                layer.hidden = true;
                layer.enabled = false;
            }
        }

        public PassthroughState State
        {
            get
            {
                if (!started) return PassthroughState.NotStarted;
                if (problem != null) return PassthroughState.Failed;
                if (OVRManager.instance == null)
                {
                    if (Time.unscaledTime - startedAt < ManagerTimeoutSeconds) return PassthroughState.Starting;
                    return Fail("OVRManager didn't start: the Meta runtime isn't running. Enable \"Meta XR Feature\" " +
                                "in Project Settings > XR Plug-in Management > OpenXR (Android tab), or use the menu " +
                                "Marsmap > AR: Set Up Quest Passthrough.");
                }
                if (!OVRManager.IsInsightPassthroughSupported())
                    return Fail("this headset has no passthrough camera (Quest 2 or newer needed).");
                if (OVRManager.HasInsightPassthroughInitFailed())
                    return Fail("Meta's passthrough wouldn't start. Is Passthrough Support set to Supported in " +
                                "Meta XR > Project Config (Marsmap > AR: Set Up Quest Passthrough does it)?");
                if (OVRManager.IsInsightPassthroughInitialized()) return PassthroughState.Running;
                return PassthroughState.Starting;
            }
        }

        PassthroughState Fail(string why)
        {
            problem = why;
            if (layer != null) layer.hidden = true;
            return PassthroughState.Failed;
        }
    }
}
