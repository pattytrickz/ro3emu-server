using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// "Monsters Attack" (story dungeon 1021301) spawns its waves 22-27 m from the entry while their AI leash is 18-20 m:
    /// the engagement fell away on the next tick and the waves stood still. A wave sent at the players ignores its leash.
    /// </summary>
    [ClientDataFact]
    public async Task AWaveSentAtThePlayerKeepsChasingBeyondItsLeash()
    {
        await using var alice = await InWorld("Alice");
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        async Task<MonsterUnit> Far() => await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            var unit = await monsters.SpawnSingleAsync(s.SceneEntry, 5000028,
                new MapPoint(s.Position.X + 25, s.Position.Y, s.Position.Z, 0), s.InstanceId);
            Assert.NotNull(unit);
            unit!.Hp = 1_000_000;
            return unit;
        });
        var wave = await Far();
        var stray = await Far();
        await Loop.InvokeAsync(() =>
        {
            Assert.True(MonsterService.Distance(wave.Home.X, wave.Home.Z, Session("alice").Position.X, Session("alice").Position.Z) > wave.Template.LeashDistance);
            wave.IgnoresLeash = true;
            monsters.Engage(wave, Session("alice"));
            monsters.Engage(stray, Session("alice"));
            return Task.CompletedTask;
        });
        await Task.Delay(1500);
        await Loop.InvokeAsync(() =>
        {
            Assert.Same(Session("alice"), wave.Target);
            Assert.Null(stray.Target); // an ordinary monster still gives up beyond its leash
            return Task.CompletedTask;
        });
    }
}
