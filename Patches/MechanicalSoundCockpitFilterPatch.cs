using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // BayDoor's own Update() re-triggers doorAudioSource.Play() directly
    // (on a clip-change check) whenever it's actively opening or closing,
    // and disables itself once fully closed -- same "continuous while
    // active, cheap when idle" shape as tire noise/airbrake, so this gets
    // the same lightweight direct-on-real-source filter, no clone or
    // propagation delay, just applied every tick this component is
    // actually enabled. At the reduced Internal Mechanical Sound
    // Multiplier strength -- a bay door hinge is a moving mechanism on the
    // aircraft itself, not outside air rushing past a sealed hull.
    [HarmonyPatch(typeof(BayDoor), "Update")]
    internal static class BayDoorCockpitFilterPatch
    {
        private static void Postfix(AudioSource ___doorAudioSource)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(
                ___doorAudioSource, muffleMultiplier: CockpitLowpassConfig.InternalMechanicalMuffleMultiplier);
        }
    }

    // SwingWingController's swingSource is a continuous loop, lazily
    // created the first time the wings actually move and started/stopped
    // by FixedUpdate's own Audio() call far too often (every sweep-rate
    // change) to fit the tracked-loop system -- same reasoning as tire
    // noise/airbrake again. swingSource is null until the wings first
    // move; ApplyCockpitOnlyLowpass already no-ops on a null source.
    [HarmonyPatch(typeof(SwingWingController), "FixedUpdate")]
    internal static class SwingWingCockpitFilterPatch
    {
        private static void Postfix(AudioSource ___swingSource)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(
                ___swingSource, muffleMultiplier: CockpitLowpassConfig.InternalMechanicalMuffleMultiplier);
        }
    }
}
