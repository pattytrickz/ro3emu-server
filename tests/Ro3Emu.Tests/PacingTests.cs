using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Sol RO3 pacing house rules (owner decision 24 Oct 2026): the published level-cap schedule (LevelCaps), Bonus Time
/// for hands-on kills only from a level up (BonusExpAlwaysBelowLevel) and a larger battle-time budget
/// (BonusTimeMultiplier). None of it is RO3's own behaviour; unset, the original rules hold.
/// </summary>
public sealed class PacingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-pacing-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(Start);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.LevelCaps = [new LevelCapPhase { From = Start.AddDays(-1), Cap = 5 }, new LevelCapPhase { From = Start.AddDays(7), Cap = 8 }];
            o.BonusTimeMultiplier = 2;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public void TheCapIsTheLatestPhaseThatHasStarted()
    {
        LevelCapPhase[] phases =
        [
            new() { From = Start.AddDays(30), Cap = 80 },
            new() { From = Start, Cap = 70 },
            new() { From = Start.AddDays(60), Cap = 120 },
        ];
        Assert.Equal(99, CharacterService.LevelCapAt([], 99, Start)); // no schedule: the data's maximum
        Assert.Equal(99, CharacterService.LevelCapAt(phases, 99, Start.AddSeconds(-1)));
        Assert.Equal(70, CharacterService.LevelCapAt(phases, 99, Start));
        Assert.Equal(80, CharacterService.LevelCapAt(phases, 99, Start.AddDays(45)));
        Assert.Equal(99, CharacterService.LevelCapAt(phases, 99, Start.AddDays(61))); // never above the data's maximum
    }

    [Fact]
    public void AutoBattleFromTheBandLevelUsesBattleTime()
    {
        var o = new ServerOptions { BonusExpAlways = true, BonusExpAlwaysBelowLevel = 50 };
        Assert.True(WildAfkService.AlwaysBonus(o, 49, autoBattle: true));
        Assert.False(WildAfkService.AlwaysBonus(o, 50, autoBattle: true));
        Assert.True(WildAfkService.AlwaysBonus(o, 80, autoBattle: false)); // hands-on play keeps the table
        Assert.True(WildAfkService.AlwaysBonus(new ServerOptions { BonusExpAlways = true }, 99, autoBattle: true));
        Assert.False(WildAfkService.AlwaysBonus(new ServerOptions(), 10, autoBattle: false)); // the original rule
    }

    /// <summary>EXP stops at the cap with a full bar, the role levels on its first EXP after a raise, and a level above the
    /// cap is never taken away.</summary>
    [ClientDataFact]
    public async Task EXPStopsAtTheCapWithAFullBarAndCountsAgainAfterARaise()
    {
        await using var alice = await InWorld("Alice");
        var progress = Session(alice).Progress!;
        await Gm(alice.Account!, "!exp 999999999");
        Assert.Equal(5, progress.Level);
        long full = Service<CharacterService>().BaseExpNeed(5) - 1;
        Assert.Equal(full, progress.Exp);

        _clock.Advance(TimeSpan.FromDays(7));
        await Gm(alice.Account!, "!exp 1");
        Assert.Equal(6, progress.Level);

        await Gm(alice.Account!, "!lvl 20"); // a GM may still set any level, and EXP never lowers it
        await Gm(alice.Account!, "!exp 1000");
        Assert.Equal(20, progress.Level);
    }

    [ClientDataFact]
    public async Task BattleTimeIsMultiplied()
    {
        await using var alice = await InWorld("Alice");
        Assert.Equal(2 * 60 * 60, Session(alice).WildAfk!.Info.HighMultiplierTimeSeconds); // 60 minutes a day, twice
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private async Task Gm(string account, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
