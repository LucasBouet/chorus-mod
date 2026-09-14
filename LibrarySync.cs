using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ChorusMod;

/// <summary>
/// Backfills the "already installed" index from Clone Hero's own JSON
/// song-list export (Songs menu > Export). Triggered by the "Sync" button
/// in the overlay.
///
/// This does NOT call the enchor.us API at all. Earlier versions searched
/// per artist to resolve an exact download-hash match, but that meant one
/// (or several, with retries) HTTP request per unique artist -- on a
/// library of tens of thousands of songs, that's tens of thousands of
/// requests, which the API rate-limits hard (HTTP 429) regardless of how
/// carefully the requests are paced or retried.
///
/// Instead, this indexes every local (Artist, Name) pair directly via
/// InstalledSongs.MarkInstalledLocally -- entirely offline, no network
/// call, effectively instant even on a huge library. The trade-off: it's
/// a name/artist match rather than an exact chart-hash match, so two
/// different charts sharing the exact same title and artist would both
/// show as "installed" in search results even if only one is actually on
/// disk. Acceptable given this is a visual hint, not a correctness-
/// critical identifier -- and it's the same kind of imprecision the old
/// API-based scoring already carried, just without the network cost.
/// </summary>
public static class LibrarySync
{
    private class ExportedSong
    {
        public string? Name { get; set; }
        public string? Artist { get; set; }
    }

    public class ProgressState
    {
        public int Total;
        public int Indexed;
    }

    /// Reads the export and returns everything worth indexing. Does NOT
    /// touch InstalledSongs itself -- that mutation must happen on the
    /// Unity main thread (see InstalledSongs remarks), so the caller is
    /// expected to pass the result to MarkInstalledLocallyBatch from
    /// there. Keeping this method a pure read+parse also means it's safe
    /// to call from a background thread without any locking.
    public static (ProgressState Progress, List<(string Artist, string Name)> Entries) Run()
    {
        var path = Plugin.LibraryExportPath.Value;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "LibraryExportPath isn't set. Export your library from "
                    + "Clone Hero's Songs menu, then point LibraryExportPath "
                    + "at the resulting .json file in the .cfg."
            );
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Export file not found: {path}");
        }

        List<ExportedSong>? entries;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        using (var stream = File.OpenRead(path))
        {
            entries = JsonSerializer.Deserialize<List<ExportedSong>>(stream, options);
        }

        entries ??= new List<ExportedSong>();

        var toIndex = new List<(string Artist, string Name)>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Artist))
            {
                continue;
            }

            toIndex.Add((entry.Artist!, entry.Name!));
        }

        var progress = new ProgressState { Total = entries.Count, Indexed = toIndex.Count };
        return (progress, toIndex);
    }
}
