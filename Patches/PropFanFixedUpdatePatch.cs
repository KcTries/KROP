using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // PropFan sets source.volume/.pitch directly inline at the end of
    // FixedUpdate(), mixed in with thrust/power physics -- no isolated
    // Animate()-style method to Prefix or Postfix here, so a transpiler
    // swaps out the two property setter calls (source.volume = x and
    // source.pitch = y compile to callvirt AudioSource::set_volume/
    // set_pitch, same as any other method call) for our own versions,
    // leaving the surrounding thrust calculation completely untouched.
    //
    // Keyed by the AudioSource itself rather than the owning PropFan --
    // MethodReplacer's replacement signature only gets what the original
    // setter call received (the instance and the new value), not the
    // PropFan instance, and the AudioSource is just as unique a key.
    [HarmonyPatch(typeof(PropFan), "FixedUpdate")]
    internal static class PropFanFixedUpdatePatch
    {
        private static readonly MethodInfo SetVolumeMethod = AccessTools.PropertySetter(typeof(AudioSource), "volume");
        private static readonly MethodInfo SetPitchMethod = AccessTools.PropertySetter(typeof(AudioSource), "pitch");
        private static readonly MethodInfo SetVolumeReplacement = AccessTools.Method(typeof(PropFanFixedUpdatePatch), nameof(SetVolume));
        private static readonly MethodInfo SetPitchReplacement = AccessTools.Method(typeof(PropFanFixedUpdatePatch), nameof(SetPitch));

        // PropFan always sets volume immediately before pitch (see the
        // decompiled source), so pitch's handler has a fresh same-frame
        // volume value to pair it with when it records the combined sample.
        private static readonly Dictionary<AudioSource, float> _pendingVolume = new Dictionary<AudioSource, float>();

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            IEnumerable<CodeInstruction> result = Transpilers.MethodReplacer(instructions, SetVolumeMethod, SetVolumeReplacement);
            return Transpilers.MethodReplacer(result, SetPitchMethod, SetPitchReplacement);
        }

        private static void SetVolume(AudioSource source, float volume)
        {
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                // Falls back to exactly what the replaced call would have
                // done -- this method stands in for the original setter
                // entirely, so disabling delay treatment here means
                // actually performing the vanilla assignment ourselves,
                // not just skipping our own extra behavior.
                source.volume = volume;
                SoundPropagation.ApplyCockpitOnlyLowpass(source);
                return;
            }

            _pendingVolume[source] = volume;
            // The real source's own volume line is now fully replaced by
            // this call (not just observed), so nothing else ever sets it
            // back to something audible -- needed because PropFan never
            // calls Play()/Stop() based on RPM the way the other engine
            // types do, so if this prefab has "Play On Awake" set (as the
            // absence of any explicit Play() call in Awake()/Start()
            // suggests), the real source would otherwise sit there playing
            // undelayed at whatever volume it last happened to have.
            source.volume = 0f;
        }

        private static void SetPitch(AudioSource source, float pitch)
        {
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                source.pitch = pitch;
                return;
            }

            // Removed rather than left in place -- this dictionary was never
            // cleaned up at all before, so every distinct PropFan AudioSource
            // that ever called SetVolume (i.e. every PropFan aircraft ever
            // spawned for the rest of the process's life, destroyed or not)
            // permanently held a slot and a dead-source reference. Since the
            // volume write is always immediately followed by this same
            // frame's pitch write (per the comment above), there's nothing
            // to preserve across frames anyway.
            float volume = _pendingVolume.TryGetValue(source, out float pending) ? pending : source.volume;
            _pendingVolume.Remove(source);
            SoundPropagation.RecordEngineSample(source, source, pitch, volume, source.transform.position, source.dopplerLevel);
        }
    }
}
