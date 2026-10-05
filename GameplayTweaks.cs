using System;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ChorusMod;

/// <summary>
/// In-song HUD tweaks. For now: hide every player's solo counter while
/// playing multiplayer ([Gameplay] HideSoloCounterInMultiplayer).
///
/// GameManager only exists in the Gameplay scene, so finding an active one
/// means a song is loaded. Multiplayer = more than one player in the song
/// (local), or connected to an online server. Online, the game only puts
/// this PC's own players in GameManager.playerObjects -- the others live
/// in the obfuscated network code -- so being connected is the signal:
/// CHNetManager.instance's ConnectionState (the field keeps the same
/// generated name across 1.1.0.5675 and 1.1.0.6142).
///
/// SoloCounter draws through two plain uGUI graphics (textObject: UI.Text,
/// Solo_HUD: UI.Image). They're hidden with a CanvasGroup at alpha 0 on
/// each: purely visual, the counter keeps running, and the game setting
/// the graphics' own color or toggling them on and off doesn't undo it.
///
/// Polled a few times per second, like MainMenuOverlay.
///
/// DIAGNOSTIC BUILD: logs a detailed state line on every change (scene,
/// GameManager, each player) and mirrors the game's own errors into the
/// BepInEx log -- the game only writes them to Player.log, without
/// timestamps -- so both can be read in order. Used to find out why
/// online songs aren't detected.
/// </summary>
public class GameplayTweaks : MonoBehaviour
{
    public GameplayTweaks(IntPtr ptr) : base(ptr) { }

    private const float PollSeconds = 0.5f;

    private float _pollTimer;
    private string _lastState = "";

    /// Called once from Plugin.Load().
    public static void Initialize()
    {
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<GameplayTweaks>();
        var host = new GameObject("ChorusMod.GameplayTweaks");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<GameplayTweaks>();

        MirrorGameErrors();
    }

    /// Copies the game's errors and exceptions into the BepInEx log, so
    /// they land between our own lines in the order they happened.
    private static void MirrorGameErrors()
    {
        try
        {
            Application.add_logMessageReceived(
                (Application.LogCallback)new Action<string, string, LogType>(OnGameLog)
            );
            Plugin.Logger.LogInfo("Gameplay: mirroring game errors into this log.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Gameplay: can't mirror game errors: {e.Message}");
        }
    }

    private static void OnGameLog(string message, string stackTrace, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
        {
            Plugin.Logger.LogWarning($"[Game {type}] {message}\n{stackTrace}");
        }
    }

    private void Update()
    {
        _pollTimer += Time.unscaledDeltaTime;
        if (_pollTimer < PollSeconds)
        {
            return;
        }

        _pollTimer = 0f;

        try
        {
            Apply();
        }
        catch (Exception e)
        {
            // Full trace: shows exactly where, if it's ever our code.
            LogState($"Gameplay: tweak failed: {e}");
        }
    }

    private void Apply()
    {
        var scene = SceneManager.GetActiveScene().name;
        var game = UnityEngine.Object.FindObjectOfType<GameManager>();
        if (game == null)
        {
            LogState($"Gameplay: not in a song (scene='{scene}', GameManager=none)");
            return;
        }

        if (!game.playersInit)
        {
            LogState(
                $"Gameplay: not in a song (scene='{scene}', GameManager found, playersInit=False, "
                    + $"isSongPlaying={game.isSongPlaying}, actualPlayerCount={game.actualPlayerCount})"
            );
            return;
        }

        var players = game.playerObjects;
        var remote = false;
        if (players != null)
        {
            foreach (var player in players)
            {
                if (player != null && player.isRemotePlayerPlaying)
                {
                    remote = true;
                }
            }
        }

        var online = IsOnline();
        var multiplayer = game.actualPlayerCount > 1 || remote || online;
        var hide = multiplayer && Plugin.HideSoloCounterInMultiplayer.Value;

        // Diagnostic: one line per change, never per tick.
        LogState(
            $"Gameplay: scene='{scene}', playing={game.isSongPlaying}, paused={game.isPaused}, "
                + $"over={game.isSongOver}, players={game.actualPlayerCount}, remote={remote}, online={online}, "
                + $"multiplayer={multiplayer}, hideSoloCounter={hide}, {DescribePlayers(players)}"
        );

        if (players == null)
        {
            return;
        }

        foreach (var player in players)
        {
            var counter = player != null ? player.soloCounter : null;
            if (counter == null)
            {
                continue;
            }

            SetVisible(counter.textObject != null ? counter.textObject.gameObject : null, !hide);
            SetVisible(counter.Solo_HUD != null ? counter.Solo_HUD.gameObject : null, !hide);
        }
    }

    private static bool _onlineCheckFailed;

    /// Connected to an online server. Reads a generated (obfuscated) field
    /// name: if a game update renames it, this logs once and reports
    /// "not online" instead of breaking the rest.
    private static bool IsOnline()
    {
        if (_onlineCheckFailed)
        {
            return false;
        }

        try
        {
            var net = CHNetManager.instance;
            return net != null && net.field_Private_ConnectionState_0 == ConnectionState.Connected;
        }
        catch (Exception e)
        {
            _onlineCheckFailed = true;
            Plugin.Logger.LogWarning($"Gameplay: online detection unavailable on this game version: {e.Message}");
            return false;
        }
    }

    /// Per player: remote flag and whether its solo counter graphics exist.
    private static string DescribePlayers(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BasePlayer>? players)
    {
        if (players == null)
        {
            return "playerObjects=null";
        }

        var text = new StringBuilder($"playerObjects={players.Length} [");
        for (var i = 0; i < players.Length; i++)
        {
            var player = players[i];
            if (i > 0)
            {
                text.Append("; ");
            }

            if (player == null)
            {
                text.Append($"{i}:null");
                continue;
            }

            var counter = player.soloCounter;
            text.Append(
                $"{i}:remote={player.isRemotePlayerPlaying},solo="
                    + (counter == null
                        ? "none"
                        : $"text:{counter.textObject != null}/hud:{counter.Solo_HUD != null}")
            );
        }

        return text.Append(']').ToString();
    }

    /// Opacity only: the object stays active so the game's own logic is
    /// untouched. The CanvasGroup is added once and reused.
    private static void SetVisible(GameObject? target, bool visible)
    {
        if (target == null)
        {
            return;
        }

        var group = target.GetComponent<CanvasGroup>();
        if (group == null)
        {
            if (visible)
            {
                return; // never hidden, nothing to restore
            }

            group = target.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
        }

        group.alpha = visible ? 1f : 0f;
    }

    private void LogState(string state)
    {
        if (state != _lastState)
        {
            _lastState = state;
            Plugin.Logger.LogInfo(state);
        }
    }
}
