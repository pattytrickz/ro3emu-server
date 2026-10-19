using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Players held the attack key and the normal attack, which cancels itself (_bCancelCast 1) and lands its damage
    /// early in the swing, hit once per request rather than once per swing time (1005001: 1200 ms at base ASPD).
    /// Every request is still accepted (the real client asks for each swing itself); an impact sooner than
    /// NormalAttackMinGap swing times deals nothing, and 0 only measures.
    /// </summary>
    [ClientDataFact]
    public async Task ARepeatedNormalAttackIsAcceptedButHitsOncePerSwingTime()
    {
        const int Dagger = 2010120, BasicAttack = 1005001, Presses = 8;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100");
        await Gm("alice", $"!item {Dagger} 1");
        var combat = app.Services.GetRequiredService<CombatService>();
        var target = await Target("alice", stationary: true);
        int hits = 0;
        Func<GameSession, MonsterUnit, System.Text.Json.JsonElement, long, Task> count = (s, m, row, amount) =>
        {
            if (m == target && amount > 0) hits++;
            return Task.CompletedTask;
        };
        await Loop.InvokeAsync(() =>
        {
            var dagger = Session("alice").Items.Single(i => i.Entry == Dagger);
            dagger.Pack = 2;
            dagger.Grid = 1;
            target.Hp = 1_000_000; // survives every swing
            combat.DealtAsync.Add(count);
            combat.PacingCommand("reset");
            return Task.CompletedTask;
        });
        async Task<int> Spam(double minGap)
        {
            await Loop.InvokeAsync(() => { combat.NormalAttackMinGap = minGap; hits = 0; return Task.CompletedTask; });
            for (int i = 0; i < Presses; i++) // a press every 300 ms: each impact (~240 ms in) lands before the next press
            {
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = BasicAttack, TargetId = target.Uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result); // never refused
                await Task.Delay(300);
            }
            await Loop.InvokeAsync(() => { Session("alice").Attack = null; return Task.CompletedTask; });
            return await Loop.InvokeAsync(() => Task.FromResult(hits));
        }
        try
        {
            int measured = await Spam(0);
            Assert.True(measured >= Presses - 2, $"measure only: {measured} hits"); // the exploit: a hit per press
            await Task.Delay(1500);
            int paced = await Spam(0.7);
            Assert.InRange(paced, 1, Math.Min(4, measured / 2)); // ~2.6 s of presses at one hit per 0.84 s
            string player = (await Loop.InvokeAsync(() => Task.FromResult(combat.PacingCommand("")))).Split('\n').Single(l => l.StartsWith("player "));
            Assert.False(player.EndsWith("| 0"), player);
        }
        finally
        {
            await Loop.InvokeAsync(() => { combat.DealtAsync.Remove(count); combat.NormalAttackMinGap = 0; return Task.CompletedTask; });
        }
    }
}
