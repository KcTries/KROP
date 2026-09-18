using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace QOL_Realisim_Fixes.Patches
{
    // Vanilla's own NightVision.Update() (Postfix target here) only ever
    // adjusts postExposure/bloom threshold on the SAME Volume profile it
    // already toggles enabled/disabled based on nightVisActive -- no color
    // tint or noise at all. Rather than build a second, separate Volume
    // (which would need its own render-layer/priority wiring to actually
    // take effect), this adds ChannelMixer and FilmGrain as overrides onto
    // that SAME profile and layers a bloom intensity boost onto the Bloom
    // override vanilla already fetched for its own threshold control --
    // separate parameters on a component vanilla already owns, so it
    // doesn't fight its per-frame gain updates. Nothing here needs to
    // "undo" itself when night vision turns off: vanilla's own Update()
    // already disables the whole Volume in that case (postProcessing.
    // enabled = false), which takes every override on it out of the render
    // along with it.
    [HarmonyPatch(typeof(NightVision), "Update")]
    internal static class NightVisionEnhancementPatch
    {
        // A stable pattern -- only re-rolled when Static Density itself
        // changes, not on any timer. Sparkle specks used to be stamped on
        // top of this same texture, but that approach is gone entirely
        // (see NightVisionSparkleOverlay) -- Unity's post-process Volume
        // stack has no way to make a "speck" reliably stand out from the
        // scene's own HDR brightness, no matter how it's encoded.
        private static Texture2D _staticTexture;
        private static Color32[] _grainPixels;
        private static float _grainDensity = -1f;
        private static readonly System.Random RandomSource = new System.Random();

        // Boosts the ALREADY-tinted result from ChannelMixer below, not the
        // original scene -- safe to apply here (unlike the rejected
        // Saturation+ColorFilter approach) since by this point in the
        // pipeline the image has no original color information left to
        // muddy, just the single target hue at varying brightness. Punches
        // that hue up rather than fighting the ordering bug again.
        private const float VibrancyBoost = 80f;

        // Every component below is re-resolved from ___postProcessing.
        // profile on EVERY call instead of cached in a static field --
        // a mission/scene change can tear down and rebuild the profile a
        // Volume points to (confirmed: phosphor tint, saturation boost, and
        // static noise all silently stopped applying after a scene change,
        // despite looking correct right up until then, while nothing here
        // threw or logged an error -- a cached ChannelMixer/FilmGrain
        // reference from the OLD profile would do exactly that: writes
        // still succeed, just onto an object nothing renders anymore).
        // TryGet/Add are cheap dictionary-style lookups on a small profile,
        // so there's no real cost to just always re-checking rather than
        // trying to detect staleness some other way.
        // Tracks vanilla's own nightVisActive state independent of this
        // mod's "Enhanced NVGs" toggle below -- NightVisionAutoGain (a
        // separately toggleable feature) needs to know when NVG is on
        // regardless of whether tint/static/specks are enabled.
        internal static bool NightVisionActive { get; private set; }

        private static void Postfix(bool ___nightVisActive, Volume ___postProcessing)
        {
            NightVisionActive = ___nightVisActive;
            NightVisionTransitionOverlay.TickAnimation();

            if (!___nightVisActive || !NightVisionConfig.Enabled.Value)
            {
                NightVisionSparkleOverlay.SetActive(false);
                return;
            }
            if (___postProcessing == null || ___postProcessing.profile == null)
            {
                return;
            }

            NightVisionSparkleOverlay.SetActive(true);
            int effectiveSpeckCount = NightVisionDetonationFlash.GetEffectiveSpeckCount(NightVisionConfig.SpeckCount.Value);
            NightVisionSparkleOverlay.UpdateFrame(effectiveSpeckCount, NightVisionConfig.SpeckSize);

            VolumeProfile profile = ___postProcessing.profile;

            // ColorAdjustments' own Saturation and Color Filter parameters
            // were tried first for the tint itself and rejected -- Unity
            // applies Color Filter (multiply) BEFORE Saturation in its
            // internal pipeline regardless of what order this code sets
            // them in, so desaturating there computed per-pixel gray from
            // the ALREADY-tinted, still-multicolored result instead of a
            // clean grayscale-then-tint -- reported as washed-out,
            // inconsistent phosphor colors. ChannelMixer sums across
            // channels (30/59/11 Rec.601 luma weights, the standard
            // grayscale conversion, scaled by each fraction of the target
            // tint color) to compute "luminance x tint channel" as ONE
            // operation instead of two, so there's no ordering to get
            // wrong.
            if (!profile.TryGet(out ChannelMixer channelMixer))
            {
                channelMixer = profile.Add<ChannelMixer>(true);
            }
            channelMixer.active = true;
            Color tint = NightVisionConfig.GetTintColor();
            SetMixerRow(channelMixer.redOutRedIn, channelMixer.redOutGreenIn, channelMixer.redOutBlueIn, tint.r);
            SetMixerRow(channelMixer.greenOutRedIn, channelMixer.greenOutGreenIn, channelMixer.greenOutBlueIn, tint.g);
            SetMixerRow(channelMixer.blueOutRedIn, channelMixer.blueOutGreenIn, channelMixer.blueOutBlueIn, tint.b);

            // Vanilla's own Start() already throws if ColorAdjustments/
            // Bloom aren't on the profile it's handed, so both TryGets
            // below are guaranteed to succeed whenever Update() is even
            // running -- no Add() fallback needed for these two.
            if (profile.TryGet(out ColorAdjustments colorAdjustments))
            {
                colorAdjustments.saturation.overrideState = true;
                colorAdjustments.saturation.value = VibrancyBoost;
            }

            if (!profile.TryGet(out FilmGrain filmGrain))
            {
                filmGrain = profile.Add<FilmGrain>(true);
            }
            filmGrain.active = true;
            EnsureStaticTexture(filmGrain);
            filmGrain.intensity.overrideState = true;
            filmGrain.intensity.value = NightVisionConfig.StaticIntensity;

            if (profile.TryGet(out Bloom bloom))
            {
                bloom.intensity.overrideState = true;
                bloom.intensity.value = NightVisionConfig.BloomLevel;
                bloom.scatter.overrideState = true;
                bloom.scatter.value = 0.8f;
            }
        }

        private static void SetMixerRow(ClampedFloatParameter redIn, ClampedFloatParameter greenIn, ClampedFloatParameter blueIn, float tintChannel)
        {
            redIn.overrideState = true;
            redIn.value = 30f * tintChannel;
            greenIn.overrideState = true;
            greenIn.value = 59f * tintChannel;
            blueIn.overrideState = true;
            blueIn.value = 11f * tintChannel;
        }

        private const int StaticTextureSize = 128;

        // Procedurally builds the fine static grain texture and assigns it
        // as the CURRENT FilmGrain's custom grain map every call. Only
        // re-rolled when Static Density itself changes -- a stable
        // pattern, not something that needs to move to look right. The
        // type/response/texture assignments are re-applied unconditionally
        // in case this is a freshly re-added FilmGrain component on a new
        // profile (post scene-change) that hasn't seen them yet, even
        // though the Texture2D object itself is a plain C# object that
        // survives scene loads fine.
        private static void EnsureStaticTexture(FilmGrain filmGrain)
        {
            float density = NightVisionConfig.StaticDensity;

            bool firstRun = _staticTexture == null;
            if (firstRun)
            {
                _staticTexture = new Texture2D(StaticTextureSize, StaticTextureSize, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Point
                };
                _grainPixels = new Color32[StaticTextureSize * StaticTextureSize];
            }

            if (firstRun || !Mathf.Approximately(_grainDensity, density))
            {
                _grainDensity = density;
                RegenerateGrain(_grainPixels, density);
                _staticTexture.SetPixels32(_grainPixels);
                _staticTexture.Apply(false, false);
            }

            filmGrain.type.overrideState = true;
            filmGrain.type.value = FilmGrainLookup.Custom;
            filmGrain.response.overrideState = true;
            // 0 = uniform grain regardless of scene brightness. Response
            // suppresses grain in bright areas, which is most of an NVG-
            // brightened scene -- that's why noise barely showed up before
            // even at a nonzero intensity.
            filmGrain.response.value = 0f;
            filmGrain.texture.overrideState = true;
            filmGrain.texture.value = _staticTexture;
        }

        // Unity's own URP source (UberPostProcessPass.CalcFilmGrainParams)
        // secretly multiplies the intensity slider by a hardcoded 4x before
        // it reaches the shader -- undocumented, invisible in the
        // Inspector. Combined with the shader's actual blend, color + color
        // * grain * intensity * lum (Grain.hlsl's ApplyGrain, grain remapped
        // to [-1;1] with 0.5 as neutral), a fully-white grain pixel (grain
        // = +1) at Static Intensity 1 becomes a flat 5x multiply on that
        // pixel -- which clips to solid white on almost any non-black scene
        // content. Narrowing grain's swing to a band around neutral (only
        // 128 +/- 32) keeps it well under that clip point at any normal
        // intensity setting, giving a visible but non-blown-out texture
        // instead of stark binary black/white.
        private const byte GrainNeutral = 128;
        private const byte GrainSwing = 32;

        private static void RegenerateGrain(Color32[] pixels, float density)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                byte value = (byte)(RandomSource.NextDouble() < density ? GrainNeutral + GrainSwing : GrainNeutral - GrainSwing);
                // Unity's grain shader (Core RP Library's Grain.hlsl) reads
                // luminance from the ALPHA channel, not RGB -- its own
                // built-in grain textures are authored that way (DXT5
                // compresses a grayscale alpha channel better than RGB).
                // Writing the same value into every channel means it works
                // whichever channel actually gets sampled.
                pixels[i] = new Color32(value, value, value, value);
            }
        }
    }
}
