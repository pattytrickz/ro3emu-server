using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// A player's control and slow debuffs only showed their icon on a monster (#221: "Frozen, Stunned, Petrified or
    /// Immobilized enemies just slide towards you and can still hit", "Slows dont have any effects either"). A skill's
    /// own Stun row (BuffConfig 1210101: BuffTagConfig tag 13 only) now forbids what SpecialStatusConfig 18 lists, and
    /// Slow (902601: per_speed -5000) halves the monster's speed.
    /// </summary>
    [ClientDataFact]
    public async Task APlayersStunAndSlowActOnAMonster()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice");
        await Loop.InvokeAsync(async () =>
        {
            var buffs = _app.Services.GetRequiredService<BuffService>();
            long self = Session("alice").Role!.Id;
            int template = target.Template.SpeedCm;
            await buffs.AddSupportAsync(target, 1210101, 3000, self);
            Assert.True(buffs.UnitForbidden(target, ForbiddenState.Walk));
            Assert.True(buffs.UnitForbidden(target, ForbiddenState.NormalAttack));
            await buffs.AddSupportAsync(target, 902601, 3000, self);
            Assert.InRange(target.SpeedCm(MonsterService.NowMs), template / 2 - 1, template / 2 + 1);
        });
    }
}
