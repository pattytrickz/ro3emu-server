using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Myriad Stars Chase the Moon Lv 1 (1332001, _iRelease 6 Rect360, _kRange [1800, 600], _iTargetMax 10): "Fires a
    /// hurricane Arrow in the designated direction across a ${1}-meter by ${2}-meter area" (_kDescData 18, 4), and the
    /// show 13320 draws an arrow indicator m_kSkillIndicatorParam [4, 18]: 4 m wide, 18 m long, ahead of the caster. A
    /// Rect360 row is _kRange [length, width], as the boss skills read it. The server took it as an 18 m wide, 6 m deep
    /// box around the caster, so an enemy at range was missed. Reported: the animation plays but nothing takes damage.
    /// </summary>
    [ClientDataFact]
    public async Task MyriadStarsChaseTheMoonStrikesTheEnemiesAheadInTheLine()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 3400"); await Teach("alice", 13320);
        var monsters = new List<MonsterUnit>();
        for (int i = 0; i < 4; i++) monsters.Add(await Target("alice", stationary: true));
        var start = Session("alice").Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            // The map's own monsters are moved away: a roaming one could stand in the line.
            var map = _app.Services.GetRequiredService<World>().Map(Session("alice").WorldScene);
            foreach (var other in map.Units.OfType<MonsterUnit>().Where(m => !monsters.Contains(m)))
            {
                other.Moving = false; other.X = start.X + 100; other.Z = start.Z + 100;
            }
            // Face 90 is +X. Near, far end of the line, behind the caster, and 5 m beside the line.
            (float X, float Z)[] offsets = [(8, 0), (16, 1), (-8, 0), (8, 5)];
            for (int i = 0; i < monsters.Count; i++)
            {
                var m = monsters[i];
                m.Hp = 1_000_000; m.Moving = false;
                m.X = start.X + offsets[i].X; m.Y = start.Y; m.Z = start.Z + offsets[i].Z;
            }
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1332001, Face = 90 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        var struck = hit.Dsts.Where(d => d.Damage > 0).Select(d => d.DstID).ToHashSet();
        HashSet<long> inLine = [.. monsters.Take(2).Select(m => m.Uid)];
        Assert.Equal(inLine, struck);
    }
}
