using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The account-wide weekly Crystal budget (owner's plan v1.1 §2.4, ServerOptions.Supporter.CrystalEconomy). Repeatable
/// play Crystals (pass challenges and MVP fights, NewActivityService.PassPlay) share one budget per account and pass
/// week, over all its characters; an award past it pays what is left. Active supporters earn one Supporter Token per
/// <see cref="SupporterOptions.CrystalsPerToken"/> Crystals actually paid, the remainder kept for the next award. Pass
/// level rewards are a season reward outside the budget, and staff grants never count. "shadow" prices every award
/// the same way into its own ledger without changing what is paid, so the owner can see who the cap would touch.
/// One account's awards are priced one at a time (<see cref="LockAsync"/>) and written with a compare-and-set
/// (CrystalAwardPlan), so two characters can't both spend the same remaining budget.
/// </summary>
public sealed class CrystalEconomy(IOptions<ServerOptions> options, AccountStore accounts, ILogger<CrystalEconomy> log)
{
    internal enum Mode { Off, Shadow, Live }

    internal Mode Current => options.Value.Supporter.CrystalEconomy.Trim().ToLowerInvariant() switch
    {
        "shadow" => Mode.Shadow,
        "on" or "live" => Mode.Live,
        _ => Mode.Off,
    };

    internal int Cap => Math.Max(0, options.Value.Supporter.WeeklyCrystalCap);

    private static string Ledger(Mode mode) => mode == Mode.Live ? "live" : "shadow";
    private static string Key(string account) => account.ToLowerInvariant();

    /// <summary>What an award of <paramref name="gross"/> pays with <paramref name="earned"/> already counted this week,
    /// and the tokens and new remainder for a supporter. The cap is never exceeded and nothing is rounded away.</summary>
    internal static (long Credited, long Tokens, long Remainder) Price(long gross, long earned, long cap, bool supporter,
        long remainder, long perToken)
    {
        long credited = Math.Clamp(cap - earned, 0, Math.Max(0, gross));
        if (!supporter || perToken <= 0) return (credited, 0, remainder);
        long pool = remainder + credited;
        return (credited, pool / perToken, pool % perToken);
    }

    /// <summary>One priced award, and the state it was priced on.</summary>
    internal sealed record Quote(Mode Mode, string Account, long Week, long Gross, long Credited, long Tokens, bool Supporter,
        long Earned, long Remainder, long NextRemainder, long Cap)
    {
        internal bool Live => Mode == Mode.Live;
        internal long EarnedAfter => Earned + Credited;
    }

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Ledger, string Account), (long Week, CrystalAccountState State)> _cache = new();

    /// <summary>Held from pricing an award until its commit is done: one account's awards never interleave.</summary>
    internal async Task<IDisposable> LockAsync(string account)
    {
        var gate = _locks.GetOrAdd(Key(account), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }

    private async Task<CrystalAccountState> StateAsync(Mode mode, string account, long week)
    {
        if (_cache.TryGetValue((Ledger(mode), account), out var hit) && hit.Week == week) return hit.State;
        var state = await accounts.ReadAsync<CrystalAccountState>(new CrystalAccountReadPlan(Ledger(mode), account, week));
        _cache[(Ledger(mode), account)] = (week, state);
        return state;
    }

    internal async Task<Quote> QuoteAsync(Mode mode, string account, long week, long gross, bool supporter)
    {
        string key = Key(account);
        var state = await StateAsync(mode, key, week);
        var (credited, tokens, next) = Price(gross, state.Earned, Cap, supporter, state.Remainder, options.Value.Supporter.CrystalsPerToken);
        return new Quote(mode, key, week, gross, credited, tokens, supporter, state.Earned, state.Remainder, next, Cap);
    }

    internal CrystalAwardPlan Plan(Quote quote, string sourceKey, long roleId, long nowUtcMs) => new(sourceKey, Ledger(quote.Mode),
        quote.Account, roleId, quote.Week, "pass/play", quote.Gross, quote.Credited, quote.Tokens, quote.Supporter, quote.Earned,
        quote.Remainder, quote.NextRemainder, nowUtcMs);

    /// <summary>The award's transaction committed: the cache moves with it.</summary>
    internal void Committed(Quote quote) => _cache[(Ledger(quote.Mode), quote.Account)] =
        (quote.Week, new CrystalAccountState(quote.EarnedAfter, quote.Supporter ? quote.NextRemainder : quote.Remainder));

    /// <summary>The award failed or lost a race: the next one reads the database again.</summary>
    internal void Forget(Quote quote)
    {
        _cache.TryRemove((Ledger(quote.Mode), quote.Account), out _);
        log.LogInformation("Crystal budget of {Account} re-read after a failed award", quote.Account);
    }

    /// <summary>The live budget counted for the account this week (for /pass).</summary>
    internal async Task<long> EarnedAsync(string account, long week) => (await StateAsync(Mode.Live, Key(account), week)).Earned;

    internal static string WeekName(long week) =>
        DateOnly.FromDayNumber((int)week).ToString("ddd d MMM", CultureInfo.InvariantCulture) + " 05:00 UTC";

    public const string Usage = "report [shadow|live] [weeks back]";

    /// <summary>GM "!crystals report": who the budget touched in a week (the current one by default).</summary>
    internal async Task<string> ReportAsync(string[] args, long currentWeek)
    {
        string ledger = args.Contains("live", StringComparer.OrdinalIgnoreCase) ? "live"
            : args.Contains("shadow", StringComparer.OrdinalIgnoreCase) ? "shadow" : Ledger(Current);
        int back = args.Select(a => int.TryParse(a, out int n) ? n : 0).FirstOrDefault(n => n > 0);
        long week = currentWeek - 7L * back;
        var report = await accounts.ReadAsync<CrystalWeekReport>(new CrystalWeekReportReadPlan(ledger, week));
        string head = $"Crystal budget ({ledger}, mode {Current.ToString().ToLowerInvariant()}, cap {Cap}/account), week of {WeekName(week)}: ";
        if (report.Accounts.Count == 0) return head + "no awards recorded.";
        var earned = report.Accounts.Select(a => a.Earned).Order().ToArray();
        long gross = report.Accounts.Sum(a => a.Gross), paid = earned.Sum();
        return head + $"{earned.Length} accounts earned, {report.Accounts.Count(a => a.Earned >= Cap)} reached the cap; "
            + $"median {earned[earned.Length / 2]}, highest {earned[^1]}; offered {gross}, paid {paid}, held back {gross - paid}; "
            + $"{report.Tokens} Supporter Tokens to {report.SupporterAccounts} supporter accounts.";
    }
}
