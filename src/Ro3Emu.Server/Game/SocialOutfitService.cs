using Google.Protobuf;
using System.Text.Json;
using System.Security.Cryptography;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Profile cosmetics: MG_SocialOutfit 83480/83481 and 5044 DBSocialOutfit.
/// LC_DAL_SocialOutfit updates the selection from 5044, not the empty equip acknowledgement.</summary>
public sealed class SocialOutfitService
{
    private readonly GameData data;
    private readonly RoleStore roles;
    private readonly CharacterService character;
    private readonly TimeProvider clock;

    public SocialOutfitService(GameData data, RoleStore roles, CharacterService character, TimeProvider clock)
    {
        this.data = data;
        this.roles = roles;
        this.character = character;
        this.clock = clock;
        character.SpecialItemUse.Add(TryUseItemAsync);
    }

    public const uint Sync = 5044, Equip = 83480, Refresh = 83481;

    private DBSocialOutfit Load(GameSession session)
    {
        var saved = roles.LoadSocialOutfit(session.Role!.Id);
        var state = saved is { Length: > 0 } ? DBSocialOutfit.Parser.ParseFrom(saved) : new DBSocialOutfit();
        // The profile's avatar list includes the current job and its ancestors.
        var careers = new HashSet<long>();
        for (long job = session.Progress!.Career; job != 0 && careers.Add(job);)
            job = data.Row("JobConfig", job) is { } jobRow ? GameData.Int(jobRow, "_iParentId") : 0;
        foreach (var row in data.Table("SocialOutfitConfig").Values)
        {
            if (GameData.Int(row, "_iIsDefaultUnLock") != 1) continue;
            int type = (int)GameData.Int(row, "_iType");
            long id = GameData.Int(row, "_iID");
            if (type is < 1 or > 6 || id <= 0) continue;
            long sex = GameData.Int(row, "_iSex"), job = GameData.Int(row, "_iJobID");
            if (sex != 0 && sex != session.Role.Sex || job != 0 && !careers.Contains(job)) continue;
            if (!state.SocialOutfitContainer.TryGetValue(type, out var container))
                state.SocialOutfitContainer[type] = container = new DBSocialOutfitContainer();
            if (!container.SocialOutfitInfos.ContainsKey(id))
                container.SocialOutfitInfos[id] = new DBSocialOutfitInfo { CfgId = id, Status = (int)SocialOutfitStatus.Permanent };
            if (!state.SocialOutfitEquip.ContainsKey(type) && GameData.Int(row, "_iIsDefaultWear") == 1)
                state.SocialOutfitEquip[type] = new DBSocialOutfitEquip { CfgId = id };
        }
        return state;
    }

    public Task LoginAsync(GameSession session) => CharacterService.SyncAsync(session, Sync, Load(session));

    public async Task EquipAsync(GameSession session, EquipSocialOutfitReq request)
    {
        using var gate = await CharacterService.Lock(session);
        var state = Load(session);
        var row = data.Row("SocialOutfitConfig", request.CfgId);
        int result = request.Type is < 1 or > 6 || row == null || GameData.Int(row.Value, "_iType") != request.Type
            ? ErrorCodes.Parameter : 0;
        if (result == 0)
        {
            int type = (int)request.Type;
            bool owned = state.SocialOutfitContainer.TryGetValue(type, out var container)
                && container.SocialOutfitInfos.TryGetValue(request.CfgId, out var item)
                && (item.Status == (int)SocialOutfitStatus.Permanent
                    || item.Status == (int)SocialOutfitStatus.TimeLimit && item.ExpireTimestamp > clock.GetUtcNow().ToUnixTimeSeconds());
            if (!owned) result = ErrorCodes.NoPermission;
            else
            {
                state.SocialOutfitEquip[type] = new DBSocialOutfitEquip { CfgId = request.CfgId };
                roles.SaveSocialOutfit(session.Role!.Id, state.ToByteArray());
            }
        }
        await session.SendGameAsync(Equip, new EquipSocialOutfitRes { Err = result });
        if (result == 0) await CharacterService.SyncAsync(session, Sync, state);
    }

    public async Task RefreshAsync(GameSession session)
    {
        using var gate = await CharacterService.Lock(session);
        await CharacterService.SyncAsync(session, Sync, Load(session));
        await session.SendGameAsync(Refresh, new RefreshSocialOutfitRes());
    }

    /// <summary>Item action 349 names SocialOutfitWearConfig, not SocialOutfitConfig directly.
    /// Permanent, unconditional unlocks spend one copy and save ownership in the same economy transaction.
    /// Unsupported timed/conditional grants leave the item untouched.</summary>
    private async Task<int?> TryUseItemAsync(GameSession session, ItemUseReq request, InvItem item, JsonElement row, int count)
    {
        if (!row.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array
            || effects.GetArrayLength() == 0 || effects[0].ValueKind != JsonValueKind.Array
            || effects[0].GetArrayLength() == 0 || GameData.Number(effects[0][0]) != 349) return null;
        if (count != 1 || effects.GetArrayLength() != 1 || effects[0].GetArrayLength() != 2) return ErrorCodes.Parameter;
        var wear = data.Row("SocialOutfitWearConfig", (long)(GameData.Number(effects[0][1]) ?? 0));
        if (wear == null || GameData.Int(wear.Value, "_iTimeLimit") != -1
            || GameData.Int(wear.Value, "_iCondition") != 0) return ErrorCodes.FeatureUnavailable;
        long id = GameData.Int(wear.Value, "_iSocialOutfit");
        var outfit = data.Row("SocialOutfitConfig", id);
        if (outfit == null || GameData.Int(outfit.Value, "_iType") is < 1 or > 6) return ErrorCodes.Parameter;
        int type = (int)GameData.Int(outfit.Value, "_iType");
        var next = Load(session);
        if (!next.SocialOutfitContainer.TryGetValue(type, out var container))
            next.SocialOutfitContainer[type] = container = new DBSocialOutfitContainer();
        if (container.SocialOutfitInfos.TryGetValue(id, out var owned)
            && owned.Status == (int)SocialOutfitStatus.Permanent) return ErrorCodes.Parameter;
        container.SocialOutfitInfos[id] = new DBSocialOutfitInfo { CfgId = id, Status = (int)SocialOutfitStatus.Permanent };
        byte[] saved = next.ToByteArray();
        var spent = await character.ExchangeLockedAsync(session, [], [], new Dictionary<long, int> { [item.Guid] = 1 },
            popup: false, persist: tx =>
            {
                tx.RoleBlob(session.Role!.Id, "social_outfit", saved);
                tx.WithEconomy(character.FamilyEconomy(session, Economy.CharacterFamilyEconomy.SocialOutfitItemKind,
                    [(item.Entry, 1L)], [], new { Outfit = id, Item = item.Entry, StateHash = Convert.ToHexString(SHA256.HashData(saved)) }));
            }, asyncCommit: true);
        if (spent == null) return ErrorCodes.ItemsShort;
        await CharacterService.SyncAsync(session, Sync, next);
        return 0;
    }
}
