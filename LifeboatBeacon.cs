using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Blinking beacon light for spawned lifeboats (see LifeboatSpawnPatch).
    // Uses a MaterialPropertyBlock targeted at just the "Exterior light"
    // material slot rather than a per-instance material instance, so this
    // doesn't break renderer batching or duplicate materials when a single
    // sinking ship spawns up to 15 of these at once.
    internal sealed class LifeboatBeacon : MonoBehaviour
    {
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly Color OnColor = new Color(4f, 2.5f, 0.5f);

        private const float OnDuration = 0.1f;
        private const float Period = 1.5f;
        private const float MaxStartDelay = 5f;

        private Renderer _renderer;
        private int _materialIndex;
        private MaterialPropertyBlock _block;
        private float _phaseOffset;
        private float _activateTime;

        internal void Initialize(Renderer targetRenderer, int materialIndex)
        {
            _renderer = targetRenderer;
            _materialIndex = materialIndex;
            _block = new MaterialPropertyBlock();
            // Phase offset staggers the ongoing blink pattern once active;
            // activateTime is a one-time random delay before the beacon
            // starts blinking at all, so a batch of lifeboats from the same
            // ship doesn't all light up in perfect unison the instant they
            // spawn.
            _phaseOffset = Random.Range(0f, Period);
            _activateTime = Time.time + Random.Range(0f, MaxStartDelay);
        }

        private void Update()
        {
            bool isOn = Time.time >= _activateTime && (Time.time + _phaseOffset) % Period < OnDuration;
            _renderer.GetPropertyBlock(_block, _materialIndex);
            _block.SetColor(EmissionColorId, isOn ? OnColor : Color.black);
            _renderer.SetPropertyBlock(_block, _materialIndex);
        }
    }
}
