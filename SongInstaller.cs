using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChorusMod;

public static class SongInstaller
{
    /// Writes the downloaded bytes to the Songs folder. Automatically
    /// detects zip vs. a standalone .sng file.
    /// Call from a background thread (blocking I/O).
    public static string Install(byte[] data, string songName)
    {
        var root = Plugin.SongsFolder.Value;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "Songs folder not configured (see BepInEx/config/fr.lucas.chorus-mod.cfg)."
            );
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Songs folder not found: {root}");
        }

        var folderName = Sanitize(songName);
        var targetDir = Path.Combine(root, folderName);

        if (IsZip(data))
        {
            ExtractZip(data, targetDir);
        }
        else
        {
            // Not a zip: most likely a standalone .sng. Place it in its own
            // folder so the scanner picks it up properly.
            Directory.CreateDirectory(targetDir);
            File.WriteAllBytes(Path.Combine(targetDir, folderName + ".sng"), data);
        }

        return targetDir;
    }

    private static bool IsZip(byte[] data) =>
        data.Length >= 2 && data[0] == 'P' && data[1] == 'K';

    private static void ExtractZip(byte[] data, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        using var stream = new MemoryStream(data);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var fullTarget = Path.GetFullPath(targetDir);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // folder
            }

            var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));

            // Zip-slip guard: reject any entry that would escape the target
            // folder via ../ in its path.
            if (!destPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger.LogWarning($"Suspicious zip entry ignored: {entry.FullName}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    /// Characters forbidden on Windows. Deliberately applied on ALL
    /// platforms, using the strictest list: Path.GetInvalidFileNameChars()
    /// only returns '/' and '\0' on Linux, which would produce folders like
    /// "AC/DC - T.N.T" — unreadable if the library is later shared with or
    /// synced to a Windows machine.
    private static readonly char[] ForbiddenChars =
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*',
    };

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            // Control characters included (0-31), forbidden everywhere.
            builder.Append(
                c < 32 || Array.IndexOf(ForbiddenChars, c) >= 0 ? '_' : c
            );
        }

        // Windows also rejects trailing dots/spaces in names.
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');

        return cleaned.Length == 0 ? "chart" : cleaned;
    }

    /// Triggers a library rescan.
    /// MUST be called on the Unity main thread.
    public static bool TriggerRescan()
    {
        var songScan = FindSongScan();
        if (songScan == null)
        {
            Plugin.Logger.LogWarning(
                "SongScan not found (neither active nor inactive): trigger "
                    + "the scan manually from the game menu."
            );
            return false;
        }

        if (songScan.isScanning)
        {
            Plugin.Logger.LogInfo("A scan is already in progress.");
            return false;
        }

        // false = incremental scan. If new songs don't show up, set
        // FullScan to true in the .cfg.
        var full = Plugin.FullScan.Value;
        Plugin.Logger.LogInfo($"Starting rescan (fullScan={full})…");
        songScan.Method_Public_Coroutine_Boolean_0(full);
        return true;
    }

    /// Object.FindObjectOfType ignores disabled GameObjects, and SongScan
    /// lives on the scan overlay which is hidden most of the time.
    /// Resources.FindObjectsOfTypeAll, on the other hand, sees those too.
    private static SongScan? FindSongScan()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll<SongScan>();
            if (all != null && all.Length > 0)
            {
                Plugin.Logger.LogInfo($"SongScan found ({all.Length} instance(s)).");
                return all[0];
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"FindObjectsOfTypeAll unavailable: {e.Message}");
        }

        // Fall back to the classic lookup (active objects only).
        return Object.FindObjectOfType<SongScan>();
    }
}
