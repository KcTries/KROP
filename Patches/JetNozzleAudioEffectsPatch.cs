using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // JetNozzle.AudioEffects() is the main thrust "roar" (thrustAudio,
    // distinct from Turbojet/Turbofan's own RPM-pitch turbineAudio hum) --
    // clean and audio-only, but it also writes camFacing/directionalVolumeMult
    // back onto instance fields that Thrust() reads immediately afterward to
    // feed into each Afterburner.Run() call, so those need `ref` injection
    // to keep propagating correctly even though this Prefix skips the rest
    // of the original method's body.
    [HarmonyPatch(typeof(JetNozzle), "AudioEffects")]
    internal static class JetNozzleAudioEffectsPatch
    {
        // Afterburner is a private nested type -- can't reference it by name
        // from here, so its "source" field is reached via cached reflection
        // instead (AccessTools.Inner, same pattern JetNozzleAfterburnerPatch
        // already uses to patch the type itself).
        private static readonly FieldInfo AfterburnerSourceField =
            AccessTools.Field(AccessTools.Inner(typeof(JetNozzle), "Afterburner"), "source");

        // afterburners is a serialized field baked into the prefab -- its
        // contents (which AudioSources exist) never change after Awake, so
        // the sibling list built from it is cached per-JetNozzle rather than
        // rebuilt (a fresh array + a reflection GetValue per afterburner)
        // every single physics tick this branch runs, which is every tick
        // while Engine Sound Propagation is off (the default).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JetNozzle, AudioSource[]> _nozzleSourcesCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<JetNozzle, AudioSource[]>();

        private static bool Prefix(
            JetNozzle __instance,
            Aircraft ___aircraft,
            AudioSource ___thrustAudio,
            float ___thrustRatio,
            float ___thrustMaxVolume,
            Transform ___thrustTransform,
            ref float ___camFacing,
            ref float ___directionalVolumeMult,
            object ___afterburners)
        {
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                // thrustAudio and every Afterburner's own source commonly
                // all live on the same 'nozzle' GameObject -- each is
                // declared as an accepted sibling of every other so the
                // cross-contamination guard (see ApplyCockpitOnlyLowpass)
                // doesn't mistake this mod's own sources sharing a
                // GameObject for sharing with something unrelated (root-
                // caused via [CockpitFilterShareDiag] logging 'nozzle'
                // being skipped). Handled here for both thrustAudio AND
                // every afterburner -- not split between here and
                // JetNozzleAfterburnerPatch -- because Thrust() always
                // calls AudioEffects() before any Afterburner.Run(),
                // guaranteeing this runs whenever an afterburner might, and
                // an Afterburner has no back-reference to its owning
                // JetNozzle to look thrustAudio up the other way around.
                AudioSource[] nozzleSources = _nozzleSourcesCache.GetValue(
                    __instance, _ => BuildNozzleSourceList(___thrustAudio, ___afterburners));
                for (int i = 0; i < nozzleSources.Length; i++)
                {
                    if (nozzleSources[i] != null)
                    {
                        SoundPropagation.ApplyCockpitOnlyLowpass(nozzleSources[i], nozzleSources);
                    }
                }
                return true;
            }

            Vector3 direction = FastMath.NormalizedDirection(
                SceneSingleton<CameraStateManager>.i.transform.GlobalPosition(),
                __instance.transform.GlobalPosition());
            ___camFacing = Vector3.Dot(direction, ___thrustTransform.forward);
            ___directionalVolumeMult = Mathf.Lerp(0.5f, 2f, ___camFacing);

            float volume = ___thrustRatio * ___thrustMaxVolume * ___directionalVolumeMult;
            float dopplerLevel = ___thrustAudio.dopplerLevel > 0f
                ? Mathf.Max(1f - ___camFacing * 2f, 0.01f)
                : ___thrustAudio.dopplerLevel;

            // Keyed by thrustAudio itself, not __instance -- a single
            // JetNozzle also owns one or more independent Afterburner
            // sources (see JetNozzleAfterburnerPatch) that need their own
            // separate tracked clones.
            SoundPropagation.RecordEngineSample(
                ___thrustAudio, ___thrustAudio, ___thrustAudio.pitch, volume, __instance.transform.position, dopplerLevel,
                ___aircraft);

            // This method is the ONLY place that ever wrote to
            // thrustAudio.volume -- now that it's fully replaced, the real
            // source would otherwise sit stuck at whatever volume it last
            // had (likely audible, undelayed, if "Play On Awake" is set).
            ___thrustAudio.volume = 0f;

            return false;
        }

        private static AudioSource[] BuildNozzleSourceList(AudioSource thrustAudio, object afterburnersObj)
        {
            if (!(afterburnersObj is System.Array afterburners) || afterburners.Length == 0)
            {
                return new[] { thrustAudio };
            }
            AudioSource[] result = new AudioSource[afterburners.Length + 1];
            result[0] = thrustAudio;
            for (int i = 0; i < afterburners.Length; i++)
            {
                object afterburner = afterburners.GetValue(i);
                result[i + 1] = afterburner != null ? AfterburnerSourceField.GetValue(afterburner) as AudioSource : null;
            }
            return result;
        }
    }
}
