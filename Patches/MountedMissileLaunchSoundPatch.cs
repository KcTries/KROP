using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // MountedMissile.PlayLaunchSound() -- the "pylon release" thump heard
    // when firing a missile (bombs share the same base Weapon class and
    // release path) -- is a small, self-contained, non-async method, same
    // shape as EjectionSeat.FireEffects(), so fully replacing it here is
    // low-risk. Vanilla explicitly sets bypassListenerEffects=true on this
    // source, which is exactly why it was never touched by anything this
    // mod does -- but this is a real, physical mechanical release event,
    // not a synthesized cockpit UI cue, so it should get the same delay/
    // distance/cockpit-filter treatment as everything else this mod
    // manages rather than staying exempt.
    [HarmonyPatch(typeof(MountedMissile), "PlayLaunchSound")]
    internal static class MountedMissileLaunchSoundPatch
    {
        private static bool Prefix(MountedMissile __instance, AudioClip ___deploySound, float ___deployVolume)
        {
            Transform parent = __instance.transform.parent;

            // Reuse the parent's own GameObject as a scratch host for the
            // settings template, then discard it immediately -- same
            // approach as EjectionSeatFireEffectsPatch, avoids spawning and
            // destroying a whole extra GameObject for something that only
            // ever needs to exist long enough for ScheduleDelayedOneShot to
            // read its settings.
            AudioSource template = parent.gameObject.AddComponent<AudioSource>();
            template.outputAudioMixerGroup = SoundManager.i.EffectsMixer;
            template.clip = ___deploySound;
            template.volume = ___deployVolume;
            template.pitch = Random.Range(0.8f, 1.2f);
            template.spatialBlend = 1f;
            template.dopplerLevel = 0f;
            template.spread = 5f;
            template.maxDistance = 40f;
            template.minDistance = 5f;

            SoundPropagation.ScheduleDelayedOneShot(template, ___deploySound, parent.position);
            Object.Destroy(template);

            return false;
        }
    }
}
