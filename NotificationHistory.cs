using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// One trophy site notification, as kept in the history.
/// </summary>
public class Notification
{
    public string Id { get; set; } = "";
    public long Time { get; set; } // unix seconds, when the site sent it
    public string Kind { get; set; } = ""; // see NotificationHistory.Kinds
    public string Label { get; set; } = "";
    public string Title { get; set; } = "";
    public string TitleBefore { get; set; } = ""; // rank-up toasts only
    public string Message { get; set; } = "";
    public float Seconds { get; set; }

    /// Its toast went on screen, or the history was opened since.
    public bool Seen { get; set; }

    /// Its toast was on screen (false: disabled, streamer mode...).
    public bool Toasted { get; set; }

    public Color Accent => Kind switch
    {
        NotificationHistory.Trophy => TrophyListener.TrophyAccent,
        NotificationHistory.Record => TrophyListener.RecordAccent,
        NotificationHistory.Level => TrophyListener.LevelAccent,
        NotificationHistory.DuelWon => TrophyListener.DuelWonAccent,
        NotificationHistory.DuelLost => TrophyListener.DuelLostAccent,
        NotificationHistory.Challenge => TrophyListener.ChallengeAccent,
        NotificationHistory.Announcement => TrophyListener.AnnouncementAccent,
        _ => Toast.DefaultAccent,
    };

    /// Puts its toast on screen. With `historyId`, the toast marks the
    /// entry as seen once it actually shows.
    public void ShowToast(string? historyId = null)
    {
        switch (Kind)
        {
            case NotificationHistory.Level:
            case NotificationHistory.DuelWon:
                Toast.ShowRankUp(TitleBefore, Title, Message, Seconds, Accent, Label, historyId);
                break;
            case NotificationHistory.Challenge:
                Toast.ShowClash(Title, Message, Seconds, Accent, Label, historyId);
                break;
            default:
                Toast.Show(Title, Message, Seconds, Accent, Label, historyId);
                break;
        }
    }
}

/// <summary>
/// Every trophy site notification meant for the player, toasted or not
/// (toast type turned off, streamer mode, a click that dismissed it...),
/// saved to BepInEx/config/chorus-mod-notifications.json so the history
/// survives restarts. Read in the NotificationWindow.
///
/// Thread-safe: TrophyListener adds from its background reader.
/// </summary>
public static class NotificationHistory
{
    public const string Trophy = "trophy";
    public const string Record = "record";
    public const string Level = "level";
    public const string DuelWon = "duel_won";
    public const string DuelLost = "duel_lost";
    public const string Challenge = "challenge";
    public const string Announcement = "announcement";

    private const int MaxEntries = 300;

    private static readonly string FilePath = Path.Combine(
        Paths.ConfigPath, "chorus-mod-notifications.json"
    );

    private static readonly object Gate = new();
    private static List<Notification>? _entries; // oldest first
    private static int _unseen;

    /// Bumped on every change: windows reload their copy when it moves.
    public static int Version { get; private set; }

    /// Notifications never seen: not toasted, history not opened since.
    public static int Unseen
    {
        get
        {
            lock (Gate)
            {
                Load();
                return _unseen;
            }
        }
    }

    /// Records the notification and, if `toast`, shows it (the toast marks
    /// it seen once on screen). A replayed id is ignored.
    public static void Add(Notification notification, bool toast)
    {
        lock (Gate)
        {
            Load();
            if (notification.Id.Length == 0)
            {
                notification.Id = Guid.NewGuid().ToString("N");
            }
            else if (_entries!.Any(n => n.Id == notification.Id))
            {
                return;
            }

            if (notification.Time <= 0)
            {
                notification.Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            _entries!.Add(notification);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }

            Changed();
        }

        if (toast)
        {
            notification.ShowToast(notification.Id);
        }
    }

    /// Called by Toast when the entry's toast goes on screen.
    public static void MarkToasted(string id)
    {
        lock (Gate)
        {
            var entry = _entries?.FirstOrDefault(n => n.Id == id);
            if (entry == null || (entry.Toasted && entry.Seen))
            {
                return;
            }

            entry.Toasted = true;
            entry.Seen = true;
            Changed();
        }
    }

    public static void MarkAllSeen()
    {
        lock (Gate)
        {
            Load();
            if (_unseen == 0)
            {
                return;
            }

            foreach (var entry in _entries!)
            {
                entry.Seen = true;
            }

            Changed();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Load();
            _entries!.Clear();
            Changed();
        }
    }

    /// Copies, newest first.
    public static List<Notification> Snapshot()
    {
        lock (Gate)
        {
            Load();
            return _entries!
                .Select(Copy)
                .Reverse()
                .ToList();
        }
    }

    private static Notification Copy(Notification n) => new()
    {
        Id = n.Id,
        Time = n.Time,
        Kind = n.Kind,
        Label = n.Label,
        Title = n.Title,
        TitleBefore = n.TitleBefore,
        Message = n.Message,
        Seconds = n.Seconds,
        Seen = n.Seen,
        Toasted = n.Toasted,
    };

    /// Call under Gate.
    private static void Changed()
    {
        _unseen = _entries!.Count(n => !n.Seen);
        Version++;
        Save();
    }

    /// Call under Gate.
    private static void Load()
    {
        if (_entries != null)
        {
            return;
        }

        _entries = new List<Notification>();
        try
        {
            if (File.Exists(FilePath))
            {
                _entries = JsonSerializer.Deserialize<List<Notification>>(File.ReadAllText(FilePath))
                    ?? new List<Notification>();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Notifications: couldn't read the history, starting empty: {e.Message}");
        }

        _unseen = _entries.Count(n => !n.Seen);
    }

    /// Call under Gate.
    private static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Notifications: couldn't save the history: {e.Message}");
        }
    }
}
