using HarmonyLib;

namespace QOL_Realisim_Fixes.Patches
{
    // Airbrake's drag whoosh (airbrakeSound) is a continuous, physics-driven
    // loop -- volume/pitch set every Update() from live speed and how far
    // open it is -- that starts and stops as often as the pilot taps the
    // airbrake, same shape as LandingGear's tire rolling/skid noise. That's
    // exactly the case the existing tracked-loop system (built for
    // Missile's single start-to-detonation flight) doesn't fit well: it
    // assumes one start and one stop per lifetime, and a quick re-open
    // shortly after closing would either fight its pending-stop cleanup or
    // risk a stuck/silent clone. So this gets the same treatment tire noise
    // did -- cockpit-view muffling only, applied directly to the real, live
    // AudioSource via the lightweight cached-filter path engines already
    // use for their own "system off" fallback, no propagation delay (an
    // airbrake is mounted on and moves with the aircraft, not a fixed
    // emission point).
    [HarmonyPatch(typeof(Airbrake), "Update")]
    internal static class AirbrakeSoundPatch
    {
        private static void Postfix(UnityEngine.AudioSource ___airbrakeSound)
        {
            SoundPropagation.ApplyCockpitOnlyLowpass(___airbrakeSound);
        }
    }
}
