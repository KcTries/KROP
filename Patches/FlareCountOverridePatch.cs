using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // FlareEjector.ammo (from Countermeasure) is baked per-aircraft in the
    // prefab, same as ejectionVelocity -- Awake() copies it into maxAmmo,
    // which Rearm() resets to later. Vanilla's baked values don't always
    // match the number of flare dispenser ports actually visible on the
    // aircraft's own model (counted directly in-game per aircraft). This
    // raises maxAmmo/ammo to the counted value where vanilla's own number
    // is lower, and leaves it alone where vanilla's is already higher --
    // a floor, not a hard override, so no aircraft ends up with fewer
    // flares than vanilla already gave it. Flares only -- the game has no
    // chaff mechanic, so ChaffEjector is untouched.
    [HarmonyPatch(typeof(FlareEjector), "Awake")]
    internal static class FlareCountOverridePatch
    {
        // jsonKey -> counted flare port count, confirmed against each
        // aircraft's actual in-game model.
        private static readonly Dictionary<string, int> FlareCountOverrides = new Dictionary<string, int>
        {
            { "CAS1", 180 },
            { "UtilityHelo1", 80 },
            { "Multirole1", 112 },
        };

        private static void Postfix(FlareEjector __instance, ref int ___maxAmmo)
        {
            if (!FlareCountConfig.UseActualFlareValues.Value)
            {
                return;
            }

            Aircraft aircraft = __instance.GetComponentInParent<Aircraft>();
            if (aircraft == null || aircraft.definition == null)
            {
                SoundPropagation.Log.LogWarning(
                    $"[FlareCountDiag] FlareEjector on '{__instance.name}' has no resolvable Aircraft/definition at Awake -- skipping flare count override.");
                return;
            }

            if (!FlareCountOverrides.TryGetValue(aircraft.definition.jsonKey, out int overrideCount))
            {
                return;
            }

            if (___maxAmmo >= overrideCount)
            {
                return;
            }

            int original = ___maxAmmo;
            ___maxAmmo = overrideCount;
            __instance.ammo = overrideCount;
            SoundPropagation.Log.LogInfo(
                $"[FlareCountDiag] '{aircraft.definition.jsonKey}' flare count raised {original} -> {overrideCount} "
                + "(matches counted ejector ports)");
        }
    }
}
