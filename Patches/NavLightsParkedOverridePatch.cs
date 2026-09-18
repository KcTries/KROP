using System;
using System.Reflection;
using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // NavLights.NavLight is a PRIVATE class nested inside a PUBLIC class
    // (same situation as JetNozzle.Afterburner/SonicBoomManager.
    // ManagedSonicBoom) -- can't be named with typeof(), so it's resolved
    // via AccessTools.Inner and patched manually from Plugin.Awake()
    // instead of the normal [HarmonyPatch(Type, string)] attribute.
    //
    // NavLight.Toggle(bool enabled) is the only place that actually
    // applies a light's on/off state -- called both from gear-state
    // changes and the player's manual nav-light toggle. Its own
    // ControlledState enum only supports Never/Auto/ForceOn, with no
    // "ForceOff" option, so there's no way to guarantee lights stay off
    // through the game's own public API.
    //
    // A parked aircraft already has its gear down before a pilot ever
    // reaches the Parked state (it spawns that way), so the gear-extend
    // event that turns lights on fires once, early, and never fires again
    // while the aircraft just sits there -- a Prefix that only reacts to
    // Toggle calls never gets a chance to intercept that one call if the
    // pilot isn't in the Parked state yet at that exact moment, and
    // nothing re-invokes Toggle afterward for it to catch. ForceAllOff is
    // an active push, called directly from PilotParkedStatePatch the
    // moment a pilot enters Parked, that invokes each NavLight's own
    // Toggle(false) via reflection immediately rather than waiting to
    // intercept a future call. The Prefix stays as a backstop for any
    // Toggle call that DOES happen while parked (e.g. someone manually
    // toggling nav lights on a parked aircraft).
    internal static class NavLightsParkedOverridePatch
    {
        private static MethodInfo _toggleMethod;
        private static FieldInfo _navLightsArrayField;

        internal static void ApplyManualPatch(Harmony harmony)
        {
            Type navLightType = AccessTools.Inner(typeof(NavLights), "NavLight");
            _toggleMethod = AccessTools.Method(navLightType, "Toggle");
            _navLightsArrayField = AccessTools.Field(typeof(NavLights), "navLights");
            harmony.Patch(_toggleMethod, prefix: new HarmonyMethod(typeof(NavLightsParkedOverridePatch), nameof(Prefix)));
        }

        internal static void ForceAllOff(Aircraft aircraft)
        {
            if (aircraft == null)
            {
                return;
            }

            NavLights navLights = aircraft.GetComponentInChildren<NavLights>();
            if (navLights == null)
            {
                return;
            }

            if (!(_navLightsArrayField.GetValue(navLights) is Array lights))
            {
                return;
            }

            for (int i = 0; i < lights.Length; i++)
            {
                object light = lights.GetValue(i);
                if (light != null)
                {
                    _toggleMethod.Invoke(light, new object[] { false });
                }
            }
        }

        private static void Prefix(ref bool enabled, Aircraft ___aircraft)
        {
            if (!enabled || ___aircraft == null || ___aircraft.pilots == null)
            {
                return;
            }

            Pilot[] pilots = ___aircraft.pilots;
            for (int i = 0; i < pilots.Length; i++)
            {
                if (pilots[i] != null && pilots[i].currentState is PilotParkedState)
                {
                    enabled = false;
                    return;
                }
            }
        }
    }
}
