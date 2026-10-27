using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Mystical Amplification (1220301) puts its buff on at its template's attack point, 83 ms into the cast, and its
    /// template lets another skill interrupt it only at 333 ms (skill_12203 m_kAtkPoints [0.083], m_fInterruptTime 0.333).
    /// Reported: with a Fire Bolt cast right after it (Auto Hunt does), the SP and the cooldown were spent but no buff came.
    /// </summary>
    [ClientDataFact]
    public async Task MysticalAmplificationLandsWhenTheNextSpellFollowsAtOnce()
    {
        const int MysticalAmplification = 1220301, FireBolt = 1200401;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400");
        await Teach("alice", 12203); await Teach("alice", 12004);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 10_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = MysticalAmplification, TargetId = monster.Uid });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = FireBolt, TargetId = monster.Uid });
        var amplification = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        var bolt = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, x => x.SessionId != amplification.SessionId);
        Assert.Equal(0, amplification.Result);
        Assert.Equal(0, bolt.Result);
        await Task.Delay(500);

        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Buffs.Any(b => b.ConfigId == 1220101))),
            "Mystical Amplification's buff 1220101 is missing");
    }
}
