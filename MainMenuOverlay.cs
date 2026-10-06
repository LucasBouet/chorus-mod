using System;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ChorusMod;

/// <summary>
/// Player card shown top-left, only while Clone Hero's main menu is the
/// current screen -- not song select, not settings, not in a song.
///
/// Every menu screen is a BaseMenu MonoBehaviour (MainMenu, SongSelect,
/// SettingsMenu, ...) toggled on and off by the game. FindObjectOfType
/// only returns enabled components on active objects, so a hit means the
/// main menu object is live; MainMenu also keeps the screens it opens as
/// GameObjects, which are checked on top in case the game leaves MainMenu
/// enabled underneath them. Polled a few times per second rather than
/// Harmony-patched: cheap, and nothing to unpatch if the game's menu code
/// changes.
///
/// Profile data comes from the trophy site (PlayerApi), fetched in the
/// background when the card appears and the data is stale, or right
/// after one of the player's own trophy events.
/// </summary>
public class MainMenuOverlay : MonoBehaviour
{
    public MainMenuOverlay(IntPtr ptr) : base(ptr) { }

    private const float PollSeconds = 0.2f;
    private const float RefreshSeconds = 120f;

    private const float CardWidth = 460f;
    private const float CardHeight = 118f;
    private const float ScreenMargin = 36f;
    private const float InSeconds = 0.3f;
    private const float OutSeconds = 0.2f;
    private const float SlideDistance = 40f;
    private const float AvatarSize = 72f;
    private const float TextLeft = 106f;
    private const float TextRight = 96f; // room for the level badge
    private const float XpFillSeconds = 0.8f;

    private const int GlowLayers = 5;
    private const float GlowStep = 2f;
    private const float GlowLayerAlpha = 0.045f;
    private const float GlowPulseSeconds = 2.4f;

    private static readonly Color Accent = Toast.DefaultAccent;
    private static readonly Color LevelAccent = TrophyListener.LevelAccent;

    private static volatile bool _refreshRequested;

    private bool _built;
    private float _pollTimer;
    private bool _onMainMenu;
    private string _lastState = "";

    private RectTransform? _root;
    private CanvasGroup? _canvasGroup;
    private CanvasGroup? _glowGroup;
    private float _shown;
    private float _clock;

    private TextMeshProUGUI? _nameText;
    private TextMeshProUGUI? _statsText;
    private TextMeshProUGUI? _detailText;
    private TextMeshProUGUI? _initialText;
    private RawImage? _avatarImage;

    private GameObject? _levelBlock;
    private TextMeshProUGUI? _levelText;
    private RectTransform? _xpFill;
    private TextMeshProUGUI? _xpText;
    private float _xpTarget;  // 0-1
    private float _xpShown;   // animated toward _xpTarget

    private readonly AlbumArtCache _avatars = new();
    private readonly SettingsButton _settingsButton = new();
    private readonly QuitButton _quitButton = new();
    private readonly DuelButton _duelButton = new();
    private string _avatarUrl = "";
    private bool _avatarApplied;

    private PlayerLookup? _lookupResult; // written by the fetch task
    private bool _fetching;
    private string _fetchingFor = "";
    private string? _cardUser; // username the card currently shows
    private float _lastFetchTime = float.NegativeInfinity;
    private bool _hasPlayer;

    /// Called once from Plugin.Load().
    public static void Initialize()
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<MainMenuOverlay>();
        var host = new GameObject("ChorusMod.MainMenuOverlayHost");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<MainMenuOverlay>();
    }

    /// Thread-safe: TrophyListener calls it when the player's own trophy,
    /// record or level event arrives, so the card's numbers follow.
    public static void RequestRefresh() => _refreshRequested = true;

    private void Update()
    {
        if (!_built)
        {
            BuildUI();
            _built = true;
        }

        var dt = Time.unscaledDeltaTime;
        _clock += dt;

        _pollTimer += dt;
        if (_pollTimer >= PollSeconds)
        {
            _pollTimer = 0f;
            _onMainMenu = IsOnMainMenu();
        }

        // Shown even with no username: the SETTINGS button under it is
        // where the name gets set.
        HandleStreamerKey();
        ChartSelector.Tick();

        var username = Plugin.TrophyUsername.Value.Trim();
        var visible = _onMainMenu && !Plugin.StreamerMode.Value;

        if (username != _cardUser)
        {
            // Changed from the settings window (or first frame).
            _cardUser = username;
            _hasPlayer = false;
            _refreshRequested = true;
            if (username.Length == 0)
            {
                ShowMessage("No player set", "Set your Discord name in SETTINGS", "");
            }
        }

        if (visible && username.Length > 0)
        {
            MaybeFetch(username);
        }

        ApplyLookupResult(username);
        UpdateAvatar(visible);
        Animate(visible, dt);
        _settingsButton.Tick(visible && _shown >= 1f);
        _quitButton.Tick(visible ? _shown : Mathf.Min(_shown, 0.999f));
        _duelButton.Tick(visible ? _shown : Mathf.Min(_shown, 0.999f));
    }

    private void MaybeFetch(string username)
    {
        if (_fetching)
        {
            return;
        }

        var stale = Time.unscaledTime - _lastFetchTime > RefreshSeconds;
        if (!stale && !_refreshRequested)
        {
            return;
        }

        _refreshRequested = false;
        _fetching = true;
        _fetchingFor = username;
        _lastFetchTime = Time.unscaledTime;

        if (!_hasPlayer)
        {
            ShowMessage(username, "Loading…", "");
        }

        Task.Run(async () =>
        {
            var result = await PlayerApi.FetchAsync(username).ConfigureAwait(false);
            Volatile.Write(ref _lookupResult, result);
        });
    }

    /// Main thread: picks up what the fetch task produced.
    private void ApplyLookupResult(string username)
    {
        var result = Interlocked.Exchange(ref _lookupResult, null);
        if (result == null)
        {
            return;
        }

        _fetching = false;

        if (_fetchingFor != username)
        {
            // Answer for a name that has been changed since: ask again.
            _refreshRequested = true;
            return;
        }

        switch (result.Status)
        {
            case PlayerLookupStatus.Found when result.Player != null:
                ShowPlayer(result.Player);
                break;

            case PlayerLookupStatus.NotFound:
                _hasPlayer = false;
                ShowMessage(username, "Player not found on the trophy site", "Check your Discord name in SETTINGS");
                break;

            default:
                // Keep showing the last good data if there is some.
                if (!_hasPlayer)
                {
                    ShowMessage(username, "Trophy site unreachable", "");
                }
                break;
        }
    }

    private void ShowPlayer(PlayerInfo player)
    {
        _hasPlayer = true;
        SetTexts(
            player.Name,
            $"{player.TrophyCount} trophies  ·  {player.FullCombos} FC  ·  "
                + $"Best {TrophyListener.FormatScore(player.BestScore.ToString())}",
            JoinNonEmpty(
                player.BestSong.Length > 0 ? $"Best on {player.BestSong}" : "",
                player.LatestTrophy.Length > 0 ? $"Latest: {player.LatestTrophy}" : ""
            )
        );

        SetLevel(player.Level, player.LevelProgress);

        if (player.AvatarUrl != _avatarUrl)
        {
            _avatarUrl = player.AvatarUrl;
            _avatarApplied = false;
            SetAvatar(null);
        }
    }

    private void ShowMessage(string username, string stats, string detail)
    {
        SetTexts(username, stats, detail);
        SetLevel(0, 0f);
        _avatarUrl = "";
        _avatarApplied = false;
        SetAvatar(null);
    }

    private void SetTexts(string name, string stats, string detail)
    {
        if (_nameText == null || _statsText == null || _detailText == null || _initialText == null)
        {
            return;
        }

        _nameText.text = name;
        _statsText.text = stats;
        _detailText.text = detail;
        _initialText.text = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?";
    }

    /// level 0 = the API didn't send one: the badge and XP bar hide.
    private void SetLevel(int level, float progressPercent)
    {
        if (_levelBlock == null || _levelText == null || _xpText == null)
        {
            return;
        }

        _levelBlock.SetActive(level > 0);
        _levelText.text = level.ToString();
        _xpText.text = $"{Mathf.FloorToInt(progressPercent)}%";
        _xpTarget = progressPercent / 100f;
    }

    private static string JoinNonEmpty(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : $"{a}  ·  {b}";

    /// Avatar download goes through AlbumArtCache: UnityWebRequestTexture
    /// is the only way to get a texture on this build (see there).
    private void UpdateAvatar(bool visible)
    {
        if (!visible || _avatarApplied || _avatarUrl.Length == 0 || _avatars.Disabled)
        {
            return;
        }

        _avatars.Pump();
        var texture = _avatars.Get(_avatarUrl, _avatarUrl);
        if (texture != null)
        {
            SetAvatar(texture);
            _avatarApplied = true;
        }
    }

    /// null = show the initial-letter placeholder instead.
    private void SetAvatar(Texture? texture)
    {
        if (_avatarImage == null || _initialText == null)
        {
            return;
        }

        _avatarImage.texture = texture;
        _avatarImage.enabled = texture != null;
        _initialText.enabled = texture == null;
    }

    /// Slides in from the left and fades; gentle glow pulse while shown.
    private void Animate(bool visible, float dt)
    {
        if (_root == null || _canvasGroup == null)
        {
            return;
        }

        _shown = visible
            ? Mathf.Min(1f, _shown + dt / InSeconds)
            : Mathf.Max(0f, _shown - dt / OutSeconds);

        var ease = 1f - Mathf.Pow(1f - _shown, 3f);
        _canvasGroup.alpha = _shown;
        _root.anchoredPosition = new Vector2(ScreenMargin - SlideDistance * (1f - ease), -ScreenMargin);

        if (_glowGroup != null)
        {
            _glowGroup.alpha = 0.75f + 0.1f * Mathf.Sin(_clock * 2f * Mathf.PI / GlowPulseSeconds);
        }

        if (_xpFill != null)
        {
            // Empty while hidden, so the bar fills up again on every visit.
            _xpShown = _shown <= 0f
                ? 0f
                : Mathf.MoveTowards(_xpShown, _xpTarget, dt / XpFillSeconds);
            _xpFill.anchorMax = new Vector2(_xpShown, 1f);
        }
    }

    /// Streamer mode's key, checked here because this component runs the
    /// whole session. Ignored while typing in the mod's windows (the game
    /// keys are swallowed then, this one included).
    private static void HandleStreamerKey()
    {
        var key = Plugin.StreamerModeKey.Value;
        if (key == KeyCode.None || !Input.GetKeyDown(key))
        {
            return;
        }

        var on = !Plugin.StreamerMode.Value;
        Plugin.StreamerMode.Value = on;
        Plugin.Logger.LogInfo($"Streamer mode {(on ? "on" : "off")}.");

        if (!on)
        {
            Toast.Show("Streamer mode off", "The mod's overlays are visible again.");
        }
    }

    private bool IsOnMainMenu()
    {
        try
        {
            var mainMenu = UnityEngine.Object.FindObjectOfType<MainMenu>();
            if (mainMenu == null)
            {
                LogState("MainMenu: not active");
                return false;
            }

            ApplyNewsVisibility(mainMenu);

            var songSelect = IsOpen(mainMenu.songSelect);
            var settings = IsOpen(mainMenu.settingsMenu);
            var controlMapper = IsControlMapperOpen(mainMenu);

            // Diagnostic: one line per change, never per frame.
            LogState(
                $"MainMenu: isActive={mainMenu.isActive}, transitioning={BaseMenu.transitioning}, "
                    + $"songSelect={songSelect}, settings={settings}, controlMapper={controlMapper}"
            );

            return mainMenu.isActive && !songSelect && !settings && !controlMapper;
        }
        catch (Exception e)
        {
            LogState($"MainMenu: detection failed: {e.Message}");
            return false;
        }
    }

    /// The controller mapping screen (Space on the main menu) is Rewired's
    /// ControlMapper: MainMenu stays active under it, and the card would
    /// cover its close button. Kept apart so a failure here only loses
    /// this check, not the whole detection.
    private static bool IsControlMapperOpen(MainMenu mainMenu)
    {
        try
        {
            var mapper = mainMenu.controlMapper;
            return mapper != null && mapper.isOpen;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// [Gameplay] HideNewsPanel: the game's news panel goes to opacity 0,
    /// like the solo counter in GameplayTweaks. The object stays active
    /// (the game still loads and animates it) and stops catching clicks.
    /// Re-applied on every poll, so the setting takes effect right away.
    private static void ApplyNewsVisibility(MainMenu mainMenu)
    {
        try
        {
            var news = mainMenu.news;
            if (news == null)
            {
                return;
            }

            var hide = Plugin.HideNewsPanel.Value;
            var group = news.gameObject.GetComponent<CanvasGroup>();
            if (group == null)
            {
                if (!hide)
                {
                    return; // never hidden, nothing to restore
                }

                group = news.gameObject.AddComponent<CanvasGroup>();
            }

            group.alpha = hide ? 0f : 1f;
            group.blocksRaycasts = !hide;
            group.interactable = !hide;
        }
        catch (Exception)
        {
            // Only loses the news tweak, not the menu detection.
        }
    }

    /// MainMenu holds the screens it opens as GameObjects.
    private static bool IsOpen(GameObject? screen) => screen != null && screen.activeInHierarchy;

    private void LogState(string state)
    {
        if (state != _lastState)
        {
            _lastState = state;
            Plugin.Logger.LogInfo(state);
        }
    }

    private void BuildUI()
    {
        try
        {
            // Must run BEFORE adding our own TMP texts, see UiKit.FindGameFont.
            var font = UiKit.FindGameFont();

            var canvasObject = new GameObject("ChorusMod.MainMenuCanvas");
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 900; // above the game's menus, below Toast (1000)
            UiKit.AddScaler(canvasObject);

            _root = UiKit.NewRect("Card", canvasObject.transform);
            _root.anchorMin = new Vector2(0f, 1f);
            _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 1f);
            _root.sizeDelta = new Vector2(CardWidth, CardHeight);
            _canvasGroup = _root.gameObject.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            // Same recipe as Toast: stacked faint quads behind the panel.
            var glow = UiKit.Stretch(UiKit.NewRect("Glow", _root), 0f);
            _glowGroup = glow.gameObject.AddComponent<CanvasGroup>();
            for (var i = 1; i <= GlowLayers; i++)
            {
                UiKit.Stretch(UiKit.Quad(glow, $"Glow{i}", UiKit.WithAlpha(Accent, GlowLayerAlpha)).rectTransform, i * GlowStep);
            }

            var panel = UiKit.Stretch(UiKit.NewRect("Panel", _root), 0f);
            UiKit.Stretch(UiKit.Quad(panel, "Background", UiKit.PanelColor).rectTransform, 0f);
            var sheen = UiKit.Quad(panel, "Sheen", new Color(1f, 1f, 1f, 0.03f));
            UiKit.Place(sheen.rectTransform, new Vector2(0f, 0.5f), Vector2.one, Vector2.zero, Vector2.zero);

            var border = UiKit.WithAlpha(Accent, 0.3f);
            UiKit.Place(UiKit.Quad(panel, "BorderTop", border).rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -1f), Vector2.zero);
            UiKit.Place(UiKit.Quad(panel, "BorderBottom", border).rectTransform, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 1f));
            UiKit.Place(UiKit.Quad(panel, "BorderRight", border).rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-1f, 0f), Vector2.zero);
            UiKit.Place(UiKit.Quad(panel, "AccentBar", Accent).rectTransform, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(4f, 0f));

            BuildAvatar(panel, font);

            var label = UiKit.Text(panel, "Label", font, 11f, FontStyles.Bold, Accent);
            label.characterSpacing = 6f;
            label.text = "PLAYER";
            UiKit.Place(label.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(TextLeft, -26f), new Vector2(-TextRight, -12f));

            _nameText = UiKit.Text(panel, "Name", font, 22f, FontStyles.Bold, Color.white);
            _nameText.enableAutoSizing = true;
            _nameText.fontSizeMin = 14f;
            _nameText.fontSizeMax = 22f;
            UiKit.Place(_nameText.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(TextLeft, -53f), new Vector2(-TextRight, -26f));

            _statsText = UiKit.Text(panel, "Stats", font, 13f, FontStyles.Normal, Color.white);
            _statsText.enableAutoSizing = true;
            _statsText.fontSizeMin = 9f;
            _statsText.fontSizeMax = 13f;
            UiKit.Place(_statsText.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(TextLeft, -73f), new Vector2(-TextRight, -55f));

            _detailText = UiKit.Text(panel, "Detail", font, 11f, FontStyles.Normal, UiKit.MutedText);
            _detailText.enableAutoSizing = true;
            _detailText.fontSizeMin = 8f;
            _detailText.fontSizeMax = 11f;
            UiKit.Place(_detailText.rectTransform, new Vector2(0f, 1f), Vector2.one, new Vector2(TextLeft, -92f), new Vector2(-TextRight, -75f));

            BuildLevel(panel, font);
            _settingsButton.Build(_root, font);
            _quitButton.Build(canvasObject.transform, font);
            _duelButton.Build(canvasObject.transform, font);

            Plugin.Logger.LogInfo("Player card: UI built.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Player card: failed to build UI: {e}");
        }
    }

    /// Top-right gem holding the level number, "LEVEL" under it, and an
    /// XP bar along the bottom with the percentage toward the next level.
    private void BuildLevel(RectTransform panel, TMP_FontAsset? font)
    {
        _levelBlock = UiKit.Stretch(UiKit.NewRect("Level", panel), 0f).gameObject;
        var block = _levelBlock.transform;

        var gem = UiKit.NewRect("Gem", block);
        gem.anchorMin = new Vector2(1f, 1f);
        gem.anchorMax = new Vector2(1f, 1f);
        gem.anchoredPosition = new Vector2(-50f, -42f);
        gem.sizeDelta = Vector2.zero;
        gem.localRotation = Quaternion.Euler(0f, 0f, 45f);
        Diamond(UiKit.Quad(gem, "Halo", UiKit.WithAlpha(LevelAccent, 0.15f)), 46f);
        Diamond(UiKit.Quad(gem, "Outer", LevelAccent), 40f);
        Diamond(UiKit.Quad(gem, "Inner", UiKit.PanelColor), 35f);

        // Not under the rotated gem: the number stays upright.
        _levelText = UiKit.Text(block, "Number", font, 20f, FontStyles.Bold, Color.white);
        _levelText.alignment = TextAlignmentOptions.Center;
        var number = _levelText.rectTransform;
        number.anchorMin = new Vector2(1f, 1f);
        number.anchorMax = new Vector2(1f, 1f);
        number.anchoredPosition = new Vector2(-50f, -42f);
        number.sizeDelta = new Vector2(40f, 30f);

        var caption = UiKit.Text(block, "Caption", font, 9f, FontStyles.Bold, LevelAccent);
        caption.text = "LEVEL";
        caption.characterSpacing = 4f;
        caption.alignment = TextAlignmentOptions.Center;
        UiKit.Place(caption.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-90f, -88f), new Vector2(-10f, -74f));

        var track = UiKit.Quad(block, "XpTrack", new Color(1f, 1f, 1f, 0.08f)).rectTransform;
        UiKit.Place(track, Vector2.zero, new Vector2(1f, 0f), new Vector2(TextLeft, 13f), new Vector2(-56f, 18f));
        _xpFill = UiKit.Quad(track, "XpFill", LevelAccent).rectTransform;
        UiKit.Place(_xpFill, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, Vector2.zero);

        _xpText = UiKit.Text(block, "XpPercent", font, 11f, FontStyles.Bold, LevelAccent);
        _xpText.alignment = TextAlignmentOptions.MidlineRight;
        UiKit.Place(_xpText.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-52f, 6f), new Vector2(-16f, 25f));

        _levelBlock.SetActive(false);
    }

    private static void Diamond(Image image, float size)
    {
        var rect = image.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(size, size);
    }

    /// Accent frame, dark well with the name's initial as a placeholder,
    /// and the Discord avatar on top once downloaded.
    private void BuildAvatar(RectTransform panel, TMP_FontAsset? font)
    {
        var frame = UiKit.Quad(panel, "AvatarFrame", Accent).rectTransform;
        frame.anchorMin = new Vector2(0f, 0.5f);
        frame.anchorMax = new Vector2(0f, 0.5f);
        frame.anchoredPosition = new Vector2(18f + AvatarSize / 2f, 0f);
        frame.sizeDelta = new Vector2(AvatarSize + 4f, AvatarSize + 4f);

        UiKit.Stretch(UiKit.Quad(frame, "Well", UiKit.PanelColor).rectTransform, -2f);

        _initialText = UiKit.Text(frame, "Initial", font, 34f, FontStyles.Bold, Accent);
        _initialText.alignment = TextAlignmentOptions.Center;
        UiKit.Stretch(_initialText.rectTransform, -2f);

        _avatarImage = UiKit.NewRect("Avatar", frame).gameObject.AddComponent<RawImage>();
        _avatarImage.raycastTarget = false;
        _avatarImage.enabled = false;
        UiKit.Stretch(_avatarImage.rectTransform, -2f);
    }
}
