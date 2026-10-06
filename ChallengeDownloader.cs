using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Duel window's Download: gets a received challenge's chart from Chorus
/// (enchor.us) when it isn't in the library, rescans, then selects it like
/// Select does.
///
/// The challenge only gives Clone Hero's checksum, the MD5 of the chart's
/// notes.mid / notes.chart; Chorus doesn't index that one (its own md5 is
/// of the whole package). So charts are searched by title and artist, the
/// challenge's charter first, and each candidate is downloaded and its
/// notes file hashed: only the one with the exact checksum is installed,
/// never a look-alike version the challenge wouldn't count.
///
/// One download at a time. Network and hashing run off the main thread;
/// the rescan and the wait for the chart to show up in the library run in
/// Tick() (called from MainMenuOverlay, which runs all session).
/// </summary>
public static class ChallengeDownloader
{
    private const int MaxCandidates = 6;
    private const float PollSeconds = 0.5f;
    private const float ScanTimeoutSeconds = 240f;

    private enum Stage
    {
        Idle,
        Fetching,  // searching / downloading / installing, off the main thread
        Scanning,  // installed, waiting for the library to list it
    }

    private static readonly ConcurrentQueue<Action> MainThread = new();

    private static Stage _stage = Stage.Idle;
    private static Duel? _duel;
    private static volatile string _progress = "";

    private static bool _scanStarted;
    private static bool _scanSeen;
    private static float _scanSince;
    private static float _nextPoll;

    /// Checksum (upper case) of the chart being fetched, null if none.
    public static string? Current => _duel?.Checksum.ToUpperInvariant();

    /// What's happening, for the duel window's row ("Downloading 2/4…").
    public static string Progress => _progress;

    public static bool Busy => _stage != Stage.Idle;

    /// Starts fetching the duel's chart. False if another one is running.
    public static bool Start(Duel duel)
    {
        if (Busy || duel.Checksum.Length == 0)
        {
            return false;
        }

        _duel = duel;
        _stage = Stage.Fetching;
        _progress = "Searching…";
        var label = Label(duel);
        Plugin.Logger.LogInfo($"Challenge download: looking for {label} ({duel.Checksum}) on Chorus.");

        Task.Run(async () =>
        {
            try
            {
                var installed = await FetchAsync(duel).ConfigureAwait(false);
                MainThread.Enqueue(() =>
                {
                    if (installed)
                    {
                        StartScan();
                    }
                    else
                    {
                        Fail("Not found on Chorus", $"No version of {label} with this exact chart: use Find chart.");
                    }
                });
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Challenge download failed: {e}");
                MainThread.Enqueue(() => Fail("Download failed", $"{label}: {e.Message}"));
            }
        });

        return true;
    }

    /// Called every frame.
    public static void Tick()
    {
        while (MainThread.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Challenge download: deferred action failed: {e}");
            }
        }

        if (_stage != Stage.Scanning || _duel == null || Time.unscaledTime < _nextPoll)
        {
            return;
        }

        _nextPoll = Time.unscaledTime + PollSeconds;
        var scanning = SongInstaller.IsScanning();
        _scanSeen |= scanning;

        if (ChartSelector.IsInstalled(_duel.Checksum))
        {
            Done();
            return;
        }

        var waited = Time.unscaledTime - _scanSince;
        var scanOver = _scanSeen && !scanning;
        var noScan = !_scanStarted && !scanning && waited > 10f;
        if (scanOver || noScan || waited > ScanTimeoutSeconds)
        {
            Fail(
                "Chart installed, not listed yet",
                _scanStarted
                    ? $"{Label(_duel)} is in your Songs folder but the scan missed it: rescan from the game (or set FullScan)."
                    : $"{Label(_duel)} is in your Songs folder: rescan from the game's settings to play it."
            );
        }
    }

    // ---------------------------------------------------------------
    //  Background: search, download, check, install
    // ---------------------------------------------------------------

    /// True once the exact chart is installed.
    private static async Task<bool> FetchAsync(Duel duel)
    {
        var candidates = new List<SongResult>();
        var seen = new HashSet<string>();
        foreach (var query in new[] { $"{duel.Song} {duel.Artist}", duel.Song })
        {
            if (query.Trim().Length == 0)
            {
                continue;
            }

            var page = await EnchorClient.SearchAsync(query.Trim(), "", "", "", new Dictionary<string, bool?>(), 1)
                .ConfigureAwait(false);
            foreach (var song in page.Songs)
            {
                if (!song.RequiresBrowser && seen.Add(song.DownloadUrl))
                {
                    candidates.Add(song);
                }
            }
        }

        var ranked = candidates
            .OrderByDescending(s => Same(s.Charter, duel.Charter))
            .ThenByDescending(s => Same(s.Name, duel.Song))
            .ThenByDescending(s => Same(s.Artist, duel.Artist))
            .Take(MaxCandidates)
            .ToList();
        Plugin.Logger.LogInfo(
            $"Challenge download: {candidates.Count} result(s), trying {ranked.Count}: "
                + string.Join(", ", ranked.Select(s => $"{s.Artist} - {s.Name} by {s.Charter}"))
        );

        for (var i = 0; i < ranked.Count; i++)
        {
            var song = ranked[i];
            _progress = ranked.Count > 1 ? $"Downloading {i + 1}/{ranked.Count}…" : "Downloading…";

            byte[] data;
            try
            {
                data = await EnchorClient.DownloadAsync(song.DownloadUrl).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Challenge download: {song.DownloadUrl} failed: {e.Message}");
                continue;
            }

            var checksum = NotesChecksum(data);
            var match = string.Equals(checksum, duel.Checksum, StringComparison.OrdinalIgnoreCase);
            Plugin.Logger.LogInfo(
                $"Challenge download: {song.Artist} - {song.Name} by {song.Charter} has chart {checksum ?? "none"}"
                    + (match ? ", that's the one." : ", not the one.")
            );
            if (!match)
            {
                continue;
            }

            _progress = "Installing…";
            var path = SongInstaller.Install(data, $"{song.Artist} - {song.Name}");
            Plugin.Logger.LogInfo($"Challenge download: installed to {path}");
            MainThread.Enqueue(() => InstalledSongs.MarkInstalled(song.DownloadUrl));
            return true;
        }

        return false;
    }

    /// MD5 of the notes file in a .sng or a zip, null if there's none.
    private static string? NotesChecksum(byte[] data)
    {
        try
        {
            if (SngPackage.IsSng(data))
            {
                return SngPackage.NotesChecksum(data);
            }

            if (data.Length < 2 || data[0] != 'P' || data[1] != 'K')
            {
                return null;
            }

            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            foreach (var preferred in new[] { "notes.mid", "notes.chart" })
            {
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, preferred, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    return SngPackage.Md5(buffer.ToArray());
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Challenge download: couldn't read the chart package: {e.Message}");
        }

        return null;
    }

    // ---------------------------------------------------------------
    //  Main thread: rescan, then select
    // ---------------------------------------------------------------
    private static void StartScan()
    {
        _stage = Stage.Scanning;
        _progress = "Scanning…";
        _scanSince = Time.unscaledTime;
        _nextPoll = 0f;
        _scanSeen = false;
        // Already scanning counts as started: the new chart may be in it.
        _scanStarted = SongInstaller.TriggerRescan() || SongInstaller.IsScanning();
    }

    private static void Done()
    {
        var duel = _duel!;
        Reset();
        DuelWindow.RequestRefresh();
        Plugin.Logger.LogInfo($"Challenge download: {Label(duel)} is in the library, selecting it.");

        if (!ChartSelector.Request(duel.Checksum, Label(duel)))
        {
            return;
        }

        DuelWindow.Close();
        Toast.Show(
            ChartSelector.OpensQuickplay ? "Chart downloaded · opening Quickplay" : "Chart downloaded · open Quickplay",
            $"{duel.Song} will be selected · play it on {DuelWindow.Chart(duel.Instrument, duel.Difficulty, duel.Speed, duel.Modifiers)}",
            9f,
            TrophyListener.ChallengeAccent,
            "Duel"
        );
    }

    private static void Fail(string title, string message)
    {
        Plugin.Logger.LogInfo($"Challenge download: {title}. {message}");
        Reset();
        DuelWindow.RequestRefresh();
        Toast.Show(title, message, 8f, TrophyListener.ChallengeAccent, "Duel");
    }

    private static void Reset()
    {
        _stage = Stage.Idle;
        _duel = null;
        _progress = "";
    }

    private static string Label(Duel duel) =>
        duel.Artist.Length > 0 ? $"{duel.Artist} - {duel.Song}" : duel.Song;

    private static bool Same(string a, string b) =>
        a.Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
