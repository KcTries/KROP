using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // TurbineEngine.Animate(bool running) is a small, isolated method
    // called once per frame from Update() (after all the RPM/fuel/power
    // physics for that frame has already run) that does nothing but set
    // turbineAudio.pitch/.volume -- purely presentational, safe to fully
    // replace. Reproduces the exact same formula, but feeds the result into
    // SoundPropagation's engine history instead of writing it directly onto
    // the real (moving, attached-to-the-aircraft) AudioSource. Since that
    // real source is only ever set to volume 0 once in OnEnable() and never
    // touched again anywhere else in TurbineEngine, skipping this method
    // leaves it permanently silent with no separate mute step needed -- a
    // tracked clone in SoundPropagation is the only thing that ever
    // actually plays this engine's sound now.
    //
    // Covers TurbineEngine specifically -- Turbojet, Turbofan, PropFan,
    // DuctedFan, and ConstantSpeedProp are separate, independent classes
    // with their own audio code, not subclasses of this one, and aren't
    // covered by this patch.
    [HarmonyPatch(typeof(TurbineEngine), "Animate")]
    internal static class TurbineEngineAnimatePatch
    {
        // Logged once per engine instance, not every frame -- an AI
        // aircraft that simply never comes into detail range would
        // otherwise spam this every frame of the whole match. A
        // ConditionalWeakTable, not a plain HashSet -- every turbine-engine
        // aircraft that ever spawned added an entry that was never removed,
        // leaking the TurbineEngine reference for the rest of the process
        // lifetime across an entire session's worth of missions.
        private static readonly ConditionalWeakTable<TurbineEngine, object> _loggedDisplayDetailSkip =
            new ConditionalWeakTable<TurbineEngine, object>();

        // On a turboprop, turbineAudio commonly shares its GameObject with
        // an unrelated ConstantSpeedProp's own propAudio -- each needs to
        // be an accepted sibling of the other, or ApplyCockpitOnlyLowpass's
        // cross-contamination guard skips filtering entirely on both
        // (confirmed reported symptom: turboprop turbine whine staying
        // unmuffled in the cockpit). Same root cause already found and
        // fixed once for JetNozzle's thrustAudio/Afterburner sharing a
        // GameObject -- see JetNozzleAudioEffectsPatch and the matching
        // fix in ConstantSpeedPropAnimatePatch.
        private static readonly ConditionalWeakTable<AudioSource, AudioSource[]> _siblingCache =
            new ConditionalWeakTable<AudioSource, AudioSource[]>();

        private static bool Prefix(
            TurbineEngine __instance,
            bool running,
            AudioSource ___turbineAudio,
            float ___pitch,
            float ___volume)
        {
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                AudioSource[] siblings = _siblingCache.GetValue(
                    ___turbineAudio, source => source.gameObject.GetComponents<AudioSource>());
                SoundPropagation.ApplyCockpitOnlyLowpass(___turbineAudio, siblings);
                return true;
            }

            if (__instance.aircraft.displayDetail < 1f)
            {
                // displayDetail defaults to 0 (C#'s default for an
                // unassigned float field) until CameraStateManager assigns
                // it a real value -- for a just-spawned aircraft, there can
                // be a brief window before that happens where this gate is
                // closed for reasons unrelated to actual camera distance.
                // Suspected contributor to the reported "random turbine
                // whine at spawn": while closed, RecordEngineSample never
                // runs, leaving a gap in this engine's history; once it
                // reopens, whatever pitch/volume was captured first (a
                // startup RPM transient, potentially) plays back through a
                // freshly created clone with no prior context to smooth
                // against.
                if (!_loggedDisplayDetailSkip.TryGetValue(__instance, out _))
                {
                    _loggedDisplayDetailSkip.Add(__instance, null);
                    SoundPropagation.Log.LogInfo(
                        $"[EngineAudioDiag] TurbineEngine.Animate skipped (displayDetail={__instance.aircraft.displayDetail:F3} < 1) "
                        + $"on '{__instance.aircraft.name}' at t={Time.timeSinceLevelLoad:F2}");
                }
                return false;
            }

            float pitch = __instance.RPMRatio - __instance.powerRatio * ___pitch + ___pitch;
            float volume = running
                ? (__instance.RPMRatio * 0.25f + __instance.powerRatio * 0.75f) * ___volume
                : __instance.RPMRatio * 0.5f * ___volume;

            SoundPropagation.RecordEngineSample(
                __instance, ___turbineAudio, pitch, volume, __instance.transform.position, ___turbineAudio.dopplerLevel,
                __instance.aircraft);

            return false;
        }
    }
}
