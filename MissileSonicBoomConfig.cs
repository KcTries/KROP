using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class MissileSonicBoomConfig
    {
        internal static ConfigEntry<bool> Enabled;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                "Audio",
                "Missile Sonic Booms",
                true,
                "Enables Sonic Booms for missiles going above mach 1.");
        }
    }
}
