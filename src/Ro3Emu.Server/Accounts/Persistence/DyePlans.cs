using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// The Dye Merchant's unlocks (Shared.CharacterAppearanceRules): one row per account and colour, kept forever. source
/// says how it came: "crystals" or "tokens" (bought), "bottle" (a first-catalogue dye bottle used), "worn" (the colour a
/// character already wore when unlocks began), "gm" (granted by staff). Accounts are lower-case names, times UTC ms.
/// </summary>
internal sealed class DyeSchemaPlan : DbPlan
{
    internal override int Bytes => 256;
    internal override string Label => "dye/schema";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS dye_unlock (
                account TEXT NOT NULL, palette INTEGER NOT NULL CHECK(palette > 0 AND palette < 32), source TEXT NOT NULL,
                unlocked_utc_ms INTEGER NOT NULL, PRIMARY KEY(account, palette));
            """);
        return 1;
    }
}

/// <summary>An account's unlocked palettes as a bit mask (bit n = palette n).</summary>
internal sealed class DyeUnlockReadPlan(string account) : DbReadPlan
{
    internal override int Bytes => 128 + account.Length * 2;
    internal override string Label => "dye/read-unlocks";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = DbSql.Command(db, tx, "SELECT palette FROM dye_unlock WHERE account=$account");
        cmd.Parameters.AddWithValue("$account", account);
        using var reader = cmd.ExecuteReader();
        long mask = 0;
        while (reader.Read()) mask |= 1L << reader.GetInt32(0);
        return mask;
    }
}

/// <summary>Unlocks palettes for an account; an existing unlock is kept as it was. Returns the rows added.</summary>
internal sealed class DyeUnlockPlan(string account, IReadOnlyList<int> palettes, string source, long now) : DbPlan
{
    internal override int Bytes => 256 + account.Length * 2 + palettes.Count * 8;
    internal override string Label => "dye/unlock";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        int added = 0;
        foreach (int palette in palettes)
        {
            using var cmd = DbSql.Command(db, tx, """
                INSERT INTO dye_unlock(account,palette,source,unlocked_utc_ms) VALUES($account,$palette,$source,$now)
                    ON CONFLICT(account,palette) DO NOTHING
                """);
            cmd.Parameters.AddWithValue("$account", account);
            cmd.Parameters.AddWithValue("$palette", palette);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$now", now);
            added += cmd.ExecuteNonQuery();
        }
        return added;
    }
}
