using BepInEx.Configuration;

namespace QOL_Realisim_Fixes
{
    // AeroSentry SPAAG fires through the same shared BulletSim.Bullet
    // proximity-fuse code as every other gun in the game, which normally
    // calls DamageEffects.BlastFrag on a proximity detonation -- a big
    // blast-radius explosion. A real 20-30mm point-defense autocannon
    // doesn't use blast-radius proximity rounds like that; it either scores
    // a direct kinetic hit or proximity-detonates into a narrow cone of
    // shrapnel ahead of the round's own flight path. This section only
    // changes what happens on a proximity detonation -- a direct hit
    // (BulletSim.Bullet's Linecast/IDamageable.TakeDamage branch) is
    // untouched and still "explodes like normal", exactly as it does for
    // every other gun in the game. Scoped to AeroSentry only (jsonKey
    // "SPAAG1") -- CRAM was deliberately left out at the user's request,
    // see FragmentationJsonKeys in CramProximityFragmentPatch. Further
    // scoped by target type -- only bombs bigger than the PAB-125 and both
    // cruise missiles, see FragmentationTargetJsonKeys in the same file --
    // since the precision-aimed fragment cone whiffed against small/fast
    // targets that vanilla's own wider blast radius already handled fine.
    internal static class CramFragmentationConfig
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> FragmentCount;
        internal static ConfigEntry<float> ConeAngleDegrees;
        internal static ConfigEntry<float> LifetimeSeconds;
        internal static ConfigEntry<float> DamageFraction;
        internal static ConfigEntry<float> DetonateLeadSeconds;
        internal static ConfigEntry<bool> ShowDebugTracers;

        internal static void Initialize(ConfigFile config)
        {
            const string section = "Advanced SPAAG Ammo";

            Enabled = config.Bind(
                section,
                "Enabled",
                true,
                "Enables the Advanced SPAAG Ammo system. Disable to increase performance on heavy maps");

            FragmentCount = config.Bind(
                section,
                "Fragment Count",
                5,
                new ConfigDescription(
                    "Number of fragments simulated per 30mm airburst (can get performance heavy with higher numbers)",
                    new AcceptableValueRange<int>(3, 10)));

            ConeAngleDegrees = config.Bind(
                section,
                "Max Cone Angle",
                1.5f,
                new ConfigDescription(
                    "Maximum angle the fragments can be spawned at.",
                    new AcceptableValueRange<float>(0f, 45f)));

            LifetimeSeconds = config.Bind(
                section,
                "Fragment Lifetime",
                2f,
                new ConfigDescription(
                    "Time in seconds the frags continue to be simulated (If having performance issues try reducing this)",
                    new AcceptableValueRange<float>(0.5f, 5f)));

            DamageFraction = config.Bind(
                section,
                "Fragment Damage Fraction",
                0.5f,
                new ConfigDescription(
                    "Division of damage between each fragment from the original 30mm round. If Aerosentries are "
                    + "underperforming, turn this up",
                    new AcceptableValueRange<float>(0.01f, 1f)));

            DetonateLeadSeconds = config.Bind(
                section,
                "Early Detonation Offset",
                0.5f,
                new ConfigDescription(
                    "Time before estimated impact with target to detonate and release fragments. More time is a "
                    + "bigger cloud but less reliable damage on smaller targets, shorter time is more reliable "
                    + "damage but on a smaller area.",
                    new AcceptableValueRange<float>(0f, 2f)));

            ShowDebugTracers = config.Bind(
                section,
                "Show Fragment Tracers",
                true,
                "Show fragment tracers. Unrealistic, but cool.");
        }
    }
}
