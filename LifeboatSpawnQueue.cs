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

        private struct PendingSpawn
        {
            public Vector3 Position;
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
                Position = position,
                Rotation = rotation,
                SpawnTime = Time.time + delaySeconds,
            });
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

                SpawnNow(spawn.Position, spawn.Rotation);
                _pending.RemoveAt(i);
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
