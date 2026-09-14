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
    private static readonly string LocalIndexPath = Path.Combine(
        Paths.ConfigPath, "chorus-mod-installed-local.json"
    );

    private static readonly HashSet<string> _installed = new();
    private static readonly HashSet<string> _installedLocal = new();
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
            if (File.Exists(StorePath))
            {
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
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not read installed-songs list: {e.Message}");
        }

        try
        {
            if (File.Exists(LocalIndexPath))
            {
                var json = File.ReadAllText(LocalIndexPath);
                var keys = JsonSerializer.Deserialize<string[]>(json);
                if (keys != null)
                {
                    foreach (var key in keys)
                    {
                        _installedLocal.Add(key);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not read local installed-songs index: {e.Message}");
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

    /// <summary>
    /// Artist+name index, built entirely from Clone Hero's own local
    /// library export (see LibrarySync) -- no API call involved. Less
    /// precise than the hash-backed IsInstalled/MarkInstalled pair above
    /// (two different charts sharing the exact same title and artist
    /// would both show as "installed"), but free: no rate limits, no
    /// network round-trip, works instantly on a library of any size.
    /// </summary>
    private static string LocalKey(string? artist, string? name) =>
        $"{(artist ?? "").Trim().ToLowerInvariant()}|{(name ?? "").Trim().ToLowerInvariant()}";

    public static bool IsInstalledLocally(string? artist, string? name) =>
        !string.IsNullOrWhiteSpace(artist)
        && !string.IsNullOrWhiteSpace(name)
        && _installedLocal.Contains(LocalKey(artist, name));

    /// Call on the main thread only. Adds every (artist, name) pair in one
    /// pass and persists once at the end -- calling MarkInstalledLocally
    /// per entry would be O(n^2) total across a large batch, since each
    /// call re-serializes the whole growing list to disk. Matters once
    /// indexing tens of thousands of entries at once via Sync.
    public static void MarkInstalledLocallyBatch(IEnumerable<(string? Artist, string? Name)> entries)
    {
        var changed = false;

        foreach (var (artist, name) in entries)
        {
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (_installedLocal.Add(LocalKey(artist, name)))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(new List<string>(_installedLocal));
            File.WriteAllText(LocalIndexPath, json);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not save local installed-songs index: {e.Message}");
        }
    }
}
