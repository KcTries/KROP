using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Explosions (blast damage, bomb/missile impacts, etc.) go through a
    // completely separate vanilla system -- ExplosionAudioManager -- never
    // touched by anything else this mod does. It already has its own
    // speed-of-sound delay simulation (ManagedExplosion.InRange literally
    // does "propagation += 340f * Time.deltaTime") and its own distance-
    // based lowpass (set in Play(), right before this Postfix runs), so
    // there's no need to re-derive or duplicate any of that -- just layer
    // the cockpit filter on top of what vanilla already correctly computed.
    //
    // ManagedExplosion is a PUBLIC class nested inside a PUBLIC class, so
    // this uses the normal [HarmonyPatch] attribute directly, no
    // AccessTools.Inner needed.
    [HarmonyPatch(typeof(ExplosionAudioManager.ManagedExplosion), nameof(ExplosionAudioManager.ManagedExplosion.Play))]
    internal static class ExplosionAudioCockpitFilterPatch
    {
        private static void Postfix(AudioSource ___audioSource, AudioLowPassFilter ___filter)
        {
            if (___audioSource == null || ___filter == null)
            {
                return;
            }

            // Some explosion sources (rocket warhead detonations, confirmed
            // by testing -- possibly others) ship with bypassEffects=true on
            // their own AudioSource. That flag makes Unity silently ignore
            // any filter component on the same GameObject, including
            // ___filter itself and the highpass added below, no matter what
            // their cutoff is set to. This operates on the real, live
            // explosion source (never cloned), so it has to be forced off
            // here directly rather than relying on CopyAudioSourceSettings'
            // own copy of this mod's clones elsewhere.
            ___audioSource.bypassEffects = false;

            // Merge cockpit muffling on top of vanilla's own just-computed
            // distance cutoff -- whichever wants MORE cut wins, same
            // relationship used everywhere else this mod merges cockpit
            // cutoff with an existing one.
            ___filter.cutoffFrequency = Mathf.Min(___filter.cutoffFrequency, SoundPropagation.ComputeCockpitLowpassCutoffOnly());

            // Vanilla never added a highpass here at all -- add one
            // (lazily, cached via the component itself) so explosions get
            // the same boxy/telephone-like cockpit treatment as everything
            // else this mod manages instead of just a lowpass.
            AudioHighPassFilter highpassFilter = ___audioSource.GetComponent<AudioHighPassFilter>();
            if (highpassFilter == null)
            {
                highpassFilter = ___audioSource.gameObject.AddComponent<AudioHighPassFilter>();
            }
            highpassFilter.cutoffFrequency = SoundPropagation.ComputeCockpitHighpassCutoffHz();
        }
    }
}
