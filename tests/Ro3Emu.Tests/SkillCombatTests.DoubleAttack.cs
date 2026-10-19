using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Double Attack 10 (SkillConfig._kDescData ["10","1","50","5"]): with a Dagger, a Basic Attack has a 10% chance
    /// of 1 extra hit of PATK * 50%; with a Katar, SpecialEffectConfig op [16, 12, 111, 500] adds CRIT 500 (shown 5).
    /// Players reported the passive did nothing.
    /// </summary>
    [ClientDataFact]
    public async Task DoubleAttackAddsItsDaggerHitToBasicAttacksAndItsKatarCrit()
    {
        const int Dagger = 2010120, Katar = 2120120, BladeSmash = 1509901, DoubleAttack = 15002;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100");
        await Gm("alice", $"!item {Dagger} 1");
        await Gm("alice", $"!item {Katar} 1");
        var combat = app.Services.GetRequiredService<CombatService>();
        var skills = app.Services.GetRequiredService<SkillService>();
        var character = app.Services.GetRequiredService<CharacterService>();
        var data = app.Services.GetRequiredService<GameData>();
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            using var held = await CharacterService.Lock(s);
            long Extra(int skillRow) => combat.ExtraHitDamage.Sum(f => f(s, target, data.Row("SkillConfig", skillRow)!.Value, 100));
            void Hold(int entry)
            {
                foreach (var worn in s.Items.Where(i => i.Grid == 1)) worn.Grid = 0;
                var item = s.Items.First(i => i.Entry == entry);
                item.Pack = 2;
                item.Grid = 1;
            }

            Hold(Dagger);
            int daggerAttack = skills.NormalAttack(5100, 1, 0);
            combat.DoubleAttackRoll = _ => true;
            Assert.Equal(0, Extra(daggerAttack)); // not learned
            s.Progress!.Skills[DoubleAttack] = 10;
            long patk = character.Stats(s)[103];
            Assert.True(patk > 0);
            Assert.Equal((long)Math.Floor(patk * 50 / 100d), Extra(daggerAttack));
            Assert.Equal(2, combat.TakeDoubleAttackSplit(s)); // shown as two numbers, like classic RO
            Assert.Equal(0, combat.TakeDoubleAttackSplit(s));
            Assert.Equal(0, Extra(BladeSmash)); // only Basic Attacks
            int? rolled = null;
            combat.DoubleAttackRoll = chance => { rolled = chance; return false; };
            Assert.Equal(0, Extra(daggerAttack));
            Assert.Equal(10, rolled);

            Hold(Katar);
            combat.DoubleAttackRoll = _ => true;
            Assert.Equal(0, Extra(skills.NormalAttack(5100, 12, 0))); // the hit is the Dagger's
            long crit = character.Stats(s)[111];
            s.Progress.Skills[DoubleAttack] = 0;
            Assert.Equal(500, crit - character.Stats(s)[111]);
            Hold(Dagger);
            s.Progress.Skills[DoubleAttack] = 10;
            long daggerCrit = character.Stats(s)[111];
            s.Progress.Skills[DoubleAttack] = 0;
            Assert.Equal(0, daggerCrit - character.Stats(s)[111]); // the CRIT is the Katar's
        });
    }
}
