using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // EjectionSeat.FireEffects() is a small, self-contained method: it adds
    // a fresh AudioSource to the seat's own GameObject, hardcodes its 3D
    // sound settings in code (no prefab to copy from), plays the rocket
    // motor's fire sound, and kicks off the ejection particles. Cleanly
    // isolated from the actual ejection physics (FirePhysics(), started
    // separately), so fully replacing it here is low-risk.
    [HarmonyPatch(typeof(EjectionSeat), "FireEffects")]
    internal static class EjectionSeatFireEffectsPatch
    {
        private static bool Prefix(EjectionSeat __instance, AudioClip ___fireSound, ParticleSystem[] ___ejectParticles)
        {
            Vector3 origin = __instance.transform.position;

            // Reuse the seat's own GameObject as a scratch host for the
            // settings template, then discard just this component -- avoids
            // spawning and immediately destroying a whole extra GameObject
            // for what only ever needs to exist for one method call.
            AudioSource template = __instance.gameObject.AddComponent<AudioSource>();
            template.outputAudioMixerGroup = SoundManager.i.EffectsMixer;
            template.bypassListenerEffects = true;
            template.clip = ___fireSound;
            template.volume = 2f;
            template.dopplerLevel = 0f;
            template.minDistance = 50f;
            template.maxDistance = 1000f;
            template.spatialBlend = 1f;
            template.rolloffMode = AudioRolloffMode.Linear;

            SoundPropagation.ScheduleDelayedOneShot(template, ___fireSound, origin);
            Object.Destroy(template);

            for (int i = 0; i < ___ejectParticles.Length; i++)
            {
                ___ejectParticles[i].Play();
            }

            return false;
        }
    }
}
