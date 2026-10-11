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
    //
    // Fireball() is also called again by RuptureEffects() on a tank that's
    // already burning, where it spawns nothing (the fireball already
    // exists). Only a call that actually spawned a NEW fireball is handled
    // -- otherwise the existing fireball's sounds were stopped mid-play and
    // the ignition replayed. Same before/after check as
    // FuelTankFireballVariantPatch.
    [HarmonyPatch(typeof(FuelTank), "Fireball")]
    internal static class FuelTankFireballPatch
    {
        // Single-threaded, Prefix/Postfix always run back to back for the
        // same call.
        private static bool _hadFireballSpawn;

        private static void Prefix(GameObject ___fireballSpawn)
        {
            _hadFireballSpawn = ___fireballSpawn != null;
        }

        private static void Postfix(GameObject ___fireballSpawn)
        {
            if (_hadFireballSpawn || ___fireballSpawn == null)
            {
                return;
            }

            AudioSource[] sources = ___fireballSpawn.GetComponentsInChildren<AudioSource>(includeInactive: true);
            foreach (AudioSource source in sources)
            {
                // Only take over what vanilla actually started -- a source
                // that isn't playing (inactive object, or no Play On Awake)
                // was never going to be heard, so scheduling it would add a
                // sound vanilla doesn't have.
                if (source.clip == null || !(source.isPlaying || (source.playOnAwake && source.isActiveAndEnabled)))
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
