using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // ParticleEffectManager.PrefabEffect.Play() is the single choke point
    // for every bullet impact effect (ground/armor/water/self-destruct --
    // see BulletSim.cs's TrajectoryTrace, which calls it for all four via
    // Gun.ImpactEffectsPrefabs) as well as any other pooled prefab effect
    // in the game that carries its own AudioSource. The visual burst (the
    // pooled GameObject's transform/particle systems) stays instant, same
    // as a muzzle flash -- only the impact's sound gets delayed.
    //
    // This PrefabEffect instance is POOLED and reused for many unrelated
    // impacts over its lifetime, so its own AudioSource can't be reused as
    // a delayed-playback target the way SoundPropagation.ScheduleDelayedOneShot
    // already handles for gunfire -- it clones a disposable temp AudioSource
    // per event instead, which sidesteps that pooling problem for free.
    [HarmonyPatch(typeof(ParticleEffectManager.PrefabEffect), "Play")]
    internal static class ImpactEffectPlayPatch
    {
        // Performance bug, found and fixed via real [PerfSummary] log data
        // (pendingOneShots climbing into the thousands, Tick() cost rising
        // from ~0.001ms to 20+ms, during heavy sustained automatic gunfire)
        // -- this is the exact same uncapped-object-creation-storm root
        // cause BulletCrackPatch's own crack triggering already hit and
        // fixed (a CIWS-style rotary cannon logging ~2700 qualifying rounds
        // in 10 seconds), just on the impact side instead of the near-miss
        // crack side, and never caught at the time since this file wasn't
        // touched by that investigation. Every bullet impact (ground/armor/
        // water/self-destruct, from every gun in the game) was
        // unconditionally creating its own brand-new GameObject+AudioSource
        // +2 filter components with zero rate limiting -- a rotary cannon
        // or CIWS firing hundreds of rounds/second into water or a target
        // could schedule hundreds of these per second, and several firing
        // at once (a missile-swarm engagement, say) compounds directly.
        // Capped the same way, at a looser rate than cracks since impact
        // sound is more central to combat feedback than the subtler crack
        // effect -- a dense stream of impacts also physically blurs into
        // one continuous sound in reality anyway, same reasoning as the
        // crack cap. The visual effect (particles) is completely unaffected
        // either way; only the delayed-sound scheduling is skipped when
        // throttled.
        private const float MinImpactIntervalSeconds = 1f / 30f;
        private static float _lastGlobalImpactTime = float.NegativeInfinity;

        private static bool Prefix(ParticleEffectManager.PrefabEffect __instance, Vector3 position, Quaternion rotation)
        {
            if (__instance.gameObject == null)
            {
                return false;
            }

            __instance.gameObject.transform.position = position;
            __instance.gameObject.transform.rotation = rotation;
            __instance.gameObject.SetActive(true);

            if (__instance.source != null && !__instance.source.isPlaying)
            {
                // Mutates the pooled source's own pitch, matching the
                // original's behavior -- harmless since it's set fresh on
                // every Play() call regardless.
                __instance.source.pitch = Random.Range(0.9f, 1.1f);
                float now = Time.timeSinceLevelLoad;
                if (now - _lastGlobalImpactTime >= MinImpactIntervalSeconds)
                {
                    _lastGlobalImpactTime = now;
                    // A shell hitting your own airframe should sound
                    // muffled by however much fuselage stands between the
                    // hit and the cockpit -- a canopy strike is basically
                    // right next to your head, a tail strike isn't. Only
                    // resolved (and only matters) for hits confirmed to be
                    // on the local player's own aircraft; everything else
                    // keeps the normal distance/cockpit cutoff untouched.
                    float? ownHitDistance = SoundPropagation.TryGetOwnAirframeHitDistance(position, out float distanceToCockpit)
                        ? (float?)distanceToCockpit
                        : null;
                    SoundPropagation.ScheduleDelayedOneShot(
                        __instance.source, __instance.source.clip, position,
                        ownHitDistanceToCockpitMeters: ownHitDistance);
                }
            }

            for (int i = 0; i < __instance.systems.Length; i++)
            {
                __instance.systems[i].Play(withChildren: true);
            }

            return false;
        }
    }
}
