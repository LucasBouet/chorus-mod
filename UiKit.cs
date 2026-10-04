using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChorusMod;

/// <summary>
/// uGUI building blocks shared by Toast and the main-menu player card.
///
/// Texture2D/Sprite construction is unavailable on this build, so every
/// shape is an Image with no sprite (drawn as a flat colored quad) and
/// every visual effect is built out of those.
/// </summary>
public static class UiKit
{
    public static readonly Color PanelColor = new(0.05f, 0.06f, 0.09f, 0.96f);
    public static readonly Color MutedText = new(0.72f, 0.75f, 0.82f, 1f);

    private static TMP_FontAsset? _gameFont;

    /// Keeps a canvas the same apparent size from 720p to 4K. Optional:
    /// without it the content is just fixed-pixel, so a failure is
    /// logged and ignored.
    public static void AddScaler(GameObject canvasObject)
    {
        try
        {
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"UI: no CanvasScaler, fixed pixel size: {e.Message}");
        }
    }

    public static RectTransform NewRect(string name, Transform parent)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        return obj.AddComponent<RectTransform>();
    }

    /// Fills the parent, grown by `outset` on every side.
    public static RectTransform Stretch(RectTransform rect, float outset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(-outset, -outset);
        rect.offsetMax = new Vector2(outset, outset);
        return rect;
    }

    public static void Place(
        RectTransform rect,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 offsetMin,
        Vector2 offsetMax
    )
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    /// No sprite assigned: Unity draws an Image with a color set and no
    /// sprite as a flat colored quad.
    public static Image Quad(Transform parent, string name, Color color)
    {
        var image = NewRect(name, parent).gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    public static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }

    public static TextMeshProUGUI Text(
        Transform parent,
        string name,
        TMP_FontAsset? font,
        float size,
        FontStyles style,
        Color color
    )
    {
        var text = NewRect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null)
        {
            text.font = font;
        }

        text.fontSize = size;
        text.fontStyle = style;
        text.color = color;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.raycastTarget = false;
        return text;
    }

    /// Borrows a font asset from an existing TMP text already in the
    /// scene, rather than depending on TMP_Settings.defaultFontAsset
    /// being set for this build. The first call must happen BEFORE any
    /// of our own TextMeshProUGUI components exist, otherwise it would
    /// find one of those instead (confirmed by testing) -- hence the
    /// cache, so whichever UI builds second reuses the first's result.
    public static TMP_FontAsset? FindGameFont()
    {
        if (_gameFont != null)
        {
            return _gameFont;
        }

        try
        {
            var existing = UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>();
            _gameFont = existing != null ? existing.font : null;
        }
        catch (Exception)
        {
            _gameFont = null;
        }

        return _gameFont;
    }
}
