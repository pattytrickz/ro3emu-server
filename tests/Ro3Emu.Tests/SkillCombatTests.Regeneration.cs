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
            s.LastCombatMs = MonsterService.NowMs + 60000;
            s.NoRegenUntilMs = MonsterService.NowMs + 60000; // isolate the skill from natural regeneration
            await Character.SetHpSpAsync(s, 1, null);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1120701 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        // Upstream's periodic buff heal (BuffService.Periodic): one 350 tick a second while the five-second buff lives,
        // each shown as its own heal number.
        int heals = 0;
        var until = DateTime.UtcNow.AddSeconds(7);
        while (DateTime.UtcNow < until)
        {
            SkillHitInfo hit;
            try { hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DamageType == DamageType.Heal)); }
            catch (TimeoutException) { break; }
            var heal = Assert.Single(hit.Dsts);
            Assert.Equal((long)alice.Self!.Uuid, heal.DstID);
            Assert.Equal(350, heal.Damage);
            heals++;
            if (heals == 5) break;
        }
        Assert.InRange(heals, 4, 5);
        long hp = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000]));
        Assert.Equal(1 + 350L * heals, hp);
        await Task.Delay(1600);
        Assert.Equal(hp, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2000])));
    }

    /// <summary>
    /// AttributeConfig 117/118 (SP Recovery on the character panel) is "Recovers ${1} MP every 5 sec." with no combat
    /// condition; only 120/121 say "while out of combat". A caster at 0 SP in a fight got none of it back (#general, 27 Oct).
    /// </summary>
    [ClientDataFact]
    public async Task SpRecoveryRestoresSpEveryFiveSecondsInCombat()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100");
        await Gm("alice", "!lvl 40");
        var combat = _app.Services.GetRequiredService<CombatService>();
        long sp = await Loop.InvokeAsync(() => Task.FromResult(combat.RegenAmount(Session("alice"), Character.Stats(Session("alice"))).Sp));
        await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            s.StatusBits |= 1L << 15; // fighting, and staying so for the whole test
            s.LastCombatMs = MonsterService.NowMs + 60000;
            await Character.SetHpSpAsync(s, null, 1);
        });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(7);
        while (DateTime.UtcNow < deadline && await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2001])) == 1)
            await Task.Delay(50);
        Assert.Equal(1 + sp, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session("alice"))[2001])));
        Assert.NotEqual(0, await Loop.InvokeAsync(() => Task.FromResult(Session("alice").StatusBits & (1L << 15))));
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
            s.NoRegenUntilMs = MonsterService.NowMs + 60000;
            await Character.SetHpSpAsync(s, Character.Stats(s)[101] - 10, null);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1120701 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var heal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DamageType == DamageType.Heal));
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
