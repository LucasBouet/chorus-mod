using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChorusMod;

/// <summary>
/// The trophy site's per-player localization endpoint
/// ([Trophies] LocalizationApiUrl), keyed by the player's uuid:
///   ?action=context&amp;uuid=          -> {"language","supported_languages",...}
///   ?action=translations&amp;uuid=&amp;scope=overlay
///                                   -> {"translations":{"overlay.level":"…",…}}
///   ?action=trophies&amp;uuid=[&amp;lang=]   -> {"trophies":[{"id","name","description"}]}
///   ?action=mediators&amp;uuid=         -> {"wallet":{"currency","balance"}}
/// Every call takes &amp;lang= to force a language; without it the site
/// uses the player's own preference ([Trophies] Language = auto).
///
/// Texts the site hasn't translated yet come back as their own key: those
/// count as missing, and the mod's built-in English text is used instead.
///
/// Refreshed with the main-menu player card (off the main thread); the
/// lookups are plain dictionary reads, safe from any thread.
/// </summary>
public static class SiteLocalization
{
    public static readonly string[] Languages = { "auto", "fr", "en" };

    // Translations and trophy names change rarely: reloaded this often at most.
    private static readonly TimeSpan TextsLifetime = TimeSpan.FromMinutes(15);

    private static readonly HttpClient Http = CreateClient();

    // Replaced whole, never modified: readers on other threads always see
    // a complete dictionary.
    private static volatile Dictionary<string, string> _texts = new();
    private static volatile Dictionary<string, (string Name, string Description)> _trophies = new();

    private static string _loadedFor = "";
    private static DateTime _loadedAt = DateTime.MinValue;

    /// The language the site answers in ("fr", "en"...), "" until known.
    public static string Language { get; private set; } = "";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "ChorusMod/1.0");
        return client;
    }

    /// The site's text for `key`, or `fallback` while it has none.
    public static string T(string key, string fallback) =>
        _texts.TryGetValue(key, out var text) && text.Length > 0 && text != key ? text : fallback;

    /// A trophy's name and description in the player's language.
    public static bool TryTrophy(string id, out string name, out string description)
    {
        if (id.Length > 0 && _trophies.TryGetValue(id, out var trophy) && trophy.Name.Length > 0)
        {
            (name, description) = trophy;
            return true;
        }

        name = "";
        description = "";
        return false;
    }

    /// Forgets the loaded texts: the next refresh loads them again (the
    /// language setting changed).
    public static void Invalidate() => _loadedAt = DateTime.MinValue;

    /// Reloads what's stale and returns the player's Médiators balance, -1
    /// if unknown. Never throws. Call off the main thread.
    public static async Task<int> RefreshAsync(string uuid)
    {
        if (uuid.Length == 0 || BaseUrl().Length == 0)
        {
            return -1;
        }

        var lang = ForcedLanguage();
        var key = $"{uuid}|{lang}";
        if (key != _loadedFor || DateTime.UtcNow - _loadedAt > TextsLifetime)
        {
            await LoadTextsAsync(uuid).ConfigureAwait(false);
            _loadedFor = key;
            _loadedAt = DateTime.UtcNow;
        }

        var wallet = await GetAsync("mediators", uuid).ConfigureAwait(false);
        if (wallet != null
            && wallet.Value.TryGetProperty("wallet", out var w)
            && w.ValueKind == JsonValueKind.Object
            && w.TryGetProperty("balance", out var balance)
            && balance.TryGetInt32(out var mediators))
        {
            return mediators;
        }

        return -1;
    }

    private static async Task LoadTextsAsync(string uuid)
    {
        var context = await GetAsync("context", uuid).ConfigureAwait(false);
        if (context != null)
        {
            Language = Str(context.Value, "language");
        }

        var translations = await GetAsync("translations", uuid, "&scope=overlay").ConfigureAwait(false);
        if (translations != null
            && translations.Value.TryGetProperty("translations", out var map)
            && map.ValueKind == JsonValueKind.Object)
        {
            var texts = new Dictionary<string, string>();
            foreach (var entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String)
                {
                    texts[entry.Name] = entry.Value.GetString() ?? "";
                }
            }

            _texts = texts;
        }

        var trophies = await GetAsync("trophies", uuid).ConfigureAwait(false);
        if (trophies != null
            && trophies.Value.TryGetProperty("trophies", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            var byId = new Dictionary<string, (string, string)>();
            foreach (var trophy in list.EnumerateArray())
            {
                var id = Str(trophy, "id");
                if (id.Length > 0)
                {
                    byId[id] = (Str(trophy, "name"), Str(trophy, "description"));
                }
            }

            _trophies = byId;
        }

        Plugin.Logger.LogInfo(
            $"Localization: language {(Language.Length > 0 ? Language : "unknown")}, "
                + $"{_texts.Count} text(s), {_trophies.Count} trophy name(s)."
        );
    }

    /// The answer's root, or null on any failure (logged).
    private static async Task<JsonElement?> GetAsync(string action, string uuid, string extra = "")
    {
        var lang = ForcedLanguage();
        var url = $"{BaseUrl()}?action={action}&uuid={Uri.EscapeDataString(uuid)}{extra}"
            + (lang.Length > 0 ? $"&lang={Uri.EscapeDataString(lang)}" : "");
        try
        {
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
            {
                return root.Clone();
            }

            Plugin.Logger.LogInfo($"Localization: {action} refused: {Str(root, "error")}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Localization: request to {url} failed: {e.Message}");
        }

        return null;
    }

    private static string BaseUrl() => Plugin.LocalizationApiUrl.Value.Trim();

    /// "" = let the site use the player's preference.
    private static string ForcedLanguage()
    {
        var lang = Plugin.Language.Value.Trim().ToLowerInvariant();
        return lang is "" or "auto" ? "" : lang;
    }

    private static string Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
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
}
