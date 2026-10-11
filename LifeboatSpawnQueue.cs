using System.Collections.Generic;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Staggers lifeboat spawns over a random per-boat delay (see
    // LifeboatSpawnPatch) instead of every boat from a sinking ship
    // appearing in the same instant. Same pending-list + driver shape as
    // FragmentSim's own Tick() pattern elsewhere in this mod.
    internal static class LifeboatSpawnQueue
    {
        private const int BeaconMaterialIndex = 5;

        // Position is Datum.origin-relative: a spawn waits up to 8s, and a
        // floating-origin shift in that window (every ~1km of camera
        // travel) would otherwise drop the boat 1000+ m from where it was
        // meant to be.
        private struct PendingSpawn
        {
            public Vector3 OriginRelativePosition;
            public Quaternion Rotation;
            public float SpawnTime;
        }

        private static readonly List<PendingSpawn> _pending = new List<PendingSpawn>();
        private static bool _driverEnsured;

        internal static void EnsureDriver(GameObject pluginGameObject)
        {
            if (_driverEnsured)
            {
                return;
            }
            _driverEnsured = true;
            pluginGameObject.AddComponent<LifeboatSpawnQueueDriver>();
        }

        internal static void Schedule(Vector3 position, Quaternion rotation, float delaySeconds)
        {
            _pending.Add(new PendingSpawn
            {
                OriginRelativePosition = Datum.origin.InverseTransformPoint(position),
                Rotation = rotation,
                SpawnTime = Time.time + delaySeconds,
            });
        }

        // Called on mission teardown (see LevelInfoCleanupPatch) so a boat
        // still waiting to spawn can't appear in the next mission.
        internal static void ClearAll()
        {
            _pending.Clear();
        }

        internal static void Tick()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingSpawn spawn = _pending[i];
                if (Time.time < spawn.SpawnTime)
                {
                    continue;
                }

                _pending.RemoveAt(i);
                if (Datum.origin != null)
                {
                    SpawnNow(Datum.origin.TransformPoint(spawn.OriginRelativePosition), spawn.Rotation);
                }
            }
        }

        private static void SpawnNow(Vector3 position, Quaternion rotation)
        {
            GameObject lifeboat = Object.Instantiate(LifeboatAssets.LifeboatPrefab, position, rotation, Datum.origin);
            lifeboat.AddComponent<LifeboatBob>();
            lifeboat.AddComponent<LifeboatLifetime>();
            Renderer renderer = lifeboat.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                lifeboat.AddComponent<LifeboatBeacon>().Initialize(renderer, BeaconMaterialIndex);
            }
        }
    }

    internal sealed class LifeboatSpawnQueueDriver : MonoBehaviour
    {
        private void Update()
        {
            LifeboatSpawnQueue.Tick();
        }
    }
}
