using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Delays a one-shot sound so it's heard only once the speed-of-sound
    // propagation front (from a fixed point in space -- the source doesn't
    // "carry" the sound with it after firing) reaches the listener, instead
    // of playing instantly like a normal Unity 3D AudioSource does. Sea-level
    // speed at all altitudes, per explicit instruction -- no altitude lookup.
    // Driven by SoundPropagationDriver's Update() since this is a static
    // utility and can't run its own Update().
    internal static class SoundPropagation
    {
        // Temporary diagnostic logging for the engine volume-vs-distance
        // investigation -- dumps exactly what CopyAudioSourceSettings reads
        // from the real source and writes onto the clone, once per newly
        // created engine clone, straight to BepInEx's LogOutput.log. Removes
        // any ambiguity from manually hunting down the right AudioSource in
        // UnityExplorer (e.g. picking the wrong engine on a multi-engine
        // aircraft) since this reads the exact same object reference the
        // patches themselves are using.
        internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("QOL_Realisim_Fixes.SoundPropagation");

        private const float SpeedOfSoundMps = 340f;

        private class PendingShot
        {
            public AudioSource Source;
            public AudioClip Clip;
            public GameObject TempObject;
            public float SpawnTime;

            // Only the *extra* delay (e.g. the sonic boom's trailing-shock
            // gap) is stored -- the base propagation delay is recomputed
            // fresh from the listener's CURRENT position every tick (see
            // Tick()'s pending-shot loop) instead of being baked in once
            // here, since the listener is very often still moving (flying)
            // during the wait. TempObject itself is the fixed emission
            // point (parented under Datum.origin, so floating-origin shifts
            // don't matter) -- only the listener's position needs
            // re-sampling.
            public float ExtraDelaySeconds;

            // 0 = play the clip out in full (original behavior). Above
            // zero, playback is cut short this many seconds after it
            // actually starts -- used to truncate a sonic boom's leading
            // shock so a closely-following second copy of the same clip
            // (the trailing shock) isn't stepping on/overlapping a full-
            // length copy of itself, which read as a muddy echo rather than
            // a crisp double-crack.
            public float ClipDurationSeconds;
            public bool Started;
            public float PlayStartTime;

            // Distance-based lowpass -- see ComputeLowpassCutoffHz. Set
            // once, right when the shot actually starts playing (using the
            // listener's live distance at that moment), not re-evaluated
            // continuously afterward: the emission point is fixed and these
            // clips are short, so distance barely changes over the course
            // of playback.
            public AudioLowPassFilter LowpassFilter;

            // Static per-call request (see ScheduleDelayedOneShot's own
            // highpassCutoffHz param, e.g. the bullet crack's fixed 4kHz
            // tone-shaping cut) merged with the live cockpit highpass at
            // play time -- whichever wants MORE cut (a higher cutoff) wins,
            // same Mathf.Max-vs-Min relationship the lowpass merge uses in
            // the opposite direction.
            public float RequestedHighpassCutoffHz;
            public AudioHighPassFilter HighpassFilter;

            // Set only for impacts confirmed (at schedule time, via
            // TryGetOwnAirframeHitDistance) to have struck the LOCAL
            // player's own aircraft -- null for everything else (impacts
            // on other units, ground/water, anything far away), which
            // keep using the normal distance/cockpit cutoff merge below.
            // Distance to the cockpit specifically (not the listener/
            // camera), computed once at schedule time: the delay before
            // an own-airframe hit actually plays is at most a couple
            // hundredths of a second (see NearFieldNoDelayMeters), so
            // re-sampling every tick would buy nothing.
            public float? OwnHitDistanceToCockpitMeters;

            // 1 = normal, flat cockpit-filter strength (every existing
            // caller). Below 1 for mechanical sounds heard mostly through
            // the airframe itself (bay doors, gear, wing sweep) rather
            // than open air -- see MechanicalSoundConfig. Ignored when
            // OwnHitDistanceToCockpitMeters is set; the two features are
            // never relevant to the same sound.
            public float CockpitMuffleMultiplier = 1f;
        }

        // A one-shot the listener has outrun (receding faster than the
        // speed of sound, so the propagation-delay check can never be
        // satisfied) would otherwise sit in _pending forever -- correct in
        // principle (you really would never hear it), but still needs a
        // cutoff so the list doesn't grow unbounded over a long session.
        // Matches the ~20km/340m/s ceiling MaxTrackingDistanceMeters
        // already implies elsewhere.
        private const float MaxPendingWaitSeconds = 60f;

        private static readonly List<PendingShot> _pending = new List<PendingShot>();
        private static GameObject _driverObject;

        // Continuous/rotary weapons: the sustained loop (Gun's sources[1])
        // is suppressed at the real source entirely (see GunShotSoundPatch)
        // and replaced end-to-end by a tracked clone here, so there's only
        // ever one audible copy -- close-up it starts almost instantly
        // (negligible delay), at range it lags realistically, same as the
        // one-shot handling above.
        private class LoopState
        {
            public AudioSource CloneSource;
            public GameObject CloneObject;
            public AudioSource Template;
            public AudioClip FireEndClip;
            public bool Started;
            public bool PendingStop;
            public bool AudioStopped;
            public float DelaySeconds;
            public float StartTime;
            public float ScheduledStopTime;
            public float CleanupTime;
            public bool ModifyPitch;
            public float TargetPitch;
            public float StartPitch;
            public float PitchClimbRate;
            public float LastShotTime;
            public float ObservedInterval;
            public AudioLowPassFilter LowpassFilter;
            public AudioHighPassFilter HighpassFilter;
            public bool FadingOut;
            public float FadeOutStartVolume;
            public float FadeOutStartTime;
            public float NormalVolume;
        }

        // How long a loop's tracking entry outlives its own audio actually
        // going silent, before being fully removed. Firing an automatic
        // weapon in real, continuous bursts can still have brief gaps
        // between shots (frame timing, the game's own bullet-queue jitter)
        // that momentarily exceed fireInterval -- without this grace window,
        // such a gap would fully remove the entry, and the very next shot
        // would see no active loop and replay the spool-up "fireStart" bang
        // as if a whole new burst had begun, even mid-burst. Keeping the
        // entry (just silenced) around a bit longer lets NotifyLoopActive's
        // existing resume path reuse it instead.
        private const float LoopCleanupGraceSeconds = 3f;

        // AudioSource.Stop() cuts playback instantly, at whatever sample
        // the waveform happens to be at -- essentially never a zero
        // crossing, so it reliably produced an audible click/pop (reported
        // as "popping in the gunloop"). A short linear fade to silence
        // first removes the discontinuity; well within LoopCleanupGraceSeconds
        // above, so it doesn't need any extra headroom of its own.
        private const float GunLoopFadeOutSeconds = 0.05f;

        private static readonly Dictionary<Gun, LoopState> _loops = new Dictionary<Gun, LoopState>();
        private static readonly List<Gun> _loopsToRemove = new List<Gun>();

        // fireInterval is private on Gun. Only used to seed a brand-new
        // loop's initial ObservedInterval guess (see NotifyLoopActive) --
        // TickLoops()'s actual stop-detection uses the weapon's own
        // observed cadence instead, since this field doesn't reliably
        // reflect a weapon's real firing rate (e.g. mid-spin-up).
        private static readonly AccessTools.FieldRef<Gun, float> FireIntervalRef =
            AccessTools.FieldRefAccess<Gun, float>("fireInterval");

        // Anything within this radius of the listener plays with no delay
        // at all -- a few meters/milliseconds of "lag" would never be
        // perceptible anyway, and it sidesteps a nastier problem: a live
        // distance/speed-of-sound calculation would otherwise make a
        // sound source's own audio permanently delayed (or never arrive)
        // once it's moving away from the listener at a meaningful fraction
        // of the speed of sound -- e.g. the shooter's own gun, mounted on
        // and moving with their own aircraft, at transonic speed. Simpler
        // and more general than special-casing "is the listener riding the
        // same unit this came from," and it happens to fix that case too
        // since gun-to-cockpit distance is always well under this.
        private const float NearFieldNoDelayMeters = 10f;

        private static float ComputeDelaySeconds(Vector3 origin)
        {
            CameraStateManager cameraStateManager = SceneSingleton<CameraStateManager>.i;
            if (cameraStateManager == null)
            {
                return 0f;
            }
            float distance = Vector3.Distance(origin, cameraStateManager.transform.position);
            if (distance <= NearFieldNoDelayMeters)
            {
                return 0f;
            }
            return distance / SpeedOfSoundMps;
        }

        private static float GetDistanceToListener(Vector3 position)
        {
            CameraStateManager cameraStateManager = SceneSingleton<CameraStateManager>.i;
            return cameraStateManager != null ? Vector3.Distance(position, cameraStateManager.transform.position) : 0f;
        }

        // Real atmospheric absorption attenuates high frequencies far more
        // than low ones over distance -- a big part of why a distant
        // gunshot/impact/boom sounds duller than the same sound up close.
        // Modeled here off real absorption-coefficient data (ISO 9613-1
        // style tables, ~20C/70% RH, a commonly cited "average outdoor day"
        // reference): attenuation is roughly proportional to frequency
        // SQUARED once above a couple kHz (~33dB/km at 4kHz, ~117dB/km at
        // 8kHz in those tables -- doubling frequency roughly quadruples the
        // loss). Fitting attenuation(f, d) = k * f^2 * d to the 4kHz/33dB
        // point gives k (kept fixed -- it's real physical data, not a taste
        // knob), then solving for the frequency that has lost
        // AttenuationThresholdDb worth of level at a given distance gives a
        // physically-grounded cutoff curve instead of an arbitrary lerp.
        // AttenuationThresholdDb/ResonanceQ/MinCutoffHz ARE exposed (see
        // DistanceLowpassConfig) since "how much" and "how harsh" are
        // genuinely a matter of taste worth live-tuning by ear in-game
        // rather than guessing and rebuilding each time.
        private const float AbsorptionCoefficient = 2.05e-6f; // dB per km per Hz^2, fit to ~33dB/km @ 4kHz
        private const float LowpassMaxCutoffHz = 22000f; // Unity's own ceiling for AudioLowPassFilter, effectively unfiltered

        // A resonant (Q>1) filter still colors/rings audibly right at its
        // own cutoff frequency even when that cutoff is parked at the "no
        // muffling" edge of its range (22000Hz for lowpass, 10Hz for
        // highpass) -- the comment above calls 22000Hz "effectively
        // unfiltered," which is only true at Q=1. A resonance boost sitting
        // at ~22kHz (near the top of human hearing) rings as a persistent
        // high-pitched whine layered under the real sound, masked by a
        // loud/broadband source but exposed against a quiet one (e.g. an
        // idling engine at low RPM) -- root-caused this way after this
        // exact filter chain went from silently inert to actually running
        // for the first time (the AudioSource.bypassEffects fix). Since
        // Resonance is meant to shape how HARD an actually-active cutoff
        // bites, not to season a filter that isn't cutting anything, it's
        // reset to Unity's own neutral Q (1, no boost) whenever the cutoff
        // sits at that fully-open edge, applied everywhere this mod sets
        // either filter's resonance.
        // A strict equality-adjacent comparison here is fragile: the cutoff
        // reaching this point is often the result of one or more chained
        // Mathf.Lerp calls (own-hit distance blend, internal-mechanical
        // multiplier, ejection-release fade, airframe-opening multiplier),
        // and while Lerp(a, b, t) is exact in IEEE-754 at t=0 or t=1 for
        // most round values, it isn't guaranteed to be for every value
        // these blends can produce -- landing a fraction of a Hz short of
        // the exact edge was enough to flip this from "fully open" back to
        // "actively cutting," reintroducing the exact resonance-ringing
        // artifact the original fix eliminated, just intermittently
        // instead of constantly (reported as a warble that came and went
        // with camera/view changes, since those are what drive which blend
        // paths get chained together on a given frame). A small epsilon
        // absorbs that without meaningfully changing when resonance
        // actually kicks in for a genuinely-cutting filter.
        private const float ResonanceEdgeEpsilonHz = 1f;

        private static float LowpassResonanceFor(float cutoffHz)
        {
            return cutoffHz < LowpassMaxCutoffHz - ResonanceEdgeEpsilonHz ? DistanceLowpassConfig.ResonanceQ : 1f;
        }

        private static float HighpassResonanceFor(float cutoffHz)
        {
            return cutoffHz > HighpassMinCutoffHz + ResonanceEdgeEpsilonHz ? DistanceLowpassConfig.ResonanceQ : 1f;
        }

        // True while the camera is actually in the cockpit view state --
        // same check ShakeCamera uses internally. Shared here since both
        // the cockpit lowpass below and BulletCrackPatch's own-fire
        // exclusion need the identical check.
        internal static bool IsInCockpitView()
        {
            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            return cam != null && cam.currentState == cam.cockpitState;
        }

        // Blends a cutoff back toward "fully open" by however much a
        // caller wants LESS than the full cockpit-filter effect applied to
        // one specific source or sound, rather than the flat cutoff
        // everything else uses -- shared by the internal-mechanical-sound
        // multiplier below and ApplyCockpitOnlyLowpass's own overload.
        // multiplier >= 1 is a no-op (returns mutedValue unchanged), so
        // every existing caller that doesn't pass one keeps behaving
        // exactly as before.
        private static float ApplyMuffleMultiplier(float openValue, float mutedValue, float multiplier)
        {
            return multiplier >= 1f ? mutedValue : Mathf.Lerp(openValue, mutedValue, Mathf.Clamp01(multiplier));
        }

        private static float ComputeLowpassCutoffHz(float distanceMeters, float cockpitMuffleMultiplier = 1f)
        {
            float cutoff = LowpassMaxCutoffHz;
            if (DistanceLowpassConfig.Enabled.Value)
            {
                float distanceKm = distanceMeters / 1000f;
                if (distanceKm > 0f)
                {
                    cutoff = Mathf.Sqrt(DistanceLowpassConfig.AttenuationThresholdDb / (AbsorptionCoefficient * distanceKm));
                    cutoff = Mathf.Clamp(cutoff, DistanceLowpassConfig.MinCutoffHz, LowpassMaxCutoffHz);
                }
            }

            // Cockpit muffling is applied here, per-source, alongside the
            // distance lowpass, rather than as a separate filter on the
            // AudioListener -- a listener-attached filter only affects
            // sources with bypassListenerEffects=false, and this game's own
            // sound sources widely default that flag to true, so a
            // listener-wide filter had no audible effect on almost
            // anything. Merging into the same per-source cutoff this mod
            // already sets directly sidesteps that entirely, and naturally
            // only touches sounds this mod manages (which are overwhelmingly
            // external-origin: gunfire, impacts, booms, engines, cracks) --
            // vanilla's own RWR/interface/menu/voice audio was never routed
            // through here in the first place, so it's unaffected with no
            // exemption logic needed.
            float cockpitCutoff = ApplyMuffleMultiplier(LowpassMaxCutoffHz, ComputeCockpitLowpassCutoffOnly(), cockpitMuffleMultiplier);
            cutoff = Mathf.Min(cutoff, cockpitCutoff);

            return cutoff;
        }

        // Just the cockpit component of the lowpass, with no distance
        // factored in at all -- used by ComputeLowpassCutoffHz above (this
        // mod's own sources, merged with its own distance calc) AND by
        // systems with their OWN independent, already-correct distance
        // simulation (e.g. ExplosionAudioManager's speed-of-sound delay +
        // distance cutoff) that just need the cockpit muffling layered on
        // top without re-deriving or duplicating distance attenuation a
        // second time.
        // Depends only on global state (config, camera view, ejection/
        // airframe blends) -- never on which source is asking -- yet this
        // gets called by every ApplyCockpitOnlyLowpass invocation (a dozen-
        // plus call sites) plus every TickLoops/TickEngines/pending-shot
        // iteration, every tick. Cached per rendered frame (Time.frameCount,
        // not a time-based throttle -- a stale value for a fraction of a
        // frame is meaningless here, unlike the 0.5s airframe-multiplier
        // throttle below, which trades a coarser staleness window for
        // skipping actual hardpoint-list work) so all of those calls within
        // the same frame share one computation instead of repeating it.
        private static int _cachedCockpitLowpassFrame = -1;
        private static float _cachedCockpitLowpassCutoff;

        internal static float ComputeCockpitLowpassCutoffOnly()
        {
            if (_cachedCockpitLowpassFrame == Time.frameCount)
            {
                return _cachedCockpitLowpassCutoff;
            }
            float cutoff = CockpitLowpassConfig.Enabled.Value && IsInCockpitView()
                ? CockpitLowpassConfig.CutoffHz
                : LowpassMaxCutoffHz;
            // Airframe-specific openings (e.g. the Ibis's door gun mounts)
            // blend the intended cutoff back toward fully open BEFORE the
            // ejection-release fade below, so the two compose the same way
            // the own-hit-distance blend composes with the flat cutoff --
            // a multiplier of 1 (the overwhelming majority of airframes,
            // always when the feature is off) leaves this line a no-op.
            cutoff = Mathf.Lerp(LowpassMaxCutoffHz, cutoff, GetAirframeCockpitMuffleMultiplier());
            cutoff = Mathf.Lerp(cutoff, LowpassMaxCutoffHz, GetEjectionMuffleReleaseBlend());
            _cachedCockpitLowpassFrame = Time.frameCount;
            _cachedCockpitLowpassCutoff = cutoff;
            return cutoff;
        }

        // Re-checked periodically rather than on every call -- this can
        // mean walking the local aircraft's actual hardpoint list, and
        // ComputeCockpitLowpassCutoffOnly/ComputeCockpitHighpassCutoffHz
        // run every tick for every managed source. A loadout change is
        // never time-critical enough to need faster than this.
        private const float AirframeMuffleCheckIntervalSeconds = 0.5f;
        private static float _cachedAirframeMuffleMultiplier = 1f;
        private static float _nextAirframeMuffleCheckTime;

        private static float GetAirframeCockpitMuffleMultiplier()
        {
            if (Time.timeSinceLevelLoad < _nextAirframeMuffleCheckTime)
            {
                return _cachedAirframeMuffleMultiplier;
            }
            _nextAirframeMuffleCheckTime = Time.timeSinceLevelLoad + AirframeMuffleCheckIntervalSeconds;
            _cachedAirframeMuffleMultiplier = GameManager.GetLocalAircraft(out Aircraft localAircraft)
                ? AirframeCockpitOpenings.GetMuffleMultiplier(localAircraft)
                : 1f;
            return _cachedAirframeMuffleMultiplier;
        }

        // Snapping straight from "fully muffled" to "fully open" the instant
        // the camera actually leaves cockpit view would be audible as a pop,
        // and that camera cut can lag a beat behind the real eject action
        // anyway (the seat rides the rail for a bit before the view cuts
        // away) -- so ejection instead triggers this short, explicit fade
        // back to unfiltered, layered on top of the normal cockpit-view gate
        // above rather than replacing it.
        //
        // This has to stay latched fully open once the fade finishes,
        // rather than expiring back to "let IsInCockpitView() decide" --
        // IsInCockpitView() can and does keep reporting true for a while
        // after ejecting (the camera doesn't necessarily leave the cockpit
        // state just because the pilot did), which without latching made
        // the fade audibly revert back to muffled right after playing.
        // ResetEjectionMuffleRelease() below is what actually clears it,
        // once the player is confirmed back in a real cockpit.
        private const float EjectionMuffleReleaseSeconds = 0.3f;
        private static bool _ejectionMuffleReleaseActive;
        private static float _ejectionMuffleReleaseStartTime = float.NegativeInfinity;

        internal static void TriggerEjectionMuffleRelease()
        {
            _ejectionMuffleReleaseActive = true;
            _ejectionMuffleReleaseStartTime = Time.time;
        }

        // Called once the player is seated in a fresh cockpit (respawning
        // into player-controlled flight) so ordinary cockpit-view muffling
        // can take over again -- otherwise the override from the last
        // ejection would force every managed sound open forever.
        internal static void ResetEjectionMuffleRelease()
        {
            _ejectionMuffleReleaseActive = false;
        }

        // 0 = no release in progress, normal cockpit-view gating applies
        // untouched. Ramps to 1 (fully forced open) over
        // EjectionMuffleReleaseSeconds and then holds at 1 until
        // ResetEjectionMuffleRelease() is called.
        private static float GetEjectionMuffleReleaseBlend()
        {
            if (!_ejectionMuffleReleaseActive)
            {
                return 0f;
            }
            float elapsed = Time.time - _ejectionMuffleReleaseStartTime;
            return Mathf.Clamp01(elapsed / EjectionMuffleReleaseSeconds);
        }

        private const float HighpassMinCutoffHz = 10f; // Unity's own floor for AudioHighPassFilter, effectively unfiltered

        // Cockpit highpass -- cuts low-end rumble while in cockpit view,
        // paired with the lowpass above to shape a boxy/telephone-like band
        // rather than just a dull, bassy muffle. Unlike the lowpass, there's
        // no distance component to merge with here (atmospheric absorption
        // doesn't remove LOW frequencies with distance the way it does high
        // ones), so this is cockpit-view-only.
        // Same per-frame caching rationale as ComputeCockpitLowpassCutoffOnly
        // above.
        private static int _cachedCockpitHighpassFrame = -1;
        private static float _cachedCockpitHighpassCutoff;

        internal static float ComputeCockpitHighpassCutoffHz()
        {
            if (_cachedCockpitHighpassFrame == Time.frameCount)
            {
                return _cachedCockpitHighpassCutoff;
            }
            float cutoff = CockpitLowpassConfig.HighpassEnabled && IsInCockpitView()
                ? CockpitLowpassConfig.HighpassCutoffHz
                : HighpassMinCutoffHz;
            cutoff = Mathf.Lerp(HighpassMinCutoffHz, cutoff, GetAirframeCockpitMuffleMultiplier());
            cutoff = Mathf.Lerp(cutoff, HighpassMinCutoffHz, GetEjectionMuffleReleaseBlend());
            _cachedCockpitHighpassFrame = Time.frameCount;
            _cachedCockpitHighpassCutoff = cutoff;
            return cutoff;
        }

        // Blends between fully unmuffled (a hit right at the cockpit --
        // effectively next to the pilot's own head) and the normal fixed
        // cockpit cutoff (a hit far enough away, e.g. the tail, that the
        // sound has to travel back through the rest of the airframe to
        // reach the cockpit) -- ONLY for impacts already confirmed to have
        // struck the local player's own aircraft (see
        // TryGetOwnAirframeHitDistance). Reuses ComputeCockpitLowpassCutoffOnly
        // as the "fully muffled" endpoint rather than re-deriving it, so
        // enable/disable, IsInCockpitView, and the ejection-muffle-release
        // fade all keep working identically -- when that endpoint is
        // already LowpassMaxCutoffHz (not in cockpit view, feature
        // disabled, etc.), the lerp is a no-op regardless of distance.
        internal static float ComputeOwnHitImpactLowpassCutoffHz(float distanceToCockpitMeters)
        {
            float fullyMuffledCutoff = ComputeCockpitLowpassCutoffOnly();
            float blend = CockpitLowpassConfig.OwnHitDistanceScalingEnabled
                ? Mathf.Clamp01(distanceToCockpitMeters / Mathf.Max(0.01f, CockpitLowpassConfig.OwnHitFullMuffleDistanceMeters))
                : 1f;
            return Mathf.Lerp(LowpassMaxCutoffHz, fullyMuffledCutoff, blend);
        }

        // Same distance blend as above, applied to the cockpit highpass
        // instead.
        internal static float ComputeOwnHitImpactHighpassCutoffHz(float distanceToCockpitMeters)
        {
            float fullyMuffledCutoff = ComputeCockpitHighpassCutoffHz();
            float blend = CockpitLowpassConfig.OwnHitDistanceScalingEnabled
                ? Mathf.Clamp01(distanceToCockpitMeters / Mathf.Max(0.01f, CockpitLowpassConfig.OwnHitFullMuffleDistanceMeters))
                : 1f;
            return Mathf.Lerp(HighpassMinCutoffHz, fullyMuffledCutoff, blend);
        }

        // ImpactEffectPlayPatch calls this with the exact impact position
        // (which, for ground/armor hits, is BulletSim's own hitInfo.point --
        // precisely on the struck collider's surface) to determine whether
        // this specific impact landed on the LOCAL player's own aircraft,
        // and if so, how far from the cockpit. BulletSim.Bullet.TrajectoryTrace
        // already resolves the struck collider to an IDamageable/Unit
        // itself (via hitInfo.collider.gameObject.GetComponent<IDamageable>())
        // but doesn't expose it to Play() -- only position/rotation cross
        // that boundary -- so this re-derives the same answer independently
        // via a tiny physics query centered exactly on that point, rather
        // than transplanting a local variable out of a vanilla method via a
        // transpiler for what's otherwise a one-line lookup.
        internal static bool TryGetOwnAirframeHitDistance(Vector3 position, out float distanceToCockpitMeters)
        {
            distanceToCockpitMeters = 0f;
            // No early-out on CockpitLowpassConfig.OwnHitDistanceScalingEnabled
            // here (unlike the two Compute*CutoffHz functions that actually
            // use it) -- it's a hardcoded `true` constant now, which made
            // that guard unreachable code. If it's ever flipped to `false`
            // in source, the blend functions already fall back to full flat
            // muffling regardless of the distance this still computes.
            if (!GameManager.GetLocalAircraft(out Aircraft localAircraft) || localAircraft == null || localAircraft.cockpit == null)
            {
                return false;
            }
            // Cheap bounding check before the physics query below -- the
            // overwhelming majority of impacts in the game (enemy-vs-enemy,
            // distant ground fire) are nowhere near the player's own
            // aircraft, so this skips OverlapSphere entirely for those.
            const float MaxPlausibleAirframeSpan = 40f;
            if (Vector3.Distance(position, localAircraft.transform.position) > MaxPlausibleAirframeSpan)
            {
                return false;
            }
            // Small enough to reliably land on just the collider that was
            // actually hit (the impact position sits exactly on its
            // surface), while still forgiving normal floating-point slop
            // right at that boundary.
            Collider[] hits = Physics.OverlapSphere(position, 0.25f);
            for (int i = 0; i < hits.Length; i++)
            {
                IDamageable damageable = hits[i].gameObject.GetComponent<IDamageable>();
                if (damageable == null)
                {
                    continue;
                }
                if ((Unit)localAircraft == damageable.GetUnit())
                {
                    distanceToCockpitMeters = Vector3.Distance(position, localAircraft.cockpit.xform.position);
                    return true;
                }
            }
            return false;
        }

        // Cockpit muffling for engine sound specifically is deliberately
        // NOT gated behind EngineAudioConfig.DelayEnabled -- that toggle
        // guards the expensive part (continuous history/smoothing/clone
        // tracking for the distance simulation), but cockpit-view muffling
        // is cheap and shouldn't require paying for that just to get it.
        // Every engine-type patch's own "system off" branch calls this
        // directly on the REAL, live AudioSource instead of a tracked
        // clone -- a ConditionalWeakTable caches the lazily-added filter
        // pair per source so repeated per-frame calls are just a lookup
        // plus a few scalar writes, not a fresh GetComponent/AddComponent
        // check every time. Despite the name (kept to avoid touching all
        // eight call sites), this now manages both the lowpass and the
        // highpass.
        private class CockpitOnlyFilters
        {
            public AudioLowPassFilter Lowpass;
            public AudioHighPassFilter Highpass;

            // Once true, Lowpass/Highpass are guaranteed non-null and the
            // safety check below is never repeated for this source again --
            // a GameObject's set of AudioSources doesn't change once every
            // sound-emitting component on it has actually initialized.
            public bool FiltersCreated;

            // Logged once per source the first time it's found unsafe, not
            // once ever globally and not every frame -- see the check
            // below for why this can't just be cached permanently like
            // FiltersCreated.
            public bool UnsafeLogged;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AudioSource, CockpitOnlyFilters>
            _cockpitOnlyFilters = new System.Runtime.CompilerServices.ConditionalWeakTable<AudioSource, CockpitOnlyFilters>();

        // acceptedSiblings lists other AudioSource(s) this mod ALSO manages
        // (via their own separate ApplyCockpitOnlyLowpass call) that are
        // known to legitimately live on the same GameObject as realSource --
        // e.g. LandingGear's tireNoiseSound/tireSkidSound, a ground
        // vehicle's engineIdleSound/engineDriveSound, or a gun's own
        // sources[] array sharing a GameObject with its recoilSound (those
        // never actually play on the real source at all once ShotSound()
        // is fully replaced, so they're harmless to co-filter). Without
        // this, two of this mod's own sources sharing one GameObject looked
        // identical to the real problem this check exists for (one of our
        // sources sharing a GameObject with something genuinely unrelated,
        // like ThreatList's missile alarm) and got the same "skip filtering
        // entirely" treatment -- silently leaving tire/engine sound
        // unfiltered on whichever aircraft's prefab happens to put both
        // sources on one object instead of two.
        //
        // The safety check itself is re-evaluated every call rather than
        // locked in once and cached (only whether the FILTER COMPONENTS
        // have been created is cached) -- a source whose sibling hasn't
        // been created/registered YET (e.g. JetNozzle's thrustAudio ticking
        // before any Afterburner has ever run once) would otherwise be
        // judged unsafe on its very first call and stay stuck that way
        // forever even after the sibling shows up. Re-checking is just a
        // GetComponents<AudioSource> + a linear scan of a tiny array, cheap
        // enough for a per-frame call; a source that's genuinely sharing
        // with unrelated, never-registered vanilla audio (e.g. AoA
        // feedback's source on the aircraft's cockpit GameObject) simply
        // never converges and re-checks every call for its whole lifetime,
        // which is the correct (if slightly wasteful) outcome for that case.
        internal static void ApplyCockpitOnlyLowpass(AudioSource realSource, AudioSource[] acceptedSiblings = null, float muffleMultiplier = 1f)
        {
            if (realSource == null)
            {
                return;
            }
            if (!_cockpitOnlyFilters.TryGetValue(realSource, out CockpitOnlyFilters filters))
            {
                filters = new CockpitOnlyFilters();
                _cockpitOnlyFilters.Add(realSource, filters);
            }
            if (!filters.FiltersCreated)
            {
                // Unity applies AudioLowPassFilter/AudioHighPassFilter to
                // the COMBINED output of every AudioSource on the same
                // GameObject, not just the one this call intends to shape --
                // if this real source's GameObject happens to also host some
                // completely unrelated AudioSource (a UI/alert sound this
                // mod was never meant to touch -- e.g. ThreatList's missile-
                // incoming alarm loop, added directly onto the aircraft's
                // own root GameObject), adding a filter here would silently
                // muffle that one too. Reported symptom this was written
                // for: "missile warning beep sometimes muted." Rather than
                // rebuild this widely-used function around an isolated
                // clone just to be sure, this fails toward safety instead:
                // if any unaccepted sharing is detected, skip touching
                // bypassEffects/filters on this source for now.
                bool safe = true;
                AudioSource[] coHostedSources = realSource.gameObject.GetComponents<AudioSource>();
                for (int i = 0; i < coHostedSources.Length; i++)
                {
                    AudioSource coHosted = coHostedSources[i];
                    if (coHosted == realSource || (acceptedSiblings != null && System.Array.IndexOf(acceptedSiblings, coHosted) >= 0))
                    {
                        continue;
                    }
                    safe = false;
                    break;
                }
                if (!safe)
                {
                    if (!filters.UnsafeLogged)
                    {
                        filters.UnsafeLogged = true;
                        Log.LogInfo(
                            $"[CockpitFilterShareDiag] Skipping cockpit-only filter for '{realSource.gameObject.name}' -- "
                            + "its GameObject hosts more than one AudioSource, and Unity's filter components can't be "
                            + "scoped to just one of them.");
                    }
                    return;
                }
                filters.Lowpass = realSource.GetComponent<AudioLowPassFilter>() ?? realSource.gameObject.AddComponent<AudioLowPassFilter>();
                filters.Highpass = realSource.GetComponent<AudioHighPassFilter>() ?? realSource.gameObject.AddComponent<AudioHighPassFilter>();
                filters.FiltersCreated = true;
            }
            // Same reasoning as CopyAudioSourceSettings' bypassEffects override
            // -- this runs directly on the real, live engine source rather
            // than a copy, so if that particular engine's own AudioSource
            // ships with bypassEffects=true, the filters just below would be
            // silently ignored by Unity regardless of their cutoff.
            realSource.bypassEffects = false;
            float lowpassCutoff = ApplyMuffleMultiplier(LowpassMaxCutoffHz, ComputeCockpitLowpassCutoffOnly(), muffleMultiplier);
            filters.Lowpass.lowpassResonanceQ = LowpassResonanceFor(lowpassCutoff);
            filters.Lowpass.cutoffFrequency = lowpassCutoff;
            float highpassCutoff = ApplyMuffleMultiplier(HighpassMinCutoffHz, ComputeCockpitHighpassCutoffHz(), muffleMultiplier);
            filters.Highpass.highpassResonanceQ = HighpassResonanceFor(highpassCutoff);
            filters.Highpass.cutoffFrequency = highpassCutoff;
        }

        // Engines' TickEngines() clamps how fast its history-playback
        // pointer can advance to real-time (see LastTargetTime below) so a
        // supersonic approach can't make the sound arrive early -- but that
        // clamp assumes the LISTENER only ever moves continuously. A camera
        // cut (toggling cockpit/external view, a freecam tool snapping back
        // to the cockpit) instead teleports it, which can change the live
        // delay by seconds in a single frame; without detecting that, the
        // clamp mistakes the resulting jump for an impossible supersonic
        // overtake and throttles it to real-time-per-frame too, so engine
        // volume/pitch takes several real seconds to "catch up" to the
        // correct distance after a cut. A jump this large in one frame is
        // otherwise impossible for a camera smoothly following any unit
        // (even a very fast jet), so it's an unambiguous signal a cut just
        // happened.
        private const float CameraTeleportThresholdMeters = 300f;
        private static Vector3 _lastCameraPosition;
        private static bool _hasLastCameraPosition;
        private static bool _cameraTeleportedThisTick;

        // A cockpit camera commonly rotates around a pivot offset from the
        // AudioListener's own position (looking around the cockpit), which
        // measurably moves the listener frame to frame even with the
        // aircraft completely stationary -- reported as a high-pitched
        // engine warble, present only while the camera is rotating, that
        // held a steady (still audible) pitch once it stopped. Unity's
        // automatic Doppler reads that rotation-induced motion as real
        // relative velocity and applies a pitch shift for it, same as it
        // would for the aircraft actually moving. _listenerVelocity is
        // computed alongside teleport detection since both need the same
        // frame-to-frame camera position tracking.
        private static Vector3 _listenerVelocity;

        private static void DetectCameraTeleport()
        {
            CameraStateManager cameraStateManager = SceneSingleton<CameraStateManager>.i;
            if (cameraStateManager == null)
            {
                _cameraTeleportedThisTick = false;
                _listenerVelocity = Vector3.zero;
                return;
            }
            // Origin-relative, not raw transform.position -- the game
            // re-centers the whole scene (FloatingOrigin.OriginShift)
            // routinely at combat speed (same reason RecordEngineSample
            // stores history this way), and a raw world position jumps by
            // the shift amount even though the camera never actually moved.
            // Confirmed via logging: every "teleport" detected during normal
            // flight was a ~1000-1200m jump along one axis, coinciding with
            // dozens of unrelated engines across the whole scene resetting
            // simultaneously -- an origin shift, not a real camera cut. That
            // was falsely defeating the supersonic-overtake clamp on
            // ordinary fast flight, not just actual camera cuts.
            Vector3 position = Datum.origin.InverseTransformPoint(cameraStateManager.transform.position);
            _cameraTeleportedThisTick = _hasLastCameraPosition
                && Vector3.Distance(position, _lastCameraPosition) > CameraTeleportThresholdMeters;
            if (_cameraTeleportedThisTick && VerboseLoggingConfig.Enabled.Value)
            {
                Log.LogInfo(
                    $"[EngineAudioDiag] Camera teleport detected at t={Time.timeSinceLevelLoad:F2} "
                    + $"(moved {Vector3.Distance(position, _lastCameraPosition):F1}m in one frame, origin-relative: "
                    + $"{_lastCameraPosition} -> {position})");
            }
            // A teleport (or the very first sample) is a huge, instantaneous
            // jump that must never be read as real velocity -- zeroed here
            // rather than left as whatever the raw delta/deltaTime works
            // out to.
            _listenerVelocity = _hasLastCameraPosition && !_cameraTeleportedThisTick && Time.deltaTime > 0f
                ? (position - _lastCameraPosition) / Time.deltaTime
                : Vector3.zero;
            _lastCameraPosition = position;
            _hasLastCameraPosition = true;
        }

        // Below this relative speed, Unity's automatic Doppler is disabled
        // outright for that source this frame (dopplerLevel forced to 0)
        // rather than scaled down -- confirmed reported cause: rotating a
        // cockpit camera measurably moves the listener even with the
        // aircraft (and its engine) completely stationary, which Unity
        // reads as real velocity and pitch-shifts for, same as it would
        // for actual relative motion. Real relative motion this slow
        // wouldn't produce a perceptible Doppler shift anyway, so there's
        // nothing to lose by gating it out entirely.
        private const float DopplerVelocityThresholdMetersPerSecond = 10f;

        private static float GateDopplerLevel(float baseDopplerLevel, Vector3 sourceVelocity)
        {
            Vector3 relativeVelocity = sourceVelocity - _listenerVelocity;
            return relativeVelocity.magnitude < DopplerVelocityThresholdMetersPerSecond ? 0f : baseDopplerLevel;
        }

        // Past this range, a sound isn't worth tracking at all -- it'd take
        // over a minute to arrive anyway, long past anything gameplay-
        // relevant, and continuously simulating it (engines especially,
        // which run for a whole aircraft's flight and keep a rolling
        // position/pitch/volume history every frame) is wasted work for
        // something nobody's ever going to hear. New sounds beyond this
        // just aren't scheduled/tracked in the first place; engines
        // (the only long-lived, continuously-updated case) additionally get
        // dropped if they drift past it mid-flight, freeing their clone and
        // history -- they'll just pick back up fresh if the listener ever
        // gets close enough again.
        private const float MaxTrackingDistanceMeters = 20000f;

        private static bool IsBeyondTrackingRange(Vector3 position)
        {
            CameraStateManager cameraStateManager = SceneSingleton<CameraStateManager>.i;
            if (cameraStateManager == null)
            {
                return false;
            }
            return Vector3.Distance(position, cameraStateManager.transform.position) > MaxTrackingDistanceMeters;
        }

        // extraDelaySeconds adds on top of the normal propagation delay --
        // for a second, closely-following one-shot (e.g. a sonic boom's
        // trailing shock) rather than a separate distance to compute from.
        // clipDurationSeconds, if above zero, cuts this specific shot's
        // playback short that many seconds after it starts, instead of
        // letting the clip ring out in full -- see PendingShot.
        // highpassCutoffHz, if above zero, adds a static AudioHighPassFilter
        // ahead of the distance lowpass in the processing chain -- Unity
        // processes multiple filter components on the same GameObject in
        // the order they're attached (confirmed via Unity's own manual, not
        // a fixed built-in order), so adding this one first here means it
        // always runs before the lowpass below regardless of that filter's
        // own live cutoff. Useful for a source clip that carries more
        // low-end than the desired sound should have (see the bullet crack
        // patch, which strips a boom clip's low "thud" before pitching it
        // up into a thin snap) -- unlike the lowpass, this one is static,
        // since it's shaping the source's own timbre rather than modeling
        // distance.
        public static void ScheduleDelayedOneShot(
            AudioSource template, AudioClip clip, Vector3 worldPosition,
            float extraDelaySeconds = 0f, float clipDurationSeconds = 0f, float highpassCutoffHz = 0f,
            float? ownHitDistanceToCockpitMeters = null, float cockpitMuffleMultiplier = 1f)
        {
            if (clip == null || IsBeyondTrackingRange(worldPosition))
            {
                return;
            }

            EnsureDriver();

            GameObject tempObject = new GameObject("DelayedGunfire");
            // Datum.origin is the floating-origin anchor everything else in
            // the scene (e.g. SonicBoomManager, ExplosionAudioManager) also
            // parents world-space effects under -- without it, a mid-flight
            // origin rebase would leave this object's position stale.
            tempObject.transform.SetParent(Datum.origin, worldPositionStays: false);
            tempObject.transform.position = worldPosition;

            AudioSource source = tempObject.AddComponent<AudioSource>();
            CopyAudioSourceSettings(template, source);
            // Stationary emitter (fixed at the point it fired from, never
            // tracking anything afterward) -- no doppler shift to apply,
            // matching vanilla's own ExplosionAudio for its one-shot booms.
            source.dopplerLevel = 0f;

            // Always created (not just when highpassCutoffHz > 0), so the
            // live cockpit highpass can still apply even to one-shots that
            // don't request their own static cut -- attached before the
            // lowpass below so it always runs first in the chain (Unity
            // processes filter components in attachment order).
            AudioHighPassFilter highpassFilter = tempObject.AddComponent<AudioHighPassFilter>();
            highpassFilter.cutoffFrequency = highpassCutoffHz; // corrected to the real (merged) value right before playback

            AudioLowPassFilter lowpassFilter = tempObject.AddComponent<AudioLowPassFilter>();
            lowpassFilter.lowpassResonanceQ = 1f; // neutral placeholder -- corrected (see LowpassResonanceFor) right before playback, same as the cutoff below
            lowpassFilter.cutoffFrequency = LowpassMaxCutoffHz; // corrected to the real value right before playback

            _pending.Add(new PendingShot
            {
                Source = source,
                Clip = clip,
                TempObject = tempObject,
                SpawnTime = Time.timeSinceLevelLoad,
                ExtraDelaySeconds = extraDelaySeconds,
                ClipDurationSeconds = clipDurationSeconds,
                LowpassFilter = lowpassFilter,
                RequestedHighpassCutoffHz = highpassCutoffHz,
                HighpassFilter = highpassFilter,
                OwnHitDistanceToCockpitMeters = ownHitDistanceToCockpitMeters,
                CockpitMuffleMultiplier = cockpitMuffleMultiplier
            });
        }

        // Called every shot while a continuous weapon is conceptually
        // firing (mirrors the original "!sources[1].isPlaying" gate, but
        // that check now lives inside here instead of at the call site,
        // since resuming from a pending stop needs the same entry point).
        //
        // Returns whether this call is the one that actually created a
        // brand-new tracking entry -- the caller uses that (not its own
        // separate check beforehand) to decide whether to play the
        // spool-up/fireStart bang, since the dictionary insert below is the
        // only thing that happens synchronously and atomically with the
        // check. Gating fireStart on a *separate* pre-check (the vanilla
        // gun's own lastFired/fireInterval fields, or a prior IsLoopActive
        // call) left a race: after a loop's cleanup grace period expires, a
        // quick trigger re-press can call this method (indirectly, via
        // GunShotSoundPatch) two or three times back-to-back while the
        // gun's own lastFired field still reflects the *previous* burst,
        // and each of those calls would independently see "no loop yet" and
        // schedule its own fireStart -- audible as the startup bang
        // repeating.
        public static bool NotifyLoopActive(
            Gun gun,
            AudioSource template,
            AudioClip loopClip,
            AudioClip fireEndClip,
            Vector3 origin,
            bool modifyPitch,
            float targetPitch,
            float startPitch,
            float pitchClimbRate)
        {
            if (_loops.TryGetValue(gun, out LoopState existing))
            {
                // Self-measured cadence, not the gun's own fireInterval
                // field -- TickLoops()'s stop-detection uses this instead
                // of that field because some weapons' real firing rate (a
                // rotary cannon still spinning up, say) runs well below
                // what fireInterval claims, which made that check see a
                // "gap" on nearly every shot and re-trigger the stop sound
                // mid-burst. Blended in (not overwritten outright) and
                // capped to at most double the running estimate per sample:
                // a single abnormally long gap between two real shots (a
                // frame hitch from a camera cut, say) would otherwise fully
                // overwrite ObservedInterval with that one outlier, and
                // since it's not corrected again until the *next* real
                // shot, a hitch landing on the last shot before the trigger
                // is released would leave the stop-detection threshold
                // inflated for the whole rest of that burst's tail --
                // audible as the loop staying "stuck on" a beat too long.
                float now = Time.timeSinceLevelLoad;
                float observedInterval = now - existing.LastShotTime;
                if (observedInterval > 0f)
                {
                    float cappedSample = Mathf.Min(observedInterval, existing.ObservedInterval * 2f);
                    existing.ObservedInterval = Mathf.Lerp(existing.ObservedInterval, cappedSample, 0.3f);
                }
                existing.LastShotTime = now;

                // Already active, or winding down but firing resumed before
                // its delayed tail finished (or even after it, within the
                // cleanup grace window) -- either way, keep using the same
                // clone rather than starting a second overlapping one.
                if (existing.AudioStopped)
                {
                    if (VerboseLoggingConfig.Enabled.Value)
                    {
                        Log.LogInfo($"[GunLoopDiag] Resuming already-stopped-audio loop for '{gun.GetInstanceID()}' (was PendingStop={existing.PendingStop})");
                    }
                    existing.AudioStopped = false;
                    existing.CloneSource.Play();
                    existing.CloneSource.time = UnityEngine.Random.Range(0f, existing.CloneSource.clip.length);
                }
                // A real shot means "actively playing now" regardless of
                // whether the stop-fade (see GunLoopFadeOutSeconds) had
                // already finished or was only partway through -- either
                // way, TickLoops() was ramping volume toward 0 and nothing
                // else ever restored it, so a burst resuming mid-fade (not
                // just after it completed) was left silently stuck at
                // whatever partial volume the fade had reached. Reset here
                // unconditionally rather than only inside the AudioStopped
                // branch above.
                existing.FadingOut = false;
                existing.CloneSource.volume = existing.NormalVolume;
                existing.PendingStop = false;
                return false;
            }

            if (IsBeyondTrackingRange(origin))
            {
                return false;
            }

            if (VerboseLoggingConfig.Enabled.Value)
            {
                Log.LogInfo($"[GunLoopDiag] NEW loop registered for '{gun.GetInstanceID()}' at t={Time.timeSinceLevelLoad:F2}");
            }
            EnsureDriver();

            GameObject cloneObject = new GameObject("DelayedGunLoop");
            cloneObject.transform.SetParent(Datum.origin, worldPositionStays: false);
            cloneObject.transform.position = origin;

            AudioSource cloneSource = cloneObject.AddComponent<AudioSource>();
            CopyAudioSourceSettings(template, cloneSource);
            cloneSource.clip = loopClip;
            cloneSource.loop = true;
            cloneSource.pitch = modifyPitch ? startPitch : targetPitch;
            float normalVolume = cloneSource.volume;

            AudioLowPassFilter lowpassFilter = cloneObject.AddComponent<AudioLowPassFilter>();
            lowpassFilter.lowpassResonanceQ = 1f; // neutral placeholder -- corrected (see LowpassResonanceFor) live every tick in TickLoops
            lowpassFilter.cutoffFrequency = LowpassMaxCutoffHz; // corrected live every tick in TickLoops

            AudioHighPassFilter highpassFilter = cloneObject.AddComponent<AudioHighPassFilter>();
            highpassFilter.highpassResonanceQ = 1f; // neutral placeholder -- corrected (see HighpassResonanceFor) live every tick in TickLoops
            highpassFilter.cutoffFrequency = HighpassMinCutoffHz; // corrected live every tick in TickLoops

            // Seeded from the gun's own fireInterval until a second real
            // shot gives an actual observed interval to replace it with --
            // just needs to be a sane starting guess for the very first
            // stop-detection check.
            float fireIntervalSeed = FireIntervalRef(gun);
            _loops[gun] = new LoopState
            {
                CloneSource = cloneSource,
                CloneObject = cloneObject,
                Template = template,
                FireEndClip = fireEndClip,
                DelaySeconds = ComputeDelaySeconds(origin),
                StartTime = Time.timeSinceLevelLoad,
                ModifyPitch = modifyPitch,
                TargetPitch = targetPitch,
                StartPitch = startPitch,
                PitchClimbRate = pitchClimbRate,
                LastShotTime = Time.timeSinceLevelLoad,
                ObservedInterval = fireIntervalSeed > 0f ? fireIntervalSeed : 0.05f,
                LowpassFilter = lowpassFilter,
                HighpassFilter = highpassFilter,
                NormalVolume = normalVolume
            };
            return true;
        }

        private static void TickLoops()
        {
            foreach (KeyValuePair<Gun, LoopState> kvp in _loops)
            {
                Gun gun = kvp.Key;
                LoopState state = kvp.Value;

                // A single bad entry (destroyed gun/clone slipping past the
                // checks below, a reused Gun instance from a pooled/reset
                // aircraft, a scene transition tearing down Datum.origin's
                // children out from under us) must never be able to throw
                // and silently halt processing for every OTHER gun's loop
                // for the rest of the session -- that's the leading
                // suspect for loops getting stuck on indefinitely.
                try
                {
                    if (gun == null || state.CloneObject == null || state.CloneSource == null)
                    {
                        StopAndDestroyLoop(state);
                        _loopsToRemove.Add(gun);
                        continue;
                    }

                    // Gun.ShotSound()'s own Prefix mirrors vanilla's
                    // displayDetail gate (attachedUnit.displayDetail < 1f)
                    // and returns without ever reaching our code once the
                    // owning aircraft falls out of camera-following/detail
                    // range -- e.g. the camera switches to following a
                    // different unit, which immediately reverts this one
                    // from a fixed high detail value to a distance-based
                    // one (see CameraStateManager). If firing is still
                    // conceptually ongoing in the sim at that point, no
                    // further real shots ever reach NotifyLoopActive to
                    // keep this entry's timing fresh, so it would otherwise
                    // sit in limbo -- audible as the loop staying "stuck
                    // on" past when it should stop, and, if detail comes
                    // back mid-burst, ambiguous about whether to play a
                    // fresh fireStart. Dropping it immediately here instead
                    // means it always starts clean next time a shot from
                    // this gun actually reaches us.
                    if (gun.attachedUnit.displayDetail < 1f)
                    {
                        StopAndDestroyLoop(state);
                        _loopsToRemove.Add(gun);
                        continue;
                    }

                    // Track the gun's live position (not the fixed origin it
                    // started at) so the clone's panning follows the aircraft
                    // as it keeps flying during a multi-second burst -- only
                    // the start/stop TIMING uses the fixed delay, not where
                    // the sound appears to come from while it plays.
                    Vector3 gunPosition = gun.transform.position;
                    state.CloneObject.transform.position = gunPosition;
                    float gunLowpassCutoff = ComputeLowpassCutoffHz(GetDistanceToListener(gunPosition));
                    state.LowpassFilter.lowpassResonanceQ = LowpassResonanceFor(gunLowpassCutoff);
                    state.LowpassFilter.cutoffFrequency = gunLowpassCutoff;
                    float gunHighpassCutoff = ComputeCockpitHighpassCutoffHz();
                    state.HighpassFilter.highpassResonanceQ = HighpassResonanceFor(gunHighpassCutoff);
                    state.HighpassFilter.cutoffFrequency = gunHighpassCutoff;

                    if (IsBeyondTrackingRange(gunPosition))
                    {
                        StopAndDestroyLoop(state);
                        _loopsToRemove.Add(gun);
                        continue;
                    }

                    if (!state.Started && Time.timeSinceLevelLoad - state.StartTime >= state.DelaySeconds)
                    {
                        state.Started = true;
                        state.CloneSource.Play();
                        state.CloneSource.time = UnityEngine.Random.Range(0f, state.CloneSource.clip.length);
                    }

                    if (state.Started && state.ModifyPitch && !state.PendingStop)
                    {
                        state.CloneSource.pitch = Mathf.Min(
                            state.CloneSource.pitch + Time.deltaTime * state.PitchClimbRate,
                            state.TargetPitch);
                    }

                    if (!state.PendingStop)
                    {
                        // Compared against the weapon's own observed shot
                        // cadence (updated in NotifyLoopActive on every real
                        // shot), not its static fireInterval field -- that
                        // field can claim a much faster rate than a weapon
                        // is actually firing at (e.g. a rotary cannon still
                        // spinning up), which made this trip on nearly every
                        // shot and re-schedule the stop sound mid-burst. The
                        // 1.5x margin absorbs normal jitter around whatever
                        // that real cadence is without needing to know it in
                        // advance.
                        if (Time.timeSinceLevelLoad - state.LastShotTime > Time.deltaTime + state.ObservedInterval * 1.5f)
                        {
                            if (VerboseLoggingConfig.Enabled.Value)
                            {
                                Log.LogInfo(
                                    $"[GunLoopDiag] PendingStop for '{gun.GetInstanceID()}' at t={Time.timeSinceLevelLoad:F2} "
                                    + $"(gap={Time.timeSinceLevelLoad - state.LastShotTime:F3} observedInterval={state.ObservedInterval:F3})");
                            }
                            state.PendingStop = true;
                            Vector3 stopOrigin = gun.transform.position;
                            // Recomputed fresh (not reusing the start-time
                            // delay) in case the listener's distance to the
                            // gun changed meaningfully over the burst -- this
                            // is also what correctly compresses/stretches the
                            // apparent burst duration for an approaching or
                            // receding listener, the same physical effect as
                            // Doppler pitch shift applied to duration instead.
                            float stopDelay = ComputeDelaySeconds(stopOrigin);
                            // At high enough closing speed (supersonic
                            // closure is achievable in this game) the
                            // compressed stop can land before the (also
                            // delayed) start has even arrived, which would
                            // be heard as an impossible "end before
                            // beginning" -- clamp so the burst can compress
                            // all the way to zero length but never go negative.
                            state.ScheduledStopTime = Mathf.Max(
                                Time.timeSinceLevelLoad + stopDelay,
                                state.StartTime + state.DelaySeconds);
                            state.CleanupTime = state.ScheduledStopTime + LoopCleanupGraceSeconds;
                            ScheduleDelayedOneShot(state.Template, state.FireEndClip, stopOrigin);
                        }
                    }
                    else
                    {
                        // Audio goes silent right on schedule (matching the
                        // delayed "fireEnd" bang's own arrival), but the
                        // tracking entry itself lingers a bit longer so a
                        // shot arriving shortly after can still resume it.
                        // A short fade-out (see GunLoopFadeOutSeconds) runs
                        // first so the actual Stop() call below always
                        // lands on near-silence instead of cutting the
                        // waveform mid-cycle.
                        if (!state.AudioStopped && Time.timeSinceLevelLoad >= state.ScheduledStopTime)
                        {
                            if (!state.FadingOut)
                            {
                                state.FadingOut = true;
                                state.FadeOutStartVolume = state.CloneSource.volume;
                                state.FadeOutStartTime = Time.timeSinceLevelLoad;
                            }

                            float fadeT = Mathf.Clamp01((Time.timeSinceLevelLoad - state.FadeOutStartTime) / GunLoopFadeOutSeconds);
                            state.CloneSource.volume = Mathf.Lerp(state.FadeOutStartVolume, 0f, fadeT);

                            if (fadeT >= 1f)
                            {
                                if (VerboseLoggingConfig.Enabled.Value)
                                {
                                    Log.LogInfo($"[GunLoopDiag] Audio stopped for '{gun.GetInstanceID()}' at t={Time.timeSinceLevelLoad:F2}");
                                }
                                state.CloneSource.Stop();
                                state.AudioStopped = true;
                            }
                        }
                        if (Time.timeSinceLevelLoad >= state.CleanupTime)
                        {
                            if (VerboseLoggingConfig.Enabled.Value)
                            {
                                Log.LogInfo($"[GunLoopDiag] Cleanup/removed loop for '{gun.GetInstanceID()}' at t={Time.timeSinceLevelLoad:F2}");
                            }
                            StopAndDestroyLoop(state);
                            _loopsToRemove.Add(gun);
                        }
                    }
                }
                catch (Exception)
                {
                    StopAndDestroyLoop(state);
                    _loopsToRemove.Add(gun);
                }
            }

            if (_loopsToRemove.Count > 0)
            {
                foreach (Gun gun in _loopsToRemove)
                {
                    _loops.Remove(gun);
                }
                _loopsToRemove.Clear();
            }
        }

        private static void StopAndDestroyLoop(LoopState state)
        {
            if (state.CloneSource != null)
            {
                state.CloneSource.Stop();
            }
            if (state.CloneObject != null)
            {
                UnityEngine.Object.Destroy(state.CloneObject);
            }
        }

        // Engine hum -- covers every engine type's own Animate()-equivalent
        // (TurbineEngine, Turbojet, Turbofan, DuctedFan, ConstantSpeedProp;
        // see Patches/*EnginePatch.cs), keyed generically by Component since
        // none of those five classes share a common base beyond MonoBehaviour.
        // Unlike gunfire/loops, this sound is ALWAYS on and its pitch/volume
        // change continuously and smoothly for the aircraft's entire
        // lifetime rather than switching between a couple of discrete
        // states -- a single fixed delay computed once wouldn't capture
        // that, and delay itself needs to keep being re-evaluated live as
        // distance to the listener changes over the course of a whole
        // flight, not just once at "start". Instead, every sample the real
        // engine would have applied to its own (permanently silenced --
        // see the individual engine patches) audio source gets recorded
        // into a short rolling history, and the clone continuously plays
        // back whichever historical sample lines up with "now minus
        // however long the sound currently takes to arrive" -- the audio
        // equivalent of the gun loop's live position tracking, just
        // extended to pitch/volume/position all together.
        private readonly struct EngineSample
        {
            public readonly float Time;
            public readonly float Pitch;
            public readonly float Volume;
            public readonly Vector3 Position;
            public readonly float DopplerLevel;

            public EngineSample(float time, float pitch, float volume, Vector3 position, float dopplerLevel)
            {
                Time = time;
                Pitch = pitch;
                Volume = volume;
                Position = position;
                DopplerLevel = dopplerLevel;
            }
        }

        // A plain List<EngineSample> used as a sliding window (Add at the
        // back, RemoveRange(0, n) at the front every frame once the window
        // is full) makes every single trim an O(window size) array shift --
        // at 30s of history and a typical frame rate that's potentially
        // ~1000+ elements copied down by one slot, every frame, for every
        // tracked engine simultaneously, once steady-state. A ring buffer
        // makes both Add and trimming the front O(1) (just moves an index),
        // which is the actual access pattern this needs -- confirmed real
        // measurable cost, not a hypothetical one, since this runs
        // unconditionally every frame per engine for as long as it's alive.
        private class EngineSampleHistory
        {
            private EngineSample[] _items = new EngineSample[64];
            private int _head;
            private int _count;

            public int Count => _count;

            public EngineSample this[int index] => _items[(_head + index) % _items.Length];

            public void Add(EngineSample item)
            {
                if (_count == _items.Length)
                {
                    Grow();
                }
                _items[(_head + _count) % _items.Length] = item;
                _count++;
            }

            // Drops the oldest `n` entries -- just advances where "index 0"
            // points, no data movement.
            public void RemoveFront(int n)
            {
                _head = (_head + n) % _items.Length;
                _count -= n;
            }

            private void Grow()
            {
                var newItems = new EngineSample[_items.Length * 2];
                for (int i = 0; i < _count; i++)
                {
                    newItems[i] = this[i];
                }
                _items = newItems;
                _head = 0;
            }
        }

        private class EngineState
        {
            public AudioSource CloneSource;
            public GameObject CloneObject;
            public readonly EngineSampleHistory History = new EngineSampleHistory();
            public float NextDiagLogTime;
            public float NextSettingsSyncTime;

            // Unity's automatic Doppler infers velocity from how fast the
            // AudioSource's own position changes frame to frame. The
            // sampled history position isn't a smooth glide -- delay is
            // recomputed fresh every frame from live distance, so as the
            // listener opens or closes distance, the point in history being
            // read jumps forward faster or slower than real time, which
            // Unity's Doppler reads as a noisy, warbling velocity. Smoothing
            // the clone's actual position toward that sampled target (a
            // little extra lag on top of an already-delayed position, so
            // imperceptible on its own) gives Unity a clean signal instead.
            public Vector3 SmoothedPosition;
            public Vector3 SmoothVelocity;
            public bool HasSmoothedPosition;

            // The point in History currently being played back can never
            // legitimately advance faster than real time -- delay/340 only
            // holds up for a source closing slower than the speed of sound.
            // Once closing speed exceeds it, the naive "now - delay" target
            // would run ahead of "now" itself (heard before the source
            // physically arrives, since sound can never outrun a supersonic
            // source), and SampleHistoryAt would just clamp to the latest
            // live sample -- audibly, that reads as the engine being
            // instant/undelayed the moment closing speed goes supersonic.
            // Capping the advance rate at 1:1 instead queues up the
            // backlog and plays it back at normal speed once it can.
            public float LastTargetTime;
            public bool HasLastTargetTime;
            public AudioLowPassFilter LowpassFilter;
            public AudioHighPassFilter HighpassFilter;

            // Optional (null for the couple of call sites that can't easily
            // get at it -- PropFan's transpiler-based patch and JetNozzle's
            // Afterburner, a plain [Serializable] value class with no
            // back-reference to its owning JetNozzle/Aircraft at all). Used
            // purely by TickEngines() below to detect the SAME
            // camera-detail drop the recording side already gates on, so a
            // null Owner just means this particular entry doesn't get that
            // extra cleanup -- never a correctness issue, only a missed
            // optimization for those two engine types.
            //
            // Typed as Unit, not Aircraft -- GroundVehicle (tanks, trucks,
            // etc.) is a sibling Unit subclass, not an Aircraft, but shares
            // the same displayDetail property (declared on Unit itself) and
            // deserves the same cleanup.
            public Unit Owner;
        }

        // Long enough to cover realistic hearing range (340 m/s * 30s = a
        // little over 10km) without letting the buffer grow unbounded for
        // an engine that runs the whole mission.
        private const float EngineHistorySeconds = 30f;

        // See the comment at its use site in RecordEngineSample -- how often
        // AudioSource settings get re-synced from the real engine outside of
        // an actual clip change, as a low-cost safety net rather than doing
        // it every single frame.
        private const float SettingsSyncIntervalSeconds = 0.5f;

        private static readonly Dictionary<Component, EngineState> _engines = new Dictionary<Component, EngineState>();
        private static readonly List<Component> _enginesToRemove = new List<Component>();

        // For engine types (Turbojet/Turbofan) whose real Animate() only
        // ever gates a one-time Play() trigger and never stops again
        // afterward, regardless of later RPM -- this lets their patches
        // replicate that same "started once, keeps going forever" quirk
        // faithfully rather than accidentally fixing it.
        public static bool IsEngineActive(Component engine)
        {
            return _engines.ContainsKey(engine);
        }

        public static void RecordEngineSample(
            Component engine, AudioSource template, float pitch, float volume, Vector3 position, float dopplerLevel,
            Unit owner = null)
        {
            if (!_engines.TryGetValue(engine, out EngineState state))
            {
                if (IsBeyondTrackingRange(position))
                {
                    return;
                }

                EnsureDriver();

                GameObject cloneObject = new GameObject("DelayedEngine");
                cloneObject.transform.SetParent(Datum.origin, worldPositionStays: false);
                cloneObject.transform.position = position;

                AudioSource cloneSource = cloneObject.AddComponent<AudioSource>();
                if (VerboseLoggingConfig.Enabled.Value)
                {
                    Log.LogInfo(
                        $"[EngineAudioDiag] === New engine clone for '{engine.GetType().Name}' on '{engine.transform.root.name}' "
                        + $"at t={Time.timeSinceLevelLoad:F2} -- firstSample pitch={pitch:F3} volume={volume:F3} ===");
                }
                // logDiagnostics also reads 4 custom AnimationCurves off each
                // of the from/to sources through Unity's native interop --
                // real cost, not just the log line itself, so this is tied
                // to the same toggle rather than always running.
                CopyAudioSourceSettings(template, cloneSource, logDiagnostics: VerboseLoggingConfig.Enabled.Value);
                cloneSource.clip = template.clip;
                cloneSource.loop = true;
                cloneSource.time = UnityEngine.Random.Range(0f, template.clip != null ? template.clip.length : 0f);
                cloneSource.Play();

                AudioLowPassFilter lowpassFilter = cloneObject.AddComponent<AudioLowPassFilter>();
                lowpassFilter.lowpassResonanceQ = 1f; // neutral placeholder -- corrected (see LowpassResonanceFor) live every tick in TickEngines
                lowpassFilter.cutoffFrequency = LowpassMaxCutoffHz; // corrected live every tick in TickEngines

                AudioHighPassFilter highpassFilter = cloneObject.AddComponent<AudioHighPassFilter>();
                highpassFilter.highpassResonanceQ = 1f; // neutral placeholder -- corrected (see HighpassResonanceFor) live every tick in TickEngines
                highpassFilter.cutoffFrequency = HighpassMinCutoffHz; // corrected live every tick in TickEngines

                state = new EngineState
                {
                    CloneSource = cloneSource, CloneObject = cloneObject,
                    LowpassFilter = lowpassFilter, HighpassFilter = highpassFilter,
                    Owner = owner
                };
                _engines[engine] = state;
            }
            else
            {
                // Re-synced periodically, not just when .clip happens to
                // differ -- a clip change (Aircraft.SetCockpitRenderers()'s
                // cockpit/external toggle, which several engine types
                // respond to by swapping AudioSource.clip) was the first
                // trigger found, but logging showed the same engine type on
                // the same aircraft could end up with permanently wrong
                // spatialBlend/minDistance/maxDistance depending on exactly
                // when its clone happened to be created -- e.g. a clone
                // created in the same frame as SetCockpitRenderers(true)
                // caught the engine's AudioSource unparented and
                // spatialBlend=0, a transient state from spawn/init that
                // the game fixes up moments later without ever touching
                // .clip, so the old clip-only check never noticed.
                //
                // Doing this unconditionally every single frame (as
                // originally written) turned out to be a real, measurable
                // cost -- 4 curve reads plus up to 4 curve writes through
                // Unity's native interop, times every tracked engine, times
                // every frame, for the engine's entire lifetime -- to guard
                // against a race that only ever matters for a moment right
                // after a clip swap. clip changes still get an instant,
                // unconditional re-sync (real Stop()/Play() needs to happen
                // right then anyway); everything else only needs a periodic
                // safety-net check, since the bug this guards against is a
                // transient state that resolves itself shortly afterward,
                // not one that needs catching within the same frame.
                bool clipChanging = state.CloneSource.clip != template.clip;
                bool dueForPeriodicSync = Time.timeSinceLevelLoad >= state.NextSettingsSyncTime;
                if (clipChanging)
                {
                    Log.LogInfo(
                        $"[EngineAudioDiag] Re-syncing clone for '{engine.GetType().Name}' -- clip changed "
                        + $"'{(state.CloneSource.clip != null ? state.CloneSource.clip.name : "null")}' -> "
                        + $"'{(template.clip != null ? template.clip.name : "null")}'");
                }

                if (clipChanging || dueForPeriodicSync)
                {
                    CopyAudioSourceSettings(template, state.CloneSource, logDiagnostics: clipChanging);
                    state.NextSettingsSyncTime = Time.timeSinceLevelLoad + SettingsSyncIntervalSeconds;
                }

                if (clipChanging)
                {
                    state.CloneSource.Stop();
                    state.CloneSource.clip = template.clip;
                    state.CloneSource.loop = true;
                    if (template.clip != null)
                    {
                        state.CloneSource.time = UnityEngine.Random.Range(0f, template.clip.length);
                        state.CloneSource.Play();
                    }
                }
            }

            // Stored relative to Datum.origin, not as a raw world position --
            // the game re-centers the whole scene (FloatingOrigin.OriginShift)
            // whenever the camera drifts far enough from the current origin,
            // which happens routinely at combat speed. A live GameObject's
            // position follows a shift automatically (Unity re-derives world
            // position from its parent), but a plain Vector3 sitting in this
            // history list does not -- any sample older than the last shift
            // would silently replay at the wrong world position (and thus
            // the wrong apparent distance from the listener) otherwise. This
            // is why only engines needed the fix: they're the only sound
            // here that replays positions from more than one frame ago.
            Vector3 localPosition = Datum.origin.InverseTransformPoint(position);
            state.History.Add(new EngineSample(Time.timeSinceLevelLoad, pitch, volume, localPosition, dopplerLevel));

            float cutoff = Time.timeSinceLevelLoad - EngineHistorySeconds;
            int trimCount = 0;
            while (trimCount < state.History.Count && state.History[trimCount].Time < cutoff)
            {
                trimCount++;
            }
            if (trimCount > 0)
            {
                state.History.RemoveFront(trimCount);
            }
        }

        private static EngineSample SampleHistoryAt(EngineSampleHistory history, float targetTime)
        {
            int lastIndex = history.Count - 1;
            if (targetTime <= history[0].Time)
            {
                return history[0];
            }
            if (targetTime >= history[lastIndex].Time)
            {
                return history[lastIndex];
            }

            int lo = 0;
            int hi = lastIndex;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (history[mid].Time <= targetTime)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            EngineSample a = history[lo];
            EngineSample b = history[hi];
            float span = b.Time - a.Time;
            float t = span > 0.0001f ? (targetTime - a.Time) / span : 0f;
            return new EngineSample(
                targetTime,
                Mathf.Lerp(a.Pitch, b.Pitch, t),
                Mathf.Lerp(a.Volume, b.Volume, t),
                Vector3.Lerp(a.Position, b.Position, t),
                Mathf.Lerp(a.DopplerLevel, b.DopplerLevel, t));
        }

        // Shared by every position-smoothed clone (engine hum, missile
        // motor) that tracks a resampled/physics-tick target instead of a
        // live transform each frame. That smoothing exists purely to hide
        // Doppler pitch noise from Unity's automatic Doppler (see the
        // Doppler-warbling fix), not for any physical reason -- unlike the
        // underlying propagation delay itself, which legitimately makes a
        // fast source's sound trail behind its visual position on a close
        // pass (the real effect of a jet's roar lingering behind it, most
        // pronounced approaching/departing a nearby camera at trans- or
        // supersonic speed). This smoothing lag is on top of that, and is a
        // pure implementation artifact whose perceptual size scales with
        // the listener's angular rate to the source -- tiny at typical
        // distances, but large right as a fast source passes close by.
        // Tightened at close range where that matters, eased back out to
        // the original warble-safe value at typical distance where
        // positional precision barely matters.
        private const float CloseRangeSmoothMeters = 50f;
        private const float FarRangeSmoothMeters = 500f;
        private const float CloseRangeSmoothTime = 0.02f;
        private const float FarRangeSmoothTime = 0.15f;

        private static float ComputePositionSmoothTime(Vector3 position)
        {
            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            float listenerDistance = cam != null
                ? Vector3.Distance(position, cam.transform.position)
                : CloseRangeSmoothMeters;
            return Mathf.Lerp(
                CloseRangeSmoothTime,
                FarRangeSmoothTime,
                Mathf.InverseLerp(CloseRangeSmoothMeters, FarRangeSmoothMeters, listenerDistance));
        }

        private static void TickEngines()
        {
            // Disabling the config toggle only stops *new* samples from
            // being recorded (see each engine type's own patch) -- any
            // clone already tracked before the toggle flipped would
            // otherwise keep playing here forever, frozen on its last
            // sample, at the same time the real source resumes playing
            // normally again. Tearing every entry down as soon as the
            // toggle is off makes the switch clean in both directions:
            // instant vanilla audio with this off, and a fresh start next
            // time an engine patch fires once it's back on.
            if (!EngineAudioConfig.DelayEnabled.Value)
            {
                if (_engines.Count > 0)
                {
                    foreach (EngineState state in _engines.Values)
                    {
                        StopAndDestroyEngine(state);
                    }
                    _engines.Clear();
                }
                return;
            }

            foreach (KeyValuePair<Component, EngineState> kvp in _engines)
            {
                Component engine = kvp.Key;
                EngineState state = kvp.Value;

                try
                {
                    if (engine == null || state.CloneObject == null || state.CloneSource == null || state.History.Count == 0)
                    {
                        StopAndDestroyEngine(state);
                        _enginesToRemove.Add(engine);
                        continue;
                    }

                    // Performance bug, found and fixed: unlike TickLoops()'s
                    // equivalent gun check, this never existed here at all --
                    // once an engine's entry was created, it kept getting a
                    // full per-frame tick (history sampling, position
                    // smoothing, both filter cutoffs) for as long as it
                    // existed, REGARDLESS of the owning aircraft's camera
                    // detail. Several of the recording-side patches already
                    // gate on displayDetail before ever calling
                    // RecordEngineSample, but that only stops feeding NEW
                    // samples -- it does nothing to stop this loop from
                    // continuing to fully process the existing entry off
                    // stale history, forever. In a multiplayer session,
                    // "camera detail" is only ever 1 for whichever single
                    // unit the camera is actually following -- every OTHER
                    // aircraft's engine(s), once started, paid this full
                    // cost for their entire remaining existence (out to the
                    // 20km tracking range), scaling with total aircraft
                    // rather than just the one actually being watched. This
                    // is very likely the dominant cost behind "gets worse
                    // the bigger/longer the session," rather than any single
                    // engine type's own recording-side gate.
                    if (state.Owner != null && state.Owner.displayDetail < 1f)
                    {
                        StopAndDestroyEngine(state);
                        _enginesToRemove.Add(engine);
                        continue;
                    }

                    EngineSample latest = state.History[state.History.Count - 1];
                    Vector3 latestWorldPosition = Datum.origin.TransformPoint(latest.Position);
                    if (IsBeyondTrackingRange(latestWorldPosition))
                    {
                        StopAndDestroyEngine(state);
                        _enginesToRemove.Add(engine);
                        continue;
                    }
                    float delay = ComputeDelaySeconds(latestWorldPosition);
                    float naiveTargetTime = Time.timeSinceLevelLoad - delay;
                    float targetTime;
                    if (!state.HasLastTargetTime || _cameraTeleportedThisTick)
                    {
                        // Not throttled like the tick log below -- this is
                        // exactly the moment the supersonic-overtake clamp
                        // gets bypassed (brand-new entry, or a camera
                        // teleport reset), so it needs to be visible even if
                        // it only happens on one frame.
                        if (VerboseLoggingConfig.Enabled.Value)
                        {
                            Log.LogInfo(
                                $"[EngineAudioDiag] Unclamped target-time reset for '{engine.GetType().Name}' at "
                                + $"t={Time.timeSinceLevelLoad:F2} (reason={(!state.HasLastTargetTime ? "new entry" : "camera teleport")}, "
                                + $"delay={delay:F2}s, naiveTargetTime={naiveTargetTime:F2})");
                        }
                        targetTime = naiveTargetTime;
                        state.HasLastTargetTime = true;
                    }
                    else
                    {
                        // Never advance faster than real time (closing
                        // supersonic), and never move backward (opening
                        // supersonic, which would otherwise briefly replay
                        // the same stretch of history) -- both are
                        // physically impossible for the listener to hear.
                        targetTime = Mathf.Clamp(
                            naiveTargetTime,
                            state.LastTargetTime,
                            state.LastTargetTime + Time.deltaTime);
                    }
                    state.LastTargetTime = targetTime;
                    EngineSample sample = SampleHistoryAt(state.History, targetTime);

                    state.CloneSource.pitch = sample.Pitch;
                    state.CloneSource.volume = sample.Volume;
                    // Positions in history are Datum.origin-relative (see
                    // RecordEngineSample) -- convert back to world space
                    // using the CURRENT origin, so a shift that happened
                    // since this sample was recorded doesn't matter.
                    Vector3 clonePosition = Datum.origin.TransformPoint(sample.Position);

                    if (!state.HasSmoothedPosition)
                    {
                        state.SmoothedPosition = clonePosition;
                        state.HasSmoothedPosition = true;
                        state.SmoothVelocity = Vector3.zero;
                    }
                    else
                    {
                        float smoothTime = ComputePositionSmoothTime(clonePosition);
                        state.SmoothedPosition = Vector3.SmoothDamp(
                            state.SmoothedPosition, clonePosition, ref state.SmoothVelocity, smoothTime);
                    }
                    // Gated on the clone's own smoothed velocity (a direct
                    // byproduct of the SmoothDamp above) relative to the
                    // listener's -- see GateDopplerLevel.
                    state.CloneSource.dopplerLevel = GateDopplerLevel(sample.DopplerLevel, state.SmoothVelocity);
                    state.CloneObject.transform.position = state.SmoothedPosition;
                    float engineLowpassCutoff = ComputeLowpassCutoffHz(GetDistanceToListener(clonePosition));
                    state.LowpassFilter.lowpassResonanceQ = LowpassResonanceFor(engineLowpassCutoff);
                    state.LowpassFilter.cutoffFrequency = engineLowpassCutoff;
                    float engineHighpassCutoff = ComputeCockpitHighpassCutoffHz();
                    state.HighpassFilter.highpassResonanceQ = HighpassResonanceFor(engineHighpassCutoff);
                    state.HighpassFilter.cutoffFrequency = engineHighpassCutoff;

                    if (VerboseLoggingConfig.Enabled.Value && Time.timeSinceLevelLoad >= state.NextDiagLogTime)
                    {
                        state.NextDiagLogTime = Time.timeSinceLevelLoad + 2f;
                        CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
                        float liveDistance = cam != null ? Vector3.Distance(clonePosition, cam.transform.position) : -1f;
                        // Manually evaluates the same CustomRolloff curve
                        // Unity itself should be applying, at the live
                        // distance, so we can tell "my recorded base volume
                        // is fine but Unity isn't applying the curve" apart
                        // from "the curve itself doesn't vary with distance".
                        // Unity's distance curves use a 0-1 normalized X-axis
                        // (0 = the source, 1 = maxDistance), not raw meters --
                        // normalize before evaluating or every reading past
                        // 1 meter clamps to the curve's last keyframe.
                        AnimationCurve rolloffCurve = state.CloneSource.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
                        string expectedRolloffText = "no curve";
                        if (rolloffCurve != null && liveDistance >= 0f && state.CloneSource.maxDistance > 0f)
                        {
                            float normalizedDistance = Mathf.Clamp01(liveDistance / state.CloneSource.maxDistance);
                            float rolloffFactor = rolloffCurve.Evaluate(normalizedDistance);
                            expectedRolloffText = $"curve.Evaluate({normalizedDistance:F3})={rolloffFactor:F3} "
                                + $"expectedFinalVolume~={sample.Volume * rolloffFactor:F3}";
                        }
                        Log.LogInfo(
                            $"[EngineAudioDiag] tick '{engine.GetType().Name}' -- "
                            + $"delay={delay:F2}s liveDistanceToListener={liveDistance:F1}m "
                            + $"clonePos={clonePosition} baseVolume(set)={sample.Volume:F3} "
                            + $"cloneSource.volume(readback)={state.CloneSource.volume:F3} "
                            + $"spatialBlend={state.CloneSource.spatialBlend} minDist={state.CloneSource.minDistance} "
                            + $"maxDist={state.CloneSource.maxDistance} {expectedRolloffText}");
                    }
                }
                catch (Exception)
                {
                    StopAndDestroyEngine(state);
                    _enginesToRemove.Add(engine);
                }
            }

            if (_enginesToRemove.Count > 0)
            {
                foreach (Component engine in _enginesToRemove)
                {
                    _engines.Remove(engine);
                }
                _enginesToRemove.Clear();
            }
        }

        private static void StopAndDestroyEngine(EngineState state)
        {
            if (state.CloneSource != null)
            {
                state.CloneSource.Stop();
            }
            if (state.CloneObject != null)
            {
                UnityEngine.Object.Destroy(state.CloneObject);
            }
        }

        // Simple event-driven start/stop loop -- for a sound that begins
        // once (e.g. a missile motor igniting) and ends once (burnout or
        // destruction), with no re-triggering to handle, unlike Gun's
        // fireInterval-polled loop above. Keyed by the real AudioSource
        // itself, same as PropFan/JetNozzle's thrustAudio/Afterburner,
        // since there's no single owning Component to key by here either.
        private class TrackedLoopState
        {
            public AudioSource CloneSource;
            public GameObject CloneObject;
            public bool Started;
            public float StartTime;
            public float DelaySeconds;
            public bool PendingStop;
            public float ScheduledStopTime;

            // Same Doppler-warbling fix as the engine clones: a missile's
            // position update comes from Motor.Thrust(), which runs on the
            // physics tick rather than every rendered frame, so snapping
            // straight to it produces a stair-stepped position signal that
            // Unity's automatic Doppler reads as noisy velocity. Smoothing
            // toward it instead gives a clean signal to compute Doppler from.
            public Vector3 SmoothedPosition;
            public Vector3 SmoothVelocity;
            public bool HasSmoothedPosition;
            public Vector3 TargetPosition;
            public AudioLowPassFilter LowpassFilter;
            public AudioHighPassFilter HighpassFilter;

            // Optional live pitch/volume pass-through (see
            // UpdateTrackedLoopAudio) -- the original Motor use case never
            // calls it, so HasTargetAudio stays false and the clone just
            // keeps whatever pitch/volume CopyAudioSourceSettings set once
            // at creation, exactly as before. Missile's own flightSound
            // (whoosh volume/pitch that changes continuously with speed)
            // needs this to not sound static/wrong once tracked.
            public bool HasTargetAudio;
            public float TargetPitch;
            public float TargetVolume;
        }

        private static readonly Dictionary<AudioSource, TrackedLoopState> _trackedLoops = new Dictionary<AudioSource, TrackedLoopState>();
        private static readonly List<AudioSource> _trackedLoopsToRemove = new List<AudioSource>();

        public static void StartTrackedLoop(AudioSource template, Vector3 position)
        {
            if (template == null || template.clip == null || _trackedLoops.ContainsKey(template)
                || IsBeyondTrackingRange(position))
            {
                return;
            }

            EnsureDriver();

            GameObject cloneObject = new GameObject("DelayedTrackedLoop");
            cloneObject.transform.SetParent(Datum.origin, worldPositionStays: false);
            cloneObject.transform.position = position;

            AudioSource cloneSource = cloneObject.AddComponent<AudioSource>();
            CopyAudioSourceSettings(template, cloneSource);
            cloneSource.clip = template.clip;
            cloneSource.loop = template.loop;

            AudioLowPassFilter lowpassFilter = cloneObject.AddComponent<AudioLowPassFilter>();
            lowpassFilter.lowpassResonanceQ = 1f; // neutral placeholder -- corrected (see LowpassResonanceFor) live every tick in TickTrackedLoops
            lowpassFilter.cutoffFrequency = LowpassMaxCutoffHz; // corrected live every tick in TickTrackedLoops

            AudioHighPassFilter highpassFilter = cloneObject.AddComponent<AudioHighPassFilter>();
            highpassFilter.highpassResonanceQ = 1f; // neutral placeholder -- corrected (see HighpassResonanceFor) live every tick in TickTrackedLoops
            highpassFilter.cutoffFrequency = HighpassMinCutoffHz; // corrected live every tick in TickTrackedLoops

            _trackedLoops[template] = new TrackedLoopState
            {
                CloneSource = cloneSource,
                CloneObject = cloneObject,
                StartTime = Time.timeSinceLevelLoad,
                DelaySeconds = ComputeDelaySeconds(position),
                TargetPosition = position,
                SmoothedPosition = position,
                HasSmoothedPosition = true,
                LowpassFilter = lowpassFilter,
                HighpassFilter = highpassFilter
            };
        }

        // Schedules a delayed stop instead of stopping immediately -- a
        // distant listener wouldn't hear the motor cut out the instant it
        // actually does in the real game either, same reasoning as the gun
        // loop's own delayed stop. Recomputed fresh from the position at
        // the moment of stopping (not the start-time delay), which is also
        // what gives an approaching listener the correctly-compressed
        // burst duration.
        public static void StopTrackedLoop(AudioSource template, Vector3 position)
        {
            if (template != null && _trackedLoops.TryGetValue(template, out TrackedLoopState state) && !state.PendingStop)
            {
                state.PendingStop = true;
                float stopDelay = ComputeDelaySeconds(position);
                // At high enough closing speed the compressed stop can
                // otherwise land before the (also delayed) start has even
                // arrived, which would be heard as an impossible "end
                // before beginning" -- clamp so the burst can compress all
                // the way to zero length but never go negative.
                state.ScheduledStopTime = Mathf.Max(
                    Time.timeSinceLevelLoad + stopDelay,
                    state.StartTime + state.DelaySeconds);
            }
        }

        // Called from Thrust()'s Postfix, i.e. on the physics tick, not
        // once per rendered frame -- just records where the missile
        // currently is. TickTrackedLoops() (once per rendered frame, via
        // the driver) smooths the clone's actual position toward this
        // target, same reasoning and Time.deltaTime timing as the engine
        // clones' own Doppler-warbling fix.
        public static void UpdateTrackedLoopPosition(AudioSource template, Vector3 position)
        {
            if (template != null && _trackedLoops.TryGetValue(template, out TrackedLoopState state))
            {
                state.TargetPosition = position;
            }
        }

        // Optional -- only needed by tracked loops whose real source's
        // pitch/volume keeps changing after it starts (e.g. Missile's
        // flightSound, driven by live speed), unlike Motor's own sources
        // which are set once at Activate() and never touched again.
        public static void UpdateTrackedLoopAudio(AudioSource template, float pitch, float volume)
        {
            if (template != null && _trackedLoops.TryGetValue(template, out TrackedLoopState state))
            {
                state.HasTargetAudio = true;
                state.TargetPitch = pitch;
                state.TargetVolume = volume;
            }
        }

        private static void TickTrackedLoops()
        {
            foreach (KeyValuePair<AudioSource, TrackedLoopState> kvp in _trackedLoops)
            {
                AudioSource template = kvp.Key;
                TrackedLoopState state = kvp.Value;

                try
                {
                    if (state.CloneObject == null || state.CloneSource == null)
                    {
                        StopAndDestroyTrackedLoop(state);
                        _trackedLoopsToRemove.Add(template);
                        continue;
                    }

                    if (!state.HasSmoothedPosition)
                    {
                        state.SmoothedPosition = state.TargetPosition;
                        state.HasSmoothedPosition = true;
                    }
                    else
                    {
                        // Same distance-scaled smoothing as the engine
                        // clones (see ComputePositionSmoothTime) -- a
                        // missile flashing past the camera up close is
                        // exactly the scenario where a fixed smoothing
                        // constant reads as the sound trailing behind it.
                        float smoothTime = ComputePositionSmoothTime(state.TargetPosition);
                        state.SmoothedPosition = Vector3.SmoothDamp(
                            state.SmoothedPosition, state.TargetPosition, ref state.SmoothVelocity, smoothTime);
                    }
                    state.CloneObject.transform.position = state.SmoothedPosition;
                    float trackedLowpassCutoff = ComputeLowpassCutoffHz(GetDistanceToListener(state.SmoothedPosition));
                    state.LowpassFilter.lowpassResonanceQ = LowpassResonanceFor(trackedLowpassCutoff);
                    state.LowpassFilter.cutoffFrequency = trackedLowpassCutoff;
                    float trackedHighpassCutoff = ComputeCockpitHighpassCutoffHz();
                    state.HighpassFilter.highpassResonanceQ = HighpassResonanceFor(trackedHighpassCutoff);
                    state.HighpassFilter.cutoffFrequency = trackedHighpassCutoff;

                    if (state.HasTargetAudio && state.Started)
                    {
                        state.CloneSource.pitch = state.TargetPitch;
                        state.CloneSource.volume = state.TargetVolume;
                    }

                    // template (the real missile's AudioSource) going
                    // Unity-fake-null means the missile itself was destroyed
                    // outright without Burnout()/Destruct() explicitly
                    // stopping this loop first (e.g. an abrupt kill) --
                    // treat that as an implicit stop, using the clone's last
                    // known position since there's no live one to ask.
                    if (template == null && !state.PendingStop)
                    {
                        state.PendingStop = true;
                        state.ScheduledStopTime = Mathf.Max(
                            Time.timeSinceLevelLoad + ComputeDelaySeconds(state.CloneObject.transform.position),
                            state.StartTime + state.DelaySeconds);
                    }

                    if (state.PendingStop)
                    {
                        if (Time.timeSinceLevelLoad >= state.ScheduledStopTime)
                        {
                            StopAndDestroyTrackedLoop(state);
                            _trackedLoopsToRemove.Add(template);
                        }
                        continue;
                    }

                    if (IsBeyondTrackingRange(state.CloneObject.transform.position))
                    {
                        StopAndDestroyTrackedLoop(state);
                        _trackedLoopsToRemove.Add(template);
                        continue;
                    }

                    if (!state.Started && Time.timeSinceLevelLoad - state.StartTime >= state.DelaySeconds)
                    {
                        state.Started = true;
                        state.CloneSource.Play();
                    }
                }
                catch (Exception)
                {
                    StopAndDestroyTrackedLoop(state);
                    _trackedLoopsToRemove.Add(template);
                }
            }

            if (_trackedLoopsToRemove.Count > 0)
            {
                foreach (AudioSource template in _trackedLoopsToRemove)
                {
                    _trackedLoops.Remove(template);
                }
                _trackedLoopsToRemove.Clear();
            }
        }

        private static void StopAndDestroyTrackedLoop(TrackedLoopState state)
        {
            if (state.CloneSource != null)
            {
                state.CloneSource.Stop();
            }
            if (state.CloneObject != null)
            {
                UnityEngine.Object.Destroy(state.CloneObject);
            }
        }

        private static readonly AudioSourceCurveType[] CurveTypes =
        {
            AudioSourceCurveType.CustomRolloff,
            AudioSourceCurveType.SpatialBlend,
            AudioSourceCurveType.ReverbZoneMix,
            AudioSourceCurveType.Spread
        };

        private static void CopyAudioSourceSettings(AudioSource from, AudioSource to, bool logDiagnostics = false)
        {
            if (logDiagnostics)
            {
                Log.LogInfo(
                    $"[EngineAudioDiag] SOURCE '{DescribeAudioSource(from)}' -- "
                    + $"minDistance={from.minDistance} maxDistance={from.maxDistance} "
                    + $"rolloffMode={from.rolloffMode} spatialBlend={from.spatialBlend} "
                    + $"volume={from.volume} clip={(from.clip != null ? from.clip.name : "null")}");
                foreach (AudioSourceCurveType curveType in CurveTypes)
                {
                    AnimationCurve sourceCurve = from.GetCustomCurve(curveType);
                    Log.LogInfo(
                        $"[EngineAudioDiag]   curve {curveType}: "
                        + (sourceCurve == null ? "null (not set)" : $"{sourceCurve.length} keyframes"));
                }
            }

            to.outputAudioMixerGroup = from.outputAudioMixerGroup;
            to.pitch = from.pitch;
            to.spatialBlend = from.spatialBlend;
            to.rolloffMode = from.rolloffMode;
            to.minDistance = from.minDistance;
            to.maxDistance = from.maxDistance;
            to.volume = from.volume;
            to.spread = from.spread;
            to.reverbZoneMix = from.reverbZoneMix;
            // Deliberately NOT copied like the other two bypass flags below --
            // bypassEffects skips any filter component (AudioLowPassFilter,
            // AudioHighPassFilter) sitting on the SAME GameObject as the
            // source, which is exactly the mechanism every one of this mod's
            // per-source filters depends on. At least one vanilla weapon (the
            // 57mm autocannon) ships with this flag already set true on its
            // own AudioSource -- copying it faithfully silently made our own
            // lowpass/highpass components on the clone do nothing, with no
            // error or warning, while every other gun (bypassEffects=false by
            // default) filtered correctly. Since we always attach our own
            // filters right after this call and need them honored, this is
            // forced off unconditionally rather than inherited.
            to.bypassEffects = false;
            to.bypassListenerEffects = from.bypassListenerEffects;
            to.bypassReverbZones = from.bypassReverbZones;
            // Inherit the template's current dopplerLevel by default -- loop
            // and engine clones keep updating this every frame from their
            // own recorded/live values afterward anyway (see NotifyLoopActive/
            // RecordEngineSample callers), so this is only the resting value
            // for anything that doesn't. One-shot clones override this
            // explicitly to 0 right after creation (see ScheduleDelayedOneShot)
            // since they're stationary once emitted -- matching vanilla's own
            // ExplosionAudio, which does the same for its one-shot explosions.
            to.dopplerLevel = from.dopplerLevel;

            // 3D Sound Settings can drive spatialBlend/reverbZoneMix/spread/
            // rolloff by an animation curve over distance instead of a flat
            // value -- copying only the scalar fields above silently drops
            // that curve, which is exactly what caused gunshots to lose
            // their distance-based volume falloff the first time around.
            // GetCustomCurve returns null when no curve was ever set, and
            // SetCustomCurve throws ArgumentNullException on null, so only
            // copy the ones that actually exist.
            foreach (AudioSourceCurveType curveType in CurveTypes)
            {
                AnimationCurve curve = from.GetCustomCurve(curveType);
                if (curve != null)
                {
                    to.SetCustomCurve(curveType, curve);
                }
            }

            if (logDiagnostics)
            {
                Log.LogInfo(
                    $"[EngineAudioDiag] CLONE (after copy) -- "
                    + $"minDistance={to.minDistance} maxDistance={to.maxDistance} "
                    + $"rolloffMode={to.rolloffMode} spatialBlend={to.spatialBlend} "
                    + $"volume={to.volume} clip={(to.clip != null ? to.clip.name : "null (not yet assigned)")}");
                foreach (AudioSourceCurveType curveType in CurveTypes)
                {
                    AnimationCurve cloneCurve = to.GetCustomCurve(curveType);
                    Log.LogInfo(
                        $"[EngineAudioDiag]   curve {curveType}: "
                        + (cloneCurve == null ? "null (not set)" : $"{cloneCurve.length} keyframes"));
                }
            }
        }

        private static string DescribeAudioSource(AudioSource source)
        {
            return source == null ? "null" : DescribePath(source.transform);
        }

        private static string DescribePath(Transform t)
        {
            if (t == null)
            {
                return "null";
            }
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        private static void EnsureDriver()
        {
            if (_driverObject != null)
            {
                return;
            }
            _driverObject = new GameObject("QOL_Realisim_Fixes.SoundPropagationDriver");
            UnityEngine.Object.DontDestroyOnLoad(_driverObject);
            _driverObject.AddComponent<SoundPropagationDriver>();
        }

        // Called on LevelInfo.OnDestroy (mission/scene teardown) so a stale
        // pending shot or loop from the mission that just ended can never
        // carry over into the next one holding a reference to something
        // Unity is about to (or just did) destroy -- belt-and-suspenders
        // alongside the try/catch above, covering the same "stuck on" risk
        // from the other direction (proactively clearing instead of only
        // reacting to a bad entry after the fact).
        internal static void ClearAll()
        {
            foreach (PendingShot shot in _pending)
            {
                if (shot.TempObject != null)
                {
                    UnityEngine.Object.Destroy(shot.TempObject);
                }
            }
            _pending.Clear();

            foreach (LoopState state in _loops.Values)
            {
                StopAndDestroyLoop(state);
            }
            _loops.Clear();

            foreach (EngineState state in _engines.Values)
            {
                StopAndDestroyEngine(state);
            }
            _engines.Clear();

            foreach (TrackedLoopState state in _trackedLoops.Values)
            {
                StopAndDestroyTrackedLoop(state);
            }
            _trackedLoops.Clear();
        }

        private static float _nextListenerDiagTime;

        // One-off-ish (throttled) sanity check that the transform this
        // whole mod treats as "the listener" (CameraStateManager.i.transform)
        // is actually where Unity's own AudioListener is -- if there's a
        // mismatch (wrong GameObject, disabled, or more than one enabled
        // listener in the scene, which Unity only partially supports),
        // every distance calculation this mod does would be measuring from
        // the wrong point while Unity's actual audio attenuation uses a
        // different one entirely.
        private static void LogListenerDiagnostics()
        {
            if (Time.timeSinceLevelLoad < _nextListenerDiagTime)
            {
                return;
            }
            _nextListenerDiagTime = Time.timeSinceLevelLoad + 5f;

            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            Log.LogInfo(
                $"[EngineAudioDiag] Assumed listener (CameraStateManager.i.transform): "
                + (cam != null ? $"'{DescribePath(cam.transform)}' pos={cam.transform.position}" : "null (no CameraStateManager)"));

            AudioListener[] listeners = UnityEngine.Object.FindObjectsOfType<AudioListener>();
            Log.LogInfo($"[EngineAudioDiag] Active AudioListener(s) in scene: {listeners.Length}");
            foreach (AudioListener listener in listeners)
            {
                Log.LogInfo(
                    $"[EngineAudioDiag]   '{DescribePath(listener.transform)}' "
                    + $"enabled={listener.enabled} pos={listener.transform.position}");
            }
        }

        private static float _nextCockpitDiagTime;

        // Direct evidence for the "cockpit lowpass still doesn't seem to
        // apply" report -- logs the actual currentState/cockpitState type
        // names and the IsInCockpitView() result every couple seconds, so a
        // real test run shows definitively whether cockpit detection itself
        // is the problem versus something else (e.g. testing via engine
        // sound while Engine Sound Propagation is still off, which routes
        // through none of this mod's systems at all). Extended for the
        // "filter doesn't reapply after cockpit -> third-person -> flyby ->
        // cockpit" report -- also dumps the ejection-muffle-release latch
        // and airframe-opening multiplier, since both are LATCHED/CACHED
        // state (unlike the rest of this computation, which is derived
        // fresh every call) and are the only things that could plausibly
        // leave the cockpit cutoff stuck open independent of the live
        // camera-state check above.
        private static void LogCockpitDiagnostics()
        {
            if (Time.timeSinceLevelLoad < _nextCockpitDiagTime)
            {
                return;
            }
            _nextCockpitDiagTime = Time.timeSinceLevelLoad + 2f;

            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            if (cam == null)
            {
                Log.LogInfo("[CockpitLowpassDiag] CameraStateManager.i is null.");
                return;
            }
            bool inCockpit = IsInCockpitView();
            Log.LogInfo(
                $"[CockpitLowpassDiag] currentState={(cam.currentState != null ? cam.currentState.GetType().Name : "null")} "
                + $"cockpitState={(cam.cockpitState != null ? cam.cockpitState.GetType().Name : "null")} "
                + $"IsInCockpitView={inCockpit} CockpitLowpassConfig.Enabled={CockpitLowpassConfig.Enabled.Value} "
                + $"CutoffHz={CockpitLowpassConfig.CutoffHz:F0} "
                + $"sampleComputedCutoff(50m)={ComputeLowpassCutoffHz(50f):F0} "
                + $"finalCockpitOnlyCutoff={ComputeCockpitLowpassCutoffOnly():F0} "
                + $"ejectionReleaseActive={_ejectionMuffleReleaseActive} ejectionReleaseBlend={GetEjectionMuffleReleaseBlend():F2} "
                + $"airframeMultiplier={GetAirframeCockpitMuffleMultiplier():F2}");
        }

        private static float _nextPerfSummaryTime;
        private static double _perfSummaryTotalMs;
        private static double _perfSummaryMaxMs;
        private static int _perfSummarySamples;

        // Always on, regardless of Verbose Logging -- this is the one
        // diagnostic meant to answer "is this mod's own per-frame work
        // actually the problem" directly, with real numbers, rather than
        // guessing from code reading alone. Cheap (one Stopwatch per frame,
        // one log line every 10s): tracked-object counts confirm whether
        // the various leak/cleanup fixes elsewhere are actually holding
        // steady in a real session instead of growing, and the timing
        // shows how much of a frame this mod's own systems actually cost
        // versus whatever else might be the real bottleneck.
        internal static void Tick()
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            TickInner();
            stopwatch.Stop();

            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            _perfSummaryTotalMs += elapsedMs;
            _perfSummarySamples++;
            if (elapsedMs > _perfSummaryMaxMs)
            {
                _perfSummaryMaxMs = elapsedMs;
            }

            if (Time.timeSinceLevelLoad >= _nextPerfSummaryTime)
            {
                _nextPerfSummaryTime = Time.timeSinceLevelLoad + 10f;
                double avgMs = _perfSummarySamples > 0 ? _perfSummaryTotalMs / _perfSummarySamples : 0;
                Log.LogInfo(
                    $"[PerfSummary] tracked: engines={_engines.Count} gunLoops={_loops.Count} "
                    + $"trackedLoops={_trackedLoops.Count} pendingOneShots={_pending.Count} -- "
                    + $"Tick() cost over last {_perfSummarySamples} frames: avg={avgMs:F3}ms max={_perfSummaryMaxMs:F3}ms");
                _perfSummaryTotalMs = 0;
                _perfSummarySamples = 0;
                _perfSummaryMaxMs = 0;
            }
        }

        private static void TickInner()
        {
            if (VerboseLoggingConfig.Enabled.Value)
            {
                LogListenerDiagnostics();
                LogCockpitDiagnostics();
            }
            DetectCameraTeleport();

            TickLoops();
            TickEngines();
            TickTrackedLoops();

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingShot shot = _pending[i];
                if (shot.Source == null || shot.TempObject == null)
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                // Already playing and just waiting to be cut short (see
                // ClipDurationSeconds) -- separate from the "still waiting
                // to start" branch below since it's timed off PlayStartTime,
                // not SpawnTime/propagation delay.
                if (shot.Started)
                {
                    if (Time.timeSinceLevelLoad - shot.PlayStartTime >= shot.ClipDurationSeconds)
                    {
                        shot.Source.Stop();
                        _pending.RemoveAt(i);
                        UnityEngine.Object.Destroy(shot.TempObject, 0.5f);
                    }
                    continue;
                }

                float elapsed = Time.timeSinceLevelLoad - shot.SpawnTime;
                if (elapsed > MaxPendingWaitSeconds)
                {
                    // Listener has been receding faster than the speed of
                    // sound for a full minute -- correctly never going to
                    // arrive, so stop carrying it.
                    _pending.RemoveAt(i);
                    UnityEngine.Object.Destroy(shot.TempObject);
                    continue;
                }

                // Recomputed every tick from the listener's CURRENT
                // position rather than a value baked in at schedule time --
                // the emission point itself (shot.TempObject) is fixed, but
                // the listener is very often still moving (flying) during
                // the wait, so a one-time snapshot would make the sound
                // arrive early or late (or, if outrunning it, never) versus
                // where the listener actually ends up.
                float requiredDelay = ComputeDelaySeconds(shot.TempObject.transform.position) + shot.ExtraDelaySeconds;
                if (elapsed >= requiredDelay)
                {
                    if (shot.LowpassFilter != null)
                    {
                        float oneShotLowpassCutoff = shot.OwnHitDistanceToCockpitMeters.HasValue
                            ? ComputeOwnHitImpactLowpassCutoffHz(shot.OwnHitDistanceToCockpitMeters.Value)
                            : ComputeLowpassCutoffHz(GetDistanceToListener(shot.TempObject.transform.position), shot.CockpitMuffleMultiplier);
                        shot.LowpassFilter.lowpassResonanceQ = LowpassResonanceFor(oneShotLowpassCutoff);
                        shot.LowpassFilter.cutoffFrequency = oneShotLowpassCutoff;
                    }
                    if (shot.HighpassFilter != null)
                    {
                        float cockpitHighpassCutoff = shot.OwnHitDistanceToCockpitMeters.HasValue
                            ? ComputeOwnHitImpactHighpassCutoffHz(shot.OwnHitDistanceToCockpitMeters.Value)
                            : ApplyMuffleMultiplier(HighpassMinCutoffHz, ComputeCockpitHighpassCutoffHz(), shot.CockpitMuffleMultiplier);
                        float oneShotHighpassCutoff = Mathf.Max(shot.RequestedHighpassCutoffHz, cockpitHighpassCutoff);
                        shot.HighpassFilter.highpassResonanceQ = HighpassResonanceFor(oneShotHighpassCutoff);
                        shot.HighpassFilter.cutoffFrequency = oneShotHighpassCutoff;
                    }
                    shot.Source.PlayOneShot(shot.Clip);

                    if (shot.ClipDurationSeconds > 0f)
                    {
                        // Leave it in _pending, now tracked by the Started
                        // branch above instead, so it gets cut off partway
                        // through instead of left to ring out in full.
                        shot.Started = true;
                        shot.PlayStartTime = Time.timeSinceLevelLoad;
                    }
                    else
                    {
                        _pending.RemoveAt(i);
                        UnityEngine.Object.Destroy(shot.TempObject, shot.Clip.length + 0.5f);
                    }
                }
            }
        }
    }

    internal class SoundPropagationDriver : MonoBehaviour
    {
        private void Update()
        {
            SoundPropagation.Tick();
        }
    }
}
