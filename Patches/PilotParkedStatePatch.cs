using System.Runtime.CompilerServices;
using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // PilotParkedState.EnterState currently only zeroes the throttle and
    // sets the brake when grounded (see decompiled source) -- this adds
    // more while an AI pilot is parked: nudge the throttle up to 1%
    // instead of exactly 0 (Airbrake.Update() deploys the airbrakes
    // whenever throttle == 0f exactly and retracts them for any nonzero
    // value -- confirmed via decompiled source -- so vanilla's own exact
    // zero was deploying them while parked; negligible enough not to
    // actually move the aircraft, especially with the brake already
    // engaged), hold the special axis (customAxis1 -- shared by swing
    // wings, tilt rotors, and VTOL engines, whichever a given aircraft
    // actually has) at its minimum, force navigation lights off (see
    // NavLightsParkedOverridePatch.ForceAllOff -- a parked aircraft's gear
    // is already down before this state is even entered, so the one-time
    // gear-extend event that turns lights on has already fired and won't
    // fire again just from sitting still, meaning a reactive Prefix alone
    // can't catch it), and hide every crew member's model, not just the
    // one Pilot whose EnterState fired -- Aircraft.pilots holds separate
    // Pilot instances for copilot/other crew seats that don't run their
    // own AI state machine, so only iterating the triggering pilot left
    // them all still visible.
    //
    // LeaveState() takes no parameters at all, and PilotBaseState's own
    // `pilot`/`aircraft` protected fields are never actually assigned by
    // this particular state (EnterState only uses its own `Pilot pilot`
    // parameter locally, never stores it) -- so there's no built-in way to
    // recover the aircraft leaving the state from LeaveState() alone. A
    // ConditionalWeakTable remembers the Aircraft for each PilotParkedState
    // instance across the Enter/Leave pair, keyed per-instance (each Pilot
    // owns its own `parkedState = new PilotParkedState()`) so multiple
    // simultaneously-parked aircraft -- a whole carrier deck, an airbase --
    // don't stomp on a single shared reference.
    [HarmonyPatch(typeof(PilotParkedState))]
    internal static class PilotParkedStatePatch
    {
        private static readonly ConditionalWeakTable<PilotParkedState, Aircraft> OwningAircraft = new ConditionalWeakTable<PilotParkedState, Aircraft>();

        [HarmonyPatch("EnterState")]
        [HarmonyPostfix]
        private static void EnterStatePostfix(PilotParkedState __instance, Pilot pilot, ControlInputs ___controlInputs)
        {
            Aircraft aircraft = pilot.aircraft;
            OwningAircraft.Remove(__instance);
            OwningAircraft.Add(__instance, aircraft);

            // 1% throttle keeps the airbrakes retracted (see class
            // comment) without meaningfully driving the aircraft.
            ___controlInputs.throttle = 0.01f;

            // -1 = held fully down/retracted -- covers swing wings, tilt
            // rotors, and VTOL engines alike without needing to know which
            // specific mechanism a given aircraft actually has.
            ___controlInputs.customAxis1 = -1f;

            NavLightsParkedOverridePatch.ForceAllOff(aircraft);
            SetAllCrewVisible(aircraft, false);
        }

        [HarmonyPatch("LeaveState")]
        [HarmonyPostfix]
        private static void LeaveStatePostfix(PilotParkedState __instance)
        {
            if (OwningAircraft.TryGetValue(__instance, out Aircraft aircraft))
            {
                SetAllCrewVisible(aircraft, true);
            }
        }

        private static void SetAllCrewVisible(Aircraft aircraft, bool visible)
        {
            if (aircraft == null || aircraft.pilots == null)
            {
                return;
            }

            Pilot[] pilots = aircraft.pilots;
            for (int i = 0; i < pilots.Length; i++)
            {
                pilots[i]?.TogglePilotVisibility(visible);
            }
        }
    }
}
