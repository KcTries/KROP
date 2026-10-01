namespace QOL_Realisim_Fixes
{
    // Shared ConfigManager section for features that change shared/simulated
    // game state (damage, ammo counts) rather than only what the local player
    // perceives -- they only behave correctly when every player in a match
    // has this mod, so they aren't multiplayer compatible. Every entry in it
    // is marked advanced so it stays hidden unless "Show advanced settings"
    // is on.
    internal static class NonMultiplayerConfig
    {
        internal const string Section = "Non-Multiplayer Compatible";
    }
}
