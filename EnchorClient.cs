using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChorusMod;

/// A chart as returned by the API and displayed in the overlay.
public class SongResult
{
    public string Name = "(untitled)";
    public string Artist = "Unknown Artist";
    public string Album = "";
    public string Genre = "";
    public string Year = "";
    public string Charter = "";
    public int LengthMs;
    public int GuitarTier = -1;
    public string[] Instruments = Array.Empty<string>();
    public string AlbumArtMd5 = "";
    public string DownloadUrl = "";
    public bool RequiresBrowser;

    public string LengthText =>
        LengthMs <= 0
            ? "—"
            : $"{LengthMs / 60000}:{(LengthMs % 60000) / 1000:00}";

    public string AlbumArtUrl =>
        string.IsNullOrEmpty(AlbumArtMd5)
            ? ""
            : $"{Plugin.FilesBaseUrl.Value.TrimEnd('/')}/{AlbumArtMd5}.jpg";
}

/// A page of results. The advanced endpoint also returns the total,
/// which the simple endpoint doesn't provide (Found then stays at -1).
public class SearchPage
{
    public List<SongResult> Songs = new();
    public int Found = -1;
}

/// <summary>
/// Client for the enchor.us API.
///
/// Request confirmed by network capture of the site:
///   POST https://api.enchor.us/search   (Content-Type: application/json)
///   {"search":"…","page":1,"instrument":"guitar","difficulty":null,
///    "drumType":null,"drumsReviewed":false,"source":"website"}
///
/// Response schema confirmed by dumping the first result: name, artist,
/// album, genre, year, charter, md5, albumArtMd5, song_length (ms),
/// diff_* (tier, -1 if absent), notesData.instruments[].
/// The chart file downloads from files.enchor.us/{md5}.sng.
/// </summary>
public static class EnchorClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Referer", "https://www.enchor.us/");
        client.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ChorusMod/0.4"
        );
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <param name="difficulty">null / easy / medium / hard / expert.</param>
    /// <param name="field">
    /// Empty = free-text search via /search. Otherwise (name, artist, album,
    /// genre, charter) switches to /search/advanced, which targets that one
    /// field and returns the total result count as a bonus.
    /// </param>
    /// <param name="flags">
    /// Tri-state flags for /search/advanced: true = require,
    /// false = exclude, absent/null = don't care. Verified against the API:
    /// hasSoloSections true (308) + false (92) = total with no filter (400).
    /// </param>
    public static async Task<SearchPage> SearchAsync(
        string query,
        string instrument,
        string difficulty,
        string field,
        Dictionary<string, bool?> flags,
        int page
    )
    {
        var hasFlag = false;
        foreach (var flag in flags)
        {
            if (flag.Value != null)
            {
                hasFlag = true;
                break;
            }
        }

        var advanced = !string.IsNullOrEmpty(field) || hasFlag;
        var baseUrl = Plugin.ApiBaseUrl.Value.TrimEnd('/');
        var url = advanced
            ? baseUrl + "/search/advanced"
            : baseUrl + Plugin.SearchEndpoint.Value;

        var body = advanced
            ? AdvancedBody(query, instrument, difficulty, field, flags, page)
            : SimpleBody(query, instrument, difficulty, page);

        Plugin.Logger.LogInfo($"POST {url} -> {body}");

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await Http.PostAsync(url, content).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var preview = json.Length > 300 ? json.Substring(0, 300) : json;
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {preview}");
        }

        return ParsePage(json);
    }

    /// "null" or empty -> literal JSON null, otherwise an escaped string.
    private static string Opt(string value) =>
        string.IsNullOrWhiteSpace(value) || value == "null"
            ? "null"
            : JsonSerializer.Serialize(value);

    private static string SimpleBody(
        string query,
        string instrument,
        string difficulty,
        int page
    ) =>
        "{"
        + $"\"search\":{JsonSerializer.Serialize(query)},"
        + $"\"page\":{Math.Max(1, page)},"
        + $"\"instrument\":{Opt(instrument)},"
        + $"\"difficulty\":{Opt(difficulty)},"
        + "\"drumType\":null,"
        + "\"drumsReviewed\":false,"
        + "\"source\":\"website\""
        + "}";

    /// /search/advanced expects ALL fields, even empty ones. Each text
    /// field is a {value, exact, exclude} object; only the one the user
    /// selected gets filled in.
    private static string AdvancedBody(
        string query,
        string instrument,
        string difficulty,
        string field,
        Dictionary<string, bool?> flags,
        int page
    )
    {
        string Flag(string name) =>
            $"\"{name}\":"
            + (flags.TryGetValue(name, out var v) && v != null
                ? (v.Value ? "true" : "false")
                : "null");

        string Text(string name)
        {
            var value = name == field ? query : "";
            return $"\"{name}\":{{\"value\":{JsonSerializer.Serialize(value)},"
                + "\"exact\":false,\"exclude\":false}";
        }

        return "{"
            + $"\"instrument\":{Opt(instrument)},"
            + $"\"difficulty\":{Opt(difficulty)},"
            + "\"drumType\":null,\"drumsReviewed\":false,\"sort\":null,"
            + "\"source\":\"website\","
            + Text("name") + ","
            + Text("artist") + ","
            + Text("album") + ","
            + Text("genre") + ","
            + Text("year") + ","
            + Text("charter") + ","
            + "\"minLength\":null,\"maxLength\":null,"
            + "\"minIntensity\":null,\"maxIntensity\":null,"
            + "\"minAverageNPS\":null,\"maxAverageNPS\":null,"
            + "\"minMaxNPS\":null,\"maxMaxNPS\":null,"
            + "\"minYear\":null,\"maxYear\":null,"
            + "\"modifiedAfter\":\"\",\"hash\":\"\",\"trackHash\":\"\","
            + Flag("hasSoloSections") + "," + Flag("hasForcedNotes") + ","
            + Flag("hasOpenNotes") + "," + Flag("hasTapNotes") + ","
            + Flag("hasLyrics") + "," + Flag("hasVocals") + ","
            + Flag("hasRollLanes") + "," + Flag("has2xKick") + ","
            + Flag("hasIssues") + "," + Flag("hasVideoBackground") + ","
            + Flag("modchart") + ","
            + $"\"page\":{Math.Max(1, page)}"
            + "}";
    }

    private static SearchPage ParsePage(string json)
    {
        var page = new SearchPage();
        var results = page.Songs;

        using var doc = JsonDocument.Parse(json);

        // "found" = server-side total, present on /search/advanced only.
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            page.Found = Int(doc.RootElement, "found", -1);
        }

        var array = FindSongArray(doc.RootElement);

        if (array == null)
        {
            Plugin.Logger.LogWarning(
                "No results array in the response. Excerpt: "
                    + (json.Length > 400 ? json.Substring(0, 400) : json)
            );
            return page;
        }

        var logged = false;
        foreach (var element in array.Value.EnumerateArray())
        {
            if (!logged && Plugin.LogRawResponse.Value)
            {
                logged = true;
                var raw = element.GetRawText();
                Plugin.Logger.LogInfo(
                    "First raw result: "
                        + (raw.Length > 1500 ? raw.Substring(0, 1500) + "…" : raw)
                );
            }

            var song = ExtractSong(element);
            if (song != null)
            {
                results.Add(song);
            }
        }

        return page;
    }

    private static JsonElement? FindSongArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in prop.Value.EnumerateArray())
            {
                return item.ValueKind == JsonValueKind.Object ? prop.Value : null;
            }
        }

        return null;
    }

    private static SongResult? ExtractSong(JsonElement e)
    {
        var song = new SongResult
        {
            Name = Str(e, "name", "title") ?? "(untitled)",
            Artist = Str(e, "artist") ?? "Unknown Artist",
            Album = Str(e, "album") ?? "",
            Genre = Str(e, "genre") ?? "",
            Year = Str(e, "year") ?? "",
            Charter = Str(e, "charter") ?? "",
            LengthMs = Int(e, "song_length"),
            GuitarTier = Int(e, "diff_guitar", -1),
            AlbumArtMd5 = Str(e, "albumArtMd5") ?? "",
            Instruments = ReadInstruments(e),
        };

        var direct = Str(e, "downloadUrl", "directLink", "link");
        if (string.IsNullOrEmpty(direct))
        {
            var hash = Str(e, "md5", "hash");
            if (!string.IsNullOrEmpty(hash))
            {
                direct = $"{Plugin.FilesBaseUrl.Value.TrimEnd('/')}/{hash}.sng";
            }
        }

        if (string.IsNullOrEmpty(direct))
        {
            return null;
        }

        song.DownloadUrl = direct!;
        song.RequiresBrowser = direct!.Contains("drive.google.com");
        return song;
    }

    /// notesData.instruments: the list of parts actually charted.
    private static string[] ReadInstruments(JsonElement e)
    {
        var notes = Child(e, "notesData");
        if (notes == null)
        {
            return Array.Empty<string>();
        }

        var list = Child(notes.Value, "instruments");
        if (list == null || list.Value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in list.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var v = item.GetString();
                if (!string.IsNullOrEmpty(v))
                {
                    values.Add(v!);
                }
            }
        }

        return values.ToArray();
    }

    private static JsonElement? Child(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value;
            }
        }

        return null;
    }

    /// CASE-INSENSITIVE lookup: the API's exact naming convention (name vs
    /// Name) isn't known, and TryGetProperty is strict.
    private static string? Str(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var value = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    // Some fields (year…) can arrive as a number.
                    return prop.Value.ToString();
                }
            }
        }

        return null;
    }

    private static int Int(JsonElement obj, string name, int fallback = 0)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (
                string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number
                && prop.Value.TryGetInt32(out var i)
            )
            {
                return i;
            }
        }

        return fallback;
    }

    public static async Task<byte[]> DownloadAsync(string url)
    {
        var response = await Http.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }
}
