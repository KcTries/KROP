using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class CockpitLowpassConfig
    {
        // Cutoff/highpass/own-hit-distance/mechanical-multiplier were all
        // ConfigManager sliders at one point -- removed to declutter the
        // menu down to just the one on/off switch players actually touch.
        // The underlying behavior is unchanged; these are hardcoded at
        // whatever values were actually in use (this user's own tuning for
        // CutoffHz/HighpassEnabled/HighpassCutoffHz, shipped defaults for
        // the rest) rather than reset to shipped defaults across the board.
        internal const float CutoffHz = 456.338f;
        internal const bool HighpassEnabled = false;
        internal const float HighpassCutoffHz = 50f;
        internal const bool OwnHitDistanceScalingEnabled = true;
        internal const float OwnHitFullMuffleDistanceMeters = 12f;
        internal const float InternalMechanicalMuffleMultiplier = 0.75f;

        internal static ConfigEntry<bool> Enabled;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                "General",
                "1st Person Audio Filter",
                true,
                "Muffles sounds when in first person.");
        }
    }
}
