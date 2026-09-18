using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Rewired;
using Rewired.Data.Mapping;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Adds "Toggle PCR" and "PCR Modifier" directly into the game's
    // EXISTING "Flight" action category, rather than creating a whole new
    // category/tab the way an earlier attempt did. This never touches
    // ControlMapper.Initialize or its _mappingSets array at all -- that's
    // specifically what collided with a bug in BOTE's own (all released
    // versions) category registration, corrupting the Controls menu's
    // layout (see PeriodicCountermeasureControl's own comment for the full
    // story). Since "Flight" is already part of the vanilla "Game" tab's
    // existing MappingSet, both actions just appear as extra rows there
    // automatically once registered -- no menu-side wiring needed, and no
    // risk of the same interaction.
    //
    // "Toggle PCR" is a direct single-key toggle (defaults to keyboard B).
    // "PCR Modifier" is meant to be held alongside the vanilla
    // Countermeasures button on controller -- Rewired's own modifier-key
    // system (ActionElementMap's modifierKey1/2/3) only works for keyboard
    // mappings, so a real combo needs two separate actions checked together
    // in code (see PeriodicCountermeasureControl) rather than one native
    // binding. Ships unbound; there's no universal default hardware
    // element the way KeyCode.B works for keyboard, so the player picks
    // whatever button they want (D-pad Down, a bumper, etc.) themselves.
    //
    // Registration here happens too early to also assign "Toggle PCR"'s
    // default keyboard binding -- ReInput/the player's actual ControllerMap
    // instances don't exist yet at InputManager_Base.Awake, only the raw
    // template data. EnsureDefaultKeyboardBinding (called later, once
    // ReInput is ready -- see PeriodicCountermeasureControl.Tick) uses the
    // real runtime API for that, ControllerMap.CreateElementMap.
    [HarmonyPatch(typeof(InputManager_Base), nameof(InputManager_Base.Awake))]
    internal static class PcrToggleActionPatch
    {
        internal const string ActionName = "Toggle PCR";
        internal const string ModifierActionName = "PCR Modifier";
        private const string FlightCategoryName = "Flight";

        internal static int ToggleActionId { get; private set; } = -1;
        internal static int ModifierActionId { get; private set; } = -1;
        internal static int FlightCategoryId { get; private set; } = -1;

        private static bool _registered;
        private static bool _defaultBindingChecked;

        private static void Prefix(InputManager_Base __instance)
        {
            if (_registered)
            {
                return;
            }

            List<InputAction> actions = __instance?.userData?.actions;
            List<InputActionCategory> actionCategories = __instance?.userData?.actionCategories;
            ActionCategoryMap actionCategoryMap = __instance?.userData?.actionCategoryMap;
            if (actions == null || actionCategories == null || actionCategoryMap == null)
            {
                return;
            }

            InputActionCategory flightCategory = actionCategories.FirstOrDefault(c => c.name == FlightCategoryName);
            if (flightCategory == null)
            {
                SoundPropagation.Log.LogWarning(
                    $"[PcrDiag] Could not find existing '{FlightCategoryName}' action category -- PCR actions not registered.");
                return;
            }

            _registered = true;
            FlightCategoryId = flightCategory.id;

            ToggleActionId = RegisterAction(actions, actionCategoryMap, flightCategory.id, ActionName);
            ModifierActionId = RegisterAction(actions, actionCategoryMap, flightCategory.id, ModifierActionName);
        }

        private static int RegisterAction(
            List<InputAction> actions, ActionCategoryMap actionCategoryMap, int categoryId, string actionName)
        {
            InputAction existing = actions.FirstOrDefault(a => a.name == actionName);
            if (existing != null)
            {
                return existing.id;
            }

            InputAction action = new InputAction
            {
                id = GetId(actionName),
                name = actionName,
                type = InputActionType.Button,
                descriptiveName = actionName,
                categoryId = categoryId,
                userAssignable = true,
            };
            actions.Add(action);
            actionCategoryMap.AddAction(categoryId, action.id);

            SoundPropagation.Log.LogInfo(
                $"[PcrDiag] Registered '{actionName}' action (id={action.id}) into existing "
                + $"'{FlightCategoryName}' category (id={categoryId}).");
            return action.id;
        }

        // Called every Tick() until it succeeds once (or gives up because
        // the player already has some binding for this action, whether
        // from a prior session's save or their own manual rebind -- never
        // overwrite a deliberate choice). Runs long after Awake(), once
        // ReInput has finished building the player's real ControllerMap
        // instances from the template data registered above. Only
        // "Toggle PCR" gets a default -- "PCR Modifier" has no universal
        // default hardware element to assign the same way.
        internal static void EnsureDefaultKeyboardBinding(Player playerInput)
        {
            if (_defaultBindingChecked || ToggleActionId == -1 || playerInput == null || !ReInput.isReady)
            {
                return;
            }

            IEnumerable<ControllerMap> maps = playerInput.controllers.maps.GetAllMaps();
            if (maps == null)
            {
                return;
            }

            ControllerMap keyboardFlightMap = null;
            foreach (ControllerMap map in maps)
            {
                if (map.categoryId == FlightCategoryId && map.controllerType == ControllerType.Keyboard)
                {
                    keyboardFlightMap = map;
                    break;
                }
            }

            if (keyboardFlightMap == null)
            {
                return;
            }

            _defaultBindingChecked = true;

            foreach (ActionElementMap existing in keyboardFlightMap.AllMaps)
            {
                if (existing.actionId == ToggleActionId)
                {
                    SoundPropagation.Log.LogInfo(
                        $"[PcrDiag] '{ActionName}' already has a keyboard binding -- leaving it alone.");
                    return;
                }
            }

            bool created = keyboardFlightMap.CreateElementMap(
                ToggleActionId, Pole.Positive, KeyCode.B, ModifierKey.None, ModifierKey.None, ModifierKey.None);
            if (created)
            {
                ReInput.userDataStore?.Save();
            }
            SoundPropagation.Log.LogInfo($"[PcrDiag] Default keyboard binding (B) for '{ActionName}' created={created}.");
        }

        // Deterministic so a saved binding survives across sessions.
        // Offset well clear of vanilla's own small sequential IDs and of
        // BOTE's 10000-59999 bucket, to avoid any repeat of the earlier
        // collision confusion.
        private static int GetId(string actionName)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in "QOL_Realisim_Fixes:" + actionName)
                {
                    hash = hash * 31 + c;
                }
                return Math.Abs(hash) % 50000 + 300000;
            }
        }
    }
}
