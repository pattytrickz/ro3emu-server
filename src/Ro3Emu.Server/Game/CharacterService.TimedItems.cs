using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    // ---- timed items ----

    /// <summary>
    /// ItemConfig._iTimeBoundEnum 1: _kTimeBoundPara [minutes] (Spirit Conqueror (24 Hours) 1950015: [1440]).
    /// (inferred) The minutes count from when the role receives the item; the other kinds (2 a calendar window, 3, 4)
    /// are only on test rows and are not timed here.
    /// </summary>
    internal const long TimeBoundFromReceipt = 1;

    /// <summary>ItemBaseInfo.expireTm (unix seconds, inferred like ItemBindInfo.expireTimeStamp) for a new instance of
    /// <paramref name="entry"/>; 0 when it never lapses.</summary>
    private long TimedExpiry(int entry) => TimedMinutes(entry) is > 0 and var minutes
        ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + minutes * 60 : 0;

    private long TimedMinutes(int entry) =>
        ItemConfig.Get(data, entry) is { TimeBoundEnum: TimeBoundFromReceipt, TimeBoundPara: [var minutes, ..] } ? minutes : 0;

    /// <summary>A timed item past its expireTm: the client shows it as Expired, it can no longer be used, and a lapse
    /// return (31327 isLapseDecompose) turns it into its _iSeasonLapseProduct.</summary>
    internal static bool IsExpired(InvItem item) =>
        item.ExpireTm > 0 && item.ExpireTm <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Timed items received before their expiry was stored (they had expireTm 0, so the client showed them as Expired
    /// at once) get their full duration from this login: when they were received is unknown (inferred). The rows are
    /// written before the session sees the new values.
    /// </summary>
    private void StampTimedItems(GameSession session)
    {
        var unstamped = session.Items.Where(i => i.ExpireTm == 0 && TimedMinutes(i.Entry) > 0).ToList();
        if (unstamped.Count == 0) return;
        var replacements = new Dictionary<InvItem, InvItem>(ReferenceEqualityComparer.Instance);
        var builder = roles.NewTransaction();
        foreach (var old in unstamped)
        {
            var item = old.Clone();
            item.ExpireTm = TimedExpiry(item.Entry);
            builder.ItemUpdate(item);
            replacements[old] = item;
        }
        roles.Commit(builder, "character/timed-item-expiry");
        session.Items = session.Items.Select(i => replacements.GetValueOrDefault(i, i)).ToList();
    }
}
