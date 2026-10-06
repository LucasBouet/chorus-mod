using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Duel window's Select: puts a received challenge's chart under the
/// cursor in the game's song list.
///
/// The chart is found in the game's song cache by its checksum (the same
/// MD5 the trophy site sends). Opening the song list from code would mean
/// driving the main menu through obfuscated methods, so the player opens
/// Quickplay themselves: the request waits (a few minutes at most), and as
/// soon as the song list is on screen the chart is selected through
/// SongSelect's own "go to this song" method. (Not the game's list
/// filter: it sticks for the whole session.)
///
/// Speed, modifiers, instrument and difficulty are chosen in the game's
/// own screens afterwards: a toast says which ones the challenge needs.
///
/// The game's song cache class and SongSelect's methods have generated
/// names that differ between game builds, so they're found by reflection
/// (by their types, not their names): the library is the biggest static
/// List<SongEntry> of the cache class, and "go to this song" is SongSelect's
/// only void method taking a SongEntry (property setters aside) -- when a
/// build has several, nothing is guessed: a toast says to pick the chart
/// by hand. Everything is in try/catch and
/// logged, so a game update only loses the feature.
/// </summary>
public static class ChartSelector
{
    private const float WaitSeconds = 300f;
    private const float SettleSeconds = 0.4f;
    private const float MaxTransitionWait = 2f;

    private static string? _pending;          // checksum, upper case
    private static string _pendingLabel = "";
    private static float _pendingUntil;
    private static float _songSelectSince = -1f;

    /// Checksums (upper case) of the given charts that are installed.
    /// One pass over the library: call once when the list changes, not
    /// every frame.
    public static HashSet<string> FindInstalled(IEnumerable<string> checksums)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var checksum in checksums)
        {
            if (checksum.Length > 0)
            {
                wanted.Add(checksum);
            }
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return found;
        }

        ForEachSong(song =>
        {
            var checksum = Checksum(song);
            if (checksum.Length > 0 && wanted.Contains(checksum))
            {
                found.Add(checksum);
            }

            return found.Count == wanted.Count; // stop when all found
        });
        return found;
    }

    /// Queues the chart: selected as soon as the song list is open.
    /// False if it isn't installed.
    public static bool Request(string checksum, string label)
    {
        if (Find(checksum) == null)
        {
            return false;
        }

        _pending = checksum.ToUpperInvariant();
        _pendingLabel = label;
        _pendingUntil = Time.unscaledTime + WaitSeconds;
        _songSelectSince = -1f;
        Plugin.Logger.LogInfo($"Chart select: waiting for the song list to select {label} ({_pending}).");
        return true;
    }

    /// Called every frame (from MainMenuOverlay, which runs all session).
    public static void Tick()
    {
        if (_pending == null)
        {
            return;
        }

        if (Time.unscaledTime > _pendingUntil)
        {
            Plugin.Logger.LogInfo("Chart select: the song list wasn't opened in time, request dropped.");
            _pending = null;
            return;
        }

        // Every SongSelect, not just the first one found: after a first
        // visit the game may keep more than one around.
        SongSelect? songSelect = null;
        bool transitioning;
        string state;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<SongSelect>();
            var parts = new List<string>();
            foreach (var candidate in all)
            {
                var active = candidate.isActive;
                var shown = candidate.gameObject.activeInHierarchy;
                parts.Add($"{(active ? "active" : "idle")}/{(shown ? "shown" : "hidden")}");
                // Shown is what counts: the game's isActive flag stays off
                // on later visits to the list, even while browsing it.
                if (songSelect == null && shown)
                {
                    songSelect = candidate;
                }
            }

            transitioning = BaseMenu.transitioning;
            state = $"{all.Length} song list(s) [{string.Join(", ", parts)}], transitioning={transitioning}";
        }
        catch (Exception e)
        {
            songSelect = null;
            transitioning = false;
            state = $"song list unreadable: {e.Message}";
        }

        LogState(state);

        if (songSelect == null)
        {
            _songSelectSince = -1f;
            return;
        }

        // Give the list a moment to build itself after opening. A menu
        // transition is waited for, but not forever: the flag may lag.
        if (_songSelectSince < 0f)
        {
            _songSelectSince = Time.unscaledTime;
            return;
        }

        var open = Time.unscaledTime - _songSelectSince;
        if (open < SettleSeconds || (transitioning && open < MaxTransitionWait))
        {
            return;
        }

        var checksum = _pending;
        _pending = null;
        Select(songSelect, checksum);
    }

    private static void Select(SongSelect songSelect, string checksum)
    {
        var song = Find(checksum);
        if (song == null)
        {
            Plugin.Logger.LogWarning($"Chart select: {checksum} isn't in the song cache anymore.");
            return;
        }

        try
        {
            var jump = JumpMethod();
            if (jump == null)
            {
                throw new InvalidOperationException("no single \"go to song\" method on this build");
            }

            jump.Invoke(songSelect, new object[] { song });
            Plugin.Logger.LogInfo($"Chart select: asked the song list for {_pendingLabel}; current song is now {Describe(songSelect.currentSong)}.");
            if (IsCurrent(songSelect, checksum))
            {
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Chart select: jumping to the song failed: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
        }

        // No filter fallback: the game's song list filter sticks for the
        // whole session, with no way out of it from the menus.
        Toast.Show(
            "Chart not selected",
            $"Couldn't jump to {_pendingLabel} on this game build: pick it in the list.",
            7f,
            TrophyListener.ChallengeAccent,
            "Duel"
        );
    }

    private static string _lastState = "";

    /// Diagnostic: one line per change of what the song list looks like
    /// while a request waits.
    private static void LogState(string state)
    {
        if (state != _lastState)
        {
            _lastState = state;
            Plugin.Logger.LogInfo($"Chart select: {state}");
        }
    }

    private static MethodInfo? _jump;
    private static bool _jumpSearched;

    private static MethodInfo? JumpMethod()
    {
        if (_jumpSearched)
        {
            return _jump;
        }

        _jumpSearched = true;
        var candidates = typeof(SongSelect)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(void) && !m.IsSpecialName) // not set_currentSong
            .Where(m =>
            {
                var parameters = m.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(SongEntry);
            })
            .ToArray();
        Plugin.Logger.LogInfo($"Chart select: SongSelect methods taking a song: {string.Join(", ", candidates.Select(m => m.Name))}.");
        _jump = candidates.Length == 1 ? candidates[0] : null;
        return _jump;
    }

    private static PropertyInfo? _library;
    private static bool _librarySearched;

    /// The biggest static List<SongEntry> of the song cache class.
    private static Il2CppSystem.Collections.Generic.List<SongEntry>? Library()
    {
        if (!_librarySearched)
        {
            _librarySearched = true;
            var listType = typeof(Il2CppSystem.Collections.Generic.List<SongEntry>);
            var cache = typeof(SongEntry).Assembly.GetType("ObjectPublicAbstractSealedLi1SoDi2ObInLi1SoUnique");
            var lists = cache?
                .GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.PropertyType == listType && p.GetIndexParameters().Length == 0)
                .ToArray() ?? Array.Empty<PropertyInfo>();

            var sizes = new List<string>();
            var best = -1;
            foreach (var property in lists)
            {
                var count = (property.GetValue(null) as Il2CppSystem.Collections.Generic.List<SongEntry>)?.Count ?? -1;
                sizes.Add($"{property.Name}={count}");
                if (count > best)
                {
                    best = count;
                    _library = property;
                }
            }

            Plugin.Logger.LogInfo($"Chart select: song cache {(cache == null ? "not found" : "lists: " + string.Join(", ", sizes))}; using {_library?.Name ?? "none"}.");

            // An empty library (scan not done yet): look again next time.
            if (best <= 0)
            {
                _librarySearched = false;
                _library = null;
            }
        }

        return _library?.GetValue(null) as Il2CppSystem.Collections.Generic.List<SongEntry>;
    }

    private static bool IsCurrent(SongSelect songSelect, string checksum)
    {
        try
        {
            var current = songSelect.currentSong;
            return current != null && string.Equals(Checksum(current), checksum, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SongEntry? Find(string checksum)
    {
        SongEntry? match = null;
        ForEachSong(song =>
        {
            if (string.Equals(Checksum(song), checksum, StringComparison.OrdinalIgnoreCase))
            {
                match = song;
                return true;
            }

            return false;
        });
        return match;
    }

    /// Walks the game's song library; `visit` returns true to stop.
    private static void ForEachSong(Func<SongEntry, bool> visit)
    {
        try
        {
            var library = Library();
            if (library == null)
            {
                return;
            }

            for (var i = 0; i < library.Count; i++)
            {
                var song = library[i];
                if (song != null && visit(song))
                {
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Chart select: reading the song library failed: {e.Message}");
        }
    }

    private static string Checksum(SongEntry song)
    {
        try
        {
            return song.ChecksumString ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string Describe(SongEntry? song)
    {
        if (song == null)
        {
            return "none";
        }

        try
        {
            return $"{song.Name_StrippedTags} ({Checksum(song)})";
        }
        catch (Exception)
        {
            return Checksum(song);
        }
    }
}
