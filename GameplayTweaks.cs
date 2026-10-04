using System;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// In-song HUD tweaks. For now: hide every player's solo counter while
/// playing multiplayer ([Gameplay] HideSoloCounterInMultiplayer).
///
/// GameManager only exists in the Gameplay scene, so finding an active one
/// means a song is loaded. Multiplayer = more than one player in the song
/// (local), or any player flagged isRemotePlayerPlaying (online).
///
/// SoloCounter draws through two plain uGUI graphics (textObject: UI.Text,
/// Solo_HUD: UI.Image). They're hidden with a CanvasGroup at alpha 0 on
/// each: purely visual, the counter keeps running, and the game setting
/// the graphics' own color or toggling them on and off doesn't undo it.
///
/// Polled a few times per second, like MainMenuOverlay.
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
            LogState($"Gameplay: tweak failed: {e.Message}");
        }
    }

    private void Apply()
    {
        var game = UnityEngine.Object.FindObjectOfType<GameManager>();
        if (game == null || !game.playersInit)
        {
            LogState("Gameplay: not in a song");
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

        var multiplayer = game.actualPlayerCount > 1 || remote;
        var hide = multiplayer && Plugin.HideSoloCounterInMultiplayer.Value;

        // Diagnostic: one line per change, never per tick.
        LogState(
            $"Gameplay: players={game.actualPlayerCount}, remote={remote}, "
                + $"multiplayer={multiplayer}, hideSoloCounter={hide}"
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
