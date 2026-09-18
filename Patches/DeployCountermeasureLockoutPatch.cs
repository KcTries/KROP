using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // CountermeasureManager.DeployCountermeasure is the single choke point
    // vanilla fires through -- Aircraft.FixedUpdate calls it every physics
    // tick for as long as countermeasureTrigger is held, always targeting
    // whatever's currently *selected*. PCR itself no longer calls this
    // method at all (it fires its locked target's Fire() directly, see
    // PeriodicCountermeasureControl), so this only ever needs to block
    // *manual* presses -- for two separate reasons:
    //  - the player's current selection is the same item PCR is already
    //    dispensing in the background, to avoid a redundant manual burst
    //    on top of it. If the player has switched to something else (ECM,
    //    etc.), that manual path is left completely alone regardless of
    //    what PCR is doing.
    //  - the "PCR Modifier" Rewired action is currently held, since that's
    //    always meant as half of the controller PCR-toggle combo (PCR
    //    Modifier + Countermeasures) -- a press in that state should never
    //    ALSO fire a real countermeasure release, whether or not it
    //    actually lines up with the toggle's own rising-edge check.
    [HarmonyPatch(typeof(CountermeasureManager), nameof(CountermeasureManager.DeployCountermeasure))]
    internal static class DeployCountermeasureLockoutPatch
    {
        private static bool Prefix(Aircraft aircraft)
        {
            if (PeriodicCountermeasureControl.IsModifierHeld())
            {
                return false;
            }
            return !PeriodicCountermeasureControl.ShouldOverrideActiveSelection(aircraft);
        }
    }
}
