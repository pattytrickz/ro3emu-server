using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Magic Crasher (1220101, _iDmgType 1): "Deals Neutral Ranged Physical damage equal to MATK * ^{1}${2}%+${3}^{2}"
    /// (_iDescription 10110301152). Reported: MATK did not raise the hit, PATK did.
    /// </summary>
    [ClientDataFact]
    public async Task MagicCrasherScalesWithMatkNotPatk()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400"); await Teach("alice", 12201);
        _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianStableDamageRolls();
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 10_000_000; return Task.CompletedTask; });
        (long Id, double Value)[] extra = [];
        var own = Character.TalentAttributes;
        Character.TalentAttributes = s => [.. own?.Invoke(s) ?? [], .. extra];

        async Task<long> CrasherAsync()
        {
            await Loop.InvokeAsync(() =>
            {
                var s = Session("alice");
                s.SkillCds.Clear();
                s.SkillModeCds.Clear();
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1220101, TargetId = monster.Uid });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
            await Task.Delay(1500);
            return hit.Dsts.Single(d => d.DstID == monster.Uid).Damage;
        }

        long plain = await CrasherAsync();
        extra = [(AttributeId.Matk, 5000)];
        long moreMatk = await CrasherAsync();
        extra = [(AttributeId.Atk, 5000)];
        long morePatk = await CrasherAsync();
        Assert.True(moreMatk > plain + 1000, $"MATK +5000: {plain} -> {moreMatk}");
        Assert.True(morePatk < plain + 100, $"PATK +5000: {plain} -> {morePatk}");
    }
}
