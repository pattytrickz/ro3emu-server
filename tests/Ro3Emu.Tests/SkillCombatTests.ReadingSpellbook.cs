using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Comet: "While in the [Reading] state, consumes ${5} stacks of [Book Page] to cast this skill 1 Extra time." Its
    /// _kSkillHighParam "0:1231001:1&amp;2:240001:5" is the Reading buff and 5 of the Book Page meter (EnergyConfig 240001,
    /// 8 at the start); its Spellbook buff 1231501 names the copy 12325 (Comet (Copy) 1232501). Reported: Reading had no
    /// effect on Jack Frost, Crimson Rock, Earth Strain, Thunderclap, Tetra Vortex and Comet.
    /// </summary>
    [ClientDataFact]
    public async Task ReadingSpellbookCastsCometOneExtraTimeForFivePages()
    {
        const int ReadingSpellbook = 1231901, Comet = 1231501, CometCopy = 1232501, BookPages = 240001;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400");
        await Teach("alice", 12319); await Teach("alice", 12315);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 50_000_000; return Task.CompletedTask; });

        async Task<CastSkillRsp> CastAsync(int skill)
        {
            await Loop.InvokeAsync(() =>
            {
                var s = Session("alice");
                s.SkillCds.Clear();
                s.SkillModeCds.Clear();
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = monster.Uid, TargetPos = monster.Pos.Clone() });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            return accepted;
        }
        int Copies() => alice.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == CometCopy);
        Task<long> PagesAsync() => Loop.InvokeAsync(() => Task.FromResult(Session("alice").Energy[BookPages].Value));

        // Without Reading, Comet is cast once.
        var plain = await CastAsync(Comet);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == plain.SessionId);
        await Task.Delay(500);
        Assert.Equal(0, Copies());

        await CastAsync(ReadingSpellbook);
        await Task.Delay(800);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Buffs.Any(b => b.ConfigId == 1231001))));
        long before = await PagesAsync();
        Assert.True(before >= 5, $"{before} Book Pages");

        var read = await CastAsync(Comet);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == read.SessionId);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == CometCopy);
        Assert.Equal(1, Copies());
        // The meter regains 1 page every 5 s, so allow one.
        Assert.InRange(before - await PagesAsync(), 4, 5);

        // Fewer than 5 pages left: no extra cast. Comet's action (1 s) ends first.
        await Task.Delay(1200);
        await Loop.InvokeAsync(() => { Session("alice").Energy[BookPages].Value = 2; return Task.CompletedTask; });
        var starved = await CastAsync(Comet);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == starved.SessionId);
        await Task.Delay(500);
        Assert.Equal(1, Copies());
    }
}
