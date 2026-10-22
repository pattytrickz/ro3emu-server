using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task BossClearPublishesParticipantsBeforeOpeningTheResultsPanel()
    {
        SyntheticSolo();
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice));
        long instance = await Load(alice);
        await Kill(alice, await BossIn(instance));
        await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        var messages = alice.Inbox.Snapshot().ToList();
        int stats = messages.FindIndex(e => e.IsGame(DungeonService.Statistics));
        int result = messages.FindIndex(e => e.IsGame(DungeonService.Result));
        Assert.True(stats >= 0 && stats < result, "The MVP panel needs combat statistics before the clear result.");
        var report = DungeonStatisticsRes.Parser.ParseFrom(messages[stats].Body);
        var role = Assert.Single(report.DungeonRolesInfo[instance].DungeonRolesInfo);
        Assert.Equal((long)alice.Self!.Uuid, role.RoleBase.Id);
        Assert.True(role.TotalDamage > 0);
    }

    [ClientDataFact]
    public async Task BossBattleResurrectionUsesItsAllowanceWhileSelfReviveStaysDisabled()
    {
        ChangeRow("TenGameplayConfig", "50101", row => row["_kLimit"] = new System.Text.Json.Nodes.JsonArray(2, 5));
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        await Party(alice, bob);
        Assert.Equal(0, await Enter(alice));
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        await Load(alice);
        await Load(bob);
        var target = Session(bob);
        var dungeon = Service<DungeonService>();
        await OnLoop(async () =>
        {
            target.Dead = true;
            foreach (var handler in Service<CombatService>().PlayerDied) await handler(target);
        });
        await bob.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(bob, CombatService.Relive, EntityReliveAck.Parser)).Result);
        Assert.Equal(0, dungeon.ExternalReviveError(target));

        // A valid resurrection invitation for the last available battle revive. The acceptance travels
        // through the real handler, which owns the dead-to-alive transition and debits the allowance.
        await OnLoop(() =>
        {
            target.DungeonAttempt!.RevivesUsed[target.Role!.Id] = 9;
            target.WorldState.ReviveSource = Session(alice).Role!.Id;
            target.WorldState.ReviveExpiresMs = MonsterService.NowMs + 30000;
            target.WorldState.ReviveHpRate = target.WorldState.ReviveSpRate = 10000;
            return Task.CompletedTask;
        });
        await bob.SendGameAsync(WorldCoreService.ReliveAccept, new EntityReliveInviteAcceptReq
        { SourceEntityID = Session(alice).Role!.Id, IsAccept = true });
        Assert.Equal(0, (await Next(bob, WorldCoreService.ReliveAccept, EntityReliveInviteAcceptResp.Parser)).Result);
        Assert.False(target.Dead);
        Assert.Equal(10, target.DungeonAttempt!.RevivesUsed[target.Role!.Id]);
        await OnLoop(() => { target.Dead = true; return Task.CompletedTask; });
        Assert.Equal(ErrorCodes.FeatureUnavailable, dungeon.ExternalReviveError(target));
    }
}
