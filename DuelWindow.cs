using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Duels window, opened from the DUELS button on the right of the main
/// menu. Same look and IMGUI building blocks as SettingsWindow; game
/// keyboard input is blocked while it's open.
///
/// Three tabs:
///  - New challenge: pick an opponent (left) and one of your own scores
///    (right, searchable and filtered by instrument / difficulty), then
///    Send. Sending takes a second click to confirm: the site only allows
///    a few challenges per 24 h.
///  - Received: challenges to play (first) and finished ones, with Find
///    chart (opens the search window) and Rematch.
///  - Sent: your challenges, waiting or decided.
///
/// Typing always goes to the search field of the current tab (on New
/// challenge, the focused one: Tab switches, a click picks). Up / Down
/// move through the focused list, Enter goes to the next step.
///
/// Also keeps the count of challenges waiting to be played, shown as a
/// badge on the DUELS button: refreshed every few minutes while the
/// button is on screen, and right away when a duel event arrives.
/// </summary>
public class DuelWindow : MonoBehaviour
{
    public DuelWindow(IntPtr ptr) : base(ptr) { }

    private enum Tab
    {
        New,
        Received,
        Sent,
    }

    private enum Focus
    {
        Player,
        Song,
    }

    private enum Filter
    {
        All,
        Open,
        Done,
    }

    private const float Pad = 16f;
    private const float FieldH = 28f;
    private const float ChipH = 22f;
    private const float PlayerRowH = 34f;
    private const float SongRowH = 48f;
    private const float DuelRowH = 64f;
    private const float ConfirmSeconds = 4f;
    private const float BadgeRefreshSeconds = 180f;

    private const float MinPanelW = 980f;
    private const float MinPanelH = 680f;

    private static readonly Color Accent = TrophyListener.ChallengeAccent;
    private static readonly Color Won = TrophyListener.DuelWonAccent;
    private static readonly Color Lost = Theme.Red;
    private static readonly Color Waiting = Theme.Yellow;

    private static readonly string[] Difficulties = { "Expert", "Hard", "Medium", "Easy" };
    private static readonly string[] Sorts = { "Artist", "Title", "Best score" };

    private static DuelWindow? _instance;
    private static volatile bool _refreshRequested;

    private readonly ConcurrentQueue<Action> _mainThread = new();

    private bool _open;
    private bool _cursorWasVisible;
    private CursorLockMode _previousLockState;
    private float _panelW;
    private float _panelH;

    private Tab _tab;
    private Focus _focus = Focus.Song;

    // --- Data (null = not loaded yet) ---
    private string _loadedFor = "";
    private int _loading;
    private List<DuelPlayer>? _players;
    private List<DuelScore>? _scores;
    private List<Duel>? _received;
    private List<Duel>? _sent;
    private int _remaining = -1;
    private int _limit = -1;
    private string[] _instruments = Array.Empty<string>(); // present in _scores, most used first
    private HashSet<string> _installed = new(); // checksums of open received duels found in the library

    // --- New challenge ---
    private string _playerQuery = "";
    private string _songQuery = "";
    private string? _player;
    private DuelScore? _score;
    private string _instrumentFilter = "";  // "" = all
    private string _difficultyFilter = "";
    private int _sort;
    private float _playerScroll;
    private float _songScroll;
    private float _armedUntil = float.NegativeInfinity;
    private bool _sending;

    private List<DuelPlayer> _shownPlayers = new();
    private List<DuelScore> _shownScores = new();
    private string _shownKey = "\0";

    // --- Received / Sent ---
    private string _receivedQuery = "";
    private string _sentQuery = "";
    private Filter _receivedFilter = Filter.All;
    private Filter _sentFilter = Filter.All;
    private float _listScroll;

    private string _status = "";
    private Color _statusColor = Theme.TextDim;

    // --- Badge ---
    private float _badgeSeenAt = float.NegativeInfinity;
    private float _nextBadgeFetch;
    private bool _badgeFetching;

    private GUIStyle? _centerStyle;
    private GUIStyle? _rightTitle;
    private GUIStyle? _colorStyle;

    public static bool IsOpen => _instance != null && _instance._open;

    /// Challenges received and not played yet; 0 while unknown.
    public static int PendingCount { get; private set; }

    /// Called once from Plugin.Load().
    public static void Initialize()
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<DuelWindow>();
        var host = new GameObject("ChorusMod.DuelWindow");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        _instance = host.AddComponent<DuelWindow>();
    }

    public static void Open()
    {
        if (_instance != null && !_instance._open)
        {
            _instance.SetOpen(true);
        }
    }

    public static void Close()
    {
        if (_instance != null && _instance._open)
        {
            _instance.SetOpen(false);
        }
    }

    /// Thread-safe: TrophyListener calls it when one of the player's duel
    /// events arrives.
    public static void RequestRefresh() => _refreshRequested = true;

    /// The DUELS button calls it every frame it's on screen, so the badge
    /// is only polled while someone can see it.
    public static void KeepBadgeFresh()
    {
        if (_instance != null)
        {
            _instance._badgeSeenAt = Time.unscaledTime;
        }
    }

    // ---------------------------------------------------------------
    //  Update: deferred results, refreshes
    // ---------------------------------------------------------------
    private void Update()
    {
        while (_mainThread.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Duels: deferred action failed: {e}");
            }
        }

        // Not on top of another window: each one owns the keyboard.
        var key = Plugin.DuelKey.Value;
        if (key != KeyCode.None && Input.GetKeyDown(key)
            && !SettingsWindow.IsOpen && !NotificationWindow.IsOpen && !ChorusUI.IsOpen)
        {
            SetOpen(!_open);
        }

        var me = Me();
        if (_refreshRequested)
        {
            _refreshRequested = false;
            _nextBadgeFetch = 0f;
            if (_open && me.Length > 0)
            {
                LoadChallenges(me);
            }
        }

        var buttonShown = Time.unscaledTime - _badgeSeenAt < 1f;
        if (!_open && buttonShown && !_badgeFetching && me.Length > 0
            && Time.unscaledTime >= _nextBadgeFetch)
        {
            FetchBadge(me);
        }
    }

    private static string Me() => Plugin.TrophyUsername.Value.Trim();

    private void FetchBadge(string me)
    {
        _badgeFetching = true;
        _nextBadgeFetch = Time.unscaledTime + BadgeRefreshSeconds;
        Task.Run(async () =>
        {
            try
            {
                var received = await ChallengeApi.GetChallengesAsync(me, false).ConfigureAwait(false);
                _mainThread.Enqueue(() =>
                {
                    if (me == Me())
                    {
                        PendingCount = received.Count(IsOpenDuel);
                    }

                    _badgeFetching = false;
                });
            }
            catch (Exception)
            {
                _mainThread.Enqueue(() => _badgeFetching = false);
            }
        });
    }

    // ---------------------------------------------------------------
    //  Loading
    // ---------------------------------------------------------------
    private void LoadAll()
    {
        var me = Me();
        if (me.Length == 0)
        {
            return;
        }

        if (me != _loadedFor)
        {
            // Another player: nothing of the previous one may stay.
            _players = null;
            _scores = null;
            _received = null;
            _sent = null;
            _player = null;
            _score = null;
            _remaining = -1;
            _limit = -1;
            _instruments = Array.Empty<string>();
            _shownKey = "\0";
            _loadedFor = me;
        }

        SetStatus("Loading…", Theme.TextDim);

        Load(me, () => ChallengeApi.GetPlayersAsync(me), roster =>
        {
            _players = roster.Players;
            _remaining = roster.Remaining;
            _limit = roster.Limit;
            if (_player != null && !_players.Any(p => p.Name == _player))
            {
                _player = null;
            }
        });

        Load(me, () => ChallengeApi.GetScoresAsync(me), scores =>
        {
            _scores = scores;
            _instruments = scores
                .GroupBy(s => s.Instrument)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Where(i => i.Length > 0)
                .ToArray();
            if (_score != null)
            {
                _score = scores.FirstOrDefault(s => s.Key == _score.Key);
            }
        });

        LoadChallenges(me);
    }

    private void LoadChallenges(string me)
    {
        Load(me, () => ChallengeApi.GetChallengesAsync(me, false), received =>
        {
            _received = received;
            PendingCount = received.Count(IsOpenDuel);
            _installed = ChartSelector.FindInstalled(received.Where(IsOpenDuel).Select(d => d.Checksum));
        });
        Load(me, () => ChallengeApi.GetChallengesAsync(me, true), sent => _sent = sent);
    }

    /// Runs one request off the main thread and applies its result on it,
    /// unless the player name changed meanwhile.
    private void Load<T>(string me, Func<Task<T>> request, Action<T> apply)
    {
        _loading++;
        Task.Run(async () =>
        {
            try
            {
                var result = await request().ConfigureAwait(false);
                _mainThread.Enqueue(() =>
                {
                    _loading--;
                    if (me != _loadedFor)
                    {
                        return;
                    }

                    apply(result);
                    _shownKey = "\0";
                    if (_loading == 0 && !_sending && _status == "Loading…")
                    {
                        SetStatus("", Theme.TextDim);
                    }
                });
            }
            catch (Exception e)
            {
                var message = e is DuelApiException ? e.Message : "Unexpected error, see the BepInEx log.";
                if (e is not DuelApiException)
                {
                    Plugin.Logger.LogError($"Duels: load failed: {e}");
                }

                _mainThread.Enqueue(() =>
                {
                    _loading--;
                    if (me == _loadedFor)
                    {
                        SetStatus(message, Theme.Red);
                    }
                });
            }
        });
    }

    private void Send()
    {
        var me = Me();
        var target = _player;
        var score = _score;
        if (me.Length == 0 || target == null || score == null || _sending)
        {
            return;
        }

        _sending = true;
        _armedUntil = float.NegativeInfinity;
        SetStatus($"Sending the challenge to {target}…", Theme.TextDim);

        Task.Run(async () =>
        {
            try
            {
                var remaining = await ChallengeApi.CreateAsync(me, target, score.Key).ConfigureAwait(false);
                Plugin.Logger.LogInfo($"Duels: challenged {target} on {score.Artist} - {score.Title} ({score.Key}).");
                _mainThread.Enqueue(() =>
                {
                    _sending = false;
                    if (remaining >= 0)
                    {
                        _remaining = remaining;
                    }
                    else if (_remaining > 0)
                    {
                        _remaining--;
                    }

                    SetStatus($"Challenge sent to {target}: {score.Title}, {Fmt(score.Score)} to beat.", Won);
                    Toast.Show("Challenge sent", $"{target} · {score.Title} · {Fmt(score.Score)} to beat", 5f, Accent, "Duel");

                    _score = null;
                    _tab = Tab.Sent;
                    _sentFilter = Filter.All;
                    _listScroll = 0f;
                    LoadAll();
                });
            }
            catch (Exception e)
            {
                var message = e is DuelApiException ? e.Message : "Unexpected error, see the BepInEx log.";
                if (e is not DuelApiException)
                {
                    Plugin.Logger.LogError($"Duels: create failed: {e}");
                }

                _mainThread.Enqueue(() =>
                {
                    _sending = false;
                    SetStatus($"Not sent: {message}", Theme.Red);
                });
            }
        });
    }

    private void SetStatus(string text, Color color)
    {
        _status = text;
        _statusColor = color;
    }

    // ---------------------------------------------------------------
    //  Open / close
    // ---------------------------------------------------------------
    private void SetOpen(bool open)
    {
        _open = open;
        _armedUntil = float.NegativeInfinity;

        if (open)
        {
            _panelW = Mathf.Min(Mathf.Max(Plugin.PanelWidth.Value, MinPanelW), Screen.width - 24f);
            _panelH = Mathf.Min(Mathf.Max(Plugin.PanelHeight.Value, MinPanelH), Screen.height - 24f);
            _cursorWasVisible = Cursor.visible;
            _previousLockState = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            // Something to play first, otherwise straight to challenging.
            _tab = PendingCount > 0 ? Tab.Received : Tab.New;
            _listScroll = 0f;
            LoadAll();
        }
        else
        {
            Cursor.visible = _cursorWasVisible;
            Cursor.lockState = _previousLockState;
        }

        // Same two layers as ChorusUI and SettingsWindow.
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
            Plugin.Logger.LogError($"Duels render failed, window closed: {e}");
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

        if (e.keyCode != KeyCode.None && e.keyCode == Plugin.DuelKey.Value)
        {
            Consume(e); // handled in Update()
            return;
        }

        switch (e.keyCode)
        {
            case KeyCode.Escape:
                if (Armed)
                {
                    _armedUntil = float.NegativeInfinity;
                }
                else
                {
                    SetOpen(false);
                }

                Consume(e);
                return;

            case KeyCode.PageUp:
                SwitchTab((Tab)(((int)_tab + 2) % 3));
                Consume(e);
                return;

            case KeyCode.PageDown:
                SwitchTab((Tab)(((int)_tab + 1) % 3));
                Consume(e);
                return;

            case KeyCode.Tab:
                if (_tab == Tab.New)
                {
                    _focus = _focus == Focus.Player ? Focus.Song : Focus.Player;
                }

                Consume(e);
                return;

            case KeyCode.UpArrow:
            case KeyCode.DownArrow:
                if (_tab == Tab.New)
                {
                    MoveSelection(e.keyCode == KeyCode.DownArrow ? 1 : -1);
                }

                Consume(e);
                return;

            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                if (_tab == Tab.New)
                {
                    if (_focus == Focus.Player && _player != null)
                    {
                        _focus = Focus.Song;
                    }
                    else if (_focus == Focus.Song)
                    {
                        PressSend();
                    }
                }

                Consume(e);
                return;

            case KeyCode.Backspace:
                // Ctrl+Backspace clears the whole field.
                EditQuery(q => e.control || q.Length == 0 ? "" : q.Substring(0, q.Length - 1));
                Consume(e);
                return;
        }

        if (e.control && e.keyCode == KeyCode.V)
        {
            try
            {
                var clip = GUIUtility.systemCopyBuffer;
                if (!string.IsNullOrEmpty(clip))
                {
                    EditQuery(q => q + clip.Trim());
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"Clipboard unavailable: {ex.Message}");
            }

            Consume(e);
            return;
        }

        var c = e.character;
        if (!char.IsControl(c) && c != '\0')
        {
            EditQuery(q => q + c);
            Consume(e);
        }
    }

    /// Applies an edit to the search field that's receiving the keyboard.
    private void EditQuery(Func<string, string> edit)
    {
        switch (_tab)
        {
            case Tab.New when _focus == Focus.Player:
                _playerQuery = edit(_playerQuery);
                _playerScroll = 0f;
                break;
            case Tab.New:
                _songQuery = edit(_songQuery);
                _songScroll = 0f;
                break;
            case Tab.Received:
                _receivedQuery = edit(_receivedQuery);
                _listScroll = 0f;
                break;
            case Tab.Sent:
                _sentQuery = edit(_sentQuery);
                _listScroll = 0f;
                break;
        }
    }

    private void MoveSelection(int step)
    {
        RefreshShown();
        if (_focus == Focus.Player)
        {
            var index = Step(_shownPlayers.FindIndex(p => p.Name == _player), step, _shownPlayers.Count);
            if (index >= 0)
            {
                _player = _shownPlayers[index].Name;
                _playerScroll = ScrollTo(index, PlayerRowH, _playerScroll, _playerListH);
            }
        }
        else
        {
            var index = Step(_score == null ? -1 : _shownScores.IndexOf(_score), step, _shownScores.Count);
            if (index >= 0)
            {
                SelectScore(_shownScores[index]);
                _songScroll = ScrollTo(index, SongRowH, _songScroll, _songListH);
            }
        }
    }

    private static int Step(int current, int step, int count)
    {
        if (count == 0)
        {
            return -1;
        }

        return current < 0 ? (step > 0 ? 0 : count - 1) : Mathf.Clamp(current + step, 0, count - 1);
    }

    /// Scroll offset that keeps row `index` visible.
    private static float ScrollTo(int index, float rowH, float scroll, float viewH)
    {
        var top = index * rowH;
        if (top < scroll)
        {
            return top;
        }

        return top + rowH > scroll + viewH ? top + rowH - viewH : scroll;
    }

    private void SwitchTab(Tab tab)
    {
        _tab = tab;
        _listScroll = 0f;
        _armedUntil = float.NegativeInfinity;
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

    // ---------------------------------------------------------------
    //  Selection helpers
    // ---------------------------------------------------------------
    private bool Armed => Time.unscaledTime < _armedUntil;

    private void SelectScore(DuelScore score)
    {
        if (_score != score)
        {
            _armedUntil = float.NegativeInfinity;
        }

        _score = score;
    }

    private void PressSend()
    {
        if (!CanSend(out _))
        {
            return;
        }

        if (Armed)
        {
            Send();
        }
        else
        {
            _armedUntil = Time.unscaledTime + ConfirmSeconds;
        }
    }

    /// Whether Send is possible; `why` = the button's label otherwise.
    private bool CanSend(out string why)
    {
        why = "";
        if (_sending)
        {
            why = "Sending…";
        }
        else if (_remaining == 0)
        {
            why = "No challenges left today";
        }
        else if (_player == null)
        {
            why = "Pick an opponent";
        }
        else if (_score == null)
        {
            why = "Pick a song";
        }

        return why.Length == 0;
    }

    /// Rematch: back to New challenge with the same opponent and song.
    private void Rematch(Duel duel)
    {
        _tab = Tab.New;
        _player = _players?.FirstOrDefault(p => string.Equals(p.Name, duel.Opponent, StringComparison.OrdinalIgnoreCase))?.Name;
        _playerQuery = "";
        _instrumentFilter = "";
        _difficultyFilter = "";
        _songQuery = duel.Song;
        _score = _scores?.FirstOrDefault(s =>
            string.Equals(s.Checksum, duel.Checksum, StringComparison.OrdinalIgnoreCase)
            && s.Instrument == duel.Instrument
            && s.Difficulty == duel.Difficulty);
        _focus = Focus.Song;
        _playerScroll = 0f;
        _songScroll = 0f;
        _armedUntil = float.NegativeInfinity;

        if (_player == null)
        {
            SetStatus($"{duel.Opponent} can't be challenged right now.", Theme.Orange);
        }
        else if (_score == null)
        {
            SetStatus("You have no score on that exact chart yet: play it first, or pick another one.", Theme.Orange);
        }
    }

    /// Closes the window; the chart gets selected once the player opens
    /// the song list (see ChartSelector).
    private void SelectChart(Duel duel)
    {
        if (!ChartSelector.Request(duel.Checksum, $"{duel.Artist} - {duel.Song}"))
        {
            _installed.Remove(duel.Checksum);
            SetStatus("That chart isn't in your library anymore: use Find chart.", Theme.Orange);
            return;
        }

        SetOpen(false);
        Toast.Show(
            ChartSelector.OpensQuickplay ? "Opening Quickplay" : "Open Quickplay",
            $"{duel.Song} will be selected · play it on {Chart(duel.Instrument, duel.Difficulty, duel.Speed, duel.Modifiers)}",
            9f,
            Accent,
            "Duel"
        );
    }

    /// Gets the chart from Chorus; once it's installed and scanned, it's
    /// selected like Select does (see ChallengeDownloader).
    private void DownloadChart(Duel duel)
    {
        if (ChallengeDownloader.Start(duel))
        {
            SetStatus($"Getting {duel.Song} from Chorus: it'll be selected once installed. You can close this window.", Theme.TextDim);
        }
    }

    /// Filled accent button; greyed out and inert when not `enabled`.
    private static bool AccentButton(Rect rect, string label, bool enabled)
    {
        var pill = Accent;
        pill.a = !enabled ? 0.25f : Hovered(rect) ? 1f : 0.8f;
        Theme.Fill(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), pill, 1);
        GUI.enabled = enabled;
        var clicked = GUI.Button(rect, label);
        GUI.enabled = true;
        return clicked && enabled;
    }

    private void FindChart(Duel duel)
    {
        SetOpen(false);
        ChorusUI.OpenSearch(duel.Song);
    }

    /// Recomputes the filtered lists when a search or filter changed.
    private void RefreshShown()
    {
        var key = $"{_playerQuery}\n{_songQuery}\n{_instrumentFilter}\n{_difficultyFilter}\n{_sort}";
        if (key == _shownKey)
        {
            return;
        }

        _shownKey = key;

        _shownPlayers = (_players ?? new List<DuelPlayer>())
            .Where(p => Matches(_playerQuery, p.Name))
            .ToList();

        IEnumerable<DuelScore> scores = (_scores ?? new List<DuelScore>())
            .Where(s => _instrumentFilter.Length == 0 || s.Instrument == _instrumentFilter)
            .Where(s => _difficultyFilter.Length == 0 || s.Difficulty == _difficultyFilter)
            .Where(s => Matches(_songQuery, s.Title, s.Artist, s.Charter));

        scores = _sort switch
        {
            1 => scores.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase),
            2 => scores.OrderByDescending(s => s.Score),
            _ => scores.OrderBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase),
        };
        _shownScores = scores.ToList();
    }

    /// Every word of the query appears in one of the fields.
    private static bool Matches(string query, params string[] fields)
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

    private static bool IsOpenDuel(Duel duel) =>
        duel.Status is "" or "pending" or "open" or "active" or "waiting" or "sent";

    // ---------------------------------------------------------------
    //  Rendering
    // ---------------------------------------------------------------
    private float _playerListH = 300f;
    private float _songListH = 300f;

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
        GUI.Label(new Rect(x, y, 400f, 22f), "CHORUS MOD  ·  DUELS", S(Theme.Header));
        if (GUI.Button(new Rect(panel.xMax - Pad - 80f, y, 80f, 22f), "Close"))
        {
            SetOpen(false);
            return;
        }

        GUI.enabled = _loading == 0 && !_sending;
        if (GUI.Button(new Rect(panel.xMax - Pad - 170f, y, 84f, 22f), _loading > 0 ? "…" : "Refresh"))
        {
            LoadAll();
        }

        GUI.enabled = true;
        GUI.Label(new Rect(panel.xMax - Pad - 600f, y, 420f, 22f), QuotaText(), S(Theme.Meta));
        y += 32f;

        var me = Me();
        if (me.Length == 0)
        {
            DrawNoPlayer(x, y, w);
            return;
        }

        // --- Tabs ---
        var pending = _received?.Count(IsOpenDuel) ?? PendingCount;
        DrawTab(new Rect(x, y, 200f, 30f), Tab.New, "NEW CHALLENGE");
        DrawTab(new Rect(x + 206f, y, 200f, 30f), Tab.Received, pending > 0 ? $"RECEIVED  ·  {pending} TO PLAY" : "RECEIVED");
        DrawTab(new Rect(x + 412f, y, 200f, 30f), Tab.Sent, "SENT");
        y += 42f;
        Theme.Fill(new Rect(x, y, w, 1f), Theme.FieldBg);
        y += 14f;

        // --- Footer: status and key hints ---
        var footerY = panel.yMax - Pad - 18f;
        Theme.Fill(new Rect(x, footerY - 10f, w, 1f), Theme.FieldBg);
        if (_colorStyle != null)
        {
            _colorStyle.normal.textColor = _statusColor;
        }

        GUI.Label(new Rect(x, footerY, w * 0.6f, 18f), Ellipsize(_status, w * 0.6f, 6.4f), _colorStyle ?? S(Theme.Status));
        GUI.Label(
            new Rect(x + w * 0.4f, footerY, w * 0.6f, 18f),
            _tab == Tab.New
                ? "Tab = switch list  ·  ↑↓ = pick  ·  Enter = next  ·  PgUp/PgDn = tabs  ·  Esc = close"
                : "Type to search  ·  PgUp/PgDn = tabs  ·  Esc = close",
            S(Theme.Meta)
        );

        var content = new Rect(x, y, w, footerY - 14f - y);
        switch (_tab)
        {
            case Tab.New:
                DrawNew(content);
                break;
            case Tab.Received:
                DrawDuels(content, _received, false);
                break;
            case Tab.Sent:
                DrawDuels(content, _sent, true);
                break;
        }
    }

    private string QuotaText()
    {
        if (_remaining < 0)
        {
            return "";
        }

        var limit = _limit > 0 ? $" of {_limit}" : "";
        return _remaining == 1 ? $"1{limit} challenge left today" : $"{_remaining}{limit} challenges left today";
    }

    private void DrawNoPlayer(float x, float y, float w)
    {
        y += 40f;
        GUI.Label(new Rect(x, y, w, 22f), "Who are you?", S(Theme.Title));
        GUI.Label(new Rect(x, y + 24f, w, 18f), "Set your player name (your Discord name on the trophy site) in SETTINGS to send and receive challenges.", S(Theme.Sub));
        if (GUI.Button(new Rect(x, y + 56f, 180f, FieldH), "Open SETTINGS"))
        {
            SetOpen(false);
            SettingsWindow.Open();
        }
    }

    private void DrawTab(Rect rect, Tab tab, string label)
    {
        var selected = _tab == tab;
        var pill = Accent;
        pill.a = selected ? 0.9f : Hovered(rect) ? 0.25f : 0.08f;
        Theme.Fill(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), pill, 1);
        if (GUI.Button(rect, label) && !selected)
        {
            SwitchTab(tab);
        }
    }

    // --- New challenge ---------------------------------------------
    private void DrawNew(Rect area)
    {
        RefreshShown();

        const float barH = 66f;
        var listsBottom = area.yMax - barH - 12f;
        var leftW = Mathf.Round(area.width * 0.27f);
        var right = new Rect(area.x + leftW + 18f, area.y, area.width - leftW - 18f, listsBottom - area.y);
        var left = new Rect(area.x, area.y, leftW, listsBottom - area.y);

        // Left: opponent.
        var y = left.y;
        GUI.Label(new Rect(left.x, y, left.width, 20f), "1  ·  OPPONENT", S(Theme.Title));
        y += 24f;
        DrawSearchField(new Rect(left.x, y, left.width, FieldH), _playerQuery, "Search a player…", _focus == Focus.Player, () => _focus = Focus.Player);
        y += FieldH + 8f;
        var playerList = new Rect(left.x, y, left.width, left.yMax - y);
        _playerListH = playerList.height;
        DrawPlayers(playerList);

        // Right: song.
        y = right.y;
        var count = _scores == null ? "" : $"  ·  {_shownScores.Count} of {_scores.Count} scores";
        GUI.Label(new Rect(right.x, y, right.width, 20f), $"2  ·  SONG{count}", S(Theme.Title));
        y += 24f;
        DrawSearchField(new Rect(right.x, y, right.width, FieldH), _songQuery, "Search your scores: title, artist, charter…", _focus == Focus.Song, () => _focus = Focus.Song);
        y += FieldH + 8f;
        DrawSongFilters(new Rect(right.x, y, right.width, ChipH));
        y += ChipH + 8f;
        var songList = new Rect(right.x, y, right.width, right.yMax - y);
        _songListH = songList.height;
        DrawSongs(songList);

        DrawSendBar(new Rect(area.x, listsBottom + 12f, area.width, barH));
    }

    private void DrawSearchField(Rect rect, string query, string placeholder, bool focused, Action focus)
    {
        if (focused)
        {
            Theme.Fill(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), Theme.Accent, 1);
        }

        Theme.Fill(rect, Theme.FieldBg, 2);

        var clear = new Rect(rect.xMax - 26f, rect.y + 4f, 22f, rect.height - 8f);
        if (query.Length > 0 && GUI.Button(clear, "x"))
        {
            focus();
            EditQuery(_ => "");
            return;
        }

        string shown;
        if (focused)
        {
            var caret = (Time.unscaledTime % 1f) < 0.5f ? "|" : " ";
            shown = query + caret;
        }
        else
        {
            shown = query.Length > 0 ? query : placeholder;
        }

        var style = query.Length == 0 && !focused ? S(Theme.Sub) : S(Theme.Field);
        GUI.Label(new Rect(rect.x + 8f, rect.y, rect.width - 40f, rect.height), shown, style);

        if (Clicked(rect))
        {
            focus();
        }
    }

    private void DrawPlayers(Rect area)
    {
        if (_players == null)
        {
            GUI.Label(new Rect(area.x + 4f, area.y + 6f, area.width, 20f), "Loading players…", S(Theme.Sub));
            return;
        }

        if (_shownPlayers.Count == 0)
        {
            GUI.Label(new Rect(area.x + 4f, area.y + 6f, area.width, 20f), "No player matches.", S(Theme.Sub));
            return;
        }

        DrawScrollList(area, _shownPlayers.Count, PlayerRowH, ref _playerScroll, (i, row) =>
        {
            var player = _shownPlayers[i];
            var selected = player.Name == _player;
            Theme.Fill(row, selected ? UiKit.WithAlpha(Accent, 0.22f) : Hovered(row) ? Theme.RowEven : Theme.RowOdd, 1);
            if (selected)
            {
                Theme.Fill(new Rect(row.x, row.y, 4f, row.height), Accent, 1);
            }

            GUI.Label(new Rect(row.x + 14f, row.y + 7f, row.width - 20f, 20f), Ellipsize(player.Name, row.width - 24f, 7.4f), S(Theme.Title));

            if (Clicked(row))
            {
                _player = player.Name;
                _focus = Focus.Player;
                _armedUntil = float.NegativeInfinity;
            }
        });
    }

    private void DrawSongFilters(Rect rect)
    {
        var x = rect.x;
        x = DrawChip(x, rect.y, "All", _instrumentFilter.Length == 0, () => _instrumentFilter = "");
        foreach (var instrument in _instruments)
        {
            var value = instrument;
            x = DrawChip(x, rect.y, instrument, _instrumentFilter == instrument, () => _instrumentFilter = value);
        }

        x += 14f;
        x = DrawChip(x, rect.y, "Any level", _difficultyFilter.Length == 0, () => _difficultyFilter = "");
        foreach (var difficulty in Difficulties)
        {
            var value = difficulty;
            x = DrawChip(x, rect.y, difficulty, _difficultyFilter == difficulty, () => _difficultyFilter = value);
        }

        var sortRect = new Rect(rect.xMax - 150f, rect.y, 150f, ChipH);
        if (sortRect.x > x + 8f && GUI.Button(sortRect, $"Sort: {Sorts[_sort]}"))
        {
            _sort = (_sort + 1) % Sorts.Length;
            _songScroll = 0f;
        }
    }

    /// Returns the x after the chip.
    private float DrawChip(float x, float y, string label, bool selected, Action select)
    {
        var width = Mathf.Max(54f, label.Length * 7.4f + 18f);
        var rect = new Rect(x, y, width, ChipH);
        if (selected)
        {
            Theme.Fill(rect, Theme.Accent, 2);
        }

        if (GUI.Button(rect, label) && !selected)
        {
            select();
            _songScroll = 0f;
        }

        return x + width + 4f;
    }

    private void DrawSongs(Rect area)
    {
        if (_scores == null)
        {
            GUI.Label(new Rect(area.x + 4f, area.y + 6f, area.width, 20f), "Loading your scores…", S(Theme.Sub));
            return;
        }

        if (_shownScores.Count == 0)
        {
            GUI.Label(
                new Rect(area.x + 4f, area.y + 6f, area.width, 20f),
                _scores.Count == 0 ? "No score on the trophy site yet: play a song first." : "No score matches.",
                S(Theme.Sub)
            );
            return;
        }

        DrawScrollList(area, _shownScores.Count, SongRowH, ref _songScroll, (i, row) =>
        {
            var score = _shownScores[i];
            var selected = score == _score;
            Theme.Fill(row, selected ? UiKit.WithAlpha(Accent, 0.22f) : Hovered(row) ? Theme.RowEven : (i % 2 == 0 ? Theme.RowOdd : Theme.RowEven * 0.5f), 1);
            Theme.Fill(new Rect(row.x, row.y, 4f, row.height), selected ? Accent : InstrumentColor(score.Instrument), 1);

            const float rightW = 230f;
            var textW = row.width - rightW - 30f;
            GUI.Label(new Rect(row.x + 14f, row.y + 6f, textW, 20f), Ellipsize(score.Title, textW, 7.4f), S(Theme.Title));
            GUI.Label(new Rect(row.x + 14f, row.y + 26f, textW, 18f), Ellipsize(Join(score.Artist, Charted(score.Charter)), textW, 6.4f), S(Theme.Sub));

            var rightX = row.xMax - rightW - 10f;
            GUI.Label(new Rect(rightX, row.y + 6f, rightW, 20f), Fmt(score.Score), _rightTitle ?? S(Theme.Title));
            GUI.Label(new Rect(rightX, row.y + 26f, rightW, 18f), Chart(score.Instrument, score.Difficulty, score.Speed, score.Modifiers), S(Theme.Meta));

            if (Clicked(row))
            {
                SelectScore(score);
                _focus = Focus.Song;
            }
        });
    }

    private void DrawSendBar(Rect bar)
    {
        Theme.Fill(bar, Theme.RowEven, 1);
        Theme.Fill(new Rect(bar.x, bar.y, 4f, bar.height), Accent, 1);

        const float buttonW = 280f;
        var textW = bar.width - buttonW - 40f;
        string line1;
        string line2;
        if (_player == null && _score == null)
        {
            line1 = "Pick an opponent and one of your scores.";
            line2 = "They get a notification and have to beat your score on the same chart, instrument and difficulty.";
        }
        else if (_score == null)
        {
            line1 = $"Challenge {_player} on…";
            line2 = "Pick one of your scores on the right.";
        }
        else
        {
            line1 = $"{_player ?? "…"}  must beat  {Fmt(_score.Score)}";
            line2 = $"{_score.Title}  ·  {_score.Artist}  ·  {Chart(_score.Instrument, _score.Difficulty, _score.Speed, _score.Modifiers)}";
        }

        GUI.Label(new Rect(bar.x + 18f, bar.y + 12f, textW, 22f), Ellipsize(line1, textW, 7.4f), S(Theme.Title));
        GUI.Label(new Rect(bar.x + 18f, bar.y + 34f, textW, 18f), Ellipsize(line2, textW, 6.4f), S(Theme.Sub));

        var button = new Rect(bar.xMax - buttonW - 14f, bar.y + 12f, buttonW, bar.height - 24f);
        var can = CanSend(out var why);
        var armed = can && Armed;
        var pill = armed ? Theme.Red : Accent;
        pill.a = !can ? 0.12f : armed ? 1f : Hovered(button) ? 1f : 0.8f;
        Theme.Fill(new Rect(button.x - 3f, button.y - 3f, button.width + 6f, button.height + 6f), pill, 1);

        string label;
        if (!can)
        {
            label = why;
        }
        else if (armed)
        {
            label = _remaining > 0 ? $"CONFIRM  ·  uses 1 of {_remaining} left" : "CONFIRM";
        }
        else
        {
            label = $"SEND CHALLENGE TO {_player!.ToUpperInvariant()}";
        }

        GUI.enabled = can;
        if (GUI.Button(button, Ellipsize(label, buttonW - 16f, 7.2f)))
        {
            PressSend();
        }

        GUI.enabled = true;
    }

    // --- Received / Sent -------------------------------------------
    private void DrawDuels(Rect area, List<Duel>? duels, bool sent)
    {
        var query = sent ? _sentQuery : _receivedQuery;
        var filter = sent ? _sentFilter : _receivedFilter;

        var y = area.y;
        const float chipsW = 3f * 110f;
        DrawSearchField(new Rect(area.x, y, area.width - chipsW - 16f, FieldH), query, sent ? "Search by player or song…" : "Search by challenger or song…", true, () => { });

        var cx = area.xMax - chipsW;
        foreach (var f in new[] { Filter.All, Filter.Open, Filter.Done })
        {
            var chip = new Rect(cx, y + 2f, 104f, FieldH - 4f);
            var label = f switch
            {
                Filter.Open => sent ? "Waiting" : "To play",
                Filter.Done => "Finished",
                _ => "All",
            };
            if (filter == f)
            {
                Theme.Fill(chip, Theme.Accent, 2);
            }

            if (GUI.Button(chip, label) && filter != f)
            {
                if (sent)
                {
                    _sentFilter = f;
                }
                else
                {
                    _receivedFilter = f;
                }

                _listScroll = 0f;
            }

            cx += 110f;
        }

        y += FieldH + 12f;
        var list = new Rect(area.x, y, area.width, area.yMax - y);

        if (duels == null)
        {
            GUI.Label(new Rect(list.x + 4f, list.y + 6f, list.width, 20f), "Loading…", S(Theme.Sub));
            return;
        }

        var me = Me();
        var shown = duels
            .Where(d => filter == Filter.All || (filter == Filter.Open) == IsOpenDuel(d))
            .Where(d => Matches(query, d.Opponent, d.Song, d.Artist, d.Charter))
            .OrderByDescending(d => !sent && IsOpenDuel(d)) // to play first
            .ThenByDescending(d => d.CreatedAt, StringComparer.Ordinal)
            .ToList();

        if (shown.Count == 0)
        {
            var empty = duels.Count == 0
                ? sent
                    ? "You haven't challenged anyone yet: go to New challenge."
                    : "Nobody has challenged you yet."
                : "Nothing matches.";
            GUI.Label(new Rect(list.x + 4f, list.y + 6f, list.width, 20f), empty, S(Theme.Sub));
            return;
        }

        DrawScrollList(list, shown.Count, DuelRowH, ref _listScroll, (i, row) => DrawDuel(row, shown[i], sent, me, i));
    }

    private void DrawDuel(Rect row, Duel duel, bool sent, string me, int index)
    {
        var open = IsOpenDuel(duel);
        var won = duel.Completed && string.Equals(duel.Winner, me, StringComparison.OrdinalIgnoreCase);
        var color = open ? (sent ? Waiting : Accent) : duel.Completed ? (won ? Won : Lost) : Theme.TextDim;

        Theme.Fill(row, index % 2 == 0 ? Theme.RowEven : Theme.RowOdd, 1);
        Theme.Fill(new Rect(row.x, row.y, 4f, row.height), color, 1);

        // Right: action buttons, then state and scores.
        const float buttonW = 130f;
        var button = new Rect(row.xMax - buttonW - 12f, row.y + (row.height - 28f) * 0.5f, buttonW, 28f);
        var leftmost = button.x;
        if (!sent && open)
        {
            if (_installed.Contains(duel.Checksum))
            {
                if (AccentButton(button, "Select", true))
                {
                    SelectChart(duel);
                    return;
                }
            }
            else
            {
                // Not in the library: fetched from Chorus, or searched by hand.
                var fetching = string.Equals(ChallengeDownloader.Current, duel.Checksum, StringComparison.OrdinalIgnoreCase);
                var label = fetching ? ChallengeDownloader.Progress : "Download";
                if (AccentButton(button, label, !ChallengeDownloader.Busy))
                {
                    DownloadChart(duel);
                    return;
                }

                var find = new Rect(button.x - 96f, button.y, 90f, button.height);
                leftmost = find.x;
                if (GUI.Button(find, "Find chart"))
                {
                    FindChart(duel);
                    return;
                }
            }
        }
        else if (duel.Completed)
        {
            if (GUI.Button(button, "Rematch"))
            {
                Rematch(duel);
                return;
            }
        }

        const float stateW = 250f;
        var stateX = leftmost - stateW - 14f;
        var mine = sent ? duel.ScoreToBeat : duel.ChallengedScore;
        var theirs = sent ? duel.ChallengedScore : duel.ScoreToBeat;
        string state;
        string scores;
        if (open)
        {
            state = sent ? "WAITING" : "TO PLAY";
            scores = sent ? $"your score {Fmt(mine)}" : $"beat {Fmt(theirs)}";
        }
        else if (duel.Completed)
        {
            state = won ? "WON" : "LOST";
            scores = $"{Fmt(mine)}  vs  {Fmt(theirs)}";
        }
        else
        {
            state = duel.Status.ToUpperInvariant();
            scores = sent ? $"your score {Fmt(mine)}" : $"to beat {Fmt(theirs)}";
        }

        if (_colorStyle != null)
        {
            _colorStyle.normal.textColor = color;
            _colorStyle.alignment = TextAnchor.UpperRight;
        }

        GUI.Label(new Rect(stateX, row.y + 12f, stateW, 20f), state, _colorStyle ?? S(Theme.Title));
        if (_colorStyle != null)
        {
            _colorStyle.alignment = TextAnchor.UpperLeft;
        }

        GUI.Label(new Rect(stateX, row.y + 34f, stateW, 18f), scores, S(Theme.Meta));

        // Left: song, then who / chart / when.
        var textW = stateX - row.x - 30f;
        GUI.Label(new Rect(row.x + 14f, row.y + 10f, textW, 20f), Ellipsize(Join(duel.Song, duel.Artist), textW, 7.4f), S(Theme.Title));
        var who = sent ? $"To {duel.Opponent}" : $"From {duel.Opponent}";
        var when = ShortDate(duel.Completed && duel.CompletedAt.Length > 0 ? duel.CompletedAt : duel.CreatedAt);
        var details = $"{who}  ·  {Chart(duel.Instrument, duel.Difficulty, duel.Speed, duel.Modifiers)}{(when.Length > 0 ? "  ·  " + when : "")}";
        GUI.Label(new Rect(row.x + 14f, row.y + 34f, textW, 18f), Ellipsize(details, textW, 6.4f), S(Theme.Sub));
    }

    // --- Shared ----------------------------------------------------

    /// Hand-made scrolling list (no GUILayout on this build): rows drawn at
    /// an offset inside a clipping group, wheel to scroll, bar on the right.
    private void DrawScrollList(Rect area, int count, float rowH, ref float scroll, Action<int, Rect> drawRow)
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

    private static string Fmt(long score) => score.ToString("N0", CultureInfo.InvariantCulture);

    private static string Join(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : $"{a}  ·  {b}";

    private static string Charted(string charter) => charter.Length > 0 ? $"charted by {charter}" : "";

    /// "Guitar Expert · 150% · DoubleKick"
    internal static string Chart(string instrument, string difficulty, int speed, string modifiers)
    {
        var text = $"{instrument} {difficulty}".Trim();
        if (speed > 0 && speed != 100)
        {
            text += $"  ·  {speed}%";
        }

        if (modifiers.Length > 0)
        {
            text += $"  ·  {modifiers}";
        }

        return text;
    }

    /// "2026-10-05 16:11:26" -> "Oct 5, 16:11"
    private static string ShortDate(string date)
    {
        return DateTime.TryParseExact(date, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)
            : date;
    }

    private static Color InstrumentColor(string instrument)
    {
        var name = instrument.ToLowerInvariant();
        if (name.Contains("bass"))
        {
            return Theme.Red;
        }

        if (name.Contains("drum"))
        {
            return Theme.Yellow;
        }

        if (name.Contains("key"))
        {
            return Theme.Blue;
        }

        if (name.Contains("vocal"))
        {
            return Theme.Orange;
        }

        return name.Contains("guitar") ? Theme.Green : Theme.TextDim;
    }

    /// Labels wrap instead of clipping: long text is cut to roughly fit.
    private static string Ellipsize(string text, float width, float charW)
    {
        var max = Mathf.Max(4, (int)(width / charW));
        return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    private void EnsureStyles()
    {
        if (_centerStyle != null || !Theme.Ready)
        {
            return;
        }

        try
        {
            _centerStyle = new GUIStyle(Theme.Field) { alignment = TextAnchor.MiddleCenter };
            _rightTitle = new GUIStyle(Theme.Title) { alignment = TextAnchor.UpperRight };
            _colorStyle = new GUIStyle(Theme.Title);
        }
        catch (Exception)
        {
            // Plain styles are fine.
        }
    }

    private static GUIStyle S(GUIStyle custom) =>
        Theme.Ready && custom != null ? custom : GUI.skin.label;
}
