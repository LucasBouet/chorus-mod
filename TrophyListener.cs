using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    private static readonly int[] RetryDelaysSeconds = { 1, 2, 5, 10, 30 };

    private static readonly HttpClient Http = CreateClient();

    private static readonly HashSet<string> SeenMessageIds = new();

    private static string? _lastMessageId;
    private static long _lastEventTime;
    private static bool _started;

    private static HttpClient CreateClient()
    {
        // Dedicated client: EnchorClient's 30 s timeout would kill the stream.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("Accept", "text/event-stream");
        client.DefaultRequestHeaders.Add("User-Agent", "ChorusMod/1.0");
        return client;
    }

    /// Called once from Plugin.Load(). Does nothing if no topic is set.
    public static void Start()
    {
        if (_started)
        {
            return;
        }

        var topic = Plugin.NtfyTopic.Value.Trim();
        if (string.IsNullOrEmpty(topic))
        {
            Plugin.Logger.LogInfo(
                "Trophies: no NtfyTopic set in [Trophies], listener disabled."
            );
            return;
        }

        _started = true;
        var url = $"{Plugin.NtfyServer.Value.Trim().TrimEnd('/')}/{topic}/sse";

        // Thread-pool threads are background threads: this never keeps the
        // game process alive on exit.
        Task.Run(() => RunAsync(url));
    }

    private static async Task RunAsync(string baseUrl)
    {
        var attempt = 0;

        while (true)
        {
            try
            {
                var receivedAny = await ListenOnceAsync(ResumeUrl(baseUrl))
                    .ConfigureAwait(false);
                if (receivedAny)
                {
                    attempt = 0;
                }

                Plugin.Logger.LogWarning("Trophies: stream closed, reconnecting.");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Trophies: stream error: {e.Message}");
            }

            var delay = RetryDelaysSeconds[Math.Min(attempt, RetryDelaysSeconds.Length - 1)];
            attempt++;
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
        }
    }

    /// First connection: live events only, no replay of the 12 h cache.
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
    private static async Task<bool> ListenOnceAsync(string url)
    {
        Plugin.Logger.LogInfo($"Trophies: connecting to {url}");

        using var watchdog = new CancellationTokenSource();
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
            catch (Exception) when (watchdog.IsCancellationRequested)
            {
                Plugin.Logger.LogWarning(
                    $"Trophies: nothing received for {SilenceTimeout.TotalSeconds:0} s, "
                        + "connection considered dead."
                );
                return receivedAny;
            }

            if (line == null)
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
                        if (!SeenMessageIds.Add(id))
                        {
                            return;
                        }

                        _lastMessageId = id;
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

            var user = $"{GetString(root, "username")} (#{GetString(root, "user_id")})";
            root.TryGetProperty("payload", out var payload);
            var showToast = IsForMe(GetString(root, "username"));
            if (showToast)
            {
                // The player card's numbers just changed.
                MainMenuOverlay.RequestRefresh();
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

    /// "MrPlopy challenges you · Guitar Expert · beat 268,000"
    internal static string ChallengeMessage(
        string challenger,
        string instrument,
        string difficulty,
        string scoreToBeat
    ) =>
        $"{challenger} challenges you · {instrument} {difficulty} · beat {FormatScore(scoreToBeat)}";

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
