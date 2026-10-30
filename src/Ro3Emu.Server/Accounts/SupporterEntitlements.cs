using System.Globalization;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Supporter status as a paid-through time per account (owner's plan v1.1 §2.1, ServerOptions.Supporter.Entitlements).
/// A day is 24 hours; a purchase extends from the later of now and the current end, never more than 365 days ahead.
/// Every change has a source key (a payment capture, a staff grant, the legacy end date) and applies once. The ends
/// are cached here, the database is the truth. AccessList.IsSupporter asks this when entitlements are on.
/// </summary>
public sealed class SupporterEntitlements(IOptions<ServerOptions> options, AccountStore accounts, TimeProvider clock,
    ILogger<SupporterEntitlements> log)
{
    internal const long DayMs = 86_400_000, MaxAheadMs = 365 * DayMs;

    /// <summary>The fixed contribution tiers (CAD): every tier has the same perks, only the length and tokens scale.</summary>
    internal static readonly (int Cad, int Days, int Tokens)[] Tiers = [(10, 30, 100), (20, 60, 200), (30, 90, 300), (50, 150, 500), (100, 300, 1000)];

    private readonly object _sync = new();
    private Dictionary<string, long>? _paidThrough;

    public bool Enabled => options.Value.Supporter.Entitlements;

    private static string Key(string account) => account.ToLowerInvariant();
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private Dictionary<string, long> Loaded()
    {
        lock (_sync)
            return _paidThrough ??= accounts.ReadSync<Dictionary<string, long>>(new SupporterEntitlementReadPlan());
    }

    /// <summary>The account's paid-through time (UTC ms), or null when it never had one.</summary>
    public long? PaidThrough(string account)
    {
        var all = Loaded();
        lock (_sync) return all.TryGetValue(Key(account), out long end) ? end : null;
    }

    public bool IsActive(string account) => PaidThrough(account) is long end && end > Now;

    /// <summary>Adds <paramref name="days"/> once per <paramref name="sourceKey"/>; refused past 365 days ahead.</summary>
    internal Task<EntitlementResult> ExtendAsync(string sourceKey, string account, string reason, int days) =>
        ApplyAsync(new SupporterEntitlementPlan(sourceKey, Key(account), reason, extend: true, days * DayMs, Now, MaxAheadMs), account);

    /// <summary>Moves the end to at least <paramref name="until"/>, once per <paramref name="sourceKey"/>.</summary>
    internal Task<EntitlementResult> AtLeastAsync(string sourceKey, string account, string reason, DateTimeOffset until) =>
        ApplyAsync(new SupporterEntitlementPlan(sourceKey, Key(account), reason, extend: false, until.ToUnixTimeMilliseconds(), Now, MaxAheadMs), account);

    private async Task<EntitlementResult> ApplyAsync(SupporterEntitlementPlan plan, string account)
    {
        Loaded();
        var tx = accounts.NewTransaction();
        int index = tx.Add(plan);
        var result = EntitlementResult.Decode((await tx.CommitAsync(accounts, "supporter/entitlement"))[index]);
        if (result.Outcome != EntitlementOutcome.Refused)
            lock (_sync) _paidThrough![Key(account)] = result.PaidThroughUtcMs;
        log.LogInformation("Supporter entitlement {Account}: {Outcome}, paid through {End}", account, result.Outcome, Date(result.PaidThroughUtcMs));
        return result;
    }

    internal static string Date(long utcMs) => utcMs <= 0 ? "never"
        : DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public const string Usage = "status <name> | grant <name> <days> <reference> | legacy <yyyy-mm-dd> | list";

    /// <summary>
    /// The staff side (GM "!supporter", the host's loopback tool). "grant" is for a contribution staff checked by hand
    /// (PayPal.Me, until verified checkout): the reference makes it apply once. "legacy" gives every permanent supporter
    /// of data/access.json (<paramref name="legacy"/>) an end date: at least that day, 05:00 UTC (the game's day turn).
    /// </summary>
    public async Task<string> RunAsync(string line, IReadOnlyList<string> legacy)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        string mode = Enabled ? "Entitlements are ON: status comes from these dates."
            : "Entitlements are OFF: status still comes from data/access.json (Supporter:Entitlements turns them on).";
        switch (verb)
        {
            case "status" when parts.Length == 2:
                return $"{parts[1]}: {(IsActive(parts[1]) ? "active" : "not active")}, paid through {Date(PaidThrough(parts[1]) ?? 0)}. {mode}";
            case "grant" when parts.Length == 4 && int.TryParse(parts[2], out int days) && days is > 0 and <= 365:
            {
                var result = await ExtendAsync("manual/" + parts[3], parts[1], "staff grant " + parts[3], days);
                return result.Outcome switch
                {
                    EntitlementOutcome.Refused => $"Refused: {parts[1]} would be paid more than 365 days ahead (now {Date(result.PaidThroughUtcMs)}).",
                    EntitlementOutcome.Replayed => $"Reference {parts[3]} was already granted: {parts[1]} is paid through {Date(PaidThrough(parts[1]) ?? 0)}.",
                    _ => $"{parts[1]} is paid through {Date(result.PaidThroughUtcMs)}. {mode}",
                };
            }
            case "legacy" when parts.Length == 2 && DateTimeOffset.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var day):
            {
                var until = day.AddHours(5);
                if (until.ToUnixTimeMilliseconds() <= Now) return "Pick a day in the future.";
                var lines = new List<string>();
                foreach (string name in legacy)
                {
                    var result = await AtLeastAsync("legacy/" + Key(name), name, "permanent supporter end date", until);
                    lines.Add($"{name} {Date(result.PaidThroughUtcMs)}{(result.Outcome == EntitlementOutcome.Replayed ? " (already set)" : "")}");
                }
                return lines.Count == 0 ? "data/access.json lists no supporters." : "Legacy supporters: " + string.Join(", ", lines) + ". " + mode;
            }
            case "list":
            {
                var all = Loaded();
                List<KeyValuePair<string, long>> active;
                lock (_sync) active = all.Where(e => e.Value > Now).OrderBy(e => e.Value).ToList();
                return (active.Count == 0 ? "No active supporters." : "Active supporters: "
                    + string.Join(", ", active.Select(e => $"{e.Key} until {Date(e.Value)}")) + ".") + " " + mode;
            }
            default:
                return "Usage: " + Usage;
        }
    }
}
