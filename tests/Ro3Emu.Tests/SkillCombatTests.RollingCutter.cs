using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Rolling Cutter Lv 1 (1530301, _iRangeType 1, _iRelease 2 FixCircle, _kRange [500], _iTargetMax 3): "Quickly spins
    /// in place, dealing ... to up to ${2} enemies within a ${1}-meter radius of You" (_kDescData ${1} 5, ${2} 3). Four
    /// enemies stand within 5 m and one 7 m away; the cast names no target. The three nearest are struck, the fourth and
    /// the far one are not. Reported: the animation plays but nothing takes damage.
    /// </summary>
    [ClientDataFact]
    public async Task RollingCutterStrikesTheNearestEnemiesAroundTheCaster()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15303);
        var monsters = new List<MonsterUnit>();
        for (int i = 0; i < 5; i++) monsters.Add(await Target("alice", stationary: true));
        var start = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            // The map's own monsters are moved out of the circle: a roaming one would be among the nearest.
            var map = _app.Services.GetRequiredService<World>().Map(Session("alice").WorldScene);
            foreach (var other in map.Units.OfType<MonsterUnit>().Where(m => !monsters.Contains(m)))
            {
                other.Moving = false; other.X = start.X + 100; other.Z = start.Z + 100;
            }
            (float X, float Z)[] offsets = [(0, 1), (1.5f, 0), (0, -2), (-4, 0), (0, 7)];
            for (int i = 0; i < monsters.Count; i++)
            {
                var m = monsters[i];
                m.Hp = 1_000_000; m.Moving = false;
                m.X = start.X + offsets[i].X; m.Y = start.Y; m.Z = start.Z + offsets[i].Z;
            }
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530301 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        var struck = hit.Dsts.Where(d => d.Damage > 0).Select(d => d.DstID).ToHashSet();
        Assert.Equal(monsters.Take(3).Select(m => m.Uid).ToHashSet(), struck);
    }
}
