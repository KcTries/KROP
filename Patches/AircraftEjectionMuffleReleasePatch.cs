using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // Aircraft.StartEjectionSequence() is the single choke point every
    // ejection path runs through -- the player's own "Eject" keybind
    // (PilotPlayerState.PlayerControls), the radial menu action, and the
    // auto-eject-on-death path all call it, and it's already guarded by its
    // own "if (!ejected)" check so this only ever fires once per real
    // ejection. Postfix, not Prefix: let vanilla actually start the sequence
    // first, then kick off the cockpit-muffle release.
    [HarmonyPatch(typeof(Aircraft), "StartEjectionSequence")]
    internal static class AircraftEjectionMuffleReleasePatch
    {
        private static void Postfix(Aircraft __instance)
        {
            if (GameManager.IsLocalAircraft(__instance))
            {
                SoundPropagation.TriggerEjectionMuffleRelease();
            }
        }
    }
}
