using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace QOL_Realisim_Fixes.Patches
{
    // Measures actual rendered-frame brightness via a cheap downsample plus
    // an ASYNC GPU readback (AsyncGPUReadback.Request never stalls waiting
    // on the GPU, unlike Texture2D.ReadPixels/ScreenCapture, which would be
    // a real frame-time cost if done every frame), then drives postExposure
    // toward keeping that measurement within [MinBrightness, MaxBrightness]
    // -- unlike vanilla's ambient-light-only approach, this reacts to
    // whatever is actually bright on screen (a flare, an explosion, looking
    // near the sun) regardless of the game world's own time-of-day
    // lighting value.
    //
    // This is the most technically involved piece of this mod's NVG work --
    // grabbing a camera's just-rendered frame from outside the render
    // pipeline (no custom ScriptableRendererFeature registered) via a
    // CommandBuffer Blit from BuiltinRenderTextureType.CameraTarget. Worth
    // watching closely after deploying: confirm there's no stutter/frame
    // time regression, and that exposure visibly responds (darkens) when
    // looking at something bright through NVG.
    internal static class NightVisionAutoGain
    {
        // 16x16 rather than 8x8 -- a small, intense light source (a
        // flare, a star) can be washed out to near-nothing by the GPU's
        // own bilinear downsampling at very low resolutions, before this
        // mod's own code ever sees the pixel data.
        private const int DownsampleSize = 16;

        private static RenderTexture _downsampleTexture;
        private static bool _readbackPending;
        private static float _measuredBrightness = 0.3f;
        private static float _currentExposure;
        private static float _nextSampleTime;
        private static bool _subscribed;

        internal static void EnsureSubscribed()
        {
            if (_subscribed)
            {
                return;
            }
            _subscribed = true;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        // Called every frame NVG is active, from the Harmony Prefix that
        // replaces UpdateGain. MoveTowards (a fixed stops-per-second ramp,
        // not an exponential lerp) keeps Compensation Speed's units
        // literally "exposure stops per second", matching its description.
        internal static void Apply(ColorAdjustments colorAdjustments)
        {
            float target = Mathf.Clamp(_measuredBrightness, AutoGainConfig.MinBrightness, AutoGainConfig.MaxBrightness);
            float error = _measuredBrightness - target;
            // Negative error (too dark) -> positive exposure (brighten).
            // Positive error (too bright) -> negative exposure (darken).
            float desiredExposure = Mathf.Clamp(-error * AutoGainConfig.Sensitivity, -8f, 8f);

            _currentExposure = Mathf.MoveTowards(
                _currentExposure,
                desiredExposure,
                AutoGainConfig.CompensationSpeed * Time.unscaledDeltaTime);

            colorAdjustments.postExposure.overrideState = true;
            colorAdjustments.postExposure.value = _currentExposure;
        }

        private static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!AutoGainConfig.Enabled.Value || !NightVisionEnhancementPatch.NightVisionActive)
            {
                return;
            }
            if (_readbackPending || Time.unscaledTime < _nextSampleTime)
            {
                return;
            }
            CameraStateManager cameraManager = SceneSingleton<CameraStateManager>.i;
            if (cameraManager == null || camera != cameraManager.mainCamera)
            {
                return;
            }

            _nextSampleTime = Time.unscaledTime + (1f / Mathf.Max(1f, AutoGainConfig.SampleRate));

            if (_downsampleTexture == null)
            {
                _downsampleTexture = new RenderTexture(DownsampleSize, DownsampleSize, 0, RenderTextureFormat.ARGB32)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
            }

            // BuiltinRenderTextureType.CameraTarget symbolically refers to
            // wherever THIS camera just rendered (its own target texture,
            // or the backbuffer) -- Blitting from it downsamples straight
            // to an 8x8 copy without needing a registered
            // ScriptableRendererFeature.
            CommandBuffer cmd = CommandBufferPool.Get("QOL_NvgAutoGainSample");
            cmd.Blit(BuiltinRenderTextureType.CameraTarget, _downsampleTexture);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            _readbackPending = true;
            AsyncGPUReadback.Request(_downsampleTexture, 0, TextureFormat.RGBA32, OnReadbackComplete);
        }

        private static void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;
            if (request.hasError)
            {
                return;
            }

            NativeArray<Color32> pixels = request.GetData<Color32>();
            if (pixels.Length == 0)
            {
                return;
            }

            // Metered off the BRIGHTEST sample, not the average. A flat
            // average was the actual bug behind "gets brighter looking at
            // a light, dimmer looking away": a small, intense light source
            // occupies only a few of these samples, so it barely moves a
            // whole-frame average, while panning toward a large, evenly
            // NVG-brightened patch of terrain or sky can raise the average
            // more than the light itself did -- the exact reason real
            // cameras use spot/highlight metering instead of a flat
            // average for exposure protection against bright point
            // sources, which is what this feature is actually for.
            float maxLuma = 0f;
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                float luma = (0.3f * pixel.r + 0.59f * pixel.g + 0.11f * pixel.b) / 255f;
                if (luma > maxLuma)
                {
                    maxLuma = luma;
                }
            }
            _measuredBrightness = maxLuma;
        }
    }
}
