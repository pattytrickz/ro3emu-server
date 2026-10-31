using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// The game server's side of the website's fixed-tier checkout (owner's plan v1.1 §4.2, ServerOptions.Supporter.Checkout).
/// The website talks to PayPal; the admin bridge passes its calls here (POST /ro3emu/supporter/{action}, loopback only).
/// This side owns what must not be trusted to a browser: which account pays (password checked here), which tier and
/// price (from SupporterEntitlements.Tiers, never from the request), the 365-day limit before an order is made, and
/// the once-only capture that extends the account's paid-through time. A capture is accepted only for the order's own
/// provider order id, amount and currency, with the provider's capture id as the idempotency key; the website must
/// have verified it with PayPal first. The tier's Supporter Tokens are recorded on the order for mail delivery.
/// </summary>
public sealed class SupporterCheckout(IOptions<ServerOptions> options, AccountStore accounts, SupporterEntitlements entitlements,
    AccessList access, TimeProvider clock, ILogger<SupporterCheckout> log)
{
    internal const string Currency = "CAD";
    private const int FailuresPerWindow = 5;
    private const long FailureWindowSeconds = 15 * 60;

    internal string Mode => options.Value.Supporter.Checkout.Trim().ToLowerInvariant() switch
    {
        "sandbox" => "sandbox",
        "live" => "live",
        _ => "off",
    };

    internal string Provider => Mode == "sandbox" ? "paypal-sandbox" : "paypal";

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static string Key(string account) => account.ToLowerInvariant();

    private bool MayPay(string account) => Mode == "live"
        || Mode == "sandbox" && options.Value.Supporter.SandboxAccounts.Any(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase));

    // Every website sign-in reaches this over the tunnel from one address, so the per-address CredentialLimiter can't
    // tell visitors apart (the bridge limits by visitor address). This caps guesses per account instead.
    private readonly ConcurrentDictionary<string, (long Window, int Failures)> _failures = new(StringComparer.Ordinal);

    /// <summary>A capture added Supporter time: Game.SupporterTokenMail mails the order's tokens now.</summary>
    internal event Action? Paid;

    private static object Error(string code, string message) => new { ok = false, code, error = message };

    /// <summary>One bridge call: login, status, order, bind, capture, reverse, funding, active.</summary>
    public async Task<object> HandleAsync(string action, JsonElement body)
    {
        string Text(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        long Number(string name) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
        return action switch
        {
            "login" => await LoginAsync(Text("account"), body.ValueKind == JsonValueKind.Object && body.TryGetProperty("password", out var p)
                && p.ValueKind == JsonValueKind.String ? p.GetString()! : ""),
            "status" => await StatusAsync(Text("account")),
            "order" => await OrderAsync(Text("account"), (int)Number("tierCad")),
            "bind" => await BindAsync(Number("orderId"), Text("providerOrderId")),
            "capture" => await CaptureAsync(Number("orderId"), Text("providerOrderId"), Text("captureId"), Number("amountCents"),
                Text("currency"), Text("status")),
            "reverse" => await ReverseAsync(Text("eventId"), Text("captureId"), Text("kind"), Number("amountCents")),
            "funding" => await FundingAsync(Number("fromUtcMs"), Number("toUtcMs"), Text("provider")),
            "active" => Active(),
            _ => Error("unknown-action", "Unknown supporter action."),
        };
    }

    internal async Task<object> LoginAsync(string account, string password)
    {
        if (account.Length is 0 or > 64 || password.Length is 0 or > 64) return Error("bad-login", "Wrong account name or password.");
        string key = Key(account);
        long window = clock.GetUtcNow().ToUnixTimeSeconds() / FailureWindowSeconds;
        if (_failures.TryGetValue(key, out var seen) && seen.Window == window && seen.Failures >= FailuresPerWindow)
            return Error("rate-limited", "Too many wrong passwords. Try again in 15 minutes.");
        if (await accounts.VerifiedAccountAsync(account, password) is not { } name)
        {
            _failures.AddOrUpdate(key, _ => (window, 1), (_, old) => old.Window == window ? (window, old.Failures + 1) : (window, 1));
            return Error("bad-login", "Wrong account name or password.");
        }
        _failures.TryRemove(key, out _);
        return new { ok = true, account = name };
    }

    private object Tier((int Cad, int Days, int Tokens) tier, long paidThrough) => new
    {
        cad = tier.Cad, amountCents = tier.Cad * 100L, currency = Currency, days = tier.Days, tokens = tier.Tokens,
        available = Fits(paidThrough, tier.Days),
    };

    private bool Fits(long paidThrough, int days) =>
        Math.Max(Now, paidThrough) + days * SupporterEntitlements.DayMs - Now <= SupporterEntitlements.MaxAheadMs;

    internal async Task<object> StatusAsync(string account)
    {
        if (await accounts.FindNameAsync(account) is not { } name) return Error("unknown-account", "No such account.");
        long paidThrough = entitlements.PaidThrough(name) ?? 0;
        return new
        {
            ok = true, account = name, mode = Mode, provider = Provider, mayPay = MayPay(name),
            entitlementsOn = entitlements.Enabled, active = paidThrough > Now, paidThroughUtcMs = paidThrough,
            tiers = SupporterEntitlements.Tiers.Select(t => Tier(t, paidThrough)).ToArray(),
        };
    }

    internal async Task<object> OrderAsync(string account, int tierCad)
    {
        if (Mode == "off") return Error("checkout-off", "Contributions are not open yet.");
        if (await accounts.FindNameAsync(account) is not { } name) return Error("unknown-account", "No such account.");
        if (!MayPay(name)) return Error("not-allowed", "Checkout is in testing: only staff test accounts can pay right now.");
        if (SupporterEntitlements.Tiers.Where(t => t.Cad == tierCad).Select(t => ((int Cad, int Days, int Tokens)?)t).FirstOrDefault()
            is not { } tier) return Error("unknown-tier", "Choose one of the listed amounts.");
        if (!Fits(entitlements.PaidThrough(name) ?? 0, tier.Days))
            return Error("over-limit", "That would put your Supporter time more than 365 days ahead. Choose a smaller amount.");
        var tx = accounts.NewTransaction();
        int index = tx.Add(new SupporterOrderCreatePlan(Key(name), Provider, tier.Cad, tier.Cad * 100L, Currency, tier.Days, tier.Tokens, Now));
        long id = (long)(await tx.CommitAsync(accounts, "supporter/order-create"))[index]!;
        log.LogInformation("Supporter order {Id}: {Account} ${Tier} {Provider}", id, name, tier.Cad, Provider);
        return new
        {
            ok = true, orderId = id, account = name, provider = Provider, tierCad = tier.Cad, amountCents = tier.Cad * 100L,
            currency = Currency, days = tier.Days, tokens = tier.Tokens,
        };
    }

    internal async Task<object> BindAsync(long orderId, string providerOrderId)
    {
        if (orderId <= 0 || providerOrderId.Length is 0 or > 64) return Error("bad-request", "Missing order.");
        var tx = accounts.NewTransaction();
        int index = tx.Add(new SupporterOrderBindPlan(orderId, providerOrderId, Now));
        bool bound = Convert.ToInt64((await tx.CommitAsync(accounts, "supporter/order-bind"))[index], System.Globalization.CultureInfo.InvariantCulture) == 1;
        return bound ? new { ok = true } : Error("bad-state", "That order can't be paid any more. Start again.");
    }

    internal async Task<object> CaptureAsync(long orderId, string providerOrderId, string captureId, long amountCents,
        string currency, string status)
    {
        if (orderId <= 0 || providerOrderId.Length is 0 or > 64 || captureId.Length is 0 or > 64)
            return Error("bad-request", "Missing payment details.");
        // Only a completed capture pays; PENDING (e.g. an eCheck) arrives again as COMPLETED through the webhook.
        if (status != "COMPLETED") return Error("not-completed", "The payment has not completed yet.");
        var tx = accounts.NewTransaction();
        int index = tx.Add(new SupporterCapturePlan(orderId, providerOrderId, captureId, amountCents, currency, Now, SupporterEntitlements.MaxAheadMs));
        var result = PaymentResult.Decode((await tx.CommitAsync(accounts, "supporter/capture"))[index]);
        var order = await accounts.ReadAsync<SupporterOrderRow?>(new SupporterOrderReadPlan(orderId));
        if (order != null && result.Outcome is PaymentOutcome.Applied or PaymentOutcome.Replayed)
        {
            entitlements.Committed(order.Account, result.PaidThroughUtcMs);
            Paid?.Invoke();
        }
        log.LogInformation("Supporter capture {Capture} of order {Order}: {Outcome}, paid through {End}", captureId, orderId,
            result.Outcome, SupporterEntitlements.Date(result.PaidThroughUtcMs));
        return result.Outcome switch
        {
            PaymentOutcome.Applied or PaymentOutcome.Replayed or PaymentOutcome.NeedsRefund => new
            {
                ok = result.Outcome != PaymentOutcome.NeedsRefund, outcome = Name(result.Outcome), needsRefund = result.Outcome == PaymentOutcome.NeedsRefund,
                account = order?.Account, days = order?.Days, tokens = order?.Tokens, paidThroughUtcMs = result.PaidThroughUtcMs,
            },
            PaymentOutcome.UnknownOrder => Error("unknown-order", "No such order."),
            PaymentOutcome.Mismatch => Error("mismatch", "The payment does not match the order."),
            PaymentOutcome.AlreadyCaptured => Error("already-captured", "That order was already paid by another payment."),
            _ => Error("bad-state", "That order can't be paid any more."),
        };
    }

    internal async Task<object> ReverseAsync(string eventId, string captureId, string kind, long amountCents)
    {
        if (eventId.Length is 0 or > 96 || captureId.Length is 0 or > 64 || kind is not ("refund" or "reversal" or "dispute"))
            return Error("bad-request", "Missing refund details.");
        var tx = accounts.NewTransaction();
        int index = tx.Add(new SupporterReversePlan(eventId, captureId, kind, amountCents, Now));
        var result = PaymentResult.Decode((await tx.CommitAsync(accounts, "supporter/reverse"))[index]);
        if (result.Outcome == PaymentOutcome.UnknownOrder) return Error("unknown-order", "No order has that capture.");
        var order = await accounts.ReadAsync<SupporterOrderRow?>(new SupporterOrderReadPlan(0, captureId));
        if (order != null) entitlements.Committed(order.Account, result.PaidThroughUtcMs);
        log.LogInformation("Supporter {Kind} {Event} of capture {Capture}: {Outcome}, order now {State}", kind, eventId, captureId,
            result.Outcome, order?.State);
        return new { ok = true, outcome = Name(result.Outcome), state = order?.State, account = order?.Account, paidThroughUtcMs = result.PaidThroughUtcMs };
    }

    internal async Task<object> FundingAsync(long fromUtcMs, long toUtcMs, string provider)
    {
        var sums = await accounts.ReadAsync<long[]>(new SupporterFundingReadPlan(provider.Length > 0 ? provider : Provider, fromUtcMs,
            toUtcMs > 0 ? toUtcMs : long.MaxValue));
        return new { ok = true, grossCents = sums[0], refundedCents = sums[1], netCents = sums[0] - sums[1], orders = sums[2], currency = Currency };
    }

    /// <summary>
    /// Who is a supporter right now, as AccessList.IsSupporter decides in game: paid-through dates when entitlements are
    /// on, else the permanent list. The bridge gives these accounts' linked Discord users the Supporter role (phase 5).
    /// </summary>
    internal object Active() => new
    {
        ok = true, source = entitlements.Enabled ? "entitlements" : "legacy-list",
        accounts = entitlements.Enabled ? entitlements.ActiveAccounts()
            : [.. access.LegacySupporters().Select(Key).Distinct().Order(StringComparer.Ordinal)],
    };

    /// <summary>GM "!supporter sandbox-clear": takes back the days of every sandbox payment (before going live).</summary>
    internal async Task<string> ClearSandboxAsync()
    {
        var orders = await accounts.ReadAsync<List<SupporterOrderRow>>(new SupporterOrderListReadPlan("paypal-sandbox"));
        int cleared = 0;
        foreach (var order in orders.Where(o => o.CaptureId != null && o.State is not ("refunded" or "reversed")))
        {
            await ReverseAsync("sandbox-clear/" + order.CaptureId, order.CaptureId!, "refund", order.AmountCents);
            cleared++;
        }
        return $"Sandbox payments cleared: {cleared} of {orders.Count} sandbox orders had days to take back.";
    }

    private static string Name(PaymentOutcome outcome) => outcome switch
    {
        PaymentOutcome.Applied => "applied",
        PaymentOutcome.Replayed => "replayed",
        PaymentOutcome.NeedsRefund => "needs-refund",
        _ => "recorded",
    };
}
