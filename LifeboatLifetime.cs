using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Despawns a lifeboat a configurable number of minutes after it spawns.
    internal sealed class LifeboatLifetime : MonoBehaviour
    {
        private void Awake()
        {
            Object.Destroy(gameObject, LifeboatConfig.LifetimeMinutes.Value * 60f);
        }
    }
}
