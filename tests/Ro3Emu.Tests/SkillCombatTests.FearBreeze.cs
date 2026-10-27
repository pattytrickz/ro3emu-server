using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class FearBreezeRolls(int proc) : CombatRolls
    {
        public override double Hit() => 0;
        public override int Proc(int max) => proc;
    }

    /// <summary>
    /// Fear Breeze Lv 1 (buff 1330101, _kDescData ["30", "1", "42"]): "While the effect lasts, when a targeted bow skill
    /// hits, the target has a 30% Chance to be struck by piercing arrows". Its arrows are SkillConfig 1330401. Reported:
    /// Arrow Attack and Focused Arrow Strike never brought the extra arrows.
    /// </summary>
    [ClientDataFact]
    public async Task FearBreezeStrikesTheTargetOfABowSkillWithItsArrows()
    {
        var spike = await ArrowAttackUnderFearBreeze(lands: true);
        Assert.NotNull(spike);
        Assert.True(Assert.Single(spike.Dsts).Damage > 0);
    }

    [ClientDataFact]
    public async Task FearBreezeArrowsMissTheirThirtyPercentChance()
    {
        Assert.Null(await ArrowAttackUnderFearBreeze(lands: false));
    }

    private async Task<SkillHitInfo?> ArrowAttackUnderFearBreeze(bool lands)
    {
        const int ArrowAttack = 1300001, Buff = 1330101, Arrows = 1330401;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3400");
        _app.Services.GetRequiredService<CombatService>().Rolls = new FearBreezeRolls(lands ? 29 : 30); // a roll of [0, 100) below the 30 lands
        var target = await Target("alice", stationary: true);
        var s = Session("alice");
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1_000_000;
            await _app.Services.GetRequiredService<BuffService>().AddAsync(s, Buff, 120_000, s.Role!.Id);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArrowAttack, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        if (lands) return await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == Arrows);
        await Task.Delay(1000);
        return alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
            .FirstOrDefault(h => h.SkillId == Arrows);
    }
}
