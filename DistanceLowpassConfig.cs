using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class DistanceLowpassConfig
    {
        // Attenuation/resonance/minimum-cutoff sliders removed to declutter
        // ConfigManager -- hardcoded at this user's own values, which were
        // already at the shipped defaults, so no behavior change.
        internal const float AttenuationThresholdDb = 20f;
        // Lowered from 2.5 -- confirmed (see SoundPropagation's own
        // resonance comments) that a resonant filter rings audibly right
        // at its cutoff frequency whenever it's genuinely cutting, not
        // just at the already-handled "fully open" edge case; normally
        // masked by loud engine noise, reported as a warble that became
        // clearly audible against a quiet, low-RPM/idling engine. Still
        // some character above Unity's neutral Q of 1, just not enough to
        // self-ring against a quiet source.
        internal const float ResonanceQ = 1.4f;
        internal const float MinCutoffHz = 200f;

        internal static ConfigEntry<bool> Enabled;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                "General",
                "Atmospheric Audio Filtering",
                true,
                "Applies a filter to audio based on distance from the camera.");
        }
    }
}
