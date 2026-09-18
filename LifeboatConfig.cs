using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    internal static class LifeboatConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> LifetimeMinutes;
        internal static ConfigEntry<int> MaxLifeboatsPerShip;

        internal static void Initialize(ConfigFile config)
        {
            const string section = "Lifeboats";

            Enabled = config.Bind(
                section,
                "Enable",
                true,
                "Spawns Lifeboats when Ships sink.");

            LifetimeMinutes = config.Bind(
                section,
                "Lifetime",
                15f,
                new ConfigDescription(
                    "Time in minutes how long the lifeboats stick around before theyre banished to the void.",
                    new AcceptableValueRange<float>(1f, 60f)));

            MaxLifeboatsPerShip = config.Bind(
                section,
                "Maximum Lifeboats Per Ship",
                15,
                new ConfigDescription(
                    "The maximum number of lifeboats a sinking ship can spawn. Most ships are largely unaffected "
                    + "by this since most of them crew less than 500 people. Each Lifeboat Holds 15 crew members.",
                    new AcceptableValueRange<int>(5, 30)));
        }
    }
}
