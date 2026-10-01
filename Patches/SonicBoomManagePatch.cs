using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // SonicBoomManager.ManagedSonicBoom is a PRIVATE class nested inside a
    // PUBLIC static class -- its own Mach-cone detection/mute-state method
    // (Manage()) is where the actual boom's source.PlayOneShot(...) call
    // lives, but the type itself can't be named with typeof() from outside,
    // so this can't use the normal [HarmonyPatch(Type, string)] attribute
    // like the rest of this mod's patches. Instead it's resolved via
    // AccessTools.Inner/Method at runtime and patched manually from
    // Plugin.Awake(), same trick as reaching any other private nested type.
    //
    // As with Canopy's jettison sound, Manage() also handles unrelated Mach
    // cone geometry and SetSoundsMuted() state that shouldn't be touched --
    // a transpiler swaps out just the PlayOneShot call, leaving everything
    // else in the method exactly as the game wrote it.
    internal static class SonicBoomManagePatch
    {
        private static readonly MethodInfo PlayOneShotMethod =
            AccessTools.Method(typeof(AudioSource), "PlayOneShot", new[] { typeof(AudioClip), typeof(float) });
        private static readonly MethodInfo PlayOneShotDelayedMethod =
            AccessTools.Method(typeof(SonicBoomManagePatch), nameof(PlayOneShotDelayed));

        internal static void ApplyManualPatch(Harmony harmony)
        {
            Type managedSonicBoomType = AccessTools.Inner(typeof(SonicBoomManager), "ManagedSonicBoom");
            MethodInfo manageMethod = AccessTools.Method(managedSonicBoomType, "Manage");
            harmony.Patch(manageMethod, transpiler: new HarmonyMethod(typeof(SonicBoomManagePatch), nameof(Transpiler)));

            // ManagedSonicBoom's constructor is where vanilla sets up the
            // boom's own AudioSource (Unit-generic -- SonicBoomManager.
            // RegisterUnit(Unit) already works for a Missile as-is, since
            // Missile : Unit, so no new Mach-cone detection is needed to
            // give missiles their own boom, just registering them; see
            // MissileMotorPatch.ActivatePostfix). Tagging the missile case
            // here, once, is cheaper than re-deriving "was this boom's unit
            // a missile" every time it plays.
            ConstructorInfo constructor = AccessTools.Constructor(managedSonicBoomType, new[] { typeof(Unit) });
            harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(SonicBoomManagePatch), nameof(ConstructorPostfix)));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Transpilers.MethodReplacer(instructions, PlayOneShotMethod, PlayOneShotDelayedMethod);
        }

        // A missile is a fraction of an aircraft's length, so its N-wave is
        // a short, high-frequency "crack" rather than a deep boom -- pitching
        // the shared sonicBoom clip up approximates that without a separate
        // audio asset. Set once here, on the boom's own AudioSource, rather
        // than passed around: CopyAudioSourceSettings already carries pitch
        // through to every delayed clone downstream. Originally 1.8 -- scaled
        // back down to 1.25 as too aggressive once real missile-boom samples
        // (SonicBoomAssets) replaced the shared aircraft clip below.
        private const float MissileBoomPitch = 1.25f;

        private static void ConstructorPostfix(Unit supersonicUnit, AudioSource ___source)
        {
            if (supersonicUnit is Missile)
            {
                ___source.pitch = MissileBoomPitch;
            }
        }

        // A real sonic boom is actually two shocks -- a bow shock off the
        // nose and a trailing shock off the tail, heard as a quick
        // double-crack rather than a single bang. Originally approximated by
        // scheduling the same clip twice in a row -- dropped once real boom
        // samples (SonicBoomAssets) replaced the shared aircraft clip, since
        // it didn't read as a clean double-crack with those samples the way
        // it did with vanilla's own single clip. Every boom (aircraft near,
        // aircraft far, missile) is a single full-length shot now.
        private const float MissileBoomClipDurationSeconds = 0.35f;

        // [BoomDiag] confirmed a single missile pass can trip vanilla's own
        // Mach-cone trigger twice within a couple of seconds: once via the
        // ordinary directional angle test while still far out, and again
        // (or first) via ManagedSonicBoom's short-range override that always
        // treats a unit as "in cone" once it's within its own maxRadius. An
        // aircraft rarely double-triggers this close together -- its real
        // separate supersonic passes are seconds to minutes apart -- but a
        // missile closing on or flashing past the camera can satisfy both
        // paths in quick succession, and since every trigger also plays our
        // own double-crack, that read as up to four cracks from one shot.
        // A short per-unit cooldown, keyed on the boom's own AudioSource
        // (stable for that unit's whole life -- one ManagedSonicBoom/source
        // per RegisterUnit call), suppresses the redundant retrigger without
        // touching genuinely separate later passes. A ConditionalWeakTable,
        // not a plain Dictionary -- every supersonic unit ever registered
        // (especially every missile fired) added an entry that was never
        // removed, even on mission end, leaking the destroyed AudioSource/
        // GameObject for the rest of the process lifetime. TValue must be a
        // reference type for ConditionalWeakTable, so the timestamp is boxed
        // in a small mutable class instead of a bare float.
        private const float RetriggerCooldownSeconds = 1.5f;

        private class BoomTimeBox
        {
            public float Time;
        }

        private static readonly ConditionalWeakTable<AudioSource, BoomTimeBox> _lastBoomTime =
            new ConditionalWeakTable<AudioSource, BoomTimeBox>();

        // Signature matches AudioSource.PlayOneShot(AudioClip, float) exactly
        // (instance -> static taking the instance as the first arg) so
        // MethodReplacer can swap the call in place.
        private static void PlayOneShotDelayed(AudioSource source, AudioClip clip, float volumeScale)
        {
            float now = Time.timeSinceLevelLoad;
            if (_lastBoomTime.TryGetValue(source, out BoomTimeBox lastBoom) && now - lastBoom.Time < RetriggerCooldownSeconds)
            {
                if (VerboseLoggingConfig.Enabled.Value)
                {
                    SoundPropagation.Log.LogInfo(
                        $"[BoomDiag] Suppressed retrigger for '{source.GetInstanceID()}' at t={now:F2} "
                        + $"(last boomed {now - lastBoom.Time:F2}s ago)");
                }
                return;
            }
            if (lastBoom != null)
            {
                lastBoom.Time = now;
            }
            else
            {
                _lastBoomTime.Add(source, new BoomTimeBox { Time = now });
            }

            Vector3 position = source.transform.position;
            bool isMissileBoom = source.pitch > 1f;
            CameraStateManager cam = SceneSingleton<CameraStateManager>.i;
            float distanceToListener = cam != null ? Vector3.Distance(position, cam.transform.position) : 0f;

            if (VerboseLoggingConfig.Enabled.Value)
            {
                SoundPropagation.Log.LogInfo(
                    $"[BoomDiag] PlayOneShot for '{source.GetInstanceID()}' at t={now:F2} "
                    + $"isMissile={isMissileBoom} pitch={source.pitch:F2} "
                    + $"position={position} distanceToListener={distanceToListener:F1}");
            }

            // clip (vanilla's own shared sonicBoom asset) is only ever a
            // fallback now -- SonicBoomAssets supplies the real custom clip,
            // randomized per pool so the same exact sample never repeats
            // twice in a row.
            if (isMissileBoom)
            {
                AudioClip missileClip = SonicBoomAssets.GetRandomMissile() ?? clip;
                SoundPropagation.ScheduleDelayedOneShot(
                    source, missileClip, position, clipDurationSeconds: MissileBoomClipDurationSeconds);
            }
            else if (distanceToListener >= SonicBoomAssets.FarDistanceThresholdMeters)
            {
                AudioClip farClip = SonicBoomAssets.GetRandomFar() ?? clip;
                // Overrides the temp source's own rolloff range so a far
                // boom always plays as loud as if heard from exactly
                // FarDistanceThresholdMeters, fading out toward
                // FarMaxAudibleDistanceMeters -- not attenuated down using
                // whatever the real (often much larger) distance is.
                SoundPropagation.ScheduleDelayedOneShot(
                    source, farClip, position,
                    minDistanceOverride: SonicBoomAssets.FarDistanceThresholdMeters,
                    maxDistanceOverride: SonicBoomAssets.FarMaxAudibleDistanceMeters);
            }
            else
            {
                AudioClip nearClip = SonicBoomAssets.GetRandomNear() ?? clip;
                SoundPropagation.ScheduleDelayedOneShot(source, nearClip, position);
            }
        }
    }
}
