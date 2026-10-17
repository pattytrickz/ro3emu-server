using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int MaxHpProperty = 101, MaxSpProperty = 102;

    private Task<(int Level, long? Hp, long? Sp, long MaxHp, long MaxSp)> Vitals(string name) => Loop.InvokeAsync(() =>
    {
        var s = Session(name);
        var stats = Character.Stats(s);
        return Task.FromResult((s.Progress!.Level, s.CurrentHp, s.CurrentSp, stats[MaxHpProperty], stats[MaxSpProperty]));
    });

    private Task Hurt(string name) => Loop.InvokeAsync(() => { Session(name).CurrentHp = 1; Session(name).CurrentSp = 0; return Task.CompletedTask; });

    private void LevelUpRefill(bool on) => app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.LevelUpRefill = on;

    /// <summary>ServerOptions.LevelUpRefill (house rule, off by default): a base level-up refills HP and SP.</summary>
    [ClientDataFact]
    public async Task LevelUpRefillsHpAndSpOnlyWhenTheHouseRuleIsOn()
    {
        await using var alice = await InWorld("Alice");
        await Hurt("alice");
        await Gm("alice", "!exp 100000"); // off: the level rises, HP and SP are not refilled
        var off = await Vitals("alice");
        Assert.True(off.Level > 1, $"level {off.Level}");
        Assert.True(off.Hp < off.MaxHp && off.Sp < off.MaxSp, $"HP {off.Hp}/{off.MaxHp} SP {off.Sp}/{off.MaxSp}");

        LevelUpRefill(true);
        await Gm("alice", "!exp 100000");
        var on = await Vitals("alice");
        Assert.True(on.Level > off.Level, $"level {off.Level} -> {on.Level}");
        Assert.Equal(on.MaxHp, on.Hp);
        Assert.Equal(on.MaxSp, on.Sp);
    }

    /// <summary>Kill EXP reaches the role through ExchangeLockedAsync (RewardService); that level-up refills too.</summary>
    [ClientDataFact]
    public async Task LevelUpFromKillExpRefillsHpAndSpWhenTheHouseRuleIsOn()
    {
        LevelUpRefill(true);
        await using var alice = await InWorld("Alice");
        await Hurt("alice");
        var before = await Vitals("alice");
        var session = await Loop.InvokeAsync(() => Task.FromResult(Session("alice")));

        await Loop.InvokeAsync(() => app.Services.GetRequiredService<OwnerRegistry>().RunAsync("kill exp",
            [ReservationKey.Role(session.Role!.Id)], async () =>
            {
                using var state = await Ro3Emu.Server.Game.CharacterService.Lock(session);
                await Character.ExchangeLockedAsync(session, [], [], popup: false, sync: false, baseExp: 100000);
            }));

        var after = await Vitals("alice");
        Assert.True(after.Level > before.Level, $"level {before.Level} -> {after.Level}");
        Assert.Equal(after.MaxHp, after.Hp);
        Assert.Equal(after.MaxSp, after.Sp);
    }
}
