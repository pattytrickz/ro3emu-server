using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>E04 fixed rewards, real role ownership/writer and wire presentation. No load runs.</summary>
public sealed class KillEconomyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(RepoRoot(), ".loadtest-temp", "e04-kill-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private static readonly Lazy<EconomyCatalog> Catalog = new(() => EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "killtest" && s.Role != null);
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.PartyShareExcludedAccounts = ["aibot*"]; // Sol RO3 house rule; the party tests' other accounts are real players
        });
        SureHitRolls.Install(_app);
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
    }
    public async Task DisposeAsync()
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = null;
        Service<AccountStore>().BeforeOwnedApplyForTest = null;
        await _app.StopAsync(); await _app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
    private async Task<BotClient> Enter(bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("killtest", "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync("KillTest")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(10));
        await OnLoop(() =>
        {
            Service<RewardService>().QualifiesForBonus = null;
            Service<RewardService>().KillDropsGranted.Clear();
            Service<RewardService>().ResolveKillForTest = (_, _) => ([(1000000, 3), (1, 7), (2010121, 1)], 3, 4);
            Service<CharacterService>().AutoDecomposeReturns = (_, item) => item.Entry == 2010121 ? [(1, 11L)] : [];
            return Task.CompletedTask;
        });
        return bot;
    }
    private MonsterUnit Monster(long uid = 77) => new()
    {
        Uid = uid, SceneEntry = Session.SceneEntry, RuntimeMap = Session.RuntimeMap,
        Template = new MonsterTemplate(10001, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
            false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, 10001), Hp = 0
    };
    private Task Reward(GameSession player, MonsterUnit monster) => Service<CombatService>().Killed
        .Single(h => h.Target == Service<RewardService>())(player, monster);
    private EconomySnapshot Cut() => S3EconomyReader.Read(_directory, Catalog.Value);
    private long ReceiptCount()
    {
        using var db = Service<AccountStore>().Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='monster-kill'"; return (long)c.ExecuteScalar()!;
    }

    [ClientDataFact]
    public async Task LethalHitAndCorpseWaitForDurableRewardBarrier()
    {
        await using var bot = await Enter();
        using (var client = new HttpClient())
            (await client.PostAsync(_http + "/ro3emu/gm?account=killtest", new StringContent("!job 1100"))).EnsureSuccessStatusCode();
        var player = Session;
        var monster = await Service<GameLoop>().InvokeAsync(async () =>
        {
            player.Progress!.Skills[11001] = 1;
            var monsters = Service<MonsterService>();
            var target = await monsters.SpawnSingleAsync(player.SceneEntry, 5000001,
                new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, 0), player.InstanceId,
                customTemplate: monsters.Template(5000001)! with { RoamDistance = 0 });
            target!.Hp = 1; return target;
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/monster-kill") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = monster.Uid });
        var cast = CastSkillRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), TimeSpan.FromSeconds(10))).Body);
        Assert.Equal(0, cast.Result);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(monster.Dead); Assert.Equal(0, ReceiptCount());
            await Assert.ThrowsAsync<TimeoutException>(async () => await bot.Inbox.WaitAsync(e =>
                e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == cast.SessionId,
                TimeSpan.FromMilliseconds(150)));
            release.TrySetResult();
            var hit = SkillHitInfo.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
                && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == cast.SessionId, TimeSpan.FromSeconds(10))).Body);
            Assert.True(Assert.Single(hit.Dsts).IsDead); Assert.Equal(1, ReceiptCount());
            await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify)
                && GetRewardNotify.Parser.ParseFrom(e.Body).RewardType.Contains(8), TimeSpan.FromSeconds(10));
            Assert.True(monster.Dead);
        }
        finally { release.TrySetResult(); }
    }

    [ClientDataFact]
    public async Task PostcommitEffectFaultStillFinalizesAndObservesTheKillOnce()
    {
        await using var bot = await Enter();
        using (var client = new HttpClient())
            (await client.PostAsync(_http + "/ro3emu/gm?account=killtest", new StringContent("!job 1100"))).EnsureSuccessStatusCode();
        var player = Session;
        var monster = await Service<GameLoop>().InvokeAsync(async () =>
        {
            player.Progress!.Skills[11001] = 1;
            var monsters = Service<MonsterService>();
            var target = await monsters.SpawnSingleAsync(player.SceneEntry, 5000001,
                new MapPoint(player.Position.X, player.Position.Y, player.Position.Z, 0), player.InstanceId,
                customTemplate: monsters.Template(5000001)! with { RoamDistance = 0 });
            target!.Hp = 1; return target;
        });
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int observerCalls = 0;
        Service<CombatService>().DealtAsync.Add((_, target, _, _) => target == monster
            ? throw new IOException("postcommit effect failure") : Task.CompletedTask);
        Service<CombatService>().Killed.Add((_, target) =>
        {
            if (target == monster) { observerCalls++; observed.TrySetResult(); }
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = monster.Uid });
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(monster.Dead);
        Assert.True(monster.KillObserversStarted);
        Assert.Equal(1, observerCalls);
        Assert.Equal(1, ReceiptCount());
        await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify)
            && GetRewardNotify.Parser.ParseFrom(e.Body).RewardType.Contains(8), TimeSpan.FromSeconds(10));
        await OnLoop(() => Reward(player, monster));
        Assert.Equal(1, observerCalls);
        Assert.Equal(1, ReceiptCount());
    }

    [ClientDataFact]
    public async Task RepeatedLethalCallbackCommitsWholeConvertedOutcomeOnceAndNewSpawnIsDistinct()
    {
        await using var bot = await Enter(); var player = Session; var monster = Monster();
        var opening = Cut(); long currency = player.Progress!.Currency[1], exp = player.Progress.Exp;
        await OnLoop(async () => { await Reward(player, monster); await Reward(player, monster); });
        var packet = GetRewardNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify)
            && GetRewardNotify.Parser.ParseFrom(e.Body).RewardType.Contains(8), TimeSpan.FromSeconds(10))).Body);
        Assert.Equal(monster.Template.NpcId, packet.TargetEntry);
        Assert.Equal(currency + 18, player.Progress.Currency[1]); Assert.Equal(exp + 3, player.Progress.Exp);
        Assert.Equal(3, player.Progress.JobLevel); Assert.Equal(2, player.Progress.JobExp); // novice levels 1 and 2 each consume 1 EXP
        Assert.Equal(23, player.Items.Single(i => i.Entry == 1000000).Stack);
        Assert.Single(player.Items, i => i.Entry == 2010121); // starter weapon, generated loot decomposed
        Assert.Equal(1, ReceiptCount());
        var closing = Cut(); var ledger = S3EconomyReader.ReadLedger(_directory, Catalog.Value, opening, closing)!;
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.Equal(monster.RewardSource!.Key, Assert.Single(ledger.Receipts).SourceKeys!.Single());
        var newSpawn = Monster(); await OnLoop(() => Reward(player, newSpawn));
        Assert.NotEqual(monster.RewardSource, newSpawn.RewardSource); Assert.Equal(2, ReceiptCount());
    }

    /// <summary>
    /// The live realm database grew about 1.5 GB a day, mostly kill receipts and their owner acknowledgements (20 Oct:
    /// 354,000 kills, 2.4 GB), until the server stalled under its memory limit. A receipt past the retention window keeps
    /// its row, source claim and ledger legs; its payload is emptied and its acknowledgement deleted.
    /// </summary>
    [ClientDataFact]
    public async Task OldKillReceiptsAreCompactedAndTheirSourceStaysClaimed()
    {
        await using var bot = await Enter(); var player = Session; var monster = Monster();
        await OnLoop(() => Reward(player, monster));
        Assert.Equal(1, ReceiptCount());
        var compaction = Service<ReceiptCompaction>();
        Assert.Equal(0, await compaction.PassAsync(TimeSpan.FromHours(1))); // still inside the window
        await Task.Delay(10);
        Assert.Equal(1, await compaction.PassAsync(TimeSpan.Zero));
        using (var db = Service<AccountStore>().Open())
        using (var c = db.CreateCommand())
        {
            c.CommandText = """
                SELECT length(r.intent_payload)+length(r.outcome)+length(r.child_receipts)+length(r.before_revisions),
                       (SELECT count(*) FROM owner_operation_receipt o WHERE o.operation_id=r.operation_id),
                       (SELECT count(*) FROM operation_source s WHERE s.operation_id=r.operation_id),
                       (SELECT count(*) FROM economy_entry e WHERE e.operation_id=r.operation_id), r.leg_count
                FROM operation_receipt r WHERE r.command_kind='monster-kill'
                """;
            using var row = c.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal(0, row.GetInt64(0));
            Assert.Equal(0, row.GetInt64(1));
            Assert.Equal(1, row.GetInt64(2));
            Assert.Equal(row.GetInt64(4), row.GetInt64(3));
        }
        Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(monster.RewardSource!.Key));
        Assert.Equal(0, await compaction.PassAsync(TimeSpan.Zero)); // the saved position skips it

        long currency = player.Progress!.Currency[1];
        var next = Monster(); await OnLoop(() => Reward(player, next));
        Assert.Equal(2, ReceiptCount());
        Assert.Equal(currency + 18, player.Progress.Currency[1]);
    }

    [ClientDataFact]
    public async Task HeldCommitThenDetachPaysOriginalRoleOnceWithoutEarlyPresentation()
    {
        var bot = await Enter(); var player = Session; long role = player.Role!.Id; var monster = Monster();
        long currency = player.Progress!.Currency[1];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/monster-kill") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        var pending = OnLoop(() => Reward(player, monster));
        Task? detached = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, ReceiptCount()); Assert.Equal(currency, Service<RoleStore>().LoadProgress(role).Currency[1]);
            await Assert.ThrowsAsync<TimeoutException>(async () => await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.GetRewardNotify), TimeSpan.FromMilliseconds(150)));
            await OnLoop(() => { detached = Service<RoleHandover>().Begin(player); Assert.True(player.Attachment!.Cancelled); return Task.CompletedTask; });
            release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await detached!.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); await bot.DisposeAsync(); }
        Assert.Equal(1, ReceiptCount()); Assert.Equal(currency + 18, Service<RoleStore>().LoadProgress(role).Currency[1]);
        await using var again = await Enter(create: false);
        await OnLoop(() => Reward(Session, monster));
        Assert.Equal(1, ReceiptCount()); Assert.Equal(currency + 18, Session.Progress!.Currency[1]);
    }

    [ClientDataFact]
    public async Task LostAcknowledgementAndPostcommitNoticeFaultNeverPayAgain()
    {
        await using var bot = await Enter(); var player = Session; var monster = Monster();
        Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        Service<RewardService>().KillDropsGranted.Add((_, _, _) => throw new IOException("postcommit observer"));
        await Assert.ThrowsAsync<IOException>(() => OnLoop(() => Reward(player, monster)));
        Assert.True(monster.RewardSettlement!.Committed); Assert.Equal(1, ReceiptCount());
        var committed = Service<RoleStore>().LoadProgress(player.Role!.Id);
        await OnLoop(() => Reward(player, monster));
        Assert.Equal(1, ReceiptCount()); Assert.Equal(committed.Currency[1], player.Progress!.Currency[1]);
    }

    [ClientDataFact]
    public async Task LastLedgerFailureRollsBackItemsCurrencyExpAndSource()
    {
        await using var bot = await Enter(); var player = Session; var monster = Monster();
        var before = Service<RoleStore>().LoadProgress(player.Role!.Id);
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER kill_fail BEFORE INSERT ON economy_entry WHEN NEW.leg_no=1 BEGIN SELECT RAISE(ABORT,'kill rollback'); END");
        await Assert.ThrowsAsync<SqliteException>(() => OnLoop(() => Reward(player, monster)));
        var after = Service<RoleStore>().LoadProgress(player.Role.Id);
        Assert.Equal(before.Currency[1], after.Currency[1]); Assert.Equal(before.Exp, after.Exp); Assert.Equal(before.JobExp, after.JobExp);
        Assert.Equal(20, player.Items.Single(i => i.Entry == 1000000).Stack); Assert.Equal(0, ReceiptCount());
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER kill_fail");
        monster.RewardCommit = null;
        await OnLoop(() => Reward(player, monster)); Assert.Equal(1, ReceiptCount());
    }

    /// <summary>
    /// Issue #3: a party kill paid only the killer. A field kill's EXP (after the level gap and party bonus) is split
    /// evenly among the party members online on the killer's map, each through its own kill source; loot stays with
    /// the killer, a member on another map gets nothing, and a replayed kill pays nobody twice.
    /// </summary>
    [ClientDataFact]
    public async Task PartyMembersOnTheKillersMapShareTheKillExpOnce()
    {
        await using var bot = await Enter(); var player = Session;
        await using var bob = await Member("partybob", "Bob");
        await using var carol = await Member("partycarol", "Carol");
        await using var dave = await Member("partydave", "Dave");
        await Party(bot, bob, carol, dave);
        GameSession Of(string account) => Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);
        var (b, c, d) = (Of("partybob"), Of("partycarol"), Of("partydave"));
        await OnLoop(() =>
        {
            d.SceneEntry = player.SceneEntry + 1; // another map
            Service<RewardService>().ResolveKillForTest = (_, _) => ([(1000000, 3)], 9, 9);
            return Task.CompletedTask;
        });
        var before = new[] { player, b, c, d }.Select(s => (s.Progress!.Exp, s.Progress.JobLevel, s.Progress.JobExp)).ToArray();
        int bobLoot = b.Items.Where(i => i.Entry == 1000000).Sum(i => i.Stack);
        var monster = Monster();
        await OnLoop(async () => { await Reward(player, monster); await Reward(player, monster); });

        foreach (var s in new[] { player, b, c }) Assert.Equal(before[0].Exp + 3, s.Progress!.Exp);
        Assert.All(new[] { b, c }, s => Assert.Equal((player.Progress!.JobLevel, player.Progress.JobExp), (s.Progress!.JobLevel, s.Progress.JobExp)));
        Assert.NotEqual((before[0].JobLevel, before[0].JobExp), (player.Progress!.JobLevel, player.Progress.JobExp));
        Assert.Equal(before[3], (d.Progress!.Exp, d.Progress.JobLevel, d.Progress.JobExp));
        Assert.Equal(23, player.Items.Single(i => i.Entry == 1000000).Stack);
        Assert.Equal(bobLoot, b.Items.Where(i => i.Entry == 1000000).Sum(i => i.Stack));
        Assert.Equal(3, ReceiptCount());
        Assert.Equal(before[0].Exp + 3, Service<RoleStore>().LoadProgress(c.Role!.Id).Exp);
    }

    [ClientDataFact]
    public async Task AnAiPartyMemberTakesNoShareOfAPlayersKillButSharesItsOwn()
    {
        await using var bot = await Enter(); var player = Session;
        await using var bob = await Member("partybob", "Bob");
        await using var ai = await Member("aibot01", "Botty");
        await Party(bot, bob, ai);
        GameSession Of(string account) => Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);
        var (b, a) = (Of("partybob"), Of("aibot01"));
        await OnLoop(() => { Service<RewardService>().ResolveKillForTest = (_, _) => ([], 2, 2); return Task.CompletedTask; });
        long p0 = player.Progress!.Exp, b0 = b.Progress!.Exp, a0 = a.Progress!.Exp;

        // The player's kill: split between the two real players (2 / 2), the AI member gets nothing.
        await OnLoop(() => Reward(player, Monster(78)));
        Assert.Equal((p0 + 1, b0 + 1, a0), (player.Progress.Exp, b.Progress.Exp, a.Progress.Exp));

        // The AI member's kill still shares with the real players (3 / 3). Small amounts: a level-1 role levels at 5.
        await OnLoop(() => { Service<RewardService>().ResolveKillForTest = (_, _) => ([], 3, 3); return Task.CompletedTask; });
        await OnLoop(() => Reward(a, Monster(79)));
        Assert.Equal((p0 + 2, b0 + 2, a0 + 1), (player.Progress.Exp, b.Progress.Exp, a.Progress.Exp));
    }

    private async Task<BotClient> Member(string account, string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(account, "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        await bot.Inbox.WaitAsync(e => e.IsGame(ShopService.ShopSync), TimeSpan.FromSeconds(10));
        return bot;
    }

    private static async Task Party(BotClient leader, params BotClient[] members)
    {
        static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
            where T : Google.Protobuf.IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(10))).Body);
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        foreach (var member in members)
        {
            await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
            Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
            await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
            await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
                { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
            Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
