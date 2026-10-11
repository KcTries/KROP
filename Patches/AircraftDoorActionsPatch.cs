using HarmonyLib;
using Rewired;
using Rewired.Data;

namespace QOL_Realisim_Fixes.Patches
{
    // Registers the two door/canopy toggles as native actions in the game's
    // own Controls menu (Flight category), exactly the way PcrToggleActionPatch
    // registers PCR's -- through UserData's own public AddAction API, so they
    // get ordinary-looking ids instead of a self-assigned out-of-range one.
    // Both ship unbound; the player assigns them in the Controls menu.
    [HarmonyPatch(typeof(InputManager_Base), nameof(InputManager_Base.Awake))]
    internal static class AircraftDoorActionsPatch
    {
        internal const string CanopyActionName = "Canopy/Doors Toggle";
        internal const string CargoRampActionName = "Cargo Ramp Toggle";

        internal static int CanopyActionId { get; private set; } = -1;
        internal static int CargoRampActionId { get; private set; } = -1;

        private static void Prefix(InputManager_Base __instance)
        {
            try
            {
                UserData userData = __instance?.userData;
                if (userData == null)
                {
                    return;
                }

                int flightCategoryId = userData.GetActionCategoryId(PcrToggleActionPatch.FlightCategoryName);
                if (flightCategoryId < 0)
                {
                    SoundPropagation.Log.LogWarning(
                        $"[DoorDiag] Could not find existing '{PcrToggleActionPatch.FlightCategoryName}' action category "
                        + "-- canopy/cargo ramp actions not registered.");
                    return;
                }

                // RegisterAction is idempotent by name (it returns the existing
                // action's id if there already is one), so this is safe to run
                // on every InputManager Awake without a "registered" latch.
                CanopyActionId = PcrToggleActionPatch.RegisterAction(userData, flightCategoryId, CanopyActionName);
                CargoRampActionId = PcrToggleActionPatch.RegisterAction(userData, flightCategoryId, CargoRampActionName);
            }
            catch (System.Exception ex)
            {
                SoundPropagation.Log.LogError($"[DoorDiag] EXCEPTION in AircraftDoorActionsPatch.Prefix: {ex}");
            }
        }
    }
}
