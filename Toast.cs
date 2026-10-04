using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChorusMod;

/// <summary>
/// Custom toast notification -- small accent-colored label, big title,
/// smaller message. Slides in with a glow, holds while a progress bar
/// drains, slides out. Queues multiple requests and shows them one at a
/// time.
///
/// Built after confirming (through hands-on debug testing) that the
/// game's own FadeCanvas/CanvasFader can't be reused safely: FadeCanvas's
/// root CanvasGroup sits at alpha=0, driven by some external game system
/// we don't control -- our own content would either stay invisible under
/// it (Unity multiplies alpha down through nested CanvasGroups, so a
/// parent at 0 hides everything under it no matter what a child sets) or
/// risk fighting that system if we forced the parent's alpha ourselves.
/// CanvasFader's own public trigger method was also confirmed to leave a
/// freshly-attached CanvasGroup's alpha completely untouched.
///
/// So this owns its entire rendering pipeline instead: a dedicated Canvas
/// (sortingOrder above everything else) and only Images with no sprite
/// assigned -- Unity renders those as flat colored quads, which sidesteps
/// Texture2D/Sprite construction being unavailable on this build. Every
/// effect is built out of such quads: the glow is a stack of growing,
/// faint quads behind the panel; the badge is a few quads rotated 45°.
/// Animation is a plain Update() state machine rather than a coroutine,
/// consistent with the rest of the plugin's IL2CPP-safe style.
/// </summary>
public class Toast : MonoBehaviour
{
    public Toast(IntPtr ptr) : base(ptr) { }

    private struct Request
    {
        public string Label;
        public string Title;
        public string Message;
        public float DurationSeconds;
        public Color Accent;

        // Rank-up only: the title starts as TitleBefore, then flips to
        // Title with a punch, a badge spin and rising sparks.
        public bool RankUp;
        public string TitleBefore;
    }

    private enum State
    {
        Idle,
        FadingIn,
        Holding,
        FadingOut,
    }

    /// An Image tinted with the current toast's accent color, keeping
    /// its own opacity.
    private struct AccentPart
    {
        public Image Image;
        public float Alpha;
    }

    public static readonly Color DefaultAccent = new(0.30f, 0.75f, 1f);

    private const float PanelWidth = 400f;
    private const float PanelHeight = 86f;
    private const float ScreenMargin = 36f;
    private const float InSeconds = 0.35f;
    private const float OutSeconds = 0.3f;
    private const float SlideDistance = 60f;
    private const float DefaultDurationSeconds = 3.5f;

    private const int GlowLayers = 5;
    private const float GlowStep = 2f;
    private const float GlowLayerAlpha = 0.045f;
    private const float GlowPulseSeconds = 1.6f;
    private const float GlowFlashSeconds = 0.6f;

    private const float RankUpDelay = 0.5f;
    private const float RankUpPunchSeconds = 0.35f;
    private const float RankUpSpinSeconds = 0.6f;
    private const int SparkCount = 10;
    private const float SparkLifeSeconds = 0.9f;
    private const float SparkStagger = 0.06f;
    private const float SparkRise = 55f;


    private static Toast? _instance;

    private bool _built;
    private RectTransform? _root;
    private CanvasGroup? _canvasGroup;
    private CanvasGroup? _glowGroup;
    private RectTransform? _progress;
    private RectTransform? _badge;
    private readonly List<Image> _sparks = new();
    private TextMeshProUGUI? _labelText;
    private TextMeshProUGUI? _titleText;
    private TextMeshProUGUI? _messageText;
    private readonly List<AccentPart> _accentParts = new();

    private readonly ConcurrentQueue<Request> _queue = new();
    private State _state = State.Idle;
    private float _timer;
    private float _currentDuration;
    private Request _current;
    private bool _rankUpFlipped;

    /// Called once from Plugin.Load().
    public static void Initialize()
    {
        if (_instance != null)
        {
            return;
        }

        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<Toast>();
        var host = new GameObject("ChorusMod.ToastHost");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        _instance = host.AddComponent<Toast>();
    }

    /// Safe to call from any thread: it only enqueues, and Update() does
    /// all the Unity work on the main thread. TrophyListener calls it
    /// straight from its background SSE reader.
    /// <param name="label">Small uppercase line above the title.</param>
    /// <param name="accent">Glow, badge, label and progress bar color.</param>
    public static void Show(
        string title,
        string message,
        float durationSeconds = DefaultDurationSeconds,
        Color? accent = null,
        string label = "Chorus Mod"
    )
    {
        if (_instance == null)
        {
            Plugin.Logger.LogWarning("Toast: Show() called before Initialize().");
            return;
        }

        _instance._queue.Enqueue(
            new Request
            {
                Label = label,
                Title = title,
                Message = message,
                DurationSeconds = durationSeconds,
                Accent = accent ?? DefaultAccent,
            }
        );
    }

    /// Toast whose title visibly ticks over from `titleBefore` to
    /// `titleAfter` shortly after it appears ("Level 7" -> "Level 8").
    public static void ShowRankUp(
        string titleBefore,
        string titleAfter,
        string message,
        float durationSeconds,
        Color accent,
        string label
    )
    {
        if (_instance == null)
        {
            Plugin.Logger.LogWarning("Toast: ShowRankUp() called before Initialize().");
            return;
        }

        _instance._queue.Enqueue(
            new Request
            {
                Label = label,
                Title = titleAfter,
                TitleBefore = titleBefore,
                Message = message,
                DurationSeconds = durationSeconds,
                Accent = accent,
                RankUp = true,
            }
        );
    }

    private void Update()
    {
        if (!_built)
        {
            // Built lazily on first tick rather than in Awake(): this
            // mirrors the pattern already proven reliable elsewhere in
            // the plugin (ChorusUI builds its UI lazily too) rather than
            // depending on Awake() firing predictably for a component
            // registered and added purely through IL2CPP interop.
            BuildUI();
            _built = true;
        }

        // Unscaled: a paused game (timeScale 0) mustn't freeze the toast.
        var dt = Time.unscaledDeltaTime;

        switch (_state)
        {
            case State.Idle:
                if (!_queue.IsEmpty)
                {
                    StartNext();
                }
                break;

            case State.FadingIn:
                _timer += dt;
                var tIn = Mathf.Clamp01(_timer / InSeconds);
                var easeOut = 1f - Mathf.Pow(1f - tIn, 3f);
                SetAlpha(tIn);
                SetSlide(SlideDistance * (1f - easeOut));
                SetGlow(1f);
                if (tIn >= 1f)
                {
                    _timer = 0f;
                    _state = State.Holding;
                }
                break;

            case State.Holding:
                _timer += dt;
                SetProgress(1f - Mathf.Clamp01(_timer / _currentDuration));

                // Bright flash on arrival settling into a slow pulse.
                var pulse = 0.75f + 0.1f * Mathf.Sin(_timer * 2f * Mathf.PI / GlowPulseSeconds);
                var flash = Mathf.Clamp01(1f - _timer / GlowFlashSeconds);
                if (_current.RankUp)
                {
                    var sinceFlip = _timer - RankUpDelay;
                    AnimateRankUp(sinceFlip);
                    if (sinceFlip >= 0f)
                    {
                        flash = Mathf.Max(flash, Mathf.Clamp01(1f - sinceFlip / GlowFlashSeconds));
                    }
                }

                SetGlow(Mathf.Lerp(pulse, 1f, flash));

                if (_timer >= _currentDuration)
                {
                    _timer = 0f;
                    _state = State.FadingOut;
                }
                break;

            case State.FadingOut:
                _timer += dt;
                var tOut = Mathf.Clamp01(_timer / OutSeconds);
                SetAlpha(1f - tOut);
                SetSlide(SlideDistance * tOut * tOut);
                if (tOut >= 1f)
                {
                    _state = State.Idle;
                }
                break;
        }
    }

    private void SetAlpha(float value)
    {
        if (_canvasGroup != null)
        {
            _canvasGroup.alpha = value;
        }
    }

    private void SetGlow(float value)
    {
        if (_glowGroup != null)
        {
            _glowGroup.alpha = value;
        }
    }

    /// Horizontal offset to the right of the resting position.
    private void SetSlide(float offset)
    {
        if (_root != null)
        {
            _root.anchoredPosition = new Vector2(-ScreenMargin + offset, -ScreenMargin);
        }
    }

    private void SetProgress(float remaining)
    {
        if (_progress != null)
        {
            _progress.anchorMax = new Vector2(remaining, _progress.anchorMax.y);
        }
    }

    private void StartNext()
    {
        if (_titleText == null || _messageText == null || _labelText == null
            || !_queue.TryDequeue(out var request))
        {
            return;
        }

        _current = request;
        _rankUpFlipped = false;
        ResetRankUp();

        _labelText.text = request.Label.ToUpperInvariant();
        _labelText.color = request.Accent;
        _titleText.text = request.RankUp ? request.TitleBefore : request.Title;
        _messageText.text = request.Message;

        foreach (var part in _accentParts)
        {
            var color = request.Accent;
            color.a = part.Alpha;
            part.Image.color = color;
        }

        _currentDuration = request.DurationSeconds;
        _timer = 0f;
        SetProgress(1f);
        SetAlpha(0f);
        SetSlide(SlideDistance);
        _state = State.FadingIn;
    }

    /// `since` = seconds since the title flip (negative = not yet).
    private void AnimateRankUp(float since)
    {
        if (since < 0f || _titleText == null || _badge == null)
        {
            return;
        }

        if (!_rankUpFlipped)
        {
            _titleText.text = _current.Title;
            _rankUpFlipped = true;
        }

        // Title punch: pops to 130 % and settles back.
        var punch = EaseOut(Mathf.Clamp01(since / RankUpPunchSeconds));
        _titleText.rectTransform.localScale = Vector3.one * Mathf.Lerp(1.3f, 1f, punch);

        // Badge: one full turn with a bump in size halfway through.
        var spin = Mathf.Clamp01(since / RankUpSpinSeconds);
        _badge.localRotation = Quaternion.Euler(0f, 0f, 45f + 360f * EaseOut(spin));
        _badge.localScale = Vector3.one * (1f + 0.35f * Mathf.Sin(spin * Mathf.PI));

        // Sparks rise one after another, fading as they go.
        for (var i = 0; i < _sparks.Count; i++)
        {
            var life = (since - i * SparkStagger) / SparkLifeSeconds;
            var spark = _sparks[i];
            var color = _current.Accent;

            if (life < 0f || life > 1f)
            {
                color.a = 0f;
                spark.color = color;
                continue;
            }

            spark.rectTransform.anchoredPosition = new Vector2(
                84f + i * 30f + 4f * Mathf.Sin(life * 9f + i),
                10f + SparkRise * EaseOut(life)
            );
            color.a = 1f - life;
            spark.color = color;
        }
    }

    private void ResetRankUp()
    {
        if (_titleText != null)
        {
            _titleText.rectTransform.localScale = Vector3.one;
        }

        if (_badge != null)
        {
            _badge.localRotation = Quaternion.Euler(0f, 0f, 45f);
            _badge.localScale = Vector3.one;
        }

        foreach (var spark in _sparks)
        {
            spark.color = Color.clear;
        }
    }

    private static float EaseOut(float t) => 1f - Mathf.Pow(1f - t, 3f);

    private void BuildUI()
    {
        try
        {
            // Must run BEFORE adding our own TMP texts, see UiKit.FindGameFont.
            var font = UiKit.FindGameFont();

            var canvasObject = new GameObject("ChorusMod.ToastCanvas");
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000; // above FadeCanvas (100) and everything else
            UiKit.AddScaler(canvasObject);

            // Root: what slides and fades. Glow and panel are siblings
            // under it, glow first so it draws behind.
            _root = UiKit.NewRect("Root", canvasObject.transform);
            _root.anchorMin = new Vector2(1f, 1f);
            _root.anchorMax = new Vector2(1f, 1f);
            _root.pivot = new Vector2(1f, 1f);
            _root.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            _canvasGroup = _root.gameObject.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            BuildGlow(_root);

            var panel = UiKit.Stretch(UiKit.NewRect("Panel", _root), 0f);
            UiKit.Stretch(UiKit.Quad(panel, "Background", UiKit.PanelColor).rectTransform, 0f);

            // Lighter top half: a cheap two-step vertical gradient.
            var sheen = UiKit.Quad(panel, "Sheen", new Color(1f, 1f, 1f, 0.03f));
            UiKit.Place(sheen.rectTransform, new Vector2(0f, 0.5f), Vector2.one, Vector2.zero, Vector2.zero);

            BuildBorder(panel);

            // Accent bar along the left edge.
            var bar = AccentQuad(panel, "AccentBar", 1f);
            UiKit.Place(bar.rectTransform, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(4f, 0f));

            BuildBadge(panel);
            BuildSparks(panel);

            _labelText = UiKit.Text(panel, "Label", font, 11f, FontStyles.Bold, Color.white);
            _labelText.characterSpacing = 6f;
            UiKit.Place(_labelText.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(76f, -28f), new Vector2(-16f, -12f));

            _titleText = UiKit.Text(panel, "Title", font, 19f, FontStyles.Bold, Color.white);
            // Left pivot so the rank-up punch grows from where the text
            // starts. Set before UiKit.Place(): offsets are computed from it.
            _titleText.rectTransform.pivot = new Vector2(0f, 0.5f);
            _titleText.enableAutoSizing = true;
            _titleText.fontSizeMin = 13f;
            _titleText.fontSizeMax = 19f;
            UiKit.Place(_titleText.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(76f, -54f), new Vector2(-16f, -27f));

            // Trophy descriptions can run longer than one line: shrink to
            // fit the panel instead of overflowing it.
            _messageText = UiKit.Text(panel, "Message", font, 13f, FontStyles.Normal, UiKit.MutedText);
            _messageText.enableAutoSizing = true;
            _messageText.fontSizeMin = 9f;
            _messageText.fontSizeMax = 13f;
            UiKit.Place(_messageText.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(76f, 7f), new Vector2(-16f, 32f));

            // Shrinks toward the left while the toast holds.
            var track = UiKit.Quad(panel, "ProgressTrack", new Color(1f, 1f, 1f, 0.06f));
            UiKit.Place(track.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(4f, 0f), new Vector2(0f, 2f));
            var progress = AccentQuad(panel, "Progress", 0.9f);
            _progress = progress.rectTransform;
            UiKit.Place(_progress, Vector2.zero, new Vector2(1f, 0f), new Vector2(4f, 0f), new Vector2(0f, 2f));

            Plugin.Logger.LogInfo("Toast: UI built.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Toast: failed to build UI, notifications will be silent: {e}");
        }
    }

    /// Faint quads, each a bit larger than the last: where they overlap
    /// near the panel's edge they add up, so the glow is brightest there
    /// and fades out with distance.
    private void BuildGlow(RectTransform root)
    {
        var glow = UiKit.Stretch(UiKit.NewRect("Glow", root), 0f);
        _glowGroup = glow.gameObject.AddComponent<CanvasGroup>();
        _glowGroup.blocksRaycasts = false;
        _glowGroup.interactable = false;

        for (var i = 1; i <= GlowLayers; i++)
        {
            var layer = AccentQuad(glow, $"Glow{i}", GlowLayerAlpha);
            UiKit.Stretch(layer.rectTransform, i * GlowStep);
        }
    }

    /// 1 px accent outline: the panel's edge reads clearly against the
    /// glow instead of melting into it.
    private void BuildBorder(RectTransform panel)
    {
        const float alpha = 0.3f;

        var top = AccentQuad(panel, "BorderTop", alpha);
        UiKit.Place(top.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -1f), Vector2.zero);
        var bottom = AccentQuad(panel, "BorderBottom", alpha);
        UiKit.Place(bottom.rectTransform, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 1f));
        var right = AccentQuad(panel, "BorderRight", alpha);
        UiKit.Place(right.rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-1f, 0f), Vector2.zero);
    }

    /// A glowing gem: concentric diamonds (squares rotated 45°).
    private void BuildBadge(RectTransform panel)
    {
        var badge = UiKit.NewRect("Badge", panel);
        _badge = badge;
        badge.anchorMin = new Vector2(0f, 0.5f);
        badge.anchorMax = new Vector2(0f, 0.5f);
        badge.anchoredPosition = new Vector2(40f, 0f);
        badge.sizeDelta = Vector2.zero;
        badge.localRotation = Quaternion.Euler(0f, 0f, 45f);

        Diamond(AccentQuad(badge, "Halo", 0.12f), 32f);
        Diamond(AccentQuad(badge, "Outer", 1f), 26f);
        Diamond(UiKit.Quad(badge, "Inner", UiKit.PanelColor), 18f);
        Diamond(AccentQuad(badge, "Core", 1f), 9f);
    }

    /// Small diamonds, invisible until a rank-up animates them.
    private void BuildSparks(RectTransform panel)
    {
        for (var i = 0; i < SparkCount; i++)
        {
            var spark = UiKit.Quad(panel, $"Spark{i}", Color.clear);
            var rect = spark.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.sizeDelta = new Vector2(4f, 4f);
            rect.localRotation = Quaternion.Euler(0f, 0f, 45f);
            _sparks.Add(spark);
        }
    }

    private static void Diamond(Image image, float size)
    {
        var rect = image.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(size, size);
    }

    /// Quad recolored with each toast's accent, at a fixed opacity.
    private Image AccentQuad(Transform parent, string name, float alpha)
    {
        var color = DefaultAccent;
        color.a = alpha;
        var image = UiKit.Quad(parent, name, color);
        _accentParts.Add(new AccentPart { Image = image, Alpha = alpha });
        return image;
    }
}
