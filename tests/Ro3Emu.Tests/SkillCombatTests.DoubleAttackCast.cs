using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// A target click casts DBSkillData.currentAtkSkillId and the swings that follow are the one learned _iIsNormalAtk
    /// group (LC_Actor_AutoAttack.GetNormalSkill). A Thief with a dagger was told 1500001 "Normal Attack" and swung it
    /// rather than the dagger's 1005001 "Basic Attack" (shown 0/1), so players saw no Double Attack and lost its +45.
    /// </summary>
    [ClientDataFact]
    public async Task AThiefWithADaggerIsToldItsBasicAttackAndSwapsBackWithoutIt()
    {
        const int Dagger = 2010120, NormalAttack = 1500001, BasicAttack = 1005001, NoviceAttackGroup = 10000;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100");
        await Gm("alice", $"!item {Dagger} 1");
        var skills = app.Services.GetRequiredService<SkillService>();
        long guid = await Loop.InvokeAsync(() =>
        {
            var dagger = Session("alice").Items.Single(i => i.Entry == Dagger);
            (dagger.Attrs ??= new())[CharacterService.AttrAppraise] = 1;
            return Task.FromResult(dagger.Guid);
        });
        Task<DBSkillData> Built() => Loop.InvokeAsync(() => Task.FromResult(skills.Build(Session("alice"))));
        static int Learned(DBSkillData d, int group) => d.SkillAssignPlans[d.CurrentPlanIndex].SkillAssigns.TryGetValue(group, out var s) ? s.SkillLevel : 0;
        async Task<int> Switch(int plan)
        {
            await Loop.InvokeAsync(() => { Session("alice").SkillPlanSwitchUtc = DateTime.MinValue; return Task.CompletedTask; });
            await alice.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = plan });
            return (await Next(alice, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result;
        }

        Assert.Equal(NormalAttack, (await Built()).CurrentAtkSkillId);

        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)1, Opr = (EquipItemOprType)0 });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.HasCurrentAtkSkillId && m.CurrentAtkSkillId == BasicAttack);
        var held = await Built();
        Assert.Equal(BasicAttack, held.CurrentAtkSkillId);
        Assert.Equal(BasicAttack, held.SkillWheel.AtkSkillId);
        Assert.Equal(1, Learned(held, 10050)); // the skill window's Basic Attack
        Assert.Equal(0, Learned(held, NoviceAttackGroup)); // GetNormalSkill finds only one
        Assert.Equal(0, Learned(held, 15000));

        // Saved plans still switch: a stored 10050 is a normal attack, not a skill outside the tree.
        Assert.Equal(0, await Switch(1));
        Assert.Equal(0, await Switch(0));
        Assert.Equal(BasicAttack, (await Built()).CurrentAtkSkillId);

        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Opr = (EquipItemOprType)1 });
        await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser);
        await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.HasCurrentAtkSkillId && m.CurrentAtkSkillId == NormalAttack);

        await Gm("alice", "!job 1200"); // a Knight: JobAttackConfig names no weapon, so _kSkillNormalID stays
        Assert.Equal(1100001, (await Built()).CurrentAtkSkillId);
    }
}
