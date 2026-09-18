using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class FlareCountConfig
    {
        internal static ConfigEntry<bool> UseActualFlareValues;

        internal static void Initialize(ConfigFile config)
        {
            UseActualFlareValues = config.Bind(
                "Countermeasures",
                "Use Actual Flare Values",
                true,
                "Makes flare counts accurate to visual models. Does not reduce flares, only increases.");
        }
    }
}
