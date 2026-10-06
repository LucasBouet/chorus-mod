using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Listens to the trophy site's ntfy topic over SSE, logs every event as
/// it arrives and shows a toast for it.
///
/// ntfy SSE format (docs.ntfy.sh/subscribe/api): every event is a single
/// "data:" line holding a JSON object whose "event" field says what it is
/// (open, keepalive, message, ...). Real messages carry no SSE "event:" or
/// "id:" line, so the SSE framing is only used to split events; all the
/// meaning comes from the JSON. The site publishes its own JSON
/// ({"event":"trophy_unlocked", "payload":{...}, ...}) as the ntfy message
/// body, so it arrives as a string in the "message" field and gets parsed
/// a second time.
///
/// The topic is shared by every player: every event is logged, but only
/// the configured Username's events get a toast (all of them if it's
/// empty).
///
/// ntfy has no Last-Event-ID support; resuming after a drop goes through
/// ?since=&lt;message id or unix time&gt; instead, and the ids already seen
/// are remembered so the replayed overlap isn't logged twice.
///
/// Events sent while the game was closed: the time of the last message
/// received and the latest ids are saved to BepInEx/config
/// (chorus-mod-trophies-state.json). At the next launch the stream opens
/// with ?since=&lt;that time&gt;, so ntfy first replays everything cached
/// since then (ntfy.sh keeps 12 h) and goes on live; the saved ids drop
/// the one message that is replayed again (since= is inclusive). With no
/// saved state (first launch, new topic) it starts live only, rather than
/// replaying 12 h of other players' events.
/// </summary>
public static class TrophyListener
{
    // ntfy.sh sends a keepalive every ~45 s. With no read timeout on the
    // stream, a half-open connection (Wi-Fi drop, sleep) would hang
    // forever, so silence longer than this counts as a dead connection.
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(120);

    private const float ToastSeconds = 5f;

    internal static readonly Color TrophyAccent = new(1f, 0.76f, 0.22f);
    internal static readonly Color RecordAccent = new(1f, 0.32f, 0.45f);
    internal static readonly Color LevelAccent = new(0.68f, 0.38f, 1f);
    internal static readonly Color ChallengeAccent = new(1f, 0.48f, 0.12f);
    internal static readonly Color AnnouncementAccent = new(0.25f, 0.92f, 0.78f);
    internal static readonly Color DuelWonAccent = new(0.32f, 0.90f, 0.42f);
    internal static readonly Color DuelLostAccent = new(0.58f, 0.63f, 0.74f);

    // Announcements are free text meant to be read: longer on screen.
    private const float AnnouncementSeconds = 8f;

    private static readonly int[] RetryDelaysSeconds = { 1, 2, 5, 10, 30 };

    private static readonly HttpClient Http = CreateClient();

    private static readonly HashSet<string> SeenMessageIds = new();

    private static readonly object Gate = new();

    private static readonly string StatePath = Path.Combine(
        Paths.ConfigPath, "chorus-mod-trophies-state.json"
    );

    // Enough to cover several messages sharing the last second.
    private const int SavedIdCount = 20;

    private static string? _lastMessageId;
    private static long _lastEventTime;
    private static long _openTime;
    private static CancellationTokenSource? _run;

    // What gets saved: tied to the topic and server it came from.
    private static string _stateKey = "";
    private static long _lastMessageTime;
    private static readonly Queue<string> RecentIds = new();

    private static HttpClient CreateClient()
    {
        // Dedicated client: EnchorClient's 30 s timeout would kill the stream.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("Accept", "text/event-stream");
        client.DefaultRequestHeaders.Add("User-Agent", "ChorusMod/1.0");
        return client;
    }

    /// Called once from Plugin.Load(). Does nothing if no topic is set.
    public static void Start() => Restart();

    /// Stops the current stream, if any, and listens again with the
    /// current settings: called when the topic or the server is changed
    /// from the settings window.
    public static void Restart()
    {
        lock (Gate)
        {
            if (_run != null)
            {
                _run.Cancel();
                _run = null;
                Plugin.Logger.LogInfo("Trophies: listener stopped (settings changed).");
            }

            _lastMessageId = null;
            _lastEventTime = 0;
            _lastMessageTime = 0;
            RecentIds.Clear();

            var topic = Plugin.NtfyTopic.Value.Trim();
            if (string.IsNullOrEmpty(topic))
            {
                Plugin.Logger.LogInfo(
                    "Trophies: no notification channel (NtfyTopic) set, listener disabled."
                );
                return;
            }

            var url = $"{Plugin.NtfyServer.Value.Trim().TrimEnd('/')}/{topic}/sse";

            // Picks up where the last session (on this same channel) left
            // off: the first connection then replays what was missed.
            _stateKey = url;
            LoadState();
            var run = new CancellationTokenSource();
            _run = run;

            // Thread-pool threads are background threads: this never keeps
            // the game process alive on exit.
            Task.Run(() => RunAsync(url, run.Token));
        }
    }

    private static async Task RunAsync(string baseUrl, CancellationToken stop)
    {
        var attempt = 0;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                var receivedAny = await ListenOnceAsync(ResumeUrl(baseUrl), stop)
                    .ConfigureAwait(false);
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                if (receivedAny)
                {
                    attempt = 0;
                }

                Plugin.Logger.LogWarning("Trophies: stream closed, reconnecting.");
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Trophies: stream error: {e.Message}");
            }

            var delay = RetryDelaysSeconds[Math.Min(attempt, RetryDelaysSeconds.Length - 1)];
            attempt++;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// First connection: since the last message of the previous session if
    /// one was saved (see LoadState), otherwise live events only.
    /// Reconnections: resume after the last message, or after the last
    /// event time if no message has arrived yet (keepalive ids aren't
    /// message ids, ntfy wouldn't accept them in since=).
    private static string ResumeUrl(string baseUrl)
    {
        if (_lastMessageId != null)
        {
            return $"{baseUrl}?since={Uri.EscapeDataString(_lastMessageId)}";
        }

        return _lastEventTime > 0 ? $"{baseUrl}?since={_lastEventTime}" : baseUrl;
    }

    /// Returns whether at least one event came through, to reset backoff.
    private static async Task<bool> ListenOnceAsync(string url, CancellationToken stop)
    {
        Plugin.Logger.LogInfo($"Trophies: connecting to {url}");

        // Fires on silence (CancelAfter below) or on Restart().
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var response = await Http.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, url),
                HttpCompletionOption.ResponseHeadersRead,
                watchdog.Token
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        // ReadLineAsync takes no token on .NET 6: disposing the response is
        // what actually aborts a read stuck on a silent connection.
        using var abort = watchdog.Token.Register(() => response.Dispose());

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var data = new StringBuilder();
        var receivedAny = false;
        watchdog.CancelAfter(SilenceTimeout);

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return receivedAny;
            }
            catch (Exception) when (watchdog.IsCancellationRequested)
            {
                Plugin.Logger.LogWarning(
                    $"Trophies: nothing received for {SilenceTimeout.TotalSeconds:0} s, "
                        + "connection considered dead."
                );
                return receivedAny;
            }

            if (line == null || stop.IsCancellationRequested)
            {
                return receivedAny;
            }

            watchdog.CancelAfter(SilenceTimeout);

            // Blank line = end of one SSE event.
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    receivedAny = true;
                    Dispatch(data.ToString());
                    data.Clear();
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.Length > 5 && line[5] == ' ' ? line.Substring(6) : line.Substring(5));
            }

            // "event:", "id:", "retry:" and ":" comments carry nothing the
            // JSON doesn't already say.
        }
    }

    private static void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("time", out var time) && time.TryGetInt64(out var unix))
            {
                _lastEventTime = unix;
            }

            switch (GetString(root, "event"))
            {
                case "open":
                    _openTime = _lastEventTime;
                    Plugin.Logger.LogInfo(
                        $"Trophies: connected, listening on topic '{GetString(root, "topic")}'."
                    );
                    break;

                case "keepalive":
                    Plugin.Logger.LogDebug("Trophies: keepalive");
                    break;

                case "message":
                    var id = GetString(root, "id");
                    if (id.Length > 0)
                    {
                        lock (Gate)
                        {
                            if (!SeenMessageIds.Add(id))
                            {
                                return;
                            }

                            _lastMessageId = id;
                            Remember(id, _lastEventTime);
                        }
                    }

                    // Older than the connection: replayed from ntfy's cache,
                    // i.e. sent while we weren't listening.
                    if (_openTime > 0 && _lastEventTime < _openTime)
                    {
                        Plugin.Logger.LogInfo(
                            $"Trophies: missed event from {DateTimeOffset.FromUnixTimeSeconds(_lastEventTime).ToLocalTime():yyyy-MM-dd HH:mm:ss}, delivered now."
                        );
                    }

                    // ntfy turns a body that isn't valid UTF-8, or is over
                    // 4 KB, into a file attachment: the JSON is then gone
                    // from "message". That's a publisher-side problem.
                    if (root.TryGetProperty("attachment", out var attachment))
                    {
                        Plugin.Logger.LogWarning(
                            "Trophies: event received as an attachment instead of text "
                                + "(body not UTF-8 or over 4 KB on the site's side): "
                                + GetString(attachment, "url")
                        );
                        return;
                    }

                    LogSiteEvent(GetString(root, "message"));
                    break;

                // message_delete, message_clear, poll_request: irrelevant here.
            }
        }
        catch (JsonException e)
        {
            Plugin.Logger.LogWarning($"Trophies: unreadable SSE event ({e.Message}): {json}");
        }
    }

    /// Parses the site's own JSON, carried in ntfy's "message" field.
    private static void LogSiteEvent(string message)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            Plugin.Logger.LogInfo($"Trophies: non-JSON message: {message}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Plugin.Logger.LogInfo($"Trophies: unexpected message: {message}");
                return;
            }

            root.TryGetProperty("payload", out var payload);

            // Duel results name their recipient only inside the payload.
            var username = GetString(root, "username");
            if (username.Length == 0)
            {
                username = DuelRecipient(payload);
            }

            var user = $"{username} (#{GetString(root, "user_id")})";
            var showToast = IsForMe(username);
            if (showToast)
            {
                // The player card's numbers just changed, and maybe the
                // duels waiting to be played.
                MainMenuOverlay.RequestRefresh();
                DuelWindow.RequestRefresh();
            }

            switch (GetString(root, "event"))
            {
                case "trophy_unlocked":
                    Plugin.Logger.LogInfo(
                        $"[Trophy] {user} unlocked {GetString(payload, "icon")} "
                            + $"\"{GetString(payload, "name")}\" "
                            + $"(#{GetString(payload, "trophy_id")}): "
                            + GetString(payload, "description")
                    );
                    // No icon: the game's TMP font has no emoji glyphs,
                    // it would render as a missing-character box.
                    if (showToast && Plugin.ToastTrophies.Value)
                    {
                        Toast.Show(
                            GetString(payload, "name"),
                            GetString(payload, "description"),
                            ToastSeconds,
                            TrophyAccent,
                            "Trophy unlocked"
                        );
                    }
                    break;

                case "record_beaten":
                    Plugin.Logger.LogInfo(
                        $"[Record] {user}: record beaten on "
                            + $"{GetString(payload, "artist")} - {GetString(payload, "song")} "
                            + $"({GetString(payload, "instrument")} {GetString(payload, "difficulty")}) "
                            + $"{GetString(payload, "previous_score")} -> {GetString(payload, "new_score")}, "
                            + $"new holder: {GetString(payload, "new_holder_name")}"
                    );
                    if (showToast && Plugin.ToastRecords.Value)
                    {
                        Toast.Show(
                            GetString(payload, "song"),
                            $"{GetString(payload, "artist")} · "
                                + $"{GetString(payload, "instrument")} {GetString(payload, "difficulty")} · "
                                + $"{FormatScore(GetString(payload, "new_score"))} by "
                                + GetString(payload, "new_holder_name"),
                            ToastSeconds,
                            RecordAccent,
                            "Record beaten"
                        );
                    }
                    break;

                case "level_up":
                    Plugin.Logger.LogInfo(
                        $"[Level] {user}: level {GetString(payload, "old_level")} -> "
                            + $"{GetString(payload, "new_level")} ({GetString(payload, "points")} points)"
                    );
                    if (showToast && Plugin.ToastLevels.Value)
                    {
                        Toast.ShowRankUp(
                            $"Level {GetString(payload, "old_level")}",
                            $"Level {GetString(payload, "new_level")}",
                            $"{FormatScore(GetString(payload, "points"))} points",
                            ToastSeconds,
                            LevelAccent,
                            "Level up"
                        );
                    }
                    break;

                case "global_notification":
                    // Broadcast to every player: no username to filter on.
                    Plugin.Logger.LogInfo(
                        $"[Announcement] {GetString(payload, "subject")}: {GetString(payload, "text")}"
                    );
                    if (Plugin.ToastAnnouncements.Value)
                    {
                        Toast.Show(
                            GetString(payload, "subject"),
                            GetString(payload, "text"),
                            AnnouncementSeconds,
                            AnnouncementAccent,
                            "Announcement"
                        );
                    }
                    break;

                case "challenge_won":
                case "challenge_lost":
                {
                    var won = GetString(root, "event") == "challenge_won";
                    var opponent = GetString(payload, "opponent_name");
                    var (mine, theirs) = DuelScores(payload, opponent);
                    var song = GetString(payload, "song");
                    Plugin.Logger.LogInfo(
                        $"[Duel] {user}: {(won ? "won" : "lost")} against {opponent} on "
                            + $"{GetString(payload, "artist")} - {song} "
                            + $"({GetString(payload, "instrument")} {GetString(payload, "difficulty")}), "
                            + $"{mine} vs {theirs} (challenge #{GetString(payload, "challenge_id")})"
                    );
                    if (showToast && Plugin.ToastDuelResults.Value)
                    {
                        if (won)
                        {
                            Toast.ShowRankUp(
                                $"Duel vs {opponent}",
                                "Victory !",
                                DuelResultMessage(song, mine, theirs),
                                ToastSeconds + 1f,
                                DuelWonAccent,
                                "Duel won"
                            );
                        }
                        else
                        {
                            Toast.Show(
                                "Defeat",
                                $"{opponent} beat you · {DuelResultMessage(song, mine, theirs)}",
                                ToastSeconds + 1f,
                                DuelLostAccent,
                                "Duel lost"
                            );
                        }
                    }
                    break;
                }

                case "challenge_received":
                    Plugin.Logger.LogInfo(
                        $"[Duel] {user}: challenged by {GetString(payload, "challenger_name")} on "
                            + $"{GetString(payload, "artist")} - {GetString(payload, "song")} "
                            + $"(charter {GetString(payload, "charter")}, "
                            + $"{GetString(payload, "instrument")} {GetString(payload, "difficulty")}), "
                            + $"score to beat {GetString(payload, "score_to_beat")}"
                    );
                    if (showToast && Plugin.ToastChallenges.Value)
                    {
                        Toast.ShowClash(
                            GetString(payload, "song"),
                            ChallengeMessage(
                                GetString(payload, "challenger_name"),
                                GetString(payload, "instrument"),
                                GetString(payload, "difficulty"),
                                GetString(payload, "score_to_beat")
                            ),
                            ToastSeconds + 1f,
                            ChallengeAccent,
                            "Duel challenge"
                        );
                    }
                    break;

                default:
                    Plugin.Logger.LogInfo($"Trophies: unknown event: {message}");
                    break;
            }
        }
    }

    /// The duel participant who isn't the opponent: the one the event is
    /// for. "" if the payload doesn't say.
    private static string DuelRecipient(JsonElement payload)
    {
        var opponent = GetString(payload, "opponent_name");
        var challenger = GetString(payload, "challenger_name");
        var challenged = GetString(payload, "challenged_name");
        if (opponent.Length == 0)
        {
            return "";
        }

        return string.Equals(opponent, challenger, StringComparison.OrdinalIgnoreCase) ? challenged : challenger;
    }

    /// (recipient's score, opponent's score), formatted.
    private static (string Mine, string Theirs) DuelScores(JsonElement payload, string opponent)
    {
        var challenger = FormatScore(GetString(payload, "challenger_score"));
        var challenged = FormatScore(GetString(payload, "challenged_score"));
        return string.Equals(opponent, GetString(payload, "challenger_name"), StringComparison.OrdinalIgnoreCase)
            ? (challenged, challenger)
            : (challenger, challenged);
    }

    /// "Through the Fire and Flames · 274,521 vs 268,000"
    internal static string DuelResultMessage(string song, string mine, string theirs) =>
        $"{song} · {mine} vs {theirs}";

    /// "MrPlopy challenges you · Guitar Expert · beat 268,000"
    internal static string ChallengeMessage(
        string challenger,
        string instrument,
        string difficulty,
        string scoreToBeat
    ) =>
        $"{challenger} challenges you · {instrument} {difficulty} · beat {FormatScore(scoreToBeat)}";

    /// Saved state: {"key": "<sse url>", "time": <unix>, "ids": [...]}.
    /// Call under Gate.
    private static void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            var root = doc.RootElement;
            if (GetString(root, "key") != _stateKey
                || !root.TryGetProperty("time", out var time)
                || !time.TryGetInt64(out var unix)
                || unix <= 0)
            {
                return; // other channel: start live
            }

            _lastMessageTime = unix;
            _lastEventTime = unix; // ResumeUrl -> ?since=<unix>

            if (root.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in ids.EnumerateArray())
                {
                    var value = id.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        SeenMessageIds.Add(value);
                        RecentIds.Enqueue(value);
                    }
                }
            }

            Plugin.Logger.LogInfo(
                "Trophies: catching up on events since "
                    + $"{DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime():yyyy-MM-dd HH:mm:ss}."
            );
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Trophies: couldn't read saved state, starting live: {e.Message}");
        }
    }

    /// Records a delivered message and saves the state. Call under Gate.
    private static void Remember(string id, long time)
    {
        RecentIds.Enqueue(id);
        while (RecentIds.Count > SavedIdCount)
        {
            RecentIds.Dequeue();
        }

        _lastMessageTime = Math.Max(_lastMessageTime, time);

        try
        {
            File.WriteAllText(
                StatePath,
                JsonSerializer.Serialize(
                    new Dictionary<string, object>
                    {
                        ["key"] = _stateKey,
                        ["time"] = _lastMessageTime,
                        ["ids"] = RecentIds.ToArray(),
                    }
                )
            );
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Trophies: couldn't save state: {e.Message}");
        }
    }

    /// 856742 -> "856,742"; anything unparsable is shown as-is.
    internal static string FormatScore(string score) =>
        long.TryParse(score, out var value)
            ? value.ToString("N0", CultureInfo.InvariantCulture)
            : score;

    /// Names are compared case-insensitively: the site and the desktop
    /// app don't necessarily agree on casing.
    private static bool IsForMe(string username)
    {
        var mine = Plugin.TrophyUsername.Value.Trim();
        return mine.Length == 0
            || string.Equals(username.Trim(), mine, StringComparison.OrdinalIgnoreCase);
    }

    /// Any JSON value as text ("" if missing), so ids and scores that are
    /// numbers today and strings tomorrow don't break the log line.
    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null => "",
            _ => value.GetRawText(),
        };
    }
}
