using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ChorusMod;

// APIS IMGUI UTILISABLES SUR CETTE BUILD IL2CPP
// ----------------------------------------------
// Beaucoup de méthodes UnityEngine n'ont pas été restaurées par Cpp2IL et
// lèvent "NotSupportedException: Method unstripping failed" à l'appel.
// Constaté au fil des tests :
//   OK : GUI.Box, GUI.Label, GUI.Button,
//        GUI.BeginGroup/EndGroup, Texture2D.whiteTexture
//   HS : GUI.TextField, GUI.DrawTexture, GUILayout.Window, GUILayout.Space,
//        Resources.FindObjectsOfTypeAll,
//        constructeurs Texture2D(int,int) et RectOffset(int,int,int,int)
//
// Conclusion : cette UI n'utilise QUE des GUI.* à rectangles absolus.
// Pas de GUILayout du tout — le défilement de la liste est fait à la main
// (offset + GUI.BeginGroup pour le clipping).
public class ChorusUI : MonoBehaviour
{
    public ChorusUI(IntPtr ptr) : base(ptr) { }

    private const float Pad = 16f;
    private const float BtnH = 28f;
    private const float ArtSize = 56f;

    // Taille réglable dans le .cfg : l'overlay doit rester lisible aussi
    // bien en 1080p qu'en 4K.
    private static float PanelW => Plugin.PanelWidth.Value;
    private static float PanelH => Plugin.PanelHeight.Value;
    private static float RowH => Plugin.ShowAlbumArt.Value ? 68f : 46f;

    private static readonly string[] Instruments =
    {
        "guitar",
        "bass",
        "drums",
        "keys",
        "vocals",
        "null",
    };

    /// Valeurs acceptées par le champ "difficulty" de l'API (vérifiées :
    /// chacune change bien les résultats renvoyés).
    private static readonly string[] Difficulties =
    {
        "null",
        "easy",
        "medium",
        "hard",
        "expert",
    };

    /// Clé d'API -> libellé. Vide = recherche libre (endpoint /search) ;
    /// les autres ciblent un champ via /search/advanced.
    private static readonly string[] FieldKeys =
    {
        "",
        "name",
        "artist",
        "album",
        "genre",
        "charter",
    };

    /// Drapeaux tri-état de /search/advanced (clé API, libellé affiché).
    /// Ordre repris de la page enchor.us pour ne pas dérouter.
    private static readonly string[] FlagKeys =
    {
        "hasForcedNotes",
        "hasSoloSections",
        "hasIssues",
        "hasRollLanes",
        "hasOpenNotes",
        "hasLyrics",
        "hasVideoBackground",
        "has2xKick",
        "hasTapNotes",
        "hasVocals",
        "modchart",
    };

    private static readonly string[] FlagLabels =
    {
        "forced",
        "solo",
        "issues",
        "roll lanes",
        "open notes",
        "lyrics",
        "video bg",
        "2x kick",
        "tap notes",
        "vocals",
        "modchart",
    };

    private static readonly string[] FieldLabels =
    {
        "partout",
        "titre",
        "artiste",
        "album",
        "genre",
        "charter",
    };

    private bool _open;

    private string _query = "";
    private string _instrument = "guitar";
    private string _difficulty = "null";
    private string _field = "";
    private int _found = -1;
    private bool _flagsOpen;

    /// null = indifférent, true = exiger, false = exclure.
    private readonly Dictionary<string, bool?> _flags = new();
    private int _page = 1;
    private string _status = "Tape une recherche puis Entrée.";
    private bool _busy;

    private readonly List<SongResult> _results = new();
    private float _scrollY;

    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly AlbumArtCache _art = new();

    private bool _cursorWasVisible;
    private CursorLockMode _previousLockState;

    private void Update()
    {
        if (_open)
        {
            _art.Pump();
        }

        while (_mainThread.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Action différée en échec : {e}");
            }
        }

        if (Input.GetKeyDown(Plugin.ToggleKey.Value))
        {
            Toggle();
        }
    }

    private void Toggle()
    {
        _open = !_open;

        if (_open)
        {
            _instrument = Plugin.Instrument.Value;
            _cursorWasVisible = Cursor.visible;
            _previousLockState = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            SetGameInputEnabled(false);
        }
        else
        {
            Cursor.visible = _cursorWasVisible;
            Cursor.lockState = _previousLockState;
            SetGameInputEnabled(true);
        }
    }

    /// Coupe UNIQUEMENT le contrôleur clavier de Rewired pendant la saisie
    /// (voir InputBlocker pour le détail de ce qui a été essayé avant).
    private void SetGameInputEnabled(bool enabled)
    {
        // Deux couches complémentaires :
        //  - Rewired pour les entrées manette/clavier passant par ses maps
        //  - le patch Harmony sur Input.GetKey* pour l'Input legacy d'Unity,
        //    d'où venait le Control Remapper sur Espace.
        InputBlocker.SetKeyboardEnabled(enabled);
        InputPatches.SwallowKeys = !enabled;
    }

    private void OnDestroy()
    {
        if (_open)
        {
            SetGameInputEnabled(true);
        }
    }

    private void OnGUI()
    {
        if (!_open)
        {
            return;
        }

        Theme.EnsureInit();

        try
        {
            DrawPanel();
        }
        catch (Exception e)
        {
            _open = false;
            SetGameInputEnabled(true);
            Plugin.Logger.LogError($"Rendu de l'UI impossible, panneau désactivé : {e}");
        }
    }

    // ---------------------------------------------------------------
    //  Saisie clavier maison (GUI.TextField n'existe pas ici)
    // ---------------------------------------------------------------
    private void HandleTextInput()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
        {
            return;
        }

        if (e.keyCode == KeyCode.Backspace)
        {
            if (_query.Length > 0)
            {
                _query = _query.Substring(0, _query.Length - 1);
            }
            Consume(e);
            return;
        }

        if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
        {
            Search(1);
            Consume(e);
            return;
        }

        if (e.keyCode == KeyCode.Escape)
        {
            Toggle();
            Consume(e);
            return;
        }

        if (e.keyCode == Plugin.ToggleKey.Value)
        {
            return; // géré dans Update()
        }

        var c = e.character;
        if (!char.IsControl(c) && c != '\0')
        {
            _query += c;
            Consume(e);
        }
    }

    private static void Consume(Event e)
    {
        try
        {
            e.Use();
        }
        catch (Exception)
        {
            // Sans Use(), le blocage passe de toute façon par Rewired.
        }
    }

    // ---------------------------------------------------------------
    //  Rendu (100 % GUI.* en coordonnées absolues)
    // ---------------------------------------------------------------
    private void DrawPanel()
    {
        HandleTextInput();

        var panel = new Rect(
            Mathf.Round((Screen.width - PanelW) * 0.5f),
            Mathf.Round((Screen.height - PanelH) * 0.5f),
            PanelW,
            PanelH
        );

        // 8 couches en repli = fond quasi opaque. Si la teinte GUI.color
        // fonctionne, une seule passe suffit et le paramètre est ignoré.
        Theme.Fill(panel, Theme.Bg, Plugin.PanelOpacityLayers.Value);

        var x = panel.x + Pad;
        var w = PanelW - (Pad * 2f);
        var y = panel.y + 10f;

        // --- En-tête ---
        GUI.Label(new Rect(x, y, 300f, 22f), "CHORUS MOD", S(Theme.Header));
        if (GUI.Button(new Rect(panel.xMax - Pad - 80f, y, 80f, 22f), "Fermer"))
        {
            Toggle();
            return;
        }
        y += 30f;

        // --- Recherche ---
        var fieldW = w - 240f;
        var fieldRect = new Rect(x, y, fieldW, BtnH);
        Theme.Fill(fieldRect, Theme.FieldBg, 2);
        var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
        var shown = string.IsNullOrEmpty(_query)
            ? "Rechercher un artiste, un titre, un charter…"
            : _query + caret;
        GUI.Label(new Rect(fieldRect.x + 8f, fieldRect.y, fieldRect.width - 12f, BtnH),
            shown, S(Theme.Field));

        var bx = x + fieldW + 8f;
        if (GUI.Button(new Rect(bx, y, 96f, BtnH), _busy ? "…" : "Chercher"))
        {
            Search(1);
        }
        if (GUI.Button(new Rect(bx + 100f, y, 66f, BtnH), "Coller"))
        {
            PasteFromClipboard();
        }
        if (GUI.Button(new Rect(bx + 170f, y, 62f, BtnH), "Vider"))
        {
            _query = "";
        }
        y += BtnH + 8f;

        // --- Filtres instrument ---
        GUI.Label(new Rect(x, y, 80f, 20f), "Instrument", S(Theme.Sub));
        var fx = x + 82f;
        foreach (var instrument in Instruments)
        {
            var selected = _instrument == instrument;
            var label = instrument == "null" ? "tous" : instrument;
            if (selected)
            {
                Theme.Fill(new Rect(fx, y, 66f, 20f), Theme.Accent, 2);
            }

            if (GUI.Button(new Rect(fx, y, 66f, 20f), label) && !selected)
            {
                _instrument = instrument;
                Search(1);
            }

            fx += 70f;
        }
        y += 24f;

        // --- Filtre difficulté ---
        GUI.Label(new Rect(x, y, 80f, 20f), "Difficulté", S(Theme.Sub));
        fx = x + 82f;
        foreach (var difficulty in Difficulties)
        {
            var selected = _difficulty == difficulty;
            var label = difficulty == "null" ? "toutes" : difficulty;
            if (selected)
            {
                Theme.Fill(new Rect(fx, y, 66f, 20f), Theme.Accent, 2);
            }

            if (GUI.Button(new Rect(fx, y, 66f, 20f), label) && !selected)
            {
                _difficulty = difficulty;
                Search(1);
            }

            fx += 70f;
        }
        y += 24f;

        // --- Champ ciblé par la recherche ---
        GUI.Label(new Rect(x, y, 80f, 20f), "Chercher", S(Theme.Sub));
        fx = x + 82f;
        for (var i = 0; i < FieldKeys.Length; i++)
        {
            var selected = _field == FieldKeys[i];
            if (selected)
            {
                Theme.Fill(new Rect(fx, y, 66f, 20f), Theme.Accent, 2);
            }

            if (GUI.Button(new Rect(fx, y, 66f, 20f), FieldLabels[i]) && !selected)
            {
                _field = FieldKeys[i];
                Search(1);
            }

            fx += 70f;
        }
        y += 24f;

        // --- Filtres avancés (drapeaux tri-état) ---
        var activeFlags = CountActiveFlags();
        var toggleLabel = _flagsOpen
            ? "Filtres avancés -"
            : activeFlags > 0
                ? $"Filtres avancés + ({activeFlags})"
                : "Filtres avancés +";

        if (GUI.Button(new Rect(x, y, 150f, 20f), toggleLabel))
        {
            _flagsOpen = !_flagsOpen;
        }

        if (activeFlags > 0 && GUI.Button(new Rect(x + 156f, y, 90f, 20f), "Réinitialiser"))
        {
            _flags.Clear();
            Search(1);
        }

        GUI.Label(
            new Rect(x + 252f, y, w - 252f, 20f),
            "clic : indifférent -> exiger -> exclure",
            S(Theme.Sub)
        );
        y += 24f;

        if (_flagsOpen)
        {
            y = DrawFlagGrid(x, y, w);
        }

        y += 2f;

        // --- Statut ---
        GUI.Label(new Rect(x, y, w, 18f), _status, S(Theme.Status));
        y += 22f;

        // --- Liste ---
        var listRect = new Rect(x, y, w, panel.yMax - y - 44f);
        DrawList(listRect);

        // --- Pied de page ---
        DrawFooter(panel, x, w);
    }

    private void DrawList(Rect area)
    {
        var contentH = _results.Count * RowH;
        HandleScroll(area, contentH);

        GUI.BeginGroup(area);

        if (_results.Count == 0)
        {
            GUI.Label(
                new Rect(4f, 8f, area.width - 8f, 20f),
                _busy ? "Chargement…" : "Aucun résultat pour l'instant.",
                S(Theme.Sub)
            );
        }

        for (var i = 0; i < _results.Count; i++)
        {
            var rowY = (i * RowH) - _scrollY;

            // Culling : on ne dessine que ce qui est visible.
            if (rowY + RowH < 0f || rowY > area.height)
            {
                continue;
            }

            DrawRow(_results[i], i, new Rect(0f, rowY, area.width, RowH));
        }

        GUI.EndGroup();

        // Indicateur de défilement (barre à droite).
        if (contentH > area.height)
        {
            var ratio = area.height / contentH;
            var barH = Mathf.Max(24f, area.height * ratio);
            var maxScroll = contentH - area.height;
            var t = maxScroll <= 0f ? 0f : _scrollY / maxScroll;
            var barY = area.y + (area.height - barH) * t;
            Theme.Fill(new Rect(area.xMax - 4f, barY, 3f, barH), Theme.TextDim, 2);
        }
    }

    private void HandleScroll(Rect area, float contentH)
    {
        var e = Event.current;
        if (e != null && e.type == EventType.ScrollWheel && area.Contains(e.mousePosition))
        {
            _scrollY += e.delta.y * 16f;
            Consume(e);
        }

        var max = Mathf.Max(0f, contentH - area.height);
        _scrollY = Mathf.Clamp(_scrollY, 0f, max);
    }

    /// rect est en coordonnées LOCALES au groupe de la liste.
    private void DrawRow(SongResult song, int index, Rect rect)
    {
        if (index % 2 == 0)
        {
            Theme.Fill(rect, Theme.RowEven, 1);
        }

        var textX = rect.x + 8f;

        // Jaquette (si disponible sur cette build).
        if (Plugin.ShowAlbumArt.Value && !_art.Disabled)
        {
            var artRect = new Rect(
                rect.x + 6f,
                rect.y + (RowH - ArtSize) * 0.5f,
                ArtSize,
                ArtSize
            );

            var texture = _art.Get(song.AlbumArtMd5, song.AlbumArtUrl);
            if (texture != null)
            {
                Theme.DrawImage(artRect, texture);
            }
            else
            {
                // Placeholder discret le temps du chargement.
                Theme.Fill(artRect, Theme.FieldBg, 1);
            }

            textX = artRect.xMax + 12f;
        }

        var textW = rect.xMax - textX - 300f;

        GUI.Label(
            new Rect(textX, rect.y + (RowH * 0.5f) - 20f, textW, 20f),
            song.Name,
            S(Theme.Title)
        );

        var sub = song.Artist;
        if (!string.IsNullOrEmpty(song.Year))
        {
            sub += $"  ·  {song.Year}";
        }
        if (!string.IsNullOrEmpty(song.Charter))
        {
            sub += $"  ·  chart : {song.Charter}";
        }
        GUI.Label(
            new Rect(textX, rect.y + (RowH * 0.5f) + 2f, textW, 18f),
            sub,
            S(Theme.Sub)
        );

        // Pastilles des instruments chartés (couleurs des frets).
        var dotX = rect.xMax - 300f;
        var dotY = rect.y + (RowH - 9f) * 0.5f;
        foreach (var instrument in song.Instruments)
        {
            if (dotX > rect.xMax - 200f)
            {
                break;
            }
            Theme.Fill(new Rect(dotX, dotY, 9f, 9f), Theme.InstrumentColor(instrument), 1);
            dotX += 13f;
        }

        // Durée + tier guitare.
        var meta = song.LengthText;
        if (song.GuitarTier >= 0)
        {
            meta += $"   tier {song.GuitarTier}";
        }
        GUI.Label(
            new Rect(rect.xMax - 190f, rect.y + (RowH - 18f) * 0.5f, 90f, 18f),
            meta,
            S(Theme.Meta)
        );

        // Action.
        var btnRect = new Rect(rect.xMax - 94f, rect.y + (RowH - BtnH) * 0.5f, 88f, BtnH);
        if (song.RequiresBrowser)
        {
            if (GUI.Button(btnRect, "Ouvrir"))
            {
                Application.OpenURL(song.DownloadUrl);
            }
        }
        else
        {
            GUI.enabled = !_busy;
            if (GUI.Button(btnRect, "Installer"))
            {
                StartDownload(song);
            }
            GUI.enabled = true;
        }
    }

    private void DrawFooter(Rect panel, float x, float w)
    {
        var y = panel.yMax - 34f;

        GUI.enabled = _page > 1 && !_busy;
        if (GUI.Button(new Rect(x, y, 90f, 22f), "< Préc."))
        {
            Search(_page - 1);
        }
        GUI.enabled = true;

        GUI.Label(new Rect(x + 96f, y, 60f, 22f), $"page {_page}", S(Theme.Sub));

        GUI.enabled = _results.Count > 0 && !_busy;
        if (GUI.Button(new Rect(x + 156f, y, 90f, 22f), "Suiv. >"))
        {
            Search(_page + 1);
        }
        GUI.enabled = true;

        GUI.Label(
            new Rect(x + 256f, y, w - 256f, 22f),
            $"Entrée = chercher · Échap = fermer · molette = défiler",
            S(Theme.Sub)
        );
    }

    private static GUIStyle S(GUIStyle custom) =>
        Theme.Ready && custom != null ? custom : GUI.skin.label;

    // ---------------------------------------------------------------
    //  Actions
    // ---------------------------------------------------------------
    private void PasteFromClipboard()
    {
        try
        {
            var clip = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(clip))
            {
                _query += clip.Trim();
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Presse-papier inaccessible : {ex.Message}");
        }
    }

    /// Grille de drapeaux : 4 colonnes, cycle à chaque clic.
    /// Retourne le Y sous la grille.
    private float DrawFlagGrid(float x, float y, float w)
    {
        const float cellW = 132f;
        const float cellH = 22f;
        const int columns = 4;

        for (var i = 0; i < FlagKeys.Length; i++)
        {
            var col = i % columns;
            var row = i / columns;
            var rect = new Rect(x + (col * (cellW + 6f)), y + (row * (cellH + 4f)), cellW, cellH);

            _flags.TryGetValue(FlagKeys[i], out var state);

            var label = FlagLabels[i];
            if (state == true)
            {
                Theme.Fill(rect, Theme.Green, 2);
                label = "+ " + label;
            }
            else if (state == false)
            {
                Theme.Fill(rect, Theme.Red, 2);
                label = "- " + label;
            }

            if (GUI.Button(rect, label))
            {
                // indifférent -> exiger -> exclure -> indifférent
                _flags[FlagKeys[i]] = state == null ? true : state == true ? false : (bool?)null;
                Search(1);
            }
        }

        var rows = (FlagKeys.Length + columns - 1) / columns;
        return y + (rows * (cellH + 4f));
    }

    private int CountActiveFlags()
    {
        var count = 0;
        foreach (var flag in _flags)
        {
            if (flag.Value != null)
            {
                count++;
            }
        }

        return count;
    }

    private void Search(int page)
    {
        if (_busy)
        {
            return;
        }

        var activeFlags = CountActiveFlags();
        var hasQuery = !string.IsNullOrWhiteSpace(_query);

        // Sans texte, une recherche n'a de sens que si des drapeaux
        // filtrent quelque chose (parcourir toutes les charts ayant telle
        // caractéristique).
        if (!hasQuery && activeFlags == 0)
        {
            return;
        }

        var field = _field;
        var note = "";

        // /search/advanced ne gère pas de recherche libre (vérifié : une
        // clé "search" y est ignorée). Si des drapeaux sont actifs alors
        // que le champ est "partout", il faut donc en cibler un.
        if (activeFlags > 0 && string.IsNullOrEmpty(field) && hasQuery)
        {
            field = "name";
            note = " (recherche limitée au titre : les filtres avancés "
                + "exigent un champ ciblé)";
        }

        _busy = true;
        _status = page > 1 ? $"Chargement de la page {page}…" : "Recherche en cours…";
        var query = _query;
        var instrument = _instrument;
        var difficulty = _difficulty;
        var flags = new Dictionary<string, bool?>(_flags);
        var suffix = note;

        Task.Run(async () =>
        {
            try
            {
                var result = await EnchorClient.SearchAsync(
                    query,
                    instrument,
                    difficulty,
                    field,
                    flags,
                    page
                );

                _mainThread.Enqueue(() =>
                {
                    if (result.Songs.Count == 0 && page > 1)
                    {
                        _status = "Fin des résultats.";
                        _busy = false;
                        return;
                    }

                    _results.Clear();
                    _results.AddRange(result.Songs);
                    _page = page;
                    _found = result.Found;
                    _scrollY = 0f;

                    if (result.Songs.Count == 0)
                    {
                        _status = "Aucun résultat." + suffix;
                    }
                    else if (result.Found >= 0)
                    {
                        // Total connu : seul /search/advanced le renvoie.
                        _status =
                            $"{result.Songs.Count} affiché(s) sur {result.Found} "
                                + $"— page {page}." + suffix;
                    }
                    else
                    {
                        _status =
                            $"{result.Songs.Count} résultat(s) — page {page}." + suffix;
                    }

                    _busy = false;
                });
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Recherche en échec : {e}");
                _mainThread.Enqueue(() =>
                {
                    _status = $"Échec : {e.Message}";
                    _busy = false;
                });
            }
        });
    }

    private void StartDownload(SongResult song)
    {
        _busy = true;
        _status = $"Téléchargement de « {song.Name} »…";

        Task.Run(async () =>
        {
            try
            {
                var data = await EnchorClient.DownloadAsync(song.DownloadUrl);
                var path = SongInstaller.Install(data, $"{song.Artist} - {song.Name}");
                Plugin.Logger.LogInfo($"Installé dans {path}");

                _mainThread.Enqueue(() =>
                {
                    var scanning = SongInstaller.TriggerRescan();
                    _status = scanning
                        ? $"« {song.Name} » installé — rescan en cours…"
                        : $"« {song.Name} » installé (rescan manuel nécessaire).";
                    _busy = false;
                });
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Installation en échec : {e}");
                _mainThread.Enqueue(() =>
                {
                    _status = $"Échec : {e.Message}";
                    _busy = false;
                });
            }
        });
    }
}
