using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// SkillConfig._iIfAutoAtk 2 (enum_AutoNormalAttackType.AutoNormalAttack) on an active skill: the client follows
    /// the cast with the career's normal attack (LC_AutoAttackAppendState: GetNormalSkill). The server's attack loop
    /// repeated Blade Smash itself every swing, past its 7 s cooldown.
    /// </summary>
    [ClientDataFact]
    public async Task AutoNormalAttackSkillIsFollowedByTheNormalAttackNotRepeatedPastItsCooldown()
    {
        const int bladeSmash = 1509901; // group 15099, _iIfAutoAtk 2, _iIsNormalAtk 0, _iCD 7000
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100"); await Teach("alice", 15099);
        int normal = app.Services.GetRequiredService<SkillService>().NormalAttack(5100);
        Assert.NotEqual(bladeSmash, normal);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = bladeSmash, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        Assert.Equal(bladeSmash, (await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => u.SessionId == accepted.SessionId)).SkillID);

        // The loop's next swings, well inside the cooldown, are normal attacks on the same target.
        long self = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id));
        for (int i = 0; i < 2; i++)
        {
            var swing = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
                u => u.SrcID == self && u.SessionId != accepted.SessionId);
            Assert.Equal(normal, swing.SkillID);
            Assert.Equal(target.Uid, swing.TargetId);
        }
        await Loop.InvokeAsync(() => { Assert.Equal(normal, Session("alice").Attack?.SkillId); return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = bladeSmash, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillCooldown, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>
    /// _iIfAutoAtk 1 (NotInterrupt): LC_Skill_RunTimeContext leaves the normal-attack cache alone at the skill's end, so
    /// a running auto-attack goes on. The server cleared it on every cast that was not AutoNormalAttack.
    /// </summary>
    [ClientDataFact]
    public async Task NotInterruptSkillLeavesTheRunningAutoAttackGoingOn()
    {
        const int arrowAttack = 1300001, chargeArrow = 15500102; // careers 3100-3400 normal attack; group 155001, _iIfAutoAtk 1
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3400"); await Teach("alice", 155001);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.CompletedTask; });
        long self = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id));

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = arrowAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() => { Assert.Equal(arrowAttack, Session("alice").Attack?.SkillId); return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = chargeArrow, TargetId = target.Uid });
        var charge = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, charge.Result);
        await Loop.InvokeAsync(() => { Assert.Equal(arrowAttack, Session("alice").Attack?.SkillId); return Task.CompletedTask; });

        // After Charge Arrow, the loop swings the normal attack again without a new request from the client.
        var swing = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => u.SrcID == self && u.SessionId > charge.SessionId && u.SkillID == arrowAttack);
        Assert.Equal(target.Uid, swing.TargetId);
    }
    /// <summary>
    /// Normal attacks have no cooldown and _bCancelCast 1: each tap cancelled the running swing after its hit and
    /// started another, so tapping out-paced the attack speed (#general: "spam manually AA ... huge aspd", AGI made
    /// no difference). A tap before the running swing's ASPD-scaled action ends is refused; on another monster it
    /// moves the running auto-attack there for the next swing.
    /// </summary>
    [ClientDataFact]
    public async Task TappingTheNormalAttackCannotOutpaceTheAttackSpeed()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        int normal = app.Services.GetRequiredService<SkillService>().NormalAttack(1100);
        var first = await Target("alice", stationary: true);
        var second = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { first.Hp = second.Hp = 1_000_000_000; return Task.CompletedTask; });
        long self = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id));

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = first.Uid });
        var started = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, started.Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = first.Uid });
        Assert.Equal(ErrorCodes.SkillCooldown, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = second.Uid });
        Assert.Equal(ErrorCodes.SkillCooldown, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Loop.InvokeAsync(() => { Assert.Same(second, Session("alice").Attack?.Target); return Task.CompletedTask; });

        var swing = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => u.SrcID == self && u.SessionId != started.SessionId);
        Assert.Equal(normal, swing.SkillID);
        Assert.Equal(second.Uid, swing.TargetId);
        long ready = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").NormalAttackReadyMs));
        Assert.True(ready > swing.StartTime, "the loop's swing paces the next tap too");
    }
}

