using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ChorusMod;

[BepInPlugin("fr.lucas.chorus-mod", "Chorus Mod", "1.0.0")]
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

        Logger.LogInfo($"Chorus Mod loaded. Songs folder: '{SongsFolder.Value}'");
        if (string.IsNullOrWhiteSpace(SongsFolder.Value))
        {
            Logger.LogWarning(
                "No Songs folder auto-detected. Set SongsFolder in "
                    + "BepInEx/config/fr.lucas.chorus-mod.cfg, otherwise "
                    + "downloads will fail."
            );
        }

        if (BlockGameInput.Value)
        {
            InputPatches.Apply("fr.lucas.chorus-mod");
        }

        InstalledSongs.EnsureLoaded();

        // A custom MonoBehaviour must be registered with the IL2CPP runtime
        // before it can be attached to a GameObject.
        ClassInjector.RegisterTypeInIl2Cpp<ChorusUI>();

        var host = new GameObject("ChorusMod.UI");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<ChorusUI>();

        Toast.Initialize();

        Logger.LogInfo($"Press {ToggleKey.Value} in-game to open Chorus Mod.");
    }

    /// Best-effort, cross-platform: looks for a plausible Songs folder.
    ///
    /// Clone Hero lets the user set their own library paths, so no
    /// detection can be 100% reliable -- hence the SongsFolder setting as a
    /// fallback. No absolute path is hardcoded: everything is rebuilt from
    /// the OS's special folders and from the game's actual location.
    private static string DetectSongsFolder()
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
