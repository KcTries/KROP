using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Missile.flightSound is a SEPARATE AudioSource from Motor's own
    // audioSources (already handled by MissileMotorPatch) -- the missile's
    // in-flight "whoosh", continuously pitched/volumed by live speed in
    // FixedUpdate(), then repurposed wholesale at detonation to play
    // nearbyDetonationClip once instead (the real "missile impact" sound).
    // Neither path went through this mod's delay/lowpass/cockpit-filter
    // system at all before these three patches.
    //
    // Postfix, not Prefix-replace, on all three -- StartMissile()/
    // FixedUpdate()/the detonate RPC all do a lot of other launch/physics/
    // network work that shouldn't be reimplemented here. Let vanilla run
    // completely, then take over flightSound the same way
    // MissileMotorPatch.ActivatePostfix already takes over Motor's own
    // sources.
    [HarmonyPatch(typeof(Missile), "StartMissile")]
    internal static class MissileStartFlightSoundPatch
    {
        private static void Postfix(Missile __instance, AudioSource ___flightSound)
        {
            if (___flightSound == null)
            {
                return;
            }
            ___flightSound.Stop();
            SoundPropagation.StartTrackedLoop(___flightSound, __instance.transform.position);
        }
    }

    [HarmonyPatch(typeof(Missile), "FixedUpdate")]
    internal static class MissileFlightSoundUpdatePatch
    {
        // Nothing here touches any of the flight/guidance physics
        // FixedUpdate() also runs -- only reads back whatever vanilla's own
        // speed-based formula just set on flightSound.pitch/.volume and
        // feeds it to the tracked clone.
        private static void Postfix(Missile __instance, AudioSource ___flightSound)
        {
            if (___flightSound == null)
            {
                return;
            }
            SoundPropagation.UpdateTrackedLoopPosition(___flightSound, __instance.transform.position);
            SoundPropagation.UpdateTrackedLoopAudio(___flightSound, ___flightSound.pitch, ___flightSound.volume);
        }
    }

    [HarmonyPatch(typeof(Missile), "UserCode_RpcDetonate_897349600")]
    internal static class MissileDetonateSoundPatch
    {
        // By the time this runs, the original already stopped flightSound,
        // swapped its clip to nearbyDetonationClip, and Play()'d it
        // instantly (undelayed). Take the flight loop down -- it's done
        // being a continuous whoosh -- and reschedule the impact sound
        // properly delayed instead of leaving the instant vanilla playback
        // as the audible copy.
        private static void Postfix(Missile __instance, AudioSource ___flightSound, AudioClip ___nearbyDetonationClip)
        {
            if (___flightSound == null)
            {
                return;
            }
            Vector3 position = ___flightSound.transform.position;
            SoundPropagation.StopTrackedLoop(___flightSound, position);
            if (___nearbyDetonationClip != null && ___flightSound.clip == ___nearbyDetonationClip)
            {
                ___flightSound.Stop();
                SoundPropagation.ScheduleDelayedOneShot(___flightSound, ___nearbyDetonationClip, position);
            }
        }
    }
}
