using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Fed by MushroomCloudDetonationFlashPatch, whenever an actual nuclear
    // detonation (not a regular explosion -- see that patch's own comment)
    // spawns within the camera's view. Tracks the boost as a peak value
    // plus a trigger timestamp rather than decrementing a counter every
    // frame -- the remaining boost at any moment is just computed on
    // demand from elapsed time, so nothing needs to tick this every frame.
    internal static class NightVisionDetonationFlash
    {
        // Locked in after live testing -- always on, no toggle.
        private const int SpeckBoost = 300;
        private const float TaperDuration = 20f;

        private static float _triggerTime = float.NegativeInfinity;
        private static float _peakBoost;

        internal static void TriggerFlash(Vector3 worldPosition)
        {
            if (!NightVisionEnhancementPatch.NightVisionActive)
            {
                return;
            }

            CameraStateManager cameraManager = SceneSingleton<CameraStateManager>.i;
            Camera camera = cameraManager != null ? cameraManager.mainCamera : null;
            if (camera == null)
            {
                return;
            }

            Vector3 viewportPoint = camera.WorldToViewportPoint(worldPosition);
            bool inFrontOfCamera = viewportPoint.z > 0f;
            bool withinViewport = viewportPoint.x >= 0f && viewportPoint.x <= 1f && viewportPoint.y >= 0f && viewportPoint.y <= 1f;
            if (!inFrontOfCamera || !withinViewport)
            {
                return;
            }

            _triggerTime = Time.unscaledTime;
            _peakBoost = SpeckBoost;
        }

        // Called every frame the sparkle overlay updates -- adds the
        // currently-remaining boost (exponentially decayed since the
        // trigger) on top of the normal configured Speck Count.
        internal static int GetEffectiveSpeckCount(int baseline)
        {
            if (_peakBoost <= 0f)
            {
                return baseline;
            }

            float elapsed = Time.unscaledTime - _triggerTime;
            float taper = Mathf.Max(0.01f, TaperDuration);
            float remaining = _peakBoost * Mathf.Exp(-elapsed / taper);
            if (remaining < 0.5f)
            {
                _peakBoost = 0f;
                return baseline;
            }

            return baseline + Mathf.RoundToInt(remaining);
        }
    }
}
