using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Heal (1140501) heals 139 ms after its cast ends, and its template lets another skill interrupt it only after
    /// 306 ms (skill_14007 m_kAtkPoints [2.167], m_fInterruptTime 2.367, chant until 2.0 of 2.6). Reported: Heal cast
    /// again right away cancelled the first one before its heal. The same skill on the same target before its impact
    /// joins the running cast (CombatService.JoinsRunningCast), so the second request is answered with the first's session.
    /// </summary>
    [ClientDataFact]
    public async Task HealLandsWhenTheNextHealFollowsRightAfterItsCast()
    {
        const int Heal = 1140501;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1400");
        await Teach("alice", 11405);
        var s = Session("alice");
        long self = s.Role!.Id;
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<CharacterService>().SetHpSpAsync(s, 10, null));

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = Heal, TargetId = self });
        var first = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, first.Result);
        int chant = await Loop.InvokeAsync(() => Task.FromResult(s.SkillCast!.ChantMs));
        await Task.Delay(Math.Max(chant, 400) + 20); // past the chant and the 500 ms cooldown, before the heal
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = Heal, TargetId = self });
        var second = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, second.Result);
        Assert.Equal(first.SessionId, second.SessionId);

        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == first.SessionId);
    }

    /// <summary>
    /// Sonic Blow (1510205) hits 333 ms into its 1200 ms action and can be interrupted only from 667 ms
    /// (skill_15102 m_kAtkPoints [0.333], m_fInterruptTime 0.667). A Sonic Blow sent 250 ms after the first, within
    /// the cast grace of its hit, leaves the first one's hit in place.
    /// </summary>
    [ClientDataFact]
    public async Task SonicBlowReplacedJustBeforeItsHitStillHits()
    {
        const int SonicBlow = 1510205;
        await using var alice = await InWorld("Alice");
        var target = await SonicBlowReady();

        var first = await SonicBlowAt(alice, target);
        await Task.Delay(250);
        await SonicBlowAt(alice, target);

        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == first.SessionId && h.SkillId == SonicBlow);
    }

    /// <summary>Pressing Sonic Blow again and again at once, long before its hit, still deals one hit: pressing it faster deals no more.</summary>
    [ClientDataFact]
    public async Task SonicBlowPressedRepeatedlyBeforeItsHitHitsOnce()
    {
        await using var alice = await InWorld("Alice");
        var target = await SonicBlowReady();

        var first = await SonicBlowAt(alice, target);
        for (int i = 0; i < 3; i++) await SonicBlowAt(alice, target);
        await Task.Delay(1000);

        var hits = alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Where(h => h.SkillId == 1510205).ToList();
        Assert.Single(hits);
        Assert.Equal(first.SessionId, hits[0].SessionId);
    }

    private async Task<MonsterUnit> SonicBlowReady()
    {
        const int Katar = 2120120;
        await Gm("alice", "!job 5200");
        await Gm("alice", $"!item {Katar} 1");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            var katar = s.Items.Single(i => i.Entry == Katar);
            katar.Pack = 2;
            katar.Grid = 1;
            s.Progress!.Skills[15101] = 5;
            s.Progress.Skills[15102] = 5;
            target.Hp = 1_000_000;
            return Task.CompletedTask;
        });
        return target;
    }

    private async Task<CastSkillRsp> SonicBlowAt(BotClient alice, MonsterUnit target)
    {
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510205, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        return accepted;
    }
}
