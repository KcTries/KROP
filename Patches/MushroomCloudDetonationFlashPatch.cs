using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // MushroomCloud is the actual nuclear detonation visual effect --
    // distinct from the separate Explosion/ExplosionAudio classes used for
    // regular ordnance -- confirmed via decompiled source: its yield field
    // (divided by 1,000,000 for blast scale) and mushroom-cloud-specific
    // rendering (Fireball, Cloud, CloudRing, Stem, Updraft) only make
    // sense for a nuclear-scale burst. Start() runs once, the moment the
    // effect spawns at the detonation's world position, making it a clean
    // "a nuke just went off here" hook.
    [HarmonyPatch(typeof(MushroomCloud), "Start")]
    internal static class MushroomCloudDetonationFlashPatch
    {
        private static void Postfix(MushroomCloud __instance)
        {
            NightVisionDetonationFlash.TriggerFlash(__instance.transform.position);
        }
    }
}
