using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // GroundVehicle (tanks, trucks, IFVs, SPAAG platforms, etc.) is the
    // single shared class for every ground vehicle in the game, the same
    // way Aircraft is for planes -- but its engine sound (engineIdleSound/
    // engineDriveSound, cross-faded by speed in Update()) was never covered
    // by any of this mod's 8 aircraft engine patches, so ground vehicles
    // got instant, undelayed, unfiltered engine audio regardless of
    // Engine Sound Propagation or the cockpit filter.
    //
    // Update() mixes the audio cross-fade with wheel-rotation visuals
    // (AnimateWheels) and debug-marker code in the same method -- same
    // situation as DuctedFan/ConstantSpeedProp's own Animate() methods, so
    // this uses the same solution: a Postfix that lets the original run
    // completely (visuals, and whichever Play()/Stop() transitions it
    // decided to make -- harmless since both sources end up muted
    // regardless of play state), then captures whatever it just set on
    // both real sources and mutes them.
    [HarmonyPatch(typeof(GroundVehicle), "Update")]
    internal static class GroundVehicleEngineSoundPatch
    {
        // idle/drive are fixed fields on a given vehicle -- cached per
        // instance rather than allocating a fresh sibling array twice every
        // Update() (every tick while Engine Sound Propagation is off, the
        // default, for every ground vehicle on the map).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GroundVehicle, AudioSource[]> _engineSourcesCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<GroundVehicle, AudioSource[]>();

        private static void Postfix(GroundVehicle __instance, AudioSource ___engineIdleSound, AudioSource ___engineDriveSound)
        {
            if (___engineIdleSound == null || ___engineDriveSound == null)
            {
                return;
            }

            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                // Declared as siblings (see ApplyCockpitOnlyLowpass) since
                // idle/drive commonly share one GameObject on a vehicle's
                // prefab -- otherwise the cross-contamination guard would
                // mistake that for sharing with something unrelated and
                // skip filtering both, same latent bug LandingGear's tire
                // sounds had. The same 2-element list (including the source
                // being filtered itself) is reused for both calls -- the
                // guard already skips the "is this me" case, so there's no
                // need for two different single-element lists.
                AudioSource[] engineSources = _engineSourcesCache.GetValue(
                    __instance, _ => new[] { ___engineIdleSound, ___engineDriveSound });
                SoundPropagation.ApplyCockpitOnlyLowpass(___engineIdleSound, engineSources);
                SoundPropagation.ApplyCockpitOnlyLowpass(___engineDriveSound, engineSources);
                return;
            }

            Vector3 position = __instance.transform.position;

            // Both sources recorded unconditionally every call, same as
            // JetNozzle's Afterburner -- whichever one the cross-fade has
            // faded to near-zero volume this frame just tracks as a quiet
            // clone, no separate "which one is active" branching needed.
            SoundPropagation.RecordEngineSample(
                ___engineIdleSound, ___engineIdleSound, ___engineIdleSound.pitch, ___engineIdleSound.volume,
                position, ___engineIdleSound.dopplerLevel, __instance);
            ___engineIdleSound.volume = 0f;

            SoundPropagation.RecordEngineSample(
                ___engineDriveSound, ___engineDriveSound, ___engineDriveSound.pitch, ___engineDriveSound.volume,
                position, ___engineDriveSound.dopplerLevel, __instance);
            ___engineDriveSound.volume = 0f;
        }
    }
}
