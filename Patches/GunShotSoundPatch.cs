using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Gun.ShotSound() is the single choke point where a gun's audible bang
    // actually gets played (called from SpawnBullet() alongside muzzle
    // flash/recoil physics, which stay instant and untouched -- only the
    // sound itself should lag behind at range). This Prefix fully replaces
    // it (returns false) rather than patching around it, since Harmony can't
    // intercept a single call *inside* a method, only the whole method.
    //
    // All three of the gun's audible pieces get delayed here: the discrete
    // semi/burst-fire bang, the spool-up transition on continuous-fire
    // weapons, and (via SoundPropagation's loop tracking) the sustained
    // loop sound itself -- sources[1] is never actually played on the real
    // (moving, attached-to-the-aircraft) AudioSource at all anymore; a
    // tracked clone in SoundPropagation drives the whole loop's lifecycle
    // instead, so there's only ever one audible copy of it.
    [HarmonyPatch(typeof(Gun), "ShotSound")]
    internal static class GunShotSoundPatch
    {
        private static bool Prefix(
            Gun __instance,
            AudioSource[] ___sources,
            AudioClip[] ___fireSounds,
            AudioClip ___fireStart,
            AudioClip ___fireEnd,
            AudioSource ___recoilSound,
            float ___pitch,
            float ___pitchVariation,
            bool ___heatEnabled,
            float ___lastFired,
            float ___fireInterval,
            bool ___modifyPitch,
            float ___pitchClimbRate,
            float ___startPitch)
        {
            if (__instance.attachedUnit.displayDetail < 1f)
            {
                return false;
            }

            Vector3 origin = __instance.transform.position;

            if (___sources.Length < 2)
            {
                ___sources[0].pitch = ___pitch + Random.value * ___pitchVariation - Random.value * ___pitchVariation;
                AudioClip clip = ___fireSounds[Random.Range(0, ___fireSounds.Length)];
                SoundPropagation.ScheduleDelayedOneShot(___sources[0], clip, origin);

                if (___recoilSound != null)
                {
                    if (!___heatEnabled)
                    {
                        ___recoilSound.pitch = Random.Range(0.95f, 1.05f);
                    }
                    // Mechanical recoil feedback, not the gunshot's own sound
                    // -- left instant like the original rather than routed
                    // through the speed-of-sound delay (it's effectively
                    // co-located with the platform, not a separate emission
                    // point). It's still an external, non-cockpit sound
                    // though, so cockpit-view muffling should still apply --
                    // this was the actual reason the 57mm autocannon kept
                    // sounding unfiltered even after fixing bypassEffects on
                    // the delayed-one-shot path: that gun's audible signature
                    // turned out to be dominated by this recoil/action sound,
                    // which never went through any filter at all, on any gun.
                    // ___sources is declared as an accepted sibling set here
                    // (see ApplyCockpitOnlyLowpass) -- on some guns (e.g. the
                    // Ibis's 40mm GMG turret, confirmed via
                    // [CockpitFilterShareDiag]) recoilSound shares its
                    // GameObject with the gun's own bang/loop sources, but
                    // those never actually play on the real source at all
                    // once this Prefix fully replaces ShotSound() (line 102
                    // always returns false), so co-hosting a filter there is
                    // harmless.
                    SoundPropagation.ApplyCockpitOnlyLowpass(___recoilSound, ___sources);
                    ___recoilSound.Play();
                }
            }
            else
            {
                // NotifyLoopActive's own dictionary insert is the single
                // source of truth for "is this a brand-new loop" -- it's
                // called unconditionally here (not gated on the vanilla
                // gun's own lastFired/fireInterval fields first) so the
                // check-and-register happens atomically in one call. A
                // separate pre-check using those fields raced: right after
                // a loop's cleanup grace period expires, a quick trigger
                // re-press can call this Prefix two or three times back to
                // back while lastFired still reflects the *previous* burst,
                // and each call would independently conclude "no loop yet"
                // and schedule its own fireStart -- heard as the spool-up
                // bang repeating.
                bool isNewLoop = SoundPropagation.NotifyLoopActive(
                    __instance, ___sources[0], ___sources[1].clip, ___fireEnd, origin,
                    ___modifyPitch, ___pitch, ___startPitch, ___pitchClimbRate);
                if (isNewLoop)
                {
                    if (VerboseLoggingConfig.Enabled.Value)
                    {
                        SoundPropagation.Log.LogInfo(
                            $"[GunLoopDiag] fireStart scheduled for '{__instance.GetInstanceID()}' at t={Time.timeSinceLevelLoad:F2} "
                            + $"(lastFired={___lastFired:F2} fireInterval={___fireInterval:F3})");
                    }
                    SoundPropagation.ScheduleDelayedOneShot(___sources[0], ___fireStart, origin);
                }
            }

            return false;
        }
    }
}
