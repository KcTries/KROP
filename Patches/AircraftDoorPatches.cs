using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // Cargo release (MountedCargo.RailLaunch) calls the ramp's OpenDoor() over
    // and over, each time overwriting its open timer with the short vanilla
    // duration -- which would make a ramp the player toggled open close by
    // itself shortly after a drop. This re-asserts the player's hold after
    // every such call; a ramp that isn't held is left exactly as vanilla
    // handles it.
    [HarmonyPatch(typeof(CargoRamp), nameof(CargoRamp.OpenDoor))]
    internal static class CargoRampHoldPatch
    {
        private static void Postfix(CargoRamp __instance)
        {
            AircraftDoorControl.EnforceRampHold(__instance);
        }
    }

    // Aircraft.OpenCanopies() is what vanilla runs when the player ejects
    // while landed (the pilot climbs out through the open canopy instead of
    // jettisoning it). Canopy.OpenHinges() only sets "keep opening" and never
    // resets the angle, so an already-open canopy simply stays open and the
    // pilot gets out on the normal delay -- all this has to do is stop one of
    // OUR close animations that might still be running, so the two never
    // fight over the hinges.
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.OpenCanopies))]
    internal static class CanopyEjectKeepOpenPatch
    {
        private static void Postfix(Aircraft __instance)
        {
            AircraftDoorControl.CancelCanopyClose(__instance);
        }
    }
}
