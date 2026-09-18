using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Missile.Motor is a PRIVATE class nested inside a PUBLIC class (same
    // situation as SonicBoomManager.ManagedSonicBoom and JetNozzle.Afterburner)
    // -- can't be named with typeof(), so it's resolved via AccessTools.Inner
    // and patched manually from Plugin.Awake().
    //
    // The motor's own audioSources[] play once on ignition (Activate()) and
    // stop once on burnout or destruction (Burnout(), also called from
    // Destruct()) -- unlike Gun's loop, there's no re-triggering to handle,
    // so this uses SoundPropagation's simpler event-driven TrackedLoop
    // instead of the fireInterval-polled one. All three patched methods are
    // Postfixes: Activate()/Burnout() also drive particles/lights/trails
    // that shouldn't be touched, and Thrust() drives the actual flight
    // physics -- none of that is reimplemented here, only observed
    // afterward to drive the delayed clone(s) alongside it.
    internal static class MissileMotorPatch
    {
        internal static void ApplyManualPatch(Harmony harmony)
        {
            Type motorType = AccessTools.Inner(typeof(Missile), "Motor");

            harmony.Patch(
                AccessTools.Method(motorType, "Activate"),
                postfix: new HarmonyMethod(typeof(MissileMotorPatch), nameof(ActivatePostfix)));
            harmony.Patch(
                AccessTools.Method(motorType, "Burnout"),
                postfix: new HarmonyMethod(typeof(MissileMotorPatch), nameof(BurnoutPostfix)));
            harmony.Patch(
                AccessTools.Method(motorType, "Thrust"),
                postfix: new HarmonyMethod(typeof(MissileMotorPatch), nameof(ThrustPostfix)));
        }

        private static void ActivatePostfix(Missile missile, AudioSource[] ___audioSources)
        {
            // Missile : Unit, and SonicBoomManager's Mach-cone detection is
            // already fully generic over Unit -- vanilla just never
            // registers anything but aircraft (from JetNozzle.Awake).
            // Registering here gives missiles the same boom-on-supersonic
            // behavior for free; SonicBoomManagePatch tags it as a missile
            // boom (pitched up, tighter double-crack) at construction time.
            // RegisterUnit no-ops if this missile is already registered.
            SonicBoomManager.RegisterUnit(missile);

            if (___audioSources == null)
            {
                return;
            }

            Vector3 position = missile.transform.position;
            foreach (AudioSource source in ___audioSources)
            {
                if (source == null)
                {
                    continue;
                }
                // The real source is already playing (the original Activate()
                // just ran) -- stop it immediately so the delayed clone is
                // the only audible copy, same as every other tracked loop.
                source.Stop();
                SoundPropagation.StartTrackedLoop(source, position);
            }
        }

        private static void BurnoutPostfix(bool forceStopEffects, AudioSource[] ___audioSources)
        {
            if (___audioSources == null)
            {
                return;
            }

            foreach (AudioSource source in ___audioSources)
            {
                if (source == null)
                {
                    continue;
                }
                // Mirrors the original's own condition for which sources
                // actually stop here (loops always stop; one-shots stopped
                // early too, but only when force-stopping). Burnout() only
                // receives forceStopEffects, not the owning Missile, but
                // the AudioSource's own transform already reflects wherever
                // the missile currently is (it's mounted on it), so that
                // works just as well for the stop-delay calculation.
                if (forceStopEffects || source.loop)
                {
                    SoundPropagation.StopTrackedLoop(source, source.transform.position);
                }
            }
        }

        private static void ThrustPostfix(Missile missile, AudioSource[] ___audioSources)
        {
            if (___audioSources == null)
            {
                return;
            }

            Vector3 position = missile.transform.position;
            foreach (AudioSource source in ___audioSources)
            {
                if (source != null)
                {
                    SoundPropagation.UpdateTrackedLoopPosition(source, position);
                }
            }
        }
    }
}
