using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Sol RO3 house rule ServerOptions.FieldMvpHpPercent: field MVPs spawn with a share of their HP.</summary>
public sealed class FieldMvpHpTests : IAsyncLifetime
{
    private const int Angeling = 5020001;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-mvphp-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
            o.FieldMvpHpPercent = 50;
        });
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public async Task AFieldMvpSpawnsWithTheHouseRuleShareOfItsHp()
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        MonsterUnit? boss = null;
        for (int i = 0; i < 150 && boss == null; i++)
        {
            boss = await loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<World>().AllUnits()
                .OfType<MonsterUnit>().FirstOrDefault(m => m.Template.NpcId == Angeling && !m.Dead)));
            if (boss == null) await Task.Delay(100);
        }
        Assert.NotNull(boss);
        long configured = _app.Services.GetRequiredService<MonsterService>().Template(Angeling)!.MaxHp;
        Assert.Equal((configured + 1) / 2, boss!.Template.MaxHp);
        Assert.Equal(boss.Template.MaxHp, boss.Hp);
    }
}
