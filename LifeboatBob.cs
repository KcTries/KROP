using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Purely cosmetic sine-wave bob for spawned lifeboats (see
    // LifeboatSpawnPatch) -- the game's water is a flat plane with no
    // queryable per-point wave height, so a real buoyancy simulation
    // wouldn't actually produce any motion. This fakes a gentle up/down
    // float instead, with a random phase per boat so a batch spawned
    // together doesn't bob in unison.
    internal sealed class LifeboatBob : MonoBehaviour
    {
        private const float Amplitude = 0.08f;
        private const float Period = 4f;

        private float _baseLocalY;
        private float _phaseOffset;

        // Local, not world, position -- the lifeboat is parented under
        // Datum.origin (see LifeboatSpawnQueue.SpawnNow), and the game
        // re-centers that origin periodically as the camera roams far from
        // it (FloatingOrigin.OriginShift). Caching a raw WORLD Y here and
        // reapplying it every frame (the original bug) fights that: once an
        // origin shift happened while the boat wasn't being watched, this
        // would permanently pin it to its stale pre-shift world altitude
        // forever after, while the correctly-shifted water surface moved on
        // without it -- reported as boats floating ~100m in the air after
        // the camera returned. Local space is origin-shift-safe: Unity
        // re-derives world position from local position * parent transform
        // automatically, so an origin shift alone (with no change to this
        // boat's own offset from its parent) just naturally keeps working.
        private void Awake()
        {
            _baseLocalY = transform.localPosition.y;
            _phaseOffset = Random.Range(0f, Mathf.PI * 2f);
        }

        private void Update()
        {
            Vector3 localPosition = transform.localPosition;
            localPosition.y = _baseLocalY + Mathf.Sin(Time.time / Period * Mathf.PI * 2f + _phaseOffset) * Amplitude;
            transform.localPosition = localPosition;
        }
    }
}
