using System;
using BepInEx.Configuration;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Full-size settings window, same look and same IMGUI building blocks as
/// the search window (ChorusUI / Theme): opened from the SETTINGS button
/// under the main-menu player card, closed with its Close button or
/// Escape. Game keyboard input is blocked while it's open, like ChorusUI.
///
/// For now: one Notifications section, a row per trophy-site toast with
/// an ON/OFF switch (a ConfigEntry, saved to the .cfg immediately) and a
/// Preview button that fires a sample toast.
/// </summary>
public class SettingsWindow : MonoBehaviour
{
    public SettingsWindow(IntPtr ptr) : base(ptr) { }

    private struct Row
    {
        public ConfigEntry<bool> Entry;
        public string Title;
        public string Description;
        public Color Accent;
        public Action Preview;
    }

    private const float Pad = 16f;
    private const float RowH = 58f;
    private const float ButtonH = 28f;

    private static float PanelW => Plugin.PanelWidth.Value;
    private static float PanelH => Plugin.PanelHeight.Value;

    private static SettingsWindow? _instance;

    private Row[] _rows = Array.Empty<Row>();
    private bool _open;
    private bool _cursorWasVisible;
    private CursorLockMode _previousLockState;

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
        _rows = new[]
        {
            new Row
            {
                Entry = Plugin.ToastTrophies,
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
            new Row
            {
                Entry = Plugin.ToastRecords,
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
            new Row
            {
                Entry = Plugin.ToastLevels,
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
        };
    }

    private void SetOpen(bool open)
    {
        _open = open;

        if (open)
        {
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

    private void HandleKeys()
    {
        var e = Event.current;
        if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            SetOpen(false);
            try
            {
                e.Use();
            }
            catch (Exception)
            {
                // Game input is blocked anyway.
            }
        }
    }

    private void DrawPanel()
    {
        var panel = new Rect(
            Mathf.Round((Screen.width - PanelW) * 0.5f),
            Mathf.Round((Screen.height - PanelH) * 0.5f),
            PanelW,
            PanelH
        );

        Theme.Fill(panel, Theme.Bg, Plugin.PanelOpacityLayers.Value);

        var x = panel.x + Pad;
        var w = PanelW - Pad * 2f;
        var y = panel.y + 10f;

        // --- Header ---
        GUI.Label(new Rect(x, y, 400f, 22f), "CHORUS MOD  ·  SETTINGS", S(Theme.Header));
        if (GUI.Button(new Rect(panel.xMax - Pad - 80f, y, 80f, 22f), "Close"))
        {
            SetOpen(false);
            return;
        }

        y += 32f;
        Theme.Fill(new Rect(x, y, w, 1f), Theme.FieldBg);
        y += 16f;

        // --- Notifications ---
        GUI.Label(new Rect(x, y, w, 20f), "NOTIFICATIONS", S(Theme.Title));
        y += 20f;
        GUI.Label(
            new Rect(x, y, w, 18f),
            "Choose which toasts pop up in game. Every event is still written to the BepInEx log.",
            S(Theme.Sub)
        );
        y += 28f;

        for (var i = 0; i < _rows.Length; i++)
        {
            DrawRow(new Rect(x, y, w, RowH), _rows[i], i);
            y += RowH + 4f;
        }

        GUI.Label(
            new Rect(x, panel.yMax - 28f, w, 18f),
            "Esc or Close to go back. Changes are saved immediately.",
            S(Theme.Status)
        );
    }

    private static void DrawRow(Rect row, Row data, int index)
    {
        Theme.Fill(row, index % 2 == 0 ? Theme.RowEven : Theme.RowOdd, 1);

        var on = data.Entry.Value;

        // Stripe in the toast's own color, dimmed when it's off.
        var stripe = data.Accent;
        stripe.a = on ? 1f : 0.25f;
        Theme.Fill(new Rect(row.x, row.y, 4f, row.height), stripe, 1);

        GUI.Label(new Rect(row.x + 18f, row.y + 10f, row.width - 300f, 20f), data.Title, S(Theme.Title));
        GUI.Label(new Rect(row.x + 18f, row.y + 30f, row.width - 300f, 18f), data.Description, S(Theme.Sub));

        var buttonY = row.y + (row.height - ButtonH) * 0.5f;

        if (GUI.Button(new Rect(row.xMax - 220f, buttonY, 100f, ButtonH), "Preview"))
        {
            data.Preview();
        }

        // ON/OFF switch: a colored pill behind the button so the state reads
        // at a glance even with the default button skin.
        var toggle = new Rect(row.xMax - 108f, buttonY, 96f, ButtonH);
        var pill = data.Accent;
        pill.a = on ? 0.9f : 0.12f;
        Theme.Fill(new Rect(toggle.x - 3f, toggle.y - 3f, toggle.width + 6f, toggle.height + 6f), pill, 1);
        if (GUI.Button(toggle, on ? "ON" : "OFF"))
        {
            // Saved to the .cfg by BepInEx immediately.
            data.Entry.Value = !on;
        }
    }

    private static GUIStyle S(GUIStyle custom) =>
        Theme.Ready && custom != null ? custom : GUI.skin.label;
}
