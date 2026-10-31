using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

/// <summary>
/// Paid Supporter Tokens arrive by in-game mail once per order (owner's plan v1.1 §4.4, §6 "Item/token/mail"): to the
/// account's character, offline too, held while the account has none, never expiring, never twice, and never for an
/// order refunded before its letter went out.
/// </summary>
public sealed class SupporterTokenMailTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-tokenmail-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.Supporter.Checkout = "live";
        });
        await _app.StartAsync();
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        Assert.True(await Service<AccountStore>().CreateAsync("Carol", AccountHash.Hash("password1", salt), salt));
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<JsonElement> Call(string action, object body) =>
        JsonSerializer.SerializeToElement(await Service<SupporterCheckout>().HandleAsync(action, JsonSerializer.SerializeToElement(body)));

    private async Task<string> Pay(int cad, string capture)
    {
        long id = (await Call("order", new { account = "carol", tierCad = cad })).GetProperty("orderId").GetInt64();
        await Call("bind", new { orderId = id, providerOrderId = "PP-" + capture });
        var paid = await Call("capture", new { orderId = id, providerOrderId = "PP-" + capture, captureId = capture, amountCents = cad * 100L, currency = "CAD", status = "COMPLETED" });
        Assert.Equal("applied", paid.GetProperty("outcome").GetString());
        return capture;
    }

    private Task<List<SupporterOrderRow>> Owed() =>
        Service<AccountStore>().ReadAsync<List<SupporterOrderRow>>(new SupporterTokenMailPendingReadPlan());

    private List<MailService.Letter> Letters(long role) => Service<RoleStore>().LoadMailbox(role) is { } bytes
        ? JsonSerializer.Deserialize<MailService.Mailbox>(bytes)!.Letters : [];

    [ClientDataFact]
    public async Task TokensWaitForACharacterThenArriveOnceAndNeverExpire()
    {
        await Pay(10, "CAP-1");
        // Refunded while the tokens were still owed: they are never sent.
        await Pay(30, "CAP-0");
        await Call("reverse", new { eventId = "EV-0", captureId = "CAP-0", kind = "refund", amountCents = 3000L });
        // No character yet: the tokens stay owed (the Supporter time is already active).
        await Service<SupporterTokenMail>().DeliverPendingAsync();
        Assert.Equal("CAP-1", Assert.Single(await Owed()).CaptureId);
        Assert.True(Service<SupporterEntitlements>().PaidThrough("carol") > 0);

        long role = Service<RoleStore>().Create("Carol", 1, "Carolyn", 1, 1000, 1)!.Id;
        await Service<SupporterTokenMail>().DeliverPendingAsync();
        await Service<SupporterTokenMail>().DeliverPendingAsync(); // a retry (or the minute sweep) adds nothing
        Assert.Empty(await Owed());
        var letter = Assert.Single(Letters(role));
        Assert.Equal("Thank you, Supporter!", letter.Title);
        Assert.Equal(0, letter.RemoveTime); // paid tokens never expire
        var tokens = Assert.Single(letter.Attachments);
        Assert.Equal((F2PRules.SupporterToken, 100), (tokens.Entry, tokens.Count));
        Assert.Equal("supporter/paypal/CAP-1/tokens", letter.DeliveryKey);

        // A second purchase is a second letter, sent as soon as it is captured. A refund after that leaves the
        // tokens alone (plan §3 F: flagged for staff, never taken from the player's inventory).
        await Pay(20, "CAP-2");
        await Service<SupporterTokenMail>().DeliverPendingAsync();
        await Call("reverse", new { eventId = "EV-2", captureId = "CAP-2", kind = "refund", amountCents = 2000L });
        Assert.Equal([100, 200], [.. Letters(role).Select(l => l.Attachments.Single().Count).Order()]);
        Assert.Empty(await Owed());
    }
}
