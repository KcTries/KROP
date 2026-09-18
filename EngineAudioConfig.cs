using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    // Engines are the most performance-heavy part of this mod by far --
    // continuous per-frame history tracking, position smoothing, and
    // AudioSource re-syncing for every engine on every aircraft in the
    // scene, for as long as each one is alive. This is the escape hatch:
    // turn it off to fall back to vanilla's instant, undelayed engine audio
    // (zero ongoing tracking cost) while every other system this mod
    // touches -- gunfire, missiles, sonic booms, countermeasures, impacts,
    // ejection seat, fireballs -- keeps its speed-of-sound delay treatment
    // exactly as normal, since none of those go through this flag.
    internal static class EngineAudioConfig
    {
        internal static ConfigEntry<bool> DelayEnabled;

        internal static void Initialize(ConfigFile config)
        {
            DelayEnabled = config.Bind(
                "General",
                "Engine Sound Propagation",
                false,
                "Toggle the simulation for engine audio processing (PERFORMANCE HEAVY!!!!)");
        }
    }
}
