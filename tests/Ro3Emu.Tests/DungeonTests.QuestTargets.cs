using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    private async Task<TaskDeliveryResp> Claim(BotClient bot, long task)
    {
        await bot.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = task } } });
        return await Next(bot, TaskService.TaskDelivery, TaskDeliveryResp.Parser);
    }

    /// <summary>
    /// 106602 CheckTenBOSSWin counts a committed clear of a listed TenGameplay boss: growth emblem title 3 (703990) then
    /// completes through its child 703030 "Clear Wildlands dungeons" [106602,1,4300001,..]. The guild co-op quest 1601010
    /// needs a fellow guild member in the clearing party, so a solo clear leaves it at 0.
    /// </summary>
    [ClientDataFact]
    public async Task ATenGameplayBossClearCountsForTheGrowthTitleButNotForAGuildCoopQuestAlone()
    {
        SyntheticSolo(); // exported 50101 requires three; only this host's cached admission is relaxed
        var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest done 701990");
        await Gm(alice, "!quest done 702990");
        await QuestProbe.AtAsync(_app, "alice", 703030, 0); // title 3 offered
        foreach (long child in new long[] { 703010, 703020, 703040, 703050, 703060, 703070 }) await Gm(alice, $"!quest done {child}");
        await QuestProbe.AcceptAsync(_app, _http, "alice", 1601010); // [Guild Co-op] Phantom Realm, same boss list
        await QuestProbe.AtAsync(_app, "alice", 1601010, 0);

        Assert.Equal(0, await Enter(alice));
        long instance = await Load(alice);
        await Kill(alice, await BossIn(instance));
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        await QuestProbe.DoneAsync(_app, "alice", 703030);
        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "alice", 1601010))!.CompleteNum[0]);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);

        await alice.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        await using var relog = await InWorld("Alice", create: false);
        await QuestProbe.DoneAsync(_app, "alice", 703030); // the event count survives the relogin
        Assert.Single((await Claim(relog, 703030)).Data);
        Assert.Single((await Claim(relog, 703990)).Data);
        await QuestProbe.WaitAsync(_app, "alice", 703990, i => i.Status == TaskState.Delivery, "delivered");
        Assert.True(TaskService.IsDelivered(Session(relog), 703990));
        await QuestProbe.AtAsync(_app, "alice", 704020, 0); // title 4 is offered next
    }

    /// <summary>
    /// The side quest 10200010 "Clear a Five-Player Dungeon" has one 106602 target per Wildlands boss
    /// ([106602,1,4300001], [106602,1,43100003], [106602,1,43200006]). A party's clear counts for every member, not only
    /// for the one who landed the last hit, and only on the target of the boss it cleared.
    /// </summary>
    [ClientDataFact]
    public async Task APartyBossClearCountsTheSideQuestForEveryMember()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        var members = new[] { (alice, "alice"), (bob, "bob"), (carol, "carol") };
        foreach (var (bot, account) in members)
        {
            await Gm(bot, "!lvl 99");
            await QuestProbe.AcceptAsync(_app, _http, account, 10200010);
            await QuestProbe.AtAsync(_app, account, 10200010, 0);
        }
        await Party(alice, bob, carol);

        await ClearAsParty(members, mark: 1, Scene, Boss);
        foreach (var (_, account) in members)
            await QuestProbe.WaitAsync(_app, account, 10200010, i => i.CompleteNum is [1, 0, 0], "first boss only");
        await ClearAsParty(members, mark: 2, scene: 3120101, boss: 43100003);
        foreach (var (_, account) in members)
            await QuestProbe.WaitAsync(_app, account, 10200010, i => i.CompleteNum is [1, 1, 0], "second boss too");
    }

    private async Task ClearAsParty((BotClient Bot, string Account)[] members, int mark, long scene, int boss)
    {
        var leader = members[0].Bot;
        Assert.Equal(0, await Enter(leader, Request(mark: mark, scene: scene)));
        foreach (var (bot, _) in members.Skip(1))
        {
            await bot.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
            Assert.Equal(0, (await Next(bot, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        }
        long instance = await Load(leader, scene);
        foreach (var (bot, _) in members.Skip(1)) Assert.Equal(instance, await Load(bot, scene));
        await Kill(leader, await BossIn(instance, boss));
        foreach (var (bot, _) in members)
        {
            Assert.Equal((DungeonRes)1, (await Next(bot, DungeonService.Result, DungeonResNotify.Parser)).Res);
            await bot.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(bot, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
            long outside = (await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneEntry;
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = outside });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == outside)).Result);
        }
    }
}
