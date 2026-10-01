using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class FuelExplosionConfig
    {
        // Ember drag/lifetime used to be live ConfigManager sliders -- locked
        // in at this user's tuned values (0.1185 and 1.514): lifetime rounded to the
        // nearest half (1.5), drag set by hand to 0.15. Only the on/off toggle is still a
        // setting.
        // Multiple of the game's own drag on the cook-off embers (0 = none).
        internal const float EmberDragMultiplier = 0.15f;

        // Multiple of the game's own 5-9 second ember lifetime.
        internal const float EmberLifetimeMultiplier = 1.5f;

        internal static ConfigEntry<bool> VariedExplosions;

        internal static void Initialize(ConfigFile config)
        {
            VariedExplosions = config.Bind(
                ParticlesConfig.Section,
                "Varied Fuel Explosions",
                true,
                new ConfigDescription(
                    "Adds a chance for a more energetic explosion upon aircraft component detonation. "
                    + "Looks really cool at night.",
                    null,
                    new ConfigurationManagerAttributes { Order = 5 }));
        }
    }
}
