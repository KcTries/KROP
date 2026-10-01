namespace QOL_Realisim_Fixes
{
    // Shared ConfigManager section for every particle effect toggle (air
    // wake, contrails, missile contrails). Their tuning values used to be
    // live sliders and are now hardcoded constants in AirWakeConfig/
    // ContrailConfig, locked in at whatever this user had tuned them to.
    internal static class ParticlesConfig
    {
        internal const string Section = "Particles";
    }
}
