using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Ship.UnitDisabled is a Mirage SyncVar hook (on the "disabled" field)
    // that fires on every machine observing the sync, not just the server --
    // the ship starts flooding here (see the vanilla method's own
    // part.Flood() calls) before eventually despawning. That makes it the
    // right moment for a sinking ship to leave behind lifeboats. Purely
    // cosmetic and client-side by design (each machine spawns its own,
    // independently), so no networking -- matches how the game's own
    // one-shot visual effects (impact sparks, tracers) work.
    [HarmonyPatch(typeof(Ship), "UnitDisabled")]
    internal static class LifeboatSpawnPatch
    {
        private const float MinRadius = 25f;
        private const float MaxRadius = 150f;
        private const float CrewPerLifeboat = 15f;
        // Not exposed in config -- just enough to avoid every boat from a
        // sinking ship popping in on the same frame.
        private const float MaxSpawnDelaySeconds = 8f;
        // The model's own pivot sits above its actual waterline (roughly
        // mid-hull, not at the bottom of the tube), so spawning it flush at
        // sea level sinks it in -- this raises the whole boat to compensate.
        // Tune by eye in-game; no need to touch the model itself for this.
        private const float WaterlineOffset = 0.15f;
        // The model came out of the Blender->FBX export tipped up on its
        // side (a Z-up/Y-up axis mismatch) rather than lying flat -- rather
        // than re-export, this corrects it at spawn time. Tune by eye; the
        // random Y-facing spin below is applied on top of this, in world
        // space, so it still spins around a properly "flat" boat.
        private static readonly Quaternion ModelCorrection = Quaternion.Euler(-90f, 0f, 0f);

        // Guards against spawning a second full batch of lifeboats if this
        // SyncVar hook is ever re-invoked with newState=true for a ship that
        // already sank and already got its boats (e.g. a late state resync)
        // -- a ConditionalWeakTable so tracking a sunk ship doesn't keep it
        // referenced any longer than it otherwise would be.
        private static readonly ConditionalWeakTable<Ship, object> _spawnedFor = new ConditionalWeakTable<Ship, object>();

        private static void Postfix(Ship __instance, bool oldState, bool newState)
        {
            if (!newState || !LifeboatConfig.Enabled.Value || GameManager.IsHeadless || LifeboatAssets.LifeboatPrefab == null)
            {
                return;
            }
            if (_spawnedFor.TryGetValue(__instance, out _))
            {
                return;
            }
            _spawnedFor.Add(__instance, null);

            int boatCount = Mathf.Clamp(
                Mathf.CeilToInt(__instance.definition.manpower / CrewPerLifeboat), 1, LifeboatConfig.MaxLifeboatsPerShip.Value);
            Vector3 shipPosition = __instance.transform.position;

            for (int i = 0; i < boatCount; i++)
            {
                float angle = Random.Range(0f, Mathf.PI * 2f);
                // sqrt of a uniform range over the squared radii gives an
                // even spread across the donut's area -- a plain uniform
                // pick on the radius itself would bunch boats up near the
                // inner edge, since the ring of area at a given radius
                // grows with r.
                float radius = Mathf.Sqrt(Random.Range(MinRadius * MinRadius, MaxRadius * MaxRadius));
                Vector3 spawnPosition = new Vector3(
                    shipPosition.x + Mathf.Cos(angle) * radius,
                    Datum.LocalSeaY + WaterlineOffset,
                    shipPosition.z + Mathf.Sin(angle) * radius);
                Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * ModelCorrection;

                float delaySeconds = Random.Range(0f, MaxSpawnDelaySeconds);
                LifeboatSpawnQueue.Schedule(spawnPosition, rotation, delaySeconds);
            }
        }
    }
}
