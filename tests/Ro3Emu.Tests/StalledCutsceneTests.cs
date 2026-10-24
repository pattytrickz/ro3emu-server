using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A cutscene step whose client never reports the end (84011) completes at a login once it has been open for
/// TaskService.StalledPlot (Sol RO3 fallback; "Strange Occurrences" froze one player's client, 24 Oct 2026).
/// </summary>
public sealed class StalledCutsceneTests : IAsyncLifetime
{
    private const long StrangeOccurrences = 10105050, Next = 10105060;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-cutscene-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
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

    [ClientDataFact]
    public async Task ACutsceneStepOpenTooLongCompletesAtTheNextLogin()
    {
        await using (var alice = await InWorld("Alice", create: true))
        {
            await Gm(alice.Account!, $"!quest accept {StrangeOccurrences}");
            Assert.Equal(TaskState.Running, Task(alice, StrangeOccurrences)!.Status);
        }

        _clock.Advance(TimeSpan.FromMinutes(5)); // a relog inside the window replays the cutscene as before
        await using (var again = await InWorld("Alice", create: false))
            Assert.Equal(TaskState.Running, Task(again, StrangeOccurrences)!.Status);

        _clock.Advance(TimeSpan.FromMinutes(6));
        await using var late = await InWorld("Alice", create: false);
        Assert.Equal(TaskState.Delivery, Task(late, StrangeOccurrences)!.Status);
        Assert.NotNull(Task(late, Next)); // the story moves on
    }

    private TaskInfo? Task(BotClient bot, long id) =>
        Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null).Tasks!.Infos.GetValueOrDefault(id);

    private async Task<BotClient> InWorld(string name, bool create)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync(create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id);
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
