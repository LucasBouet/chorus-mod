using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Updates from the GitHub releases of the mod's repository: each release
/// is tagged with its version (V1.8) and carries ChorusMod.dll.
///
/// A check runs shortly after launch, then every few hours, against
/// api.github.com/repos/{Repo}/releases/latest (public, no account; 60
/// calls an hour per IP, far from what this uses). A newer version shows
/// a toast and the SETTINGS button turns into UPDATE; SETTINGS > Updates
/// checks on demand and installs.
///
/// Installing can't overwrite the DLL the game has loaded, but Windows lets
/// it be renamed: the running one becomes ChorusMod.dll.old, the new one
/// takes its name, and BepInEx loads it at the next launch (it only loads
/// *.dll). The .old file is deleted at the next start. The config is never
/// touched, so the notification channel and every setting stay.
///
/// State is read by the UI on the main thread and written by the background
/// tasks: plain volatile fields, each written as a whole.
/// </summary>
public static class Updater
{
    public const string Repo = "LucasBouet/chorus-mod";
    private const string AssetName = "ChorusMod.dll";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public enum Status
    {
        Unknown,      // not checked yet (or checks turned off)
        Checking,
        UpToDate,
        Available,
        Installing,
        Installed,    // waiting for a restart
        Failed,
    }

    private static readonly HttpClient Http = CreateClient();

    private static volatile Status _status = Status.Unknown;
    private static volatile string _latestTag = "";
    private static volatile string _releaseUrl = "";
    private static volatile string _dllUrl = "";
    private static volatile string _error = "";
    private static volatile string _publishedAt = "";
    private static volatile string _announced = ""; // tag already toasted this session
    private static int _busy;

    public static Status Current => _status;

    public static string LatestTag => _latestTag;

    /// The release's page on GitHub (changelog), "" until known.
    public static string ReleaseUrl => _releaseUrl;

    public static string Error => _error;

    public static bool UpdateAvailable => _status == Status.Available;

    public static string InstalledVersion => Plugin.Version;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.Add("User-Agent", "ChorusMod/" + Plugin.Version);
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    /// Called once from Plugin.Load().
    public static void Start()
    {
        DeleteOldCopy();
        Task.Run(async () =>
        {
            await Task.Delay(FirstCheckDelay).ConfigureAwait(false);
            while (true)
            {
                if (Plugin.CheckForUpdates.Value && _status != Status.Installed)
                {
                    await CheckAsync(manual: false).ConfigureAwait(false);
                }

                await Task.Delay(CheckInterval).ConfigureAwait(false);
            }
        });
    }

    /// SETTINGS > Updates > Check now.
    public static void CheckNow() => Task.Run(() => CheckAsync(manual: true));

    /// SETTINGS > Updates > Install.
    public static void Install() => Task.Run(InstallAsync);

    /// Human-readable state for the settings window.
    public static string Describe() => _status switch
    {
        Status.Checking => "Checking GitHub for a new version…",
        Status.UpToDate => $"Up to date: {Plugin.Version} is the latest version.",
        Status.Available => $"{_latestTag}{Published()} is out (you have {Plugin.Version}).",
        Status.Installing => $"Downloading and installing {_latestTag}…",
        Status.Installed => $"{_latestTag} installed: restart Clone Hero to use it.",
        Status.Failed => $"Failed: {_error}",
        _ => Plugin.CheckForUpdates.Value
            ? $"Version {Plugin.Version}: not checked yet."
            : $"Version {Plugin.Version}: automatic checks are off.",
    };

    private static string Published() =>
        DateTime.TryParse(_publishedAt, out var date) ? $" (released {date.ToLocalTime():MMM d})" : "";

    private static async Task CheckAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        var before = _status;
        try
        {
            if (before != Status.Installed)
            {
                _status = Status.Checking;
            }

            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest")
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = Str(root, "tag_name");
            var dll = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                dll = assets.EnumerateArray()
                    .Where(a => string.Equals(Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase))
                    .Select(a => Str(a, "browser_download_url"))
                    .FirstOrDefault() ?? "";
            }

            _latestTag = tag;
            _releaseUrl = Str(root, "html_url");
            _publishedAt = Str(root, "published_at");
            _dllUrl = dll;
            _error = "";

            if (before == Status.Installed)
            {
                _status = Status.Installed;
                return;
            }

            if (!IsNewer(tag))
            {
                _status = Status.UpToDate;
                Plugin.Logger.LogInfo($"Updates: {Plugin.Version} is up to date (latest release {tag}).");
                return;
            }

            if (dll.Length == 0)
            {
                _status = Status.Failed;
                _error = $"{tag} has no {AssetName} to download.";
                Plugin.Logger.LogWarning($"Updates: {_error}");
                return;
            }

            _status = Status.Available;
            Plugin.Logger.LogInfo($"Updates: {tag} is available (installed {Plugin.Version}).");

            if (_announced != tag)
            {
                _announced = tag;
                if (Plugin.AutoInstallUpdates.Value)
                {
                    Interlocked.Exchange(ref _busy, 0);
                    await InstallAsync().ConfigureAwait(false);
                    return;
                }

                Toast.Show(
                    $"Chorus Mod {tag} is out",
                    "Click SETTINGS (now UPDATE) on the main menu to install it.",
                    10f,
                    Theme.Green,
                    "Update available"
                );
            }
        }
        catch (Exception e)
        {
            _status = before == Status.Installed ? Status.Installed : Status.Failed;
            _error = "GitHub can't be reached.";
            Plugin.Logger.LogWarning($"Updates: check failed: {e.Message}");
            if (manual)
            {
                Toast.Show("Update check failed", "GitHub can't be reached right now.", 6f, Theme.Red, "Updates");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task InstallAsync()
    {
        if (_status != Status.Available || _dllUrl.Length == 0
            || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        var tag = _latestTag;
        try
        {
            _status = Status.Installing;
            var data = await Http.GetByteArrayAsync(_dllUrl).ConfigureAwait(false);

            // A .NET DLL starts with "MZ"; anything else (an error page...)
            // must never replace the mod.
            if (data.Length < 32 * 1024 || data[0] != 'M' || data[1] != 'Z')
            {
                throw new InvalidDataException("the downloaded file isn't a DLL");
            }

            var path = PluginPath();
            var old = path + ".old";
            if (File.Exists(old))
            {
                File.Delete(old);
            }

            // The loaded DLL can be renamed, not overwritten.
            File.Move(path, old);
            try
            {
                File.WriteAllBytes(path, data);
            }
            catch
            {
                File.Move(old, path); // put the working one back
                throw;
            }

            _status = Status.Installed;
            Plugin.Logger.LogInfo($"Updates: {tag} installed to {path}, active at the next launch.");
            Toast.Show(
                $"Chorus Mod {tag} installed",
                "Restart Clone Hero to use the new version.",
                10f,
                Theme.Green,
                "Updated"
            );
        }
        catch (Exception e)
        {
            _status = Status.Failed;
            _error = $"couldn't install {tag} ({e.Message}). Download it from GitHub instead.";
            Plugin.Logger.LogError($"Updates: install failed: {e}");
            Toast.Show("Update failed", $"Download {tag} from GitHub: SETTINGS > Updates.", 8f, Theme.Red, "Updates");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// Opens the release page (changelog, manual download) in the browser.
    public static void OpenReleasePage()
    {
        var url = _releaseUrl.Length > 0 ? _releaseUrl : $"https://github.com/{Repo}/releases/latest";
        Application.OpenURL(url);
    }

    /// "V1.8" newer than the installed "1.7.0"?
    private static bool IsNewer(string tag)
    {
        return ParseVersion(tag) is { } latest
            && ParseVersion(Plugin.Version) is { } installed
            && latest > installed;
    }

    /// "V1.8" / "v1.8.1" / "1.8" -> 1.8.0 / 1.8.1 / 1.8.0 (missing parts as 0,
    /// so 1.8 and 1.8.0 compare equal).
    private static Version? ParseVersion(string text)
    {
        var trimmed = text.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(trimmed, out var version))
        {
            return null;
        }

        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// Where the running DLL is.
    private static string PluginPath()
    {
        var location = typeof(Plugin).Assembly.Location;
        if (!string.IsNullOrEmpty(location) && File.Exists(location))
        {
            return location;
        }

        var found = Directory.GetFiles(Paths.PluginPath, AssetName, SearchOption.AllDirectories).FirstOrDefault();
        return found ?? throw new FileNotFoundException($"{AssetName} not found under {Paths.PluginPath}");
    }

    /// The copy left by the last update, no longer loaded.
    private static void DeleteOldCopy()
    {
        try
        {
            var old = PluginPath() + ".old";
            if (File.Exists(old))
            {
                File.Delete(old);
                Plugin.Logger.LogInfo("Updates: removed the previous version's copy.");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogInfo($"Updates: couldn't remove the previous copy yet: {e.Message}");
        }
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
