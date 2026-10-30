using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Supporter system v2 without the game: the Crystal budget's pricing (owner's plan v1.1 §2.4 rules 5 and 10), its
/// compare-and-set accounting, and paid-through entitlements (§2.1) with the legacy end date.
/// </summary>
public sealed class SupporterEconomyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-supporter-" + Guid.NewGuid().ToString("N"));
    private readonly ServerOptions _options;
    private readonly AccountStore _accounts;
    private readonly Clock _clock = new();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public SupporterEconomyTests()
    {
        _options = new ServerOptions { DataDir = _directory };
        _accounts = new AccountStore(Options.Create(_options));
    }

    public void Dispose()
    {
        _accounts.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(100, 650, 50)]  // the plan's example: 650 of 700 earned, a 100 award pays 50
    [InlineData(100, 0, 100)]
    [InlineData(20, 700, 0)]
    [InlineData(20, 900, 0)]    // never negative, even past a lowered cap
    public void AnAwardPaysWhatIsLeftOfTheWeeklyBudget(long gross, long earned, long credited) =>
        Assert.Equal(credited, CrystalEconomy.Price(gross, earned, 700, supporter: false, 0, 10).Credited);

    [Fact]
    public void TokensComeFromCrystalsPaidAndTheRemainderCarriesOver()
    {
        // 9 then 1 make exactly one token.
        var first = CrystalEconomy.Price(9, 0, 700, supporter: true, remainder: 0, perToken: 10);
        Assert.Equal((9L, 0L, 9L), first);
        Assert.Equal((1L, 1L, 0L), CrystalEconomy.Price(1, 9, 700, supporter: true, first.Remainder, 10));
        // Only the Crystals actually paid convert: 50 of a capped 100.
        Assert.Equal((50L, 5L, 3L), CrystalEconomy.Price(100, 650, 700, supporter: true, remainder: 3, perToken: 10));
        // A free player keeps the same Crystals and mints nothing.
        Assert.Equal((50L, 0L, 3L), CrystalEconomy.Price(100, 650, 700, supporter: false, remainder: 3, perToken: 10));
    }

    private async Task<object?> Commit(DbPlan plan)
    {
        var tx = _accounts.NewTransaction();
        int index = tx.Add(plan);
        return (await tx.CommitAsync(_accounts, "test"))[index];
    }

    private static CrystalAwardPlan Award(string source, long gross, long credited, long tokens, long earnedBefore,
        long remainderBefore, long remainder) =>
        new(source, "live", "alice", 1, 20_000, "pass/play", gross, credited, tokens, supporter: true, earnedBefore,
            remainderBefore, remainder, 1);

    [Fact]
    public async Task TheBudgetMovesOnlyFromTheStateItWasPricedOnAndCountsASourceOnce()
    {
        await Commit(Award("a", 80, 80, 8, 0, 0, 0));
        Assert.Equal(new CrystalAccountState(80, 0), await _accounts.ReadAsync<CrystalAccountState>(new CrystalAccountReadPlan("live", "alice", 20_000)));

        // A second character priced on the same old state (earned 0) loses: nothing is written.
        await Assert.ThrowsAnyAsync<Exception>(() => Commit(Award("b", 20, 20, 2, 0, 0, 0)));
        // The same source again is refused even with the right state.
        await Assert.ThrowsAnyAsync<Exception>(() => Commit(Award("a", 20, 20, 2, 80, 0, 0)));
        Assert.Equal(new CrystalAccountState(80, 0), await _accounts.ReadAsync<CrystalAccountState>(new CrystalAccountReadPlan("live", "alice", 20_000)));

        await Commit(Award("b", 20, 20, 2, 80, 0, 0));
        var report = await _accounts.ReadAsync<CrystalWeekReport>(new CrystalWeekReportReadPlan("live", 20_000));
        Assert.Equal(100, report.Accounts.Single().Earned);
        Assert.Equal(10, report.Tokens);
        // The shadow ledger is separate.
        Assert.Equal(new CrystalAccountState(0, 0), await _accounts.ReadAsync<CrystalAccountState>(new CrystalAccountReadPlan("shadow", "alice", 20_000)));
    }

    private SupporterEntitlements Entitlements() => new(Options.Create(_options), _accounts, _clock, NullLogger<SupporterEntitlements>.Instance);

    [Fact]
    public async Task PaymentsExtendFromTheLaterOfNowAndTheEndOnceEachAndNeverPast365Days()
    {
        var entitlements = Entitlements();
        long now = _clock.Now.ToUnixTimeMilliseconds();
        Assert.False(entitlements.IsActive("Alice"));

        var first = await entitlements.ExtendAsync("paypal/CAP-1", "Alice", "$10", 30);
        Assert.Equal((EntitlementOutcome.Applied, now + 30 * SupporterEntitlements.DayMs), (first.Outcome, first.PaidThroughUtcMs));
        Assert.True(entitlements.IsActive("alice"));
        // A retried webhook adds nothing.
        Assert.Equal(EntitlementOutcome.Replayed, (await entitlements.ExtendAsync("paypal/CAP-1", "alice", "$10", 30)).Outcome);
        // The next purchase stacks on the end, not on today.
        Assert.Equal(now + 330 * SupporterEntitlements.DayMs, (await entitlements.ExtendAsync("paypal/CAP-2", "alice", "$100", 300)).PaidThroughUtcMs);
        // 330 + 60 > 365: refused, nothing written, so the same capture can be refunded instead.
        var refused = await entitlements.ExtendAsync("paypal/CAP-3", "alice", "$20", 60);
        Assert.Equal(EntitlementOutcome.Refused, refused.Outcome);
        Assert.Equal(now + 330 * SupporterEntitlements.DayMs, Entitlements().PaidThrough("alice"));

        // Expiry: active only while the end is ahead.
        _clock.Now = _clock.Now.AddDays(331);
        Assert.False(entitlements.IsActive("alice"));
        // After a lapse, a purchase starts from now.
        Assert.Equal(_clock.Now.ToUnixTimeMilliseconds() + 30 * SupporterEntitlements.DayMs,
            (await entitlements.ExtendAsync("paypal/CAP-4", "alice", "$10", 30)).PaidThroughUtcMs);
    }

    [Fact]
    public async Task PermanentSupportersGetTheirEndDateOnceAndOnlyWhenEntitlementsAreOnDoesItDecide()
    {
        var entitlements = Entitlements();
        var access = new AccessList(Options.Create(_options), NullLogger<AccessList>.Instance, entitlements);
        access.Run("supporter Alice");
        await entitlements.ExtendAsync("paypal/CAP-9", "bob", "$10", 30);

        // Off: the permanent list decides, as before.
        Assert.True(access.IsSupporter("alice"));
        Assert.False(access.IsSupporter("bob"));

        string reply = await entitlements.RunAsync("legacy 2026-12-01", access.LegacySupporters());
        Assert.Contains("Alice 2026-12-01 05:00 UTC", reply);
        Assert.Contains("already set", await entitlements.RunAsync("legacy 2026-12-01", access.LegacySupporters()));

        _options.Supporter.Entitlements = true;
        Assert.True(access.IsSupporter("alice"));
        Assert.True(access.IsSupporter("bob"));
        _clock.Now = new DateTimeOffset(2026, 12, 1, 5, 0, 0, TimeSpan.Zero);
        Assert.False(access.IsSupporter("alice")); // the end date passed
        Assert.True(access.IsSupporter("bob"));

        // A staff grant applies once per reference.
        Assert.Contains("paid through", await entitlements.RunAsync("grant alice 30 paypalme-0042", []));
        Assert.Contains("already granted", await entitlements.RunAsync("grant alice 30 paypalme-0042", []));
        Assert.True(access.IsSupporter("alice"));
    }
}
