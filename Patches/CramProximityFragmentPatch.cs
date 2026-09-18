using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Both of TrajectoryTrace's proximity-fuse detonation paths (the
    // reliability-roll "dud gives up out of range" case and the actual
    // intercept-solution case) call DamageEffects.BlastFrag with the exact
    // same argument pattern. A direct hit (the earlier Linecast/IDamageable
    // branch, a few lines up) never calls BlastFrag at all -- it applies
    // TakeDamage straight away -- so redirecting every BlastFrag call inside
    // this method already scopes the change to proximity detonations only,
    // with no risk of touching direct-hit damage.
    //
    // BlastFrag's own parameters (blastYield, position, dealerID, missileID)
    // don't include the bullet's direction of travel or its owning Unit, so
    // a Prefix captures both from TrajectoryTrace's real parameters/fields
    // into static fields just before the original body runs. Bullets are
    // simulated one at a time, synchronously, from BulletSim.FixedUpdate's
    // single-threaded loop, so there's no reentrancy risk between the
    // Prefix's write and the transpiled call's read a few lines later in the
    // same invocation.
    [HarmonyPatch(typeof(BulletSim.Bullet), "TrajectoryTrace")]
    internal static class CramProximityFragmentPatch
    {
        private static readonly HashSet<string> FragmentationJsonKeys = new HashSet<string>
        {
            "SPAAG1",
        };

        // Targets worth the precision-aimed fragment cone: any aircraft
        // (checked by C# type below -- this game uses one Aircraft class
        // for both fixed-wing and helicopters, so this also automatically
        // covers modded aircraft like Aryx_PropAttacker1 with no per-plane
        // list to maintain), plus bombs strictly bigger than the PAB-125
        // (125kg, jsonKey "bomb_125_1"), both cruise missiles, and both
        // anti-ship missiles. Bomb weight doesn't track physical size
        // cleanly (PAB-80LR is physically longer than the PAB-125 but a
        // lighter, smaller-yield 80kg weapon) so those are an explicit list
        // pulled from the unit definitions dump's real unitName/description
        // weights, not a size/radius heuristic. AAMs, rockets, and other
        // small/fast/erratic guided munitions (plus the PAB-125/PAB-125HD
        // themselves and the lighter PAB-80LR) still fall through to full
        // stock vanilla behavior -- vanilla's own wider blast radius
        // already handles those fine.
        private static readonly HashSet<string> FragmentationTargetJsonKeys = new HashSet<string>
        {
            "bomb_250_glide",   // PAB-250LR, 250kg
            "bomb_250_1",       // PAB-250, 250kg
            "bomb500",          // GPO-500, 500kg
            "bomb_500_glide",   // GBM-500LR, 500kg
            "bomb_cluster_1",   // CBO-400, 400kg
            "bomb_demolition",  // Demolition Bomb, heavy thermobaric
            "bomb_penetrator1", // GPO-2P Auger, bunker-buster
            "nuclearBomb1",     // GPO-N, 1.5kt
            "nuclearBomb1_strategic", // GPO-N, 250kt
            "CruiseMissile1",
            "CruiseMissile20kt",
            "AShM1",
            "AShM2",
        };

        private static readonly MethodInfo BlastFragMethod =
            AccessTools.Method(typeof(DamageEffects), nameof(DamageEffects.BlastFrag));
        private static readonly MethodInfo BlastFragReplacement =
            AccessTools.Method(typeof(CramProximityFragmentPatch), nameof(BlastFragOrFragment));

        private static Unit _currentOwner;
        private static Vector3 _currentVelocity;
        private static WeaponInfo _currentInfo;
        private static bool _currentHasTargetPos;
        private static GlobalPosition _currentTargetPos;
        private static bool _currentTargetIsEligible;

        // Temporary diagnostic logging for the "fragments only appear at
        // impact instead of pre-detonating" report -- confirms which path
        // actually detonated each round (the widened early check here, or
        // vanilla's own tight one-tick-lookahead trigger via the transpiled
        // BlastFrag call below) and how far from the target it happened.
        // Dedupes the "entered proximity band" log to once per bullet (each
        // Bullet is a fresh instance per shot, never reused). A
        // ConditionalWeakTable, not a plain HashSet -- bullets are created
        // constantly (a single CIWS-style rotary cannon burst measured
        // ~2700 qualifying rounds in 10 seconds elsewhere in this mod), and
        // a normal collection keyed by Bullet would keep every one of them
        // alive forever just by being logged once. Same fix already applied
        // to BulletCrackPatch's own CrackedBullets for the identical reason.
        private static readonly ConditionalWeakTable<BulletSim.Bullet, object> _loggedEligible =
            new ConditionalWeakTable<BulletSim.Bullet, object>();

        // Widens vanilla's own closest-approach proximity check from a
        // single physics tick of lookahead to DetonateLeadSeconds, for
        // CRAM/SPAAG only -- same geometry test vanilla runs (is the target
        // now "behind" the round relative to its direction of travel), just
        // projected further ahead so the round detonates that much earlier
        // along its flight path instead of right at the moment of closest
        // approach. When this doesn't fire (feature off, wrong unit, round
        // not yet at its early trigger point), the original method runs
        // untouched and its own real-time trigger still redirects through
        // the transpiled BlastFrag call below as a fallback -- so a missed
        // early detonation still becomes a fragment burst, just on time
        // instead of early.
        private static bool Prefix(
            BulletSim.Bullet __instance, WeaponInfo info, Unit owner,
            bool hasTargetPos, GlobalPosition targetPos, ParticleEffectManager.PrefabEffect[] impactEffects)
        {
            _currentOwner = owner;
            _currentVelocity = __instance.velocity;
            _currentInfo = info;
            _currentHasTargetPos = hasTargetPos;
            _currentTargetPos = targetPos;

            if (!CramFragmentationConfig.Enabled.Value || owner == null || owner.definition == null
                || !FragmentationJsonKeys.Contains(owner.definition.jsonKey))
            {
                // Every non-CRAM/SPAAG bullet in the match (i.e. nearly all
                // of them) bails out right here -- _currentTargetIsEligible
                // is deliberately left unset/stale in this branch rather
                // than computed above, since BlastFragOrFragment's own read
                // of it is always short-circuited first by this exact same
                // owner/FragmentationJsonKeys check on the bullet's real,
                // current owner, so a stale value from some earlier CRAM
                // round is never actually consulted here. Previously this
                // ran a type check plus a HashSet lookup against
                // __instance.target on every single bullet fired by every
                // gun in the game, CRAM or not.
                return true;
            }

            _currentTargetIsEligible = __instance.target != null && __instance.target.definition != null
                && (__instance.target is Aircraft || FragmentationTargetJsonKeys.Contains(__instance.target.definition.jsonKey));

            float leadSeconds = CramFragmentationConfig.DetonateLeadSeconds.Value;
            bool nearTarget = __instance.proximityFuse && __instance.target != null && hasTargetPos
                && FastMath.InRange(targetPos, __instance.position, 100f);
            bool inProximityBand = nearTarget && _currentTargetIsEligible;

            if (nearTarget && VerboseLoggingConfig.Enabled.Value && !_loggedEligible.TryGetValue(__instance, out _))
            {
                _loggedEligible.Add(__instance, null);
                float distanceNow = FastMath.Distance(targetPos, __instance.position);
                SoundPropagation.Log.LogInfo(
                    $"[CramFragDiag] '{owner.definition.jsonKey}' round entered proximity band at t={Time.timeSinceLevelLoad:F2}, "
                    + $"distanceToTarget={distanceNow:F1}m, speed={__instance.velocity.magnitude:F0}m/s, leadSeconds={leadSeconds:F2}, "
                    + $"targetKey={__instance.target.definition.jsonKey}, eligible={_currentTargetIsEligible}");
            }

            if (leadSeconds <= 0f || !inProximityBand)
            {
                return true;
            }

            Vector3 velocity = __instance.velocity;
            GlobalPosition predictedPosition = __instance.position + velocity * leadSeconds;
            Vector3 towardTarget = targetPos - predictedPosition;
            if (Vector3.Dot(velocity, towardTarget) >= 0f)
            {
                return true;
            }

            // Detonate right where the round actually is, NOT projected
            // onto the target's position like vanilla's own (late) trigger
            // does -- that projection is only unnoticeable in vanilla
            // because its one-tick lookahead means "current position" and
            // "closest approach to target" are already almost the same
            // point. Triggering up to leadSeconds early means they can be
            // a real distance apart; using the round's own position keeps
            // the fragment burst visually anchored to where its tracer
            // actually disappears, with the fragments then covering the
            // remaining distance to the target themselves.
            GlobalPosition detonationPosition = __instance.position;

            if (VerboseLoggingConfig.Enabled.Value)
            {
                SoundPropagation.Log.LogInfo(
                    $"[CramFragDiag] EARLY detonation for '{owner.definition.jsonKey}' at t={Time.timeSinceLevelLoad:F2}, "
                    + $"standoffDistance={FastMath.Distance(detonationPosition, targetPos):F1}m");
            }

            FragmentSim.SpawnBurst(detonationPosition.ToLocalPosition(), velocity, owner.persistentID, info);
            impactEffects[3]?.Play(detonationPosition.ToLocalPosition(), Quaternion.LookRotation(velocity));
            __instance.active = false;
            return false;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Transpilers.MethodReplacer(instructions, BlastFragMethod, BlastFragReplacement);
        }

        private static void BlastFragOrFragment(float blastYield, Vector3 blastPosition, PersistentID dealerID, PersistentID missileID)
        {
            Unit owner = _currentOwner;
            if (CramFragmentationConfig.Enabled.Value && owner != null && owner.definition != null
                && FragmentationJsonKeys.Contains(owner.definition.jsonKey) && _currentTargetIsEligible)
            {
                if (VerboseLoggingConfig.Enabled.Value)
                {
                    string standoff = _currentHasTargetPos
                        ? $"{FastMath.Distance(blastPosition.ToGlobalPosition(), _currentTargetPos):F1}m"
                        : "unknown (no target pos)";
                    SoundPropagation.Log.LogInfo(
                        $"[CramFragDiag] FALLBACK (vanilla real-time trigger) detonation for '{owner.definition.jsonKey}' "
                        + $"at t={Time.timeSinceLevelLoad:F2}, standoffDistance={standoff}");
                }

                FragmentSim.SpawnBurst(blastPosition, _currentVelocity, dealerID, _currentInfo);
                return;
            }

            DamageEffects.BlastFrag(blastYield, blastPosition, dealerID, missileID);
        }
    }
}
