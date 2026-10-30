using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChorusMod;

/// A player who can be challenged.
public class DuelPlayer
{
    public string Name = "";
}

/// One of the player's own scores: a song they can challenge someone on.
public class DuelScore
{
    public string Key = "";        // "CHECKSUM|guitar|100|", sent back on create
    public string Checksum = "";
    public string Title = "";
    public string Artist = "";
    public string Charter = "";
    public string Instrument = "";
    public string Difficulty = "";
    public long Score;
    public int Speed = 100;        // playback speed, %
    public string Modifiers = "";
}

/// A challenge, sent or received.
///
/// Two systems live side by side on the site:
///  - legacy: the challenger's existing score is the one to beat, only the
///    target plays (status pending, then completed);
///  - the current one: both players play the song after the challenge is
///    sent (waiting_both, then waiting_challenger / waiting_target as each
///    one plays, awaiting_results, then won / lost / draw).
/// display_status and my_result are already from the viewer's side.
public class Duel
{
    public int Id;
    public bool Sent;              // I'm the challenger
    public string Opponent = "";   // the target (sent) or the challenger (received)
    public string Checksum = "";
    public string Song = "";
    public string Artist = "";
    public string Charter = "";
    public string Instrument = "";
    public string Difficulty = "";
    public long ScoreToBeat;       // legacy: the challenger's score
    public long ChallengedScore;   // legacy: the target's, 0 until played
    public long ChallengerScore;   // 0 until played
    public long TargetScore;
    public bool ChallengerPlayed;
    public bool TargetPlayed;
    public int Speed = 100;
    public string Modifiers = "";
    public string Mode = "";       // "legacy" or the current system
    public string Status = "";     // lower case, see above
    public string DisplayStatus = "";
    public string MyResult = "";   // won / lost / draw, "" until decided
    public string Winner = "";
    public string CreatedAt = "";
    public string CompletedAt = "";

    public bool Legacy => Mode == "legacy" || (Mode.Length == 0 && Status == "pending");

    /// Decided: won, lost or draw.
    public bool Finished =>
        MyResult.Length > 0
        || Status is "completed" or "finished" or "won" or "lost" or "draw" or "done"
        || DisplayStatus is "won" or "lost" or "draw";

    /// Not decided, and not dead either (expired, cancelled...).
    public bool Open => !Finished && !Closed;

    public bool Closed => Status is "expired" or "cancelled" or "canceled" or "declined" or "refused";

    /// It's up to me to play the song.
    public bool MyTurn
    {
        get
        {
            if (!Open)
            {
                return false;
            }

            if (Legacy)
            {
                return !Sent; // only the target plays
            }

            return Status switch
            {
                "waiting_both" => true,
                "waiting_challenger" => Sent,
                "waiting_target" => !Sent,
                "awaiting_results" => false,
                // Unknown open status: whoever hasn't played yet.
                _ => Sent ? !ChallengerPlayed : !TargetPlayed,
            };
        }
    }

    /// Both have played, the site is settling it.
    public bool AwaitingResults => Open && Status == "awaiting_results";

    /// won / lost / draw from my side, "" if not decided.
    public string Result
    {
        get
        {
            if (!Finished)
            {
                return "";
            }

            foreach (var value in new[] { MyResult, DisplayStatus, Status })
            {
                if (value is "won" or "lost" or "draw")
                {
                    return value;
                }
            }

            // Legacy completed: by the winner's name.
            return Winner.Length == 0 ? "draw"
                : string.Equals(Winner, Opponent, StringComparison.OrdinalIgnoreCase) ? "lost" : "won";
        }
    }

    /// My score and the opponent's, 0 when not played (or not known).
    public long MyScore => Legacy ? (Sent ? ScoreToBeat : ChallengedScore) : (Sent ? ChallengerScore : TargetScore);

    public long TheirScore => Legacy ? (Sent ? ChallengedScore : ScoreToBeat) : (Sent ? TargetScore : ChallengerScore);

    public bool TheyPlayed => Legacy ? (Sent ? ChallengedScore > 0 || Finished : true) : (Sent ? TargetPlayed : ChallengerPlayed);
}

/// players: who can be challenged, plus today's quota.
public class DuelRoster
{
    public List<DuelPlayer> Players = new();
    public int Remaining = -1;     // challenges left in the rolling 24 h, -1 = unknown
    public int Limit = -1;
}

/// <summary>
/// Client for the trophy site's duel API ([Trophies] ChallengeApiUrl):
///   GET  ?action=players&amp;discordName=  -> {"remaining","limit_24h","players":[{"name"}]}
///   GET  ?action=scores&amp;discordName=   -> {"remaining","scores":[{"key","title",...}]}
///   GET  ?action=sent&amp;discordName=     -> {"challenges":[{"target":{"name"},...}]}
///   GET  ?action=received&amp;discordName= -> {"challenges":[{"challenger":{"name"},...}]}
///   (each with mode, status, display_status, my_result, challenger_score,
///   target_score, challenger_has_played, target_has_played: see Duel)
///   POST ?action=create  {"discordName","targetDiscordName","challenge_key"}
/// Failures come back as {"success":false,"error":"CODE"}.
///
/// Every method throws DuelApiException with a readable message on failure.
/// </summary>
public static class ChallengeApi
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "ChorusMod/1.0");
        return client;
    }

    public static async Task<DuelRoster> GetPlayersAsync(string me)
    {
        var root = await GetAsync("players", me).ConfigureAwait(false);
        var roster = new DuelRoster
        {
            Remaining = GetInt(root, "remaining", -1),
            Limit = GetInt(root, "limit_24h", -1),
        };

        foreach (var player in Array(root, "players"))
        {
            var name = GetString(player, "name");
            if (name.Length > 0 && !string.Equals(name, me, StringComparison.OrdinalIgnoreCase))
            {
                roster.Players.Add(new DuelPlayer { Name = name });
            }
        }

        roster.Players.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return roster;
    }

    public static async Task<List<DuelScore>> GetScoresAsync(string me)
    {
        var root = await GetAsync("scores", me).ConfigureAwait(false);
        var scores = new List<DuelScore>();
        foreach (var s in Array(root, "scores"))
        {
            var key = GetString(s, "key");
            if (key.Length == 0)
            {
                continue;
            }

            scores.Add(new DuelScore
            {
                Key = key,
                Checksum = GetString(s, "checksum"),
                Title = Fallback(GetString(s, "title"), "(untitled)"),
                Artist = GetString(s, "artist"),
                Charter = GetString(s, "charter"),
                Instrument = GetString(s, "instrument"),
                Difficulty = GetString(s, "difficulty"),
                Score = GetLong(s, "score"),
                Speed = GetInt(s, "playback_speed_percent", GetInt(s, "playback_speed", 100)),
                Modifiers = GetString(s, "modifiers"),
            });
        }

        return scores;
    }

    /// sent = challenges I sent, otherwise the ones I received. Newest first.
    public static async Task<List<Duel>> GetChallengesAsync(string me, bool sent)
    {
        var root = await GetAsync(sent ? "sent" : "received", me).ConfigureAwait(false);
        var duels = new List<Duel>();
        foreach (var c in Array(root, "challenges"))
        {
            var opponent = c.TryGetProperty(sent ? "target" : "challenger", out var who)
                && who.ValueKind == JsonValueKind.Object
                ? GetString(who, "name")
                : "";

            var challengerScore = GetLong(c, "challenger_score");
            var targetScore = GetLong(c, "target_score");
            duels.Add(new Duel
            {
                Id = GetInt(c, "id", 0),
                Sent = sent,
                Opponent = opponent,
                Checksum = GetString(c, "checksum"),
                Song = Fallback(GetString(c, "song"), "(untitled)"),
                Artist = GetString(c, "artist"),
                Charter = GetString(c, "charter"),
                Instrument = GetString(c, "instrument"),
                Difficulty = GetString(c, "difficulty"),
                ScoreToBeat = GetLong(c, "score_to_beat"),
                ChallengedScore = GetLong(c, "challenged_score"),
                ChallengerScore = challengerScore,
                TargetScore = targetScore,
                ChallengerPlayed = GetBool(c, "challenger_has_played") || challengerScore > 0,
                TargetPlayed = GetBool(c, "target_has_played") || targetScore > 0,
                Speed = GetInt(c, "playback_speed_percent", GetInt(c, "playback_speed", 100)),
                Modifiers = GetString(c, "modifiers"),
                Mode = GetString(c, "mode").ToLowerInvariant(),
                Status = GetString(c, "status").ToLowerInvariant(),
                DisplayStatus = GetString(c, "display_status").ToLowerInvariant(),
                MyResult = GetString(c, "my_result").ToLowerInvariant(),
                Winner = GetString(c, "winner_name"),
                CreatedAt = GetString(c, "created_at"),
                CompletedAt = GetString(c, "completed_at"),
            });
        }

        duels.Sort((a, b) => string.CompareOrdinal(b.CreatedAt, a.CreatedAt));
        return duels;
    }

    /// Returns the challenges left today when the answer says, else -1.
    public static async Task<int> CreateAsync(string me, string target, string challengeKey)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["discordName"] = me,
            ["targetDiscordName"] = target,
            ["challenge_key"] = challengeKey,
        });

        var url = $"{BaseUrl()}?action=create";
        string json;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(url, content).ConfigureAwait(false);
            json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Duels: request to {url} failed: {e.Message}");
            throw new DuelApiException("The trophy site can't be reached.");
        }

        using var doc = Parse(json, url);
        var root = doc.RootElement;
        CheckSuccess(root);
        return GetInt(root, "remaining", -1);
    }

    private static async Task<JsonElement> GetAsync(string action, string me)
    {
        var url = $"{BaseUrl()}?action={action}&discordName={Uri.EscapeDataString(me)}";
        string json;
        try
        {
            json = await Http.GetStringAsync(url).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Duels: request to {url} failed: {e.Message}");
            throw new DuelApiException("The trophy site can't be reached.");
        }

        using var doc = Parse(json, url);
        var root = doc.RootElement.Clone();
        CheckSuccess(root);
        return root;
    }

    private static string BaseUrl() => Plugin.ChallengeApiUrl.Value.Trim();

    private static JsonDocument Parse(string json, string url)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            Plugin.Logger.LogWarning($"Duels: {url} didn't answer JSON: {Truncate(json)}");
            throw new DuelApiException("Unexpected answer from the trophy site.");
        }
    }

    private static void CheckSuccess(JsonElement root)
    {
        if (root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.True)
        {
            return;
        }

        var code = GetString(root, "error");
        Plugin.Logger.LogInfo($"Duels: the site answered {(code.Length > 0 ? code : "an error")}.");
        throw new DuelApiException(Describe(code), code);
    }

    /// The site's error codes, in words.
    private static string Describe(string code) => code switch
    {
        "PLAYER_NOT_FOUND" => "Your player name isn't known to the trophy site.",
        "DISCORD_NAME_REQUIRED" => "Set your player name in SETTINGS first.",
        "TARGET_NOT_FOUND" => "That player can't be found.",
        "INVALID_CHALLENGE_SCORE" => "That score can't be used for a challenge.",
        "SELF_CHALLENGE_NOT_ALLOWED" => "You can't challenge yourself.",
        "" => "The trophy site refused the request.",
        _ when code.Contains("LIMIT") => "No challenges left for today.",
        _ when code.Contains("EXIST") || code.Contains("PENDING") || code.Contains("DUPLICATE")
            => "A challenge on that song is already waiting for this player.",
        _ => $"The trophy site refused: {code.Replace('_', ' ').ToLowerInvariant()}.",
    };

    private static IEnumerable<JsonElement> Array(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }
        }
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n != 0));

    private static int GetInt(JsonElement element, string name, int fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : fallback;
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)
            ? number
            : 0;
    }

    private static string Fallback(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Truncate(string text) => text.Length <= 200 ? text : text.Substring(0, 200) + "…";
}

public class DuelApiException : Exception
{
    public string Code { get; }

    public DuelApiException(string message, string code = "") : base(message)
    {
        Code = code;
    }
}
