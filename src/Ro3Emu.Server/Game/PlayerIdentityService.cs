using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Maps;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Game;

/// <summary>Sol RO3 supporter/GM identity (inferred policy), carried by the existing typed custom-data wire.</summary>
public sealed class PlayerIdentityService(AccessList access, IOptions<ServerOptions> options, SessionRegistry sessions,
    SocialOutfitService cosmetics, World world, GameLoop loop) : IGameTicker
{
    private TimeSpan _lastScan;
    private readonly HashSet<GameSession> _pending = [];

    internal int Badge(string? account) => account != null && options.Value.GmAccounts.Contains(account, StringComparer.Ordinal)
        ? SupporterIdentityRules.GameMaster : access.IsSupporter(account) ? SupporterIdentityRules.Supporter : SupporterIdentityRules.Ordinary;

    internal void Load(GameSession s)
    {
        s.PublicIdentityBadge = Badge(s.Account);
        s.SupporterIdentity = access.IsSupporter(s.Account);
    }

    internal async Task ClientReadyAsync(GameSession s, ReadOnlyMemory<byte> body)
    {
        if (body.Length != 1 || body.Span[0] != 1 || s.IdentityClientReady || s.SocialOutfit == null) return;
        s.IdentityClientReady = true; // capability only; it never grants supporter or GM status
        s.Synced.Remove(SocialOutfitService.Sync);
        await cosmetics.SyncAsync(s);
    }

    // The explicit GM account list is authoritative. GmOpenWhenEmpty and loopback tool accounts never label players GM.
    internal static CustomData Marker(GameSession s) => new()
    {
        Id = SupporterIdentityRules.BadgeCustomData,
        CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = s.PublicIdentityBadge }
    };

    public void Tick(GameTime time)
    {
        if (time.Now - _lastScan < TimeSpan.FromSeconds(1)) return;
        _lastScan = time.Now;
        foreach (var s in sessions.All())
        {
            if (s.Role == null || s.SceneLoading || s.SocialOutfit == null
                || s.PublicIdentityBadge == Badge(s.Account) && s.SupporterIdentity == access.IsSupporter(s.Account)
                || !_pending.Add(s)) continue;
            var attachment = s.Attachment;
            loop.PostLegacy(async () =>
            {
                try
                {
                    using var held = await CharacterService.Lock(s);
                    if (attachment == null || !ReferenceEquals(attachment, s.Attachment) || s.Role == null || s.SceneLoading
                        || !s.Owners!.AllowsPresentation(s)) return;
                    await RefreshAsync(s);
                }
                finally { _pending.Remove(s); }
            }, "player identity");
        }
    }

    private async Task RefreshAsync(GameSession s)
    {
        Load(s);
        await cosmetics.SyncAsync(s);
        var packet = new CustomDataChange { EntityId = s.Role!.Id, CustomDatas = { Marker(s), CharacterAppearanceService.Marker(s) } };
        await RoleFanout.EachAsync(world.Viewers(s).Prepend(s), p => p.SendGameAsync(WorldCoreService.CustomChange, packet));
    }
}
