using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Juicy (22 Oct): a new Archer with a bow took the bar's attack off and found "no basic attack button in skills or
    /// anywhere", so could not attack. The bar's Normal Attack (1000001) was shown 0/1 in the skill window: the plan
    /// zeroes every normal-attack group but the held weapon's, and a bow's Arrow Attack (1300001) is not one
    /// (_iIsNormalAtk 0), so a bow Archer had no learned normal attack at all.
    /// </summary>
    [ClientDataFact]
    public async Task ABowArchersNormalAttackStaysLearnedAndGoesBackOnTheBar()
    {
        const int Bow = 2110120, ArrowAttack = 1300001, ArrowAttackGroup = 13000, NormalAttack = 1000001, NormalAttackGroup = 10000;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100");
        await Gm("alice", $"!item {Bow} 1");
        var skills = _app.Services.GetRequiredService<SkillService>();
        long guid = await Loop.InvokeAsync(() =>
        {
            var bow = Session("alice").Items.Single(i => i.Entry == Bow);
            (bow.Attrs ??= new())[CharacterService.AttrAppraise] = 1;
            return Task.FromResult(bow.Guid);
        });
        Task<DBSkillData> Built() => Loop.InvokeAsync(() => Task.FromResult(skills.Build(Session("alice"))));
        static int Learned(DBSkillData d, int group) => d.SkillAssignPlans[d.CurrentPlanIndex].SkillAssigns.TryGetValue(group, out var s) ? s.SkillLevel : 0;
        async Task<int> Load(SkillLoadOperation op, int group, int slot)
        {
            await alice.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
            {
                OpType = op,
                IsManual = true, SocketNum = slot, Info = new SkillData { SkillId = group, SkillLevel = 1 }
            });
            return (await Next(alice, SkillService.SkillLoad, SkillLoadResp.Parser)).Result;
        }

        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)1, Opr = (EquipItemOprType)0 });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.HasCurrentAtkSkillId && m.CurrentAtkSkillId == ArrowAttack);
        var held = await Built();
        Assert.Equal(NormalAttack, held.SkillWheel.WhellSkills[1].SkillConfigId);
        Assert.Equal(1, Learned(held, ArrowAttackGroup));
        Assert.Equal(1, Learned(held, NormalAttackGroup)); // shown 0/1, it could not be dragged back

        Assert.Equal(0, await Load(SkillLoadOperation.SkillLoadOpUnload, NormalAttackGroup, 0));
        Assert.Equal(1, Learned(await Built(), NormalAttackGroup));
        Assert.Equal(0, await Load(SkillLoadOperation.SkillLoadOpLoad, NormalAttackGroup, 0));
        Assert.Equal(0, await Load(SkillLoadOperation.SkillLoadOpLoad, ArrowAttackGroup, 2));
        var bar = (await Built()).SkillWheel.WhellSkills;
        Assert.Equal(NormalAttack, bar[1].SkillConfigId);
        Assert.Equal(ArrowAttack, bar[3].SkillConfigId);
    }
}
