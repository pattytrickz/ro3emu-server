using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Supporter Merchant's goods (owner's plan v1.1 §2.2, Shared.SupporterShopRules; ServerOptions.Supporter.Shop):
/// <list type="bullet">
/// <item>Bought only by active supporters, for Supporter Tokens, within account-wide weekly limits (all characters,
/// the pass week turning Monday 05:00 UTC): counted in the purchase's own transaction (SupporterPurchasePlan), so a
/// purchase over a limit is never paid.</item>
/// <item>Used from the bag, again only while the account is an active supporter. The recharge adds 60 minutes to the
/// character's auto-battle bank, never past its limit (the item is kept otherwise). An EXP or drop booster starts a
/// timer of active auto-battle minutes: it runs down only while auto-battle time is being used (WildAfkService.Consumed),
/// never while idle, paused, in town, offline or playing by hand, and survives relogs and restarts (server_state).
/// One booster at a time per account; a second is refused until the first ends.</item>
/// <item>The EXP booster adds its percent to the recipient's kill EXP in active Auto Mode; the drop booster rolls the
/// killer's tables once more at its chance and keeps that roll's items, all of them unless Supporter:DropBoosterItems
/// lists some, but never its EXP (RewardService). Neither touches
/// Crystals, which come only from the pass (CrystalEconomy), so free and supporter players earn the same Crystals.</item>
/// </list>
/// A booster already running when status lapses runs out its purchased time (plan §3 E).
/// </summary>
public sealed class SupporterShop
{
    internal sealed record Boost(string Kind, int Percent, long RemainingSeconds, int Item);
    private const long RechargeSeconds = 3600;

    private readonly IOptions<ServerOptions> _options;
    private readonly CharacterService _character;
    private readonly AccessList _access;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly WildAfkService _afk;
    private readonly TimeProvider _clock;
    private readonly ILogger<SupporterShop> _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, Boost?> _boosts = new(StringComparer.Ordinal);

    public SupporterShop(IOptions<ServerOptions> options, CharacterService character, AccessList access, AccountStore accounts,
        RoleStore roles, WildAfkService afk, RewardService rewards, ChatService chat, TimeProvider clock, ILogger<SupporterShop> log)
    {
        _options = options; _character = character; _access = access; _accounts = accounts; _roles = roles; _afk = afk;
        _clock = clock; _log = log;
        character.SpecialItemUse.Add(TryUseAsync);
        afk.Consumed += OnConsumed;
        rewards.SupporterExpPercent = ExpPercent;
        rewards.SupporterDropBoost = DropBoost;
        chat.PlayerCommands["/boost"] = StatusCommandAsync;
    }

    private SupporterOptions Options => _options.Value.Supporter;
    private static string Key(string account) => account.ToLowerInvariant();
    internal static string StateKey(string account) => "supporter-boost/" + Key(account);
    private long Week => NewActivityService.PassPeriod(_clock.GetLocalNow()).Week;
    private static readonly int[] ExpGroup = SupporterShopRules.Products.Where(p => p.Kind == SupporterShopRules.Exp).Select(p => p.Commodity).ToArray();

    // --- Purchase (ShopService.BuyOneAsync) ------------------------------------------------------------------------

    /// <summary>Why this Supporter Merchant purchase is refused (code, reason), or null; null too for other commodities.</summary>
    internal async Task<(int Code, string Why)?> RefusalAsync(GameSession s, int commodity, int count)
    {
        if (SupporterShopRules.ByCommodity(commodity) is not { } p) return null;
        if (!Options.Shop || s.Account == null) return (ErrorCodes.FeatureUnavailable, "supporter shop off");
        if (s.ClientHookVersion < CharacterAppearanceRules.SupporterShopClientVersion) return (ErrorCodes.FeatureUnavailable, "client hooks too old");
        if (!_access.IsSupporter(s.Account))
        {
            await GmCommands.ReplyAsync(s, "The Supporter Merchant serves active Supporters. See solro3.com/supporter.");
            return (ErrorCodes.FeatureUnavailable, "not an active supporter");
        }
        var bought = await _accounts.ReadAsync<Dictionary<int, int>>(new SupporterPurchaseReadPlan(Key(s.Account), Week));
        bool overOwn = bought.GetValueOrDefault(commodity) + count > p.WeeklyLimit;
        bool overGroup = p.Kind == SupporterShopRules.Exp && ExpGroup.Sum(c => bought.GetValueOrDefault(c)) + count > SupporterShopRules.ExpWeeklyLimit;
        if (overOwn || overGroup)
        {
            await GmCommands.ReplyAsync(s, overGroup && !overOwn
                ? $"Weekly limit reached: {SupporterShopRules.ExpWeeklyLimit} EXP boosters a week per account. New week: Monday 05:00 UTC."
                : $"Weekly limit reached: {p.WeeklyLimit} {p.Name} a week per account. New week: Monday 05:00 UTC.");
            return (ErrorCodes.PurchaseLimit, "supporter weekly limit");
        }
        return null;
    }

    /// <summary>The purchase's account-wide counter, written in the purchase's transaction; null for other commodities.</summary>
    internal DbPlan? PurchasePlan(GameSession s, int commodity, int count) =>
        SupporterShopRules.ByCommodity(commodity) is { } p && s.Account != null
            ? new SupporterPurchasePlan(Key(s.Account), Week, commodity, count, p.WeeklyLimit,
                p.Kind == SupporterShopRules.Exp ? ExpGroup : [], SupporterShopRules.ExpWeeklyLimit)
            : null;

    // --- Running boosters ------------------------------------------------------------------------------------------

    internal Boost? Running(string account)
    {
        string key = Key(account);
        lock (_sync) if (_boosts.TryGetValue(key, out var cached)) return cached;
        Boost? loaded = null;
        try { loaded = _roles.ServerState(StateKey(account)) is { Length: > 0 } json ? JsonSerializer.Deserialize<Boost>(json) : null; }
        catch (JsonException e) { _log.LogWarning(e, "Supporter booster state of {Account} unreadable", account); }
        if (loaded is { RemainingSeconds: <= 0 }) loaded = null;
        lock (_sync) return _boosts.TryAdd(key, loaded) ? loaded : _boosts[key];
    }

    private int ExpPercent(GameSession s) => s.Account != null && Running(s.Account) is { Kind: SupporterShopRules.Exp } b
        && WildAfkService.AutoActive(s) ? b.Percent : 0;

    private (int Percent, IReadOnlySet<int>? Items)? DropBoost(GameSession s) =>
        s.Account != null && Running(s.Account) is { Kind: SupporterShopRules.Drop } b && WildAfkService.AutoActive(s)
            ? (b.Percent, Options.DropBoosterItems.Length > 0 ? Options.DropBoosterItems.ToHashSet() : null) : null;

    /// <summary>Auto-battle time was used: the account's booster runs down by the same seconds.</summary>
    internal void OnConsumed(GameSession s, long seconds)
    {
        if (s.Account == null || Running(s.Account) is not { } boost) return;
        long left = Math.Max(0, boost.RemainingSeconds - seconds);
        var next = left > 0 ? boost with { RemainingSeconds = left } : null;
        lock (_sync) _boosts[Key(s.Account)] = next;
        // Saved each whole minute and at the end: a crash loses at most a minute of the player's own booster time.
        if (next == null || left / 60 != boost.RemainingSeconds / 60) _ = SaveAsync(s.Account, next);
        if (next == null) _ = GmCommands.ReplyAsync(s, $"Your {Name(boost)} has ended.");
    }

    private async Task SaveAsync(string account, Boost? boost)
    {
        try { await _roles.SetServerStateAsync(StateKey(account), boost == null ? "" : JsonSerializer.Serialize(boost)); }
        catch (Exception e) { _log.LogWarning(e, "Supporter booster state of {Account} not saved", account); }
    }

    private static string Name(Boost b) => SupporterShopRules.ByItem(b.Item)?.Name ?? "booster";
    private static string Minutes(long seconds) => ((seconds + 59) / 60).ToString(CultureInfo.InvariantCulture);

    // --- Use (31323) -----------------------------------------------------------------------------------------------

    private async Task<int?> TryUseAsync(GameSession s, ItemUseReq req, InvItem item, JsonElement row, int count)
    {
        if (SupporterShopRules.ByItem(item.Entry) is not { } p || s.Role is not { } role || s.Account is not { } account) return null;
        if (count != 1 || req.ItemParam.Count != 0) return ErrorCodes.Parameter;
        if (!_access.IsSupporter(account))
        {
            await GmCommands.ReplyAsync(s, $"{p.Name} needs active Supporter status. It stays in your bag.");
            return ErrorCodes.FeatureUnavailable;
        }
        var use = new ItemActionUse(s, req, item, row, 1, []);
        if (p.Kind == SupporterShopRules.Recharge)
        {
            if (_afk.PlanRecharge(s, RechargeSeconds) is not { } plan)
            {
                await GmCommands.ReplyAsync(s, "Your auto-battle bank is too full for 60 more minutes. Use some time first; the recharge stays in your bag.");
                return ErrorCodes.FeatureUnavailable;
            }
            if (!await _character.CommitItemActionAsync(use, tx => tx.RoleBlob(role.Id, "wild_afk", plan.Bytes), plan.Apply,
                new { Supporter = p.Kind, Seconds = RechargeSeconds }, Economy.CharacterFamilyEconomy.SupporterRechargeKind))
                return ErrorCodes.ItemsShort;
            await _afk.PublishAsync(s);
            await GmCommands.ReplyAsync(s, "+60 minutes of auto-battle time.");
            return 0;
        }
        if (Running(account) is { } running)
        {
            await GmCommands.ReplyAsync(s, $"Your {Name(running)} still has {Minutes(running.RemainingSeconds)} minute(s) of auto-battle left. One booster at a time.");
            return ErrorCodes.FeatureUnavailable;
        }
        // As BoostService: the item is spent first, then the account's booster is saved (server_state).
        var next = new Boost(p.Kind, p.Percent, p.Minutes * 60L, p.Item);
        if (!await _character.CommitItemActionAsync(use, null, null, new { Supporter = p.Kind, p.Percent, p.Minutes }))
            return ErrorCodes.ItemsShort;
        lock (_sync) _boosts[Key(account)] = next;
        await _roles.SetServerStateAsync(StateKey(account), JsonSerializer.Serialize(next));
        _log.LogInformation("{Account} ({Role}) starts {Booster}: {Percent}% for {Minutes} active minutes", account, role.Id, p.Name, p.Percent, p.Minutes);
        await GmCommands.ReplyAsync(s, $"{p.Name} started: {p.Minutes} minutes of active auto-battle. It only counts down while auto-battle time is used.");
        return 0;
    }

    /// <summary>"/boost": the running booster and this week's purchases.</summary>
    private async Task StatusCommandAsync(GameSession s, string args)
    {
        if (s.Account == null) return;
        var bought = await _accounts.ReadAsync<Dictionary<int, int>>(new SupporterPurchaseReadPlan(Key(s.Account), Week));
        string running = Running(s.Account) is { } b
            ? $"{Name(b)}: {Minutes(b.RemainingSeconds)} minute(s) of active auto-battle left{(WildAfkService.AutoActive(s) ? "" : " (paused: not in active Auto Mode)")}"
            : "No booster running";
        string week = string.Join(", ", SupporterShopRules.Products.Select(p => $"{p.Name} {bought.GetValueOrDefault(p.Commodity)}/{p.WeeklyLimit}"));
        await GmCommands.ReplyAsync(s, $"{running}. This week (all your characters): {week}.");
    }
}
