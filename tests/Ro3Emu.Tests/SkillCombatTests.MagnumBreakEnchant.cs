using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int FireElement = 4;

    /// <summary>
    /// Magnum Break (buff 1100201..10, _iDescription 10220300166): "Fire damage +@{1}%. Basic Attacks become Fire." The
    /// buff's _kChangeAttri [[304 fire_ele, 500]] and its _kLogicData [11000, 4, 11400]. Reported: the hit was Fire but
    /// the weapon enchant never showed on the next attacks.
    /// </summary>
    [ClientDataFact]
    public async Task MagnumBreakMakesBasicAttacksFireAndRaisesFireDamageWhileItLasts()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var normal = data.Row("SkillConfig", 1100001)!.Value;
        var bash = data.Row("SkillConfig", 1100101)!.Value;
        int Element(System.Text.Json.JsonElement row) => combat.AttackElement(Session("alice"), row);
        long Fire(System.Text.Json.JsonElement row) => combat.ElementDamage(Session("alice"), row, 10_000);

        Assert.Equal(1, await Loop.InvokeAsync(() => Task.FromResult(Element(normal))));
        Assert.Equal(10_000, await Loop.InvokeAsync(() => Task.FromResult(Fire(normal))));

        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(Session("alice"), 1100201, 8000, Session("alice").Role!.Id));
        Assert.Equal(FireElement, await Loop.InvokeAsync(() => Task.FromResult(Element(normal))));
        Assert.Equal(1, await Loop.InvokeAsync(() => Task.FromResult(Element(bash)))); // only Basic Attacks change element
        Assert.Equal(10_500, await Loop.InvokeAsync(() => Task.FromResult(Fire(normal))));
        Assert.Equal(10_000, await Loop.InvokeAsync(() => Task.FromResult(Fire(bash)))); // Bash is Neutral, not Fire
    }
}
