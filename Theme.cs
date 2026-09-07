using System;
using UnityEngine;

namespace ChorusMod;

/// Overlay theme.
///
/// IL2CPP CONSTRAINTS (observed when compiling against this build's interop
/// assemblies): the Texture2D(int,int) and RectOffset(int,int,int,int)
/// constructors weren't restored by Cpp2IL. So textures and RectOffsets
/// can't be constructed.
///
/// Workaround: only Texture2D.whiteTexture (a static property) tinted via
/// GUI.color is used for all solid-color fills, and GUIStyles are limited
/// to safe properties (fontSize, textColor, alignment).
public static class Theme
{
    public static bool Ready { get; private set; }
    public static bool Failed { get; private set; }

    /// GUI.color isn't necessarily usable (yet another method that might not
    /// be restored). Tested once on first render: if tinting fails, we
    /// paint by stacking GUI.Box instead.
    public static bool TintOk { get; private set; }

    // Dark palette, accents taken from Clone Hero's 5 frets.
    public static readonly Color Bg = new Color(0.07f, 0.08f, 0.10f, 0.96f);
    public static readonly Color RowEven = new Color(1f, 1f, 1f, 0.04f);
    public static readonly Color RowOdd = new Color(1f, 1f, 1f, 0.02f);
    public static readonly Color FieldBg = new Color(0f, 0f, 0f, 0.35f);
    public static readonly Color Accent = new Color(0.24f, 0.81f, 0.43f, 1f);
    public static readonly Color TextDim = new Color(0.62f, 0.66f, 0.72f, 1f);

    public static readonly Color Green = new Color(0.24f, 0.81f, 0.43f);
    public static readonly Color Red = new Color(0.94f, 0.35f, 0.35f);
    public static readonly Color Yellow = new Color(0.91f, 0.76f, 0.29f);
    public static readonly Color Blue = new Color(0.29f, 0.56f, 0.91f);
    public static readonly Color Orange = new Color(0.95f, 0.57f, 0.29f);

    public static GUIStyle Header = null!;
    public static GUIStyle Title = null!;
    public static GUIStyle Sub = null!;
    public static GUIStyle Meta = null!;
    public static GUIStyle Field = null!;
    public static GUIStyle Status = null!;

    /// Call from OnGUI: GUI.skin is only valid there.
    public static void EnsureInit()
    {
        if (Ready || Failed)
        {
            return;
        }

        try
        {
            Header = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            Header.normal.textColor = Accent;

            Title = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            Title.normal.textColor = Color.white;

            Sub = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            Sub.normal.textColor = TextDim;

            Meta = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleRight,
            };
            Meta.normal.textColor = TextDim;

            Status = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            Status.normal.textColor = TextDim;

            Field = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
            };
            Field.normal.textColor = Color.white;

            TintOk = ProbeTint();
            Plugin.Logger.LogInfo(
                $"GUI.color tint: {(TintOk ? "OK" : "broken -> falling back to stacked GUI.Box")}"
            );

            Ready = true;
        }
        catch (Exception e)
        {
            Failed = true;
            Plugin.Logger.LogWarning(
                $"Theme unavailable, using default Unity skin: {e.Message}"
            );
        }
    }

    private static bool ProbeTint()
    {
        try
        {
            // We only test GUI.color here: actual drawing now goes through
            // DrawImage() and its fallback cascade.
            var previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.5f);
            GUI.color = previous;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// Solid color fill.
    ///
    /// Normal path: whiteTexture tinted by GUI.color.
    /// Fallback (TintOk == false): stack translucent GUI.Box, which darkens
    /// progressively — no color, but with opacity.
    public static void Fill(Rect rect, Color color, int fallbackLayers = 3)
    {
        if (TintOk)
        {
            try
            {
                var previous = GUI.color;
                GUI.color = color;
                DrawImage(rect, Texture2D.whiteTexture);
                GUI.color = previous;

                // If no drawing mode worked, tinting is useless: switch
                // permanently to stacked Boxes.
                if (_imageMode == 6)
                {
                    TintOk = false;
                }
                else
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Won't retry anymore.
            }
        }

        try
        {
            for (var i = 0; i < fallbackLayers; i++)
            {
                GUI.Box(rect, GUIContent.none);
            }
        }
        catch (Exception)
        {
            // Nothing more to try.
        }
    }

    /// Draws a texture.
    ///
    /// GUI.DrawTexture turned out not to be restored on this build (it's
    /// this one, not GUI.color, that made the tint probe fail). So several
    /// overloads accepting a Texture directly are tried, and whichever one
    /// works is remembered so the cascade doesn't rerun every frame.
    /// -1 = to be determined, 6 = no method available.
    private static int _imageMode = -1;

    /// Disposable style whose background is swapped before each draw. This
    /// is the core of the workaround: GUI.Box knows how to draw a
    /// GUIStyle's background (that's what it does with the default skin),
    /// and that rendering path is indeed present in the binary — unlike
    /// direct entry points like GUI.DrawTexture.
    private static GUIStyle? _imageStyle;

    public static void DrawImage(Rect rect, Texture texture)
    {
        if (texture == null || _imageMode == 6)
        {
            return;
        }

        if (_imageMode == -1)
        {
            _imageMode = DetectImageMode(rect, texture);
            Plugin.Logger.LogInfo(
                $"Image rendering: mode {_imageMode} (0=GUI.DrawTexture, "
                    + "1=GUI.Label, 2=GUI.Box, 3=Graphics.DrawTexture, "
                    + "4=DrawTextureWithTexCoords, 5=GUIStyle background, "
                    + "6=unavailable)"
            );
        }

        try
        {
            switch (_imageMode)
            {
                case 0:
                    GUI.DrawTexture(rect, texture);
                    break;
                case 1:
                    GUI.Label(rect, texture);
                    break;
                case 2:
                    GUI.Box(rect, texture);
                    break;
                case 3:
                    Graphics.DrawTexture(rect, texture);
                    break;
                case 4:
                    GUI.DrawTextureWithTexCoords(rect, texture, new Rect(0f, 0f, 1f, 1f));
                    break;
                case 5:
                    DrawViaStyle(rect, texture);
                    break;
            }
        }
        catch (Exception)
        {
            _imageMode = 6;
        }
    }

    private static int DetectImageMode(Rect rect, Texture texture)
    {
        try
        {
            GUI.DrawTexture(rect, texture);
            return 0;
        }
        catch (Exception)
        {
            // next
        }

        try
        {
            GUI.Label(rect, texture);
            return 1;
        }
        catch (Exception)
        {
            // next
        }

        try
        {
            GUI.Box(rect, texture);
            return 2;
        }
        catch (Exception)
        {
            // next
        }

        // Graphics.DrawTexture is an icall (direct call into the native
        // engine). Icalls often survive managed stripping, unlike C#
        // wrappers like GUI.DrawTexture.
        try
        {
            Graphics.DrawTexture(rect, texture);
            return 3;
        }
        catch (Exception)
        {
            // next
        }

        try
        {
            GUI.DrawTextureWithTexCoords(rect, texture, new Rect(0f, 0f, 1f, 1f));
            return 4;
        }
        catch (Exception)
        {
            // next
        }

        try
        {
            DrawViaStyle(rect, texture);
            return 5;
        }
        catch (Exception)
        {
            return 6;
        }
    }

    /// Draws a texture by setting it as a GUIStyle's background, then
    /// displaying an empty GUI.Box with that style.
    private static void DrawViaStyle(Rect rect, Texture texture)
    {
        if (_imageStyle == null)
        {
            _imageStyle = new GUIStyle(GUI.skin.box);

            // Neutralize the 9-slice borders so the image isn't stretched
            // at the corners. RectOffset can't be constructed on this
            // build, but the existing instance can be modified.
            try
            {
                _imageStyle.border.left = 0;
                _imageStyle.border.right = 0;
                _imageStyle.border.top = 0;
                _imageStyle.border.bottom = 0;
                _imageStyle.overflow.left = 0;
                _imageStyle.overflow.right = 0;
                _imageStyle.overflow.top = 0;
                _imageStyle.overflow.bottom = 0;
            }
            catch (Exception)
            {
                // Too bad for the borders, the image is what matters.
            }
        }

        _imageStyle.normal.background = texture as Texture2D;
        GUI.Box(rect, GUIContent.none, _imageStyle);
    }

    public static Color InstrumentColor(string instrument) =>
        instrument switch
        {
            "guitar" => Green,
            "bass" => Red,
            "drums" => Yellow,
            "keys" => Blue,
            "vocals" => Orange,
            _ => TextDim,
        };
}
