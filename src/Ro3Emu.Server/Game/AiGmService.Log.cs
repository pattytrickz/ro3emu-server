using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ro3Emu.Server.Game;

public sealed partial class AiGmService
{
    private List<Entry>? _entries;

    /// <summary>
    /// Whether one more action at <paramref name="at"/> keeps every <paramref name="span"/>-long stretch at or under
    /// <paramref name="max"/> actions. The busiest stretch starts at an action, so only those starts are checked.
    /// </summary>
    internal static bool Fits(IEnumerable<DateTimeOffset> done, DateTimeOffset at, int max, TimeSpan span)
    {
        var all = done.Append(at).Order().ToList();
        return all.Where(t => t > at - span && t <= at).All(from => all.Count(t => t >= from && t < from + span) <= max);
    }

    private IEnumerable<DateTimeOffset> Times(params string[] actions) =>
        Entries().Where(e => actions.Contains(e.Action)).Select(e => e.At);

    private List<Entry> Entries()
    {
        if (_entries != null) return _entries;
        try
        {
            _entries = roles.ServerState(LogKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<List<Entry>>(json) : null;
        }
        catch (JsonException e)
        {
            LogBadState(log, e);
        }
        return _entries ??= [];
    }

    /// <summary>Keeps the action; entries older than two weeks no longer count for any limit and are dropped.</summary>
    private Task RecordAsync(Entry entry)
    {
        var kept = Entries();
        kept.RemoveAll(e => e.At < clock.GetUtcNow() - 2 * Week);
        kept.Add(entry);
        return roles.SetServerStateAsync(LogKey, JsonSerializer.Serialize(kept));
    }

    /// <summary>A cancelled event that had not started yet stops counting against the limits.</summary>
    private Task ForgetPlannedAsync(string action, string detail, DateTimeOffset now)
    {
        var kept = Entries();
        return kept.RemoveAll(e => e.Action == action && e.Detail == detail && e.At > now) > 0
            ? roles.SetServerStateAsync(LogKey, JsonSerializer.Serialize(kept))
            : Task.CompletedTask;
    }

    private void Audit(string line)
    {
        if (options.Value.GmAudit && !GmCommands.AppendAudit(options.Value, AiGmOptions.Maker, "-", "aigm", line))
            LogAuditFailed(log);
    }

    /// <summary>Why a start time is not allowed, or null: from now (a minute's slack) to MaxDaysAhead.</summary>
    private string? StartRefusal(DateTimeOffset? start, DateTimeOffset now) =>
        start is { } s && (s < now.AddMinutes(-1) || s > now.AddDays(Limits.MaxDaysAhead))
            ? $"start must be between now and {Limits.MaxDaysAhead} days ahead" : null;

    private static string? Clip(string? text, int max) =>
        text?.Trim() is { Length: > 0 } t ? (t.Length > max ? t[..max] : t) : null;

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex SlugPattern();

    private static string? Slug(string? id) => id?.Trim().ToLowerInvariant() is { } s && SlugPattern().IsMatch(s) ? s : null;

    private static string When(DateTimeOffset t) => t.UtcDateTime.ToString("ddd d MMM HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>The limits, what of them is used in the stretch ending now, and the item pool with its names.</summary>
    private object LimitsData()
    {
        var now = clock.GetUtcNow();
        int Used(TimeSpan span, params string[] actions) => Times(actions).Count(t => t > now - span && t <= now);
        return new
        {
            Limits.Enabled, Limits.MaxDaysAhead, Limits.MaxExpPercent, Limits.MaxDropRolls, Limits.MaxCardRolls, Limits.MaxRateHours,
            Limits.MaxRateWindowsPerWeek, Limits.MaxGiftsPerWeek, Limits.MaxLoginGiftHours, Limits.MvpPool, Limits.MaxMvpSpawnsPerDay,
            Limits.MaxAnnouncementsPerDay,
            ItemPool = Limits.ItemPool.Select(p => new { p.Item, p.MaxCount, Name = data.ItemName(p.Item) }),
            Used = new
            {
                RatesThisWeek = Used(Week, "rates"), GiftsThisWeek = Used(Week, "gift", "logingift"),
                MvpToday = Used(Day, "mvp"), AnnouncementsToday = Used(Day, "announce"),
            },
        };
    }

    /// <summary>Every running and planned rate window and login gift (the owner's too, so the AI GM can plan around
    /// them), and the AI GM's actions of the last week.</summary>
    private object ListData()
    {
        var now = clock.GetUtcNow();
        return new
        {
            Now = now,
            Rates = events.WindowsAfter(now).OrderBy(w => w.Start).Select(w => new
            {
                w.Name, w.Start, w.End, w.ExpPercent, w.DropRolls, w.CardRolls, Mine = w.By == AiGmOptions.Maker,
            }),
            LoginGifts = events.GiftsAfter(now).OrderBy(g => g.Start).Select(g => new
            {
                g.Id, g.Start, g.End, g.Item, g.Count, Mine = g.By == AiGmOptions.Maker,
            }),
            Done = Entries().Where(e => e.At > now - Week).OrderBy(e => e.At),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI GM {Action}: {Detail}")]
    private static partial void LogAction(ILogger logger, string action, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "server_state aigm_log is not valid JSON; the AI GM's limits start from none")]
    private static partial void LogBadState(ILogger logger, Exception e);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI GM audit line not written")]
    private static partial void LogAuditFailed(ILogger logger);
}
