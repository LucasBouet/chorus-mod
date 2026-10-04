using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChorusMod;

public enum PlayerLookupStatus
{
    Found,
    NotFound,
    Error,
}

/// A player's public profile on the trophy site.
public class PlayerInfo
{
    public string Name = "";
    public string AvatarUrl = "";
    public int Level;              // 0 = not provided
    public float LevelProgress;    // % toward the next level, 0-100
    public int TrophyCount;
    public int FullCombos;
    public string BestSong = "";
    public long BestScore;
    public string LatestTrophy = "";
}

public class PlayerLookup
{
    public PlayerLookupStatus Status;
    public PlayerInfo? Player;
}

/// <summary>
/// Client for the trophy site's player endpoint:
///   GET usr.php?discordName=&lt;name&gt;
///   -> {"success":true, "player":{"name", "avatar", "level",
///       "level_progress" (0-100), "trophy_count", "fc",
///       "best_score":{"song","score"}, "trophies":[{"name","icon",
///       "unlocked_at"}, ...]}}
///   -> {"success":false, "error":"PLAYER_NOT_FOUND"} for an unknown name.
/// Trophies come newest first.
/// </summary>
public static class PlayerApi
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "ChorusMod/1.0");
        return client;
    }

    /// Never throws: network and parsing problems come back as Error.
    public static async Task<PlayerLookup> FetchAsync(string username)
    {
        var url = $"{Plugin.PlayerApiUrl.Value.Trim()}?discordName={Uri.EscapeDataString(username)}";

        try
        {
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                Plugin.Logger.LogInfo($"Player card: lookup of '{username}' failed: {error}");
                return new PlayerLookup
                {
                    Status = error == "PLAYER_NOT_FOUND"
                        ? PlayerLookupStatus.NotFound
                        : PlayerLookupStatus.Error,
                };
            }

            var player = root.GetProperty("player");
            var info = new PlayerInfo
            {
                Name = GetString(player, "name"),
                AvatarUrl = GetString(player, "avatar"),
                TrophyCount = GetInt(player, "trophy_count"),
                FullCombos = GetInt(player, "fc"),
                Level = GetInt(player, "level"),
                LevelProgress = player.TryGetProperty("level_progress", out var progress)
                    && progress.TryGetDouble(out var percent)
                    ? Math.Clamp((float)percent, 0f, 100f)
                    : 0f,
            };

            if (player.TryGetProperty("best_score", out var best)
                && best.ValueKind == JsonValueKind.Object)
            {
                info.BestSong = GetString(best, "song");
                info.BestScore = best.TryGetProperty("score", out var score)
                    && score.TryGetInt64(out var value)
                    ? value
                    : 0;
            }

            if (player.TryGetProperty("trophies", out var trophies)
                && trophies.ValueKind == JsonValueKind.Array
                && trophies.GetArrayLength() > 0)
            {
                info.LatestTrophy = GetString(trophies[0], "name");
            }

            return new PlayerLookup { Status = PlayerLookupStatus.Found, Player = info };
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Player card: request to {url} failed: {e.Message}");
            return new PlayerLookup { Status = PlayerLookupStatus.Error };
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;
}
