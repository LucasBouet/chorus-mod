using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChorusMod;

/// <summary>
/// Custom toast notification -- big title, smaller message, fades in,
/// holds, fades out. Queues multiple requests and shows them one at a
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
/// (sortingOrder above everything else), a background Image with no
/// sprite assigned -- Unity renders that as a flat colored quad, which
/// sidesteps Texture2D/Sprite construction being unavailable on this
/// build -- and two TMP_Text elements. Fade timing is a plain Update()
/// state machine rather than a coroutine, consistent with the rest of
/// the plugin's IL2CPP-safe style.
/// </summary>
public class Toast : MonoBehaviour
{
    public Toast(IntPtr ptr) : base(ptr) { }

    private struct Request
    {
        public string Title;
        public string Message;
        public float DurationSeconds;
    }

    private enum State
    {
        Idle,
        FadingIn,
        Holding,
        FadingOut,
    }

    private const float PanelWidth = 360f;
    private const float PanelHeight = 72f;
    private const float FadeSeconds = 0.25f;
    private const float DefaultDurationSeconds = 3.5f;

    private static Toast? _instance;

    private bool _built;
    private CanvasGroup? _canvasGroup;
    private TextMeshProUGUI? _titleText;
    private TextMeshProUGUI? _messageText;

    private readonly Queue<Request> _queue = new();
    private State _state = State.Idle;
    private float _timer;
    private float _currentDuration;

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

    /// Call from anywhere already on the Unity main thread -- ChorusUI's
    /// download/sync callbacks already marshal back via _mainThread
    /// before touching anything Unity-related, so this doesn't need its
    /// own thread-safety on top of that.
    public static void Show(string title, string message, float durationSeconds = DefaultDurationSeconds)
    {
        if (_instance == null)
        {
            Plugin.Logger.LogWarning("Toast: Show() called before Initialize().");
            return;
        }

        _instance._queue.Enqueue(
            new Request { Title = title, Message = message, DurationSeconds = durationSeconds }
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

        switch (_state)
        {
            case State.Idle:
                if (_queue.Count > 0)
                {
                    StartNext();
                }
                break;

            case State.FadingIn:
                _timer += Time.deltaTime;
                var fadeInT = Mathf.Clamp01(_timer / FadeSeconds);
                SetAlpha(fadeInT);
                if (fadeInT >= 1f)
                {
                    _timer = 0f;
                    _state = State.Holding;
                }
                break;

            case State.Holding:
                _timer += Time.deltaTime;
                if (_timer >= _currentDuration)
                {
                    _timer = 0f;
                    _state = State.FadingOut;
                }
                break;

            case State.FadingOut:
                _timer += Time.deltaTime;
                var fadeOutT = 1f - Mathf.Clamp01(_timer / FadeSeconds);
                SetAlpha(fadeOutT);
                if (fadeOutT <= 0f)
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

    private void StartNext()
    {
        if (_queue.Count == 0 || _titleText == null || _messageText == null)
        {
            return;
        }

        var request = _queue.Dequeue();
        _titleText.text = request.Title;
        _messageText.text = request.Message;
        _currentDuration = request.DurationSeconds;
        _timer = 0f;
        _state = State.FadingIn;
    }

    private void BuildUI()
    {
        try
        {
            var canvasObject = new GameObject("ChorusMod.ToastCanvas");
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000; // above FadeCanvas (100) and everything else

            var panel = new GameObject("Panel");
            panel.transform.SetParent(canvasObject.transform, false);

            var rect = panel.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-24f, -24f);
            rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);

            _canvasGroup = panel.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            // No sprite assigned: Unity draws an Image with a color set
            // and no sprite as a flat colored quad. Avoids needing to
            // construct a Texture2D/Sprite -- both unavailable here.
            var background = panel.AddComponent<Image>();
            background.color = new Color(0.05f, 0.05f, 0.07f, 0.92f);
            background.raycastTarget = false;

            var font = FindGameFont();

            var titleObject = new GameObject("Title");
            titleObject.transform.SetParent(panel.transform, false);
            var titleRect = titleObject.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 0.5f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.offsetMin = new Vector2(16f, 0f);
            titleRect.offsetMax = new Vector2(-16f, -8f);
            _titleText = titleObject.AddComponent<TextMeshProUGUI>();
            _titleText.fontSize = 20f;
            _titleText.fontStyle = FontStyles.Bold;
            _titleText.alignment = TextAlignmentOptions.MidlineLeft;
            _titleText.color = Color.white;
            if (font != null)
            {
                _titleText.font = font;
            }

            var messageObject = new GameObject("Message");
            messageObject.transform.SetParent(panel.transform, false);
            var messageRect = messageObject.AddComponent<RectTransform>();
            messageRect.anchorMin = new Vector2(0f, 0f);
            messageRect.anchorMax = new Vector2(1f, 0.5f);
            messageRect.offsetMin = new Vector2(16f, 8f);
            messageRect.offsetMax = new Vector2(-16f, 0f);
            _messageText = messageObject.AddComponent<TextMeshProUGUI>();
            _messageText.fontSize = 14f;
            _messageText.alignment = TextAlignmentOptions.MidlineLeft;
            _messageText.color = new Color(0.75f, 0.78f, 0.85f, 1f);
            if (font != null)
            {
                _messageText.font = font;
            }

            Plugin.Logger.LogInfo("Toast: UI built.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Toast: failed to build UI, notifications will be silent: {e}");
        }
    }

    /// Borrows a font asset from an existing TMP text already in the
    /// scene, rather than depending on TMP_Settings.defaultFontAsset
    /// being set for this build. Must be called BEFORE adding our own
    /// TextMeshProUGUI components, otherwise it would find one of those
    /// instead (confirmed by testing).
    private static TMP_FontAsset? FindGameFont()
    {
        try
        {
            var existing = UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>();
            return existing != null ? existing.font : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
