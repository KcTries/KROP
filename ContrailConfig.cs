using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class ContrailConfig
    {
        // Thickness, Normal Strength, Self-Illumination and Swap Normal Axes
        // used to be ConfigManager sliders -- removed once tuning was
        // finished, hardcoded here at whatever this user had actually tuned
        // them to (read from the saved .cfg, not the original shipped
        // defaults). Only the on/off toggles are still settings.
        internal const float StartThickness = 1.553991f;
        internal const float EndThickness = 40f;

        // How strongly the contrail is shaded like a rounded tube (generated
        // normal map, see EffectTextures).
        internal const float NormalStrength = 1f;

        // Constant brightness added to the contrail -- matches vanilla's own
        // wingtip-vortex ribbon material (smokeRibbonScatter).
        internal const float SelfIllumination = 0.6032367f;

        // Swaps the generated normal map's axes (troubleshooting).
        internal const bool SwapNormalAxes = false;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> MissileContrailsEnabled;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                ParticlesConfig.Section,
                "Contrails",
                true,
                new ConfigDescription(
                    "Aircraft (and missiles if toggled on) will emit trails once above a certain altitude. This is "
                    + "caused by suspended water vapor high in the atmosphere crystalizing (into ice, or clouds) on "
                    + "the particles coming from the aircraft's exhaust. Can be performant heavy on lower end systems",
                    null,
                    new ConfigurationManagerAttributes { Order = 20 }));

            MissileContrailsEnabled = config.Bind(
                ParticlesConfig.Section,
                "Missile Contrails",
                true,
                new ConfigDescription(
                    "Toggle Missiles with active engines to leave contrails.",
                    null,
                    new ConfigurationManagerAttributes { Order = 10 }));
        }
    }
}
