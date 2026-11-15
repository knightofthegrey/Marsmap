using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.OpenXR;

namespace Marsmap.AR.EditorTools
{
    /// <summary>
    /// The project settings Quest passthrough and hand tracking need, applied automatically when
    /// the editor loads (and from the menu Marsmap > AR: Set Up Quest Passthrough):
    ///  - "Meta XR Feature" on in OpenXR for Android, so the Meta runtime (OVRManager) starts;
    ///  - Passthrough Support = Supported in Meta XR's Project Config, which adds the passthrough
    ///    feature to the Android manifest (without it the headset refuses passthrough);
    ///  - "Hand Tracking Subsystem" on in OpenXR for Android (XR Hands: HandInput reads it), and
    ///    Hand Tracking Support = Controllers And Hands in the Project Config, which adds the hand
    ///    tracking permission and feature to the manifest (without them the Quest gives no hands).
    /// Changes nothing that's already set; says in the Console what it changed.
    /// </summary>
    [InitializeOnLoad]
    static class PassthroughProjectSetup
    {
        const float GiveUpAfterSeconds = 120f;
        static double waitStarted;

        static PassthroughProjectSetup()
        {
            if (Application.isBatchMode) return;
            waitStarted = EditorApplication.timeSinceStartup;
            EditorApplication.update += WaitThenApply; // Meta's config can't be read until the editor is ready
        }

        static void WaitThenApply()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling
                || EditorApplication.isUpdating) return;
            if (OVRProjectConfig.CachedProjectConfig == null
                && EditorApplication.timeSinceStartup - waitStarted < GiveUpAfterSeconds) return;
            EditorApplication.update -= WaitThenApply;
            Apply(false);
        }

        [MenuItem("Marsmap/AR: Set Up Quest Passthrough")]
        static void ApplyFromMenu() => Apply(true);

        static void Apply(bool fromMenu)
        {
            var changed = new List<string>();
            var problems = new List<string>();

            OVRProjectConfig config = OVRProjectConfig.CachedProjectConfig;
            if (config == null)
            {
                problems.Add("Meta XR's Project Config isn't ready yet; try the menu again in a few seconds");
            }
            else if (config.insightPassthroughSupport == OVRProjectConfig.FeatureSupport.None)
            {
                config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Supported;
                OVRProjectConfig.CommitProjectConfig(config);
                changed.Add("Meta XR Project Config: Passthrough Support = Supported");
            }
            if (config != null && config.handTrackingSupport == OVRProjectConfig.HandTrackingSupport.ControllersOnly)
            {
                config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
                OVRProjectConfig.CommitProjectConfig(config);
                changed.Add("Meta XR Project Config: Hand Tracking Support = Controllers And Hands");
            }

            OpenXRSettings android = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            global::Meta.XR.MetaXRFeature metaFeature = android != null ? android.GetFeature<global::Meta.XR.MetaXRFeature>() : null;
            if (metaFeature == null)
            {
                problems.Add("no OpenXR settings for Android (Project Settings > XR Plug-in Management, Android tab: tick OpenXR)");
            }
            else if (!metaFeature.enabled)
            {
                metaFeature.enabled = true;
                EditorUtility.SetDirty(android);
                AssetDatabase.SaveAssets();
                changed.Add("OpenXR (Android): Meta XR Feature on");
            }
            var hands = android != null ? android.GetFeature<UnityEngine.XR.Hands.OpenXR.HandTracking>() : null;
            if (android != null && hands == null)
            {
                problems.Add("the OpenXR Hand Tracking Subsystem feature isn't there (is com.unity.xr.hands installed?)");
            }
            else if (hands != null && !hands.enabled)
            {
                hands.enabled = true;
                EditorUtility.SetDirty(android);
                AssetDatabase.SaveAssets();
                changed.Add("OpenXR (Android): Hand Tracking Subsystem on");
            }

            if (changed.Count > 0)
                Debug.Log("[Marsmap AR] Set up for Quest passthrough and hands: " + string.Join("; ", changed) + ".");
            if (problems.Count > 0)
                Debug.LogWarning("[Marsmap AR] Passthrough setup incomplete: " + string.Join("; ", problems) + ".");
            else if (fromMenu && changed.Count == 0)
                Debug.Log("[Marsmap AR] Quest passthrough and hand tracking settings are already in place.");
        }
    }
}
