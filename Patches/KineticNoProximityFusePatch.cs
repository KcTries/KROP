using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Vanilla assigns a bullet's proximityFuse purely from the TARGET's
    // armor tier (BulletSim.AddBullet: "target.definition.armorTier < 2f"),
    // completely independent of whether the firing weapon has any blast
    // damage to actually detonate. A genuinely kinetic round (blastDamage=0,
    // e.g. a railgun penetrator) fired at a soft/lightly-armored target
    // still gets proximityFuse=true, which sends it through
    // TrajectoryTrace's proximity-detonation branch and calls
    // DamageEffects.BlastFrag(0, ...) at closest approach -- a harmless
    // (zero damage either way) but behaviorally wrong "fizzle detonation"
    // that ends the round's flight early instead of letting it continue
    // straight through like a real kinetic penetrator would.
    //
    // The Bullet constructor already receives `explosive` (=
    // weaponInfo.blastDamage > 0f) as its own parameter, so this Postfix
    // can cleanly override proximityFuse right there -- general fix, not
    // scoped to any one weapon by name, so it also covers any other
    // zero-blast-damage weapon in the game the same way.
    [HarmonyPatch(typeof(BulletSim.Bullet), MethodType.Constructor,
        typeof(Vector3), typeof(BulletSim.TracerView), typeof(float), typeof(bool), typeof(bool), typeof(Unit))]
    internal static class KineticNoProximityFusePatch
    {
        private static void Postfix(BulletSim.Bullet __instance, bool explosive)
        {
            if (!explosive)
            {
                __instance.proximityFuse = false;
            }
        }
    }
}
