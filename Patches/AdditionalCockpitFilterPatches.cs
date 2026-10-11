using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Cockpit-view muffling for sounds that originally had none -- all
    // external to the cockpit (engines, airframe, weapons, ships, ambience),
    // so all of them belong behind it. Same direct-on-real-source path as
    // tire noise/airbrake: no propagation delay, just the cockpit filter.
    // Where the game updates a sound every frame, the filter is applied in
    // that same method; where the game only ever starts it,
    // SoundPropagation.KeepCockpitFiltered keeps it filtered while it plays.

    // Turbine startup spool (helicopter turboshafts and other
    // TurbineEngines) -- played from TurbineEngine.Update() at low RPM.
    // Same strength as the running turbine sound
    // (TurbineEngineAnimatePatch), which it plays alongside.
    [HarmonyPatch(typeof(TurbineEngine), "Update")]
    internal static class TurbineStartupCockpitFilterPatch
    {
        private static void Postfix(AudioSource ___startupAudio)
        {
            if (___startupAudio != null && ___startupAudio.isPlaying)
            {
                SoundPropagation.ApplyCockpitOnlyLowpass(
                    ___startupAudio, muffleMultiplier: SoundPropagation.GetHelicopterTurbineMuffleMultiplier());
            }
        }
    }

    // Engine fire loop -- started in Start(), faded in Update().
    [HarmonyPatch(typeof(TurbineFireSound), "Update")]
    internal static class EngineFireCockpitFilterPatch
    {
        private static void Postfix(TurbineFireSound __instance)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(__instance.source);
        }
    }

    // Split control surfaces (split airbrakes/decelerons) -- splitSource
    // is created and looped lazily from Aero() the first time the surface
    // splits; null (a no-op here) on every surface that never does.
    [HarmonyPatch(typeof(ControlSurface), nameof(ControlSurface.Aero))]
    internal static class SplitAirbrakeCockpitFilterPatch
    {
        private static void Postfix(AudioSource ___splitSource)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(___splitSource);
        }
    }

    // Belly/airframe scrape loop -- created and kept playing from
    // ThrowSparks(), called every physics tick the airframe is scraping.
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.ThrowSparks))]
    internal static class BellyScrapeCockpitFilterPatch
    {
        private static void Postfix(AudioSource ___scrapeSource)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(___scrapeSource);
        }
    }

    // Ground/ship missile launchers -- launchSound is only started from
    // Fire(), so it's handed to KeepCockpitFiltered just before it plays.
    [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.Fire))]
    internal static class MissileLauncherLaunchCockpitFilterPatch
    {
        private static void Prefix(AudioSource ___launchSound)
        {
            SoundPropagation.KeepCockpitFiltered(___launchSound);
        }
    }

    // VLS booster burn -- started in Activate(), FixedUpdate() runs for the
    // rest of the booster's flight.
    [HarmonyPatch(typeof(VLSBooster), "FixedUpdate")]
    internal static class VLSBoosterCockpitFilterPatch
    {
        private static void Postfix(AudioSource[] ___audioSources)
        {
            if (___audioSources == null)
            {
                return;
            }
            for (int i = 0; i < ___audioSources.Length; i++)
            {
                AudioSource source = ___audioSources[i];
                if (source != null && source.isPlaying)
                {
                    SoundPropagation.ApplyCockpitOnlyLowpass(source, ___audioSources);
                }
            }
        }
    }

    // Laser fire start/loop/stop -- all three sources live on the laser's
    // own GameObject and are driven every frame from LaserSound().
    [HarmonyPatch(typeof(Laser), "LaserSound")]
    internal static class LaserCockpitFilterPatch
    {
        private static void Postfix(AudioSource[] ___sources)
        {
            if (___sources == null)
            {
                return;
            }
            for (int i = 0; i < ___sources.Length; i++)
            {
                if (___sources[i] != null && ___sources[i].isPlaying)
                {
                    SoundPropagation.ApplyCockpitOnlyLowpass(___sources[i], ___sources);
                }
            }
        }
    }

    // Ship engine and thrust loops, updated from ShipPropulsion.FixedUpdate().
    [HarmonyPatch(typeof(ShipPropulsion), "FixedUpdate")]
    internal static class ShipEngineCockpitFilterPatch
    {
        private static void Postfix(AudioSource ___engineSound, AudioSource ___thrustSound)
        {
            if (___engineSound != null && ___engineSound.isPlaying)
            {
                SoundPropagation.ApplyCockpitOnlyLowpass(___engineSound);
            }
            if (___thrustSound != null && ___thrustSound.isPlaying)
            {
                SoundPropagation.ApplyCockpitOnlyLowpass(___thrustSound);
            }
        }
    }

    // Ambient sounds placed around the world -- started once in Start()
    // and never touched again.
    [HarmonyPatch(typeof(RandomSound), "Start")]
    internal static class AmbientSoundCockpitFilterPatch
    {
        private static void Postfix(RandomSound __instance)
        {
            SoundPropagation.KeepCockpitFiltered(__instance.source);
        }
    }

    // Escape capsule launch -- CreateLaunchSound() adds a temporary source
    // (destroyed after 10s) and plays it; found again here by its clip.
    [HarmonyPatch(typeof(EscapeCapsule), "CreateLaunchSound")]
    internal static class EscapeCapsuleCockpitFilterPatch
    {
        private static void Postfix(EscapeCapsule __instance, AudioClip ___launchSound)
        {
            foreach (AudioSource source in __instance.GetComponents<AudioSource>())
            {
                if (source.clip == ___launchSound && source.isPlaying)
                {
                    SoundPropagation.KeepCockpitFiltered(source);
                }
            }
        }
    }
}
