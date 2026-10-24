using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Starts alice's auto-attack on a stationary, unkillable target; returns the target and alice's role id.</summary>
    private async Task<(MonsterUnit Target, long Self)> AutoAttacking(BotClient alice, bool immobile = false)
    {
        var target = immobile ? await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            var monsters = _app.Services.GetRequiredService<MonsterService>();
            var template = monsters.Template(5000001)! with { RoamDistance = 0, SpeedCm = 0 };
            return (await monsters.SpawnSingleAsync(s.SceneEntry, 5000001,
                new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0), s.InstanceId, customTemplate: template))!;
        }) : await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() => { Assert.NotNull(Session("alice").Attack); return Task.CompletedTask; });
        return (target, await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id)));
    }

    /// <summary>A walkable point <paramref name="metres"/> from alice, or null when the nav grid offers none.</summary>
    private Vec3 Beside(float metres)
    {
        var s = Session("alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(s.SceneEntry)!.Nav;
        var p = s.Position;
        return Enumerable.Range(0, 16).Select(i => i * MathF.Tau / 16)
            .Select(a => new Vec3 { X = p.X + metres * MathF.Cos(a), Y = p.Y, Z = p.Z + metres * MathF.Sin(a) })
            .First(v => nav == null || nav.IsWalkable(v.X, v.Y, v.Z) && nav.CanWalk(p.X, p.Y, p.Z, v.X, v.Z));
    }

    /// <summary>
    /// The client reports small corrections while its player stands and fights (turning to the target, a nudge from
    /// a hit). Each ended the server's auto-attack at 0.1 m, silently: the attack just stopped and the player had to
    /// click again. A stop within AttackLeash keeps it swinging.
    /// </summary>
    [ClientDataFact]
    public async Task ASmallPositionCorrectionKeepsTheAutoAttackGoing()
    {
        await using var alice = await InWorld("Alice");
        var (target, self) = await AutoAttacking(alice);
        await alice.SendGameAsync(MsgId.Walk, new WalkReq
        {
            Pos = await Loop.InvokeAsync(() => Task.FromResult(Beside(0.5f))),
            Face = 90, BStop = true, SpeedRate = 1
        });
        await Loop.InvokeAsync(() => { Assert.NotNull(Session("alice").Attack); return Task.CompletedTask; });
        long after = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").SkillCast?.Id ?? 0));
        var swing = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == self && u.SessionId > after);
        Assert.Equal(target.Uid, swing.TargetId);
    }

    /// <summary>Walking away ends the auto-attack, and the client hears so (a SkillEnd, as when its target dies).</summary>
    [ClientDataFact]
    public async Task WalkingAwayEndsTheAutoAttackAndTellsTheClient()
    {
        await using var alice = await InWorld("Alice");
        var (_, self) = await AutoAttacking(alice);
        await alice.SendGameAsync(MsgId.Walk, new WalkReq
        {
            Pos = await Loop.InvokeAsync(() => Task.FromResult(Beside(0.5f))),
            Dir = 90, Face = 90, SpeedRate = 1
        });
        var end = await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser,
            e => e.SrcID == self && e.InterruptedInfo?.Type == SkillInterruptedInfo.Types.SkillInterruptType.Move);
        Assert.Equal(SkillEndNotify.Types.SkillEndType.Interrupted, end.Type);
        await Loop.InvokeAsync(() => { Assert.Null(Session("alice").Attack); return Task.CompletedTask; });
    }

    /// <summary>
    /// A target out of reach used to hold the loop forever without a swing. It now ends after AttackOutOfReachMs and
    /// the client is told its target was lost.
    /// </summary>
    [ClientDataFact]
    public async Task ATargetLeftOutOfReachEndsTheAutoAttack()
    {
        await using var alice = await InWorld("Alice");
        var (target, self) = await AutoAttacking(alice, immobile: true); // it would walk back into reach
        await Loop.InvokeAsync(() =>
        {
            var far = Beside(12f);
            target.X = far.X; target.Z = far.Z;
            return Task.CompletedTask;
        });
        var end = await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser,
            e => e.SrcID == self && e.InterruptedInfo?.Type == SkillInterruptedInfo.Types.SkillInterruptType.LockerInvalid);
        Assert.Equal(SkillEndNotify.Types.SkillEndType.Interrupted, end.Type);
        await Loop.InvokeAsync(() => { Assert.Null(Session("alice").Attack); return Task.CompletedTask; });
    }

    /// <summary>
    /// An interrupt naming no skill ends the auto-attack, with no swing after it: how an AI party member stops hitting a
    /// monster it leaves to the players for their last hit (#40); every job attack keeps swinging until the target dies.
    /// </summary>
    [ClientDataFact]
    public async Task AnInterruptWithNoSkillEndsTheAutoAttack()
    {
        await using var alice = await InWorld("Alice");
        var (_, self) = await AutoAttacking(alice);
        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq());
        Assert.True((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        await Loop.InvokeAsync(() => { Assert.Null(Session("alice").Attack); return Task.CompletedTask; });
    }
}
