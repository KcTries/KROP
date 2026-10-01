using HarmonyLib;
using Rewired;
using Rewired.Data;

namespace QOL_Realisim_Fixes.Patches
{
    // EXPERIMENTAL, internal-build only -- second attempt at native
    // Controls-menu integration for PCR. The first attempt (see git history
    // for this file) manually spliced a hand-rolled InputAction -- with a
    // deliberately huge, self-assigned id in the 300000-350000 range, to
    // dodge another mod's 10000-59999 bucket -- directly into
    // userData.actions/actionCategoryMap via reflection-adjacent field
    // access. That broke keybind reassignment game-wide, not just for our
    // own two actions. Root cause was never fully confirmed (Rewired's own
    // core is commercially obfuscated), but the vanilla game's own action
    // ids only ever run 0-64 (confirmed by inspecting the actual shipped
    // Rewired.prefab data via AssetRipper) -- everything downstream
    // (serialization, the ControlMapper UI's conflict-checker, internal
    // per-category caches) has only ever had to handle ids in that range,
    // and a wildly out-of-range custom id is a very plausible way to
    // violate an assumption none of that code was ever tested against.
    //
    // This attempt instead goes entirely through UserData's own public,
    // supported action-creation API (UserData.AddAction, which internally
    // calls UserData.GetNewActionId() -- a plain incrementing counter
    // seeded from the project's own baked data) instead of assigning an id
    // ourselves, so our two actions land as ordinary-looking ids
    // immediately following the vanilla range instead of somewhere Rewired
    // has never seen a real id before.
    //
    // A proper id alone wasn't enough to explain an observed duplicate-
    // controller symptom, so this was briefly switched Prefix -> Postfix on
    // the theory that our action injection was racing Rewired's own
    // controller detection inside the same Awake() call. That theory was
    // WRONG: the duplicate controller was Steam Input creating a phantom
    // second XInput device, unrelated to this mod entirely (confirmed by
    // disabling Steam Input, which fixed it independently of any code
    // change here). The Postfix version introduced a real regression
    // instead -- it runs too late for ControlMapper to pick up the newly
    // added actions, so they stopped appearing in the Controls menu at all.
    // Back on Prefix, which is what actually registers correctly.
    //
    // Still not a fully CONFIRMED-safe approach -- Rewired's initialization
    // sequence is still a black box we can't fully trace. If this
    // reintroduces keybind-reassignment corruption (with Steam Input off),
    // revert PeriodicCountermeasureControl to the ConfigManager
    // KeyboardShortcut approach (see its own git history) and delete this
    // file again.
    [HarmonyPatch(typeof(InputManager_Base), nameof(InputManager_Base.Awake))]
    internal static class PcrToggleActionPatch
    {
        internal const string ActionName = "Toggle PCR";
        internal const string ModifierActionName = "PCR Modifier";
        private const string FlightCategoryName = "Flight";

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
        private static int RegisterAction(UserData userData, int categoryId, string actionName)
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
