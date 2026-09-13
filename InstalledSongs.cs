using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace ChorusMod;

/// <summary>
/// Tracks which charts have already been downloaded through the mod, so
/// the search results can highlight them. Keyed by DownloadUrl (unique
/// per chart, already available on every SongResult -- no need to touch
/// the API client).
///
/// All reads/writes happen on the Unity main thread: StartDownload in
/// ChorusUI already marshals its completion callback back via
/// _mainThread.Enqueue, so MarkInstalled() is called from there. No
/// locking needed.
/// </summary>
public static class InstalledSongs
{
    private static readonly string StorePath = Path.Combine(
        Paths.ConfigPath, "chorus-mod-installed.json"
    );

    private static readonly HashSet<string> _installed = new();
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;

        try
        {
            if (!File.Exists(StorePath))
            {
                return;
            }

            var json = File.ReadAllText(StorePath);
            var urls = JsonSerializer.Deserialize<string[]>(json);
            if (urls != null)
            {
                foreach (var url in urls)
                {
                    _installed.Add(url);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not read installed-songs list: {e.Message}");
        }
    }

    public static bool IsInstalled(string downloadUrl) =>
        !string.IsNullOrEmpty(downloadUrl) && _installed.Contains(downloadUrl);

    /// Call on the main thread only (see class remarks).
    public static void MarkInstalled(string downloadUrl)
    {
        if (string.IsNullOrEmpty(downloadUrl) || !_installed.Add(downloadUrl))
        {
            return; // already known, nothing to persist
        }

        try
        {
            var json = JsonSerializer.Serialize(new List<string>(_installed));
            File.WriteAllText(StorePath, json);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not save installed-songs list: {e.Message}");
        }
    }
}
