using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Sol RO3 house rule, not original behaviour (docs/guide/boosts.md): ServerOptions.BoostItems start a personal boost
/// when used (RO3 has no such items; Sol RO3 gives them only through events and GM gifts):
/// <list type="bullet">
/// <item>EXP: the role's kill EXP takes the higher of the running event rate and 100 + Percent (RewardService), each
/// party member by their own boost.</item>
/// <item>Item drop: the killer's loot rolls once more at Percent chance, unless an event already rolls more.</item>
/// </list>
/// One boost of each kind at a time: a second is refused while the first runs. The end times are kept in server_state
/// (<see cref="StateKey"/>), so a relog keeps them; the item is spent first, then the boost is saved.
/// </summary>
public sealed partial class BoostService
{
    internal const string Exp = "exp", Drop = "drop";
    private const int MaxMinutes = 240;

    private readonly ServerOptions _options;
    private readonly CharacterService _character;
    private readonly BuffService _buffs;
    private readonly RoleStore _roles;
    private readonly GameData _data;
    private readonly TimeProvider _clock;
    private readonly ILogger<BoostService> _log;
    private readonly Dictionary<long, State> _states = [];

    /// <summary>A role's running boosts: percent and end (Unix ms) of each kind; 0 when none.</summary>
    internal sealed record State(int ExpPercent, long ExpUntil, int DropPercent, long DropUntil);

    public BoostService(IOptions<ServerOptions> options, CharacterService character, BuffService buffs, RoleStore roles,
        SceneService scene, GameData data, TimeProvider clock, ILogger<BoostService> log)
    {
        _options = options.Value; _character = character; _buffs = buffs; _roles = roles; _data = data; _clock = clock; _log = log;
        if (_options.BoostItems.Length == 0) return;
        character.SpecialItemUse.Add(TryUseAsync);
        scene.Loaded.Add(ShowAsync);
    }

    internal static string StateKey(long role) => "boost/" + role.ToString(CultureInfo.InvariantCulture);

    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The role's running EXP boost in percent (0 when none).</summary>
    internal int ExpPercent(GameSession p) => p.Role is { } r && Of(r.Id) is { } s && s.ExpUntil > Now ? s.ExpPercent : 0;

    /// <summary>The role's running item-drop boost: the chance in percent of an extra loot roll (0 when none).</summary>
    internal int DropPercent(GameSession p) => p.Role is { } r && Of(r.Id) is { } s && s.DropUntil > Now ? s.DropPercent : 0;

    /// <summary>A kill's EXP rate in percent: the event's, or 100 plus the role's boost when that is higher.</summary>
    internal static int ExpRate(int eventExp, int boostPercent) => Math.Max(eventExp, 100 + boostPercent);

    /// <summary>A kill's loot rolls: the event's, or one more at <paramref name="boostPercent"/> chance when that is more
    /// (<paramref name="roll"/> is 0-99).</summary>
    internal static int LootRolls(int eventRolls, int boostPercent, int roll) => Math.Max(eventRolls, 1 + (roll < boostPercent ? 1 : 0));

    private State? Of(long role)
    {
        if (_states.TryGetValue(role, out var cached)) return cached;
        State? state = null;
        try
        {
            state = _roles.ServerState(StateKey(role)) is { Length: > 0 } json ? JsonSerializer.Deserialize<State>(json) : null;
        }
        catch (JsonException e)
        {
            LogBadState(_log, e, role);
        }
        _states[role] = state ?? new State(0, 0, 0, 0);
        return _states[role];
    }

    private BoostItem? Boost(int item) => _options.BoostItems.FirstOrDefault(b => b.Item == item && b.Kind is Exp or Drop
        && b.Percent is > 0 and <= 100 && b.Minutes is > 0 and <= MaxMinutes);

    /// <summary>31323 of a boost item: refused while a boost of its kind runs; otherwise spends one, saves the boost and
    /// shows it.</summary>
    private async Task<int?> TryUseAsync(GameSession session, ItemUseReq req, InvItem item, JsonElement row, int count)
    {
        if (Boost(item.Entry) is not { } boost || session.Role is not { } role) return null;
        if (count != 1) return ErrorCodes.Parameter;
        var current = Of(role.Id) ?? new State(0, 0, 0, 0);
        long running = boost.Kind == Exp ? current.ExpUntil : current.DropUntil;
        if (running > Now)
        {
            await GmCommands.ReplyAsync(session, $"Your {Word(boost.Kind)} boost still runs for {Minutes(running - Now)} more minute(s).");
            return ErrorCodes.FeatureUnavailable;
        }
        long until = Now + boost.Minutes * 60_000L;
        var next = boost.Kind == Exp ? current with { ExpPercent = boost.Percent, ExpUntil = until }
            : current with { DropPercent = boost.Percent, DropUntil = until };
        var use = new ItemActionUse(session, req, item, row, 1, []);
        if (!await _character.CommitItemActionAsync(use, null, null, new { Boost = boost.Kind, boost.Percent, boost.Minutes }))
            return ErrorCodes.ItemsShort;
        await _roles.SetServerStateAsync(StateKey(role.Id), JsonSerializer.Serialize(next));
        _states[role.Id] = next;
        LogBoost(_log, role.Name, role.Id, boost.Kind, boost.Percent, boost.Minutes, item.Entry);
        await ShowBuffAsync(session, boost, boost.Minutes * 60_000);
        await GmCommands.ReplyAsync(session, $"{Word(boost.Kind)} +{boost.Percent}% for {boost.Minutes} minutes!");
        return 0;
    }

    /// <summary>After a relog or a map change: the running boosts' buffs again, with the time left.</summary>
    private async Task ShowAsync(GameSession session)
    {
        if (session.Role is not { } role || Of(role.Id) is not { } s) return;
        foreach (var (kind, until) in new[] { (Exp, s.ExpUntil), (Drop, s.DropUntil) })
            if (until > Now && _options.BoostItems.FirstOrDefault(b => b.Kind == kind && b.Buff > 0) is { } boost
                && session.Buffs.All(b => b.ConfigId != boost.Buff))
                await ShowBuffAsync(session, boost, (int)Math.Min(int.MaxValue, until - Now));
    }

    /// <summary>The boost's buff row, for its icon and timer only: a row that changes attributes is never shown.</summary>
    private async Task ShowBuffAsync(GameSession session, BoostItem boost, int durationMs)
    {
        if (boost.Buff <= 0) return;
        if (_data.Row("BuffConfig", boost.Buff) is not { } row || GameData.Pairs(row, "_kChangeAttri").Any())
        {
            LogBadBuff(_log, boost.Buff, boost.Item);
            return;
        }
        await _buffs.AddAsync(session, boost.Buff, durationMs, session.Role!.Id);
    }

    private static string Word(string kind) => kind == Exp ? "EXP" : "Item drop";

    private static long Minutes(long ms) => (ms + 59_999) / 60_000;

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} ({Role}) starts a {Kind} boost +{Percent}% for {Minutes} min (item {Item})")]
    private static partial void LogBoost(ILogger logger, string name, long role, string kind, int percent, int minutes, int item);

    [LoggerMessage(Level = LogLevel.Warning, Message = "server_state boost of role {Role} is not valid JSON; it has no boost")]
    private static partial void LogBadState(ILogger logger, Exception e, long role);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boost buff {Buff} (item {Item}) is missing or changes attributes; no icon is shown")]
    private static partial void LogBadBuff(ILogger logger, int buff, int item);
}
