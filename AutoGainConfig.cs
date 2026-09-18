using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class AutoGainConfig
    {
        internal static ConfigEntry<bool> Enabled;

        // Locked in after live tuning -- no longer exposed as sliders, but
        // kept as named constants (rather than inlined into
        // NightVisionAutoGain) so the tuned values stay visible and easy
        // to find if they ever need revisiting.
        internal const float MinBrightness = 0.5446009f;
        internal const float MaxBrightness = 0.5164319f;
        internal const float Sensitivity = 4.647888f;
        internal const float CompensationSpeed = 0.5f;
        internal const float SampleRate = 2f;

        // Called from NightVisionConfig.Initialize so this binds into the
        // shared "Night Vision Goggles" section instead of its own -- kept
        // as a separate method/class (rather than inlined there) since the
        // tuned constants above are still referenced directly from
        // NightVisionAutoGain.cs/NightVisionAutoGainPatch.cs.
        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                NightVisionConfig.Section,
                "Auto-Gain",
                true,
                new ConfigDescription(
                    "Automatically adjusts the gain of the image when in NVGs to try and make it easier to "
                    + "see in dark and bright environments. Moderately effective.",
                    null,
                    new ConfigurationManagerAttributes { Order = 90 }));
        }
    }
}
