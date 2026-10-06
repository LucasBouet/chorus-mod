using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChorusMod;

/// <summary>
/// Close-the-game cross in the top-right corner of the main menu, shown
/// and faded along with the player card. Two clicks: the first one arms
/// it (turns red, "CLICK AGAIN TO QUIT" next to it), the second, within a
/// few seconds, quits; a stray click never closes the game.
///
/// The cross is two thin quads rotated ±45° (no texture can be built on
/// this build). Clicks are hit-tested by hand, like SettingsButton.
/// </summary>
public class QuitButton
{
    private const float Size = 40f;
    private const float ScreenMargin = 36f;
    private const float ConfirmSeconds = 3f;

    private static readonly Color Danger = new(0.94f, 0.30f, 0.32f);
    private static readonly Color HoverBackground = new(0.10f, 0.12f, 0.17f, 0.96f);

    private RectTransform? _rect;
    private CanvasGroup? _group;
    private Image? _background;
    private readonly Image[] _border = new Image[4];
    private readonly Image[] _cross = new Image[2];
    private TextMeshProUGUI? _confirmText;

    private float _armedUntil = float.NegativeInfinity;

    private bool Armed => Time.unscaledTime < _armedUntil;

    public void Build(Transform canvas, TMP_FontAsset? font)
    {
        _rect = UiKit.NewRect("QuitButton", canvas);
        _rect.anchorMin = Vector2.one;
        _rect.anchorMax = Vector2.one;
        _rect.pivot = Vector2.one;
        _rect.anchoredPosition = new Vector2(-ScreenMargin, -ScreenMargin);
        _rect.sizeDelta = new Vector2(Size, Size);

        _group = _rect.gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _group.interactable = false;

        _background = UiKit.Quad(_rect, "Background", UiKit.PanelColor);
        UiKit.Stretch(_background.rectTransform, 0f);

        _border[0] = UiKit.Quad(_rect, "BorderTop", Color.clear);
        UiKit.Place(_border[0].rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -1f), Vector2.zero);
        _border[1] = UiKit.Quad(_rect, "BorderBottom", Color.clear);
        UiKit.Place(_border[1].rectTransform, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 1f));
        _border[2] = UiKit.Quad(_rect, "BorderLeft", Color.clear);
        UiKit.Place(_border[2].rectTransform, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(1f, 0f));
        _border[3] = UiKit.Quad(_rect, "BorderRight", Color.clear);
        UiKit.Place(_border[3].rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-1f, 0f), Vector2.zero);

        for (var i = 0; i < _cross.Length; i++)
        {
            _cross[i] = UiKit.Quad(_rect, $"Cross{i}", Color.white);
            var bar = _cross[i].rectTransform;
            bar.anchorMin = new Vector2(0.5f, 0.5f);
            bar.anchorMax = new Vector2(0.5f, 0.5f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(18f, 2f);
            bar.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
        }

        // Left of the button, only while armed.
        _confirmText = UiKit.Text(_rect, "Confirm", font, 12f, FontStyles.Bold, Danger);
        _confirmText.text = "CLICK AGAIN TO QUIT";
        _confirmText.characterSpacing = 4f;
        _confirmText.alignment = TextAlignmentOptions.MidlineRight;
        var confirm = _confirmText.rectTransform;
        confirm.anchorMin = new Vector2(0f, 0.5f);
        confirm.anchorMax = new Vector2(0f, 0.5f);
        confirm.pivot = new Vector2(1f, 0.5f);
        confirm.anchoredPosition = new Vector2(-12f, 0f);
        confirm.sizeDelta = new Vector2(260f, Size);

        SetLook(false);
    }

    /// Call every frame. `shown` = the card's fade (0-1); clickable only
    /// once fully shown.
    public void Tick(float shown)
    {
        if (_rect == null || _group == null)
        {
            return;
        }

        _group.alpha = shown;
        var interactive = shown >= 1f && !SettingsWindow.IsOpen && !DuelWindow.IsOpen;
        if (!interactive)
        {
            _armedUntil = float.NegativeInfinity;
        }

        var hovered = interactive
            && RectTransformUtility.RectangleContainsScreenPoint(_rect, Input.mousePosition, null);

        if (hovered && Input.GetMouseButtonDown(0))
        {
            if (Armed)
            {
                Plugin.Logger.LogInfo("Quit button: closing the game.");
                Application.Quit();
            }
            else
            {
                _armedUntil = Time.unscaledTime + ConfirmSeconds;
            }
        }

        SetLook(hovered);
    }

    private void SetLook(bool hovered)
    {
        if (_background == null || _confirmText == null)
        {
            return;
        }

        var armed = Armed;
        _background.color = armed
            ? UiKit.WithAlpha(Danger, 0.9f)
            : hovered ? HoverBackground : UiKit.PanelColor;

        var cross = armed || hovered ? Color.white : UiKit.MutedText;
        foreach (var bar in _cross)
        {
            bar.color = cross;
        }

        var border = UiKit.WithAlpha(Danger, armed ? 1f : hovered ? 0.7f : 0.25f);
        foreach (var edge in _border)
        {
            edge.color = border;
        }

        _confirmText.enabled = armed;
    }
}
