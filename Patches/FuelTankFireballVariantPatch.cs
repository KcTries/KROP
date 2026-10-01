using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // FuelTank.Fireball() instantiates the tank's serialized `fireball`
    // prefab (fireball_medium / fireball_large) when a ruptured, burning
    // fuel tank ignites. That always happens exactly as vanilla does it;
    // this patch then gives it a 25% chance to ALSO spawn the ground
    // vehicles' `AmmoCookoffVehicle` effect (fireball + flames + smoke + a
    // burst of fast burning particles + its own explosion audio, used by
    // tanks and the Linebreaker IFV) on top of it.
    //
    // It only triggers when the original call actually spawned a fireball
    // (the original skips it if one already exists or the tank is under
    // water), detected by comparing the tank's private `fireballSpawn` field
    // before and after the call.
    //
    // The cook-off prefab isn't referenced by anything on an aircraft, so it
    // is only in memory while some ground vehicle that references it has
    // been loaded into the mission. It's looked up among loaded assets
    // rather than loaded by us; if it isn't there (an air-only mission, say)
    // only the vanilla fireball spawns. Purely cosmetic and decided
    // independently on each machine, like the game's own one-shot effects.
    [HarmonyPatch(typeof(FuelTank), "Fireball")]
    internal static class FuelTankFireballVariantPatch
    {
        private const float CookoffChance = 0.25f;
        private const string CookoffPrefabName = "AmmoCookoffVehicle";

        // Resources.FindObjectsOfTypeAll walks every loaded object, so a
        // failed lookup is only retried this often. A found prefab is cached
        // (and re-found automatically if a scene change unloads it, since
        // the cached reference then compares equal to null).
        private const float RescanIntervalSeconds = 30f;

        private static GameObject _cookoffPrefab;
        private static float _nextScanTime;

        // Single-threaded and the Prefix/Postfix pair always run back to
        // back for the same call, so one static is safe for carrying "was a
        // fireball already spawned before this call" from Prefix to Postfix.
        private static bool _hadFireballSpawn;

        private static void Prefix(GameObject ___fireballSpawn)
        {
            _hadFireballSpawn = ___fireballSpawn != null;
        }

        private static void Postfix(FuelTank __instance, GameObject ___fireballSpawn, UnitPart ___part)
        {
            // The vanilla call didn't spawn a (new) fireball -- nothing to add to.
            if (_hadFireballSpawn || ___fireballSpawn == null)
            {
                return;
            }
            if (!FuelExplosionConfig.VariedExplosions.Value || Random.value >= CookoffChance)
            {
                return;
            }
            GameObject cookoff = FindCookoffPrefab();
            if (cookoff == null)
            {
                return;
            }

            // Same way vanilla spawns its own fireball: a child of the tank at
            // its origin, with its DamageParticles registered with the part so
            // they're cleaned up the same way.
            GameObject extra = Object.Instantiate(cookoff, __instance.transform);
            extra.transform.localPosition = Vector3.zero;
            DamageParticles damageParticles = extra.GetComponent<DamageParticles>();
            if (___part != null && damageParticles != null)
            {
                ___part.AddHostedParticles(damageParticles);
            }

            Vector3 velocity = ___part != null && ___part.rb != null ? ___part.rb.velocity : Vector3.zero;
            MakeParticlesInheritVelocity(extra, velocity);

            if (VerboseLoggingConfig.Enabled.Value)
            {
                SoundPropagation.Log.LogInfo(
                    $"[FuelFireballDiag] Added '{CookoffPrefabName}' on top of a fuel tank fireball "
                    + $"(tank velocity {velocity.magnitude:F0} m/s).");
            }
        }

        // The cook-off prefab was built for a stationary ground vehicle: its
        // main burst system (the ~200 fast burning particles) is in Local
        // space with no velocity inheritance, so on a fast aircraft the burst
        // just rides along and expands around the airframe. This works on the
        // spawned INSTANCE only (the prefab asset is untouched, so ground
        // vehicles still use it exactly as before): every system is given the
        // tank's rigidbody velocity as its emitter velocity (explicit Custom
        // mode, rather than relying on Rigidbody mode finding a body) with
        // Inherit Velocity at 100%, so each particle starts with the
        // aircraft's velocity PLUS its own outward speed -- a forward cone.
        // Any system still in Local space is also moved into the same
        // Datum.origin custom space the prefab's SetGlobalParticles helper
        // puts the flames/smoke into (the helper's list doesn't include the
        // burst system), so the particles are released into the world instead
        // of traveling with the airframe.
        private static void MakeParticlesInheritVelocity(GameObject effect, Vector3 velocity)
        {
            foreach (ParticleSystem system in effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                bool needsSpaceChange = system.main.simulationSpace == ParticleSystemSimulationSpace.Local;
                if (needsSpaceChange)
                {
                    // Nothing has been simulated yet (same frame as the
                    // spawn), but restarting around the change is cheap and
                    // avoids changing the space of a system mid-emission.
                    system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                }

                ParticleSystem.MainModule main = system.main;
                if (needsSpaceChange)
                {
                    main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                    main.customSimulationSpace = Datum.origin;
                }
                main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Custom;
                main.emitterVelocity = velocity;

                ParticleSystem.InheritVelocityModule inherit = system.inheritVelocity;
                inherit.enabled = true;
                inherit.mode = ParticleSystemInheritVelocityMode.Initial;
                inherit.curve = new ParticleSystem.MinMaxCurve(1f);

                // The root system is the burst of burning embers.
                if (system.gameObject == effect)
                {
                    TuneEmbers(system);
                }

                if (needsSpaceChange)
                {
                    system.Play(false);
                }
            }
        }

        // The embers (the burst system's ~40 fast burning particles) were
        // tuned for a stationary ground vehicle: their Limit Velocity drag
        // (0.04, with "multiply drag by particle velocity" on) grows with
        // speed, so once they also carry an aircraft's several hundred m/s
        // it stops them almost immediately, and their 5-9 second lifetime
        // reads short next to a wide forward cone. Both are scaled on this
        // spawned instance only (see FuelExplosionConfig for the locked-in values).
        private static void TuneEmbers(ParticleSystem system)
        {
            ParticleSystem.LimitVelocityOverLifetimeModule limit = system.limitVelocityOverLifetime;
            if (limit.enabled)
            {
                limit.drag = new ParticleSystem.MinMaxCurve(
                    limit.drag.constant * FuelExplosionConfig.EmberDragMultiplier);
            }

            ParticleSystem.MainModule main = system.main;
            ParticleSystem.MinMaxCurve lifetime = main.startLifetime;
            float lifetimeScale = FuelExplosionConfig.EmberLifetimeMultiplier;
            main.startLifetime = new ParticleSystem.MinMaxCurve(
                lifetime.constantMin * lifetimeScale, lifetime.constantMax * lifetimeScale);
        }

        // Called on mission teardown so a stale reference to an unloaded
        // prefab (or a stale "not found" backoff from the previous mission)
        // never carries over into the next one.
        internal static void ResetCache()
        {
            _cookoffPrefab = null;
            _nextScanTime = 0f;
        }

        private static GameObject FindCookoffPrefab()
        {
            if (_cookoffPrefab != null)
            {
                return _cookoffPrefab;
            }
            if (Time.unscaledTime < _nextScanTime)
            {
                return null;
            }
            _nextScanTime = Time.unscaledTime + RescanIntervalSeconds;

            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                // The prefab asset itself, not an instantiated "(Clone)" of
                // it sitting in a scene.
                if (candidate.name == CookoffPrefabName && !candidate.scene.IsValid())
                {
                    _cookoffPrefab = candidate;
                    if (VerboseLoggingConfig.Enabled.Value)
                    {
                        SoundPropagation.Log.LogInfo($"[FuelFireballDiag] Found '{CookoffPrefabName}' prefab in memory.");
                    }
                    return _cookoffPrefab;
                }
            }
            if (VerboseLoggingConfig.Enabled.Value)
            {
                SoundPropagation.Log.LogInfo(
                    $"[FuelFireballDiag] '{CookoffPrefabName}' not loaded -- only the vanilla fireball will spawn.");
            }
            return null;
        }
    }
}
