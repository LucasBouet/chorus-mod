using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Notification history, opened from the NOTIFS button on the right of
/// the main menu: every trophy site notification meant for the player,
/// newest first, including the ones whose toast never showed (toast type
/// off, streamer mode). Same look and IMGUI building blocks as the
/// DuelWindow; game keyboard input is blocked while it's open.
///
/// Typing searches, chips filter by kind. Clicking a row shows its toast
/// again; a challenge row also opens the duels window.
/// </summary>
public class NotificationWindow : MonoBehaviour
{
    public NotificationWindow(IntPtr ptr) : base(ptr) { }

    private enum Filter
    {
        All,
        Missed,
        Trophies,
        Records,
        Levels,
        Duels,
        Announcements,
    }

    private const float Pad = 16f;
    private const float FieldH = 28f;
    private const float RowH = 64f;
    private const float ConfirmSeconds = 4f;

    private const float MinPanelW = 860f;
    private const float MinPanelH = 600f;

    private static readonly Filter[] Filters = (Filter[])Enum.GetValues(typeof(Filter));

    private static NotificationWindow? _instance;

    private bool _open;
    private bool _cursorWasVisible;
    private CursorLockMode _previousLockState;
    private float _panelW;
    private float _panelH;

    private List<Notification> _entries = new();
    private int _loadedVersion = -1;
    private HashSet<string> _new = new(); // unseen when the window opened

    private string _query = "";
    private Filter _filter = Filter.All;
    private float _scroll;
    private float _clearArmedUntil = float.NegativeInfinity;

    private GUIStyle? _colorStyle;
    private GUIStyle? _rightMeta;

    public static bool IsOpen => _instance != null && _instance._open;

    /// Called once from Plugin.Load().
    public static void Initialize()
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<NotificationWindow>();
        var host = new GameObject("ChorusMod.NotificationWindow");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        _instance = host.AddComponent<NotificationWindow>();
    }

    public static void Open()
    {
        if (_instance != null && !_instance._open)
        {
            _instance.SetOpen(true);
        }
    }

    private void SetOpen(bool open)
    {
        _open = open;
        _clearArmedUntil = float.NegativeInfinity;

        if (open)
        {
            _panelW = Mathf.Min(Mathf.Max(Plugin.PanelWidth.Value, MinPanelW), Screen.width - 24f);
            _panelH = Mathf.Min(Mathf.Max(Plugin.PanelHeight.Value, MinPanelH), Screen.height - 24f);
            _cursorWasVisible = Cursor.visible;
            _previousLockState = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            // What's new is highlighted for this visit, then counts as seen.
            _entries = NotificationHistory.Snapshot();
            _new = _entries.Where(n => !n.Seen).Select(n => n.Id).ToHashSet();
            NotificationHistory.MarkAllSeen();
            _loadedVersion = NotificationHistory.Version;
            _scroll = 0f;
        }
        else
        {
            Cursor.visible = _cursorWasVisible;
            Cursor.lockState = _previousLockState;
        }

        // Same two layers as the other windows.
        InputBlocker.SetKeyboardEnabled(!open);
        InputPatches.SwallowKeys = open;
    }

    private void OnDestroy()
    {
        if (_open)
        {
            SetOpen(false);
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
            // Something arrived while open: it shows up, already seen.
            if (_loadedVersion != NotificationHistory.Version)
            {
                _entries = NotificationHistory.Snapshot();
                NotificationHistory.MarkAllSeen();
                _loadedVersion = NotificationHistory.Version;
            }

            HandleKeys();
            if (_open)
            {
                DrawPanel();
            }
        }
        catch (Exception e)
        {
            SetOpen(false);
            Plugin.Logger.LogError($"Notification history render failed, window closed: {e}");
        }
    }

    // ---------------------------------------------------------------
    //  Keyboard
    // ---------------------------------------------------------------
    private void HandleKeys()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
        {
            return;
        }

        switch (e.keyCode)
        {
            case KeyCode.Escape:
                if (ClearArmed)
                {
                    _clearArmedUntil = float.NegativeInfinity;
                }
                else
                {
                    SetOpen(false);
                }

                Consume(e);
                return;

            case KeyCode.PageUp:
            case KeyCode.PageDown:
                var step = e.keyCode == KeyCode.PageDown ? 1 : Filters.Length - 1;
                _filter = Filters[((int)_filter + step) % Filters.Length];
                _scroll = 0f;
                Consume(e);
                return;

            case KeyCode.Backspace:
                // Ctrl+Backspace clears the whole field.
                _query = e.control || _query.Length == 0 ? "" : _query.Substring(0, _query.Length - 1);
                _scroll = 0f;
                Consume(e);
                return;
        }

        var c = e.character;
        if (!char.IsControl(c) && c != '\0')
        {
            _query += c;
            _scroll = 0f;
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
            // Game input is blocked anyway.
        }
    }

    private static bool Clicked(Rect rect)
    {
        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
        {
            Consume(e);
            return true;
        }

        return false;
    }

    private static bool Hovered(Rect rect)
    {
        var e = Event.current;
        return e != null && rect.Contains(e.mousePosition);
    }

    private bool ClearArmed => Time.unscaledTime < _clearArmedUntil;

    // ---------------------------------------------------------------
    //  Rendering
    // ---------------------------------------------------------------
    private void DrawPanel()
    {
        EnsureStyles();

        var panel = new Rect(
            Mathf.Round((Screen.width - _panelW) * 0.5f),
            Mathf.Round((Screen.height - _panelH) * 0.5f),
            _panelW,
            _panelH
        );

        Theme.Fill(panel, Theme.Bg, Plugin.PanelOpacityLayers.Value);

        var x = panel.x + Pad;
        var w = _panelW - Pad * 2f;
        var y = panel.y + 10f;

        // --- Header ---
        GUI.Label(new Rect(x, y, 400f, 22f), "CHORUS MOD  ·  NOTIFICATIONS", S(Theme.Header));
        if (GUI.Button(new Rect(panel.xMax - Pad - 80f, y, 80f, 22f), "Close"))
        {
            SetOpen(false);
            return;
        }

        GUI.enabled = _entries.Count > 0;
        var clear = new Rect(panel.xMax - Pad - 230f, y, 144f, 22f);
        if (ClearArmed)
        {
            Theme.Fill(new Rect(clear.x - 2f, clear.y - 2f, clear.width + 4f, clear.height + 4f), Theme.Red, 1);
        }

        if (GUI.Button(clear, ClearArmed ? "Click to confirm" : "Clear history"))
        {
            if (ClearArmed)
            {
                NotificationHistory.Clear();
                _entries.Clear();
                _new.Clear();
                _loadedVersion = NotificationHistory.Version;
                _clearArmedUntil = float.NegativeInfinity;
            }
            else
            {
                _clearArmedUntil = Time.unscaledTime + ConfirmSeconds;
            }
        }

        GUI.enabled = true;
        var missed = _entries.Count(n => !n.Toasted);
        GUI.Label(
            new Rect(panel.xMax - Pad - 560f, y, 320f, 22f),
            _entries.Count == 0 ? "" : $"{_entries.Count} kept  ·  {missed} never shown",
            _rightMeta ?? S(Theme.Meta)
        );
        y += 32f;

        // --- Search and filters ---
        DrawSearchField(new Rect(x, y, Mathf.Min(320f, w * 0.35f), FieldH));
        var cx = x + Mathf.Min(320f, w * 0.35f) + 14f;
        foreach (var filter in Filters)
        {
            var label = FilterLabel(filter);
            var count = filter == Filter.All ? 0 : _entries.Count(n => Matches(filter, n));
            var text = count > 0 ? $"{label} {count}" : label;
            var chip = new Rect(cx, y + 2f, Mathf.Max(54f, text.Length * 7.4f + 18f), FieldH - 4f);
            if (chip.xMax > x + w)
            {
                break;
            }

            if (_filter == filter)
            {
                Theme.Fill(chip, Theme.Accent, 2);
            }

            if (GUI.Button(chip, text) && _filter != filter)
            {
                _filter = filter;
                _scroll = 0f;
            }

            cx = chip.xMax + 4f;
        }

        y += FieldH + 12f;

        // --- Footer ---
        var footerY = panel.yMax - Pad - 18f;
        Theme.Fill(new Rect(x, footerY - 10f, w, 1f), Theme.FieldBg);
        GUI.Label(
            new Rect(x, footerY, w, 18f),
            "Click a notification to show it again  ·  Type to search  ·  PgUp/PgDn = filter  ·  Esc = close",
            S(Theme.Meta)
        );

        var list = new Rect(x, y, w, footerY - 14f - y);
        DrawList(list);
    }

    private void DrawSearchField(Rect rect)
    {
        Theme.Fill(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), Theme.Accent, 1);
        Theme.Fill(rect, Theme.FieldBg, 2);

        var clear = new Rect(rect.xMax - 26f, rect.y + 4f, 22f, rect.height - 8f);
        if (_query.Length > 0 && GUI.Button(clear, "x"))
        {
            _query = "";
            _scroll = 0f;
            return;
        }

        var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
        var shown = _query.Length > 0 ? _query + caret : "Search…" + caret;
        GUI.Label(new Rect(rect.x + 8f, rect.y, rect.width - 40f, rect.height), shown, _query.Length == 0 ? S(Theme.Sub) : S(Theme.Field));
    }

    private void DrawList(Rect area)
    {
        var shown = _entries
            .Where(n => Matches(_filter, n))
            .Where(n => MatchesQuery(_query, n.Title, n.Message, n.Label))
            .ToList();

        if (shown.Count == 0)
        {
            string empty;
            if (_entries.Count > 0)
            {
                empty = "Nothing matches.";
            }
            else if (Plugin.NtfyTopic.Value.Trim().Length == 0)
            {
                empty = "No notification channel set: add it in SETTINGS to receive the trophy site's notifications.";
            }
            else
            {
                empty = "No notification yet: trophies, records, levels, duels and announcements will show up here.";
            }

            GUI.Label(new Rect(area.x + 4f, area.y + 6f, area.width, 20f), empty, S(Theme.Sub));
            return;
        }

        DrawScrollList(area, shown.Count, RowH, ref _scroll, (i, row) => DrawRow(row, shown[i], i));
    }

    private void DrawRow(Rect row, Notification n, int index)
    {
        var accent = n.Accent;
        var hovered = Hovered(row);
        Theme.Fill(row, hovered ? UiKit.WithAlpha(accent, 0.14f) : index % 2 == 0 ? Theme.RowEven : Theme.RowOdd, 1);
        Theme.Fill(new Rect(row.x, row.y, 4f, row.height), accent, 1);

        // Right: action button, then date and state.
        const float buttonW = 110f;
        var button = new Rect(row.xMax - buttonW - 12f, row.y + (row.height - 28f) * 0.5f, buttonW, 28f);
        var hasButton = n.Kind == NotificationHistory.Challenge;
        if (hasButton && GUI.Button(button, "Duels"))
        {
            SetOpen(false);
            DuelWindow.Open();
            return;
        }

        const float metaW = 180f;
        var metaX = (hasButton ? button.x : row.xMax) - metaW - 14f;
        GUI.Label(new Rect(metaX, row.y + 10f, metaW, 18f), When(n.Time), _rightMeta ?? S(Theme.Meta));

        string tag;
        Color tagColor;
        if (_new.Contains(n.Id))
        {
            tag = n.Toasted ? "NEW" : "NEW  ·  MISSED";
            tagColor = Theme.Orange;
        }
        else
        {
            tag = n.Toasted ? "" : "MISSED";
            tagColor = Theme.TextDim;
        }

        if (tag.Length > 0 && _colorStyle != null)
        {
            _colorStyle.normal.textColor = tagColor;
            _colorStyle.alignment = TextAnchor.UpperRight;
            GUI.Label(new Rect(metaX, row.y + 34f, metaW, 18f), tag, _colorStyle);
        }

        // Left: label, title, message.
        var textX = row.x + 16f;
        var textW = metaX - textX - 12f;
        if (_colorStyle != null)
        {
            _colorStyle.normal.textColor = accent;
            _colorStyle.alignment = TextAnchor.UpperLeft;
        }

        var label = n.Label.ToUpperInvariant();
        GUI.Label(new Rect(textX, row.y + 8f, textW, 18f), label, _colorStyle ?? S(Theme.Meta));
        var titleX = textX + Mathf.Min(label.Length * 8.2f + 14f, 190f);
        GUI.Label(new Rect(titleX, row.y + 7f, textX + textW - titleX, 20f), Ellipsize(n.Title, textX + textW - titleX, 7.4f), S(Theme.Title));
        GUI.Label(new Rect(textX, row.y + 34f, textW, 18f), Ellipsize(n.Message, textW, 6.4f), S(Theme.Sub));

        if (Clicked(row))
        {
            n.ShowToast();
        }
    }

    /// Hand-made scrolling list (no GUILayout on this build), as in the
    /// DuelWindow.
    private static void DrawScrollList(Rect area, int count, float rowH, ref float scroll, Action<int, Rect> drawRow)
    {
        var contentH = count * rowH;
        var e = Event.current;
        if (e != null && e.type == EventType.ScrollWheel && area.Contains(e.mousePosition))
        {
            scroll += e.delta.y * 18f;
            Consume(e);
        }

        scroll = Mathf.Clamp(scroll, 0f, Mathf.Max(0f, contentH - area.height));

        GUI.BeginGroup(area);
        var width = contentH > area.height ? area.width - 8f : area.width;
        for (var i = 0; i < count; i++)
        {
            var rowY = i * rowH - scroll;
            if (rowY + rowH < 0f || rowY > area.height)
            {
                continue;
            }

            drawRow(i, new Rect(0f, rowY, width, rowH - 2f));
        }

        GUI.EndGroup();

        if (contentH > area.height)
        {
            var barH = Mathf.Max(24f, area.height * area.height / contentH);
            var t = scroll / (contentH - area.height);
            Theme.Fill(new Rect(area.xMax - 4f, area.y + (area.height - barH) * t, 3f, barH), Theme.TextDim, 2);
        }
    }

    private static string FilterLabel(Filter filter) => filter switch
    {
        Filter.Missed => "Missed",
        Filter.Trophies => "Trophies",
        Filter.Records => "Records",
        Filter.Levels => "Levels",
        Filter.Duels => "Duels",
        Filter.Announcements => "Announcements",
        _ => "All",
    };

    private static bool Matches(Filter filter, Notification n) => filter switch
    {
        Filter.Missed => !n.Toasted,
        Filter.Trophies => n.Kind == NotificationHistory.Trophy,
        Filter.Records => n.Kind == NotificationHistory.Record,
        Filter.Levels => n.Kind == NotificationHistory.Level,
        Filter.Duels => n.Kind is NotificationHistory.Challenge or NotificationHistory.DuelWon or NotificationHistory.DuelLost,
        Filter.Announcements => n.Kind == NotificationHistory.Announcement,
        _ => true,
    };

    /// Every word of the query appears in one of the fields.
    private static bool MatchesQuery(string query, params string[] fields)
    {
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!fields.Any(f => f.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return false;
            }
        }

        return true;
    }

    /// "Today 16:11", "Yesterday 09:02", "Oct 5, 16:11"
    private static string When(long unix)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().DateTime;
        var days = (DateTime.Today - time.Date).Days;
        var clock = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return days switch
        {
            0 => $"Today {clock}",
            1 => $"Yesterday {clock}",
            _ when time.Year == DateTime.Today.Year => time.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture),
            _ => time.ToString("MMM d yyyy, HH:mm", CultureInfo.InvariantCulture),
        };
    }

    /// Labels wrap instead of clipping: long text is cut to roughly fit.
    private static string Ellipsize(string text, float width, float charW)
    {
        var max = Mathf.Max(4, (int)(width / charW));
        return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    private void EnsureStyles()
    {
        if (_colorStyle != null || !Theme.Ready)
        {
            return;
        }

        try
        {
            _colorStyle = new GUIStyle(Theme.Meta) { fontStyle = FontStyle.Bold };
            _rightMeta = new GUIStyle(Theme.Meta) { alignment = TextAnchor.UpperRight };
        }
        catch (Exception)
        {
            // Plain styles are fine.
        }
    }

    private static GUIStyle S(GUIStyle custom) =>
        Theme.Ready && custom != null ? custom : GUI.skin.label;
}
