using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Turbojet.Animate() (called every FixedUpdate) is isolated and audio-
    // only, same shape as TurbineEngine's -- but unlike TurbineEngine, it
    // never touches turbineAudio.volume at all, and only ever calls Play()
    // once (guarded by !isPlaying) the first time rpm>0 && !outOfSoundCone;
    // after that it just keeps looping forever regardless of later rpm, a
    // real (if slightly odd) vanilla quirk this preserves faithfully via
    // SoundPropagation.IsEngineActive rather than re-gating every frame.
    //
    // Performance bug, found and fixed: this never had TurbineEngineAnimate
    // Patch's own displayDetail gate, so once shouldBeActive went true it
    // stayed true for the rest of the engine's life regardless of camera
    // distance -- every turbojet in a match (once it had ever spun up) kept
    // getting a full RecordEngineSample + TickEngines pass every single
    // frame for as long as it existed, out to the full 20km tracking range,
    // scaling with total live aircraft rather than just visible/detailed
    // ones. In a busy multiplayer session (turbojets being one of the most
    // common engine types) this is the kind of uncapped, ever-growing
    // per-frame cost that reads as "gets worse the longer/bigger the
    // session runs."
    [HarmonyPatch(typeof(Turbojet), "Animate")]
    internal static class TurbojetAnimatePatch
    {
        private static readonly ConditionalWeakTable<Turbojet, object> _loggedDisplayDetailSkip =
            new ConditionalWeakTable<Turbojet, object>();

        private static bool Prefix(
            Turbojet __instance,
            Aircraft ___aircraft,
            AudioSource ___turbineAudio,
            float ___rpm,
            float ___maxRPM,
            float ___turbineMaxPitch,
            bool ___outOfSoundCone)
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
                        $"[EngineAudioDiag] Turbojet.Animate skipped (displayDetail={___aircraft.displayDetail:F3} < 1) "
                        + $"on '{___aircraft.name}' at t={Time.timeSinceLevelLoad:F2}");
                }
                return false;
            }

            bool shouldBeActive = SoundPropagation.IsEngineActive(__instance) || (___rpm > 0f && !___outOfSoundCone);
            if (!shouldBeActive)
            {
                return false;
            }

            float pitch = ___rpm / ___maxRPM * ___turbineMaxPitch;
            // Vanilla sets this to a flat 1f every call (not left at
            // whatever the prefab defaults to), so that's what gets
            // recorded rather than reading the now-untouched real source.
            SoundPropagation.RecordEngineSample(__instance, ___turbineAudio, pitch, ___turbineAudio.volume, __instance.transform.position, 1f, ___aircraft);

            return false;
        }
    }
}
