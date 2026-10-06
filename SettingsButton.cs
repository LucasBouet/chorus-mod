using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChorusMod;

/// <summary>
/// Flat SETTINGS button under the main-menu player card, opening the
/// SettingsWindow. Sliders icon (three lines with a knob each) drawn from
/// quads, since no texture can be built on this build.
///
/// Clicks are hit-tested by hand (mouse position against the button's
/// screen rect) rather than through uGUI's EventSystem/Button.onClick:
/// no dependency on the game's EventSystem, and no managed delegates to
/// marshal into IL2CPP.
/// </summary>
public class SettingsButton
{
    private const float Width = 132f;
    private const float Height = 34f;
    private const float Gap = 10f;

    private static readonly Color Accent = Toast.DefaultAccent;
    private static readonly Color HoverBackground = new(0.10f, 0.12f, 0.17f, 0.96f);

    private RectTransform? _rect;
    private Image? _background;
    private readonly Image[] _border = new Image[4];
    private TextMeshProUGUI? _label;

    /// Built under the card so it slides and fades along with it.
    public void Build(RectTransform card, TMP_FontAsset? font)
    {
        _rect = UiKit.NewRect("SettingsButton", card);
        _rect.anchorMin = Vector2.zero;
        _rect.anchorMax = Vector2.zero;
        _rect.pivot = new Vector2(0f, 1f);
        _rect.anchoredPosition = new Vector2(0f, -Gap);
        _rect.sizeDelta = new Vector2(Width, Height);

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

        // Sliders icon: knobs at different positions on three tracks.
        var icon = UiKit.NewRect("Icon", _rect);
        icon.anchorMin = new Vector2(0f, 0.5f);
        icon.anchorMax = new Vector2(0f, 0.5f);
        icon.anchoredPosition = new Vector2(22f, 0f);
        icon.sizeDelta = new Vector2(16f, 14f);
        float[] knobs = { 0.3f, 0.75f, 0.45f };
        for (var i = 0; i < knobs.Length; i++)
        {
            var lineY = 5f - i * 5f;
            var line = UiKit.Quad(icon, $"Line{i}", UiKit.WithAlpha(Color.white, 0.55f)).rectTransform;
            line.anchorMin = new Vector2(0.5f, 0.5f);
            line.anchorMax = new Vector2(0.5f, 0.5f);
            line.anchoredPosition = new Vector2(0f, lineY);
            line.sizeDelta = new Vector2(16f, 1.5f);

            var knob = UiKit.Quad(icon, $"Knob{i}", Accent).rectTransform;
            knob.anchorMin = new Vector2(0.5f, 0.5f);
            knob.anchorMax = new Vector2(0.5f, 0.5f);
            knob.anchoredPosition = new Vector2((knobs[i] - 0.5f) * 16f, lineY);
            knob.sizeDelta = new Vector2(4f, 4f);
        }

        _label = UiKit.Text(_rect, "Label", font, 12f, FontStyles.Bold, UiKit.MutedText);
        _label.text = "SETTINGS";
        _label.characterSpacing = 4f;
        UiKit.Place(_label.rectTransform, Vector2.zero, Vector2.one, new Vector2(38f, 0f), new Vector2(-8f, 0f));

        SetHover(false);
    }

    /// Call every frame. `interactive` = the card is fully on screen.
    public void Tick(bool interactive)
    {
        if (_rect == null)
        {
            return;
        }

        var hovered = interactive
            && !SettingsWindow.IsOpen
            && !DuelWindow.IsOpen
            && RectTransformUtility.RectangleContainsScreenPoint(_rect, Input.mousePosition, null);
        SetHover(hovered);

        if (hovered && Input.GetMouseButtonDown(0))
        {
            SettingsWindow.Open();
        }
    }

    private void SetHover(bool hovered)
    {
        if (_background == null || _label == null)
        {
            return;
        }

        _background.color = hovered ? HoverBackground : UiKit.PanelColor;
        _label.color = hovered ? Color.white : UiKit.MutedText;
        var border = UiKit.WithAlpha(Accent, hovered ? 0.7f : 0.25f);
        foreach (var edge in _border)
        {
            edge.color = border;
        }
    }
}
