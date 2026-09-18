using System;
using UnityEngine;
using UnityEngine.UI;

namespace QOL_Realisim_Fixes.Patches
{
    // Drives the exact sequence requested:
    //
    // Entering NVG: a black bar wipes down from the top. Once it fully
    // covers the screen, two eyelid bars are spawned (seamlessly matching
    // where the single bar was), the original bar is despawned, and the
    // real NVG toggle fires right at that moment -- the screen stays fully
    // covered throughout, so the swap is invisible. The eyelids then
    // recede up/down off-screen and despawn once fully open.
    //
    // Exiting NVG: a black bar wipes up from the bottom. Once it reaches
    // the top (fully covers the screen), the real NVG toggle fires
    // underneath it, then the bar fades out over half a second.
    //
    // NightVisionToggleInterceptPatch is what actually calls BeginTransition
    // and controls the real toggle timing -- this class only owns the
    // visuals and phase timing, it never touches NightVision itself.
    internal static class NightVisionTransitionOverlay
    {
        private enum Phase
        {
            Idle,
            EnterWiping,
            EyelidOpening,
            ExitWiping,
            ExitFading
        }

        private const float EnterWipeDuration = 0.22f;
        private const float EyelidOpenDuration = 0.4f;
        private const float ExitWipeDuration = 0.22f;
        private const float ExitFadeDuration = 0.5f;

        // How tall the soft gradient edge is, in pixels, on every moving
        // boundary of every bar.
        private const float FeatherPixels = 80f;

        private static Canvas _canvas;
        private static Sprite _featherSprite;

        private static RectTransform _wipeBar;
        private static Image _wipeBarImage;
        private static RectTransform _wipeFringe;
        private static Image _wipeFringeImage;
        private static bool _wipeBarTopAnchored;

        private static RectTransform _topEyelid;
        private static RectTransform _topEyelidFringe;
        private static RectTransform _bottomEyelid;
        private static RectTransform _bottomEyelidFringe;

        private static Phase _phase = Phase.Idle;
        private static float _phaseStartTime;
        private static Action _pendingFlip;

        internal static bool IsBusy => _phase != Phase.Idle;

        internal static void BeginTransition(bool entering, Action onFlipPoint)
        {
            EnsureBuilt();
            _pendingFlip = onFlipPoint;

            if (entering)
            {
                SpawnWipeBar(topAnchored: true);
                StartPhase(Phase.EnterWiping);
            }
            else
            {
                SpawnWipeBar(topAnchored: false);
                StartPhase(Phase.ExitWiping);
            }
        }

        // Called every frame regardless of NVG state -- this is what
        // actually advances whichever phase is currently in progress.
        internal static void TickAnimation()
        {
            if (_phase == Phase.Idle)
            {
                return;
            }

            float elapsed = Time.unscaledTime - _phaseStartTime;

            switch (_phase)
            {
                case Phase.EnterWiping:
                {
                    float t = Mathf.Clamp01(elapsed / EnterWipeDuration);
                    SetWipeBarCoverage(t);
                    if (t >= 1f)
                    {
                        SpawnEyelids();
                        DestroyWipeBar();
                        _pendingFlip?.Invoke();
                        StartPhase(Phase.EyelidOpening);
                    }
                    break;
                }
                case Phase.EyelidOpening:
                {
                    float t = Mathf.Clamp01(elapsed / EyelidOpenDuration);
                    SetEyelidOpen(t);
                    if (t >= 1f)
                    {
                        DespawnEyelids();
                        _phase = Phase.Idle;
                    }
                    break;
                }
                case Phase.ExitWiping:
                {
                    float t = Mathf.Clamp01(elapsed / ExitWipeDuration);
                    SetWipeBarCoverage(t);
                    if (t >= 1f)
                    {
                        _pendingFlip?.Invoke();
                        StartPhase(Phase.ExitFading);
                    }
                    break;
                }
                case Phase.ExitFading:
                {
                    float t = Mathf.Clamp01(elapsed / ExitFadeDuration);
                    SetWipeBarAlpha(1f - t);
                    if (t >= 1f)
                    {
                        DestroyWipeBar();
                        _phase = Phase.Idle;
                    }
                    break;
                }
            }
        }

        private static void StartPhase(Phase phase)
        {
            _phase = phase;
            _phaseStartTime = Time.unscaledTime;
        }

        private static float CanvasHeight()
        {
            return ((RectTransform)_canvas.transform).rect.height;
        }

        private static void SetWipeBarCoverage(float t)
        {
            float height = CanvasHeight() * Mathf.Clamp01(t);
            _wipeBar.sizeDelta = new Vector2(0f, height);
            // Top-anchored bar's moving (bottom) edge sits at -height in
            // its own anchor frame; bottom-anchored bar's moving (top)
            // edge sits at +height in its own anchor frame.
            float edgeY = _wipeBarTopAnchored ? -height : height;
            _wipeFringe.anchoredPosition = new Vector2(0f, edgeY);
        }

        private static void SetWipeBarAlpha(float alpha)
        {
            SetImageAlpha(_wipeBarImage, alpha);
            SetImageAlpha(_wipeFringeImage, alpha);
        }

        // Both eyelids start flush at the vertical center (fully covering,
        // seamlessly matching where the single wipe bar just was) and
        // recede toward their own screen edge and beyond as t -> 1. Each
        // eyelid's own height stays fixed at halfHeight -- only its
        // position moves -- so its moving edge is derived from position,
        // not size, unlike the wipe bar above.
        private static void SetEyelidOpen(float t)
        {
            float halfHeight = CanvasHeight() * 0.5f;
            float offset = halfHeight * Mathf.Clamp01(t);

            _topEyelid.anchoredPosition = new Vector2(0f, offset);
            // Top eyelid's bottom (revealing) edge, in its own top-anchor
            // frame, is (position - height) below the anchor.
            _topEyelidFringe.anchoredPosition = new Vector2(0f, offset - halfHeight);

            _bottomEyelid.anchoredPosition = new Vector2(0f, -offset);
            // Bottom eyelid's top (revealing) edge, in its own
            // bottom-anchor frame, is (height - position) above the
            // anchor (position is negative here, so this adds).
            _bottomEyelidFringe.anchoredPosition = new Vector2(0f, halfHeight - offset);
        }

        private static void SetImageAlpha(Image image, float alpha)
        {
            Color color = image.color;
            color.a = Mathf.Clamp01(alpha);
            image.color = color;
        }

        private static void SpawnWipeBar(bool topAnchored)
        {
            _wipeBarTopAnchored = topAnchored;
            Vector2 anchor = topAnchored ? new Vector2(0.5f, 1f) : new Vector2(0.5f, 0f);
            (_wipeBar, _wipeBarImage) = BuildBar("WipeBar", anchor);
            _wipeFringe = BuildFringe("WipeFringe", anchor, topAnchored, out _wipeFringeImage);
        }

        private static void DestroyWipeBar()
        {
            if (_wipeBar != null)
            {
                UnityEngine.Object.Destroy(_wipeBar.gameObject);
            }
            if (_wipeFringe != null)
            {
                UnityEngine.Object.Destroy(_wipeFringe.gameObject);
            }
            _wipeBar = null;
            _wipeBarImage = null;
            _wipeFringe = null;
            _wipeFringeImage = null;
        }

        private static void SpawnEyelids()
        {
            float halfHeight = CanvasHeight() * 0.5f;

            // Both solid bars are created BEFORE either fringe. Unity UI
            // renders later siblings on top of earlier ones within the
            // same parent -- at the start of this animation, each fringe
            // sits right at screen center, which is exactly where the
            // OPPOSITE eyelid's solid bar also is. Creating a fringe
            // before the other bar meant that bar (a later sibling) drew
            // over it, hiding the gradient completely -- only one of the
            // two fringes was ever actually visible.
            (_topEyelid, _) = BuildBar("TopEyelid", new Vector2(0.5f, 1f));
            _topEyelid.sizeDelta = new Vector2(0f, halfHeight);

            (_bottomEyelid, _) = BuildBar("BottomEyelid", new Vector2(0.5f, 0f));
            _bottomEyelid.sizeDelta = new Vector2(0f, halfHeight);

            _topEyelidFringe = BuildFringe("TopEyelidFringe", new Vector2(0.5f, 1f), topAnchored: true, out _);
            _bottomEyelidFringe = BuildFringe("BottomEyelidFringe", new Vector2(0.5f, 0f), topAnchored: false, out _);
        }

        private static void DespawnEyelids()
        {
            UnityEngine.Object.Destroy(_topEyelid.gameObject);
            UnityEngine.Object.Destroy(_topEyelidFringe.gameObject);
            UnityEngine.Object.Destroy(_bottomEyelid.gameObject);
            UnityEngine.Object.Destroy(_bottomEyelidFringe.gameObject);
            _topEyelid = null;
            _topEyelidFringe = null;
            _bottomEyelid = null;
            _bottomEyelidFringe = null;
        }

        private static (RectTransform, Image) BuildBar(string name, Vector2 anchor)
        {
            GameObject barObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            barObject.transform.SetParent(_canvas.transform, false);
            Image image = barObject.GetComponent<Image>();
            image.color = Color.black;
            image.raycastTarget = false;
            RectTransform rect = barObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, anchor.y);
            rect.anchorMax = new Vector2(1f, anchor.y);
            rect.pivot = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            return (rect, image);
        }

        // A feathered strip uses the same shared gradient sprite for every
        // edge -- opaque at its own pivot edge, fading to transparent
        // FeatherPixels away from it -- flipped vertically for
        // bottom-anchored bars so the opaque side always faces the solid
        // bar it's attached to.
        private static RectTransform BuildFringe(string name, Vector2 anchor, bool topAnchored, out Image image)
        {
            GameObject fringeObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            fringeObject.transform.SetParent(_canvas.transform, false);
            image = fringeObject.GetComponent<Image>();
            image.sprite = _featherSprite;
            image.type = Image.Type.Simple;
            image.raycastTarget = false;
            RectTransform rect = fringeObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, anchor.y);
            rect.anchorMax = new Vector2(1f, anchor.y);
            rect.pivot = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(0f, FeatherPixels);
            rect.localScale = topAnchored ? Vector3.one : new Vector3(1f, -1f, 1f);
            return rect;
        }

        private static void EnsureBuilt()
        {
            if (_canvas != null)
            {
                return;
            }

            GameObject canvasObject = new GameObject("QOL_NvgTransitionOverlay", typeof(Canvas), typeof(CanvasScaler));
            UnityEngine.Object.DontDestroyOnLoad(canvasObject);
            _canvas = canvasObject.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above the sparkle overlay and the game's own HUD -- this
            // represents the player's own eyelids, so it should obscure
            // everything, cockpit instruments included.
            _canvas.sortingOrder = short.MaxValue;
            canvasObject.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;

            _featherSprite = BuildFeatherSprite();
        }

        private static Sprite BuildFeatherSprite()
        {
            const int width = 4;
            const int height = 64;
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            Color32[] pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                // Opaque at the top row (v=1), fully transparent at the
                // bottom row (v=0) -- BuildFringe flips this vertically
                // for bottom-anchored bars.
                byte alpha = (byte)Mathf.RoundToInt(255f * (y / (float)(height - 1)));
                for (int x = 0; x < width; x++)
                {
                    pixels[y * width + x] = new Color32(0, 0, 0, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
        }
    }
}
