using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Turbofan.Animate() -- identical shape to Turbojet's (isolated,
    // audio-only, no volume formula, one-time Play() trigger that's never
    // undone), just without the outOfSoundCone gate on starting.
    //
    // Same missing-displayDetail-gate performance bug as Turbojet's own
    // patch (see its comment) -- fixed the same way here.
    [HarmonyPatch(typeof(Turbofan), "Animate")]
    internal static class TurbofanAnimatePatch
    {
        private static readonly ConditionalWeakTable<Turbofan, object> _loggedDisplayDetailSkip =
            new ConditionalWeakTable<Turbofan, object>();

        private static bool Prefix(
            Turbofan __instance,
            Aircraft ___aircraft,
            AudioSource ___turbineAudio,
            float ___currentRPM,
            float ___maxRPM,
            float ___turbineMaxPitch)
        {
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                SoundPropagation.ApplyCockpitOnlyLowpass(___turbineAudio);
                return true;
            }

            if (___aircraft.displayDetail < 1f)
            {
                if (!_loggedDisplayDetailSkip.TryGetValue(__instance, out _))
                {
                    _loggedDisplayDetailSkip.Add(__instance, null);
                    SoundPropagation.Log.LogInfo(
                        $"[EngineAudioDiag] Turbofan.Animate skipped (displayDetail={___aircraft.displayDetail:F3} < 1) "
                        + $"on '{___aircraft.name}' at t={Time.timeSinceLevelLoad:F2}");
                }
                return false;
            }

            bool shouldBeActive = SoundPropagation.IsEngineActive(__instance) || ___currentRPM > 0f;
            if (!shouldBeActive)
            {
                return false;
            }

            float pitch = ___currentRPM / ___maxRPM * ___turbineMaxPitch;
            // Vanilla sets this to a flat 1f every call, same as Turbojet.
            SoundPropagation.RecordEngineSample(__instance, ___turbineAudio, pitch, ___turbineAudio.volume, __instance.transform.position, 1f, ___aircraft);

            return false;
        }
    }
}
