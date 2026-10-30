using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// One fixed-tier checkout (owner's plan v1.1 §4.2). A row is made before the buyer goes to the provider, bound to the
/// provider's order id, and captured once; its state is created → captured (or needs-refund when the capture would put
/// the account past the 365-day limit) → refunded / reversed / partially-refunded / disputed. Provider is "paypal" or
/// "paypal-sandbox".
/// </summary>
internal sealed record SupporterOrderRow(long Id, string Account, string Provider, int TierCad, long AmountCents,
    string Currency, int Days, int Tokens, string State, string? ProviderOrderId, string? CaptureId, long ReversedCents,
    long CreatedUtcMs, long CapturedUtcMs)
{
    internal const string Columns = "id,account,provider,tier_cad,amount_cents,currency,days,tokens,state,provider_order_id," +
        "capture_id,reversed_cents,created_utc_ms,captured_utc_ms";

    internal static SupporterOrderRow Read(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.GetInt32(3), r.GetInt64(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7), r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13));

    internal static SupporterOrderRow? Find(SqliteConnection db, SqliteTransaction tx, string where, string name, object value)
    {
        using var cmd = DbSql.Command(db, tx, $"SELECT {Columns} FROM supporter_order WHERE {where}");
        cmd.Parameters.AddWithValue(name, value);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }
}

/// <summary>An order by id, or (id 0) by its capture id.</summary>
internal sealed class SupporterOrderReadPlan(long id, string? captureId = null) : DbReadPlan
{
    internal override int Bytes => 256 + (captureId?.Length ?? 0) * 2;
    internal override string Label => "supporter/read-order";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx) => captureId == null
        ? SupporterOrderRow.Find(db, tx, "id=$v", "$v", id)
        : SupporterOrderRow.Find(db, tx, "capture_id=$v", "$v", captureId);
}

/// <summary>Every order of one provider (GM "!supporter sandbox-clear").</summary>
internal sealed class SupporterOrderListReadPlan(string provider) : DbReadPlan
{
    internal override int Bytes => 256 + provider.Length * 2;
    internal override string Label => "supporter/read-orders";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var rows = new List<SupporterOrderRow>();
        using var cmd = DbSql.Command(db, tx, $"SELECT {SupporterOrderRow.Columns} FROM supporter_order WHERE provider=$provider ORDER BY id");
        cmd.Parameters.AddWithValue("$provider", provider);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(SupporterOrderRow.Read(reader));
        return rows;
    }
}

internal sealed class SupporterOrderCreatePlan(string account, string provider, int tierCad, long amountCents, string currency,
    int days, int tokens, long nowUtcMs) : DbPlan
{
    internal override int Bytes => 384 + (account.Length + provider.Length) * 2;
    internal override string Label => "supporter/order-create";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            INSERT INTO supporter_order(account,provider,tier_cad,amount_cents,currency,days,tokens,state,created_utc_ms,updated_utc_ms)
                VALUES($account,$provider,$tier,$amount,$currency,$days,$tokens,'created',$now,$now);
            SELECT last_insert_rowid();
            """);
        cmd.Parameters.AddWithValue("$account", account);
        cmd.Parameters.AddWithValue("$provider", provider);
        cmd.Parameters.AddWithValue("$tier", tierCad);
        cmd.Parameters.AddWithValue("$amount", amountCents);
        cmd.Parameters.AddWithValue("$currency", currency);
        cmd.Parameters.AddWithValue("$days", days);
        cmd.Parameters.AddWithValue("$tokens", tokens);
        cmd.Parameters.AddWithValue("$now", nowUtcMs);
        return (long)cmd.ExecuteScalar()!;
    }
}

/// <summary>Ties a created order to the provider's order id, once: 1 when bound (or already bound to the same id).</summary>
internal sealed class SupporterOrderBindPlan(long id, string providerOrderId, long nowUtcMs) : DbPlan
{
    internal override int Bytes => 256 + providerOrderId.Length * 2;
    internal override string Label => "supporter/order-bind";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            UPDATE supporter_order SET provider_order_id=$provider, updated_utc_ms=$now
                WHERE id=$id AND state='created' AND (provider_order_id IS NULL OR provider_order_id=$provider)
            """);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$provider", providerOrderId);
        cmd.Parameters.AddWithValue("$now", nowUtcMs);
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>What a capture or reversal did. Plans return [code, paid-through ms] (journaled writer results are plain).</summary>
internal enum PaymentOutcome { Applied, Replayed, NeedsRefund, UnknownOrder, Mismatch, AlreadyCaptured, BadState, Recorded }

internal sealed record PaymentResult(PaymentOutcome Outcome, long PaidThroughUtcMs)
{
    internal long[] Encode() => [(long)Outcome, PaidThroughUtcMs];
    internal static PaymentResult Decode(object? value) => value is long[] { Length: 2 } v
        ? new((PaymentOutcome)v[0], v[1]) : throw new InvalidDataException("Unexpected payment result.");
}

/// <summary>
/// A verified completed capture: the order (matched on its provider order id, amount and currency) and the account's
/// paid-through time change together. The capture id is the idempotency key, so a webhook retried or racing the
/// buyer's return page applies once. A capture that would put the account more than 365 days ahead is recorded as
/// needs-refund and changes no time, so a payment is never accepted with its days silently dropped.
/// </summary>
internal sealed class SupporterCapturePlan(long orderId, string providerOrderId, string captureId, long amountCents,
    string currency, long nowUtcMs, long maxAheadMs) : DbPlan
{
    internal override int Bytes => 512 + (providerOrderId.Length + captureId.Length) * 2;
    internal override string Label => "supporter/capture";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        var order = SupporterOrderRow.Find(db, tx, "id=$v", "$v", orderId);
        if (order == null) return new PaymentResult(PaymentOutcome.UnknownOrder, 0).Encode();
        long paidThrough = PaidThrough(db, tx, order.Account);
        if (order.CaptureId == captureId) return new PaymentResult(PaymentOutcome.Replayed, paidThrough).Encode();
        if (order.CaptureId != null) return new PaymentResult(PaymentOutcome.AlreadyCaptured, paidThrough).Encode();
        if (order.ProviderOrderId != providerOrderId || order.AmountCents != amountCents || order.Currency != currency)
            return new PaymentResult(PaymentOutcome.Mismatch, paidThrough).Encode();
        if (order.State != "created") return new PaymentResult(PaymentOutcome.BadState, paidThrough).Encode();
        var entitlement = EntitlementSql.Apply(db, tx, order.Provider + "/" + captureId, order.Account,
            $"{order.Provider} ${order.TierCad} order {order.Id}", EntitlementChange.Extend, order.Days * 86_400_000L, nowUtcMs, maxAheadMs);
        bool refused = entitlement.Outcome == EntitlementOutcome.Refused;
        using (var cmd = DbSql.Command(db, tx, """
            UPDATE supporter_order SET state=$state, capture_id=$capture, captured_utc_ms=$now, updated_utc_ms=$now WHERE id=$id;
            INSERT INTO supporter_payment_event(event_key,order_id,kind,amount_cents,outcome,recorded_utc_ms)
                VALUES('capture/' || $capture,$id,'capture',$amount,$state,$now);
            """))
        {
            cmd.Parameters.AddWithValue("$state", refused ? "needs-refund" : "captured");
            cmd.Parameters.AddWithValue("$capture", captureId);
            cmd.Parameters.AddWithValue("$now", nowUtcMs);
            cmd.Parameters.AddWithValue("$id", orderId);
            cmd.Parameters.AddWithValue("$amount", amountCents);
            cmd.ExecuteNonQuery();
        }
        return new PaymentResult(refused ? PaymentOutcome.NeedsRefund : PaymentOutcome.Applied, entitlement.PaidThroughUtcMs).Encode();
    }

    internal static long PaidThrough(SqliteConnection db, SqliteTransaction tx, string account)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT paid_through_utc_ms FROM supporter_entitlement WHERE account=$account");
        cmd.Parameters.AddWithValue("$account", account);
        return cmd.ExecuteScalar() as long? ?? 0;
    }
}

/// <summary>
/// A verified refund, reversal or dispute of a capture, once per provider event. Refunds and reversals add up; once the
/// whole amount is back, the purchase's days come off the end (never before now) and the order is refunded/reversed. A
/// partial refund or a dispute changes no time: the order is marked for staff (partially-refunded, disputed).
/// </summary>
internal sealed class SupporterReversePlan(string eventKey, string captureId, string kind, long amountCents, long nowUtcMs) : DbPlan
{
    internal override int Bytes => 384 + (eventKey.Length + captureId.Length + kind.Length) * 2;
    internal override string Label => "supporter/reverse";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using (var seen = DbSql.Command(db, tx, "SELECT 1 FROM supporter_payment_event WHERE event_key=$key"))
        {
            seen.Parameters.AddWithValue("$key", eventKey);
            if (seen.ExecuteScalar() != null)
            {
                var known = SupporterOrderRow.Find(db, tx, "capture_id=$v", "$v", captureId);
                return new PaymentResult(PaymentOutcome.Replayed, known == null ? 0 : SupporterCapturePlan.PaidThrough(db, tx, known.Account)).Encode();
            }
        }
        var order = SupporterOrderRow.Find(db, tx, "capture_id=$v", "$v", captureId);
        if (order == null) return new PaymentResult(PaymentOutcome.UnknownOrder, 0).Encode();
        string state = order.State;
        long reversed = order.ReversedCents;
        var outcome = PaymentOutcome.Recorded;
        if (kind == "dispute")
        {
            if (state == "captured") state = "disputed";
        }
        else
        {
            reversed = Math.Min(order.AmountCents, reversed + Math.Max(0, amountCents));
            if (reversed < order.AmountCents) state = state is "captured" or "disputed" ? "partially-refunded" : state;
            else if (state is not ("refunded" or "reversed"))
            {
                // A needs-refund order never added days, so it has none to take back.
                if (state != "needs-refund")
                    EntitlementSql.Apply(db, tx, order.Provider + "-reversal/" + captureId, order.Account,
                        $"{kind} of {order.Provider} order {order.Id}", EntitlementChange.Reduce, order.Days * 86_400_000L, nowUtcMs, 0);
                state = kind == "reversal" ? "reversed" : "refunded";
                outcome = PaymentOutcome.Applied;
            }
        }
        using (var cmd = DbSql.Command(db, tx, """
            UPDATE supporter_order SET state=$state, reversed_cents=$reversed, updated_utc_ms=$now WHERE id=$id;
            INSERT INTO supporter_payment_event(event_key,order_id,kind,amount_cents,outcome,recorded_utc_ms)
                VALUES($key,$id,$kind,$amount,$state,$now);
            """))
        {
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$reversed", reversed);
            cmd.Parameters.AddWithValue("$now", nowUtcMs);
            cmd.Parameters.AddWithValue("$id", order.Id);
            cmd.Parameters.AddWithValue("$key", eventKey);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$amount", amountCents);
            cmd.ExecuteNonQuery();
        }
        return new PaymentResult(outcome, SupporterCapturePlan.PaidThrough(db, tx, order.Account)).Encode();
    }
}

/// <summary>Verified contributions of one provider between two times (captured orders, refunds netted out):
/// [gross cents, refunded cents, orders]. The website's and Discord's funding goal read this one number.</summary>
internal sealed class SupporterFundingReadPlan(string provider, long fromUtcMs, long toUtcMs) : DbReadPlan
{
    internal override int Bytes => 256;
    internal override string Label => "supporter/read-funding";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, """
            SELECT COALESCE(SUM(amount_cents),0), COALESCE(SUM(reversed_cents),0), COUNT(*) FROM supporter_order
                WHERE provider=$provider AND capture_id IS NOT NULL AND state <> 'needs-refund'
                  AND captured_utc_ms >= $from AND captured_utc_ms < $to
            """);
        cmd.Parameters.AddWithValue("$provider", provider);
        cmd.Parameters.AddWithValue("$from", fromUtcMs);
        cmd.Parameters.AddWithValue("$to", toUtcMs);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new long[] { reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) };
    }
}
