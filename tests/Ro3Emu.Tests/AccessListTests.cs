using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

/// <summary>
/// ServerOptions.Access (house rule, Accounts.AccessList): approval-only registration, blocked names and admin password
/// resets, through the real /sdk/login and the host's loopback /ro3emu/access tool.
/// </summary>
public sealed class AccessListTests : IAsyncLifetime
{
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-access-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    private readonly HttpClient client = new();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.Access.Mode = "approved";
            o.GmAudit = true;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    /// <summary>The SDK login's code: 0 logged in (or registered), 401 refused.</summary>
    private async Task<int> Login(string name, string password)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = name, ["credential"] = password });
        var body = await (await client.PostAsync(http + "/sdk/login", form)).Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("code").GetInt32();
    }

    private async Task<string> Access(string line) =>
        await (await client.PostAsync(http + "/ro3emu/access", new StringContent(line))).Content.ReadAsStringAsync();

    [ClientDataFact]
    public async Task OnlyApprovedNamesRegisterAndTheApprovalIsUsedUp()
    {
        Assert.Equal(401, await Login("stranger", "pw"));
        Assert.Contains("may now register", await Access("approve Newbie"));
        Assert.Equal(0, await Login("newbie", "secret"));        // registers; names compare case-insensitively
        Assert.Contains("Approved (not yet registered): none", await Access("list"));
        Assert.Equal(0, await Login("newbie", "secret"));        // the account logs in as any account does
        Assert.Equal(401, await Login("newbie", "wrong"));
        Assert.Equal(401, await Login("Newbie2", "secret"));     // one approval, one account

        var audit = File.ReadAllLines(Path.Combine(dataDir, "logs", "gm-audit.log"));
        Assert.Contains(audit, l => l.EndsWith("\t-\t-\thost\taccess approve Newbie", StringComparison.Ordinal));
    }

    [ClientDataFact]
    public async Task BlockedNamesAreRefusedAndResetLetsTheNextLoginSetThePassword()
    {
        await Access("approve Bob");
        Assert.Equal(0, await Login("Bob", "old"));
        await Access("block bob");
        Assert.Equal(401, await Login("Bob", "old"));
        await Access("unblock Bob");
        Assert.Equal(0, await Login("Bob", "old"));

        await Access("reset Bob");
        Assert.Equal(0, await Login("Bob", "new"));               // sets the new password
        Assert.Equal(401, await Login("Bob", "old"));
        Assert.Equal(0, await Login("Bob", "new"));
        Assert.Contains("Password reset pending: none", await Access("list"));
    }

    [ClientDataFact]
    public async Task OpenModeRegistersAnyNewNameButStillRefusesBlockedOnes()
    {
        app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.Access.Mode = "open";
        Assert.Equal(0, await Login("walkin", "pw"));
        await Access("block walkin");
        Assert.Equal(401, await Login("walkin", "pw"));
    }
}
