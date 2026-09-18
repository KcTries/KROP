using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // FuelTank.Fireball() instantiates the "fireball" prefab when a
    // ruptured, on-fire fuel tank ignites -- a crash or major structural
    // failure. Nothing named "Ignition" turned up as a C# class anywhere in
    // the decompiled assembly, so the ignition sound the user found via
    // UnityExplorer is almost certainly just a plain Unity AudioSource on a
    // child GameObject named "Ignition" inside the fireball prefab, likely
    // set to Play On Awake rather than driven by any script.
    //
    // A Postfix (not a full replace) is used here on purpose -- Fireball()
    // also does part.AddHostedParticles() bookkeeping that shouldn't be
    // touched, so the original runs completely unchanged and this only
    // reaches in afterward to intercept whatever AudioSource(s) the newly
    // spawned prefab is carrying. "Play On Awake" AudioSources start during
    // Instantiate()'s own synchronous Awake/OnEnable pass, before this
    // Postfix even runs, so a fraction of a second may already have played
    // before Stop() catches it here -- imperceptibly short in practice.
    [HarmonyPatch(typeof(FuelTank), "Fireball")]
    internal static class FuelTankFireballPatch
    {
        private static void Postfix(GameObject ___fireballSpawn)
        {
            if (___fireballSpawn == null)
            {
                return;
            }

            AudioSource[] sources = ___fireballSpawn.GetComponentsInChildren<AudioSource>(includeInactive: true);
            foreach (AudioSource source in sources)
            {
                if (source.clip == null)
                {
                    continue;
                }

                Vector3 position = source.transform.position;
                AudioClip clip = source.clip;
                source.Stop();
                SoundPropagation.ScheduleDelayedOneShot(source, clip, position);
            }
        }
    }
}
