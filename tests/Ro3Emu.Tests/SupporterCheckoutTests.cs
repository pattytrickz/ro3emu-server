using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>
/// The game server's side of the website's checkout (owner's plan v1.1 §4.2, §6 "Payment and identity"): sign-in,
/// server-priced orders, the 365-day limit, once-only verified captures, refunds, disputes and the funding total.
/// </summary>
public sealed class SupporterCheckoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-checkout-" + Guid.NewGuid().ToString("N"));
    private readonly ServerOptions _options;
    private readonly AccountStore _accounts;
    private readonly Clock _clock = new();
    private readonly SupporterEntitlements _entitlements;
    private readonly SupporterCheckout _checkout;
    private readonly AccessList _access;
    private const long Day = SupporterEntitlements.DayMs;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public SupporterCheckoutTests()
    {
        _options = new ServerOptions { DataDir = _directory };
        _options.Supporter.Checkout = "live";
        _accounts = new AccountStore(Options.Create(_options));
        _entitlements = new SupporterEntitlements(Options.Create(_options), _accounts, _clock, NullLogger<SupporterEntitlements>.Instance);
        _access = new AccessList(Options.Create(_options), NullLogger<AccessList>.Instance, _entitlements);
        _checkout = new SupporterCheckout(Options.Create(_options), _accounts, _entitlements, _access, _clock, NullLogger<SupporterCheckout>.Instance);
        foreach (string name in new[] { "Alice", "Bob" })
        {
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            Assert.True(_accounts.CreateAsync(name, AccountHash.Hash("password1", salt), salt).GetAwaiter().GetResult());
        }
    }

    public void Dispose()
    {
        _accounts.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    private long Now => _clock.Now.ToUnixTimeMilliseconds();

    private async Task<JsonElement> Call(string action, object body) => J(await _checkout.HandleAsync(action, J(body)));

    /// <summary>Order, bind and capture one tier as the website does after PayPal confirms the capture.</summary>
    private async Task<(long Order, JsonElement Capture)> Pay(string account, int cad, string capture, long? amountCents = null)
    {
        var order = await Call("order", new { account, tierCad = cad });
        Assert.True(order.GetProperty("ok").GetBoolean(), order.ToString());
        long id = order.GetProperty("orderId").GetInt64();
        Assert.True((await Call("bind", new { orderId = id, providerOrderId = "PP-" + capture })).GetProperty("ok").GetBoolean());
        var captured = await Call("capture", new
        {
            orderId = id, providerOrderId = "PP-" + capture, captureId = capture, amountCents = amountCents ?? cad * 100L,
            currency = "CAD", status = "COMPLETED",
        });
        return (id, captured);
    }

    [Fact]
    public async Task SignInChecksThePasswordAndStopsGuessingPerAccount()
    {
        Assert.Equal("Alice", (await Call("login", new { account = "alice", password = "password1" })).GetProperty("account").GetString());
        for (int i = 0; i < 5; i++)
            Assert.Equal("bad-login", (await Call("login", new { account = "alice", password = "wrong" + i })).GetProperty("code").GetString());
        // Locked for this window even with the right password; other accounts are not.
        Assert.Equal("rate-limited", (await Call("login", new { account = "alice", password = "password1" })).GetProperty("code").GetString());
        Assert.True((await Call("login", new { account = "bob", password = "password1" })).GetProperty("ok").GetBoolean());
        _clock.Now = _clock.Now.AddMinutes(16);
        Assert.True((await Call("login", new { account = "alice", password = "password1" })).GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData(10, 30, 100)]
    [InlineData(20, 60, 200)]
    [InlineData(30, 90, 300)]
    [InlineData(50, 150, 500)]
    [InlineData(100, 300, 1000)]
    public async Task EachTierAddsItsDaysOnceAndRecordsItsTokens(int cad, int days, int tokens)
    {
        var (_, capture) = await Pay("alice", cad, "CAP-" + cad);
        Assert.Equal("applied", capture.GetProperty("outcome").GetString());
        Assert.Equal(tokens, capture.GetProperty("tokens").GetInt32());
        Assert.Equal(Now + days * Day, capture.GetProperty("paidThroughUtcMs").GetInt64());
        Assert.Equal(Now + days * Day, _entitlements.PaidThrough("alice"));
    }

    [Fact]
    public async Task ACaptureAppliesOnceAndOnlyWhenItMatchesTheOrder()
    {
        var (order, first) = await Pay("alice", 10, "CAP-1");
        long end = first.GetProperty("paidThroughUtcMs").GetInt64();
        var capture = new { orderId = order, providerOrderId = "PP-CAP-1", captureId = "CAP-1", amountCents = 1000L, currency = "CAD", status = "COMPLETED" };
        // The webhook and the return page both report it: the second changes nothing.
        Assert.Equal("replayed", (await Call("capture", capture)).GetProperty("outcome").GetString());
        Assert.Equal("already-captured", (await Call("capture", capture with { captureId = "CAP-other" })).GetProperty("code").GetString());
        Assert.Equal(end, _entitlements.PaidThrough("alice"));

        var next = (await Call("order", new { account = "alice", tierCad = 10 })).GetProperty("orderId").GetInt64();
        await Call("bind", new { orderId = next, providerOrderId = "PP-2" });
        var good = new { orderId = next, providerOrderId = "PP-2", captureId = "CAP-2", amountCents = 1000L, currency = "CAD", status = "COMPLETED" };
        Assert.Equal("mismatch", (await Call("capture", good with { amountCents = 100L })).GetProperty("code").GetString());
        Assert.Equal("mismatch", (await Call("capture", good with { currency = "USD" })).GetProperty("code").GetString());
        Assert.Equal("mismatch", (await Call("capture", good with { providerOrderId = "PP-someone-else" })).GetProperty("code").GetString());
        Assert.Equal("not-completed", (await Call("capture", good with { status = "PENDING" })).GetProperty("code").GetString());
        Assert.Equal(end, _entitlements.PaidThrough("alice"));
        // The next purchase stacks on the end.
        Assert.Equal(end + 30 * Day, (await Call("capture", good)).GetProperty("paidThroughUtcMs").GetInt64());
        // An order can't be re-bound to another provider order once bound.
        Assert.Equal("bad-state", (await Call("bind", new { orderId = next, providerOrderId = "PP-3" })).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The365DayLimitIsCheckedBeforePayingAndARaceIsRefundedNotSwallowed()
    {
        var status = await Call("status", new { account = "alice" });
        Assert.All(status.GetProperty("tiers").EnumerateArray(), t => Assert.True(t.GetProperty("available").GetBoolean()));
        // Two $100 orders opened at once (two browser tabs) both pass the check; only the first capture can add days.
        long a = (await Call("order", new { account = "alice", tierCad = 100 })).GetProperty("orderId").GetInt64();
        long b = (await Call("order", new { account = "alice", tierCad = 100 })).GetProperty("orderId").GetInt64();
        await Call("bind", new { orderId = a, providerOrderId = "PP-A" });
        await Call("bind", new { orderId = b, providerOrderId = "PP-B" });
        Assert.Equal("applied", (await Call("capture", new { orderId = a, providerOrderId = "PP-A", captureId = "CAP-A", amountCents = 10000L, currency = "CAD", status = "COMPLETED" })).GetProperty("outcome").GetString());
        var second = await Call("capture", new { orderId = b, providerOrderId = "PP-B", captureId = "CAP-B", amountCents = 10000L, currency = "CAD", status = "COMPLETED" });
        Assert.True(second.GetProperty("needsRefund").GetBoolean());
        Assert.Equal(Now + 300 * Day, _entitlements.PaidThrough("alice"));
        // Now only tiers that fit can be ordered: 300 + 60 is under 365, 300 + 90 is not.
        status = await Call("status", new { account = "alice" });
        var available = status.GetProperty("tiers").EnumerateArray().Where(t => t.GetProperty("available").GetBoolean())
            .Select(t => t.GetProperty("cad").GetInt32()).ToArray();
        Assert.Equal([10, 20], available);
        Assert.Equal("over-limit", (await Call("order", new { account = "alice", tierCad = 30 })).GetProperty("code").GetString());
        // The refund of the over-limit capture takes no days (it never added any).
        await Call("reverse", new { eventId = "EV-B", captureId = "CAP-B", kind = "refund", amountCents = 10000L });
        Assert.Equal(Now + 300 * Day, _entitlements.PaidThrough("alice"));
    }

    [Fact]
    public async Task RefundsTakeTheDaysBackOnceDisputesAndPartialRefundsWaitForStaff()
    {
        await Pay("alice", 10, "CAP-1");
        await Pay("alice", 20, "CAP-2");
        Assert.Equal(Now + 90 * Day, _entitlements.PaidThrough("alice"));

        // A partial refund changes no time.
        var partial = await Call("reverse", new { eventId = "EV-1", captureId = "CAP-2", kind = "refund", amountCents = 500L });
        Assert.Equal("partially-refunded", partial.GetProperty("state").GetString());
        Assert.Equal(Now + 90 * Day, _entitlements.PaidThrough("alice"));
        // The rest of it: the $20's 60 days come off, once.
        var full = await Call("reverse", new { eventId = "EV-2", captureId = "CAP-2", kind = "refund", amountCents = 1500L });
        Assert.Equal("refunded", full.GetProperty("state").GetString());
        Assert.Equal(Now + 30 * Day, _entitlements.PaidThrough("alice"));
        Assert.Equal("replayed", (await Call("reverse", new { eventId = "EV-2", captureId = "CAP-2", kind = "refund", amountCents = 1500L })).GetProperty("outcome").GetString());
        Assert.Equal(Now + 30 * Day, _entitlements.PaidThrough("alice"));

        var dispute = await Call("reverse", new { eventId = "EV-3", captureId = "CAP-1", kind = "dispute", amountCents = 1000L });
        Assert.Equal("disputed", dispute.GetProperty("state").GetString());
        Assert.Equal(Now + 30 * Day, _entitlements.PaidThrough("alice"));
        Assert.Equal("unknown-order", (await Call("reverse", new { eventId = "EV-4", captureId = "CAP-none", kind = "refund", amountCents = 1L })).GetProperty("code").GetString());

        // Funding counts verified captures, refunds netted out: $10 + $20 - $20.
        var funding = await Call("funding", new { fromUtcMs = 0L, toUtcMs = Now + 1 });
        Assert.Equal((3000L, 2000L, 1000L), (funding.GetProperty("grossCents").GetInt64(), funding.GetProperty("refundedCents").GetInt64(),
            funding.GetProperty("netCents").GetInt64()));
    }

    [Fact]
    public async Task OffRefusesOrdersAndSandboxOnlyTakesStaffTestAccountsAndCanBeCleared()
    {
        _options.Supporter.Checkout = "off";
        Assert.Equal("checkout-off", (await Call("order", new { account = "alice", tierCad = 10 })).GetProperty("code").GetString());

        _options.Supporter.Checkout = "sandbox";
        _options.Supporter.SandboxAccounts = ["Bob"];
        Assert.Equal("not-allowed", (await Call("order", new { account = "alice", tierCad = 10 })).GetProperty("code").GetString());
        Assert.False((await Call("status", new { account = "alice" })).GetProperty("mayPay").GetBoolean());
        var (_, capture) = await Pay("bob", 10, "SANDBOX-1");
        Assert.True(capture.GetProperty("ok").GetBoolean());
        Assert.Equal(Now + 30 * Day, _entitlements.PaidThrough("bob"));
        // Sandbox money is not real: it never counts toward the live funding goal.
        Assert.Equal(0, (await Call("funding", new { fromUtcMs = 0L, toUtcMs = Now + 1, provider = "paypal" })).GetProperty("grossCents").GetInt64());

        Assert.Contains("1 of 1", await _checkout.ClearSandboxAsync());
        Assert.Equal(Now, _entitlements.PaidThrough("bob"));
        Assert.False(_entitlements.IsActive("bob"));
    }

    [Fact]
    public async Task ActiveListsWhoTheGameTreatsAsASupporterNow()
    {
        // Entitlements off: the permanent list decides, as in game.
        _access.Run("supporter Bob");
        await Pay("alice", 10, "CAP-1");
        var active = await Call("active", new { });
        Assert.Equal("legacy-list", active.GetProperty("source").GetString());
        Assert.Equal(["bob"], [.. active.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()!)]);
        // On: paid-through dates decide, and an ended one drops out.
        _options.Supporter.Entitlements = true;
        active = await Call("active", new { });
        Assert.Equal(["alice"], [.. active.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()!)]);
        _clock.Now = _clock.Now.AddDays(31);
        Assert.Empty((await Call("active", new { })).GetProperty("accounts").EnumerateArray());
    }
}
