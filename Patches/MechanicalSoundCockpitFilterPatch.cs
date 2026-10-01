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

    // RotorShaft.AnimateRotor() is the private method that sets
    // rotorSource.pitch/.volume every Update() -- purely presentational,
    // same shape as TurbineEngine.Animate(), so a Postfix here is the
    // right hook point. No propagation delay needed (same reasoning as
    // tire noise/airbrake -- the rotor disc is directly overhead/co-located
    // with the aircraft, not a distant source), so this uses the same
    // lightweight direct-on-real-source path rather than a tracked clone.
    // Left at full cockpit-filter strength (no muffleMultiplier override)
    // rather than the Internal Mechanical Sound Multiplier used above --
    // rotor chop is the iconic, genuinely-loud aerodynamic blade noise a
    // real helicopter cockpit doesn't meaningfully shield you from, closer
    // in character to engine/tire noise than to a hinge or gearbox heard
    // through the airframe. rotorSource is a serialized field that always
    // exists (unlike swingSource, never null-checked before use in
    // AnimateRotor itself), so no null guard needed here either --
    // ApplyCockpitOnlyLowpass already no-ops safely on a null source
    // regardless. Vanilla's own RotorShaft.SetInteriorSounds(bool) swaps
    // rotorSource.clip between separate interior/exterior recordings on
    // its own, entirely independent of this -- this only layers cockpit-
    // only filtering on top of whichever clip is currently assigned,
    // the same "don't re-derive what vanilla already does, just filter its
    // output" approach used for ExplosionAudioManager elsewhere in this
    // mod. RotorShaft.RotorStrike()'s separate one-shot strikeSource is
    // untouched -- vanilla explicitly sets bypassEffects=true on it, a
    // deliberate choice (likely so a rotor-strike damage cue always reads
    // clearly regardless of view) this isn't overriding without being asked.
    [HarmonyPatch(typeof(RotorShaft), "AnimateRotor")]
    internal static class RotorShaftCockpitFilterPatch
    {
        private static void Postfix(AudioSource ___rotorSource)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(___rotorSource);
        }
    }
}
