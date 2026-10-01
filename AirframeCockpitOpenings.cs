namespace QOL_Realisim_Fixes
{
    // Per-airframe exceptions to the flat cockpit-filter cutoff -- some
    // aircraft have real hull openings whose presence depends on the
    // current loadout rather than being a fixed structural constant, so
    // this can't just be baked into a static per-jsonKey table the way
    // e.g. TraditionalRWR's designation dictionaries are. Kept as its own
    // small switch (not a generic rule list/interface) since there's
    // currently exactly one case -- add a new jsonKey branch here if/when
    // another airframe needs one, rather than building out a framework
    // for a single example.
    internal static class AirframeCockpitOpenings
    {
        // No longer configurable in ConfigManager (removed to declutter the
        // menu) -- these were both still at their shipped defaults when
        // removed, so hardcoded here at those same values rather than
        // reflecting any user tuning.
        private const bool Enabled = true;
        private const float IbisDoorGunMuffleMultiplier = 0.5f;

        private static readonly System.Collections.Generic.HashSet<string> IbisDoorGunKeys =
            new System.Collections.Generic.HashSet<string>
        {
            "Turret_12.7mm_door",
            "Turret_40mm_grenade",
        };

        // 1 = no change (treat the airframe as fully sealed). Only ever
        // checked against the LOCAL player's own aircraft -- this whole
        // cockpit-filter system only ever describes what the local
        // listener hears from their own seat.
        internal static float GetMuffleMultiplier(Aircraft localAircraft)
        {
            if (!Enabled || localAircraft == null)
            {
                return 1f;
            }
            if (IsIbisWithDoorGunEquipped(localAircraft))
            {
                return IbisDoorGunMuffleMultiplier;
            }
            return 1f;
        }

        // hardpointSets[i].weaponMount reflects the actual spawned,
        // networked loadout (populated from Aircraft's synced Loadout via
        // WeaponManager.SpawnWeapons), not a menu-only selection -- correct
        // for remote/AI aircraft too, though only ever queried here for
        // the local one. Both door guns are Turret-driven mounts rather
        // than a plain Gun, which is why this checks the mount's own
        // jsonKey instead of trying to type-check a Weapon/Gun instance.
        private static bool IsIbisWithDoorGunEquipped(Aircraft aircraft)
        {
            if (aircraft.definition == null || aircraft.definition.jsonKey != "UtilityHelo1")
            {
                return false;
            }
            if (aircraft.weaponManager == null || aircraft.weaponManager.hardpointSets == null)
            {
                return false;
            }
            HardpointSet[] hardpointSets = aircraft.weaponManager.hardpointSets;
            for (int i = 0; i < hardpointSets.Length; i++)
            {
                WeaponMount mount = hardpointSets[i]?.weaponMount;
                if (mount != null && IbisDoorGunKeys.Contains(mount.jsonKey))
                {
                    return true;
                }
            }
            return false;
        }

        // Helicopter cockpits sit right next to/above the engine deck with
        // far less acoustic sealing around it than a fixed-wing jet's
        // canopy -- turbine whine reaches the cabin much more directly.
        // Separate from GetMuffleMultiplier() above: this only ever scales
        // the turbine engine sound specifically (see TurbineEngineAnimatePatch),
        // not rotor/fan/mechanical sounds, which keep the normal cockpit
        // filter on these same airframes.
        private const float HelicopterTurbineMuffleMultiplier = 0.5f;

        private static readonly System.Collections.Generic.HashSet<string> HelicopterJsonKeys =
            new System.Collections.Generic.HashSet<string>
        {
            "UtilityHelo1",          // UH-90 Ibis
            "AttackHelo1",           // SAH-46 Chicane
            "Aryx_LightHelicopter1", // RAH-72 Knockout (Aryx mod)
        };

        internal static float GetTurbineMuffleMultiplier(Aircraft localAircraft)
        {
            if (localAircraft?.definition == null)
            {
                return 1f;
            }
            return HelicopterJsonKeys.Contains(localAircraft.definition.jsonKey)
                ? HelicopterTurbineMuffleMultiplier
                : 1f;
        }
    }
}
