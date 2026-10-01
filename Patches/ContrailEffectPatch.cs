using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Per-engine contrails, anchored at each engine's own real position
    // field rather than raycast or velocity-based positioning at all --
    // JetNozzle's/PropFan's private "thrustTransform" field (already the
    // game's own real exhaust/hub location, used for e.g.
    // JetNozzle.CreateIRSource()), or ConstantSpeedProp's "hubVisible"
    // GameObject where no thrustTransform-equivalent exists. Modeled on two
    // other vanilla systems (both decompiled for reference, see
    // AirWakeEffectPatch's own header for the fuller story):
    //
    // - TrailEmitter (the missile smoke system, Missile.trailEmitters[]):
    //   emits ONE particle at a time via ParticleSystem.Emit(), gated by
    //   DISTANCE traveled (not a flat time-based rate), giving evenly
    //   spaced puffs along the flight path regardless of speed -- the
    //   actual visual signature of a contrail. Also confirms the
    //   position.ToGlobalPosition().AsVector3() conversion used below is
    //   the right way to feed a world position into a Custom-space Emit.
    //
    // - Ship.WakeParticles: simulationSpace = Custom tied to Datum.origin
    //   (this game's floating-origin anchor) instead of World, so already-
    //   emitted particles stay correct if Datum.origin ever re-centers.
    //
    // Deliberately NOT parented to the aircraft/engine at all (unlike the
    // ship's wake, which IS a fixed child of the ship) -- the emitter
    // GameObject is a persistent, aircraft-INDEPENDENT object parented
    // under Datum.origin. Only the engine's current *position value* is
    // read each tick to know where to Emit. If the aircraft/engine is
    // destroyed mid-flight, new emission just stops; already-emitted
    // particles are never parented to the aircraft in the first place, so
    // they keep drifting/fading normally instead of vanishing with it.
    //
    // Deliberately has NO displayDetail/LOD-based disabling (unlike
    // vanilla's own DownwashEffect) -- a contrail is specifically a thing
    // you see from far away; suppressing it for distant/low-detail aircraft
    // would defeat the point.
    //
    // Tried to get the weird vanilla lighting issue (contrails read
    // unnaturally dark when backlit by the sun) fixed by stealing the
    // game's own real cloud shader ("Shader Graphs/MeshCloud",
    // CloudLayer's _SunDirection/_ScatterColor forward-scattering), but
    // couldnt figure out how it actually worked :C -- it's a Shader Graph,
    // and AssetRipper can only recover its property list, not the
    // compiled node-graph math, so there was no way to verify it would
    // even look right on a stretched ribbon instead of a round cloud puff.
    // Reverted; see CreateContrail below -- back to the same alpha-
    // blended Lit particle material as before.
    [HarmonyPatch(typeof(Aircraft), "FixedUpdate")]
    internal static class ContrailEffectPatch
    {
        // Real atmospheric altitude (above sea level), not AGL -- a
        // contrail forms based on the surrounding air's temperature/
        // pressure, which depends on true altitude, not on how tall
        // whatever terrain happens to be directly underneath is. Internal,
        // not private -- MissileContrailPatch shares both these same
        // thresholds for rocket-motor contrails.
        internal const float MinAltitudeMeters = 5000f;

        // Above this, the air's too thin for the same reason contrails
        // don't form indefinitely high in reality -- also keeps a steep-
        // climbing ballistic missile from trailing a contrail the entire
        // way up. Real vanilla precedent for a band shaped like this:
        // VaporEmitter's own (unused by any aircraft) contrail mode gates
        // on altitude > 7500 && altitude < 12500.
        internal const float MaxAltitudeMeters = 12000f;

        // Distance between individual puffs, not a time-based rate -- see
        // TrailEmitter reference above. 100m flat was too sparse-looking;
        // two-tier by speed instead (a flat 50m at high speed still looked
        // fine there, just not below it).
        private const float FastSpeedThresholdKmh = 600f;
        private const float FastSpeedThresholdMetersPerSecond = FastSpeedThresholdKmh / 3.6f;
        private const float SegmentLengthFastMeters = 50f;
        private const float SegmentLengthSlowMeters = 25f;

        // "Stick around for a good while" -- much longer than the air-wake
        // dust's 5-20s, since a real contrail persists for minutes.
        private const float LifetimeSeconds = 180f; // 3 minutes

        // Fully opaque until this point in a particle's life, then fades to
        // 0 by LifetimeSeconds.
        private const float FadeStartSeconds = 150f; // 2.5 minutes

        // Widens from StartThickness to EndThickness over this long, then
        // holds at EndThickness for the rest of the particle's life.
        private const float WidenDurationSeconds = 10f;

        // Cap on ParticleSystem.main.maxParticles -- also sizes the shared
        // GetParticles/SetParticles buffer below, so the two can never drift
        // out of sync with each other.
        private const int MaxParticles = 2000;

        // How quickly a particle's CURRENT velocity is pulled toward the
        // CURRENT real wind, expressed as a time constant (not a flat rate)
        // -- within roughly this many seconds a particle closes ~63% of the
        // gap to wherever the wind is right now; within ~3x this it's
        // essentially fully settled. Applied via explicit per-particle math
        // in ApplyWind (see its own comment for why, not a built-in Unity
        // module). Deliberately small -- real smoke/exhaust is essentially
        // massless and snaps to the local airflow almost immediately, not
        // over a couple of seconds; 1.5s (the first tuning) was way too
        // slow, confirmed by two real symptoms: a decelerating missile
        // could end up BEHIND its own still-fast-moving smoke (particles
        // literally overtaking it, since they weren't shedding the
        // missile's old, faster velocity quickly enough), and a steeply-
        // climbing missile's early puffs kept rising well past where they
        // were actually emitted before settling, in one case ending up
        // past the Karman line. Every particle still keeps chasing whatever
        // the wind actually is for the rest of its life, so a shift or gust
        // partway through a contrail's 3-minute life still bends it.
        private const float WindSettleTimeConstantSeconds = 0.3f;

        // ApplyWind's GetParticles/SetParticles pass copies the system's
        // whole live particle buffer between native and managed code --
        // real cost with many simultaneous long-lived contrails (a
        // saturation launch of several ballistic missiles, each
        // accumulating a large particle count over a long powered climb).
        // Throttled to this interval per emitter instead of running every
        // physics tick (~50Hz); wind/turbulence don't change fast enough
        // for the difference to be visible. Uses the REAL accumulated time
        // since the last update (EngineState.TimeSinceWindUpdate), not an
        // assumed fixed delta, so the decay math above stays correct
        // regardless of how often this actually ends up running.
        private const float WindUpdateIntervalSeconds = 0.1f;

        // Same field/threshold vanilla's own heat-haze effect
        // (JetNozzle.JetParticleParameters.UpdateParticles) uses to decide
        // the engine is actually running -- covers both "engine idled/shut
        // down" and "engine destroyed" the same way vanilla does, since a
        // dead engine's rpmRatio drops too. Applied the same way to the prop
        // engine types below (PropFan/ConstantSpeedProp each carry their own
        // private rpmRatio field with the identical meaning).
        private const float MinRunningRpmRatio = 0.2f;

        private static readonly AccessTools.FieldRef<JetNozzle, Transform> JetThrustTransformRef =
            AccessTools.FieldRefAccess<JetNozzle, Transform>("thrustTransform");
        private static readonly AccessTools.FieldRef<JetNozzle, float> JetRpmRatioRef =
            AccessTools.FieldRefAccess<JetNozzle, float>("rpmRatio");

        // PropFan (twin-prop aircraft like the A-19 Brawler) carries its own
        // "thrustTransform" field, same name and same purpose as JetNozzle's
        // -- confirmed via decompile, and confirmed via the real A-19
        // prefab that it's actually named "hub_L"/"hub_R" and sits at the
        // propeller hub (front of the nacelle), not a rear exhaust point.
        // Doesn't matter functionally here -- Emit() just reads whatever
        // position this transform reports each tick.
        private static readonly AccessTools.FieldRef<PropFan, Transform> PropFanThrustTransformRef =
            AccessTools.FieldRefAccess<PropFan, Transform>("thrustTransform");
        private static readonly AccessTools.FieldRef<PropFan, float> PropFanRpmRatioRef =
            AccessTools.FieldRefAccess<PropFan, float>("rpmRatio");

        // ConstantSpeedProp (single-prop aircraft like the Ci-22 Cricket,
        // and each of the VL-49 Tarantula's four tilting rotors) has no
        // thrustTransform-equivalent field -- its "hubVisible" GameObject
        // (the visible prop hub/spinner mesh, confirmed via the real Ci-22
        // prefab to sit at local (0,0,0) as the pivot for the blade meshes)
        // is the closest equivalent position anchor.
        private static readonly AccessTools.FieldRef<ConstantSpeedProp, GameObject> ConstantSpeedPropHubRef =
            AccessTools.FieldRefAccess<ConstantSpeedProp, GameObject>("hubVisible");
        private static readonly AccessTools.FieldRef<ConstantSpeedProp, float> ConstantSpeedPropRpmRatioRef =
            AccessTools.FieldRefAccess<ConstantSpeedProp, float>("rpmRatio");

        // One shared shape for all three engine types above, so the rest of
        // this file (altitude gate, emission, wind, tuning) never needs to
        // know which kind of engine it's looking at -- just a position and
        // an RPM ratio. TiltWingController (the VL-49's tilt mechanism) is
        // deliberately NOT one of these -- it's purely a mechanical rotator
        // with no thrust/RPM/position data of its own (confirmed via
        // decompile); each of its four rotors is its own ConstantSpeedProp
        // part underneath, already covered above.
        private class EngineSource
        {
            public Component EngineComponent;
            public Transform PositionTransform;
            public System.Func<float> GetRpmRatio;
        }

        private class AircraftState
        {
            public bool EnginesFound;
            public EngineSource[] Engines;
            public float NextLogTime;
        }

        // Internal, not private -- MissileContrailPatch reuses this same
        // per-emitter state shape (and the Create/Drive/Stop methods below)
        // for rocket-motor contrails, since none of it actually references
        // Aircraft/engine-specific types.
        internal class EngineState
        {
            public GameObject EffectObject;
            public ParticleSystem System;
            public float EmitCounter;
            public bool WasEmitting;
            public float TimeSinceWindUpdate;
            public float LastEmitTime;
        }

        private static readonly ConditionalWeakTable<Aircraft, AircraftState> _aircraftStates =
            new ConditionalWeakTable<Aircraft, AircraftState>();
        private static readonly ConditionalWeakTable<Component, EngineState> _engineStates =
            new ConditionalWeakTable<Component, EngineState>();

        // Reused across every contrail's ApplyWind call rather than
        // allocating a fresh array per call -- safe to share since only one
        // ParticleSystem's particles are ever read into/written from it at
        // a time (this is all single-threaded, called synchronously from
        // Aircraft.FixedUpdate/Missile.Motor.Thrust Postfixes).
        private static readonly ParticleSystem.Particle[] _particleBuffer = new ParticleSystem.Particle[MaxParticles];

        private static void Postfix(Aircraft __instance)
        {
            if (!ContrailConfig.Enabled.Value || GameManager.IsHeadless || __instance == null || __instance.rb == null)
            {
                return;
            }

            AircraftState aircraftState = _aircraftStates.GetValue(__instance, _ => new AircraftState());
            if (!aircraftState.EnginesFound)
            {
                // None of JetNozzle/PropFan/ConstantSpeedProp are Transform-
                // children of the aircraft -- confirmed via decompiling
                // UnitPart (it carries its own Rigidbody, i.e. each part is
                // its own physics body joined to the aircraft, not nested in
                // its Transform hierarchy, which is why
                // GetComponentsInChildren<T>() on the aircraft itself always
                // found zero here). Unit.partLookup (public List<UnitPart>,
                // defined on the Unit base class) is the actual part
                // registry -- found once per aircraft, not every tick.
                // First matching engine type wins per part (a part only
                // ever carries one of these).
                var engines = new System.Collections.Generic.List<EngineSource>();
                foreach (UnitPart part in __instance.partLookup)
                {
                    if (part == null)
                    {
                        continue;
                    }

                    JetNozzle jetNozzle = part.GetComponentInChildren<JetNozzle>(true);
                    if (jetNozzle != null)
                    {
                        Transform thrustTransform = JetThrustTransformRef(jetNozzle);
                        if (thrustTransform != null)
                        {
                            engines.Add(new EngineSource
                            {
                                EngineComponent = jetNozzle,
                                PositionTransform = thrustTransform,
                                GetRpmRatio = () => JetRpmRatioRef(jetNozzle),
                            });
                        }
                        continue;
                    }

                    PropFan propFan = part.GetComponentInChildren<PropFan>(true);
                    if (propFan != null)
                    {
                        Transform thrustTransform = PropFanThrustTransformRef(propFan);
                        if (thrustTransform != null)
                        {
                            engines.Add(new EngineSource
                            {
                                EngineComponent = propFan,
                                PositionTransform = thrustTransform,
                                GetRpmRatio = () => PropFanRpmRatioRef(propFan),
                            });
                        }
                        continue;
                    }

                    ConstantSpeedProp constantSpeedProp = part.GetComponentInChildren<ConstantSpeedProp>(true);
                    if (constantSpeedProp != null)
                    {
                        GameObject hub = ConstantSpeedPropHubRef(constantSpeedProp);
                        if (hub != null)
                        {
                            engines.Add(new EngineSource
                            {
                                EngineComponent = constantSpeedProp,
                                PositionTransform = hub.transform,
                                GetRpmRatio = () => ConstantSpeedPropRpmRatioRef(constantSpeedProp),
                            });
                        }
                        continue;
                    }
                }
                aircraftState.Engines = engines.ToArray();
                aircraftState.EnginesFound = true;
                if (VerboseLoggingConfig.Enabled.Value)
                {
                    SoundPropagation.Log.LogInfo(
                        $"[ContrailDiag] '{__instance.name}' found {aircraftState.Engines.Length} engine(s) "
                        + $"across {__instance.partLookup.Count} part(s).");
                }
            }
            if (aircraftState.Engines == null || aircraftState.Engines.Length == 0)
            {
                return;
            }

            float altitude = __instance.transform.position.y - Datum.LocalSeaY;
            bool shouldEmit = altitude >= MinAltitudeMeters && altitude <= MaxAltitudeMeters;
            Vector3 velocity = __instance.rb.velocity;

            bool logNow = VerboseLoggingConfig.Enabled.Value && Time.timeSinceLevelLoad >= aircraftState.NextLogTime;
            if (logNow)
            {
                aircraftState.NextLogTime = Time.timeSinceLevelLoad + 1f;
                SoundPropagation.Log.LogInfo(
                    $"[ContrailDiag] '{__instance.name}' altitude={altitude:F0}m (range={MinAltitudeMeters:F0}-{MaxAltitudeMeters:F0}) "
                    + $"shouldEmit={shouldEmit} engines={aircraftState.Engines.Length}");
            }

            foreach (EngineSource engine in aircraftState.Engines)
            {
                if (engine.EngineComponent == null || engine.PositionTransform == null)
                {
                    continue;
                }
                float rpmRatio = engine.GetRpmRatio();
                EngineState state = _engineStates.GetValue(engine.EngineComponent, _ => new EngineState());

                if (logNow)
                {
                    SoundPropagation.Log.LogInfo(
                        $"[ContrailDiag] '{__instance.name}' engine rpmRatio={rpmRatio:F2} "
                        + $"(threshold={MinRunningRpmRatio:F2}) effectCreated={state.EffectObject != null} "
                        + (state.System != null
                            ? $"particleCount={state.System.particleCount} isPlaying={state.System.isPlaying}"
                            : "system=null"));
                }

                if (!shouldEmit || rpmRatio < MinRunningRpmRatio)
                {
                    StopEmitting(state);
                    continue;
                }

                DriveContrail(state, engine.PositionTransform.position, velocity);
            }
        }

        // Shared by both the per-engine loop above and MissileContrailPatch
        // -- entirely generic over "a position and a velocity", no
        // Aircraft/engine-specific types involved, so a rocket motor's
        // contrail goes through the exact same creation/wind/tuning/
        // emission logic as a jet's.
        internal static void DriveContrail(EngineState state, Vector3 position, Vector3 velocity)
        {
            if (state.EffectObject == null)
            {
                CreateContrail(state);
            }
            if (state.System == null)
            {
                return;
            }

            if (!state.WasEmitting)
            {
                // Resuming after a real gap (dropped below the
                // altitude/RPM floor, then came back above it) -- Ribbon
                // mode connects ALL currently-alive particles into one
                // continuous strip with no concept of "this was a gap,
                // start fresh." Our particles live up to 180s, so ones from
                // before the gap are very likely still alive, and without
                // this they'd bridge straight across to the first new one
                // -- exactly the long straight artifact reported. Clearing
                // here means the old segment disappears abruptly rather
                // than fading on its own schedule, but that's the trade for
                // not drawing a fake straight line across the gap.
                state.System.Clear();
                state.EmitCounter = 0f;
            }
            state.WasEmitting = true;
            state.LastEmitTime = Time.time;

            ApplyWind(state, position);
            ApplyTuning(state.System);

            // The container GameObject's own transform never moved
            // otherwise (Custom sim space decouples individual particle
            // positions from it entirely, which is why it was left sitting
            // wherever it was created, at Datum.origin's own position).
            // Keeping it tracking the live engine/missile instead gives
            // Unity's renderer bounds/transparency-sort computation --
            // which partly relies on the renderer's own transform, not
            // just simulated particle data -- a sane, current position to
            // work from instead of a frozen, far-away one. Updated every
            // active tick, not just on the ticks that actually Emit.
            state.EffectObject.transform.position = position;

            float segmentLength = velocity.magnitude >= FastSpeedThresholdMetersPerSecond
                ? SegmentLengthFastMeters
                : SegmentLengthSlowMeters;
            state.EmitCounter += Time.fixedDeltaTime * velocity.magnitude / segmentLength;
            if (state.EmitCounter < 1f)
            {
                return;
            }
            state.EmitCounter = 0f;

            GlobalPosition emitGlobalPosition = position.ToGlobalPosition();

            ParticleSystem.EmitParams emitParams = default;
            emitParams.position = emitGlobalPosition.AsVector3();
            // Deliberately NOT the aircraft's/missile's own velocity.
            // Confirmed as an actual bug, not just a visual nicety: a
            // particle spawned at the vehicle's full speed only gets
            // corrected toward wind on ApplyWind's next THROTTLED pass (up
            // to WindUpdateIntervalSeconds later), and a fast vehicle can
            // emit several particles within that single window -- more
            // than enough for a decelerating or maneuvering missile to end
            // up trailing behind its own still-fast, not-yet-corrected
            // smoke. Real smoke/exhaust is close to massless and matches
            // the local air velocity from the instant it exists, which is
            // also why a real contrail reads as a static line hanging in
            // the air behind a fast aircraft rather than a string of puffs
            // that need time to catch up -- spawning directly at the
            // CURRENT wind (same technique vanilla's own CloudLayer uses:
            // "emitParams.velocity = windVelocity", no vehicle speed
            // involved at all) removes the excess velocity at the source
            // instead of racing to decay it away after the fact. ApplyWind
            // still matters for existing particles when wind itself shifts
            // over a contrail's 3-minute life, just never has a large
            // vehicle-speed gap to close anymore.
            emitParams.velocity = LevelInfo.i != null ? LevelInfo.i.GetWind(emitGlobalPosition) : Vector3.zero;
            emitParams.startLifetime = LifetimeSeconds;
            state.System.Emit(emitParams, 1);
        }

        // Marks an emitter as "not currently active" so the next time it
        // resumes (DriveContrail sees WasEmitting == false), the gap-
        // bridging Clear() above runs. Doesn't stop/destroy anything by
        // itself -- an inactive emitter simply never gets DriveContrail
        // called again until conditions are met.
        internal static void StopEmitting(EngineState state)
        {
            state.WasEmitting = false;
        }

        // Re-applied periodically while a contrail is actively fed (not
        // just at creation) so ALREADY-drifting puffs keep getting the
        // current real wind rather than whatever it was when they were
        // first created -- this game's own wind (LevelInfo.windVelocity)
        // genuinely shifts and has turbulence, and real contrails visibly
        // bend when it does.
        //
        // Deliberately NOT built on Unity's ForceOverLifetime/
        // LimitVelocityOverLifetimeModule (an earlier version was, and it's
        // exactly why wind had no visible effect at all -- the drag module
        // needed to kill the aircraft's inherited spawn velocity also
        // continuously fought the wind force, and getting their combined
        // steady-state behavior right depends on ParticleSystem's internal
        // drag formula, which is native engine code with no public,
        // verifiable formula -- confirmed by checking Unity's own official
        // docs, which only describe drag/limit/dampen in general terms,
        // not the actual math). This instead reads every live particle's
        // REAL current velocity back out via GetParticles() and explicitly
        // nudges it toward the CURRENT wind by a fixed fraction of the
        // remaining gap (exact discretized exponential decay toward a
        // moving target, not a spring/force simulation) -- fully
        // deterministic, and naturally handles both jobs with the same one
        // line: a freshly-emitted particle (large gap between its
        // aircraft-inherited velocity and the much smaller wind) closes
        // most of that gap almost immediately, while a long-settled
        // particle (already close to wind) just keeps tracking it,
        // including through a shift or gust partway through its life.
        //
        // Throttled to WindUpdateIntervalSeconds per emitter (see its own
        // comment) rather than running every physics tick -- GetParticles/
        // SetParticles copies the system's whole live particle buffer
        // between native and managed code, real cost with many
        // simultaneous long-lived contrails. Uses the real accumulated time
        // since this emitter's last update, not an assumed fixed delta, so
        // the decay math stays correct regardless of the actual interval.
        internal static void ApplyWind(EngineState state, Vector3 samplePosition)
        {
            state.TimeSinceWindUpdate += Time.fixedDeltaTime;
            if (state.TimeSinceWindUpdate < WindUpdateIntervalSeconds)
            {
                return;
            }
            float elapsed = state.TimeSinceWindUpdate;
            state.TimeSinceWindUpdate = 0f;

            // LevelInfo.GetWind(GlobalPosition) is the game's own real,
            // networked, mission-wide wind (with position-based Perlin-
            // noise turbulence) -- NOT a synthetic value, found via
            // decompiling LevelInfo (windVelocity/windTurbulence/
            // windSpeed, all SyncVars) after initially missing it by only
            // searching class NAMES for "wind" instead of field usage.
            Vector3 wind = LevelInfo.i != null
                ? LevelInfo.i.GetWind(samplePosition.ToGlobalPosition())
                : Vector3.zero;

            int count = state.System.GetParticles(_particleBuffer);
            if (count == 0)
            {
                return;
            }

            // 1 - e^(-elapsed/tau): the exact fraction of the remaining gap
            // to close for a time-constant-tau exponential decay over
            // however long it's actually been since the last update, not
            // just "tau * elapsed" -- correct regardless of how often this
            // ends up running, rather than an approximation that only holds
            // for small elapsed values.
            float t = 1f - Mathf.Exp(-elapsed / WindSettleTimeConstantSeconds);
            for (int i = 0; i < count; i++)
            {
                _particleBuffer[i].velocity = Vector3.Lerp(_particleBuffer[i].velocity, wind, t);
            }
            state.System.SetParticles(_particleBuffer, count);
        }

        // Re-applied every tick a contrail is actively fed (not just at
        // creation) -- cheap enough to not bother caching.
        internal static void ApplyTuning(ParticleSystem system)
        {
            float startThickness = ContrailConfig.StartThickness;
            float endThickness = ContrailConfig.EndThickness;

            ParticleSystem.MainModule main = system.main;
            main.startSize = new ParticleSystem.MinMaxCurve(startThickness);

            float widenFraction = WidenDurationSeconds / LifetimeSeconds;
            float endMultiplier = endThickness / startThickness;

            // Multiplier relative to main.startSize (== startThickness):
            // 1x at spawn, ramps to endThickness by WidenDurationSeconds
            // in, then holds flat at endThickness for the rest of the
            // particle's life.
            ParticleSystem.SizeOverLifetimeModule sizeOverLifetime = system.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(widenFraction, endMultiplier),
                new Keyframe(1f, endMultiplier)));

            // Fully opaque until FadeStartSeconds in, then fades to 0 by
            // LifetimeSeconds. A brief fade-in right at spawn avoids a
            // harsh pop-in.
            float fadeStartFraction = FadeStartSeconds / LifetimeSeconds;
            const float fadeInFraction = 0.01f;
            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = system.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient fadeGradient = new Gradient();
            fadeGradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, fadeInFraction),
                    new GradientAlphaKey(1f, fadeStartFraction),
                    new GradientAlphaKey(0f, 1f),
                });
            colorOverLifetime.color = fadeGradient;
        }

        private static void CreateContrail(EngineState state)
        {
            Material material = EffectTextures.GetContrailMaterial();
            if (material == null)
            {
                return;
            }

            // Parented to Datum.origin (aircraft-independent), NOT to the
            // engine/aircraft -- see this file's header for why. Position/
            // rotation are irrelevant here since every particle's position
            // and velocity are set explicitly per Emit() call.
            GameObject effectObject = new GameObject("Contrail");
            effectObject.transform.SetParent(Datum.origin, worldPositionStays: false);

            ParticleSystem system = effectObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.duration = 30f;
            main.startLifetime = LifetimeSeconds;
            // Real start size/size-over-lifetime/color-over-lifetime curves
            // are set by ApplyTuning() every active tick -- these are just
            // placeholder defaults until the first ApplyTuning() call.
            main.startSize = new ParticleSystem.MinMaxCurve(ContrailConfig.StartThickness);
            // Plain white -- now that the material is a Lit (not Unlit)
            // shader, real scene lighting does the work of making it read
            // as properly bright in daylight; no artificial overbright
            // push needed (and that push was also why it stayed fullbright
            // at night, since Unlit ignored lighting entirely).
            main.startColor = Color.white;
            // Velocity comes entirely from emitParams.velocity per Emit
            // call (the aircraft's own velocity at that instant), not a
            // shape-driven launch speed.
            main.startSpeed = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = Datum.origin;
            main.maxParticles = MaxParticles;
            main.playOnAwake = false;
            // Default (Automatic) pauses simulation AND stops rendering
            // once Unity decides the system's computed bounds are off-
            // camera -- a real risk here since this container's own
            // transform is continually re-anchored to the aircraft's
            // CURRENT position (below) while the actual Custom-space
            // ribbon data can trail many kilometers behind it, which is
            // exactly the kind of large/awkward bounds situation where
            // Automatic's culling can misjudge visibility and the whole
            // trail flickers out and back. Confirmed vanilla itself never
            // leaves this at Automatic either -- the real wingtip-vortex
            // ParticleSystem explicitly sets it to PauseAndCatchup, even
            // though its bounds are tiny and short-lived by comparison.
            // AlwaysSimulate is the actual Unity-provided fix for a system
            // whose bounds are hard to compute/track correctly, not a
            // workaround -- always simulates and renders regardless of
            // computed bounds.
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = true;
            // Emission is entirely manual (Emit() calls in Postfix), not
            // automatic -- see TrailEmitter reference in this file's header.
            emission.rateOverTime = 0f;

            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = false;

            // No LimitVelocityOverLifetime/ForceOverLifetime here -- ApplyWind
            // handles killing the aircraft's inherited spawn velocity AND
            // tracking live wind itself, via explicit per-particle math
            // (GetParticles/SetParticles) rather than these built-in
            // modules. See ApplyWind's own comment for why.

            // Real size-over-lifetime/color-over-lifetime curves are set by
            // ApplyTuning() every active tick (Start/End Thickness config-
            // driven, live-tunable) -- not set here.

            // Ribbon trails -- this is the actual missile-smoke/wingtip-
            // vortex technique: connects consecutive alive particles from
            // this system into ONE continuous strip of geometry (using the
            // assigned texture along its length), rather than each particle
            // rendering as its own separate floating billboard with visible
            // gaps between them. Naturally drops from the tail end as old
            // particles die and extends at the head end as new ones are
            // emitted -- exactly the "gas trail" use case this Unity
            // feature exists for.
            ParticleSystem.TrailModule trails = system.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.Ribbon;
            trails.ratio = 1f;
            trails.lifetime = new ParticleSystem.MinMaxCurve(1f);
            // 0.2m, matching the real vanilla wingtip-vortex ribbon. A
            // one-off screen-covering haze was seen once at this value and
            // suspected as the cause, but it didn't reproduce on retest --
            // confirmed fine, keeping the real vanilla value.
            trails.minVertexDistance = 0.2f;
            // RepeatPerSegment (not Stretch) -- Stretch maps one copy of
            // the texture across the ENTIRE ribbon length, smearing the
            // source's fluffy detail into smoothness over what can be a
            // very long trail. RepeatPerSegment tiles it at natural scale
            // once per ribbon segment (between consecutive puffs) instead,
            // preserving the wispy look while still being one connected
            // ribbon.
            trails.textureMode = ParticleSystemTrailTextureMode.RepeatPerSegment;
            trails.worldSpace = false;
            trails.dieWithParticles = true;
            trails.ribbonCount = 1;
            // All four confirmed from the real wingtip-vortex TrailModule
            // dump, none of which were being set before (silently sitting
            // at whatever Unity's own defaults are):
            // - generateLightingData: without real per-vertex normals on
            //   the ribbon mesh, a Lit shader has nothing correct to shade
            //   against, which fits a color error that varies by viewing
            //   angle rather than being a flat, constant tint.
            // - inheritParticleColor: whether the ribbon actually uses each
            //   particle's own emitted color/alpha at all.
            trails.generateLightingData = true;
            trails.inheritParticleColor = true;
            trails.sizeAffectsWidth = true;
            trails.sizeAffectsLifetime = false;
            trails.shadowBias = 0.5f;

            // Individual per-particle billboards are hidden (None) --
            // the ribbon alone carries the whole visual, matching the
            // reference's connected-line look rather than a string of
            // separate puffs.
            ParticleSystemRenderer renderer = effectObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.None;
            renderer.material = material;
            renderer.trailMaterial = material;
            // Confirmed from the real wingtip-vortex renderer dump
            // (m_LightProbeUsage: 0, m_ReflectionProbeUsage: 0) -- without
            // this, a billboard/ribbon samples per-position, per-direction
            // ambient light (light probes), which is exactly what made it
            // read as white against sky but pink/gray against sunlit
            // ground depending on which way it's facing. Vanilla disables
            // both, falling back to a simpler, direction-independent
            // ambient contribution instead, which is why the real thing
            // reads as consistently white regardless of backdrop.
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            // Matches the real renderer's m_CastShadows: 0 / m_ReceiveShadows: 0.
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            state.EffectObject = effectObject;
            state.System = system;

            // The container is aircraft-independent (so already-emitted
            // particles outlive a destroyed aircraft), which also means
            // nothing else would ever destroy it -- see ContrailAutoCleanup.
            effectObject.AddComponent<ContrailAutoCleanup>().State = state;

            // Started once, immediately, and never stopped for the rest of
            // this system's life -- unlike the dust effect, contrails don't
            // toggle Play/Stop (gating is just "don't call Emit" when
            // conditions aren't met). Without this, manual Emit() calls
            // into a system that never entered the playing state don't
            // produce visible particles -- confirmed missing here the same
            // way it was originally missing (and fixed) in the dust effect.
            system.Play();
        }
    }

    // Destroys a contrail container once it has nothing left to do: no
    // particles alive and nothing emitted into it for a while (the aircraft
    // or missile was destroyed, or dropped below the altitude/RPM floor and
    // its trail has fully faded). Without this, every aircraft engine and
    // missile that ever climbed above the contrail altitude left a
    // permanent AlwaysSimulate particle system behind until the mission
    // ended. Clearing the state's references lets DriveContrail simply build
    // a fresh container if that engine climbs back into the band.
    internal sealed class ContrailAutoCleanup : MonoBehaviour
    {
        private const float CheckIntervalSeconds = 5f;
        private const float IdleSecondsBeforeCleanup = 5f;

        internal ContrailEffectPatch.EngineState State;
        private float _nextCheckTime;

        private void Update()
        {
            if (Time.unscaledTime < _nextCheckTime)
            {
                return;
            }
            _nextCheckTime = Time.unscaledTime + CheckIntervalSeconds;

            if (State == null || State.System == null)
            {
                Destroy(gameObject);
                return;
            }
            if (Time.time - State.LastEmitTime > IdleSecondsBeforeCleanup && State.System.particleCount == 0)
            {
                State.EffectObject = null;
                State.System = null;
                State.WasEmitting = false;
                Destroy(gameObject);
            }
        }
    }
}
