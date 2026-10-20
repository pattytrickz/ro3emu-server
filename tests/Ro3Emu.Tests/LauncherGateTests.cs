using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>ServerOptions.MinLauncherVersion: clients patched by an older launcher (or none) are turned away.</summary>
public sealed class LauncherGateTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-launcher-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], options =>
        {
            options.DataDir = _dataDir;
            options.GameDataDir = Path.Combine(RepoRoot(), "data");
            options.HttpPort = httpPort;
            options.MicroGatePort = microPort;
            options.GameGatePort = gamePort;
            options.MinLauncherVersion = "1.2.2";
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        // The tests run on loopback, which is exempt like the AI bots: play an outdated player's client instead.
        _app.Services.GetRequiredService<LauncherVersions>().ExemptLoopback = false;
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public void TheLauncherPrefixIsStrippedAndItsVersionRead()
    {
        Assert.True(LauncherVersions.TryStrip(new PathString("/launcher/1.2.2/sdk/login"), out var version, out var rest));
        Assert.Equal(new Version(1, 2, 2), version);
        Assert.Equal("/sdk/login", rest.Value);
        Assert.True(LauncherVersions.TryStrip(new PathString("/launcher/x/serverlist/v2/168/serverlist.json"), out var bad, out rest));
        Assert.Null(bad);
        Assert.Equal("/serverlist/v2/168/serverlist.json", rest.Value);
        Assert.False(LauncherVersions.TryStrip(new PathString("/sdk/login"), out _, out rest));
        Assert.Equal("/sdk/login", rest.Value);
    }

    [ClientDataFact]
    public async Task TheServerListTellsAnOutdatedClientToUpdateItsLauncher()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_http) };
        const string List = "serverlist/v2/168/serverlist.json?inline=true&force_refresh=true";
        Assert.Contains("Update your launcher", await http.GetStringAsync("/" + List));
        Assert.Contains("Update your launcher", await http.GetStringAsync("/launcher/1.2.1/" + List));
        string current = await http.GetStringAsync("/launcher/1.2.2/" + List);
        Assert.DoesNotContain("Update your launcher", current);
        Assert.Contains("RO3Emu", current);

        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = "newclient", ["credential"] = "pw" });
        var login = await (await http.PostAsync("/launcher/1.2.2/sdk/login", form)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, login.GetProperty("code").GetInt32());
    }

    [ClientDataFact]
    public async Task AnOutdatedClientIsToldWhyAndDisconnectedInTheWorld()
    {
        // BotClient logs in on the bare /sdk/login: the client of a launcher before 1.2.2.
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("oldclient", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("OldClient")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var notice = await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains("Launcher is out of date"), Wait);
        Assert.NotNull(notice);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "OldClient");
        Assert.True(session.LauncherTurnedAway);
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (sessions.All().Any(s => ReferenceEquals(s, session)) && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.DoesNotContain(sessions.All(), s => ReferenceEquals(s, session));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
