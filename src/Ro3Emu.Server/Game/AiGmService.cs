using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Sol RO3 house rule, not original behaviour (docs/guide/ai-gm.md): the AI GM's requests on the loopback
/// POST /ro3emu/aigm. It runs events through the same code as the GMs (<see cref="EventService"/>, MvpService,
/// <see cref="GmCommands.AnnounceAsync"/>), and every request is checked against <see cref="AiGmOptions"/>, which
/// only the settings set:
/// <list type="bullet">
/// <item>Rate windows: within the EXP, loot and card caps, at most MaxRateHours long, never overlapping another window
/// (the owner's events stay the owner's), at most MaxRateWindowsPerWeek.</item>
/// <item>Gifts (a mail now or a login gift): only ItemPool items, at most each one's MaxCount, MaxGiftsPerWeek in all.</item>
/// <item>MVP respawns (MvpPool, MaxMvpSpawnsPerDay) and marquee announcements (MaxAnnouncementsPerDay, no links).</item>
/// </list>
/// Its actions are kept in server_state (<see cref="LogKey"/>) for the limits, and each one is a gm-audit.log line.
/// </summary>
public sealed partial class AiGmService(IOptions<ServerOptions> options, EventService events, MvpService mvp, GmCommands gm,
    RoleStore roles, GameData data, TimeProvider clock, ILogger<AiGmService> log)
{
    internal const string LogKey = "aigm_log";
    private static readonly TimeSpan Week = TimeSpan.FromDays(7), Day = TimeSpan.FromDays(1);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One request. <c>action</c>: limits, list, rates, logingift, gift, mvp, announce or cancel.</summary>
    public sealed class Request
    {
        public string Action { get; set; } = "";
        public DateTimeOffset? Start { get; set; }
        public double? Hours { get; set; }
        public int? ExpPercent { get; set; }
        public int? DropRolls { get; set; }
        public int? CardRolls { get; set; }
        public string? Name { get; set; }
        public string? Id { get; set; }
        public int? Item { get; set; }
        public int? Count { get; set; }
        public string? Title { get; set; }
        public int? MinLevel { get; set; }
        /// <summary>A gift's recipients, as "!gift": all, online, "level 10-40" or "active 7".</summary>
        public string? Filter { get; set; }
        public int? Cfg { get; set; }
        public string? Text { get; set; }
        /// <summary>For cancel: rates (by name) or logingift (by id).</summary>
        public string? Kind { get; set; }
    }

    public sealed record Reply(bool Ok, string Message, object? Data = null);

    /// <summary>One done action: when it counts (a rate window at its start), what, and its name or id.</summary>
    internal sealed record Entry(DateTimeOffset At, string Action, string Detail);

    private AiGmOptions Limits => options.Value.AiGm;

    public async Task<Reply> RunAsync(Request r)
    {
        if (r.Action == "limits") return new(true, "limits", LimitsData());
        if (r.Action == "list") return new(true, "list", ListData());
        if (!Limits.Enabled) return Refused("the AI GM is off (AiGm:Enabled)");
        try
        {
            var reply = r.Action switch
            {
                "rates" => await RatesAsync(r),
                "logingift" => await LoginGiftAsync(r),
                "gift" => await GiftAsync(r),
                "mvp" => await MvpAsync(r),
                "announce" => await AnnounceAsync(r),
                "cancel" => await CancelAsync(r),
                _ => Refused("unknown action " + r.Action),
            };
            if (reply.Ok) Audit(r.Action + " " + reply.Message);
            return reply;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            return Refused(e.Message);
        }
    }

    private static Reply Refused(string why) => new(false, why);

    // ---- rate windows ----

    private async Task<Reply> RatesAsync(Request r)
    {
        var now = clock.GetUtcNow();
        if (StartRefusal(r.Start, now) is { } late) return Refused(late);
        var start = r.Start is { } s && s > now ? s : now;
        if (r.Hours is not { } hours || hours <= 0 || hours > Limits.MaxRateHours) return Refused($"hours must be above 0 and at most {Limits.MaxRateHours}");
        int exp = r.ExpPercent ?? 100, drops = r.DropRolls ?? 1, cards = r.CardRolls ?? 1;
        if (exp is < 100 || exp > Limits.MaxExpPercent || drops < 1 || drops > Limits.MaxDropRolls || cards < 1 || cards > Limits.MaxCardRolls)
            return Refused($"rates must be within EXP 100-{Limits.MaxExpPercent}%, loot 1-{Limits.MaxDropRolls}, cards 1-{Limits.MaxCardRolls}");
        if (exp == 100 && drops == 1 && cards == 1) return Refused("a window without a bonus");
        var window = new EventRateWindow
        {
            Start = start, End = start.AddHours(hours), ExpPercent = exp, DropRolls = drops, CardRolls = cards,
            Name = Clip(r.Name, 40) ?? "Bonus event", By = AiGmOptions.Maker,
        };
        if (events.WindowsAfter(now).FirstOrDefault(o => o.Start < window.End && window.Start < o.End) is { } clash)
            return Refused($"overlaps {clash.Name} ({When(clash.Start)} - {When(clash.End)})");
        if (!Fits(Times("rates"), start, Limits.MaxRateWindowsPerWeek, Week))
            return Refused($"at most {Limits.MaxRateWindowsPerWeek} rate windows a week");
        await events.AddWindowAsync(window);
        await RecordAsync(new Entry(start, "rates", window.Name));
        LogAction(log, "rates", window.Name);
        return new(true, $"{window.Name}: {EventService.Describe(exp, drops, cards)}, {When(start)} - {When(window.End)}");
    }

    // ---- gifts ----

    private async Task<Reply> LoginGiftAsync(Request r)
    {
        var now = clock.GetUtcNow();
        if (StartRefusal(r.Start, now) is { } late) return Refused(late);
        var start = r.Start is { } s && s > now ? s : now;
        if (r.Hours is not { } hours || hours <= 0 || hours > Limits.MaxLoginGiftHours)
            return Refused($"hours must be above 0 and at most {Limits.MaxLoginGiftHours}");
        if (r.Item is not { } item || r.Count is not { } count) return Refused("item and count");
        if (PoolRefusal(item, count) is { } why) return Refused(why);
        if (Slug(r.Id) is not { } slug) return Refused("an id of letters, digits and dashes");
        if (!Fits(Times("gift", "logingift"), start, Limits.MaxGiftsPerWeek, Week)) return Refused($"at most {Limits.MaxGiftsPerWeek} gifts a week");
        var gift = new LoginGift
        {
            By = AiGmOptions.Maker, Id = "ai-" + slug, Item = item, Count = count, Start = start,
            End = start.AddHours(hours), Title = Clip(r.Title, 40) ?? "A gift from the Sol RO3 team", MinLevel = Math.Max(0, r.MinLevel ?? 0),
        };
        if (!await events.AddLoginGiftAsync(gift)) return Refused($"login gift id {gift.Id} was used before");
        await RecordAsync(new Entry(start, "logingift", gift.Id));
        LogAction(log, "logingift", gift.Id);
        return new(true, $"login gift {gift.Id}: {gift.Count} x {data.ItemName(gift.Item)}, {When(start)} - {When(gift.End)}");
    }

    private async Task<Reply> GiftAsync(Request r)
    {
        var now = clock.GetUtcNow();
        if (r.Item is not { } item || r.Count is not { } count) return Refused("item and count");
        if (PoolRefusal(item, count) is { } why) return Refused(why);
        if (!Fits(Times("gift", "logingift"), now, Limits.MaxGiftsPerWeek, Week)) return Refused($"at most {Limits.MaxGiftsPerWeek} gifts a week");
        var words = r.Filter?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } given ? given : ["online"];
        var (filter, used) = EventService.GiftFilter(words, now);
        if (used != words.Length) return Refused("filter: all, online, \"level <min>-<max>\" or \"active <days>\"");
        string title = Clip(r.Title, 40) ?? "A gift from the Sol RO3 team", who = string.Join(' ', words);
        var (sent, matched) = await events.GiftAsync(item, count, filter, title);
        await RecordAsync(new Entry(now, "gift", $"{item} x{count} {who}"));
        LogAction(log, "gift", $"{count} x {item} to {sent} of {matched} ({who})");
        return new(true, $"mailed {count} x {data.ItemName(item)} to {sent} of {matched} role(s)");
    }

    /// <summary>Why an item and count are not allowed, or null: only pool items, at most the entry's MaxCount.</summary>
    private string? PoolRefusal(int item, int count)
    {
        if (Limits.ItemPool.FirstOrDefault(p => p.Item == item) is not { } entry) return "the item is not in the AI GM's item pool";
        if (count < 1 || count > entry.MaxCount) return $"count must be 1-{entry.MaxCount} for item {item}";
        return data.Row("ItemConfig", item) is null ? $"no item {item} in the game data" : null;
    }

    // ---- MVPs, announcements, cancelling ----

    private async Task<Reply> MvpAsync(Request r)
    {
        if (r.Cfg is not { } cfg) return Refused("cfg: a field MVP config id");
        if (Limits.MvpPool.Length > 0 && !Limits.MvpPool.Contains(cfg)) return Refused($"MVP {cfg} is not in the AI GM's MVP pool");
        var now = clock.GetUtcNow();
        if (!Fits(Times("mvp"), now, Limits.MaxMvpSpawnsPerDay, Day)) return Refused($"at most {Limits.MaxMvpSpawnsPerDay} MVP spawns a day");
        string reply = await mvp.GmAsync(["spawn", cfg.ToString(CultureInfo.InvariantCulture)]);
        // MvpService.GmAsync: "MVP <cfg> <name> respawns on <map> <name>." on success; any other text is a refusal.
        if (!reply.Contains(" respawns on ", StringComparison.Ordinal)) return Refused(reply.Length > 300 ? $"MVP {cfg} cannot spawn now" : reply);
        await RecordAsync(new Entry(now, "mvp", cfg.ToString(CultureInfo.InvariantCulture)));
        LogAction(log, "mvp", reply);
        return new(true, reply);
    }

    /// <summary>Links, chat commands and mentions: never on an AI GM marquee.</summary>
    [GeneratedRegex(@"https?://|www\.|discord\.gg|\.com\b|\.net\b|^[!/@#]|@everyone|@here", RegexOptions.IgnoreCase)]
    private static partial Regex Unsafe();

    private async Task<Reply> AnnounceAsync(Request r)
    {
        string text = (r.Text ?? "").Trim();
        if (text.Length == 0 || text.Length > GmCommands.AnnouncementMaxLength)
            return Refused($"text: 1-{GmCommands.AnnouncementMaxLength} characters");
        if (text.Contains('\n') || Unsafe().IsMatch(text)) return Refused("text: one line, no links, commands or mentions");
        var now = clock.GetUtcNow();
        if (!Fits(Times("announce"), now, Limits.MaxAnnouncementsPerDay, Day))
            return Refused($"at most {Limits.MaxAnnouncementsPerDay} announcements a day");
        int reached = await gm.AnnounceAsync(text);
        await RecordAsync(new Entry(now, "announce", Clip(text, 60)!));
        return new(true, $"announced to {reached}");
    }

    /// <summary>Ends the AI GM's own rate window (by name) or login gift (by id); a planned one no longer counts.</summary>
    private async Task<Reply> CancelAsync(Request r)
    {
        var now = clock.GetUtcNow();
        var (kind, detail) = r switch
        {
            { Kind: "rates", Name: { } name } => ("rates", name),
            { Kind: "logingift", Id: { } id } => ("logingift", id),
            _ => throw new ArgumentException("kind rates with a name, or logingift with an id"),
        };
        bool done = kind == "rates"
            ? await events.EndWindowsAsync(AiGmOptions.Maker, detail) > 0
            : await events.EndLoginGiftAsync(AiGmOptions.Maker, detail);
        if (!done) return Refused("no running or planned AI GM event by that name");
        await ForgetPlannedAsync(kind, detail, now);
        return new(true, $"cancelled {kind} {detail}");
    }
}
