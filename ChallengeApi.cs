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
public class Duel
{
    public int Id;
    public string Opponent = "";   // the target (sent) or the challenger (received)
    public string Checksum = "";
    public string Song = "";
    public string Artist = "";
    public string Charter = "";
    public string Instrument = "";
    public string Difficulty = "";
    public long ScoreToBeat;       // the challenger's score
    public long ChallengedScore;   // 0 until played
    public int Speed = 100;
    public string Modifiers = "";
    public string Status = "";     // "completed", or still open
    public string Winner = "";
    public string CreatedAt = "";
    public string CompletedAt = "";

    public bool Completed => Status == "completed";
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

            duels.Add(new Duel
            {
                Id = GetInt(c, "id", 0),
                Opponent = opponent,
                Checksum = GetString(c, "checksum"),
                Song = Fallback(GetString(c, "song"), "(untitled)"),
                Artist = GetString(c, "artist"),
                Charter = GetString(c, "charter"),
                Instrument = GetString(c, "instrument"),
                Difficulty = GetString(c, "difficulty"),
                ScoreToBeat = GetLong(c, "score_to_beat"),
                ChallengedScore = GetLong(c, "challenged_score"),
                Speed = GetInt(c, "playback_speed_percent", GetInt(c, "playback_speed", 100)),
                Modifiers = GetString(c, "modifiers"),
                Status = GetString(c, "status").ToLowerInvariant(),
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
