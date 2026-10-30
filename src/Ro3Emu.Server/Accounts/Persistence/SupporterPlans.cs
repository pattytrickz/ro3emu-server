using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Supporter system v2 tables. crystal_week / crystal_token_remainder / crystal_source_receipt are the account-wide
/// Crystal budget's accounting, one set per ledger ('shadow' records what the budget would pay, 'live' what it paid);
/// the Crystals and tokens themselves stay in role currency and the economy ledger. supporter_entitlement holds each
/// account's paid-through time, supporter_entitlement_ledger every change to it, keyed by its source so a retried
/// payment or grant applies once. Accounts are lower-case names, times UTC milliseconds.
/// </summary>
internal sealed class SupporterSchemaPlan : DbPlan
{
    internal override int Bytes => 1024;
    internal override string Label => "supporter/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS crystal_week (
                ledger TEXT NOT NULL CHECK(ledger IN ('shadow','live')), account TEXT NOT NULL, week INTEGER NOT NULL,
                earned INTEGER NOT NULL CHECK(earned >= 0), gross INTEGER NOT NULL CHECK(gross >= 0),
                PRIMARY KEY(ledger, account, week));
            CREATE TABLE IF NOT EXISTS crystal_token_remainder (
                ledger TEXT NOT NULL CHECK(ledger IN ('shadow','live')), account TEXT NOT NULL,
                remainder INTEGER NOT NULL CHECK(remainder >= 0), tokens INTEGER NOT NULL CHECK(tokens >= 0),
                PRIMARY KEY(ledger, account));
            CREATE TABLE IF NOT EXISTS crystal_source_receipt (
                source_key TEXT PRIMARY KEY, ledger TEXT NOT NULL, account TEXT NOT NULL, role_id INTEGER NOT NULL,
                week INTEGER NOT NULL, category TEXT NOT NULL, gross INTEGER NOT NULL, credited INTEGER NOT NULL,
                tokens INTEGER NOT NULL, supporter INTEGER NOT NULL CHECK(supporter IN (0,1)), recorded_utc_ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS crystal_source_receipt_week ON crystal_source_receipt(ledger, week);
            CREATE TABLE IF NOT EXISTS supporter_entitlement (
                account TEXT PRIMARY KEY, paid_through_utc_ms INTEGER NOT NULL, updated_utc_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS supporter_entitlement_ledger (
                source_key TEXT PRIMARY KEY, account TEXT NOT NULL, reason TEXT NOT NULL,
                before_utc_ms INTEGER NOT NULL, after_utc_ms INTEGER NOT NULL, recorded_utc_ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS supporter_entitlement_ledger_account ON supporter_entitlement_ledger(account);
            CREATE TABLE IF NOT EXISTS supporter_order (
                id INTEGER PRIMARY KEY AUTOINCREMENT, account TEXT NOT NULL, provider TEXT NOT NULL,
                tier_cad INTEGER NOT NULL, amount_cents INTEGER NOT NULL, currency TEXT NOT NULL,
                days INTEGER NOT NULL, tokens INTEGER NOT NULL, state TEXT NOT NULL,
                provider_order_id TEXT UNIQUE, capture_id TEXT UNIQUE, reversed_cents INTEGER NOT NULL DEFAULT 0,
                created_utc_ms INTEGER NOT NULL, captured_utc_ms INTEGER NOT NULL DEFAULT 0, updated_utc_ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS supporter_order_account ON supporter_order(account);
            CREATE TABLE IF NOT EXISTS supporter_payment_event (
                event_key TEXT PRIMARY KEY, order_id INTEGER NOT NULL, kind TEXT NOT NULL, amount_cents INTEGER NOT NULL,
                outcome TEXT NOT NULL, recorded_utc_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS supporter_token_mail (
                order_id INTEGER PRIMARY KEY, account TEXT NOT NULL, role_id INTEGER NOT NULL, tokens INTEGER NOT NULL,
                delivery_key TEXT NOT NULL UNIQUE, sent_utc_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS supporter_purchase (
                account TEXT NOT NULL, week INTEGER NOT NULL, commodity INTEGER NOT NULL, count INTEGER NOT NULL CHECK(count >= 0),
                PRIMARY KEY(account, week, commodity));
            """);
        return 1;
    }
}

/// <summary>An account's budget state in one ledger: Crystals counted this week and the token remainder (0 when unset).</summary>
internal sealed record CrystalAccountState(long Earned, long Remainder);

internal sealed class CrystalAccountReadPlan(string ledger, string account, long week) : DbReadPlan
{
    internal override int Bytes => 256 + account.Length * 2;
    internal override string Label => "crystal/read-account";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            SELECT (SELECT earned FROM crystal_week WHERE ledger=$ledger AND account=$account AND week=$week),
                   (SELECT remainder FROM crystal_token_remainder WHERE ledger=$ledger AND account=$account)
            """);
        cmd.Parameters.AddWithValue("$ledger", ledger);
        cmd.Parameters.AddWithValue("$account", account);
        cmd.Parameters.AddWithValue("$week", week);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new CrystalAccountState(reader.IsDBNull(0) ? 0 : reader.GetInt64(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
    }
}

/// <summary>The Crystal budget lost a race (its cached state no longer matches the database); nothing was written.</summary>
internal sealed class CrystalBudgetConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// One Crystal award's budget accounting, written in the award's own transaction: the week's counter and the token
/// remainder move only from the state the award was priced on (compare-and-set), and the receipt makes a source count
/// once. Any mismatch throws, rolling the whole award back so it is priced again.
/// </summary>
internal sealed class CrystalAwardPlan(string sourceKey, string ledger, string account, long roleId, long week,
    string category, long gross, long credited, long tokens, bool supporter, long expectedEarned, long expectedRemainder,
    long remainder, long recordedUtcMs) : DbPlan
{
    internal override int Bytes => 512 + (sourceKey.Length + account.Length) * 2;
    internal override string Label => "crystal/award";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using (var seen = DbSql.Command(db, tx, "SELECT 1 FROM crystal_source_receipt WHERE source_key=$key"))
        {
            seen.Parameters.AddWithValue("$key", sourceKey);
            if (seen.ExecuteScalar() != null) throw new CrystalBudgetConflictException($"Crystal source {sourceKey} was already counted.");
        }
        using (var week = DbSql.Command(db, tx, """
            INSERT INTO crystal_week(ledger,account,week,earned,gross) VALUES($ledger,$account,$week,0,0)
                ON CONFLICT(ledger,account,week) DO NOTHING;
            UPDATE crystal_week SET earned=earned+$credited, gross=gross+$gross
                WHERE ledger=$ledger AND account=$account AND week=$week AND earned=$expected;
            """))
        {
            Bind(week);
            week.Parameters.AddWithValue("$credited", credited);
            week.Parameters.AddWithValue("$gross", gross);
            week.Parameters.AddWithValue("$expected", expectedEarned);
            week.ExecuteNonQuery();
            if (Changes(db, tx) != 1) throw new CrystalBudgetConflictException($"Crystal week of {account} changed before the award.");
        }
        if (supporter)
        {
            using var rest = DbSql.Command(db, tx, """
                INSERT INTO crystal_token_remainder(ledger,account,remainder,tokens) VALUES($ledger,$account,0,0)
                    ON CONFLICT(ledger,account) DO NOTHING;
                UPDATE crystal_token_remainder SET remainder=$remainder, tokens=tokens+$tokens
                    WHERE ledger=$ledger AND account=$account AND remainder=$expected;
                """);
            Bind(rest);
            rest.Parameters.AddWithValue("$remainder", remainder);
            rest.Parameters.AddWithValue("$tokens", tokens);
            rest.Parameters.AddWithValue("$expected", expectedRemainder);
            rest.ExecuteNonQuery();
            if (Changes(db, tx) != 1) throw new CrystalBudgetConflictException($"Token remainder of {account} changed before the award.");
        }
        using var receipt = DbSql.Command(db, tx, """
            INSERT INTO crystal_source_receipt(source_key,ledger,account,role_id,week,category,gross,credited,tokens,supporter,recorded_utc_ms)
                VALUES($key,$ledger,$account,$role,$week,$category,$gross,$credited,$tokens,$supporter,$now)
            """);
        Bind(receipt);
        receipt.Parameters.AddWithValue("$key", sourceKey);
        receipt.Parameters.AddWithValue("$role", roleId);
        receipt.Parameters.AddWithValue("$category", category);
        receipt.Parameters.AddWithValue("$gross", gross);
        receipt.Parameters.AddWithValue("$credited", credited);
        receipt.Parameters.AddWithValue("$tokens", tokens);
        receipt.Parameters.AddWithValue("$supporter", supporter ? 1 : 0);
        receipt.Parameters.AddWithValue("$now", recordedUtcMs);
        return receipt.ExecuteNonQuery();
    }

    private void Bind(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("$ledger", ledger);
        cmd.Parameters.AddWithValue("$account", account);
        cmd.Parameters.AddWithValue("$week", week);
    }

    private static long Changes(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT changes()");
        return (long)cmd.ExecuteScalar()!;
    }
}

/// <summary>One week of a ledger, for the operator's report: per account the Crystals counted and offered.</summary>
internal sealed record CrystalWeekRow(string Account, long Earned, long Gross);
internal sealed record CrystalWeekReport(IReadOnlyList<CrystalWeekRow> Accounts, long Tokens, long SupporterAccounts);

internal sealed class CrystalWeekReportReadPlan(string ledger, long week) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "crystal/read-week";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var rows = new List<CrystalWeekRow>();
        using (var cmd = DbSql.Command(db, tx, "SELECT account, earned, gross FROM crystal_week WHERE ledger=$ledger AND week=$week"))
        {
            cmd.Parameters.AddWithValue("$ledger", ledger);
            cmd.Parameters.AddWithValue("$week", week);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }
        using var tokens = DbSql.Command(db, tx, """
            SELECT COALESCE(SUM(tokens),0), COUNT(DISTINCT CASE WHEN supporter=1 THEN account END)
                FROM crystal_source_receipt WHERE ledger=$ledger AND week=$week
            """);
        tokens.Parameters.AddWithValue("$ledger", ledger);
        tokens.Parameters.AddWithValue("$week", week);
        using var sum = tokens.ExecuteReader();
        sum.Read();
        return new CrystalWeekReport(rows, sum.GetInt64(0), sum.GetInt64(1));
    }
}

internal sealed class SupporterEntitlementReadPlan : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "supporter/read-entitlements";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var all = new Dictionary<string, long>(StringComparer.Ordinal);
        using var cmd = DbSql.Command(db, tx, "SELECT account, paid_through_utc_ms FROM supporter_entitlement");
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) all[reader.GetString(0)] = reader.GetInt64(1);
        return all;
    }
}

/// <summary>What an entitlement change did: applied now, applied before (same source), or refused (over the limit).</summary>
internal enum EntitlementOutcome { Applied, Replayed, Refused }
internal sealed record EntitlementResult(EntitlementOutcome Outcome, long PaidThroughUtcMs)
{
    // Writer results are journaled (OperationReceipts), so the plan returns plain longs.
    internal long[] Encode() => [(long)Outcome, PaidThroughUtcMs];
    internal static EntitlementResult Decode(object? value) => value is long[] { Length: 2 } v
        ? new((EntitlementOutcome)v[0], v[1]) : throw new InvalidDataException("Unexpected entitlement result.");
}

/// <summary>
/// Moves an account's paid-through time once per source. Extend adds <c>amountMs</c> from the later of now and the
/// current end, refused when that would be more than <c>maxAheadMs</c> ahead (nothing written, so a payment is never
/// half-applied). AtLeast moves the end to <c>amountMs</c> (a UTC time) unless it is already later: the end date given
/// to the permanent supporters of data/access.json.
/// </summary>
internal sealed class SupporterEntitlementPlan(string sourceKey, string account, string reason, bool extend, long amountMs,
    long nowUtcMs, long maxAheadMs) : DbPlan
{
    internal override int Bytes => 384 + (sourceKey.Length + account.Length + reason.Length) * 2;
    internal override string Label => "supporter/entitlement";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx) => EntitlementSql.Apply(db, tx, sourceKey, account,
        reason, extend ? EntitlementChange.Extend : EntitlementChange.AtLeast, amountMs, nowUtcMs, maxAheadMs).Encode();
}

/// <summary>How a ledger entry moves the end: Extend adds a length from the later of now and the end; AtLeast moves
/// the end to a time unless it is later; Reduce takes a length off the end, never before now (a refunded purchase).</summary>
internal enum EntitlementChange { Extend, AtLeast, Reduce }

/// <summary>The one place an account's paid-through time changes, inside the caller's transaction (a payment's
/// capture writes its order and its entitlement together).</summary>
internal static class EntitlementSql
{
    internal static EntitlementResult Apply(SqliteConnection db, SqliteTransaction tx, string sourceKey, string account,
        string reason, EntitlementChange change, long amountMs, long nowUtcMs, long maxAheadMs)
    {
        using (var seen = DbSql.Command(db, tx, "SELECT after_utc_ms FROM supporter_entitlement_ledger WHERE source_key=$key"))
        {
            seen.Parameters.AddWithValue("$key", sourceKey);
            if (seen.ExecuteScalar() is long applied) return new EntitlementResult(EntitlementOutcome.Replayed, applied);
        }
        long current;
        using (var read = DbSql.Command(db, tx, "SELECT paid_through_utc_ms FROM supporter_entitlement WHERE account=$account"))
        {
            read.Parameters.AddWithValue("$account", account);
            current = read.ExecuteScalar() as long? ?? 0;
        }
        long next = change switch
        {
            EntitlementChange.Extend => Math.Max(nowUtcMs, current) + amountMs,
            EntitlementChange.AtLeast => Math.Max(current, amountMs),
            _ => Math.Max(Math.Min(current, nowUtcMs), current - amountMs),
        };
        if (change == EntitlementChange.Extend && next - nowUtcMs > maxAheadMs) return new EntitlementResult(EntitlementOutcome.Refused, current);
        using var write = DbSql.Command(db, tx, """
            INSERT INTO supporter_entitlement(account,paid_through_utc_ms,updated_utc_ms) VALUES($account,$next,$now)
                ON CONFLICT(account) DO UPDATE SET paid_through_utc_ms=$next, updated_utc_ms=$now;
            INSERT INTO supporter_entitlement_ledger(source_key,account,reason,before_utc_ms,after_utc_ms,recorded_utc_ms)
                VALUES($key,$account,$reason,$current,$next,$now);
            """);
        write.Parameters.AddWithValue("$account", account);
        write.Parameters.AddWithValue("$next", next);
        write.Parameters.AddWithValue("$now", nowUtcMs);
        write.Parameters.AddWithValue("$key", sourceKey);
        write.Parameters.AddWithValue("$reason", reason);
        write.Parameters.AddWithValue("$current", current);
        write.ExecuteNonQuery();
        return new EntitlementResult(EntitlementOutcome.Applied, next);
    }
}

/// <summary>An account's Supporter Merchant purchases in a pass week: commodity id -> count.</summary>
internal sealed class SupporterPurchaseReadPlan(string account, long week) : DbReadPlan
{
    internal override int Bytes => 256 + account.Length * 2;
    internal override string Label => "supporter/read-purchases";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var counts = new Dictionary<int, int>();
        using var cmd = DbSql.Command(db, tx, "SELECT commodity, count FROM supporter_purchase WHERE account=$account AND week=$week");
        cmd.Parameters.AddWithValue("$account", account);
        cmd.Parameters.AddWithValue("$week", week);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) counts[reader.GetInt32(0)] = reader.GetInt32(1);
        return counts;
    }
}

/// <summary>Over a Supporter Merchant weekly limit: thrown inside the purchase's transaction, so nothing is paid.</summary>
internal sealed class SupporterLimitException(string message) : InvalidOperationException(message);

/// <summary>
/// Counts a Supporter Merchant purchase in the purchase's own transaction: the commodity's account-wide weekly limit
/// and, for a product in a group (the two EXP boosters), the group's. Over either, it throws and the purchase rolls back.
/// </summary>
internal sealed class SupporterPurchasePlan(string account, long week, int commodity, int count, int limit, int[] group,
    int groupLimit) : DbPlan
{
    internal override int Bytes => 256 + account.Length * 2 + group.Length * 4;
    internal override string Label => "supporter/purchase";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        long Sum(IEnumerable<int> commodities)
        {
            var ids = commodities.ToArray();
            if (ids.Length == 0) return 0;
            using var cmd = DbSql.Command(db, tx, $"SELECT COALESCE(SUM(count),0) FROM supporter_purchase WHERE account=$account AND week=$week AND commodity IN ({string.Join(',', ids)})");
            cmd.Parameters.AddWithValue("$account", account);
            cmd.Parameters.AddWithValue("$week", week);
            return (long)cmd.ExecuteScalar()!;
        }
        if (Sum([commodity]) + count > limit) throw new SupporterLimitException($"weekly limit {limit} of commodity {commodity}");
        if (group.Length > 0 && Sum(group) + count > groupLimit) throw new SupporterLimitException($"weekly group limit {groupLimit}");
        using var write = DbSql.Command(db, tx, """
            INSERT INTO supporter_purchase(account,week,commodity,count) VALUES($account,$week,$commodity,$count)
                ON CONFLICT(account,week,commodity) DO UPDATE SET count=count+$count
            """);
        write.Parameters.AddWithValue("$account", account);
        write.Parameters.AddWithValue("$week", week);
        write.Parameters.AddWithValue("$commodity", commodity);
        write.Parameters.AddWithValue("$count", count);
        return write.ExecuteNonQuery();
    }
}
