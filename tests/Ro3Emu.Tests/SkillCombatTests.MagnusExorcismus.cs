using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int MagnusExorcismus = 1410901, MagnusGroup = 14109, MagnusCross = 22000009;

    /// <summary>
    /// Magnus Exorcismus Lv 1: "Summon a ${2}m*${3}m cross ... For the next ${4} sec., it deals ... damage to up to ${6}
    /// enemies in the area every ${5} sec." (4 s, 0.5 s); _kLogicData [22000009, 4, 8, 10] names the cross NPC. Reported:
    /// "Magnus Exorcismus is not really working" (it struck once and left nothing on the ground).
    /// </summary>
    [ClientDataFact]
    public async Task MagnusExorcismusLeavesACrossThatStrikesEveryHalfSecond()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4200"); await Teach("alice", MagnusGroup);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var point = new Vec3 { X = target.X, Y = target.Y, Z = target.Z };

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = MagnusExorcismus, TargetPos = point });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SkillId == MagnusExorcismus && x.Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
        var cross = await Loop.InvokeAsync(() => Task.FromResult(Assert.Single(Session("alice").SkillObjects)));
        Assert.Equal(MagnusCross, cross.NpcId);
        Assert.Equal(500, cross.IntervalMs);
        Assert.Equal((long)cross.Uid, first.SrcID);
        var second = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SrcID == first.SrcID && x.Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
        Assert.Equal(MagnusExorcismus, second.SkillId);
    }

    /// <summary>
    /// Holy Cross Judgment (BuffConfig 1422401, _kLogicData [10000, 1420901]): "When [Magnus Exorcismus] is cast, it also
    /// unleashes a powerful burst of holy light, dealing ... to up to ${1} enemies within the area." The cross strikes
    /// later, so the burst takes the enemies in its area at the cast.
    /// </summary>
    [ClientDataFact]
    public async Task HolyCrossJudgmentBurstsOnTheEnemiesInTheCrossAtItsCast()
    {
        const int Burst = 1420901;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4200"); await Teach("alice", MagnusGroup);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var point = new Vec3 { X = target.X, Y = target.Y, Z = target.Z };

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = MagnusExorcismus, TargetPos = point });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        // Set during the cast's 0.66 s chant, once the setup's own talent refreshes (SeasonTalentService) have run.
        await Loop.InvokeAsync(() => { Session("alice").TalentProcBuffs = [(1422401, ["4"])]; return Task.CompletedTask; });
        var burst = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == Burst);
        Assert.Contains(burst.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
    }

    /// <summary>
    /// "Damage dealt to enemies of the Undead Race or Demon Race, or with the Shadow property or Undead property, is
    /// increased by ${9}%" (30): Lv 1's _kDamageParam1 [500, 650] holds that as its second value.
    /// </summary>
    [ClientDataFact]
    public async Task MagnusExorcismusStrikesUndeadAndDemonsWithItsSecondDamageValue()
    {
        await using var alice = await InWorld("Alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var row = data.Row("SkillConfig", MagnusExorcismus)!.Value;
        var heal = data.Row("SkillConfig", 1400701)!.Value;
        // NPCConfig _iRace follows AttributeConfig 401-410 (animal_race, human_race, demon_race, plant_race, undead_race ...);
        // _iElement the EleConfig columns (_iNone .. _iShadow 8, _iGhost, _iUndead 10).
        var branches = await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            var plain = monsters.Template(5000001)! with { Race = 1, Element = 1, RoamDistance = 0 };
            async Task<int> Branch(MonsterTemplate template, System.Text.Json.JsonElement skill)
            {
                var unit = await monsters.SpawnSingleAsync(s.SceneEntry, template.NpcId,
                    new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0), s.InstanceId, customTemplate: template);
                return combat.DamageBranch(s, unit!, skill);
            }
            return new[]
            {
                await Branch(plain, row), await Branch(plain with { Race = 3 }, row),
                await Branch(plain with { Race = 5 }, row), await Branch(plain with { Element = 8 }, row),
                await Branch(plain with { Element = 10 }, row), await Branch(plain with { Race = 5 }, heal),
            };
        });
        Assert.Equal([0, 1, 1, 1, 1, 1], branches);
    }
}
