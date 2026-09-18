using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Every countermeasure type's own UpdateHUD() override calls this same
    // CombatHUD method to show its name/icon/ammo -- patching the single
    // choke point here means Periodic Countermeasure Release (PCR) doesn't
    // need a separate patch per countermeasure type to relabel the HUD
    // while it's active. Only the (string, Sprite, int) ammo-count overload
    // is used by the countermeasure types PCR applies to (FlareEjector,
    // ChaffEjector); the (string, Sprite, bool) overload is for something
    // else and is left alone.
    //
    // Relabels to "PCR" only while the player's current selection is the
    // item PCR is actually dispensing -- if they've switched to something
    // else (ECM, etc.), the HUD should keep showing that normally, since
    // PCR isn't what's controlling it anymore.
    [HarmonyPatch(typeof(CombatHUD), nameof(CombatHUD.DisplayCountermeasures), new[] { typeof(string), typeof(Sprite), typeof(int) })]
    internal static class CombatHudPcrLabelPatch
    {
        private static void Prefix(ref string countermeasureName)
        {
            if (GameManager.GetLocalAircraft(out Aircraft localAircraft) && PeriodicCountermeasureControl.ShouldOverrideActiveSelection(localAircraft))
            {
                countermeasureName = "PCR";
            }
        }
    }
}
