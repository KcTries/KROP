using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // AoAFeedback is a static (not MonoBehaviour) system, one shared
    // AudioSource re-parented onto whichever aircraft's cockpit is
    // currently being flown (aircraft.cockpit.gameObject), only ever
    // driven from CameraCockpitState -- it's the aerodynamic buffet/
    // airflow rumble that only kicks in once both airspeed AND angle of
    // attack clear their own onset thresholds. Co-located with the
    // aircraft like tire noise/gun recoil/airbrake, so cockpit-view
    // muffling only, no propagation delay.
    [HarmonyPatch(typeof(AoAFeedback), nameof(AoAFeedback.RunAoAFeedback))]
    internal static class AoAFeedbackCockpitFilterPatch
    {
        // The real field is "_source" (leading underscore) -- Harmony's
        // ___fieldName injection matches the parameter name AFTER its own
        // triple-underscore prefix against the field name literally, so
        // this needs a 4th underscore here to actually reach it. Getting
        // this wrong doesn't silently no-op -- Harmony refuses to apply
        // the whole patch (confirmed via BepInEx's LogOutput.log:
        // "Failed to patch ... No such field defined in class AoAFeedback").
        private static void Postfix(AudioSource ____source)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(____source);
        }
    }

    // CameraStateManager.windNoiseExternal is a general speed-based
    // airflow/wind ambience (volume/pitch driven purely by the camera's
    // velocity relative to the local air, as a fraction of the speed of
    // sound -- no AoA involved), updated unconditionally every frame
    // regardless of view state. ApplyCockpitOnlyLowpass's own
    // IsInCockpitView() gate already makes this a no-op outside cockpit
    // view, so patching Update() unconditionally is enough -- no separate
    // view check needed here despite the "External" name.
    [HarmonyPatch(typeof(CameraStateManager), "Update")]
    internal static class WindNoiseCockpitFilterPatch
    {
        private static void Postfix(CameraStateManager __instance)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(__instance.windNoiseExternal);
        }
    }
}
