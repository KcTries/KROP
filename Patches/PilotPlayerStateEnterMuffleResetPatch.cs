using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // PilotPlayerState.EnterState runs whenever the local player becomes
    // player-controlled in a cockpit -- initial spawn, and every respawn
    // after that. That's the right moment to clear the latched eject-muffle
    // release from AircraftEjectionMuffleReleasePatch: the player is
    // confirmed back in a real cockpit, so ordinary cockpit-view muffling
    // should resume deciding things instead of staying forced open from
    // whatever they last ejected out of.
    [HarmonyPatch(typeof(PilotPlayerState), "EnterState")]
    internal static class PilotPlayerStateEnterMuffleResetPatch
    {
        private static void Postfix()
        {
            SoundPropagation.ResetEjectionMuffleRelease();
        }
    }
}
