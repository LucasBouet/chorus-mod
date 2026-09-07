using System;
using UnityEngine;

namespace ChorusMod;

/// Thème de l'overlay.
///
/// CONTRAINTES IL2CPP (constatées à la compilation contre les interop
/// assemblies de cette build) : les constructeurs Texture2D(int,int) et
/// RectOffset(int,int,int,int) n'ont pas été restaurés par Cpp2IL. On ne
/// peut donc PAS fabriquer de textures ni de RectOffset.
///
/// Parade : on n'utilise que Texture2D.whiteTexture (propriété statique)
/// teintée via GUI.color pour tous les aplats de couleur, et les GUIStyle
/// se limitent aux propriétés sûres (fontSize, textColor, alignment).
public static class Theme
{
    public static bool Ready { get; private set; }
    public static bool Failed { get; private set; }

    /// GUI.color n'est pas forcément utilisable (encore une méthode
    /// potentiellement non restaurée). Testé une fois au premier rendu :
    /// si la teinte échoue, on peint en empilant des GUI.Box à la place.
    public static bool TintOk { get; private set; }

    // Palette sombre, accents repris des 5 frets de Clone Hero.
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

    /// À appeler depuis OnGUI : GUI.skin n'est valide que là.
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
                $"Teinte GUI.color : {(TintOk ? "OK" : "HS -> repli sur GUI.Box empilées")}"
            );

            Ready = true;
        }
        catch (Exception e)
        {
            Failed = true;
            Plugin.Logger.LogWarning(
                $"Thème indisponible, skin Unity par défaut : {e.Message}"
            );
        }
    }

    private static bool ProbeTint()
    {
        try
        {
            // On teste GUI.color seul : le dessin, lui, passe désormais par
            // DrawImage() et sa cascade.
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

    /// Aplat de couleur.
    ///
    /// Voie normale : whiteTexture teintée par GUI.color.
    /// Repli (TintOk == false) : on empile des GUI.Box translucides, ce qui
    /// assombrit progressivement — sans couleur, mais avec de l'opacité.
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

                // Si aucun mode de dessin n'a fonctionné, la teinte ne sert
                // à rien : on bascule définitivement sur les Box empilées.
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
                // On ne réessaiera plus.
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
            // Rien de plus à tenter.
        }
    }

    /// Dessine une texture.
    ///
    /// GUI.DrawTexture s'est révélé non restauré sur cette build (c'est lui,
    /// et non GUI.color, qui faisait échouer la sonde de teinte). On tente
    /// donc plusieurs surcharges acceptant directement une Texture, et on
    /// mémorise celle qui passe pour ne pas relancer la cascade à chaque
    /// frame.
    /// -1 = à déterminer, 6 = aucune méthode disponible.
    private static int _imageMode = -1;

    /// Style jetable dont on remplace le fond avant chaque dessin.
    /// C'est le cœur du contournement : GUI.Box sait dessiner le fond d'un
    /// GUIStyle (c'est ce qu'elle fait avec le skin par défaut), et ce
    /// chemin de rendu est bien présent dans le binaire — contrairement
    /// aux points d'entrée directs type GUI.DrawTexture.
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
                $"Affichage d'images : mode {_imageMode} (0=GUI.DrawTexture, "
                    + "1=GUI.Label, 2=GUI.Box, 3=Graphics.DrawTexture, "
                    + "4=DrawTextureWithTexCoords, 5=fond de GUIStyle, "
                    + "6=indisponible)"
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
            // suivant
        }

        try
        {
            GUI.Label(rect, texture);
            return 1;
        }
        catch (Exception)
        {
            // suivant
        }

        try
        {
            GUI.Box(rect, texture);
            return 2;
        }
        catch (Exception)
        {
            // suivant
        }

        // Graphics.DrawTexture est un icall (appel direct au moteur natif).
        // Les icalls survivent souvent au stripping managé, contrairement
        // aux wrappers C# comme GUI.DrawTexture.
        try
        {
            Graphics.DrawTexture(rect, texture);
            return 3;
        }
        catch (Exception)
        {
            // suivant
        }

        try
        {
            GUI.DrawTextureWithTexCoords(rect, texture, new Rect(0f, 0f, 1f, 1f));
            return 4;
        }
        catch (Exception)
        {
            // suivant
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

    /// Dessine une texture en la posant comme fond d'un GUIStyle, puis en
    /// affichant une GUI.Box vide avec ce style.
    private static void DrawViaStyle(Rect rect, Texture texture)
    {
        if (_imageStyle == null)
        {
            _imageStyle = new GUIStyle(GUI.skin.box);

            // On neutralise les bordures 9-slice pour que l'image ne soit
            // pas étirée en coins. RectOffset ne peut pas être construit
            // sur cette build, mais on peut modifier l'instance existante.
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
                // Tant pis pour les bordures, l'essentiel est l'image.
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
