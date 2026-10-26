using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Envenomed Blade Lv 1 (1510501): "Then Launches a poisoned blade at a random enemy within ${4} meters of the target
    /// ... up to ${7} times" (_kDescData ${4} 3, ${7} 3). Four enemies stand 1.5 m from the target and one 6 m away:
    /// three of the near ones are struck, the far one never. Reported: only the first blade hit.
    /// </summary>
    [ClientDataFact]
    public async Task EnvenomedBladeRicochetsToEnemiesNearItsTarget()
    {
        var (near, far) = await CastEnvenomedBlade(extraBounce: false);
        Assert.Equal(3, near);
        Assert.False(far);
    }

    /// <summary>The talent BuffConfig 1523001 [0, 15105, 31 (SkillLogicData), add, 1, 0]: "[Envenomed Blade]'s ricochet
    /// effect gains an Extra ${1} bounce."</summary>
    [ClientDataFact]
    public async Task EnvenomedBladeTalentAddsABounce()
    {
        var (near, far) = await CastEnvenomedBlade(extraBounce: true);
        Assert.Equal(4, near);
        Assert.False(far);
    }

    private async Task<(int Near, bool Far)> CastEnvenomedBlade(bool extraBounce)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15105);
        if (extraBounce)
            await Loop.InvokeAsync(() =>
            {
                Session("alice").TalentSkillModifiers = [(0, 0, new SkillParameterRules.Modifier(15105, 31, 1, 1, 0))];
                return Task.CompletedTask;
            });
        var main = await Target("alice", stationary: true);
        var others = new List<MonsterUnit>();
        for (int i = 0; i < 5; i++) others.Add(await Target("alice", stationary: true));
        var start = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            void Place(MonsterUnit m, float dx, float dz)
            {
                m.Hp = 1_000_000; m.Moving = false;
                m.X = start.X + dx; m.Y = start.Y; m.Z = start.Z + dz;
            }
            Place(main, 0, 3);
            (float X, float Z)[] offsets = [(1.5f, 3), (-1.5f, 3), (0, 4.5f), (0, 1.5f), (0, 9)];
            for (int i = 0; i < others.Count; i++) Place(others[i], offsets[i].X, offsets[i].Z);
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510501, TargetId = main.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == main.Uid));
        int expected = extraBounce ? 4 : 3;
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline && await Struck() < expected) await Task.Delay(50);
        await Task.Delay(300);
        return (await Struck(), await Loop.InvokeAsync(() => Task.FromResult(others[4].Hp < 1_000_000)));

        Task<int> Struck() => Loop.InvokeAsync(() => Task.FromResult(others.Take(4).Count(m => m.Hp < 1_000_000)));
    }
}
