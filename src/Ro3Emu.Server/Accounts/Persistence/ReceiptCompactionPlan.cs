using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>One batch of <see cref="ReceiptCompaction"/>: how far it got and whether it reached the retention window.</summary>
internal sealed record ReceiptCompactionBatch(long ThroughSeq, int Visited, int Compacted, bool Caught);

/// <summary>
/// Compacts settled monster-kill receipts older than the retention cutoff, in commit order from a saved position.
/// - **Kept:** the receipt row (operation id, source key, hashes, sequence, leg count), its operation_source claim
///   and its economy_entry legs. Duplicate-source protection (RewardSourceReadPlan) reads operation_source only.
/// - **Emptied:** intent_payload, outcome, child_receipts and the revision vectors, which only a retry of that same
///   operation reads (DbWriter replay and the economy resolve plans), seconds after the kill.
/// - **Deleted:** the matching owner_operation_receipt acknowledgement (family economy/monster-kill), read the same way.
/// A full ledger audit (EconomyLedgerReader) of a compacted range reports these receipts as incomplete; the live
/// server never runs it.
/// </summary>
internal sealed class ReceiptCompactionPlan(long cutoffUtcMs, int batch) : DbPlan
{
    internal const string Kind = "monster-kill";
    internal override int Bytes => 256;
    internal override string Label => "economy/receipt-compaction";

    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using (var schema = DbSql.Command(db, tx,
            "CREATE TABLE IF NOT EXISTS receipt_compaction (singleton INTEGER PRIMARY KEY CHECK(singleton=1), through_seq INTEGER NOT NULL)"))
            schema.ExecuteNonQuery();
        long from;
        using (var read = DbSql.Command(db, tx, "SELECT through_seq FROM receipt_compaction WHERE singleton=1"))
            from = read.ExecuteScalar() is long saved ? saved : 0;

        // Commit order, stopping at the first receipt inside the retention window (times only grow with it).
        long through = from;
        int visited = 0;
        bool caught = false;
        var kills = new List<string>();
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
                if (row.GetString(2) == Kind && row.GetInt64(4) > 0) kills.Add(row.GetString(3));
            }
            if (visited < batch) caught = true;
        }

        using var compact = DbSql.Command(db, tx,
            "UPDATE operation_receipt SET intent_payload=X'',outcome=X'',child_receipts=X'',before_revisions='',after_revisions='' " +
            "WHERE operation_id=$op AND command_kind='" + Kind + "'");
        var op = compact.Parameters.Add("$op", SqliteType.Text);
        using var forget = DbSql.Command(db, tx,
            "DELETE FROM owner_operation_receipt WHERE operation_id=$op AND family='economy/" + Kind + "'");
        var ownerOp = forget.Parameters.Add("$op", SqliteType.Text);
        foreach (string id in kills)
        {
            op.Value = id;
            compact.ExecuteNonQuery();
            ownerOp.Value = id;
            forget.ExecuteNonQuery();
        }

        using (var save = DbSql.Command(db, tx,
            "INSERT INTO receipt_compaction(singleton,through_seq) VALUES(1,$through) " +
            "ON CONFLICT(singleton) DO UPDATE SET through_seq=excluded.through_seq"))
        {
            save.Parameters.AddWithValue("$through", through);
            save.ExecuteNonQuery();
        }
        return new ReceiptCompactionBatch(through, visited, kills.Count, caught);
    }
}
