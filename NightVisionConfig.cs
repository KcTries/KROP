using BepInEx.Configuration;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    internal enum NvgPhosphorColor
    {
        WhitePhosphor,
        AmberPhosphor,
        GreenPhosphor
    }

    internal static class NightVisionConfig
    {
        // Auto-Gain used to live in its own "NVG Auto-Gain" ConfigManager
        // category -- folded into this one at the user's request, since
        // it's just another NVG behavior toggle, not a separate feature
        // area. AutoGainConfig.Initialize still does its own binding (it
        // owns the Enabled entry other files reference), just into this
        // section instead of its own.
        internal const string Section = "Night Vision Goggles";

        // Bloom Level / Static Density / Speck Size were ConfigManager
        // sliders -- removed at the user's request to declutter the menu
        // down to the settings actually worth exposing, and hardcoded at
        // whatever this user had actually tuned them to (Bloom Level,
        // Static Density) rather than reset to the original shipped
        // default. Static Intensity/Speck Size happened to already be at
        // their shipped default, so no behavior change there either way.
        internal const float BloomLevel = 70f;
        internal const float StaticDensity = 0.5915493f;
        internal const float StaticIntensity = 1f;
        internal const int SpeckSize = 2;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<NvgPhosphorColor> PhosphorColor;
        internal static ConfigEntry<int> SpeckCount;
        internal static ConfigEntry<bool> TransitionAnimationEnabled;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                Section,
                "Enabled",
                true,
                new ConfigDescription(
                    "Flashing lights warning! Uses a custom built shader to make the NVGs look better.",
                    null,
                    new ConfigurationManagerAttributes { Order = 100 }));

            AutoGainConfig.Initialize(config);

            PhosphorColor = config.Bind(
                Section,
                "Phosphor Color",
                NvgPhosphorColor.GreenPhosphor,
                new ConfigDescription(
                    "Sets the color of the phosphor used in the NVGs (custom coming soon????)",
                    null,
                    new ConfigurationManagerAttributes { Order = 80 }));

            SpeckCount = config.Bind(
                Section,
                "Speck Count",
                12,
                new ConfigDescription(
                    "The number of white specks that simulate bright spots caused by radiation",
                    new AcceptableValueRange<int>(0, 100),
                    new ConfigurationManagerAttributes { Order = 70 }));

            TransitionAnimationEnabled = config.Bind(
                Section,
                "Transition Animation",
                true,
                new ConfigDescription(
                    "Enables/Disables the animations for going into/coming out of NVGs",
                    null,
                    new ConfigurationManagerAttributes { Order = 60 }));
        }

        // Exact phosphor colors as specified: white e5f2e1, amber d39934,
        // green 4bf70c.
        internal static Color GetTintColor()
        {
            switch (PhosphorColor.Value)
            {
                case NvgPhosphorColor.WhitePhosphor:
                    return new Color32(0xe5, 0xf2, 0xe1, 0xff);
                case NvgPhosphorColor.AmberPhosphor:
                    return new Color32(0xd3, 0x99, 0x34, 0xff);
                case NvgPhosphorColor.GreenPhosphor:
                default:
                    return new Color32(0x4b, 0xf7, 0x0c, 0xff);
            }
        }
    }
}
