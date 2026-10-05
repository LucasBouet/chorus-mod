using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Full-size settings window, same look and same IMGUI building blocks as
/// the search window (ChorusUI / Theme): opened from the SETTINGS button
/// under the main-menu player card, closed with its Close button or
/// Escape. Game keyboard input is blocked while it's open, like ChorusUI.
///
/// Every setting a player would want to change lives here, so the .cfg
/// never has to be edited by hand. They're split into pages, browsed with
/// the arrows at the bottom (or the Left / Right keys). Each row edits a
/// ConfigEntry directly: BepInEx saves it to the .cfg immediately, and
/// whatever uses it reads the new value live (the trophy listener and the
/// player card are told to restart / refresh).
///
/// Row kinds: ON/OFF switch (with an optional Preview), text field, key
/// binding, number stepper and choice cycler. GUI.TextField doesn't exist
/// on this build, so text fields are hand-made like ChorusUI's search
/// box: click to edit, type, Enter to save, Esc to cancel, Ctrl+V or the
/// Paste button to paste.
/// </summary>
public class SettingsWindow : MonoBehaviour
{
    public SettingsWindow(IntPtr ptr) : base(ptr) { }

    private enum Kind
    {
        Toggle,
        Text,
        Key,
        Stepper,
        Choice,
    }

    private sealed class Setting
    {
        public Kind Kind;
        public string Title = "";
        public string Description = "";
        public Color Accent = Toast.DefaultAccent;

        // Toggle
        public ConfigEntry<bool>? Bool;
        public Action? Preview; // null = no Preview button

        // Text
        public ConfigEntry<string>? Text;
        public bool Secret;              // masked until "Show"
        public string Placeholder = "";  // shown when empty
        public Func<string, string?>? Warning; // problem with the value, or null
        public string ExtraLabel = "";   // optional extra button ("Detect"...)
        public Action? Extra;
        public Action? Changed;          // after a new value is saved

        // Key
        public ConfigEntry<KeyCode>? Key;

        // Stepper
        public Func<float>? GetNumber;
        public Action<float>? SetNumber;
        public float Step;
        public float Min;
        public float Max;

        // Choice
        public string[] Choices = Array.Empty<string>();
        public ConfigEntry<string>? Choice;
    }

    private sealed class Section
    {
        public string Title = "";
        public string Description = "";
        public Setting[] Settings = Array.Empty<Setting>();
    }

    private sealed class Page
    {
        public string Title = "";
        public Section[] Sections = Array.Empty<Section>();
    }

    private const float Pad = 16f;
    private const float RowH = 58f;
    private const float ButtonH = 28f;
    private const float SlotW = 84f;
    private const float Gap = 6f;

    private const float MinPanelW = 900f;
    private const float MinPanelH = 580f;

    private static SettingsWindow? _instance;

    private Page[] _pages = Array.Empty<Page>();
    private int _page;
    private bool _open;
    private bool _cursorWasVisible;
    private CursorLockMode _previousLockState;

    // Frozen while open: resizing the search window from here mustn't
    // move the buttons under the mouse.
    private float _panelW;
    private float _panelH;

    private Setting? _editing;      // text field being typed in
    private string _editBuffer = "";
    private Rect _editArea;         // field + its buttons: clicks outside save
    private Setting? _capturing;    // key binding waiting for a key
    private Setting? _revealed;     // secret field shown in clear

    private GUIStyle? _warnStyle;
    private GUIStyle? _centerStyle;

    public static bool IsOpen => _instance != null && _instance._open;

    /// Called once from Plugin.Load().
    public static void Initialize()
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<SettingsWindow>();
        var host = new GameObject("ChorusMod.SettingsWindow");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        _instance = host.AddComponent<SettingsWindow>();
    }

    public static void Open()
    {
        if (_instance != null && !_instance._open)
        {
            _instance.SetOpen(true);
        }
    }

    private void Start()
    {
        _pages = new[]
        {
            new Page
            {
                Title = "Notifications & gameplay",
                Sections = new[]
                {
                    new Section
                    {
                        Title = "NOTIFICATIONS",
                        Description = "Choose which toasts pop up in game. Every event is still written to the BepInEx log.",
                        Settings = NotificationSettings(),
                    },
                    new Section
                    {
                        Title = "GAMEPLAY",
                        Description = "Changes to the in-song display.",
                        Settings = new[]
                        {
                            new Setting
                            {
                                Kind = Kind.Toggle,
                                Bool = Plugin.HideSoloCounterInMultiplayer,
                                Title = "Hide solo counter in multiplayer",
                                Description = "Hides the solo percentage in local multiplayer and whenever you're connected to an online server.",
                            },
                        },
                    },
                },
            },
            new Page
            {
                Title = "Trophy site",
                Sections = new[]
                {
                    new Section
                    {
                        Title = "TROPHY SITE",
                        Description = "Who you are on the trophy site, and where its notifications come from.",
                        Settings = TrophySettings(),
                    },
                },
            },
            new Page
            {
                Title = "Search window",
                Sections = new[]
                {
                    new Section
                    {
                        Title = "SEARCH WINDOW",
                        Description = "The chart download window. Size changes apply to this window the next time it opens.",
                        Settings = SearchWindowSettings(),
                    },
                },
            },
            new Page
            {
                Title = "Downloads & library",
                Sections = new[]
                {
                    new Section
                    {
                        Title = "DOWNLOADS & LIBRARY",
                        Description = "Where charts go once downloaded, and how the game picks them up.",
                        Settings = LibrarySettings(),
                    },
                },
            },
            new Page
            {
                Title = "Advanced",
                Sections = new[]
                {
                    new Section
                    {
                        Title = "ADVANCED",
                        Description = "Chart server addresses and debugging. Leave as-is unless something changed on the server's side.",
                        Settings = AdvancedSettings(),
                    },
                },
            },
        };
    }

    // ---------------------------------------------------------------
    //  Page contents
    // ---------------------------------------------------------------
    private static Setting[] NotificationSettings() => new[]
    {
        new Setting
        {
            Kind = Kind.Toggle,
            Bool = Plugin.ToastTrophies,
            Title = "Trophy unlocked",
            Description = "When you unlock a trophy on the trophy site.",
            Accent = TrophyListener.TrophyAccent,
            Preview = () => Toast.Show(
                "Full Combo !",
                "Réaliser un Full Combo sur une musique.",
                5f,
                TrophyListener.TrophyAccent,
                "Trophy unlocked"
            ),
        },
        new Setting
        {
            Kind = Kind.Toggle,
            Bool = Plugin.ToastRecords,
            Title = "Record beaten",
            Description = "When someone beats one of your records.",
            Accent = TrophyListener.RecordAccent,
            Preview = () => Toast.Show(
                "Through the Fire and Flames",
                "DragonForce · Guitar Expert · 856,742 by Detox",
                5f,
                TrophyListener.RecordAccent,
                "Record beaten"
            ),
        },
        new Setting
        {
            Kind = Kind.Toggle,
            Bool = Plugin.ToastLevels,
            Title = "Level up",
            Description = "When you reach a new level.",
            Accent = TrophyListener.LevelAccent,
            Preview = () => Toast.ShowRankUp(
                "Level 7",
                "Level 8",
                "1,842 points",
                5f,
                TrophyListener.LevelAccent,
                "Level up"
            ),
        },
        new Setting
        {
            Kind = Kind.Toggle,
            Bool = Plugin.ToastChallenges,
            Title = "Duel challenge",
            Description = "When another player challenges you to a duel.",
            Accent = TrophyListener.ChallengeAccent,
            Preview = () => Toast.ShowClash(
                "Through the Fire and Flames",
                TrophyListener.ChallengeMessage("MrPlopy", "Guitar", "Expert", "268000"),
                6f,
                TrophyListener.ChallengeAccent,
                "Duel challenge"
            ),
        },
    };

    private static Setting[] TrophySettings() => new[]
    {
        new Setting
        {
            // The player card notices a new name by itself.
            Kind = Kind.Text,
            Text = Plugin.TrophyUsername,
            Title = "Discord name",
            Description = "Your name on the trophy site: only your events pop up, and it's the player card's profile.",
            Placeholder = "empty = every player's events",
            ExtraLabel = "Detect",
            Extra = () =>
            {
                var detected = Plugin.DetectTrackerUsername();
                if (detected.Length > 0)
                {
                    Plugin.TrophyUsername.Value = detected;
                }
                else
                {
                    Toast.Show("Not found", "Rythmania Tracker's player.json wasn't found on this PC.");
                }
            },
        },
        new Setting
        {
            Kind = Kind.Text,
            Text = Plugin.NtfyTopic,
            Title = "Notification channel",
            Description = "The ntfy topic given by the trophy site. Keep it private.",
            Placeholder = "empty = trophy notifications off",
            Secret = true,
            Accent = TrophyListener.TrophyAccent,
            Changed = TrophyListener.Restart,
        },
        new Setting
        {
            Kind = Kind.Text,
            Text = Plugin.NtfyServer,
            Title = "Notification server",
            Description = "ntfy server the trophy site publishes to.",
            Warning = UrlWarning,
            Changed = TrophyListener.Restart,
        },
        new Setting
        {
            Kind = Kind.Text,
            Text = Plugin.PlayerApiUrl,
            Title = "Player profile API",
            Description = "Feeds the main-menu player card (called with ?discordName=).",
            Warning = UrlWarning,
            Changed = MainMenuOverlay.RequestRefresh,
        },
    };

    private static Setting[] SearchWindowSettings() => new[]
    {
        new Setting
        {
            Kind = Kind.Key,
            Key = Plugin.ToggleKey,
            Title = "Open / close key",
            Description = "Opens the search window from anywhere in the game.",
        },
        new Setting
        {
            Kind = Kind.Choice,
            Choice = Plugin.Instrument,
            Choices = ChorusUI.Instruments,
            Title = "Default instrument",
            Description = "Instrument filter selected when the window opens (null = any).",
        },
        new Setting
        {
            Kind = Kind.Toggle,
            Bool = Plugin.ShowAlbumArt,
            Title = "Album art",
            Description = "Shows album art in the results. Turn off if images cause slowdowns.",
        },
        new Setting
        {
            Kind = Kind.Stepper,
            Title = "Width",
            Description = "In pixels. Increase it on a large screen.",
            GetNumber = () => Plugin.PanelWidth.Value,
            SetNumber = v => Plugin.PanelWidth.Value = v,
            Step = 20f,
            Min = MinPanelW,
            Max = 3840f,
        },
        new Setting
        {
            Kind = Kind.Stepper,
            Title = "Height",
            Description = "In pixels.",
            GetNumber = () => Plugin.PanelHeight.Value,
            SetNumber = v => Plugin.PanelHeight.Value = v,
            Step = 20f,
            Min = MinPanelH,
            Max = 2160f,
        },
        new Setting
        {
            Kind = Kind.Stepper,
            Title = "Background opacity",
            Description = "Only used if color tinting fails on your setup: stacked layers, more = more opaque.",
            GetNumber = () => Plugin.PanelOpacityLayers.Value,
            SetNumber = v => Plugin.PanelOpacityLayers.Value = Mathf.RoundToInt(v),
            Step = 1f,
            Min = 1f,
            Max = 20f,
        },
    };

    private static Setting[] LibrarySettings() => new[]
    {
        new Setting
        {
            Kind = Kind.Text,
            Text = Plugin.SongsFolder,
            Title = "Songs folder",
            Description = "Where charts are installed. Must be one of the folders Clone Hero scans.",
            Placeholder = "not set: downloads will fail",
            Warning = v => v.Trim().Length == 0
                ? "Not set: downloads will fail."
                : Directory.Exists(v.Trim()) ? null : "This folder doesn't exist.",
            ExtraLabel = "Detect",
            Extra = () =>
            {
                var detected = Plugin.DetectSongsFolder();
                if (detected.Length > 0)
                {
                    Plugin.SongsFolder.Value = detected;
                }
                else
                {
                    Toast.Show("Not found", "No Songs folder found in the usual places.");
                }
            },
        },
        new Setting
        {
            Kind = Kind.Toggle,
            Bool = Plugin.FullScan,
            Title = "Full rescan after install",
            Description = "Off = quick scan of the new chart only. Turn on if new songs don't show up.",
        },
        new Setting
        {
            Kind = Kind.Text,
            Text = Plugin.LibraryExportPath,
            Title = "Library export file",
            Description = "JSON song list exported from Clone Hero's Songs menu, used by the Sync button.",
            Placeholder = "not set: Sync disabled",
            Warning = v => v.Trim().Length == 0 || File.Exists(v.Trim()) ? null : "This file doesn't exist.",
        },
        new Setting
        {
            Kind = Kind.Toggle,
            Bool = Plugin.BlockGameInput,
            Title = "Block game keys while typing",
            Description = "Keys typed in the mod's windows don't trigger the game's shortcuts. Controllers stay active.",
        },
    };

    private static Setting[] AdvancedSettings() => new[]
    {
        new Setting
        {
            Kind = Kind.Text,
            Text = Plugin.ApiBaseUrl,
            Title = "Chart API",
            Description = "Base URL of the Chorus Encore API.",
            Warning = UrlWarning,
        },
        new Setting
        {
            Kind = Kind.Text,
            Text = Plugin.SearchEndpoint,
            Title = "Search endpoint",
            Description = "Path of the search endpoint, called with POST.",
        },
        new Setting
        {
            Kind = Kind.Text,
            Text = Plugin.FilesBaseUrl,
            Title = "File host",
            Description = "Where charts and album art are downloaded from.",
            Warning = UrlWarning,
        },
        new Setting
        {
            Kind = Kind.Toggle,
            Bool = Plugin.LogRawResponse,
            Title = "Log raw API responses",
            Description = "Writes the first result's raw JSON to the BepInEx log, for debugging.",
        },
    };

    private static string? UrlWarning(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? null
            : "Not a valid http(s) address.";

    // ---------------------------------------------------------------
    //  Open / close
    // ---------------------------------------------------------------
    private void SetOpen(bool open)
    {
        _open = open;
        _editing = null;
        _capturing = null;
        _revealed = null;

        if (open)
        {
            _panelW = Mathf.Max(Plugin.PanelWidth.Value, MinPanelW);
            _panelH = Mathf.Max(Plugin.PanelHeight.Value, MinPanelH);
            _cursorWasVisible = Cursor.visible;
            _previousLockState = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            Cursor.visible = _cursorWasVisible;
            Cursor.lockState = _previousLockState;
        }

        // Same two layers as ChorusUI.SetGameInputEnabled: Rewired's
        // keyboard + the Harmony patch on Unity's legacy Input.GetKey*.
        InputBlocker.SetKeyboardEnabled(!open);
        InputPatches.SwallowKeys = open;
    }

    private void Close()
    {
        CommitEdit();
        SetOpen(false);
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
            HandleMouse();
            HandleKeys();
            if (_open)
            {
                DrawPanel();
            }
        }
        catch (Exception e)
        {
            SetOpen(false);
            Plugin.Logger.LogError($"Settings render failed, window closed: {e}");
        }
    }

    // ---------------------------------------------------------------
    //  Input
    // ---------------------------------------------------------------

    /// A click anywhere outside the field being edited (and its buttons)
    /// saves it, like leaving a normal text box.
    private void HandleMouse()
    {
        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && _editing != null
            && !_editArea.Contains(e.mousePosition))
        {
            CommitEdit();
        }
    }

    private void HandleKeys()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
        {
            return;
        }

        if (_capturing != null)
        {
            if (e.keyCode == KeyCode.None)
            {
                return; // the character half of a key press
            }

            if (e.keyCode != KeyCode.Escape && _capturing.Key != null)
            {
                _capturing.Key.Value = e.keyCode;
            }

            _capturing = null;
            Consume(e);
            return;
        }

        if (_editing != null)
        {
            HandleTyping(e);
            return;
        }

        switch (e.keyCode)
        {
            case KeyCode.Escape:
                Close();
                Consume(e);
                break;

            case KeyCode.LeftArrow:
            case KeyCode.PageUp:
                GoToPage(_page - 1);
                Consume(e);
                break;

            case KeyCode.RightArrow:
            case KeyCode.PageDown:
                GoToPage(_page + 1);
                Consume(e);
                break;
        }
    }

    private void HandleTyping(Event e)
    {
        if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
        {
            CommitEdit();
            Consume(e);
            return;
        }

        if (e.keyCode == KeyCode.Escape)
        {
            _editing = null; // cancel: the saved value stays
            Consume(e);
            return;
        }

        if (e.keyCode == KeyCode.Backspace)
        {
            // Ctrl+Backspace clears the whole field.
            _editBuffer = e.control || _editBuffer.Length == 0
                ? ""
                : _editBuffer.Substring(0, _editBuffer.Length - 1);
            Consume(e);
            return;
        }

        if (e.control && e.keyCode == KeyCode.V)
        {
            Paste();
            Consume(e);
            return;
        }

        var c = e.character;
        if (!char.IsControl(c) && c != '\0')
        {
            _editBuffer += c;
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

    private void Paste()
    {
        try
        {
            var clip = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(clip))
            {
                // Paths copied from Explorer come wrapped in quotes.
                _editBuffer += clip.Trim().Trim('"');
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"Clipboard unavailable: {ex.Message}");
        }
    }

    private void BeginEdit(Setting setting)
    {
        CommitEdit();
        _capturing = null;
        _editing = setting;
        _editBuffer = setting.Text?.Value ?? "";
    }

    private void CommitEdit()
    {
        var setting = _editing;
        _editing = null;
        if (setting?.Text == null)
        {
            return;
        }

        var value = _editBuffer.Trim();
        if (value == setting.Text.Value)
        {
            return;
        }

        // Saved to the .cfg by BepInEx immediately.
        setting.Text.Value = value;
        setting.Changed?.Invoke();
    }

    private void GoToPage(int page)
    {
        CommitEdit();
        _capturing = null;
        _revealed = null;
        _page = Mathf.Clamp(page, 0, _pages.Length - 1);
    }

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
        GUI.Label(new Rect(x, y, 400f, 22f), "CHORUS MOD  ·  SETTINGS", S(Theme.Header));
        GUI.Label(
            new Rect(panel.xMax - Pad - 96f - 420f, y, 420f, 22f),
            "Changes are saved immediately.",
            S(Theme.Meta)
        );
        if (GUI.Button(new Rect(panel.xMax - Pad - 80f, y, 80f, 22f), "Close"))
        {
            Close();
            return;
        }

        y += 32f;
        Theme.Fill(new Rect(x, y, w, 1f), Theme.FieldBg);
        y += 16f;

        // --- Current page ---
        if (_pages.Length == 0)
        {
            return;
        }

        var page = _pages[_page];
        for (var s = 0; s < page.Sections.Length; s++)
        {
            var section = page.Sections[s];
            if (s > 0)
            {
                y += 20f;
            }

            GUI.Label(new Rect(x, y, w, 20f), section.Title, S(Theme.Title));
            y += 20f;
            GUI.Label(new Rect(x, y, w, 18f), section.Description, S(Theme.Sub));
            y += 28f;

            for (var i = 0; i < section.Settings.Length; i++)
            {
                DrawRow(new Rect(x, y, w, RowH), section.Settings[i], i);
                y += RowH + 4f;
            }
        }

        DrawFooter(panel, x, w);
    }

    /// Page arrows, page number and title, and a dot per page.
    private void DrawFooter(Rect panel, float x, float w)
    {
        var y = panel.yMax - Pad - ButtonH;
        Theme.Fill(new Rect(x, y - 12f, w, 1f), Theme.FieldBg);

        GUI.enabled = _page > 0;
        if (GUI.Button(new Rect(x, y, 120f, ButtonH), "<  Previous"))
        {
            GoToPage(_page - 1);
        }

        GUI.enabled = _page < _pages.Length - 1;
        if (GUI.Button(new Rect(x + w - 120f, y, 120f, ButtonH), "Next  >"))
        {
            GoToPage(_page + 1);
        }

        GUI.enabled = true;

        GUI.Label(
            new Rect(x + 130f, y - 2f, w - 260f, 18f),
            $"{_page + 1} / {_pages.Length}  ·  {_pages[_page].Title.ToUpperInvariant()}",
            _centerStyle ?? S(Theme.Status)
        );

        // Dots: the current page lit, the others dim. Clickable.
        const float dot = 8f;
        const float spacing = 16f;
        var dotsX = x + (w - (_pages.Length - 1) * spacing - dot) * 0.5f;
        for (var i = 0; i < _pages.Length; i++)
        {
            var rect = new Rect(dotsX + i * spacing, y + 20f, dot, dot);
            var color = Theme.Accent;
            color.a = i == _page ? 1f : 0.25f;
            Theme.Fill(rect, color, 1);

            var e = Event.current;
            if (e != null && e.type == EventType.MouseDown
                && new Rect(rect.x - 4f, rect.y - 4f, rect.width + 8f, rect.height + 8f).Contains(e.mousePosition))
            {
                GoToPage(i);
                Consume(e);
            }
        }
    }

    private void DrawRow(Rect row, Setting data, int index)
    {
        Theme.Fill(row, index % 2 == 0 ? Theme.RowEven : Theme.RowOdd, 1);

        var on = data.Kind != Kind.Toggle || data.Bool == null || data.Bool.Value;

        // Stripe in the setting's color, dimmed when a switch is off.
        var stripe = data.Accent;
        stripe.a = on ? 1f : 0.25f;
        Theme.Fill(new Rect(row.x, row.y, 4f, row.height), stripe, 1);

        // Left: title and description (or what's wrong with the value).
        var textW = data.Kind == Kind.Toggle ? row.width - 300f : row.width * 0.42f - 24f;
        GUI.Label(new Rect(row.x + 18f, row.y + 10f, textW, 20f), data.Title, S(Theme.Title));

        var warning = data.Kind == Kind.Text && data.Text != null && data.Warning != null
            ? data.Warning(_editing == data ? _editBuffer : data.Text.Value)
            : null;
        GUI.Label(
            new Rect(row.x + 18f, row.y + 30f, textW, 18f),
            warning ?? data.Description,
            warning != null && _warnStyle != null ? _warnStyle : S(Theme.Sub)
        );

        // Right: the control.
        var controlX = data.Kind == Kind.Toggle ? row.xMax - 300f : row.x + row.width * 0.42f;
        var control = new Rect(
            controlX,
            row.y + (row.height - ButtonH) * 0.5f,
            row.xMax - 12f - controlX,
            ButtonH
        );

        switch (data.Kind)
        {
            case Kind.Toggle:
                DrawToggle(control, data);
                break;
            case Kind.Text:
                DrawText(control, data);
                break;
            case Kind.Key:
                DrawKey(control, data);
                break;
            case Kind.Stepper:
                DrawStepper(control, data);
                break;
            case Kind.Choice:
                DrawChoice(control, data);
                break;
        }
    }

    private static void DrawToggle(Rect control, Setting data)
    {
        if (data.Bool == null)
        {
            return;
        }

        var on = data.Bool.Value;

        if (data.Preview != null
            && GUI.Button(new Rect(control.xMax - 208f, control.y, 100f, ButtonH), "Preview"))
        {
            data.Preview();
        }

        // ON/OFF switch: a colored pill behind the button so the state reads
        // at a glance even with the default button skin.
        var toggle = new Rect(control.xMax - 96f, control.y, 96f, ButtonH);
        var pill = data.Accent;
        pill.a = on ? 0.9f : 0.12f;
        Theme.Fill(new Rect(toggle.x - 3f, toggle.y - 3f, toggle.width + 6f, toggle.height + 6f), pill, 1);
        if (GUI.Button(toggle, on ? "ON" : "OFF"))
        {
            // Saved to the .cfg by BepInEx immediately.
            data.Bool.Value = !on;
        }
    }

    /// [ field .................... ] [slot 1] [slot 2]
    /// Editing: Paste / Save. Otherwise: Show (secret) or the extra
    /// action, and Default when the value differs from it.
    private void DrawText(Rect control, Setting data)
    {
        if (data.Text == null)
        {
            return;
        }

        var field = new Rect(control.x, control.y, control.width - 2f * (SlotW + Gap), ButtonH);
        var slot1 = new Rect(field.xMax + Gap, control.y, SlotW, ButtonH);
        var slot2 = new Rect(slot1.xMax + Gap, control.y, SlotW, ButtonH);
        var editing = _editing == data;

        // Focused field: accent outline.
        if (editing)
        {
            Theme.Fill(new Rect(field.x - 2f, field.y - 2f, field.width + 4f, field.height + 4f), Theme.Accent, 1);
        }

        Theme.Fill(field, Theme.FieldBg, 2);

        string shown;
        if (editing)
        {
            var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
            shown = _editBuffer + caret;
        }
        else if (data.Text.Value.Length == 0)
        {
            shown = data.Placeholder;
        }
        else if (data.Secret && _revealed != data)
        {
            shown = new string('•', Math.Min(data.Text.Value.Length, 24));
        }
        else
        {
            shown = data.Text.Value;
        }

        var style = data.Text.Value.Length == 0 && !editing ? S(Theme.Sub) : S(Theme.Field);
        GUI.Label(new Rect(field.x + 8f, field.y, field.width - 16f, field.height), FitLeft(shown, field.width - 16f), style);

        if (editing)
        {
            _editArea = new Rect(field.x, field.y, slot2.xMax - field.x, field.height);

            if (GUI.Button(slot1, "Paste"))
            {
                Paste();
            }

            if (GUI.Button(slot2, "Save"))
            {
                CommitEdit();
            }

            return;
        }

        // Click on the field = start typing.
        var e = Event.current;
        if (e != null && e.type == EventType.MouseDown && field.Contains(e.mousePosition))
        {
            BeginEdit(data);
            Consume(e);
            return;
        }

        if (data.Secret)
        {
            if (GUI.Button(slot1, _revealed == data ? "Hide" : "Show"))
            {
                _revealed = _revealed == data ? null : data;
            }
        }
        else if (data.Extra != null && GUI.Button(slot1, data.ExtraLabel))
        {
            var before = data.Text.Value;
            data.Extra();
            if (data.Text.Value != before)
            {
                data.Changed?.Invoke();
            }
        }

        var defaultValue = data.Text.DefaultValue as string ?? "";
        if (data.Text.Value != defaultValue && GUI.Button(slot2, "Default"))
        {
            data.Text.Value = defaultValue;
            data.Changed?.Invoke();
        }
    }

    private void DrawKey(Rect control, Setting data)
    {
        if (data.Key == null)
        {
            return;
        }

        var field = new Rect(control.x, control.y, control.width - 2f * (SlotW + Gap), ButtonH);
        var slot1 = new Rect(field.xMax + Gap, control.y, SlotW, ButtonH);
        var slot2 = new Rect(slot1.xMax + Gap, control.y, SlotW, ButtonH);
        var capturing = _capturing == data;

        if (capturing)
        {
            Theme.Fill(new Rect(field.x - 2f, field.y - 2f, field.width + 4f, field.height + 4f), Theme.Accent, 1);
        }

        Theme.Fill(field, Theme.FieldBg, 2);
        GUI.Label(
            new Rect(field.x + 8f, field.y, field.width - 16f, field.height),
            capturing ? "Press a key…  (Esc to cancel)" : data.Key.Value.ToString(),
            capturing ? S(Theme.Sub) : S(Theme.Field)
        );

        if (!capturing && GUI.Button(slot1, "Change"))
        {
            CommitEdit();
            _capturing = data;
        }

        var defaultKey = data.Key.DefaultValue is KeyCode key ? key : KeyCode.None;
        if (!capturing && data.Key.Value != defaultKey && GUI.Button(slot2, "Default"))
        {
            data.Key.Value = defaultKey;
        }
    }

    /// [ - ]  value  [ + ]
    private void DrawStepper(Rect control, Setting data)
    {
        if (data.GetNumber == null || data.SetNumber == null)
        {
            return;
        }

        var set = data.SetNumber;
        var value = data.GetNumber();
        DrawCycler(
            control,
            value.ToString("0"),
            value > data.Min ? () => set(Mathf.Max(data.Min, value - data.Step)) : null,
            value < data.Max ? () => set(Mathf.Min(data.Max, value + data.Step)) : null,
            "-",
            "+"
        );
    }

    /// [ < ]  choice  [ > ], wrapping around.
    private void DrawChoice(Rect control, Setting data)
    {
        if (data.Choice == null || data.Choices.Length == 0)
        {
            return;
        }

        var entry = data.Choice;
        var choices = data.Choices;
        var current = Array.IndexOf(choices, entry.Value);
        var count = choices.Length;
        DrawCycler(
            control,
            current < 0 ? entry.Value : choices[current],
            () => entry.Value = choices[current <= 0 ? count - 1 : current - 1],
            () => entry.Value = choices[(current + 1) % count],
            "<",
            ">"
        );
    }

    /// Value between two buttons, right-aligned in the control area like
    /// the switches. A null action disables its button.
    private void DrawCycler(Rect control, string value, Action? previous, Action? next, string previousLabel, string nextLabel)
    {
        const float button = 36f;
        const float valueW = 140f;

        var nextRect = new Rect(control.xMax - button, control.y, button, ButtonH);
        var valueRect = new Rect(nextRect.x - Gap - valueW, control.y, valueW, ButtonH);
        var previousRect = new Rect(valueRect.x - Gap - button, control.y, button, ButtonH);

        Theme.Fill(valueRect, Theme.FieldBg, 2);
        GUI.Label(valueRect, value, _centerStyle ?? S(Theme.Field));

        GUI.enabled = previous != null;
        if (GUI.Button(previousRect, previousLabel))
        {
            previous?.Invoke();
        }

        GUI.enabled = next != null;
        if (GUI.Button(nextRect, nextLabel))
        {
            next?.Invoke();
        }

        GUI.enabled = true;
    }

    /// Long values (paths, URLs) keep their end visible: that's where the
    /// caret is and the part that tells paths apart.
    private static string FitLeft(string text, float width)
    {
        var maxChars = Mathf.Max(8, (int)(width / 7.2f));
        return text.Length <= maxChars ? text : "…" + text.Substring(text.Length - maxChars + 1);
    }

    private void EnsureStyles()
    {
        if (_warnStyle != null || !Theme.Ready)
        {
            return;
        }

        try
        {
            _warnStyle = new GUIStyle(Theme.Sub);
            _warnStyle.normal.textColor = Theme.Orange;

            _centerStyle = new GUIStyle(Theme.Field) { alignment = TextAnchor.MiddleCenter };
        }
        catch (Exception)
        {
            // Plain styles are fine.
        }
    }

    private static GUIStyle S(GUIStyle custom) =>
        Theme.Ready && custom != null ? custom : GUI.skin.label;
}
