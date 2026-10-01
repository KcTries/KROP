using System.IO;
using System.Reflection;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Real dust/smoke art extracted from this game's OWN shipped
    // Texture2D assets (resources.assets, found via AssetStudioModCLI --
    // "smoke_billowy"/"smokeribbon_b") rather than sourced externally,
    // since a truly generic dust/contrail sprite that matches the game's
    // own art style isn't something to draw from scratch. Embedded
    // (rather than looked up live via Resources.FindObjectsOfTypeAll) so
    // it's always available regardless of whether anything has already
    // caused the game to load that texture into memory this session.
    internal static class EffectTextures
    {
        private const string DustResourceName = "QOL_Realisim_Fixes.Textures.SmokeBillowy.png";
        private const string ContrailResourceName = "QOL_Realisim_Fixes.Textures.SmokeRibbon.png";

        // The game's own normal map for the same smoke_billowy sprite --
        // vanilla's smoke/steam/dust materials all pair the two (a flat
        // billboard has a single camera-facing normal, so without a normal
        // map it only lights up when the sun is behind the camera).
        private const string DustNormalResourceName = "QOL_Realisim_Fixes.Textures.SmokeBillowyNormal.png";

        private static Texture2D _dust;
        private static Texture2D _dustNormal;
        private static Texture2D _contrail;
        private static Texture2D _contrailNormal;
        private static Material _dustMaterial;
        private static Material _contrailMaterial;

        internal static Texture2D GetDustTexture()
        {
            return _dust != null ? _dust : (_dust = LoadTexture(DustResourceName));
        }

        internal static Texture2D GetContrailTexture()
        {
            return _contrail != null ? _contrail : (_contrail = LoadTexture(ContrailResourceName));
        }

        // Shared by every aircraft's air-wake particle system -- one
        // Material, not one per instance, same reasoning as any other
        // cached shared asset in this mod.
        internal static Material GetDustMaterial()
        {
            if (_dustMaterial == null)
            {
                _dustMaterial = BuildAlphaBlendedMaterial(GetDustTexture(), GetDustNormalTexture(), isContrail: false);
            }
            return _dustMaterial;
        }

        internal static Material GetContrailMaterial()
        {
            if (_contrailMaterial == null)
            {
                _contrailMaterial = BuildAlphaBlendedMaterial(GetContrailTexture(), GetContrailNormalTexture(), isContrail: true);
            }
            return _contrailMaterial;
        }

        // Normal maps must be sampled linearly. A plain Texture2D from
        // LoadImage defaults to sRGB, which would gamma-curve every
        // normal's xy and skew all the shading -- so decode into a
        // temporary texture and copy the raw bytes into one created with
        // linear = true.
        private static Texture2D GetDustNormalTexture()
        {
            return _dustNormal != null ? _dustNormal : (_dustNormal = LoadLinearTexture(DustNormalResourceName));
        }

        private const int ContrailNormalWidth = 4;
        private const int ContrailNormalHeight = 64;

        // No normal map ships for the ribbon (smokeribbon_b is the only
        // ribbon texture in the game, and vanilla's own ribbon materials
        // have none either), so this builds one: the ribbon is a thin
        // strip, so describe it as a half-round tube. The ribbon's UVs run
        // U along its length and V across its width, so the surface tilt
        // sweeps from -1 to +1 across V (green channel = tilt across the
        // width) and stays level along U (red channel = tilt along the
        // length = 0). Tangent-space normals are stored as value * 0.5 + 0.5;
        // blue is whatever keeps the vector unit length. Strength is applied
        // later through the material's _BumpScale, not baked in here.
        private static Texture2D GetContrailNormalTexture()
        {
            if (_contrailNormal == null)
            {
                _contrailNormal = new Texture2D(
                    ContrailNormalWidth, ContrailNormalHeight, TextureFormat.RGBA32, false, true)
                {
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear
                };
                FillContrailNormal(_contrailNormal);
            }
            return _contrailNormal;
        }

        private static void FillContrailNormal(Texture2D texture)
        {
            bool swap = ContrailConfig.SwapNormalAxes;
            // 1 = the edges tilt a full 90 degrees (a true half-round tube).
            // The very edge normal is then exactly perpendicular to the view
            // direction (z = 0), which shades as a thin dark outline when
            // the light comes from the front.
            const float MaxTilt = 1f;

            Color32[] pixels = new Color32[ContrailNormalWidth * ContrailNormalHeight];
            for (int y = 0; y < ContrailNormalHeight; y++)
            {
                for (int x = 0; x < ContrailNormalWidth; x++)
                {
                    // t runs -1..+1 across the axis that is the ribbon's
                    // width -- the V axis (rows) normally, the U axis
                    // (columns) when the swap toggle is on.
                    float t = swap
                        ? (x + 0.5f) / ContrailNormalWidth * 2f - 1f
                        : (y + 0.5f) / ContrailNormalHeight * 2f - 1f;
                    float tilt = t * MaxTilt;
                    float z = Mathf.Sqrt(Mathf.Max(0f, 1f - tilt * tilt));
                    float nx = swap ? tilt : 0f;
                    float ny = swap ? 0f : tilt;
                    pixels[y * ContrailNormalWidth + x] = new Color32(
                        (byte)Mathf.RoundToInt((nx * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((ny * 0.5f + 0.5f) * 255f),
                        (byte)Mathf.RoundToInt((z * 0.5f + 0.5f) * 255f),
                        255);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        private static void ApplyLightingTo(Material material, float normalStrength, float selfIllumination)
        {
            if (material.HasProperty("_BumpScale"))
            {
                material.SetFloat("_BumpScale", normalStrength);
            }
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", new Color(selfIllumination, selfIllumination, selfIllumination, 1f));
            }
        }

        // Values below are copied directly from the game's own real wingtip-
        // vortex material ("smokeRibbonScatter.mat", used on Multirole1's
        // wingtipvortex_L/_R), found via the AssetRipper export -- not
        // guessed. It uses this exact embedded texture (smokeribbon_b.png)
        // completely unmodified (no recoloring), a Lit particle shader, and
        // a moderate baseline emission on top -- previous attempts here
        // tried stripping the texture's own tint (forceWhiteRgb) and an
        // overbright unlit color push, both of which were solving the wrong
        // problem: the real fix is that vanilla's material isn't PURELY
        // lit-by-scene-light -- it has its own constant emission so it
        // reads as a consistent white/vapor color regardless of whatever
        // tint the current ambient/sky light happens to be casting, while
        // still dimming at night since the lit component (not the emission)
        // is what's small at night.
        private static Material BuildAlphaBlendedMaterial(Texture2D texture, Texture2D normalMap, bool isContrail)
        {
            if (texture == null)
            {
                return null;
            }

            // This project's own .csproj references
            // Unity.RenderPipelines.Universal.Runtime -- this game runs
            // URP, not the legacy built-in pipeline, so the shader has to
            // be URP's own particle shader. Lit (not Unlit) so it responds
            // to real sun/moon/ambient light like everything else in the
            // scene -- confirmed as the right choice: the real
            // smokeRibbonScatter.mat is also a Lit particle shader.
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Lit");
            if (shader == null)
            {
                SoundPropagation.Log.LogWarning(
                    "[AirWake] Could not find 'Universal Render Pipeline/Particles/Lit' shader -- effect will not render.");
                return null;
            }

            Material material = new Material(shader) { mainTexture = texture };
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", Color.white);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", Color.white);
            }
            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0f);
            }
            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", 0f);
            }
            if (material.HasProperty("_ColorMode"))
            {
                material.SetFloat("_ColorMode", 0f);
            }
            if (material.HasProperty("_Cull"))
            {
                material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
            }
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            // The real material's m_ValidKeywords list also includes
            // _RECEIVE_SHADOWS_OFF, _FADING_ON, and _SOFTPARTICLES_ON --
            // none of which were ever enabled here before. Setting the
            // backing float property alone (as done further above/below)
            // without the matching keyword often means the compiled shader
            // variant doesn't even include that code path, or reads an
            // uninitialized/inconsistent state if it partially does --
            // "washed out/muggy" fits a soft-particle depth-fade or shadow
            // term running half-configured better than a simple tint error.
            if (material.HasProperty("_ReceiveShadows"))
            {
                material.SetFloat("_ReceiveShadows", 0f);
            }
            material.EnableKeyword("_RECEIVE_SHADOWS_OFF");

            // Reverted -- Soft Particles fades a particle out when it's
            // close to intersecting ANY opaque geometry (via the camera's
            // depth texture), and our particles spawn right at the engine
            // nozzle, essentially touching the emitting aircraft's own
            // fuselage. That's exactly the "too close to geometry" zone
            // Soft Particles fades toward invisible -- reported as
            // "broke the contrails" right after this was added. Camera
            // Fading (fades particles too close to the lens) is the same
            // category of risk and reverted alongside it, untested in
            // isolation.
            //
            // material.SetFloat("_CameraFadingEnabled", 1f);
            // material.SetFloat("_CameraNearFadeDistance", 10f);
            // material.SetFloat("_CameraFarFadeDistance", 30f);
            // material.EnableKeyword("_FADING_ON");
            //
            // material.SetFloat("_SoftParticlesEnabled", 1f);
            // material.SetFloat("_SoftParticlesNearFadeDistance", 0f);
            // material.SetFloat("_SoftParticlesFarFadeDistance", 5f);
            // material.EnableKeyword("_SOFTPARTICLES_ON");
            // 2999, not the default Transparent baseline (3000) -- copied
            // directly from the real smokeRibbonScatter.mat's own
            // m_CustomRenderQueue. Two alpha-blended transparent objects
            // never depth-test against each other (neither writes depth),
            // so their draw order comes from queue value first and only
            // falls back to a fragile camera-distance sort within the same
            // queue -- this is vanilla's own deliberate fix for exactly
            // the "renders through canopy glass with no shading
            // interaction" problem, drawing itself one step earlier than
            // standard transparents like glass so they correctly layer on
            // top of it.
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent - 1;
            material.SetShaderPassEnabled("ShadowCaster", false);
            material.SetShaderPassEnabled("DepthOnly", false);

            // Same two properties vanilla's own smoke materials (e.g.
            // smoke_midground) set: a tangent-space normal map plus its
            // keyword -- the keyword is what actually compiles the
            // normal-map code path in, the texture alone does nothing.
            if (normalMap != null)
            {
                material.SetTexture("_BumpMap", normalMap);
                material.EnableKeyword("_NORMALMAP");
            }

            // Emission is always keyword-enabled and driven purely by each
            // effect's SelfIllumination constant (0 = black = no
            // contribution); the contrail's is vanilla's own 0.6032 level.
            material.EnableKeyword("_EMISSION");
            ApplyLightingTo(
                material,
                isContrail ? ContrailConfig.NormalStrength : AirWakeConfig.NormalStrength,
                isContrail ? ContrailConfig.SelfIllumination : AirWakeConfig.SelfIllumination);

            return material;
        }

        private static Texture2D LoadLinearTexture(string resourceName)
        {
            Texture2D decoded = LoadTexture(resourceName);
            if (decoded == null)
            {
                return null;
            }
            Texture2D linear = new Texture2D(decoded.width, decoded.height, TextureFormat.RGBA32, false, true)
            {
                wrapMode = TextureWrapMode.Clamp
            };
            linear.SetPixels32(decoded.GetPixels32());
            linear.Apply(false, false);
            Object.Destroy(decoded);
            return linear;
        }

        private static Texture2D LoadTexture(string resourceName)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        SoundPropagation.Log.LogError($"[AirWake] Embedded resource '{resourceName}' not found.");
                        return null;
                    }
                    byte[] data = new byte[stream.Length];
                    stream.Read(data, 0, data.Length);

                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!ImageConversion.LoadImage(texture, data))
                    {
                        SoundPropagation.Log.LogError($"[AirWake] Failed to decode embedded texture '{resourceName}'.");
                        return null;
                    }

                    return texture;
                }
            }
            catch (System.Exception ex)
            {
                SoundPropagation.Log.LogError($"[AirWake] EXCEPTION loading embedded texture '{resourceName}': {ex}");
                return null;
            }
        }
    }
}
