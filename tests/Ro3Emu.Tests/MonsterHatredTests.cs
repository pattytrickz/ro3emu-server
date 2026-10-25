using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// A monster several players fight keeps them all on its hatred list (AIConfig _iHatredType). Losing its current target
/// turns it to another attacker; it leaves combat, walks home and refills its HP only when nobody on the list is left.
/// </summary>
public sealed class MonsterHatredTests
{
    [Fact]
    public async Task LosingItsTargetTurnsTheMonsterToTheNextAttackerInsteadOfResetting()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = SyntheticData.Dir);
        await using var aliceBot = await host.InWorldAsync("Alice");
        await using var bobBot = await host.InWorldAsync("Bob");
        var loop = host.Service<GameLoop>();
        var monsters = host.Service<MonsterService>();
        var sessions = host.Service<SessionRegistry>();
        GameSession Session(string account) => sessions.All().Single(s => s.Account == account);

        var monster = await loop.InvokeAsync(async () =>
        {
            var alice = Session("alice");
            var template = monsters.Template(16320001) ?? throw new InvalidOperationException("Missing synthetic monster.");
            var unit = await monsters.SpawnSingleAsync(alice.SceneEntry, 16320001,
                new MapPoint(alice.Position.X, alice.Position.Y, alice.Position.Z, 0), alice.InstanceId,
                customTemplate: template with { SpeedCm = 250, RoamDistance = 0 })
                ?? throw new InvalidOperationException("Synthetic monster could not spawn.");
            monsters.Engage(unit, alice);
            monsters.Engage(unit, Session("bob")); // Bob's hit draws its aggro
            unit.NextSwingMs = long.MaxValue;
            unit.Hp = unit.Template.MaxHp / 2;
            return unit;
        });

        await loop.InvokeAsync(() =>
        {
            var map = host.Service<World>().Map(monster.WorldScene);
            Assert.Same(Session("bob"), monster.Target);
            Session("bob").Dead = true;
            monsters.Tick(monster, map);
            Assert.Same(Session("alice"), monster.Target); // Alice is still fighting it: no reset
            Assert.False(monster.Returning);
            Assert.Equal(monster.Template.MaxHp / 2, monster.Hp);

            Session("alice").Dead = true;
            monsters.Tick(monster, map);
            Assert.Null(monster.Target); // nobody left: back home
            Assert.True(monster.Returning);
            return Task.CompletedTask;
        });
    }
}
