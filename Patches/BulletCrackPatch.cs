using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // A real supersonic bullet passing near an observer trails its own tiny
    // Mach cone -- heard as a sharp "crack" almost instantly, well before
    // the gun's own muzzle "bang" arrives (which is delayed the normal
    // speed-of-sound way, see SoundPropagation). That crack-before-bang
    // ordering is the classic experience of actually being shot at/near.
    //
    // BulletSim.Bullet.TrajectoryTrace already receives the camera's
    // position every physics tick (used for tracer LOD sizing) but never
    // checks it for a close pass. A Prefix records the bullet's position
    // before this tick's movement; a Postfix then checks the resulting
    // travel SEGMENT (not just the endpoint -- a fast bullet can cover
    // several meters in a single tick, which could otherwise skip clean
    // over a close pass) for closest approach to the camera.
    //
    // The very first tick for a bullet (called directly from
    // BulletSim.AddBullet, with a real `muzzle` Transform passed in) is
    // skipped entirely -- position starts at the struct default (0,0,0)
    // until TrajectoryTrace itself snaps it to the muzzle, so the
    // "pre-tick" position captured by the Prefix would be bogus for that
    // one call, and a crack right at the muzzle wouldn't be a meaningful
    // near-miss anyway -- that's just the gunshot itself.
    [HarmonyPatch(typeof(BulletSim.Bullet), nameof(BulletSim.Bullet.TrajectoryTrace))]
    internal static class BulletCrackPatch
    {
        // Roughly the "you'd definitely hear that" range for a supersonic
        // rifle-caliber crack passing by. Still comfortably past the ~20m
        // point where SoundPropagation's distance-based lowpass starts
        // cutting anything (a crack any closer is, physically, too close
        // for atmospheric absorption to have done anything yet), so the
        // lowpass still has real range to work with across this radius
        // (~11kHz cutoff at the far edge vs. ~22kHz/unfiltered up close).
        private const float CrackRadiusMeters = 50f;

        // A dedicated recorded crack sample (Sounds\bulletcrack.wav, see
        // BulletCrackAssets/WavUtility) replaces the earlier sonic-boom-clip
        // hack -- pitched up only modestly (it's already the right kind of
        // sound, unlike the boom clip which needed heavy pitching just to
        // get in the right neighborhood) and cut to about half its own
        // length so just the sharp leading transient plays, not its full
        // tail. CrackPitchDeviation is scaled down from what a much higher
        // base pitch used -- keeping that same ABSOLUTE deviation next to a
        // base of only 1.5 would risk a per-crack pitch near zero (or even
        // negative/reversed playback); this keeps roughly the same
        // proportional variation instead.
        private const float CrackPitchDeviation = 0.15f;

        // Bigger rounds are physically longer, and a longer round's N-wave
        // has a correspondingly longer wavelength -- a deeper, lower-pitched
        // crack -- same physics already used for the missile-vs-aircraft
        // sonic boom pitch (SonicBoomManagePatch). There's no explicit
        // "caliber" field on WeaponInfo, but massPerRound is a real per-gun
        // value already in the data.
        //
        // These three anchor points are real values pulled from an in-game
        // massPerRound dump (see LogWeaponMassDumpOnce), confirmed against
        // which specific weapon variant actually triggered each crack during
        // a live test, not guessed: 'Light Machine Gun' (7.62mm-class,
        // 0.04kg) -> 1.5x, '12.7mm Machine Gun' (.50 cal, 0.12kg) -> 1.0x/no
        // change, '155mm Railgun' (the largest gun that actually fired,
        // 50kg -- there's also a 10kg variant of the same name elsewhere in
        // the data, unused here since it's not what was confirmed firing)
        // -> originally 0.25x, raised to 0.5x as the floor since the deepest
        // setting read as too extreme in practice. Interpolated linearly in
        // LOG-mass space between anchors, not raw mass -- the range spans
        // three orders of magnitude, so a raw-mass interpolation would be
        // entirely dominated by the 50kg end and barely move at all across
        // the whole small-arms range where most of the actual variety
        // lives. Clamped flat beyond either end rather than extrapolated
        // further.
        private const float LightMachineGunMassKg = 0.04f;
        private const float HeavyMachineGunMassKg = 0.12f;
        private const float LargestGunMassKg = 50f;
        private const float SmallArmsPitch = 1.5f;
        private const float HeavyMachineGunPitch = 1.0f;
        private const float LargestGunPitch = 0.5f;

        // Volume also scales off the same caliber curve -- a bigger round's
        // crack isn't just deeper, it's genuinely louder (more energy in the
        // shockwave). AudioSource.volume isn't capped at 1 the way a UI
        // alpha is, so these can (and do) go well above it. Small-arms-caliber
        // cracks (SmallArmsPitch, 1.5x) get BulletCrackConfig.VolumeAtSmallArms;
        // anything with a lower (bigger-caliber) pitch scales up further from
        // there, reaching VolumeAtLargestGun at LargestGunPitch. Live-tunable
        // via ConfigManager rather than fixed constants, same as the distance
        // lowpass sliders -- easier to dial in by ear than guess-and-rebuild.

        // The sonic boom clip this used to reuse carried plenty of low-end
        // "thud" even pitched way up -- AudioSource.pitch resamples playback
        // speed, it doesn't remove bass content. Kept here in case the
        // custom clip above ever needs the same treatment; harmless no-op
        // if the clip's own low end is already thin. Applied ahead of the
        // distance lowpass in the filter chain -- see
        // ScheduleDelayedOneShot's highpassCutoffHz.
        private const float CrackHighpassCutoffHz = 4000f;

        // Global rate cap on crack-sound object creation -- see its use
        // site in Postfix for the full reasoning. ~12/second max regardless
        // of how many individual bullets qualify in that window.
        private const float MinCrackIntervalSeconds = 0.08f;
        private static float _lastGlobalCrackTime = float.NegativeInfinity;

        // A round heavy/large enough to read as a genuinely deep crack
        // physically shakes the airframe, not just the ear, when it passes
        // close. CameraStateManager.ShakeCamera already no-ops unless the
        // camera is actually in the cockpit state, so this only ever does
        // anything in first-person view -- no separate view-mode check
        // needed. Intensity scales with proximity across the crack's own
        // radius (not vanilla's own sonic-boom shake formula, which is
        // scaled for a 1000m+ range and would just read as a constant 1.0
        // everywhere inside this feature's much shorter 100m band). Raised
        // from 0.5 to 0.75 to open up more of the caliber range to shaking
        // -- 0.5 stopped qualifying anything at all once LargestGunPitch's
        // own floor was also raised to 0.5 (nothing could ever be strictly
        // less than the threshold once they were equal).
        private const float ShakeCaliberPitchThreshold = 0.75f;

        // Weak table, not a plain Dictionary/HashSet -- bullets are plain
        // C# objects created and discarded constantly (every shot fired),
        // and a normal collection keyed by Bullet would keep every one of
        // them alive forever just by being in this table. A
        // ConditionalWeakTable's entries don't keep their keys alive, so a
        // bullet already removed from BulletSim's own list is free to be
        // collected normally with no extra cleanup needed here.
        private static readonly ConditionalWeakTable<BulletSim.Bullet, object> CrackedBullets =
            new ConditionalWeakTable<BulletSim.Bullet, object>();

        private static AudioSource _templateSource;

        private static void Prefix(BulletSim.Bullet __instance, out GlobalPosition __state)
        {
            __state = __instance.position;
        }

        // One-time dump of every gun's real massPerRound -- used to
        // calibrate ComputeCaliberPitch's anchor points against the game's
        // actual data instead of guessed real-world ammunition weights,
        // which may not match this game's own internal balance numbers at
        // all. Same technique as the CRAM fragmentation feature's target
        // weight list (pulled from a UnitDefinition dump rather than
        // guessed). Runs on the very first bullet tick of the very first
        // gun fired in a mission, regardless of range/crack conditions.
        private static bool _weaponMassDumpLogged;

        private static void LogWeaponMassDumpOnce()
        {
            if (_weaponMassDumpLogged)
            {
                return;
            }
            _weaponMassDumpLogged = true;
            WeaponInfo[] allWeapons = Resources.FindObjectsOfTypeAll<WeaponInfo>();
            SoundPropagation.Log.LogInfo(
                $"[BulletCrackDiag] === Gun massPerRound dump ({allWeapons.Length} WeaponInfo assets total) ===");
            foreach (WeaponInfo weapon in allWeapons)
            {
                if (weapon.gun)
                {
                    SoundPropagation.Log.LogInfo(
                        $"[BulletCrackDiag]   '{weapon.weaponName}' massPerRound={weapon.massPerRound:F4}kg "
                        + $"muzzleVelocity={weapon.muzzleVelocity:F0}m/s");
                }
            }
        }

        private static void Postfix(
            BulletSim.Bullet __instance, GlobalPosition __state,
            Transform muzzle, WeaponInfo info, Unit owner, bool hasCamera, GlobalPosition cameraPos)
        {
            LogWeaponMassDumpOnce();

            if (muzzle != null || !hasCamera || !BulletCrackConfig.Enabled.Value)
            {
                return;
            }
            if (CrackedBullets.TryGetValue(__instance, out _))
            {
                return;
            }

            // Your own outgoing rounds shouldn't crack right next to your
            // own ear while in cockpit view -- there's no "near miss" for
            // the shooter, that's just the gun going off. Scoped to
            // cockpit view specifically per request; external/free-cam
            // view is left as-is.
            if (owner != null && GameManager.IsLocalAircraft(owner) && SoundPropagation.IsInCockpitView())
            {
                return;
            }

            Vector3 closestPoint = ClosestPointOnSegment(__state, __instance.position, cameraPos, out float distance);
            if (distance > CrackRadiusMeters)
            {
                return;
            }

            // A genuine CIWS-style automatic cannon (confirmed live: a 30mm
            // rotary cannon logged ~2700 qualifying rounds in about 10
            // seconds during one burst) can have HUNDREDS of distinct
            // bullets independently qualify within the same fraction of a
            // second -- each one creating a brand-new GameObject+AudioSource
            // +filters is real, uncapped object-creation cost, and that (not
            // any per-bullet check above) is what actually tanked
            // performance. A dense stream of rounds passing by also
            // physically blurs into one continuous sound in reality anyway,
            // not hundreds of overlapping discrete cracks, so capping the
            // rate is more correct-sounding too, not just a perf
            // compromise. Deliberately NOT marking the bullet as cracked
            // when throttled -- it's a cheap recheck, and letting it retry
            // a later tick (or another bullet succeed instead) means the
            // cap doesn't have to guess which specific bullet "deserves" to
            // be the one that actually cracks.
            float now = Time.timeSinceLevelLoad;
            if (now - _lastGlobalCrackTime < MinCrackIntervalSeconds)
            {
                return;
            }
            _lastGlobalCrackTime = now;

            CrackedBullets.Add(__instance, __instance);
            float massPerRound = info != null ? info.massPerRound : 0f;
            if (VerboseLoggingConfig.Enabled.Value)
            {
                SoundPropagation.Log.LogInfo(
                    $"[BulletCrackDiag] Crack triggered at t={Time.timeSinceLevelLoad:F2} distance={distance:F1}m "
                    + $"massPerRound={massPerRound:F4}kg position={closestPoint}");
            }
            PlayCrack(closestPoint, massPerRound, distance);
        }

        // Standard closest-point-on-segment projection, done directly in
        // GlobalPosition space (origin-relative floats) via its own
        // +/- operators so no conversion to world space is needed until the
        // very end, when the result is actually used to place a sound.
        private static Vector3 ClosestPointOnSegment(
            GlobalPosition segmentStart, GlobalPosition segmentEnd, GlobalPosition point, out float distance)
        {
            Vector3 segment = segmentEnd - segmentStart;
            float lengthSquared = segment.sqrMagnitude;
            float t = lengthSquared > 0.0001f
                ? Mathf.Clamp01(Vector3.Dot(point - segmentStart, segment) / lengthSquared)
                : 0f;
            GlobalPosition closest = segmentStart + segment * t;
            distance = FastMath.Distance(closest, point);
            return closest.ToLocalPosition();
        }

        // A dedicated, otherwise-unused AudioSource purely as a settings
        // donor for CopyAudioSourceSettings (spatialBlend/rolloff/pitch
        // etc.) -- unlike the real sonic boom, a bullet crack isn't tied to
        // any one Unit's own AudioSource, so there's nothing existing to
        // borrow settings from.
        private static AudioSource GetTemplate()
        {
            if (_templateSource != null)
            {
                return _templateSource;
            }
            GameObject templateObject = new GameObject("QOL_Realisim_Fixes.BulletCrackTemplate");
            Object.DontDestroyOnLoad(templateObject);
            _templateSource = templateObject.AddComponent<AudioSource>();
            _templateSource.playOnAwake = false;
            _templateSource.spatialBlend = 1f;
            _templateSource.rolloffMode = AudioRolloffMode.Logarithmic;
            _templateSource.minDistance = 3f;
            _templateSource.maxDistance = 150f;
            _templateSource.dopplerLevel = 0f;
            return _templateSource;
        }

        // massPerRound <= 0 (missing/unavailable data) falls back to the
        // unscaled base pitch rather than dividing by zero or guessing.
        private static float ComputeCaliberPitch(float massPerRound)
        {
            if (massPerRound <= 0f)
            {
                return HeavyMachineGunPitch; // no data -- neutral fallback rather than an extreme
            }
            if (massPerRound <= LightMachineGunMassKg)
            {
                return SmallArmsPitch;
            }
            if (massPerRound <= LargestGunMassKg)
            {
                float logMass = Mathf.Log(massPerRound);
                if (massPerRound <= HeavyMachineGunMassKg)
                {
                    float t = Mathf.InverseLerp(Mathf.Log(LightMachineGunMassKg), Mathf.Log(HeavyMachineGunMassKg), logMass);
                    return Mathf.Lerp(SmallArmsPitch, HeavyMachineGunPitch, t);
                }
                else
                {
                    float t = Mathf.InverseLerp(Mathf.Log(HeavyMachineGunMassKg), Mathf.Log(LargestGunMassKg), logMass);
                    return Mathf.Lerp(HeavyMachineGunPitch, LargestGunPitch, t);
                }
            }
            return LargestGunPitch;
        }

        // Same anchor points as the pitch curve, just mapped to volume
        // instead -- a lower (bigger-caliber) pitch means a louder crack.
        private static float ComputeCrackVolume(float caliberPitch)
        {
            float t = Mathf.InverseLerp(SmallArmsPitch, LargestGunPitch, caliberPitch);
            return Mathf.Lerp(BulletCrackConfig.VolumeAtSmallArms, BulletCrackConfig.VolumeAtLargestGun, t);
        }

        private static void PlayCrack(Vector3 worldPosition, float massPerRound, float distance)
        {
            AudioClip clip = BulletCrackAssets.Clip;
            if (clip == null)
            {
                return;
            }

            AudioSource template = GetTemplate();
            // A little per-crack pitch variation so a rapid string of them
            // (an automatic weapon's whole burst passing close by) doesn't
            // sound like the exact same identical snap looping -- baked
            // onto the shared template right before this call, since
            // CopyAudioSourceSettings reads it fresh for every new one-shot.
            float caliberPitch = ComputeCaliberPitch(massPerRound);
            template.pitch = caliberPitch + Random.Range(-CrackPitchDeviation, CrackPitchDeviation);
            template.volume = ComputeCrackVolume(caliberPitch);
            // Half of the clip's own raw length, in real (unpitched) time --
            // ScheduleDelayedOneShot's truncation timer runs in real wall-
            // clock seconds regardless of playback pitch, so this cuts the
            // pitched-up clip short partway through its own natural runtime.
            float clipDurationSeconds = clip.length / 2f;
            SoundPropagation.ScheduleDelayedOneShot(
                template, clip, worldPosition,
                clipDurationSeconds: clipDurationSeconds, highpassCutoffHz: CrackHighpassCutoffHz);

            if (caliberPitch < ShakeCaliberPitchThreshold)
            {
                CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
                if (cam != null)
                {
                    float shakeIntensity = Mathf.Clamp01(1f - distance / CrackRadiusMeters);
                    cam.ShakeCamera(shakeIntensity, 0f);
                }
            }
        }
    }
}
