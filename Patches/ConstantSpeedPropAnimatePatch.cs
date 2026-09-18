using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // ConstantSpeedProp.PropAnimate() mixes prop-disc blur mesh swapping
    // and blade rotation with propAudio.pitch/.volume/.Play()/.Stop() --
    // same situation as DuctedFan, handled the same way: let the original
    // run completely (visuals, and the real Play()/Stop() calls, which are
    // harmless since the source ends up muted regardless of its play
    // state), then capture what it just set and mute the real source.
    // Unlike the turbine engines, this one genuinely does turn back off
    // (RPM < 1 stops it) rather than looping forever once started, and
    // that's naturally preserved here too since it's mirrored every frame.
    [HarmonyPatch(typeof(ConstantSpeedProp), "PropAnimate")]
    internal static class ConstantSpeedPropAnimatePatch
    {
        // On a turboprop, propAudio commonly shares its GameObject with an
        // unrelated engine's own turbine AudioSource (TurbineEngine's
        // turbineAudio) -- each needs to be an accepted sibling of the
        // other, or ApplyCockpitOnlyLowpass's cross-contamination guard
        // skips filtering entirely on both (confirmed reported symptom:
        // turboprop turbine whine staying unmuffled in the cockpit). Same
        // root cause already found and fixed once for JetNozzle's
        // thrustAudio/Afterburner sharing a GameObject -- see
        // JetNozzleAudioEffectsPatch. Rather than name TurbineEngine
        // specifically (a separate, independent class with no back-
        // reference between the two), this just accepts whatever
        // AudioSources actually share propAudio's own GameObject, cached
        // per-source rather than rebuilt every tick.
        private static readonly ConditionalWeakTable<AudioSource, AudioSource[]> _siblingCache =
            new ConditionalWeakTable<AudioSource, AudioSource[]>();

        private static void Postfix(ConstantSpeedProp __instance, Aircraft ___aircraft, AudioSource ___propAudio)
        {
            if (___propAudio == null)
            {
                return;
            }
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                AudioSource[] siblings = _siblingCache.GetValue(
                    ___propAudio, source => source.gameObject.GetComponents<AudioSource>());
                SoundPropagation.ApplyCockpitOnlyLowpass(___propAudio, siblings);
                return;
            }

            SoundPropagation.RecordEngineSample(
                __instance, ___propAudio, ___propAudio.pitch, ___propAudio.volume,
                __instance.transform.position, ___propAudio.dopplerLevel, ___aircraft);
            ___propAudio.volume = 0f;
        }
    }
}
