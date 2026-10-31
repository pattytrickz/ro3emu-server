using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Sol RO3 house events, not original behaviour (docs/guide/events.md):
/// <list type="bullet">
/// <item>Rate windows: ServerOptions.EventRates plus the ones a GM starts ("!rates"); RewardService reads
/// <see cref="Current"/> for every kill. The marquee says when one opens or ends, and tells a player who logs in
/// during one.</item>
/// <item>Login gifts: ServerOptions.LoginGifts plus GM ones ("!logingift"), mailed once per role at its first login
/// (or level-up past MinLevel) inside the window.</item>
/// <item>The gift tool ("!gift"): one mail to every role, the online ones, a level range or the recently active.</item>
/// </list>
/// GM-made windows and gifts, and each role's claim of a login gift, are kept in server_state, so a restart keeps them.
/// The AI players (ServerOptions.StatusExcludedAccounts) get no gifts.
/// </summary>
public sealed partial class EventService : IGameTicker
{
    internal const string WindowsKey = "event_rates", GiftsKey = "login_gifts";
    private const int MaxRolls = 5, MaxExpPercent = 1000;

    private readonly ServerOptions _options;
    private readonly RoleStore _roles;
    private readonly MailService _mail;
    private readonly SessionRegistry _sessions;
    private readonly GameData _data;
    private readonly GmCommands _gm;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly ILogger<EventService> _log;
    /// <summary>Per login: "" once told about the running windows, and the login gift ids already checked.</summary>
    private readonly ConditionalWeakTable<GameSession, HashSet<string>> _seen = [];
    private List<EventRateWindow> _gmWindows = [];
    private List<LoginGift> _gmGifts = [];
    private bool _loaded;
    private HashSet<EventRateWindow> _running = [];
    private long _lastSecond;

    public EventService(IOptions<ServerOptions> options, RoleStore roles, MailService mail, SessionRegistry sessions,
        SceneService scene, CharacterService character, GameData data, GmCommands gm, GameLoop loop, TimeProvider clock,
        ILogger<EventService> log)
    {
        _options = options.Value; _roles = roles; _mail = mail; _sessions = sessions; _data = data; _gm = gm; _loop = loop;
        _clock = clock; _log = log;
        scene.Loaded.Add(LoadedAsync);
        character.ProgressHandlers.Add(LoadedAsync);
        gm.Register("rates", "[<hours> <exp%> [drops] [cards] [name] | stop] (event rates: !rates 48 200 1 2 Bonus Weekend)", RatesGmAsync);
        gm.Register("gift", "<item> <count> all|online|level <min>-<max>|active <days> [title] (mail every matching role)", GiftGmAsync);
        gm.Register("logingift", "[<id> <item> <count> <hours> [title] | stop <id>] (once per role, at login inside the window)", LoginGiftGmAsync);
    }

    /// <summary>The kill rates now: EXP percent of the house rate, loot rolls and card chance factor (each 1 when quiet).</summary>
    public (int ExpPercent, int DropRolls, int CardRolls) Current => Combine(Windows(), _clock.GetUtcNow());

    internal static (int ExpPercent, int DropRolls, int CardRolls) Combine(IEnumerable<EventRateWindow> windows, DateTimeOffset now)
    {
        int exp = 100, drops = 1, cards = 1;
        foreach (var w in windows.Where(w => Open(w, now)))
        {
            exp = Math.Max(exp, Math.Clamp(w.ExpPercent, 100, MaxExpPercent));
            drops = Math.Max(drops, Math.Clamp(w.DropRolls, 1, MaxRolls));
            cards = Math.Max(cards, Math.Clamp(w.CardRolls, 1, MaxRolls));
        }
        return (exp, drops, cards);
    }

    private static bool Open(EventRateWindow w, DateTimeOffset now) => w.Start <= now && now < w.End;
    private static bool Open(LoginGift g, DateTimeOffset now) => g.Start <= now && now < g.End;

    private IEnumerable<EventRateWindow> Windows() { Load(); return _options.EventRates.Concat(_gmWindows); }
    private IEnumerable<LoginGift> Gifts() { Load(); return _options.LoginGifts.Concat(_gmGifts); }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        _gmWindows = Read<List<EventRateWindow>>(WindowsKey) ?? [];
        _gmGifts = Read<List<LoginGift>>(GiftsKey) ?? [];
    }

    private T? Read<T>(string key)
    {
        try
        {
            return _roles.ServerState(key) is { Length: > 0 } json ? JsonSerializer.Deserialize<T>(json) : default;
        }
        catch (JsonException e)
        {
            LogBadState(_log, e, key);
            return default;
        }
    }

    /// <summary>"x2 EXP, x2 cards" for a marquee.</summary>
    internal static string Describe(int exp, int drops, int cards)
    {
        var parts = new List<string>();
        if (exp > 100) parts.Add(exp % 100 == 0 ? $"x{exp / 100} EXP" : $"+{exp - 100}% EXP");
        if (drops > 1) parts.Add($"x{drops} loot");
        if (cards > 1) parts.Add($"x{cards} card chance");
        return parts.Count == 0 ? "no bonus" : string.Join(", ", parts);
    }

    private static string Until(DateTimeOffset end) =>
        end.UtcDateTime.ToString("ddd d MMM HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Notice(EventRateWindow w) =>
        $"{w.Name}: {Describe(w.ExpPercent, w.DropRolls, w.CardRolls)} until {Until(w.End)}.";

    public void Tick(GameTime time)
    {
        var now = _clock.GetUtcNow();
        long second = now.ToUnixTimeSeconds();
        if (second == _lastSecond) return;
        bool startup = _lastSecond == 0;
        _lastSecond = second;
        var running = Windows().Where(w => Open(w, now)).ToHashSet();
        // A window already open when the server starts is told at login, not announced again.
        if (!startup)
        {
            foreach (var w in running.Except(_running))
                _loop.TrackRuntimeDetached("event opens", () => _gm.AnnounceAsync(Notice(w)));
            foreach (var w in _running.Except(running))
                _loop.TrackRuntimeDetached("event ends", () => _gm.AnnounceAsync($"{w.Name} has ended. Thanks for playing!"));
        }
        _running = running;
    }

    /// <summary>
    /// Scene loads and progress changes (every kill): tells the running windows once per login, and claims each due
    /// login gift once per session, so the database is asked about a gift once per login, not per kill.
    /// </summary>
    private Task LoadedAsync(GameSession s)
    {
        if (s.Role is not { } role || ServerOptions.AccountListed(_options.StatusExcludedAccounts, s.Account)) return Task.CompletedTask;
        var now = _clock.GetUtcNow();
        var seen = _seen.GetValue(s, _ => []);
        if (seen.Add(""))
            foreach (var w in Windows().Where(w => Open(w, now)))
                _loop.PostOwned(s, () => GmCommands.ReplyAsync(s, Notice(w)), "event notice");
        int level = s.Progress?.Level ?? role.Level;
        var due = Gifts().Where(g => Open(g, now) && level >= g.MinLevel && seen.Add(g.Id)).ToList();
        if (due.Count > 0) _loop.TrackLegacyDetached("login gift", () => ClaimAsync(role.Id, role.Name, due));
        return Task.CompletedTask;
    }

    private static string ClaimKey(LoginGift g, long role) => $"login_gift/{g.Id}/{role}";

    /// <summary>
    /// Mails each due gift the role hasn't claimed. The claim is written before the mail, so a failure can lose one gift
    /// but never sends it twice. Callers hold the realm (a legacy detached job or a GM command), so claims never overlap.
    /// </summary>
    internal async Task<int> ClaimAsync(long role, string name, IReadOnlyList<LoginGift> due)
    {
        int sent = 0;
        foreach (var g in due)
        {
            if (await _roles.ServerStateAsync(ClaimKey(g, role)) != null) continue;
            await _roles.SetServerStateAsync(ClaimKey(g, role), _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            try
            {
                string mail = await _mail.SendRawAsync(role, g.Title, g.Message is { Length: > 0 } text ? text : g.Title,
                    [.. g.Attachments().Select(a => new MailService.Attachment(a.Item, a.Count))]);
                LogLoginGift(_log, g.Id, g.Count, g.Item, name, role, mail);
                sent++;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
            {
                LogLoginGiftLost(_log, e, g.Id, name, role);
            }
        }
        return sent;
    }

    // ---- GM ----

    private async Task RatesGmAsync(GameSession s, string[] a)
    {
        var now = _clock.GetUtcNow();
        if (a.Length == 0)
        {
            var open = Windows().Where(w => Open(w, now) || w.Start > now).OrderBy(w => w.Start)
                .Select(w => $"{w.Name} {Until(w.Start)} - {Until(w.End)}: {Describe(w.ExpPercent, w.DropRolls, w.CardRolls)}").ToList();
            await GmCommands.ReplyAsync(s, open.Count == 0 ? "No event rates now or planned." : string.Join(" | ", open));
            return;
        }
        Load();
        if (a[0] == "stop")
        {
            // Ends the GM windows now; windows from the settings stay (they are the owner's schedule).
            foreach (var w in _gmWindows.Where(w => w.End > now)) w.End = now;
            await SaveAsync(WindowsKey, _gmWindows);
            await GmCommands.ReplyAsync(s, "GM event rates stopped.");
            return;
        }
        double hours = double.Parse(a[0], CultureInfo.InvariantCulture);
        if (hours is <= 0 or > 24 * 31) throw new ArgumentException("hours");
        var window = new EventRateWindow
        {
            Start = now, End = now.AddHours(hours),
            ExpPercent = Math.Clamp(a.Length > 1 ? int.Parse(a[1], CultureInfo.InvariantCulture) : 200, 100, MaxExpPercent),
            DropRolls = Math.Clamp(a.Length > 2 ? int.Parse(a[2], CultureInfo.InvariantCulture) : 1, 1, MaxRolls),
            CardRolls = Math.Clamp(a.Length > 3 ? int.Parse(a[3], CultureInfo.InvariantCulture) : 1, 1, MaxRolls),
            Name = a.Length > 4 ? string.Join(' ', a[4..]) : "Bonus event",
        };
        await AddWindowAsync(window);
        LogRates(_log, s.Role?.Name, Notice(window));
        await GmCommands.ReplyAsync(s, "Started: " + Notice(window)); // the ticker announces it to everyone
    }

    private async Task LoginGiftGmAsync(GameSession s, string[] a)
    {
        var now = _clock.GetUtcNow();
        Load();
        if (a.Length == 0)
        {
            var open = Gifts().Where(g => g.End > now)
                .Select(g => $"{g.Id}: {g.Count} x {_data.ItemName(g.Item)} until {Until(g.End)}").ToList();
            await GmCommands.ReplyAsync(s, open.Count == 0 ? "No login gifts now or planned." : string.Join(" | ", open));
            return;
        }
        if (a[0] == "stop" && a.Length == 2)
        {
            int stopped = 0;
            foreach (var g in _gmGifts.Where(g => g.Id == a[1] && g.End > now)) { g.End = now; stopped++; }
            await SaveAsync(GiftsKey, _gmGifts);
            await GmCommands.ReplyAsync(s, stopped > 0 ? $"Login gift {a[1]} stopped." : $"No running GM login gift {a[1]}.");
            return;
        }
        if (a.Length < 4) throw new ArgumentException("usage");
        string id = a[0];
        int item = int.Parse(a[1], CultureInfo.InvariantCulture), count = int.Parse(a[2], CultureInfo.InvariantCulture);
        double hours = double.Parse(a[3], CultureInfo.InvariantCulture);
        if (count <= 0 || hours is <= 0 or > 24 * 62) throw new ArgumentException("count/hours");
        if (_data.ItemName(item) is not { Length: > 0 } itemName) { await GmCommands.ReplyAsync(s, $"No item {item}."); return; }
        var gift = new LoginGift
        {
            Id = id, Item = item, Count = count, Start = now, End = now.AddHours(hours),
            Title = a.Length > 4 ? string.Join(' ', a[4..]) : "A gift from the Sol RO3 team",
        };
        if (!await AddLoginGiftAsync(gift)) { await GmCommands.ReplyAsync(s, $"Login gift id {id} was used before; pick a new one."); return; }
        LogLoginGiftMade(_log, s.Role?.Name, id, count, item, gift.End);
        await GmCommands.ReplyAsync(s, $"Login gift {id}: {count} x {itemName} until {Until(gift.End)}; online players have theirs.");
    }

    private Task SaveAsync<T>(string key, T value) => _roles.SetServerStateAsync(key, JsonSerializer.Serialize(value));

    /// <summary>Every rate window, from the settings and the GMs, still open or planned at <paramref name="now"/>
    /// (a cancelled planned one, which ends where it starts, is not).</summary>
    internal IReadOnlyList<EventRateWindow> WindowsAfter(DateTimeOffset now) => [.. Windows().Where(w => w.End > now && w.End > w.Start)];

    /// <summary>Every login gift, from the settings and the GMs, still open or planned at <paramref name="now"/>.</summary>
    internal IReadOnlyList<LoginGift> GiftsAfter(DateTimeOffset now) => [.. Gifts().Where(g => g.End > now && g.End > g.Start)];

    /// <summary>Keeps a GM window, open now or planned; the ticker announces it when it opens.</summary>
    internal async Task AddWindowAsync(EventRateWindow window)
    {
        Load();
        _gmWindows.RemoveAll(w => w.End <= _clock.GetUtcNow());
        _gmWindows.Add(window);
        await SaveAsync(WindowsKey, _gmWindows);
    }

    /// <summary>Ends <paramref name="by"/>'s windows named <paramref name="name"/>; a planned one never opens. How many.</summary>
    internal async Task<int> EndWindowsAsync(string by, string name)
    {
        Load();
        var now = _clock.GetUtcNow();
        var ended = _gmWindows.Where(w => w.By == by && w.Name == name && w.End > now).ToList();
        foreach (var w in ended) w.End = w.Start > now ? w.Start : now;
        if (ended.Count > 0) await SaveAsync(WindowsKey, _gmWindows);
        return ended.Count;
    }

    /// <summary>
    /// Keeps a GM login gift, open now or planned. The roles online when it opens get it at their next scene load or
    /// kill, and at once when it is open already. False when its id was used before (ids are never reused).
    /// </summary>
    internal async Task<bool> AddLoginGiftAsync(LoginGift gift)
    {
        if (Gifts().Any(g => g.Id == gift.Id)) return false;
        _gmGifts.Add(gift);
        await SaveAsync(GiftsKey, _gmGifts);
        if (Open(gift, _clock.GetUtcNow()))
            foreach (var o in _sessions.All().Where(o => o.Role != null && !ServerOptions.AccountListed(_options.StatusExcludedAccounts, o.Account)))
                await ClaimAsync(o.Role!.Id, o.Role.Name, [gift]);
        return true;
    }

    /// <summary>Ends <paramref name="by"/>'s login gift <paramref name="id"/>; a planned one never opens. Whether one ran.</summary>
    internal async Task<bool> EndLoginGiftAsync(string by, string id)
    {
        Load();
        var now = _clock.GetUtcNow();
        var ended = _gmGifts.Where(g => g.By == by && g.Id == id && g.End > now).ToList();
        foreach (var g in ended) g.End = g.Start > now ? g.Start : now;
        if (ended.Count > 0) await SaveAsync(GiftsKey, _gmGifts);
        return ended.Count > 0;
    }

    /// <summary>"!gift 1250709 3 level 40-99 Halloween candy": one mail per matching role, the AI players left out.</summary>
    private async Task GiftGmAsync(GameSession s, string[] a)
    {
        if (a.Length < 3) throw new ArgumentException("usage");
        int item = int.Parse(a[0], CultureInfo.InvariantCulture), count = int.Parse(a[1], CultureInfo.InvariantCulture);
        if (count <= 0) throw new ArgumentException("count");
        if (_data.ItemName(item) is not { Length: > 0 } itemName) { await GmCommands.ReplyAsync(s, $"No item {item}."); return; }
        var (filter, used) = GiftFilter(a[2..], _clock.GetUtcNow());
        string title = a.Length > 2 + used ? string.Join(' ', a[(2 + used)..]) : "A gift from the Sol RO3 team";
        var (sent, matched) = await GiftAsync(item, count, filter, title);
        LogGift(_log, s.Role?.Name, count, item, itemName, sent, matched, string.Join(' ', a[2..(2 + used)]), title);
        await GmCommands.ReplyAsync(s, $"Mailed {count} x {itemName} to {sent} of {matched} role(s).");
    }

    /// <summary>One mail of <paramref name="count"/> x <paramref name="item"/> to every matching role, the AI players
    /// left out: how many were mailed, and how many matched.</summary>
    internal async Task<(int Sent, int Matched)> GiftAsync(int item, int count, Func<Role, bool, bool> filter, string title)
    {
        var online = _sessions.All().Where(o => o.Role != null).Select(o => o.Role!.Id).ToHashSet();
        var roles = (await _roles.AllAsync())
            .Where(r => !ServerOptions.AccountListed(_options.StatusExcludedAccounts, r.Account) && filter(r, online.Contains(r.Id)))
            .ToList();
        int sent = 0;
        foreach (var r in roles)
        {
            try
            {
                await _mail.SendRawAsync(r.Id, title, title, [new MailService.Attachment(item, count)]);
                sent++;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
            {
                LogGiftFailed(_log, e, r.Name, r.Id);
            }
        }
        return (sent, roles.Count);
    }

    /// <summary>The recipient filter at the start of <paramref name="a"/>, and how many words it used.</summary>
    internal static (Func<Role, bool, bool> Match, int Used) GiftFilter(string[] a, DateTimeOffset now)
    {
        switch (a[0].ToLowerInvariant())
        {
            case "all": return ((_, _) => true, 1);
            case "online": return ((_, on) => on, 1);
            case "level" when a.Length > 1 && a[1].Split('-') is [var lo, var hi]:
                int min = int.Parse(lo, CultureInfo.InvariantCulture), max = int.Parse(hi, CultureInfo.InvariantCulture);
                return ((r, _) => r.Level >= min && r.Level <= max, 2);
            case "active" when a.Length > 1:
                long since = now.AddDays(-double.Parse(a[1], CultureInfo.InvariantCulture)).ToUnixTimeSeconds();
                return ((r, on) => on || r.LastLoginTime >= since, 2);
            default: throw new ArgumentException("filter");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "server_state {Key} is not valid JSON; starting without it")]
    private static partial void LogBadState(ILogger logger, Exception e, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Login gift {Gift}: {Count} x {Item} to {Role} ({RoleId}), mail {Mail}")]
    private static partial void LogLoginGift(ILogger logger, string gift, int count, int item, string role, long roleId, string mail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Login gift {Gift} to {Role} ({RoleId}) was claimed but not mailed")]
    private static partial void LogLoginGiftLost(ILogger logger, Exception e, string gift, string role, long roleId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GM {Gm} started event rates: {Notice}")]
    private static partial void LogRates(ILogger logger, string? gm, string notice);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GM {Gm} made login gift {Id}: {Count} x {Item} until {End}")]
    private static partial void LogLoginGiftMade(ILogger logger, string? gm, string id, int count, int item, DateTimeOffset end);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gift to {Role} ({RoleId}) failed")]
    private static partial void LogGiftFailed(ILogger logger, Exception e, string role, long roleId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GM {Gm} gifted {Count} x {Item} ({ItemName}) to {Sent} of {Matched} role(s) ({Filter}): {Title}")]
    private static partial void LogGift(ILogger logger, string? gm, int count, int item, string itemName, int sent, int matched, string filter, string title);
}
