using UnityEngine;
using UnityEngine.UI;

namespace QOL_Realisim_Fixes.Patches
{
    // FilmGrain-based specks (tried first) fundamentally couldn't work:
    // Unity's post-process Volume stack (ChannelMixer, FilmGrain, Bloom)
    // all operate as multiplies on the scene's own HDR render, so anything
    // they produce gets folded back into the same tonemap/bloom pass as
    // the rest of the image -- there's no way to make a "speck" reliably
    // stand out from the scene's own brightness using that pipeline. A
    // Screen Space - Overlay Canvas sidesteps the problem entirely: Unity
    // composites it directly onto the final backbuffer AFTER the camera
    // (and its whole post-process stack, bloom included) has already
    // resolved -- a plain white UI sprite here is guaranteed to render as
    // opaque white, immune to tint, tonemapping, and bloom, by
    // construction rather than by tuning.
    internal static class NightVisionSparkleOverlay
    {
        private static Canvas _canvas;
        private static Sprite _dotSprite;
        private static RectTransform[] _dots = new RectTransform[0];
        private static int _builtSize = -1;
        private static readonly System.Random RandomSource = new System.Random();

        internal static void SetActive(bool active)
        {
            if (!active && _canvas == null)
            {
                return;
            }
            EnsureBuilt();
            _canvas.gameObject.SetActive(active);
        }

        internal static void UpdateFrame(int count, int sizePixels)
        {
            EnsureBuilt();
            EnsureDotCount(count, sizePixels);

            RectTransform canvasRect = (RectTransform)_canvas.transform;
            float width = canvasRect.rect.width;
            float height = canvasRect.rect.height;

            for (int i = 0; i < _dots.Length; i++)
            {
                float x = ((float)RandomSource.NextDouble() - 0.5f) * width;
                float y = ((float)RandomSource.NextDouble() - 0.5f) * height;
                _dots[i].anchoredPosition = new Vector2(x, y);
            }
        }

        // A plain scripted GameObject (not part of any scene asset) with
        // DontDestroyOnLoad survives scene/mission changes on its own --
        // no need for the "re-resolve every call" staleness dance the
        // Volume-based effects needed, since nothing here is destroyed and
        // recreated by the game itself.
        private static void EnsureBuilt()
        {
            if (_canvas != null)
            {
                return;
            }

            GameObject canvasObject = new GameObject("QOL_NvgSparkleOverlay", typeof(Canvas), typeof(CanvasScaler));
            Object.DontDestroyOnLoad(canvasObject);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Lowest possible sorting order so this draws UNDER every other
            // Screen Space - Overlay canvas (HUD, menus, mission editor UI,
            // etc.), which all default to sortingOrder 0 unless a mod or
            // the game itself sets something higher -- only the 3D scene
            // sits further back than this.
            _canvas.sortingOrder = short.MinValue;
            canvasObject.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            _dotSprite = BuildDotSprite();
        }

        private static Sprite BuildDotSprite()
        {
            const int size = 8;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear
            };
            Color32[] pixels = new Color32[size * size];
            float radius = size * 0.5f;
            Vector2 center = new Vector2(radius, radius);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    byte alpha = (byte)(dist <= radius ? 255 : 0);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }

        // Dot count/size only change when the config sliders move, not
        // every frame -- rebuilding the pool of Image objects is cheap but
        // there's no reason to do it more than needed.
        private static void EnsureDotCount(int count, int sizePixels)
        {
            if (_dots.Length == count && _builtSize == sizePixels)
            {
                return;
            }
            _builtSize = sizePixels;

            foreach (RectTransform existing in _dots)
            {
                if (existing != null)
                {
                    Object.Destroy(existing.gameObject);
                }
            }

            RectTransform[] newDots = new RectTransform[count];
            for (int i = 0; i < count; i++)
            {
                GameObject dotObject = new GameObject("dot", typeof(RectTransform), typeof(Image));
                dotObject.transform.SetParent(_canvas.transform, false);
                Image image = dotObject.GetComponent<Image>();
                image.sprite = _dotSprite;
                image.raycastTarget = false;
                RectTransform rect = dotObject.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(sizePixels, sizePixels);
                newDots[i] = rect;
            }
            _dots = newDots;
        }
    }
}
