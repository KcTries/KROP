using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Same real high-altitude contrail as ContrailEffectPatch (jets/props),
    // just anchored to an actively-burning missile rocket motor instead of
    // an aircraft engine. Reuses ContrailEffectPatch's EngineState/
    // CreateContrail/DriveContrail/StopEmitting wholesale -- none of that
    // logic actually references Aircraft-specific types, it only ever
    // needed a position and a velocity.
    //
    // Missile.Motor is a PRIVATE class nested inside a PUBLIC class (same
    // situation MissileMotorPatch already deals with for the motor's own
    // audio) -- can't be named with typeof(), resolved via AccessTools.Inner
    // and patched manually from Plugin.Awake(). Thrust(Missile missile) is
    // only called while the motor is actively burning (confirmed by
    // MissileMotorPatch's own ThrustPostfix, which already reads this same
    // real parameter) -- once burnout happens, the game simply stops
    // calling it, so there's no separate "motor stopped" signal to handle
    // here: emission just naturally stops the moment Thrust() does, and the
    // already-emitted contrail fades out on its own normal schedule.
    internal static class MissileContrailPatch
    {
        private static readonly ConditionalWeakTable<Missile, ContrailEffectPatch.EngineState> _missileStates =
            new ConditionalWeakTable<Missile, ContrailEffectPatch.EngineState>();

        internal static void ApplyManualPatch(Harmony harmony)
        {
            Type motorType = AccessTools.Inner(typeof(Missile), "Motor");
            harmony.Patch(
                AccessTools.Method(motorType, "Thrust"),
                postfix: new HarmonyMethod(typeof(MissileContrailPatch), nameof(ThrustPostfix)));
        }

        private static void ThrustPostfix(Missile missile)
        {
            if (!ContrailConfig.Enabled.Value || !ContrailConfig.MissileContrailsEnabled.Value || GameManager.IsHeadless
                || missile == null || missile.rb == null)
            {
                return;
            }

            float altitude = missile.transform.position.y - Datum.LocalSeaY;
            ContrailEffectPatch.EngineState state = _missileStates.GetValue(
                missile, _ => new ContrailEffectPatch.EngineState());

            if (altitude < ContrailEffectPatch.MinAltitudeMeters || altitude > ContrailEffectPatch.MaxAltitudeMeters)
            {
                ContrailEffectPatch.StopEmitting(state);
                return;
            }

            ContrailEffectPatch.DriveContrail(state, missile.transform.position, missile.rb.velocity);
        }
    }
}
