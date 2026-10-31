using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Chain Lightning Lv 1 (1231301, _iRangeType 2, _iRelease 1, _kRange [0], _iTargetMax 5): "dealing ... to the
    /// designated ST enemy within ${1} meters and up to ${3} Other enemies within ${2} meters of the target"
    /// (_kDescData ${2} 5, ${3} 4). Five enemies stand within 5 m of the target and one 7 m away: the target and the four
    /// nearest are struck. Reported: only the target took damage.
    /// </summary>
    [ClientDataFact]
    public async Task ChainLightningStrikesTheEnemiesAroundItsTarget()
    {
        await using var alice = await InWorld("Alice");
        var (main, others) = await ChainLightningTargets();
        var hit = await CastChainLightning(alice, main);
        var struck = hit.Dsts.Where(d => d.Damage > 0).Select(d => d.DstID).ToHashSet();
        HashSet<long> expected = [main.Uid, .. others.Take(4).Select(m => m.Uid)];
        Assert.Equal(expected, struck);
    }

    /// <summary>The target walks 2 m away during the chant (_iChantVar 1000): the lightning still strikes it where it
    /// now stands. Reported: "miscast 3 times in a row quite often".</summary>
    [ClientDataFact]
    public async Task ChainLightningFollowsATargetThatMovedDuringTheChant()
    {
        await using var alice = await InWorld("Alice");
        var (main, _) = await ChainLightningTargets();
        var hit = await CastChainLightning(alice, main, moveTarget: true);
        Assert.Contains(hit.Dsts, d => d.DstID == main.Uid && d.Damage > 0);
    }

    private async Task<(MonsterUnit Main, List<MonsterUnit> Others)> ChainLightningTargets()
    {
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400"); await Teach("alice", 12313);
        var main = await Target("alice", stationary: true);
        var others = new List<MonsterUnit>();
        for (int i = 0; i < 6; i++) others.Add(await Target("alice", stationary: true));
        var start = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            // The map's own monsters are moved away: a roaming one would be among the nearest.
            var map = _app.Services.GetRequiredService<World>().Map(Session("alice").WorldScene);
            foreach (var other in map.Units.OfType<MonsterUnit>().Where(m => m != main && !others.Contains(m)))
            {
                other.Moving = false; other.X = start.X + 100; other.Z = start.Z + 100;
            }
            void Place(MonsterUnit m, float dx, float dz)
            {
                m.Hp = 1_000_000; m.Moving = false;
                m.X = start.X + dx; m.Y = start.Y; m.Z = start.Z + dz;
            }
            Place(main, 0, 6);
            (float X, float Z)[] offsets = [(1, 6), (-2, 6), (0, 9), (3, 8), (0, 1.5f), (0, 13)];
            for (int i = 0; i < others.Count; i++) Place(others[i], offsets[i].X, offsets[i].Z);
            return Task.CompletedTask;
        });
        return (main, others);
    }

    private async Task<SkillHitInfo> CastChainLightning(BotClient alice, MonsterUnit main, bool moveTarget = false)
    {
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231301, TargetId = main.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        if (moveTarget) await Loop.InvokeAsync(() => { main.X += 2; return Task.CompletedTask; });
        return await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
    }
}
