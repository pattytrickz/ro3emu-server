using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Kill rewards: the monster's drop chains are rolled for the killer
/// (NPCConfig._iDrop / _iNormalDrop → MultiverseGroupConfig → NPCDropConfig[env 0 reduced / env 1 Bonus Time, inferred]
/// / additive NpcNormalDropConfig → DropPoolConfig slots → weighted DropGroupConfig rows). Item 100 rows are Base EXP,
/// scaled by LvExpCorrectionConfig; Job EXP equals Base EXP (inferred). Currencies go to the currency map, everything
/// else to the bag; then one 31322 drop notify makes
/// the client animate the loot from the corpse. A field kill's EXP is split evenly among the killer's party members
/// on the killer's map (owner decision, issue #3); the loot stays with the killer.
/// </summary>
public sealed class RewardService
{
    private const int BaseExpItem = 100, JobExpItem = 101;
    private const int RewardDrop = 8; // ManifestType Drop

    private readonly CharacterService _character;
    private readonly TeamService _teams;
    private readonly GameData _data;
    private readonly ILogger<RewardService> _log;
    private readonly Random _random = new();
    private Dictionary<int, long>? _expCorrection;
    private readonly DropAttenuation _attenuation;
    private readonly int _killExpPercent;
    private readonly double _cardDropRolls;
    private readonly EventService? _events;
    private readonly BoostService? _boosts;

    /// <summary>Field AFK selects the enhanced _iDrop table only for qualified ordinary field kills.
    /// Other kills use environment 0, preserving ordinary rewards (localization 10960000077; mapping inferred).</summary>
    public Func<GameSession, MonsterUnit, Task<bool>>? QualifiesForBonus { get; set; }
    /// <summary>Actual committed kill drops, after EXP correction; excludes GM, quest and other item grants.</summary>
    public List<Func<GameSession, MonsterUnit, IReadOnlyList<(int Entry, long Count)>, Task>> KillDropsGranted { get; } = new();
    internal Action<GameSession, MonsterUnit, RewardSettlement, IReadOnlyList<(int Entry, int Count)>, long, long>? StageAfkKill { get; set; }

    /// <summary>A recipient's Supporter EXP booster in percent (0 when none or not in active Auto Mode; Game.SupporterShop).</summary>
    internal Func<GameSession, int>? SupporterExpPercent { get; set; }

    /// <summary>The killer's Supporter drop booster: its extra-roll chance in percent and the allowlisted items it may
    /// add (null when none or not in active Auto Mode; Game.SupporterShop).</summary>
    internal Func<GameSession, (int Percent, IReadOnlySet<int> Items)?>? SupporterDropBoost { get; set; }
    /// <summary>Monster Hunt: the area drop rolled with an ordinary field kill (MonsterHuntService).</summary>
    internal List<Func<GameSession, MonsterUnit, IEnumerable<(int Entry, int Count)>>> ExtraKillDrops { get; } = [];
    /// <summary>Monster Hunt: its kill counters join the kill's own transaction, after the AFK child (MonsterHuntService).</summary>
    internal Action<GameSession, MonsterUnit, RewardSettlement>? StageHuntKill { get; set; }

    public RewardService(CombatService combat, CharacterService character, TeamService teams, GameData data, ILogger<RewardService> log,
        Microsoft.Extensions.Options.IOptions<ServerOptions>? options = null, EventService? events = null, BoostService? boosts = null)
    {
        _events = events;
        _boosts = boosts;
        _noShare = options?.Value.PartyShareExcludedAccounts ?? [];
        _realPartyPercent = Math.Clamp(options?.Value.RealPartyExpPercent ?? 0, 0, 100);
        _realPartyMinPlayers = options?.Value.RealPartyMinPlayers ?? 3;
        _handsOnExp = Math.Clamp(options?.Value.HandsOnExpPercent ?? 0, 0, 100);
        _handsOnLoot = Math.Clamp(options?.Value.HandsOnLootPercent ?? 0, 0, 100);
        _handsOnMs = Math.Max(0, options?.Value.HandsOnSeconds ?? 10) * 1000L;
        _killExpPercent = Math.Max(1, options?.Value.KillExpPercent ?? 100);
        _cardDropRolls = Math.Clamp(options?.Value.CardDropRolls ?? 1, 1, 10);
        this._character = character;
        this._teams = teams;
        this._data = data;
        this._log = log;
        _attenuation = new DropAttenuation(data);
        combat.BeforeKilled.Add(PrepareKilledAsync);
        combat.Killed.Add(KilledAsync);
    }

    private sealed class KillOutcome
    {
        internal required Role Role;
        internal required List<(int Entry, int Count)> Items;
        internal long BaseExp;
        internal long JobExp;
        internal bool Published;
        /// <summary>The other party members sharing this kill's EXP and their shares, chosen when it is admitted.</summary>
        internal List<(GameSession Session, Role Role, long BaseExp, long JobExp)> Party = [];
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<MonsterUnit, KillOutcome> _outcomes = new();
    internal Func<GameSession, MonsterUnit, (List<(int Entry, int Count)> Items, long BaseExp, long JobExp)>? ResolveKillForTest { get; set; }
    /// <summary>Replaces the drop roll only: its item 100 rows still pass the level gap, party bonus and split.</summary>
    internal Func<GameSession, MonsterUnit, List<(int Entry, int Count)>>? RollKillForTest { get; set; }

    /// <summary>Resolve and admit once before any lethal hit/death packet. A rejected pre-admission hit is
    /// not a successful kill. An accepted write retains its role owner through detach and durable completion.</summary>
    internal Task PrepareKilledAsync(GameSession player, MonsterUnit monster)
    {
        if (monster.RewardSettlement?.Committed == true) return Task.CompletedTask;
        if (monster.RewardCommit != null) return monster.RewardCommit;
        if (player.Role == null) throw new OperationCanceledException("Kill has no recipient.");
        if (monster.RewardRecipient != 0 && monster.RewardRecipient != player.Role.Id)
            throw new InvalidOperationException("Staged kill belongs to another recipient.");
        monster.RewardRecipient = player.Role.Id;
        monster.RewardSource ??= new RewardSourceId(_character.RewardRealmId, RewardSourceId.ProcessRun, monster.SpawnGeneration);
        monster.RewardSettlement ??= new RewardSettlement(monster.RewardSource.Key, "monster-kill", autoDecompose: true)
        { DeferPresentation = true };
        return monster.RewardCommit = CommitKillAsync(player, monster);
    }

    private async Task CommitKillAsync(GameSession player, MonsterUnit monster)
    {
        var role = player.Role!;
        var afk = player.WildAfk;
        bool SameRole() => ReferenceEquals(player.Role, role) && ReferenceEquals(player.WildAfk, afk)
            && player.Progress != null && player.Attachment?.Cancelled != true;
        var settlement = monster.RewardSettlement!;
        try
        {
            if (await _character.RewardSourceCommittedAsync(settlement.SourceKey))
            { settlement.Committed = true; return; }
            // Bonus eligibility may wait on the AFK owner; no successful lethal output precedes admission.
            bool bonus = QualifiesForBonus != null && await QualifiesForBonus(player, monster);
            var taskParticipants = _character.GetKillTaskParticipantKeys?.Invoke(player, monster) ?? [];
            using var held = await CharacterService.Lock(player, taskParticipants);
            if (!SameRole()) throw new OperationCanceledException("Kill recipient detached before reward admission.");
            if (_outcomes.TryGetValue(monster, out var stale) && stale.Role.Id != role.Id) _outcomes.Remove(monster);
            if (!_outcomes.TryGetValue(monster, out var outcome))
            {
                var (items, exp) = ResolveKill(player, monster, bonus);
                // Field kills: the bonused EXP is split evenly among the members online on the killer's map, each
                // share at its member's own level (KillExp).
                var members = player.InstanceId != 0 ? [] : _teams.SameMapMembers(player)
                    .Where(s => s != player && s.Role is not null && s.Progress != null && s.Attachment?.Cancelled != true
                        && !ExcludedFromShare(s.Account)).ToList();
                int sharing = members.Count + 1;
                var (baseExp, jobExp) = exp(player, sharing);
                var party = baseExp + jobExp <= 0 ? [] : members.Select(s =>
                {
                    var (memberBase, memberJob) = exp(s, sharing);
                    return (s, s.Role!, memberBase, memberJob);
                }).ToList();
                outcome = new KillOutcome { Role = role, Items = items, BaseExp = baseExp, JobExp = jobExp, Party = party };
                _outcomes.Add(monster, outcome);
            }
            _character.StageKillTaskObligations?.Invoke(player, monster, settlement);
            // A retry re-stages: the later stage is undone first, so the AFK reset never restores a stale wrapper.
            settlement.ResetHuntStage?.Invoke();
            StageAfkKill?.Invoke(player, monster, settlement, outcome.Items, outcome.BaseExp, outcome.JobExp);
            StageHuntKill?.Invoke(player, monster, settlement);
            settlement.LibraryMonster = monster.Template.NpcId;
            await _character.ExchangeLockedAsync(player, [], outcome.Items, popup: false, sync: outcome.Items.Count > 0,
                baseExp: outcome.BaseExp, jobExp: outcome.JobExp, rewardSettlement: settlement,
                persist: settlement.Enlist, committed: settlement.Applied);
        }
        catch
        {
            // A failed apply/notice cannot resurrect a committed source. The owned writer quarantines
            // ambiguous/failed live state; restart and login reload the durable bag and progression.
            try
            {
                if (await _character.RewardSourceCommittedAsync(settlement.SourceKey))
                {
                    settlement.Committed = true;
                    // Failed Apply cannot safely run room/quest callbacks on an old owner snapshot.
                    // Abort the attachment; process recovery reloads the committed outcome and durable obligations.
                    player.CancelPendingCharacterIntents();
                    player.AbortForRecovery();
                }
                else monster.RewardCommit = null;
            }
            catch
            {
                settlement.OutcomeUnknown = true;
                player.CancelPendingCharacterIntents();
                player.AbortForRecovery();
            }
            throw;
        }
    }

    private (List<(int Entry, int Count)> Items, KillShare Exp) ResolveKill(GameSession player, MonsterUnit monster, bool bonus)
    {
        if (ResolveKillForTest?.Invoke(player, monster) is (var testItems, var testBase, var testJob))
            return (testItems, (_, members) => (Share(testBase, members), Share(testJob, members)));
        // Team-boss personal awards have their own durable dungeon obligation; ordinary loot stays empty.
        if (player.DungeonAttempt is { Plan.Multi: not null } dungeon && monster.InstanceId == dungeon.Room.Id)
            return ([], NoExp);
        var (eventExp, eventDrops, eventCards) = _events?.Current ?? (100, 1, 1);
        bool handsOn = HandsOn(player);
        // BoostService: the killer's item-drop boost adds to the loot rolls, as an event's do (the higher applies).
        if (_boosts?.DropPercent(player) is int drop and > 0) eventDrops = BoostService.LootRolls(eventDrops, drop, _random.Next(100));
        if ((RollKillForTest?.Invoke(player, monster) ?? RollDrops(player, monster, bonus,
                eventDrops + (handsOn && _random.Next(100) < _handsOnLoot ? 1 : 0), eventCards)) is not { } drops)
            return ([], NoExp);
        foreach (var extra in ExtraKillDrops) drops.AddRange(extra(player, monster));
        // Supporter drop booster (plan §2.2): at its chance the tables roll once more and only allowlisted common
        // materials are kept, so those are 1.5 times as likely; everything else (cards, EXP, gear) keeps the data's odds.
        if (SupporterDropBoost?.Invoke(player) is { } boost && boost.Items.Count > 0 && _random.Next(100) < boost.Percent
            && RollDrops(player, monster, bonus, 1, 1) is { } again)
            drops.AddRange(again.Where(d => boost.Items.Contains(d.Entry)));
        long rolled = drops.Where(d => d.Entry == BaseExpItem).Sum(d => (long)d.Count);
        var items = drops.Where(d => d.Entry is not (BaseExpItem or JobExpItem) && _data.Row("ItemConfig", d.Entry) != null).ToList();
        int partyBonus = _teams.ExpBonus(player);
        int realParty = RealPartyPercent(player), handsOnPercent = handsOn ? 100 + _handsOnExp : 100;
        // Each recipient's own EXP boost (BoostService), never the killer's for the party; the same divisions as before.
        int Percent(GameSession recipient) => _killExpPercent * BoostService.ExpRate(eventExp, _boosts?.ExpPercent(recipient) ?? 0)
            / 100 * realParty / 100 * handsOnPercent / 100 * (100 + (SupporterExpPercent?.Invoke(recipient) ?? 0)) / 100;
        return (items, (recipient, members) => KillExp(recipient, monster, rolled, partyBonus, Percent(recipient), members));
    }

    /// <summary>A recipient's share of a kill, split among <c>members</c> (the killer and its sharing party members).</summary>
    private delegate (long BaseExp, long JobExp) KillShare(GameSession recipient, int members);
    private static readonly KillShare NoExp = (_, _) => (0, 0);

    /// <summary>The monster's drop chains rolled for the killer; null when NPCConfig has no row for it.</summary>
    private List<(int Entry, int Count)>? RollDrops(GameSession player, MonsterUnit monster, bool bonus, int lootRolls,
        int eventCards)
    {
        if (_data.Row("NPCConfig", monster.Template.NpcId) is not { } npc) return null;
        var drops = new List<(int Entry, int Count)>();
        void Roll(List<(int Entry, int Count)> into)
        {
            // (inferred) NPCDropConfig environment 0 is reduced and 1 is Bonus Time: the former carries
            // small EXP at player levels 1-99, the latter larger EXP. Fatigue environment 2 has no proven trigger.
            // _iNormalDrop remains additive under its own level/map conditions; no numeric multiplier is invented.
            if (Resolve(GameData.Int(npc, "_iDrop")) is var dropId and > 0 && _data.Row("NPCDropConfig", dropId) is { } nd
                && nd.ValueKind == JsonValueKind.Object && nd.TryGetProperty(bonus ? "1" : "0", out var env))
                RollPools(env, player, into, monster.Template.Level);
            if (npc.TryGetProperty("_iNormalDrop", out var normals) && normals.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in normals.EnumerateArray())
                {
                    if (GameData.Number(n) is not { } nid || Resolve((long)nid) is not (var id and > 0) || _data.Row("NpcNormalDropConfig", id) is not { } row) continue;
                    if (row.TryGetProperty("_iMapId", out var maps) && maps.ValueKind == JsonValueKind.Array && maps.GetArrayLength() > 0
                        && !maps.EnumerateArray().Any(m => GameData.Number(m) == player.SceneEntry))
                        continue;
                    RollPools(row, player, into, monster.Template.Level);
                }
            }
        }
        RollWithExtraCards(drops, Roll, _character.IsCard, WholeRolls(Math.Min(10, _cardDropRolls * eventCards), _random), lootRolls);
        return drops;
    }

    /// <summary>
    /// One recipient's share of a kill's rolled Base EXP: LvExpCorrectionConfig by the recipient's own level against the
    /// monster's, the kill's party bonus and house percent, the even split among <paramref name="members"/> (owner
    /// decision, issue #3), and the recipient's BaseExpAddConfig catch-up (Base EXP only, CharacterService.LevelRules).
    /// Each member's own level, not the killer's, so a share does not depend on who lands the kill (issue #137).
    /// </summary>
    private (long BaseExp, long JobExp) KillExp(GameSession recipient, MonsterUnit monster, long rolled, int partyBonus,
        int percent, int members)
    {
        if (rolled <= 0) return (0, 0);
        int level = recipient.Progress!.Level;
        long exp = rolled * Correction((int)Math.Clamp((long)level - monster.Template.Level, -200, 200)) / 10000;
        exp = HouseExp(Math.Max(1, exp * partyBonus / 10000), percent);
        return (Share(_character.WithPursueBonus(level, exp), members), Share(exp, members));
    }

    /// <summary>Sol RO3 house rule (ServerOptions.KillExpPercent): a kill's EXP in percent. Job EXP is the same amount
    /// (ResolveKill), so it scales both; a kill that gave EXP still gives at least 1.</summary>
    internal static long HouseExp(long exp, int percent) => exp > 0 ? Math.Max(1, exp * percent / 100) : exp;

    /// <summary>A card multiplier as a whole number of rolls for one kill: 1.5 is one roll, plus a second half of the time,
    /// so a card is 1.5 times as likely on average.</summary>
    internal static int WholeRolls(double rolls, Random random) =>
        (int)rolls + (random.NextDouble() < rolls - Math.Floor(rolls) ? 1 : 0);

    /// <summary>Sol RO3 house rules (ServerOptions.CardDropRolls, EventRates): the drop tables roll once in full, then
    /// <paramref name="dropRolls"/> - 1 more times keeping every item but no EXP (event loot), then up to
    /// <paramref name="cardRolls"/> rolls in all keeping only cards, so a card is about max(cardRolls, dropRolls) times as
    /// likely and the EXP keeps the data's odds.</summary>
    internal static void RollWithExtraCards(List<(int Entry, int Count)> drops, Action<List<(int Entry, int Count)>> roll,
        Func<int, bool> isCard, int cardRolls, int dropRolls = 1)
    {
        roll(drops);
        for (int extra = 1; extra < Math.Max(cardRolls, dropRolls); extra++)
        {
            var again = new List<(int Entry, int Count)>();
            roll(again);
            drops.AddRange(again.Where(d => extra < dropRolls ? d.Entry is not (BaseExpItem or JobExpItem) : isCard(d.Entry)));
        }
    }

    private async Task KilledAsync(GameSession player, MonsterUnit monster)
    {
        await PrepareKilledAsync(player, monster); // also covers existing service/test callbacks
        if (!_outcomes.TryGetValue(monster, out var outcome) || outcome.Published) return;
        outcome.Published = true;
        await PayPartyAsync(monster, outcome);
        var role = outcome.Role;
        if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
        using (await CharacterService.Lock(player))
        {
            if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
            await monster.RewardSettlement!.PresentAsync();
            if (outcome.Items.Count > 0)
            {
                var notify = new GetRewardNotify
                {
                    Position = monster.Pos, TargetEntry = monster.Template.NpcId,
                    TargetUid = role.Id, RewardType = { RewardDrop }
                };
                foreach (var (entry, count) in outcome.Items)
                    notify.Rewards.Add(new ItemBaseInfo
                    {
                        ItemEntry = entry, Stack = count,
                        ItemInfos = { [10001] = new ItemAttrValue { LongVal = 0 } }
                    });
                await player.SendGameAsync(CharacterService.GetRewardNotify, notify);
            }
        }
        var granted = outcome.Items.Select(i => (i.Entry, Count: (long)i.Count)).ToList();
        if (outcome.BaseExp > 0) granted.Add((BaseExpItem, outcome.BaseExp));
        if (outcome.JobExp > 0) granted.Add((JobExpItem, outcome.JobExp));
        foreach (var handler in KillDropsGranted)
        {
            if (!ReferenceEquals(player.Role, role) || player.Attachment?.Cancelled == true) return;
            await handler(player, monster, granted);
        }
        _log.LogInformation("{Name} durable loot from {Npc}: exp {Exp}, source {Source}",
            role.Name, monster.Template.NpcId, outcome.BaseExp, monster.RewardSource!.Key);
    }

    private static long Share(long exp, int members) => exp > 0 ? Math.Max(1, exp / members) : 0;

    private readonly string[] _noShare;
    private readonly int _realPartyPercent, _realPartyMinPlayers, _handsOnExp, _handsOnLoot;
    private readonly long _handsOnMs;

    /// <summary>Sol RO3 house rule (ServerOptions.HandsOnExpPercent): the killer plays hands-on and is not an AI player.</summary>
    private bool HandsOn(GameSession killer) =>
        (_handsOnExp > 0 || _handsOnLoot > 0) && !ExcludedFromShare(killer.Account) && killer.HandsOn(_handsOnMs);

    /// <summary>Sol RO3 house rule (ServerOptions.RealPartyExpPercent): 100 plus the bonus when the killer's party has at
    /// least RealPartyMinPlayers real players (not PartyShareExcludedAccounts) on the killer's map, the killer included.</summary>
    private int RealPartyPercent(GameSession killer) => RealPartyPercent(_realPartyPercent, _realPartyMinPlayers,
        _teams.SameMapMembers(killer).Count(s => !ExcludedFromShare(s.Account)));

    internal static int RealPartyPercent(int bonus, int minPlayers, int realPlayers) =>
        bonus > 0 && realPlayers >= Math.Max(2, minPlayers) ? 100 + bonus : 100;

    /// <summary>ServerOptions.PartyShareExcludedAccounts (Sol RO3 house rule): exact names or "prefix*". Such members
    /// (the AI players) take no share of a party kill, so a player's kill is split among the real players only.</summary>
    private bool ExcludedFromShare(string? account) =>
        account != null && _noShare.Any(e => e.EndsWith('*')
            ? e.Length > 1 && account.StartsWith(e[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(e, account, StringComparison.OrdinalIgnoreCase));

    /// <summary>Each member's share is its own monster-kill source (the kill's source + the role), committed after the
    /// killer's. Like the kill itself it is paid at most once: a replay finds the source claimed.</summary>
    private async Task PayPartyAsync(MonsterUnit monster, KillOutcome outcome)
    {
        foreach (var (member, role, baseExp, jobExp) in outcome.Party)
        {
            var settlement = new RewardSettlement($"{monster.RewardSource!.Key}/party/{role.Id}", "monster-kill");
            try
            {
                using (await CharacterService.Lock(member))
                {
                    if (!ReferenceEquals(member.Role, role) || member.Progress == null || member.Attachment?.Cancelled == true
                        || await _character.RewardSourceCommittedAsync(settlement.SourceKey))
                        continue;
                    await _character.ExchangeLockedAsync(member, [], [], popup: false, sync: false,
                        baseExp: baseExp, jobExp: jobExp, rewardSettlement: settlement);
                }
                _log.LogInformation("{Name} party share from {Npc}: exp {Exp}, source {Source}",
                    role.Name, monster.Template.NpcId, baseExp, settlement.SourceKey);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Party EXP share for {Role} failed, source {Source}", role.Id, settlement.SourceKey);
            }
        }
    }

    /// <summary>MultiverseGroupConfig id → the playbook's value, else _iValue_default (playbook 0 here).</summary>
    private long Resolve(long multiverseId) =>
        multiverseId > 0 && _data.Row("MultiverseGroupConfig", multiverseId) is { } mg ? GameData.Int(mg, "_iValue_default") : 0;

    /// <summary>
    /// Every _iDropPoolN of a row; each pool slot [group, chance per million, rolls, picks] is rolled "rolls" times
    /// independently (MVP first-strike pool 21001 = [2020, 100%, 30, 1] = 30 x 1000 Minted Coin).
    /// </summary>
    /// <summary>A kill's pools: <paramref name="monsterLevel"/> is the slain monster's level (DropAttenuationConfig).</summary>
    private void RollPools(JsonElement row, GameSession player, List<(int, int)> drops, int monsterLevel)
    {
        foreach (var p in row.EnumerateObject().Where(p => p.Name.StartsWith("_iDropPool", StringComparison.Ordinal)))
            if (GameData.Number(p.Value) is { } poolId) RollPool((long)poolId, player.Progress!, drops, null, quest => QuestActive(player, quest), monsterLevel);
    }

    private void RollPool(long poolId, GameSession player, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null)
        => RollPool(poolId, player.Progress!, drops, bound, quest => QuestActive(player, quest));

    /// <summary>
    /// DropGroupConfig._iQuestLimit: the row drops only while that task is running (or done, not yet delivered).
    /// Story drops use it: group 144 is the Card: Poring that "The Fate of an Egg Thief" (10102100) finds in its
    /// instance ("Look, you picked up a Card: Poring!"), whose next step is the card-insert guide; it never dropped.
    /// </summary>
    private static bool QuestActive(GameSession player, long quest) =>
        player.Tasks?.Infos.TryGetValue(quest, out var info) == true && info.Status is TaskState.Running or TaskState.Finish;

    private void RollPool(long poolId, RoleProgress progress, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null,
        Func<long, bool>? questActive = null, int monsterLevel = DropAttenuation.NoMonster)
    {
        if (_data.Row("DropPoolConfig", poolId) is not { } pool) return;
        for (int slot = 1; slot <= 16; slot++)
        {
            if (!pool.TryGetProperty($"_iDropGroup_{slot}", out var g) || g.ValueKind != JsonValueKind.Array || g.GetArrayLength() < 2) continue;
            long group = (long)(GameData.Number(g[0]) ?? 0);
            double chance = GameData.Number(g[1]) ?? 0;
            int rolls = g.GetArrayLength() >= 3 ? Math.Clamp((int)(GameData.Number(g[2]) ?? 1), 1, 1000) : 1;
            int picks = g.GetArrayLength() >= 4 ? Math.Max(1, (int)(GameData.Number(g[3]) ?? 1)) : 1;
            for (int r = 0; r < rolls; r++)
            {
                if (_random.NextDouble() * 1_000_000 >= chance) continue;
                for (int i = 0; i < picks; i++) PickFromGroup(group, progress, drops, bound, questActive, monsterLevel);
            }
        }
    }

    /// <summary>
    /// An award id (MultiverseGroupConfig → AwardConfig[0]._iDropPool → DropPoolConfig), rolled for this player;
    /// same item ids merged. Used for MVP rewards.
    /// </summary>
    public List<(int Entry, int Count)> RollAward(long multiverseId, GameSession player)
    {
        var drops = new List<(int, int)>();
        if (Resolve(multiverseId) is var awardId and > 0 && _data.Row("AwardConfig", awardId) is { ValueKind: JsonValueKind.Array } rows
            && rows.GetArrayLength() > 0 && GameData.Int(rows[0], "_iDropPool") is var pool and > 0)
            RollPool(pool, player, drops);
        return drops.GroupBy(d => d.Item1).Select(g => (g.Key, g.Sum(d => d.Item2))).ToList();
    }

    /// <summary>One DropPoolConfig draw for this player, each chosen DropGroupConfig._iBind retained, same goods merged.</summary>
    public List<(int Entry, int Count, int Bind)> RollPoolBound(long poolId, RoleProgress progress)
    {
        var bound = new List<(int Entry, int Count, int Bind)>();
        RollPool(poolId, progress, new List<(int, int)>(), bound);
        return bound.GroupBy(x => (x.Entry, x.Bind)).Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)).ToList();
    }

    /// <summary>Stage-box pool draw with each chosen DropGroupConfig._iBind retained (draw rules inferred).</summary>
    public List<(int Entry, int Count, int Bind)> RollAwardBound(long awardId, GameSession player)
        => RollAwardBound(awardId, player.Progress!);

    /// <summary>Roll the same configured award for a committed offline dungeon participant.</summary>
    public List<(int Entry, int Count, int Bind)> RollAwardBound(long awardId, RoleProgress progress)
    {
        var bound = new List<(int Entry, int Count, int Bind)>();
        if (_data.Row("AwardConfig", awardId) is { } raw)
        {
            var row = raw.ValueKind == JsonValueKind.Array && raw.GetArrayLength() > 0 ? raw[0] : raw;
            if (row.ValueKind == JsonValueKind.Object && GameData.Int(row, "_iDropPool") is var pool and > 0)
                RollPool(pool, progress, new List<(int, int)>(), bound);
        }
        return bound.GroupBy(x => (x.Entry, x.Bind)).Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)).ToList();
    }

    /// <summary>
    /// Auction source awards include fixed _kAward grants as well as _iDropPool draws. Lua_CFG_AwardConfig
    /// GetAwardList reads fixed tuples including binding; guild award arrays carry _kServerOpenDay ranges.
    /// (inferred) Those ranges are inclusive and choose the first matching row. Keep the pool-only overload above
    /// unchanged because DailyActivityService already grants its fixed rewards separately.
    /// </summary>
    public List<(int Entry, int Count, int Bind)> RollAuctionAwardBound(long awardId, RoleProgress progress, int serverOpenDay)
    {
        if (_data.Row("AwardConfig", awardId) is not { } raw)
            throw new InvalidOperationException($"Invalid auction award {awardId}.");
        var candidates = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : [raw];
        var row = candidates.FirstOrDefault(candidate => candidate.ValueKind == JsonValueKind.Object
            && (!candidate.TryGetProperty("_kServerOpenDay", out var days) || days.ValueKind != JsonValueKind.Array
                || days.GetArrayLength() < 2 || serverOpenDay >= (GameData.Number(days[0]) ?? 1)
                    && serverOpenDay <= (GameData.Number(days[1]) ?? int.MaxValue)));
        if (row.ValueKind != JsonValueKind.Object) return [];
        var goods = new List<(int Entry, int Count, int Bind)>();
        if (row.TryGetProperty("_kAward", out var fixedGoods) && fixedGoods.ValueKind == JsonValueKind.Array)
            foreach (var tuple in fixedGoods.EnumerateArray())
            {
                if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 2)
                    throw new InvalidOperationException($"Invalid fixed auction award {awardId}.");
                goods.Add((checked((int)(GameData.Number(tuple[0]) ?? 0)),
                    checked((int)(GameData.Number(tuple[1]) ?? 0)),
                    tuple.GetArrayLength() > 2 ? checked((int)(GameData.Number(tuple[2]) ?? 0)) : 0));
            }
        if (GameData.Int(row, "_iDropPool") is var pool and > 0)
            RollPool(pool, progress, [], goods);
        return goods.GroupBy(g => (g.Entry, g.Bind)).Select(g => (g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind)).ToList();
    }

    private void PickFromGroup(long group, GameSession player, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null)
        => PickFromGroup(group, player.Progress!, drops, bound);

    internal void PickFromGroup(long group, RoleProgress progress, List<(int, int)> drops, List<(int Entry, int Count, int Bind)>? bound = null,
        Func<long, bool>? questActive = null, int monsterLevel = DropAttenuation.NoMonster)
    {
        if (_data.Row("DropGroupConfig", group) is not { ValueKind: JsonValueKind.Array } rows) return;
        var p = progress;
        var candidates = rows.EnumerateArray().Where(r =>
        {
            if (GameData.Int(r, "_iWeights") <= 0 || GameData.Int(r, "_iActivityid") != 0) return false;
            if (GameData.Int(r, "_iQuestLimit") is var quest and not 0 && questActive?.Invoke(quest) != true) return false;
            long min = GameData.Int(r, "_iMinLevelLimit"), max = GameData.Int(r, "_iMaxLevelLimit");
            if (p.Level < min || max > 0 && p.Level > max) return false;
            return !r.TryGetProperty("_iJobLimit", out var jobs) || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() == 0
                   || jobs.EnumerateArray().Any(j => GameData.Number(j) == p.Career);
        }).ToList();
        long total = candidates.Sum(r => GameData.Int(r, "_iWeights"));
        if (total <= 0) return;
        long roll = (long)(_random.NextDouble() * total);
        foreach (var r in candidates)
        {
            roll -= GameData.Int(r, "_iWeights");
            if (roll >= 0) continue;
            int count = _random.Next((int)GameData.Int(r, "_iMinNumber"), (int)Math.Max(GameData.Int(r, "_iMinNumber"), GameData.Int(r, "_iMaxNumber")) + 1);
            int entry = (int)GameData.Int(r, "_kDropItem");
            int amount = _attenuation.Apply(r, Math.Max(1, count), p.Level, monsterLevel, _random);
            if (amount <= 0) return;
            drops.Add((entry, amount));
            bound?.Add((entry, amount, (int)GameData.Int(r, "_iBind")));
            return;
        }
    }

    /// <summary>LvExpCorrectionConfig: player level − monster level → ratio per 10000.</summary>
    private long Correction(int gap)
    {
        _expCorrection ??= _data.Table("LvExpCorrectionConfig").Values
            .GroupBy(r => (int)GameData.Int(r, "_iBaseLvGap"))
            .ToDictionary(g => g.Key, g => GameData.Int(g.First(), "_iLvDifferenceCorrection"));
        return _expCorrection.GetValueOrDefault(gap, 10000);
    }
}
