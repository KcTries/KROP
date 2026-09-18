using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // DuctedFan.Animate() mixes fan-blur mesh/material LOD swapping with
    // fanSource.pitch/.volume -- unlike the turbine engines above, there's
    // no clean way to isolate just the audio without also reimplementing
    // the visual LOD logic. A Postfix instead lets the original run
    // completely unchanged (visuals AND audio), then captures whatever it
    // just set on fanSource, feeds that into the delayed clone, and mutes
    // the real source so it's never actually the audible one. Animate() is
    // only called from Update() while aircraft.displayDetail > 1f, so the
    // real source only ever needs re-muting on frames it was actually
    // touched in the first place.
    [HarmonyPatch(typeof(DuctedFan), "Animate")]
    internal static class DuctedFanAnimatePatch
    {
        private static void Postfix(DuctedFan __instance, Aircraft ___aircraft, AudioSource ___fanSource)
        {
            if (___fanSource == null)
            {
                return;
            }
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                SoundPropagation.ApplyCockpitOnlyLowpass(___fanSource);
                return;
            }

            SoundPropagation.RecordEngineSample(
                __instance, ___fanSource, ___fanSource.pitch, ___fanSource.volume,
                __instance.transform.position, ___fanSource.dopplerLevel, ___aircraft);
            ___fanSource.volume = 0f;
        }
    }
}
