using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // LandingGear's audio comes in two shapes that need different treatment:
    //
    // 1. The latch thump (when the gear finishes locking) is a genuinely
    //    brief one-shot event, same category as the ejection seat rocket
    //    and weapon pylon release -- real, physical, point-in-time sounds,
    //    so it gets the full delay + cockpit-filter treatment via
    //    ScheduleDelayedOneShot, at the reduced Internal Mechanical Sound
    //    Multiplier strength. A frozen emission point for well under a
    //    second of clip is imperceptible even at high speed.
    //
    //    The fold sound (the motor whir WHILE the gear travels) is NOT
    //    handled this way despite being bound in the same original vanilla
    //    method -- see LandingGearFoldSoundPatch below for why routing a
    //    multi-second sound through the same fixed-point model was an
    //    actual bug, not just an unnecessary one.
    //
    // 2. Tire rolling/skid noise is a continuous, physics-driven loop
    //    (LandingGear.FixedUpdate sets its volume/pitch every tick based on
    //    ground speed, compression, and slip) that starts and stops far too
    //    often -- taxiing, bounces, touch-and-go -- to fit the existing
    //    tracked-loop system, which assumes a single start/stop per
    //    lifetime (built for Missile's one-shot flight). Rather than extend
    //    that system for a case this transient, this just applies the same
    //    lightweight direct-on-real-source cockpit filter used for engines'
    //    "system off" path -- no clone, no extra GameObject, cockpit
    //    muffling only, no propagation delay (a rolling/skidding tire is
    //    effectively co-located with the aircraft, same reasoning as the
    //    gun's recoil sound). LandingGear.FixedUpdate already disables
    //    itself entirely once the gear is locked retracted, so this costs
    //    nothing for the vast majority of a flight with the gear up.
    [HarmonyPatch(typeof(LandingGear), "PlayLatchSound")]
    internal static class LandingGearLatchSoundPatch
    {
        private static bool Prefix(LandingGear __instance, AudioClip ___latchSound, float ___latchVolume, AudioSource ___foldSoundSource)
        {
            Vector3 origin = __instance.transform.position;

            AudioSource template = __instance.gameObject.AddComponent<AudioSource>();
            template.outputAudioMixerGroup = SoundManager.i.EffectsMixer;
            template.clip = ___latchSound;
            template.volume = ___latchVolume;
            template.dopplerLevel = 0f;
            template.minDistance = 5f;
            template.maxDistance = 20f;
            template.spatialBlend = 1f;

            SoundPropagation.ScheduleDelayedOneShot(
                template, ___latchSound, origin,
                cockpitMuffleMultiplier: CockpitLowpassConfig.InternalMechanicalMuffleMultiplier);
            Object.Destroy(template);

            if (___foldSoundSource != null)
            {
                ___foldSoundSource.Stop();
                Object.Destroy(___foldSoundSource, 2f);
            }

            return false;
        }
    }

    // Root-caused "landing gear sound gets left behind and fades out with
    // distance": the fold sound (motor whir, running for as long as the
    // gear takes to travel -- several seconds, not a brief thump) was being
    // rerouted through ScheduleDelayedOneShot, which spawns its clone at a
    // FIXED point in space and never moves it again -- correct for a
    // genuinely instantaneous event (a gunshot's sound doesn't follow the
    // shooter after it's fired), completely wrong for something that plays
    // continuously while the emitting aircraft keeps flying. Vanilla's own
    // LandingGear_OnSetGear() already starts foldSoundSource playing
    // correctly on the REAL, live, moving AudioSource before this Postfix
    // ever runs -- the old code then threw that away (Stop() + a frozen-
    // position clone) purely to get the cockpit-filter treatment, a
    // propagation delay that was always ~0 anyway (gear is always within
    // NearFieldNoDelayMeters of the listener). Now left playing on the real
    // source and just filtered in place every tick while it's actually
    // playing, the same lightweight pattern already used for tire noise/
    // airbrake below -- it naturally keeps tracking the aircraft since
    // Unity does that for free on any live, unstopped AudioSource.
    [HarmonyPatch(typeof(LandingGear), "FixedUpdate")]
    internal static class LandingGearFoldSoundPatch
    {
        private static void Postfix(LandingGear __instance, AudioSource ___foldSoundSource, AudioSource ___tireNoiseSound, AudioSource ___tireSkidSound)
        {
            if (___foldSoundSource == null || !___foldSoundSource.isPlaying)
            {
                return;
            }
            SoundPropagation.ApplyCockpitOnlyLowpass(
                ___foldSoundSource,
                LandingGearSiblingCache.Get(__instance, ___tireNoiseSound, ___tireSkidSound, ___foldSoundSource),
                CockpitLowpassConfig.InternalMechanicalMuffleMultiplier);
        }
    }

    [HarmonyPatch(typeof(LandingGear), "FixedUpdate")]
    internal static class LandingGearTireCockpitFilterPatch
    {
        // tireNoiseSound/tireSkidSound/foldSoundSource are all declared as
        // siblings of each other here -- on some aircraft's prefabs two or
        // more of these live on the very same GameObject, which would
        // otherwise make ApplyCockpitOnlyLowpass's cross-contamination
        // guard (see its own comment) mistake "several of MY sources share
        // a GameObject" for "one of my sources shares a GameObject with
        // something unrelated," and skip filtering all of them.
        private static void Postfix(LandingGear __instance, AudioSource ___tireNoiseSound, AudioSource ___tireSkidSound, AudioSource ___foldSoundSource)
        {
            AudioSource[] siblings = LandingGearSiblingCache.Get(__instance, ___tireNoiseSound, ___tireSkidSound, ___foldSoundSource);
            SoundPropagation.ApplyCockpitOnlyLowpass(___tireNoiseSound, siblings);
            SoundPropagation.ApplyCockpitOnlyLowpass(___tireSkidSound, siblings);
        }
    }

    // Shared by both patches above -- all three fields are fixed per
    // LandingGear instance, so the combined sibling list is built once and
    // reused rather than allocating fresh arrays every FixedUpdate() (every
    // tick while any gear is extended/extending/retracting).
    internal static class LandingGearSiblingCache
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LandingGear, AudioSource[]> Cache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<LandingGear, AudioSource[]>();

        internal static AudioSource[] Get(LandingGear gear, AudioSource tireNoiseSound, AudioSource tireSkidSound, AudioSource foldSoundSource)
        {
            return Cache.GetValue(gear, _ => new[] { tireNoiseSound, tireSkidSound, foldSoundSource });
        }
    }
}
