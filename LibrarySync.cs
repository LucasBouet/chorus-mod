using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChorusMod;

/// <summary>
/// Backfills InstalledSongs from Clone Hero's own JSON song-list export
/// (Songs menu > Export). Not automatic on its own -- triggered by the
/// "Sync" button in the overlay, since it makes one API search per local
/// song and shouldn't run silently in the background.
///
/// Matching is best-effort: the export has no hash/checksum, only
/// metadata (Name, Artist, Charter, song length in ms). Each local entry
/// is searched by artist -- narrower and more relevant than a free-text
/// title search, which drowns in noise for generic titles -- then scored
/// against the candidates returned by the API on name/charter/length
/// closeness. Anything below the confidence threshold is left unmatched
/// rather than guessed.
///
/// Known limitation: only the first results page is checked per artist.
/// An artist with many charts where the right one falls on a later page
/// will be missed. Fine for the common case; worth revisiting if it
/// turns out to matter in practice.
/// </summary>
public static class LibrarySync
{
    private const int MinScoreToAccept = 5;
    private const int LengthToleranceMs = 2000;

    private class ExportedSong
    {
        public string? Name { get; set; }
        public string? Artist { get; set; }
        public string? Charter { get; set; }
        public int SongLength { get; set; }
    }

    public class ProgressState
    {
        public int Total;
        public int Done;
        public int Matched;
    }

    /// Reads the export, searches enchor.us for each entry, and calls
    /// markInstalled for every confident match. Both callbacks are
    /// invoked from whatever thread this method runs on -- callers
    /// running it off the main thread (as ChorusUI does) are expected to
    /// marshal back themselves, since InstalledSongs.MarkInstalled must
    /// only run on the main thread.
    public static async Task RunAsync(
        Action<ProgressState> onProgress,
        Action<string> markInstalled
    )
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
            entries = await JsonSerializer
                .DeserializeAsync<List<ExportedSong>>(stream, options)
                .ConfigureAwait(false);
        }

        entries ??= new List<ExportedSong>();

        var progress = new ProgressState { Total = entries.Count };

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Artist))
            {
                progress.Done++;
                onProgress(progress);
                continue;
            }

            try
            {
                var result = await EnchorClient
                    .SearchAsync(
                        entry.Artist,
                        "null",
                        "null",
                        "artist",
                        new Dictionary<string, bool?>(),
                        1
                    )
                    .ConfigureAwait(false);

                var best = FindBestMatch(entry, result.Songs);
                if (best != null)
                {
                    progress.Matched++;
                    markInstalled(best.DownloadUrl);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning(
                    $"Sync: search failed for '{entry.Artist} - {entry.Name}': {e.Message}"
                );
            }

            progress.Done++;
            onProgress(progress);

            // Gentle pacing: a library can easily be several hundred
            // entries, no reason to hammer the API back to back.
            await Task.Delay(300).ConfigureAwait(false);
        }
    }

    private static SongResult? FindBestMatch(ExportedSong entry, List<SongResult> candidates)
    {
        SongResult? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            var score = Score(entry, candidate);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return bestScore >= MinScoreToAccept ? best : null;
    }

    private static int Score(ExportedSong entry, SongResult candidate)
    {
        var score = 0;
        var entryName = Normalize(entry.Name);
        var candidateName = Normalize(candidate.Name);

        if (entryName == candidateName)
        {
            score += 3;
        }
        else if (candidateName.Contains(entryName) || entryName.Contains(candidateName))
        {
            score += 1;
        }

        if (
            !string.IsNullOrEmpty(entry.Charter)
            && Normalize(entry.Charter) == Normalize(candidate.Charter)
        )
        {
            score += 2;
        }

        if (
            entry.SongLength > 0
            && candidate.LengthMs > 0
            && Math.Abs(entry.SongLength - candidate.LengthMs) <= LengthToleranceMs
        )
        {
            score += 2;
        }

        return score;
    }

    private static string Normalize(string? s) => (s ?? "").Trim().ToLowerInvariant();
}
