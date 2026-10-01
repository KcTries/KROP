using BepInEx.Configuration;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    internal static class AirWakeConfig
    {
        // Every tuning value below used to be a live ConfigManager slider
        // under an "Air Wake" section -- removed once tuning was finished,
        // hardcoded here at whatever this user had actually tuned them to
        // (read from the saved .cfg, not the original defaults). Only the
        // on/off toggle is still a setting.
        internal const float LifetimeSeconds = 10.39437f;
        internal static readonly Color LandColor = new Color32(0xE0, 0xC5, 0x8F, 0xFF);

        // Share of the aircraft's ground speed each particle starts with,
        // along the direction of flight.
        internal const float InheritedSpeedPercent = 67.32394f;

        // Sideways throw speed (m/s) for a ~10 tonne aircraft, before
        // randomness and weight scaling.
        internal const float LateralSpeed = 10.79812f;

        // Each particle's lateral speed is 1 +/- this fraction of the base.
        internal const float LateralRandomness = 0.6f;

        // Extra no-lateral-speed puffs per burst, filling the strip between
        // the left and right halves of the wake.
        internal const int CenterFillParticles = 2;

        // Lateral speed is multiplied by (mass / 10t) ^ this.
        internal const float WeightScaling = 0.5f;

        // Puff spin (each particle spins at 60%-140% of this).
        internal const float SpinSpeedDegrees = 70f;

        // Seconds for dust to lose ~63% of its speed.
        internal const float SettleTimeSeconds = 0.8f;

        // 0 = billboard normals point out toward the puff's corners, like
        // vanilla's downwash dust; 1 = every normal faces the camera.
        internal const float NormalDirection = 0f;

        // 0 = plain white dust (vanilla), 1 = full tan LandColor.
        internal const float TintStrength = 1f;

        // Dust material lighting (see EffectTextures).
        internal const float NormalStrength = 1f;
        internal const float SelfIllumination = 0f;

        internal static ConfigEntry<bool> Enabled;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                ParticlesConfig.Section,
                "Air Wake Effects",
                true,
                new ConfigDescription(
                    "Spawns Particles when you're flying low over ground and water. Low performance Impact.",
                    null,
                    new ConfigurationManagerAttributes { Order = 30 }));
        }
    }
}
