using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Reported: "Sanctuary - only does 1 tick of healing", "Kyrie Eleison - Shield is applied but doesn't do anything
/// when hit", "Lex Aeterna - Mob seems to receive the debuff but ... doesn't receive more damage".</summary>
public sealed partial class SkillCombatTests
{
    private const int Sanctuary = 1411501, SanctuaryGroup = 14115, SanctuaryField = 22000003;
    private const int KyrieEleison = 1411701, KyrieGroup = 14117;
    private const int LexAeterna = 1410201, LexGroup = 14102, HolyLight = 1401101;

    /// <summary>
    /// Sanctuary Lv 1: "Summon a ${2}m*${3}m Holy Spirit blessing area ... For the next ${4} seconds, every ${5} seconds, it
    /// restores Life ... to up to ${6} allies within the area" (4 s, 0.6 s; _kLogicData [4]). NPCConfig 22000003 is named
    /// "Sanctuary".
    /// </summary>
    [ClientDataFact]
    public async Task SanctuaryLeavesAFieldThatHealsEverySixTenthsOfASecond()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4200"); await Teach("alice", SanctuaryGroup);
        var s = Session("alice");
        var point = s.Position.Clone();

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = Sanctuary, TargetPos = point });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SkillId == Sanctuary && x.Dsts.Any(d => d.DstID == s.Role!.Id && d.DamageType == DamageType.Heal));
        var field = await Loop.InvokeAsync(() => Task.FromResult(Assert.Single(Session("alice").SkillObjects)));
        Assert.Equal(SanctuaryField, field.NpcId);
        Assert.Equal(600, field.IntervalMs);
        Assert.Equal((long)field.Uid, first.SrcID);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SrcID == first.SrcID && x.Dsts.Any(d => d.DstID == s.Role!.Id && d.DamageType == DamageType.Heal));
    }

    /// <summary>
    /// Kyrie Eleison: "a Physical damage absorption shield equal to your Magic ATK*${2}%" (40); its buff's _kData1 [4000]
    /// holds that ratio and its description reads "This shield absorbs Physical damage".
    /// </summary>
    [ClientDataFact]
    public async Task KyrieEleisonShieldsMagicAttackTimesFortyPercentOfPhysicalDamage()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4200"); await Teach("alice", KyrieGroup);
        var s = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = KyrieEleison, TargetId = s.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, b => b.Buffs.Any(x => x.ConfigId == KyrieEleison));
        await Loop.InvokeAsync(async () =>
        {
            long shield = Character.Stats(s)[104] * 4000 / 10000;
            Assert.True(shield > 0);
            Assert.Equal(shield, Assert.Single(s.Buffs, b => b.ConfigId == KyrieEleison).ShieldRemaining);
            Assert.Equal(7, await buffs.AbsorbAsync(s, 7, magic: true));
            Assert.Equal(0, await buffs.AbsorbAsync(s, 1));
            Assert.Equal(shield - 1, buffs.ShieldAmount(s));
        });
    }

    /// <summary>
    /// Lex Aeterna: "applying ${2}% [Damage Taken Increase] ... disappears after the target takes damage"; its buff's
    /// _kChangeAttri [[1216, -2000]] is per_res_dmg1 -20%.
    /// </summary>
    [ClientDataFact]
    public async Task LexAeternaRaisesOnlyTheNextHitByTwentyPercent()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4200"); await Teach("alice", LexGroup);
        var s = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var holyLight = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", HolyLight)!.Value;
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = LexAeterna, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            b => b.UnitId == target.Uid && b.Buffs.Any(x => x.ConfigId == LexAeterna));
        combat.Rolls = new GuardianStableDamageRolls();
        var (marked, plain) = await Loop.InvokeAsync(async () =>
        {
            async Task<long> Hit()
            {
                long before = target.Hp;
                await combat.CastExtraAsync(s, holyLight, [target]);
                return before - target.Hp;
            }
            return (await Hit(), await Hit());
        });
        Assert.True(plain > 0);
        Assert.Equal((long)Math.Round(plain * 1.2), marked);
        Assert.Equal(0, _app.Services.GetRequiredService<BuffService>().UnitBuffRemainingMs(target, LexAeterna, s.Role!.Id));
    }
}
