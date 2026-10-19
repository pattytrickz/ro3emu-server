using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Sonic Blow's _kDamageParam1 [8000, 9600, 10000] (Lv 5): the hit at 50% HP or lower takes index 1 (+20%), and with
    /// Supersonic Blow (Lv 5: "70", "25") a target at 70% or lower takes index 2 (+25%). Reported: no low-HP bonus.
    /// </summary>
    [ClientDataFact]
    public async Task SonicBlowUsesItsLowHpBranchAndSupersonicBlowRaisesTheThreshold()
    {
        const int SonicBlow = 1510205, Supersonic = 15103;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200");
        var combat = app.Services.GetRequiredService<CombatService>();
        var data = app.Services.GetRequiredService<GameData>();
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            var row = data.Row("SkillConfig", SonicBlow)!.Value;
            int At(double percent) { target.Hp = (long)(target.Template.MaxHp * percent / 100); return combat.SonicBlowBranch(s, target, row); }
            Assert.Equal(0, At(100));
            Assert.Equal(0, At(60));
            Assert.Equal(1, At(50));
            Assert.Equal(1, At(10));
            s.Progress!.Skills[Supersonic] = 5;
            Assert.Equal(0, At(80));
            Assert.Equal(2, At(70));
            Assert.Equal(2, At(30));
            return Task.CompletedTask;
        });
    }
}
