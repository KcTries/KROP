using HarmonyLib;
using UnityEngine.Rendering.Universal;

namespace QOL_Realisim_Fixes.Patches
{
    // Vanilla's NightVision.UpdateGain() adjusts postExposure purely from
    // the game world's ambient-light-at-time-of-day value (see decompiled
    // source) -- it never looks at what's actually on screen, so a flare
    // or explosion filling the frame with light doesn't trigger any
    // compensation on its own. This Prefix replaces that method's body
    // entirely with one driven by an actual measurement of the rendered
    // frame's brightness (see NightVisionAutoGain) whenever the "NVG
    // Auto-Gain" toggle is on. Returning true when it's off skips straight
    // to vanilla's own method, completely unmodified -- true toggle, not a
    // permanent replacement.
    [HarmonyPatch(typeof(NightVision), "UpdateGain")]
    internal static class NightVisionAutoGainPatch
    {
        private static bool Prefix(ColorAdjustments ___colorAdjustments)
        {
            if (!AutoGainConfig.Enabled.Value)
            {
                return true;
            }
            NightVisionAutoGain.Apply(___colorAdjustments);
            return false;
        }
    }
}
