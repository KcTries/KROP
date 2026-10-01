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
            // cutoff with an existing one. ___filter is vanilla's own
            // AudioLowPassFilter, always present on this source regardless
            // of anything this mod does, so there's no creation to defer
            // here -- just merging into an already-existing, already-live
            // filter component.
            ___filter.cutoffFrequency = Mathf.Min(___filter.cutoffFrequency, SoundPropagation.ComputeCockpitLowpassCutoffOnly());

            // Vanilla never added a highpass here at all -- unlike the
            // lowpass above, this one is entirely this mod's own addition,
            // so it's subject to the same root-caused bug as
            // ApplyCockpitOnlyLowpass: merely HAVING an AudioHighPassFilter
            // attached (even at the "fully open" 10Hz floor) was found to
            // itself produce an audible artifact, independent of any value
            // written to it. EnsureHighpassFilter only creates it once
            // cockpit highpass muffling would do something real -- an
            // explosion heard outside cockpit view (the overwhelming
            // majority of them, since a firing player's explosions are
            // mostly seen third-person or from a distance) now never gets
            // this component attached at all. GetComponent every call
            // (rather than caching) since this Postfix has no per-instance
            // state to cache it in -- ManagedExplosion instances are
            // pooled/reused, and cheap enough to re-query regardless.
            AudioHighPassFilter highpassFilter = ___audioSource.GetComponent<AudioHighPassFilter>();
            SoundPropagation.EnsureHighpassFilter(
                ___audioSource.gameObject, ref highpassFilter, SoundPropagation.ComputeCockpitHighpassCutoffHz());
        }
    }
}
