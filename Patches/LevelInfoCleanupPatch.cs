using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // LevelInfo.OnDestroy() fires on mission/scene teardown. Without this,
    // a pending delayed shot or an active gun loop from the mission that
    // just ended could sit in SoundPropagation's static state holding a
    // reference into a scene that's about to be torn down (or a Gun
    // instance that gets pooled/reused for the next mission) -- a likely
    // contributor to loops getting stuck on, alongside the defensive
    // try/catch in SoundPropagation.TickLoops() itself.
    [HarmonyPatch(typeof(LevelInfo), "OnDestroy")]
    internal static class LevelInfoCleanupPatch
    {
        private static void Prefix()
        {
            SoundPropagation.ClearAll();
            PeriodicCountermeasureControl.ClearAll();
        }
    }
}
