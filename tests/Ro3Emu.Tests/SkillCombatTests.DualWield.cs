using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// JobConfig._kWeaponForm 3 (DoubleHand): an Assassin (5200: [2, 3]) holds a second Dagger in the off hand and swings
    /// the dual-dagger attack (JobAttackConfig [1, 1] 1005201); a Thief (5100: [1, 2]) keeps one weapon. Reported:
    /// "Assassin can't dual wield daggers".
    /// </summary>
    [ClientDataFact]
    public async Task AssassinsHoldADaggerInTheOffHandAndThievesDoNot()
    {
        const int Dagger = 2010120;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200");
        await Gm("alice", $"!item {Dagger} 2");
        var guids = await Loop.InvokeAsync(() =>
        {
            var daggers = Session("alice").Items.Where(i => i.Entry == Dagger).ToList();
            foreach (var d in daggers) (d.Attrs ??= new())[CharacterService.AttrAppraise] = 1;
            return Task.FromResult(daggers.Select(d => d.Guid).ToArray());
        });
        Assert.Equal(2, guids.Length);
        async Task<int> Wear(long guid, int pos)
        {
            await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)pos, Opr = (EquipItemOprType)0 });
            return (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result;
        }
        Assert.Equal(0, await Wear(guids[0], 1));
        Assert.Equal(0, await Wear(guids[1], 2));
        var skills = _app.Services.GetRequiredService<SkillService>();
        await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            Assert.Equal(1, s.Items.Single(i => i.Guid == guids[0]).Grid);
            Assert.Equal(2, s.Items.Single(i => i.Guid == guids[1]).Grid);
            Assert.Equal(1005201, skills.NormalAttack(5200, 1, 1));
            return Task.CompletedTask;
        });

        await Gm("alice", "!job 5100");
        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = guids[1], Opr = (EquipItemOprType)1 });
        await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser);
        Assert.Equal(0, await Wear(guids[1], 2)); // a Thief's off-hand request lands in the main hand
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(1, Session("alice").Items.Single(i => i.Guid == guids[1]).Grid);
            Assert.DoesNotContain(Session("alice").Items, i => i.Grid == 2);
            return Task.CompletedTask;
        });
    }
}
