using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    // This mod accumulated a lot of `[XyzDiag]` logging over many past
    // investigations (gun loop transitions, engine clone lifecycle, bullet
    // cracks, CRAM fragmentation, sonic booms, camera-teleport detection,
    // etc.) that was never gated off once its investigation concluded --
    // most of it fires on real, recurring gameplay events rather than once
    // at startup, so in a busy session (heavy automatic gunfire capped at
    // ~12 cracks/sec, dozens of simultaneously tracked gun loops/engines)
    // the aggregate log volume is real, sustained I/O for the whole
    // duration of combat, which is exactly when performance matters most.
    // Off by default -- turn on only when actually trying to reproduce and
    // diagnose a specific audio issue, same as it was always meant to be
    // used for. One-time startup/warning logs (mod init, embedded resource
    // loading, config registration) are unaffected by this and always show.
    internal static class VerboseLoggingConfig
    {
        internal static ConfigEntry<bool> Enabled;

        internal static void Initialize(ConfigFile config)
        {
            Enabled = config.Bind(
                "General",
                "Logging",
                false,
                "Enables logging. If you're having issues, send the logs located in BepInEx/LogOutput.log (in "
                + "your Nuclear Option installation folder) to the developer!");
        }
    }
}
