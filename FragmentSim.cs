using System.Collections.Generic;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Lightweight kinetic-fragment simulation for the CRAM/SPAAG proximity
    // detonation change (see CramProximityFragmentPatch). Deliberately NOT
    // built on BulletSim.Bullet -- that class only stores GlobalPosition
    // (floating-origin aware) and has no back-reference from a Bullet to the
    // BulletSim/List<Bullet> that owns it, so a proximity-fuse Prefix has no
    // way to append new bullets to the right list. Positions are stored
    // Datum.origin-relative and converted to world space each tick -- a
    // floating-origin shift moves the whole scene 1000+ m, so a raw world
    // position held across one would put the fragment far from where it
    // really is. One shared list + one FixedUpdate driver keeps the
    // per-burst cost to a handful of struct entries and a few linecasts,
    // instead of instantiating real GameObjects per fragment.
    internal static class FragmentSim
    {
        private struct Fragment
        {
            public Vector3 OriginRelativePosition;
            public Vector3 Velocity;
            public float RemainingLifetime;
            public float PierceDamage;
            public PersistentID DealerID;
            public BulletSim.TracerView Tracer;
        }

        // Bright, HDR-boosted (values >1 push past the bloom threshold)
        // orange so a debug burst is easy to spot against sky or terrain --
        // avoids plain red since Gun's own default tracerColor is red too.
        private const float DebugTracerThickness = 1.2f;
        private static readonly Color DebugTracerColor = new Color(6f, 2f, 0f);

        private static readonly List<Fragment> _fragments = new List<Fragment>();
        private static bool _driverEnsured;

        internal static void EnsureDriver(GameObject pluginGameObject)
        {
            if (_driverEnsured)
            {
                return;
            }
            _driverEnsured = true;
            pluginGameObject.AddComponent<FragmentSimDriver>();
        }

        internal static void SpawnBurst(Vector3 position, Vector3 sourceVelocity, PersistentID dealerID, WeaponInfo sourceInfo)
        {
            if (sourceVelocity.sqrMagnitude < 1f)
            {
                // No meaningful direction of travel to cone the fragments
                // around (shouldn't normally happen for a moving round) --
                // skip rather than spawn fragments flying in a random
                // direction.
                return;
            }

            Vector3 forward = sourceVelocity.normalized;
            float speed = sourceVelocity.magnitude;
            float pierceDamage = sourceInfo.pierceDamage * CramFragmentationConfig.DamageFraction.Value;
            float coneAngle = CramFragmentationConfig.ConeAngleDegrees.Value;
            float lifetime = CramFragmentationConfig.LifetimeSeconds.Value;
            int count = CramFragmentationConfig.FragmentCount.Value;

            bool showTracers = CramFragmentationConfig.ShowDebugTracers.Value && !GameManager.IsHeadless;
            Vector3 originRelativePosition = Datum.origin.InverseTransformPoint(position);

            for (int i = 0; i < count; i++)
            {
                Vector3 direction = RandomConeDirection(forward, coneAngle);
                Vector3 fragmentVelocity = direction * speed;

                BulletSim.TracerView tracer = null;
                if (showTracers)
                {
                    tracer = BulletSim.GetTracer();
                    tracer.Setup(
                        position, Quaternion.LookRotation(direction),
                        new Vector3(DebugTracerThickness, DebugTracerThickness, speed * Time.fixedDeltaTime),
                        DebugTracerColor);
                }

                _fragments.Add(new Fragment
                {
                    OriginRelativePosition = originRelativePosition,
                    Velocity = fragmentVelocity,
                    RemainingLifetime = lifetime,
                    PierceDamage = pierceDamage,
                    DealerID = dealerID,
                    Tracer = tracer,
                });
            }
        }

        private static void ReleaseTracer(BulletSim.TracerView tracer)
        {
            if (tracer != null)
            {
                BulletSim.ReleaseTracer(tracer);
            }
        }

        private static Vector3 RandomConeDirection(Vector3 forward, float maxAngleDegrees)
        {
            Vector3 arbitraryAxis = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 perpendicular = Vector3.Cross(forward, arbitraryAxis).normalized;
            Quaternion tilt = Quaternion.AngleAxis(Random.Range(0f, maxAngleDegrees), perpendicular);
            Quaternion spin = Quaternion.AngleAxis(Random.Range(0f, 360f), forward);
            return spin * tilt * forward;
        }

        // Called on mission teardown (see LevelInfoCleanupPatch) so no
        // fragment from the mission that just ended carries over.
        internal static void ClearAll()
        {
            foreach (Fragment fragment in _fragments)
            {
                ReleaseTracer(fragment.Tracer);
            }
            _fragments.Clear();
        }

        internal static void Tick(float deltaTime)
        {
            if (_fragments.Count == 0 || Datum.origin == null)
            {
                return;
            }

            for (int i = _fragments.Count - 1; i >= 0; i--)
            {
                Fragment fragment = _fragments[i];
                fragment.RemainingLifetime -= deltaTime;
                if (fragment.RemainingLifetime <= 0f)
                {
                    ReleaseTracer(fragment.Tracer);
                    _fragments.RemoveAt(i);
                    continue;
                }

                Vector3 position = Datum.origin.TransformPoint(fragment.OriginRelativePosition);
                Vector3 nextVelocity = fragment.Velocity + Vector3.down * (9.81f * deltaTime);
                Vector3 nextPosition = position + nextVelocity * deltaTime;

                if (Physics.Linecast(position, nextPosition, out RaycastHit hit, ~(int)PhysicsLayers.ExclusionZonesMask))
                {
                    IDamageable damageable = hit.collider.gameObject.GetComponent<IDamageable>();
                    if (damageable != null)
                    {
                        damageable.TakeDamage(fragment.PierceDamage, 0f, 1f, 0f, 0f, fragment.DealerID);
                    }
                    ReleaseTracer(fragment.Tracer);
                    _fragments.RemoveAt(i);
                    continue;
                }

                if (fragment.Tracer != null)
                {
                    fragment.Tracer.transform.position = nextPosition;
                    if (nextVelocity.sqrMagnitude > 0.01f)
                    {
                        fragment.Tracer.transform.rotation = Quaternion.LookRotation(nextVelocity);
                    }
                    fragment.Tracer.transform.localScale = new Vector3(
                        DebugTracerThickness, DebugTracerThickness, nextVelocity.magnitude * deltaTime);
                }

                fragment.OriginRelativePosition = Datum.origin.InverseTransformPoint(nextPosition);
                fragment.Velocity = nextVelocity;
                _fragments[i] = fragment;
            }
        }
    }

    internal sealed class FragmentSimDriver : MonoBehaviour
    {
        private void FixedUpdate()
        {
            FragmentSim.Tick(Time.fixedDeltaTime);
        }
    }
}
