using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// "The Fate of an Egg Thief" (10102100) ends with "Look, you picked up a Card: Poring!" and the card-insert guide,
    /// but no card came. The story Poring 5001049 drops pool 241949 = DropGroupConfig 144, Card: Poring with
    /// _iQuestLimit 10102100, and quest-limited rows were never rolled. They now drop while that quest runs.
    /// </summary>
    [ClientDataFact]
    public async Task TheStoryPoringDropsItsCardOnlyWhileItsQuestRuns()
    {
        const int StoryPoring = 5001049, PoringCard = 1420300, Quest = 10102100;
        await using var alice = await InWorld("Alice");
        async Task<int> KillAndCountCards()
        {
            var target = await Target("alice", stationary: true, npcId: StoryPoring);
            await Loop.InvokeAsync(() => { target.Hp = 1; return Task.CompletedTask; });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            for (int i = 0; i < 50 && !target.Dead; i++) await Task.Delay(100);
            Assert.True(target.Dead);
            await Task.Delay(500);
            return await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Items.Where(i => i.Entry == PoringCard).Sum(i => i.Stack)));
        }

        Assert.Equal(0, await KillAndCountCards());
        await Gm("alice", $"!quest accept {Quest}");
        await Loop.InvokeAsync(() => { Session("alice").Attack = null; return Task.CompletedTask; });
        await Task.Delay(1300);
        Assert.Equal(1, await KillAndCountCards());
    }
}
