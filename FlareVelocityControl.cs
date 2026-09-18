using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    // ejectionVelocity is baked per-aircraft into each FlareEjector/
    // ChaffEjector prefab, and varies a lot more than first expected --
    // [FlareDiag] logging found vanilla aircraft ranging 10-40 m/s
    // (CAS1/VTOLTrainer1 20, COIN/Fighter1/Multirole1 10, AttackHelo1 40)
    // and modded aircraft in the same range or higher (Aryx_MC260_Chimera
    // 35, Aryx_LightFighter1 20). A flat multiplier amplifies that existing
    // spread instead of closing it -- it turns an already-fast ejector like
    // Chimera's 35 into something absurd while a slow one like COIN's 10
    // barely catches up. A floor does what was actually wanted: only the
    // aircraft that are genuinely slow get boosted, up to the configured
    // target; anything already at or above it is left alone.
    internal static class FlareVelocityControl
    {
        internal static ConfigEntry<float> MinimumVelocity;

        internal static void Initialize(ConfigFile config)
        {
            MinimumVelocity = config.Bind(
                "Countermeasures",
                "Minimum Ejection Velocity",
                100f,
                "Minimum Speed for Flare Ejection");
        }
    }
}
