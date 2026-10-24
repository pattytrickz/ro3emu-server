using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>Narrow detached role projections for micro auth/social; role mutations remain transitional legacy.</summary>
internal enum RoleProjectionKind { ForAccount, ById, ByName, Wardrobe, All }

internal sealed class RoleProjectionRead(RoleProjectionKind kind, string? text = null, long id = 0) : DbReadPlan
{
    internal override int Bytes => 128 + (text?.Length ?? 0) * 2;
    internal override string Label => "role/micro-projection";
    internal override object? Execute(SqliteConnection db, SqliteTransaction tx)
    {
        if (kind == RoleProjectionKind.Wardrobe)
        {
            using var wardrobe = DbSql.Command(db, tx, "SELECT wardrobe FROM role WHERE id=$p AND deleted_time=0");
            wardrobe.Parameters.AddWithValue("$p", id);
            return wardrobe.ExecuteScalar() is byte[] blob ? (byte[])blob.Clone() : null;
        }
        string sql = kind switch
        {
            RoleProjectionKind.ForAccount => "SELECT * FROM role WHERE account=$p AND deleted_time=0 ORDER BY last_login_time DESC",
            RoleProjectionKind.ById => "SELECT * FROM role WHERE id=$p AND deleted_time=0",
            RoleProjectionKind.ByName => "SELECT * FROM role WHERE name=$p COLLATE NOCASE AND deleted_time=0",
            // Every live role without its blobs: the GM gift tool's recipient list.
            RoleProjectionKind.All => "SELECT id,account,area,name,sex,career,level,hair,create_time,last_login_time,scene_entry,"
                + "pos_x,pos_y,pos_z,face FROM role WHERE deleted_time=0 ORDER BY id",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var cmd = DbSql.Command(db, tx, sql);
        if (kind != RoleProjectionKind.All) cmd.Parameters.AddWithValue("$p", kind == RoleProjectionKind.ById ? id : text!);
        var roles = new List<Role>();
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            roles.Add(new Role(rows.GetInt64(rows.GetOrdinal("id")), rows.GetString(rows.GetOrdinal("account")),
                rows.GetInt32(rows.GetOrdinal("area")), rows.GetString(rows.GetOrdinal("name")),
                rows.GetInt32(rows.GetOrdinal("sex")), rows.GetInt32(rows.GetOrdinal("career")),
                rows.GetInt32(rows.GetOrdinal("level")), rows.GetInt32(rows.GetOrdinal("hair")),
                rows.GetInt64(rows.GetOrdinal("create_time")), rows.GetInt64(rows.GetOrdinal("last_login_time")),
                rows.GetInt64(rows.GetOrdinal("scene_entry")), rows.GetFloat(rows.GetOrdinal("pos_x")),
                rows.GetFloat(rows.GetOrdinal("pos_y")), rows.GetFloat(rows.GetOrdinal("pos_z")),
                rows.GetFloat(rows.GetOrdinal("face"))));
        }
        return roles;
    }
}
