using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// JobAttackConfig: the normal attack the client casts depends on the job and the weapons held. A Magician with a
    /// rod (type 10) swings 1299901, a Lord Knight 1001101, a Thief with one dagger 1005001; JobConfig._kSkillNormalID
    /// (1200001, 1100001, 1500001) is only part of it, and 1200001 has no SkillConfig row at all.
    /// </summary>
    [ClientDataTheory]
    [InlineData(2100, 10, 0, 1299901)] // Magician, rod
    [InlineData(2100, 0, 0, 1000001)]  // Magician, bare hands
    [InlineData(1300, 0, 0, 1001101)]  // Lord Knight: one attack for anything
    [InlineData(5100, 1, 0, 1005001)]  // Thief, one dagger
    [InlineData(5100, 1, 1, 1005201)]  // Thief, two daggers
    [InlineData(5100, 0, 0, 1500001)]  // Thief, bare hands: the {} row
    public void TheNormalAttackFollowsTheWeaponsHeld(int career, int mainType, int offType, int expected)
    {
        var skills = app.Services.GetRequiredService<SkillService>();
        Assert.Equal(expected, skills.NormalAttack(career, mainType, offType));
        Assert.True(skills.IsNormalAttack(career, expected));
    }

    /// <summary>A job's JobAttackConfig attacks need no learning: they were refused with SkillNotLearned (3002).</summary>
    [ClientDataTheory]
    [InlineData(2100, 1299901)]
    [InlineData(1300, 1001101)]
    [InlineData(5300, 1005001)]
    public async Task AJobsWeaponNormalAttackIsAcceptedWithoutLearning(int career, int attack)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", $"!job {career}");
        var target = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = attack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }
}
