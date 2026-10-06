using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChorusMod;

/// <summary>
/// NOTIFS button right under the DUELS button, opening the
/// NotificationWindow. Bell icon from quads (no texture can be built on
/// this build), and a badge with the number of notifications never seen
/// (toast off or missed while streaming).
///
/// Clicks are hit-tested by hand, like SettingsButton.
/// </summary>
public class HistoryButton
{
    private const float Width = 220f;
    private const float Height = 64f;
    private const float ScreenMargin = 36f;
    private const float Gap = 12f;
    private const float BadgeSize = 28f;

    // Top of this button: under the DUELS button, centered on the screen.
    private const float DuelButtonHalfHeight = 32f;

    private static readonly Color Accent = Toast.DefaultAccent;
    private static readonly Color HoverBackground = new(0.10f, 0.12f, 0.17f, 0.96f);

    private RectTransform? _rect;
    private CanvasGroup? _group;
    private Image? _background;
    private readonly Image[] _border = new Image[4];
    private readonly Image[] _bell = new Image[3];
    private TextMeshProUGUI? _label;
    private RectTransform? _badge;
    private TextMeshProUGUI? _badgeText;

    public void Build(Transform canvas, TMP_FontAsset? font)
    {
        _rect = UiKit.NewRect("HistoryButton", canvas);
        _rect.anchorMin = new Vector2(1f, 0.5f);
        _rect.anchorMax = new Vector2(1f, 0.5f);
        _rect.pivot = new Vector2(1f, 1f);
        _rect.anchoredPosition = new Vector2(-ScreenMargin, -DuelButtonHalfHeight - Gap);
        _rect.sizeDelta = new Vector2(Width, Height);

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
        UiKit.Place(_border[2].rectTransform, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(3f, 0f));
        _border[3] = UiKit.Quad(_rect, "BorderRight", Color.clear);
        UiKit.Place(_border[3].rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-1f, 0f), Vector2.zero);

        // Bell: dome, wider rim, clapper below.
        var icon = UiKit.NewRect("Icon", _rect);
        icon.anchorMin = new Vector2(0f, 0.5f);
        icon.anchorMax = new Vector2(0f, 0.5f);
        icon.anchoredPosition = new Vector2(40f, 0f);
        icon.sizeDelta = new Vector2(32f, 32f);
        _bell[0] = BellPart(icon, "Dome", new Vector2(0f, 4f), new Vector2(16f, 18f));
        _bell[1] = BellPart(icon, "Rim", new Vector2(0f, -7f), new Vector2(28f, 4f));
        _bell[2] = BellPart(icon, "Clapper", new Vector2(0f, -12f), new Vector2(7f, 4f));

        _label = UiKit.Text(_rect, "Label", font, 19f, FontStyles.Bold, UiKit.MutedText);
        _label.text = "NOTIFS";
        _label.characterSpacing = 6f;
        UiKit.Place(_label.rectTransform, Vector2.zero, Vector2.one, new Vector2(74f, 0f), new Vector2(-8f, 0f));

        // Unseen badge on the top-left corner.
        _badge = UiKit.NewRect("Badge", _rect);
        _badge.anchorMin = new Vector2(0f, 1f);
        _badge.anchorMax = new Vector2(0f, 1f);
        _badge.anchoredPosition = Vector2.zero;
        _badge.sizeDelta = new Vector2(BadgeSize, BadgeSize);
        var badgeBack = UiKit.Quad(_badge, "Back", Accent);
        UiKit.Stretch(badgeBack.rectTransform, 0f);
        _badgeText = UiKit.Text(_badge, "Count", font, 15f, FontStyles.Bold, Color.white);
        _badgeText.alignment = TextAlignmentOptions.Center;
        UiKit.Stretch(_badgeText.rectTransform, 0f);
        _badge.gameObject.SetActive(false);

        SetLook(false, 0);
    }

    private static Image BellPart(RectTransform icon, string name, Vector2 position, Vector2 size)
    {
        var part = UiKit.Quad(icon, name, Color.white);
        var rect = part.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return part;
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

        var interactive = shown >= 1f && !SettingsWindow.IsOpen && !DuelWindow.IsOpen && !NotificationWindow.IsOpen;
        var hovered = interactive
            && RectTransformUtility.RectangleContainsScreenPoint(_rect, Input.mousePosition, null);

        if (hovered && Input.GetMouseButtonDown(0))
        {
            NotificationWindow.Open();
        }

        SetLook(hovered, shown > 0f ? NotificationHistory.Unseen : 0);
    }

    private void SetLook(bool hovered, int unseen)
    {
        if (_background == null || _label == null || _badge == null || _badgeText == null)
        {
            return;
        }

        _background.color = hovered ? HoverBackground : UiKit.PanelColor;
        _label.color = hovered ? Color.white : UiKit.MutedText;
        foreach (var part in _bell)
        {
            part.color = hovered ? Color.white : UiKit.MutedText;
        }

        var alpha = hovered ? 0.9f : unseen > 0 ? 0.55f : 0.25f;
        for (var i = 0; i < _border.Length; i++)
        {
            // The left edge is a thick accent stripe.
            _border[i].color = UiKit.WithAlpha(Accent, i == 2 ? Mathf.Max(alpha, 0.8f) : alpha);
        }

        var showBadge = unseen > 0;
        if (_badge.gameObject.activeSelf != showBadge)
        {
            _badge.gameObject.SetActive(showBadge);
        }

        if (showBadge)
        {
            _badgeText.text = unseen > 9 ? "9+" : unseen.ToString();
        }
    }
}
