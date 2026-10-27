using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>One batch of <see cref="ReceiptCompaction"/>: how far it got and whether it reached the retention window.</summary>
internal sealed record ReceiptCompactionBatch(long ThroughSeq, int Visited, int Compacted, bool Caught);

/// <summary>
/// Settled receipts whose payload only a retry of the same operation reads: their command kind, the GLOB their source
/// keys match (a source that is never replayed later) and the name of the position they advance.
/// - **monster-kill:** a kill's source names that one spawn.
/// - **pass-play:** a pass/play award (NewActivityService.PassPlay) is sourced by the role's play sequence, which only
///   grows. Its payload and outcome are the role's whole activity state, about 20 KB a receipt (27 Oct: 85,000
///   receipts, 1.8 GB of the realm database).
/// </summary>
internal sealed record ReceiptCompactionRule(string Name, string Kind, string SourceGlob)
{
    internal static readonly ReceiptCompactionRule Kills = new("monster-kill", "monster-kill", "*"),
        PassPlay = new("pass-play", "event/newactivity", "newactivity/*/pass/play/*");
    internal static readonly ReceiptCompactionRule[] All = [Kills, PassPlay];
}

/// <summary>
/// Compacts the settled receipts of one <see cref="ReceiptCompactionRule"/> older than the retention cutoff, in commit
/// order from that rule's saved position.
/// - **Kept:** the receipt row (operation id, source key, hashes, sequence, leg count), its operation_source claim
///   and its economy_entry legs. Duplicate-source protection (RewardSourceReadPlan) reads operation_source only.
/// - **Emptied:** intent_payload, outcome, child_receipts and the revision vectors, which only a retry of that same
///   operation reads (DbWriter replay and the economy resolve plans), seconds after the commit.
/// - **Deleted:** the matching owner_operation_receipt acknowledgement (family economy/&lt;kind&gt;), read the same way.
/// A full ledger audit (EconomyLedgerReader) of a compacted range reports these receipts as incomplete; the live
/// server never runs it.
/// </summary>
internal sealed class ReceiptCompactionPlan(long cutoffUtcMs, int batch, ReceiptCompactionRule rule) : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "economy/receipt-compaction";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        // One position per rule. The kill position of the single-rule version carries over, so kills are not rescanned.
        using (var schema = DbSql.Command(db, tx, """
            CREATE TABLE IF NOT EXISTS receipt_compaction (singleton INTEGER PRIMARY KEY CHECK(singleton=1), through_seq INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS receipt_compaction_cursor (rule TEXT PRIMARY KEY, through_seq INTEGER NOT NULL);
            INSERT OR IGNORE INTO receipt_compaction_cursor(rule,through_seq)
                SELECT 'monster-kill',through_seq FROM receipt_compaction WHERE singleton=1;
            """))
            schema.ExecuteNonQuery();
        long from;
        using (var read = DbSql.Command(db, tx, "SELECT through_seq FROM receipt_compaction_cursor WHERE rule=$rule"))
        {
            read.Parameters.AddWithValue("$rule", rule.Name);
            from = read.ExecuteScalar() is long saved ? saved : 0;
        }

        // Commit order, stopping at the first receipt inside the retention window (times only grow with it).
        long through = from;
        int visited = 0;
        bool caught = false;
        var candidates = new List<string>();
        using (var scan = DbSql.Command(db, tx,
            "SELECT commit_seq,recorded_utc_ms,command_kind,operation_id,length(intent_payload) FROM operation_receipt " +
            "WHERE commit_seq>$from ORDER BY commit_seq LIMIT $batch"))
        {
            scan.Parameters.AddWithValue("$from", from);
            scan.Parameters.AddWithValue("$batch", batch);
            using var row = scan.ExecuteReader();
            while (row.Read())
            {
                if (row.GetInt64(1) >= cutoffUtcMs) { caught = true; break; }
                through = row.GetInt64(0);
                visited++;
                if (row.GetString(2) == rule.Kind && row.GetInt64(4) > 0) candidates.Add(row.GetString(3));
            }
            if (visited < batch) caught = true;
        }

        // The source GLOB is checked here, so an acknowledgement goes only with a receipt this rule emptied.
        using var compact = DbSql.Command(db, tx,
            "UPDATE operation_receipt SET intent_payload=X'',outcome=X'',child_receipts=X'',before_revisions='',after_revisions='' " +
            "WHERE operation_id=$op AND command_kind=$kind AND source_key GLOB $glob");
        var op = compact.Parameters.Add("$op", SqliteType.Text);
        compact.Parameters.AddWithValue("$kind", rule.Kind);
        compact.Parameters.AddWithValue("$glob", rule.SourceGlob);
        using var forget = DbSql.Command(db, tx, "DELETE FROM owner_operation_receipt WHERE operation_id=$op AND family=$family");
        var ownerOp = forget.Parameters.Add("$op", SqliteType.Text);
        forget.Parameters.AddWithValue("$family", "economy/" + rule.Kind);
        int compacted = 0;
        foreach (string id in candidates)
        {
            op.Value = id;
            if (compact.ExecuteNonQuery() == 0) continue;
            compacted++;
            ownerOp.Value = id;
            forget.ExecuteNonQuery();
        }

        using (var save = DbSql.Command(db, tx,
            "INSERT INTO receipt_compaction_cursor(rule,through_seq) VALUES($rule,$through) " +
            "ON CONFLICT(rule) DO UPDATE SET through_seq=excluded.through_seq"))
        {
            save.Parameters.AddWithValue("$rule", rule.Name);
            save.Parameters.AddWithValue("$through", through);
            save.ExecuteNonQuery();
        }
        return new ReceiptCompactionBatch(through, visited, compacted, caught);
    }
}
