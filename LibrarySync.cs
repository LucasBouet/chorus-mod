using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChorusMod;

/// <summary>
/// Backfills InstalledSongs from Clone Hero's own JSON song-list export
/// (Songs menu > Export). Not automatic on its own -- triggered by the
/// "Sync" button in the overlay.
///
/// Matching is best-effort: the export has no hash/checksum, only
/// metadata (Name, Artist, Charter, song length in ms). Local entries are
/// grouped by artist so ONE search can satisfy every local song by that
/// artist, instead of one search per song -- on a typical library this
/// alone can cut the number of API calls significantly, though the exact
/// ratio depends on how concentrated the library is around a few artists
/// (measured ~1.6 songs/artist on a 769-song sample; denser libraries
/// will do better). Requests additionally run with bounded concurrency
/// rather than one at a time with a fixed delay, which is the dominant
/// speedup on a large library (tens of thousands of songs would otherwise
/// take hours).
///
/// Within each artist's result set, every candidate is scored against
/// every local entry for that artist on name/charter/length closeness.
/// Anything below the confidence threshold is left unmatched rather than
/// guessed.
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

    /// Reads the export, searches enchor.us once per unique artist (with
    /// bounded concurrency), and calls markInstalled for every confident
    /// match. Both callbacks may be invoked concurrently from multiple
    /// background tasks; callers running this off the main thread (as
    /// ChorusUI does) are expected to marshal back themselves, since
    /// InstalledSongs.MarkInstalled must only run on the main thread --
    /// but the callbacks themselves don't need to be thread-safe beyond
    /// that, since ConcurrentQueue.Enqueue (used for that marshaling)
    /// already tolerates concurrent callers.
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

        // Group by normalized artist: one search response can then be
        // matched against every local song by that artist.
        var byArtist = new Dictionary<string, List<ExportedSong>>();
        var groupedCount = 0;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Artist))
            {
                continue;
            }

            groupedCount++;
            var key = Normalize(entry.Artist);
            if (!byArtist.TryGetValue(key, out var list))
            {
                list = new List<ExportedSong>();
                byArtist[key] = list;
            }
            list.Add(entry);
        }

        var progress = new ProgressState
        {
            Total = entries.Count,
            // Entries missing Name/Artist are skipped entirely -- count
            // them as already "done" so the progress bar still reaches
            // Total by the end.
            Done = entries.Count - groupedCount,
        };
        onProgress(progress);

        var concurrency = Math.Max(1, Plugin.SyncConcurrency.Value);
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>(byArtist.Count);

        foreach (var group in byArtist.Values)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            tasks.Add(ProcessArtistGroupAsync(group, gate, progress, onProgress, markInstalled));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task ProcessArtistGroupAsync(
        List<ExportedSong> group,
        SemaphoreSlim gate,
        ProgressState progress,
        Action<ProgressState> onProgress,
        Action<string> markInstalled
    )
    {
        try
        {
            List<SongResult> candidates;

            try
            {
                var result = await EnchorClient
                    .SearchAsync(
                        group[0].Artist!,
                        "null",
                        "null",
                        "artist",
                        new Dictionary<string, bool?>(),
                        1
                    )
                    .ConfigureAwait(false);
                candidates = result.Songs;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning(
                    $"Sync: search failed for artist '{group[0].Artist}': {e.Message}"
                );
                candidates = new List<SongResult>();
            }

            // Zero candidates for an entire artist almost always means the
            // artist-field search didn't recognize the name as written
            // locally (formatting, special characters, "feat." suffixes...)
            // rather than that artist being absent from enchor.us entirely.
            // Worth surfacing distinctly from a plain low-confidence miss.
            if (candidates.Count == 0 && Plugin.LogRawResponse.Value)
            {
                Plugin.Logger.LogInfo(
                    $"Sync: no results at all for artist '{group[0].Artist}' "
                        + $"({group.Count} local song(s) affected) -- possible "
                        + "name formatting mismatch with enchor.us."
                );
            }

            foreach (var entry in group)
            {
                var best = FindBestMatch(entry, candidates, out var bestScore);
                if (best != null)
                {
                    Interlocked.Increment(ref progress.Matched);
                    markInstalled(best.DownloadUrl);
                }
                else if (candidates.Count > 0 && Plugin.LogRawResponse.Value)
                {
                    // Candidates existed but none were confident enough --
                    // different failure mode from "artist not found at all".
                    Plugin.Logger.LogInfo(
                        $"Sync: no confident match for '{entry.Artist} - {entry.Name}' "
                            + $"(best score {bestScore}/{MinScoreToAccept} needed)."
                    );
                }

                Interlocked.Increment(ref progress.Done);
            }

            onProgress(progress);
        }
        finally
        {
            gate.Release();
        }
    }

    private static SongResult? FindBestMatch(
        ExportedSong entry,
        List<SongResult> candidates,
        out int bestScore
    )
    {
        SongResult? best = null;
        bestScore = 0;

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

        // Previously relied only on the search having been artist-scoped
        // to imply an artist match, without scoring it directly -- which
        // meant an exact name + exact artist pair still needed charter or
        // length to agree on top of that. That's redundant: if both name
        // and artist line up exactly, that's already strong enough on its
        // own, and it's the single biggest fix for real matches getting
        // rejected over a charter that got re-uploaded under a different
        // name, or a duration that drifted by a couple seconds.
        var entryArtist = Normalize(entry.Artist);
        var candidateArtist = Normalize(candidate.Artist);

        if (entryArtist == candidateArtist)
        {
            score += 2;
        }
        else if (
            !string.IsNullOrEmpty(entryArtist)
            && (candidateArtist.Contains(entryArtist) || entryArtist.Contains(candidateArtist))
        )
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
