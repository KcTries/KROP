using HarmonyLib;
using Rewired;
using Rewired.Data;

namespace QOL_Realisim_Fixes.Patches
{
    // Native Controls-menu integration for PCR (shipped since v1.1.0). The
    // actions go entirely through UserData's own public action-creation
    // API (UserData.AddAction, which assigns the next id from Rewired's own
    // counter) rather than a self-assigned id -- an earlier version spliced
    // in an action with a huge hand-picked id (vanilla ids only run 0-64)
    // and broke keybind reassignment game-wide.
    //
    // Must stay a Prefix: a Postfix runs too late for ControlMapper to pick
    // the actions up, so they never appear in the Controls menu. (A
    // duplicate-controller symptom once blamed on this was Steam Input
    // creating a phantom XInput device, unrelated to this mod.)
    [HarmonyPatch(typeof(InputManager_Base), nameof(InputManager_Base.Awake))]
    internal static class PcrToggleActionPatch
    {
        internal const string ActionName = "Toggle PCR";
        internal const string ModifierActionName = "PCR Modifier";
        internal const string FlightCategoryName = "Flight";

        internal static int ToggleActionId { get; private set; } = -1;
        internal static int ModifierActionId { get; private set; } = -1;

        private static bool _registered;

        private static void Prefix(InputManager_Base __instance)
        {
            try
            {
                if (_registered)
                {
                    return;
                }

                UserData userData = __instance?.userData;
                if (userData == null)
                {
                    return;
                }

                int flightCategoryId = userData.GetActionCategoryId(FlightCategoryName);
                if (flightCategoryId < 0)
                {
                    SoundPropagation.Log.LogWarning(
                        $"[PcrDiag] Could not find existing '{FlightCategoryName}' action category -- PCR actions not registered.");
                    return;
                }

                _registered = true;

                ToggleActionId = RegisterAction(userData, flightCategoryId, ActionName);
                ModifierActionId = RegisterAction(userData, flightCategoryId, ModifierActionName);
            }
            catch (System.Exception ex)
            {
                SoundPropagation.Log.LogError($"[PcrDiag] EXCEPTION in PcrToggleActionPatch.Prefix: {ex}");
            }
        }

        // Idempotent across repeated Awake() calls (mission restarts, etc.)
        // by name, same as the first attempt -- UserData.GetAction(name)
        // is the public lookup for that. A brand-new action is created via
        // UserData.AddAction(categoryId) (proper id, Button type, already
        // userAssignable -- see UserData's own default-action factory),
        // then immediately renamed: GetActions_Copy() returns the SAME
        // live InputAction references (not clones), and AddAction always
        // appends to the end, so the last entry is guaranteed to be the one
        // just created.
        internal static int RegisterAction(UserData userData, int categoryId, string actionName)
        {
            InputAction existing = userData.GetAction(actionName);
            if (existing != null)
            {
                return existing.id;
            }

            userData.AddAction(categoryId);
            var actions = userData.GetActions_Copy();
            InputAction created = actions[actions.Count - 1];
            created.name = actionName;
            created.descriptiveName = actionName;

            SoundPropagation.Log.LogInfo(
                $"[PcrDiag] Registered '{actionName}' action (id={created.id}) into existing "
                + $"'{FlightCategoryName}' category (id={categoryId}) via UserData.AddAction.");
            return created.id;
        }
    }
}
