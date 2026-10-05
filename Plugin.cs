using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ChorusMod;

[BepInPlugin("fr.lucas.chorus-mod", "Chorus Mod", "1.3.0")]
public class Plugin : BasePlugin
{
    public static ManualLogSource Logger = null!;

    public static ConfigEntry<string> SongsFolder = null!;
    public static ConfigEntry<string> ApiBaseUrl = null!;
    public static ConfigEntry<string> SearchEndpoint = null!;
    public static ConfigEntry<string> FilesBaseUrl = null!;
    public static ConfigEntry<string> Instrument = null!;
    public static ConfigEntry<bool> LogRawResponse = null!;
    public static ConfigEntry<KeyCode> ToggleKey = null!;
    public static ConfigEntry<bool> BlockGameInput = null!;
    public static ConfigEntry<bool> FullScan = null!;
    public static ConfigEntry<int> PanelOpacityLayers = null!;
    public static ConfigEntry<float> PanelWidth = null!;
    public static ConfigEntry<float> PanelHeight = null!;
    public static ConfigEntry<bool> ShowAlbumArt = null!;
    public static ConfigEntry<string> LibraryExportPath = null!;
    public static ConfigEntry<string> NtfyServer = null!;
    public static ConfigEntry<string> NtfyTopic = null!;
    public static ConfigEntry<string> TrophyUsername = null!;
    public static ConfigEntry<string> PlayerApiUrl = null!;
    public static ConfigEntry<bool> ToastTrophies = null!;
    public static ConfigEntry<bool> ToastRecords = null!;
    public static ConfigEntry<bool> ToastLevels = null!;
    public static ConfigEntry<bool> ToastChallenges = null!;
    public static ConfigEntry<bool> HideSoloCounterInMultiplayer = null!;

    public override void Load()
    {
        Logger = Log;

        SongsFolder = Config.Bind(
            "General",
            "SongsFolder",
            DetectSongsFolder(),
            "Folder where downloaded charts are installed. "
                + "Must be one of the folders Clone Hero scans."
        );

        ToggleKey = Config.Bind(
            "General",
            "ToggleKey",
            KeyCode.F9,
            "Key to open/close the search window."
        );

        BlockGameInput = Config.Bind(
            "General",
            "BlockGameInput",
            true,
            "Disables Rewired's keyboard controller while the panel is "
                + "open, so typed keys don't trigger Clone Hero's shortcuts "
                + "(Space, etc.). Controllers and guitars stay active. Set "
                + "to false if you run into issues."
        );

        FullScan = Config.Bind(
            "General",
            "FullScan",
            false,
            "false = incremental scan after install (fast). Set to true if "
                + "new songs don't show up."
        );

        PanelWidth = Config.Bind(
            "General",
            "PanelWidth",
            1180f,
            "Overlay width in pixels. Increase it on a large screen."
        );

        PanelHeight = Config.Bind(
            "General",
            "PanelHeight",
            780f,
            "Overlay height in pixels."
        );

        ShowAlbumArt = Config.Bind(
            "General",
            "ShowAlbumArt",
            true,
            "Shows album art in the list. Set to false if loading images "
                + "causes issues or slowdowns."
        );

        LibraryExportPath = Config.Bind(
            "General",
            "LibraryExportPath",
            "",
            "Path to the JSON song list exported from Clone Hero's own "
                + "Songs menu. Used by the in-game 'Sync' button to mark "
                + "existing local charts as already downloaded, purely by "
                + "matching artist and title -- entirely offline, no API "
                + "call involved."
        );

        PanelOpacityLayers = Config.Bind(
            "General",
            "PanelOpacityLayers",
            8,
            "Panel background opacity when the GUI.color tint isn't "
                + "available: number of stacked draw passes. Increase if "
                + "the background stays too transparent."
        );

        // Endpoint confirmed by network capture of the site (POST + JSON body).
        ApiBaseUrl = Config.Bind(
            "API",
            "BaseUrl",
            "https://api.enchor.us",
            "Base URL of the Chorus Encore API."
        );

        SearchEndpoint = Config.Bind(
            "API",
            "SearchEndpoint",
            "/search",
            "Path of the search endpoint (called via POST)."
        );

        FilesBaseUrl = Config.Bind(
            "API",
            "FilesBaseUrl",
            "https://files.enchor.us",
            "File host, used to rebuild a download link when the response "
                + "only provides a hash."
        );

        Instrument = Config.Bind(
            "API",
            "Instrument",
            "guitar",
            "Instrument filter sent to the server (guitar, bass, drums, "
                + "keys, vocals…). Set to 'null' to not filter."
        );

        LogRawResponse = Config.Bind(
            "API",
            "LogRawResponse",
            true,
            "Writes the raw JSON of the first result to the log. Useful "
                + "for tuning parsing; set back to false once everything "
                + "works."
        );

        NtfyServer = Config.Bind(
            "Trophies",
            "NtfyServer",
            "https://ntfy.sh",
            "ntfy server the trophy site publishes to."
        );

        NtfyTopic = Config.Bind(
            "Trophies",
            "NtfyTopic",
            "",
            "ntfy topic carrying trophy / record events. Empty = trophy "
                + "listener disabled. Anyone knowing this name can read and "
                + "publish to it: don't share it."
        );

        TrophyUsername = Config.Bind(
            "Trophies",
            "Username",
            "",
            "Your username on the trophy site: only your own events get a "
                + "toast (every event is still logged). Filled automatically "
                + "from the Rythmania Tracker app's player.json when empty. "
                + "Empty = toast every player's events."
        );

        PlayerApiUrl = Config.Bind(
            "Trophies",
            "PlayerApiUrl",
            "https://bdregieprod.com/ch_trophy_engine/api/usr.php",
            "Trophy site endpoint returning a player's profile, called with "
                + "?discordName=<Username>. Feeds the main-menu player card."
        );

        ToastTrophies = Config.Bind(
            "Notifications",
            "TrophyUnlocked",
            true,
            "Toast when you unlock a trophy. Also toggled in-game from the "
                + "SETTINGS button under the main-menu player card."
        );

        ToastRecords = Config.Bind(
            "Notifications",
            "RecordBeaten",
            true,
            "Toast when one of your records is beaten."
        );

        ToastLevels = Config.Bind(
            "Notifications",
            "LevelUp",
            true,
            "Toast when you level up."
        );

        ToastChallenges = Config.Bind(
            "Notifications",
            "ChallengeReceived",
            true,
            "Toast when another player challenges you to a duel."
        );

        HideSoloCounterInMultiplayer = Config.Bind(
            "Gameplay",
            "HideSoloCounterInMultiplayer",
            false,
            "Hides the solo percentage counter in local multiplayer and "
                + "while connected to an online server. Also toggled in-game from "
                + "the SETTINGS button under the main-menu player card."
        );

        if (string.IsNullOrWhiteSpace(TrophyUsername.Value))
        {
            var detected = DetectTrackerUsername();
            if (!string.IsNullOrEmpty(detected))
            {
                // Assigning saves it to the .cfg, where it can be edited.
                TrophyUsername.Value = detected;
                Logger.LogInfo($"Trophies: username '{detected}' read from Rythmania Tracker.");
            }
        }

        Logger.LogInfo($"Chorus Mod loaded. Songs folder: '{SongsFolder.Value}'");
        if (string.IsNullOrWhiteSpace(SongsFolder.Value))
        {
            Logger.LogWarning(
                "No Songs folder auto-detected. Set it from the SETTINGS "
                    + "button on the main menu (or SongsFolder in the .cfg), "
                    + "otherwise downloads will fail."
            );
        }

        // Always patched: BlockGameInput is checked on every call, so it
        // can be switched from the settings window without a restart.
        InputPatches.Apply("fr.lucas.chorus-mod");

        InstalledSongs.EnsureLoaded();

        // A custom MonoBehaviour must be registered with the IL2CPP runtime
        // before it can be attached to a GameObject.
        ClassInjector.RegisterTypeInIl2Cpp<ChorusUI>();

        var host = new GameObject("ChorusMod.UI");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<ChorusUI>();

        Toast.Initialize();
        MainMenuOverlay.Initialize();
        SettingsWindow.Initialize();
        GameplayTweaks.Initialize();

        TrophyListener.Start();

        Logger.LogInfo($"Press {ToggleKey.Value} in-game to open Chorus Mod.");
    }

    /// The Rythmania Tracker desktop app stores the player's Discord name
    /// -- the name the trophy site knows them by -- in
    /// %APPDATA%\Rythmania Tracker\player.json:
    /// {"discordId": "...", "discordName": "..."}. Best-effort: any problem
    /// just means no auto-fill.
    internal static string DetectTrackerUsername()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Rythmania Tracker",
                "player.json"
            );
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("discordName", out var name)
                && name.ValueKind == JsonValueKind.String
                ? name.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Trophies: couldn't read Rythmania Tracker's player.json: {e.Message}");
            return string.Empty;
        }
    }

    /// Best-effort, cross-platform: looks for a plausible Songs folder.
    ///
    /// Clone Hero lets the user set their own library paths, so no
    /// detection can be 100% reliable -- hence the SongsFolder setting as a
    /// fallback. No absolute path is hardcoded: everything is rebuilt from
    /// the OS's special folders and from the game's actual location.
    internal static string DetectSongsFolder()
    {
        foreach (var path in CandidateSongFolders())
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> CandidateSongFolders()
    {
        // 1. Next to the executable: valid on every platform.
        yield return Path.Combine(Paths.GameRootPath, "Songs");

        var documents = Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments
        );
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile
        );

        // 2. "Windows-style" location, which also exists under Proton and
        // that .NET maps to ~/Documents on Linux.
        if (!string.IsNullOrEmpty(documents))
        {
            yield return Path.Combine(documents, "Clone Hero", "Songs");
            yield return Path.Combine(documents, "Songs");
        }

        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }

        // 3. Linux/XDG conventions.
        yield return Path.Combine(home, "Clone Hero", "Songs");
        yield return Path.Combine(home, ".local", "share", "Clone Hero", "Songs");
        yield return Path.Combine(home, "Music", "Clone Hero", "Songs");
        yield return Path.Combine(home, "Musique", "Clone Hero", "Songs");

        // 4. Proton prefix: when the Windows build runs under Steam Play,
        // "My Documents" lives inside the game's Wine prefix. We walk up
        // from the game folder instead of guessing the AppID.
        foreach (var path in ProtonPrefixCandidates())
        {
            yield return path;
        }
    }

    /// .../steamapps/common/Clone Hero -> .../steamapps/compatdata/*/pfx/
    /// drive_c/users/steamuser/Documents/Clone Hero/Songs
    private static IEnumerable<string> ProtonPrefixCandidates()
    {
        string? steamapps = null;

        try
        {
            var dir = new DirectoryInfo(Paths.GameRootPath);
            while (dir != null)
            {
                if (string.Equals(dir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    steamapps = dir.FullName;
                    break;
                }

                dir = dir.Parent;
            }
        }
        catch (Exception)
        {
            yield break;
        }

        if (steamapps == null)
        {
            yield break;
        }

        var compatdata = Path.Combine(steamapps, "compatdata");
        string[] prefixes;

        try
        {
            prefixes = Directory.Exists(compatdata)
                ? Directory.GetDirectories(compatdata)
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var prefix in prefixes)
        {
            yield return Path.Combine(
                prefix,
                "pfx",
                "drive_c",
                "users",
                "steamuser",
                "Documents",
                "Clone Hero",
                "Songs"
            );
        }
    }
}
