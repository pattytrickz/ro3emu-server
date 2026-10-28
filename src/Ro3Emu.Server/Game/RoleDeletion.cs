using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Deleting one of an account's roles, for the role screen's Delete button (LOGIN_ROLE_DELETE 1008,
/// AccountDeleteRoleReq: the original message; the client's own button is hidden and unbound, Sol RO3's client hook
/// wires it) and the HTTP tool with the account's password (ServerHost /ro3emu/account/delete-role). A role in the world, on any
/// session, is refused; the row stays and its name is released (RoleStore.Delete). No grace period: AccountDeleteRoleReq
/// isCancel (the original's pending-deletion countdown) is refused (inferred, Sol RO3 deletes at once).
/// </summary>
public sealed partial class RoleDeletion(RoleStore roles, SessionRegistry sessions, Ownership.OwnerRegistry owners,
    ILogger<RoleDeletion> log)
{
    /// <summary>AccountDeleteRoleResp results (Sol RO3's, read by its client hook): done, not the account's role, in the
    /// world, not deleted, a cancel (no countdown to cancel).</summary>
    public const int Deleted = 0, NotOwned = 404, InWorld = 409, Failed = 500, NoCountdown = 501;

    /// <summary>1008 on the role screen: the session's own account only.</summary>
    internal async Task DeleteAsync(GameSession session, AccountDeleteRoleReq req)
    {
        int result = req.IsCancel ? NoCountdown
            : session.Account is { } account ? (await DeleteAsync(account, req.Id, "role screen")).Code : NotOwned;
        await session.SendGameAsync(MsgId.LoginRoleDelete, new AccountDeleteRoleResp { Result = result });
    }

    /// <summary>On the game loop: deletes <paramref name="roleId"/> if it is <paramref name="account"/>'s and no session
    /// has it in the world. A code above, and the deleted role's name.</summary>
    internal async Task<(int Code, string Message)> DeleteAsync(string account, long roleId, string source)
    {
        if (roles.Get(roleId) is not { } role || role.Account != account) return (NotOwned, "no such character on this account");
        using var lifecycle = await owners.EnterAsync("role delete", Ownership.ReservationKey.Lifecycle(roleId));
        var current = roles.Get(roleId);
        if (current == null || current.Account != account) return (NotOwned, "no such character on this account");
        if (InWorld(roleId)) return (InWorld, "the character is in the game; log out first");
        bool ok = roles.Delete(account, roleId);
        if (ok) owners.RoleDeleted(roleId, roles.ReadRevisionVector(roleId).Epoch);
        LogDelete(log, account, current.Name, roleId, source, ok);
        return ok ? (Deleted, $"{current.Name} deleted") : (Failed, "not deleted");
    }

    private bool InWorld(long roleId)
    {
        var owner = owners.Cached(roleId);
        return owner is { State: not Ownership.RoleOwnerState.Detached } || owner?.Attachment != null || owner?.Detaching != null
            || sessions.All().Any(s => s.Role?.Id == roleId || s.Owner?.RoleId == roleId || s.Attachment?.RoleId == roleId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Account {Account} deletes role {Name} ({Id}) from the {Source}: {Result}")]
    private static partial void LogDelete(ILogger logger, string account, string name, long id, string source, bool result);
}
