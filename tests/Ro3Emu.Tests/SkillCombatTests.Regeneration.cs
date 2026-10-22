using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task TensionRelaxHealsFiveTimesAndShowsEachHealDuringCombat()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1300");
        await Gm("alice", "!lvl 40");
        await Teach("alice", 11207);
        await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            s.StatusBits |= 1L << 15;
            s.LastCombatMs = MonsterService.NowMs + 60000; // isolate the skill from natural regeneration
            await Character.SetHpSpAsync(s, 1, null);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1120701 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        for (int i = 0; i < 5; i++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1120701);
            var heal = Assert.Single(hit.Dsts);
            Assert.Equal(DamageType.Heal, heal.DamageType);
            Assert.Equal((long)alice.Self!.Uuid, heal.DstID);
            Assert.Equal(350, heal.Damage);
        }
        Assert.Equal(1751, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000])));
        await Task.Delay(1600);
        Assert.Equal(1751, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000])));
    }

    [ClientDataFact]
    public async Task TensionRelaxCapsHealingAndStopsWhenItsBuffIsRemoved()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1300");
        await Gm("alice", "!lvl 40");
        await Teach("alice", 11207);
        await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            s.StatusBits |= 1L << 15;
            s.LastCombatMs = MonsterService.NowMs + 60000;
            await Character.SetHpSpAsync(s, Character.Stats(s)[101] - 10, null);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1120701 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var heal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1120701);
        Assert.Equal(10, Assert.Single(heal.Dsts).Damage);
        await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            await _app.Services.GetRequiredService<BuffService>().RemoveAsync(s, Assert.Single(s.Buffs, b => b.ConfigId == 1120701));
            await Character.SetHpSpAsync(s, 1, null);
        });
        await Task.Delay(2200);
        Assert.Equal(1, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000])));
    }

    [ClientDataFact]
    public async Task IncreaseRecuperativePowerAddsItsConfiguredRecoveryOutOfCombat()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        await Gm("alice", "!lvl 40");
        var combat = _app.Services.GetRequiredService<CombatService>();
        long plain = await Loop.InvokeAsync(() => Task.FromResult(combat.RegenAmount(Session("alice"), Character.Stats(Session("alice"))).Hp));
        await Teach("alice", 11006);
        long boosted = await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            var stats = Character.Stats(s);
            long value = combat.RegenAmount(s, stats).Hp;
            // Existing percentage recovery can change the rounding by one HP.
            Assert.InRange(value - plain, 55 + stats[101] / 100, 56 + stats[101] / 100);
            return Task.FromResult(value);
        });
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), 1, null));
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline && await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000])) == 1)
            await Task.Delay(50);
        Assert.Equal(1 + boosted, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000])));
    }
}
