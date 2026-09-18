using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class BulletCrackConfig
    {
        // Volume sliders removed to declutter ConfigManager -- hardcoded at
        // this user's own tuned values (10.14085/29.75352) rather than the
        // shipped defaults (2/4), so removing the sliders doesn't also
        // silently change how this already sounds.
        internal const float VolumeAtSmallArms = 10.14085f;
        internal const float VolumeAtLargestGun = 29.75352f;

        internal static ConfigEntry<bool> Enabled;

        internal static void Initialize(ConfigFile config)
        {
            const string section = "Bullet Cracks";

            Enabled = config.Bind(
                section,
                "Enable",
                false,
                "Plays a crack sound when bullets pass near the camera and are going supersonic. PERFORMANCE HEAVY!!!");
        }
    }
}
