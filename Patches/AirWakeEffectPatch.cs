using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Dust/spray kicked up by a fast, low-flying fixed-wing aircraft.
    //
    // Fifth design. Earlier versions emitted from the ParticleSystem's own
    // Cone shape (which points along world +Z, not up), fed emitterVelocity
    // into an Inherit Velocity module that was never enabled, and relied on
    // an undocumented drag formula. This version has no shape, no emitter
    // orientation and no built-in drag. Same technique as
    // ContrailEffectPatch: Custom-space (Datum.origin) containers, every
    // particle spawned by an explicit Emit() with its own position and
    // velocity, spaced by DISTANCE traveled so spacing along the ground
    // track is even at any speed.
    //
    // Motion model, per particle:
    //   velocity = forward * (inherited % of ground speed)
    //            + (aircraft right or left) * lateral speed (random, scaled
    //              by aircraft weight)
    //            + a small upward kick
    // then it decelerates exponentially (Settle Time) via explicit per-
    // particle math (GetParticles/SetParticles at 20Hz -- fully
    // deterministic, unlike ParticleSystem's built-in drag). Each burst
    // throws particles to BOTH sides, so the dust forms a symmetrical wake.
    //
    // Spin: each particle should curl inward -- clockwise for one side,
    // counter-clockwise for the other. The built-in Rotation-over-Lifetime
    // module applies one signed angular velocity to a whole system, so there
    // are two systems per aircraft (one per direction) and each particle is
    // emitted into the one that matches the side it was thrown to.
    //
    // Helicopters are excluded via RotorShaft presence (every aircraft has
    // some kind of ground-effect object, so that alone can't identify one).
    //
    // Driven from Aircraft.FixedUpdate(); the systems are created lazily the
    // first time an aircraft actually qualifies. Per-aircraft state lives in
    // a ConditionalWeakTable; the GameObjects are destroyed via the
    // aircraft's own onDisableUnit event.
    [HarmonyPatch(typeof(Aircraft), "FixedUpdate")]
    internal static class AirWakeEffectPatch
    {
        private const float MinSpeedMetersPerSecond = 111.11f; // 400 km/h
        private const float MaxAltitudeMeters = 30f; // matches the Medusa's own DownwashEffect

        // Straight down, not tilted -- the required travel distance never
        // changes with speed or maneuvering, so a flat margin is safe.
        private const float RaycastMaxDistance = MaxAltitudeMeters + 30f;

        // One burst every this many meters of ground track. Each burst
        // throws PairsPerBurst particles to the right AND PairsPerBurst to
        // the left.
        private const float BurstSpacingMeters = 10f;
        private const int PairsPerBurst = 2;
        // A hitch (or a very fast aircraft) can owe several bursts in one
        // physics tick; cap it so a backlog never spawns a huge clump.
        private const int MaxBurstsPerTick = 4;

        // Lifted slightly off the surface so fresh particles don't start
        // half-buried in terrain.
        private const float SpawnHeightMeters = 0.5f;
        private const float MinUpKickMetersPerSecond = 1f;
        private const float MaxUpKickMetersPerSecond = 5f;

        // Small random variation on the inherited forward speed, so the
        // wake doesn't leave in a perfectly even sheet.
        private const float InheritedSpeedVariation = 0.15f;

        // Lifetime is a hard config constant (AirWakeConfig); size is the
        // per-particle billboard size range.
        private const float MinParticleSize = 8f;
        private const float MaxParticleSize = 14f;

        // Per-particle spin varies between these fractions of Spin Speed.
        private const float MinSpinFraction = 0.6f;
        private const float MaxSpinFraction = 1.4f;

        // Lateral speed is specified for this aircraft weight.
        private const float ReferenceMassKg = 10000f;
        private const float MassRefreshIntervalSeconds = 1f;
        private const float MinWeightFactor = 0.25f;
        private const float MaxWeightFactor = 4f;

        // How often live particles are slowed down. 20Hz is smooth enough
        // that the stepping isn't visible, and keeps the GetParticles/
        // SetParticles copy cost low.
        private const float DampingIntervalSeconds = 0.05f;
        private const int MaxParticlesPerSystem = 800;

        // Confirmed in-game: a positive angular velocity in Rotation over
        // Lifetime appears CLOCKWISE on screen (the opposite of a 2D sprite's
        // positive Z rotation, which was the original assumption).
        private const float ClockwiseSign = 1f;

        // Water spray reads as a pale blue-white -- same sprite/material as
        // the land dust, since only one texture was embedded.
        private static readonly Color WaterTint = new Color(0.80f, 0.90f, 0.95f, 1f);

        // Index 0 = particles thrown to the aircraft's RIGHT (spin
        // clockwise as seen from in front of the aircraft), index 1 = LEFT
        // (counter-clockwise). From in front, the aircraft's right is the
        // viewer's left.
        private const int RightSystem = 0;
        private const int LeftSystem = 1;

        private class AirWakeState
        {
            public bool IsHelicopter;
            public bool EffectAttempted;
            public GameObject[] EffectObjects;
            public ParticleSystem[] Systems;
            public ParticleSystemRenderer[] Renderers;
            public float EmitCounter;
            public bool Emitting;
            public float NextLogTime;
            public float TimeSinceDamp;
            public float Mass = ReferenceMassKg;
            public float NextMassRefreshTime;
        }

        private static readonly ConditionalWeakTable<Aircraft, AirWakeState> _states =
            new ConditionalWeakTable<Aircraft, AirWakeState>();

        // Shared by every aircraft's damping pass -- only one system's
        // particles are ever read into/written from it at a time (all
        // single-threaded, called synchronously from Aircraft.FixedUpdate).
        private static readonly ParticleSystem.Particle[] _particleBuffer =
            new ParticleSystem.Particle[MaxParticlesPerSystem];

        private static void Postfix(Aircraft __instance)
        {
            if (!AirWakeConfig.Enabled.Value || GameManager.IsHeadless || __instance == null || __instance.rb == null)
            {
                return;
            }

            AirWakeState state = _states.GetValue(__instance, _ => new AirWakeState());
            if (state.IsHelicopter)
            {
                return;
            }

            // Runs whether or not this aircraft is currently emitting --
            // already-thrown dust keeps slowing down after the aircraft
            // climbs away.
            if (state.Systems != null)
            {
                DampParticles(state);
            }

            bool shouldPlay = __instance.speed >= MinSpeedMetersPerSecond && __instance.radarAlt <= MaxAltitudeMeters;
            if (!shouldPlay)
            {
                SetEmitting(state, __instance, false, "speed/altitude out of range");
                return;
            }

            if (!state.EffectAttempted)
            {
                CreateEffect(__instance, state);
            }
            if (state.Systems == null)
            {
                return;
            }

            Vector3 aircraftPos = __instance.transform.position;

            bool hitWater = false;
            bool hitGround = false;
            float groundY = aircraftPos.y;
            float verticalDistance = RaycastMaxDistance;

            // Only unpaved ground kicks up dust -- GameAssets.i.terrainMaterial
            // is the exact shared PhysicMaterial vanilla's own Downwash
            // (rotor wash) checks for the same reason; a runway/road collider
            // carries a different (or no) sharedMaterial.
            if (Physics.Raycast(aircraftPos, Vector3.down, out RaycastHit hitInfo, RaycastMaxDistance, PhysicsLayers.StaticsMask)
                && hitInfo.collider.sharedMaterial == GameAssets.i.terrainMaterial)
            {
                verticalDistance = hitInfo.distance;
                groundY = hitInfo.point.y;
                hitGround = true;
            }
            // Water wins over a ground hit at the same ray, same precedence
            // DownwashEffect itself uses.
            if (Datum.WaterPlane().Raycast(new Ray(aircraftPos, Vector3.down), out float enter) && enter < verticalDistance)
            {
                verticalDistance = enter;
                groundY = aircraftPos.y - enter;
                hitWater = true;
                hitGround = false;
            }

            if (VerboseLoggingConfig.Enabled.Value && Time.timeSinceLevelLoad >= state.NextLogTime)
            {
                state.NextLogTime = Time.timeSinceLevelLoad + 0.5f;
                SoundPropagation.Log.LogInfo(
                    $"[AirWakeDiag] '{__instance.name}' speed={__instance.speed:F1}m/s radarAlt={__instance.radarAlt:F1}m "
                    + $"hitGround={hitGround} hitWater={hitWater} verticalDistance={verticalDistance:F1}m groundY={groundY:F1} "
                    + $"mass={state.Mass:F0}kg particles={state.Systems[0].particleCount + state.Systems[1].particleCount}");
            }

            if (!hitWater && !hitGround)
            {
                SetEmitting(state, __instance, false, "no ground/water hit");
                return;
            }
            SetEmitting(state, __instance, true, hitWater ? "water hit" : "ground hit");

            Vector3 surfacePoint = new Vector3(aircraftPos.x, groundY + SpawnHeightMeters, aircraftPos.z);
            // The containers' own transforms play no part in where particles
            // appear (every Emit() gives an explicit position), but keeping
            // them near the action gives the renderer's bounds/sorting a
            // sane, current position to work from.
            for (int i = 0; i < state.EffectObjects.Length; i++)
            {
                state.EffectObjects[i].transform.position = surfacePoint;
            }

            Vector3 velocity = __instance.rb.velocity;
            Vector3 horizontalVelocity = new Vector3(velocity.x, 0f, velocity.z);
            float groundSpeed = horizontalVelocity.magnitude;
            if (groundSpeed < 1f)
            {
                return;
            }
            state.EmitCounter += Time.fixedDeltaTime * groundSpeed / BurstSpacingMeters;

            // Fades out toward the altitude ceiling, tinted per surface type.
            float alpha = Mathf.Clamp01(1f - verticalDistance / MaxAltitudeMeters);
            // Vanilla's own downwash dust is plain white; the tan land color
            // multiplies it darker, so Dust Tint blends between the two.
            Color tint = hitWater
                ? WaterTint
                : Color.Lerp(Color.white, AirWakeConfig.LandColor, AirWakeConfig.TintStrength);
            Color32 startColor = new Color(tint.r, tint.g, tint.b, alpha);

            Vector3 forward = horizontalVelocity / groundSpeed;
            // Unity is left-handed: with Y up, up x forward points to the
            // aircraft's right.
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float weightFactor = GetWeightFactor(__instance, state);

            int bursts = 0;
            while (state.EmitCounter >= 1f && bursts < MaxBurstsPerTick)
            {
                state.EmitCounter -= 1f;
                bursts++;
                EmitBurst(state, surfacePoint, forward, right, groundSpeed, weightFactor, startColor);
            }
            if (state.EmitCounter >= 1f)
            {
                state.EmitCounter = 0f;
            }
        }

        // Lateral speed multiplier from aircraft weight. GetMass() sums every
        // live part (so it tracks fuel burn, expended stores, battle damage),
        // but walks the part list, so it's only re-read about once a second.
        private static float GetWeightFactor(Aircraft aircraft, AirWakeState state)
        {
            if (Time.timeSinceLevelLoad >= state.NextMassRefreshTime)
            {
                state.NextMassRefreshTime = Time.timeSinceLevelLoad + MassRefreshIntervalSeconds;
                float mass = aircraft.GetMass();
                state.Mass = mass > 1f ? mass : ReferenceMassKg;
            }
            float factor = Mathf.Pow(state.Mass / ReferenceMassKg, AirWakeConfig.WeightScaling);
            return Mathf.Clamp(factor, MinWeightFactor, MaxWeightFactor);
        }

        private static void EmitBurst(
            AirWakeState state, Vector3 surfacePoint, Vector3 forward, Vector3 right,
            float groundSpeed, float weightFactor, Color32 startColor)
        {
            // Same conversion ContrailEffectPatch uses to feed a world
            // position into a Custom-space (Datum.origin) Emit().
            Vector3 emitPosition = surfacePoint.ToGlobalPosition().AsVector3();

            float inheritedSpeed = AirWakeConfig.InheritedSpeedPercent * 0.01f * groundSpeed;
            float lateralBase = AirWakeConfig.LateralSpeed * weightFactor;
            float randomness = AirWakeConfig.LateralRandomness;

            for (int pair = 0; pair < PairsPerBurst; pair++)
            {
                for (int side = 0; side < 2; side++)
                {
                    // +1 = thrown to the aircraft's right, -1 = to its left.
                    float sideSign = side == 0 ? 1f : -1f;
                    float lateral = lateralBase * Random.Range(1f - randomness, 1f + randomness);
                    float forwardSpeed = inheritedSpeed
                        * Random.Range(1f - InheritedSpeedVariation, 1f + InheritedSpeedVariation);

                    ParticleSystem.EmitParams emitParams = default;
                    emitParams.position = emitPosition;
                    emitParams.velocity =
                        forward * forwardSpeed
                        + right * (sideSign * lateral)
                        + Vector3.up * Random.Range(MinUpKickMetersPerSecond, MaxUpKickMetersPerSecond);
                    emitParams.startLifetime = AirWakeConfig.LifetimeSeconds;
                    emitParams.startSize = Random.Range(MinParticleSize, MaxParticleSize);
                    emitParams.rotation = Random.Range(0f, 360f);
                    emitParams.startColor = startColor;
                    state.Systems[side == 0 ? RightSystem : LeftSystem].Emit(emitParams, 1);
                }
            }

            // Center fill: the left and right halves each throw outward, so
            // the strip directly under the flight path is left empty. These
            // extra puffs spawn with NO lateral speed (still inherit forward
            // speed and get the upward kick), a random start angle, and a
            // random spin direction (whichever of the two systems they land
            // in).
            for (int i = 0; i < AirWakeConfig.CenterFillParticles; i++)
            {
                float forwardSpeed = inheritedSpeed
                    * Random.Range(1f - InheritedSpeedVariation, 1f + InheritedSpeedVariation);

                ParticleSystem.EmitParams emitParams = default;
                emitParams.position = emitPosition;
                emitParams.velocity =
                    forward * forwardSpeed
                    + Vector3.up * Random.Range(MinUpKickMetersPerSecond, MaxUpKickMetersPerSecond);
                emitParams.startLifetime = AirWakeConfig.LifetimeSeconds;
                emitParams.startSize = Random.Range(MinParticleSize, MaxParticleSize);
                emitParams.rotation = Random.Range(0f, 360f);
                emitParams.startColor = startColor;
                state.Systems[Random.value < 0.5f ? RightSystem : LeftSystem].Emit(emitParams, 1);
            }
        }

        // Slows every live particle by exp(-dt / settleTime) -- exact
        // discretized exponential decay, correct for whatever real time has
        // actually elapsed since the last pass (same technique as
        // ContrailEffectPatch.ApplyWind, but decaying toward rest instead of
        // toward the wind).
        private static void DampParticles(AirWakeState state)
        {
            state.TimeSinceDamp += Time.fixedDeltaTime;
            if (state.TimeSinceDamp < DampingIntervalSeconds)
            {
                return;
            }
            float elapsed = state.TimeSinceDamp;
            state.TimeSinceDamp = 0f;

            float keep = Mathf.Exp(-elapsed / Mathf.Max(0.05f, AirWakeConfig.SettleTimeSeconds));
            for (int s = 0; s < state.Systems.Length; s++)
            {
                ParticleSystem system = state.Systems[s];
                if (system == null || system.particleCount == 0)
                {
                    continue;
                }
                int count = system.GetParticles(_particleBuffer);
                for (int i = 0; i < count; i++)
                {
                    _particleBuffer[i].velocity *= keep;
                }
                system.SetParticles(_particleBuffer, count);
            }
        }

        // Sets the spin speed/direction on both systems (once, at creation).
        // ParticleSystem rotation curves are in RADIANS/sec via script.
        private static void ApplySpin(AirWakeState state)
        {
            float radPerSec = AirWakeConfig.SpinSpeedDegrees * Mathf.Deg2Rad;
            float clockwise = ClockwiseSign;

            // Right-thrown dust spins clockwise (as seen from in front),
            // left-thrown counter-clockwise.
            SetSpin(state.Systems[RightSystem], clockwise * radPerSec);
            SetSpin(state.Systems[LeftSystem], -clockwise * radPerSec);
        }

        // Renderer-level settings, applied once at creation. normalDirection
        // decides how a billboard's normals are oriented: 1 (Unity's default)
        // points every normal straight at the camera, so the puff only lights
        // up when the sun is behind the camera; 0 points them out toward the
        // quad's corners, giving dome shading that responds to the sun from
        // any angle. The game's own downwash dust uses 0.
        private static void ApplyRenderSettings(AirWakeState state)
        {
            for (int i = 0; i < state.Renderers.Length; i++)
            {
                if (state.Renderers[i] != null)
                {
                    state.Renderers[i].normalDirection = AirWakeConfig.NormalDirection;
                }
            }
        }

        private static void SetSpin(ParticleSystem system, float signedRadPerSec)
        {
            ParticleSystem.RotationOverLifetimeModule rotation = system.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(
                signedRadPerSec * MinSpinFraction, signedRadPerSec * MaxSpinFraction);
        }

        // Only logs transitions (not every tick), and only with verbose
        // logging on. Emission itself is just "stop calling Emit()" -- the
        // systems keep playing so already-emitted particles finish their own
        // lifetime instead of vanishing.
        private static void SetEmitting(AirWakeState state, Aircraft aircraft, bool emitting, string reason)
        {
            if (state.Emitting == emitting)
            {
                return;
            }
            state.Emitting = emitting;
            if (!emitting)
            {
                state.EmitCounter = 0f;
            }
            if (VerboseLoggingConfig.Enabled.Value)
            {
                SoundPropagation.Log.LogInfo(
                    $"[AirWakeDiag] '{aircraft.name}' -> {(emitting ? "EMIT" : "STOP")} reason=\"{reason}\" "
                    + $"t={Time.timeSinceLevelLoad:F2} speed={aircraft.speed:F1}m/s radarAlt={aircraft.radarAlt:F1}m");
            }
        }

        // Only ever called once per aircraft (EffectAttempted latches
        // regardless of success), the first time it actually qualifies.
        private static void CreateEffect(Aircraft aircraft, AirWakeState state)
        {
            state.EffectAttempted = true;
            bool verbose = VerboseLoggingConfig.Enabled.Value;

            if (aircraft.GetComponentInChildren<RotorShaft>(true) != null)
            {
                state.IsHelicopter = true;
                if (verbose)
                {
                    SoundPropagation.Log.LogInfo($"[AirWakeDiag] '{aircraft.name}' skipped: RotorShaft present (helicopter).");
                }
                return;
            }

            Material material = EffectTextures.GetDustMaterial();
            if (material == null)
            {
                if (verbose)
                {
                    SoundPropagation.Log.LogWarning($"[AirWakeDiag] '{aircraft.name}' skipped: dust material unavailable.");
                }
                return;
            }

            state.EffectObjects = new GameObject[2];
            state.Systems = new ParticleSystem[2];
            state.Renderers = new ParticleSystemRenderer[2];
            for (int i = 0; i < 2; i++)
            {
                state.EffectObjects[i] = new GameObject(i == RightSystem ? "AirWakeEffect_Right" : "AirWakeEffect_Left");
                state.Systems[i] = BuildSystem(state.EffectObjects[i], material);
                state.Renderers[i] = state.EffectObjects[i].GetComponent<ParticleSystemRenderer>();
            }
            ApplySpin(state);
            ApplyRenderSettings(state);

            aircraft.onDisableUnit += _ => DestroyEffect(state);

            if (verbose)
            {
                SoundPropagation.Log.LogInfo($"[AirWakeDiag] '{aircraft.name}' air-wake particle systems created.");
            }
        }

        private static ParticleSystem BuildSystem(GameObject effectObject, Material material)
        {
            effectObject.transform.SetParent(Datum.origin, worldPositionStays: false);

            ParticleSystem system = effectObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            // Long duration, not an emission-cycle length -- emission here is
            // entirely manual. Kept long as a hedge against a known Shuriken
            // gotcha where a looping system's internal time%duration modulo
            // loses float precision after running a long time with a short
            // duration.
            main.duration = 30f;
            main.startLifetime = AirWakeConfig.LifetimeSeconds;
            // Per-particle size/color/velocity all come from EmitParams.
            main.startSpeed = 0f;
            // Custom (tied to Datum.origin), not World -- matches
            // ContrailEffectPatch/Ship.WakeParticles so already-emitted
            // particles stay correct if the floating origin re-centers.
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = Datum.origin;
            main.maxParticles = MaxParticlesPerSystem;
            main.playOnAwake = false;
            // The container's transform is re-anchored to the aircraft every
            // tick while particles live in Custom space, which is exactly the
            // awkward-bounds situation Automatic culling can misjudge --
            // same reasoning (and same fix) as ContrailEffectPatch.
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f; // manual Emit() only

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = false;

            // Billboard self-spin: speed/direction set by ApplySpin().
            ParticleSystem.RotationOverLifetimeModule rotation = system.rotationOverLifetime;
            rotation.enabled = true;

            // Puffs billow out a bit as they age.
            ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 1.2f)));

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient fadeGradient = new Gradient();
            fadeGradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = fadeGradient;

            ParticleSystemRenderer renderer = effectObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.material = material;
            // Matches the real downwash renderer's m_LightProbeUsage: 0,
            // m_ReflectionProbeUsage: 0, m_CastShadows: 0, m_ReceiveShadows: 0,
            // m_SortMode: 1 (Distance). Light probes in particular give a
            // billboard direction-dependent ambient light, which made the
            // dust shade differently depending on which way it faced -- the
            // same problem already fixed for contrails.
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortMode = ParticleSystemSortMode.Distance;

            // Started once and never stopped for this system's life --
            // without it, manual Emit() into a system that never entered the
            // playing state produces nothing visible.
            system.Play();
            return system;
        }

        private static void DestroyEffect(AirWakeState state)
        {
            if (state.EffectObjects == null)
            {
                return;
            }
            for (int i = 0; i < state.EffectObjects.Length; i++)
            {
                if (state.EffectObjects[i] != null)
                {
                    Object.Destroy(state.EffectObjects[i]);
                }
            }
        }
    }
}
