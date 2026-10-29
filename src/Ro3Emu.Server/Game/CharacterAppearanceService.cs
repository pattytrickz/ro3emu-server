using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Maps;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Game;

/// <summary>Server-owned cosmetic colours and explicit-GM aura preference. No character stats are changed.</summary>
public sealed class CharacterAppearanceService
{
    public sealed record State(int Hair = 0, int Armour = 0, bool Aura = false);
    private readonly RoleStore _roles;
    private readonly CharacterService _character;
    private readonly World _world;
    private readonly ServerOptions _options;

    public CharacterAppearanceService(RoleStore roles, CharacterService character, World world,
        IOptions<ServerOptions> options, GmCommands gm)
    {
        _roles = roles; _character = character; _world = world; _options = options.Value;
        if (_options.CharacterDyePrice is <= 0 or > 999_999_999) throw new ArgumentOutOfRangeException(nameof(options), "Dye price must be 1..999999999.");
        character.SpecialItemUse.Insert(0, TryUseAsync);
        gm.Register("aura", "on|off (explicit GM cosmetic aura)", AuraAsync);
    }

    internal void Load(GameSession s)
    {
        var bytes = _roles.LoadCharacterAppearance(s.Role!.Id);
        var state = bytes == null ? new State() : JsonSerializer.Deserialize<State>(bytes) ?? new State();
        s.CharacterAppearance = state with
        {
            Hair = CharacterAppearanceRules.ValidPalette(state.Hair) ? state.Hair : 0,
            Armour = CharacterAppearanceRules.ValidPalette(state.Armour) ? state.Armour : 0
        };
        s.CharacterAppearanceEnabled = _options.CharacterAppearanceEnabled;
    }

    internal async Task ClientReadyAsync(GameSession s, ReadOnlyMemory<byte> body)
    {
        if (body.Length != 1 || body.Span[0] != 1 || s.CharacterAppearanceClientReady || s.CharacterAppearance == null) return;
        using var held = await CharacterService.Lock(s);
        s.CharacterAppearanceClientReady = true;
        await s.SendGameAsync(WorldCoreService.CustomChange, new CustomDataChange
        {
            EntityId = s.Role!.Id,
            CustomDatas = { Marker(s), LongMarker(CharacterAppearanceRules.PriceCustomData,
                s.CharacterAppearanceEnabled ? _options.CharacterDyePrice : 0) }
        });
        await _character.ResendAppearanceItemsAsync(s);
    }

    internal static CustomData Marker(GameSession s)
    {
        var state = s.CharacterAppearance ?? new State();
        long value = s.CharacterAppearanceEnabled ? CharacterAppearanceRules.Pack(state.Hair, state.Armour,
            state.Aura && s.PublicIdentityBadge == SupporterIdentityRules.GameMaster) : 0;
        return LongMarker(CharacterAppearanceRules.SelectionCustomData, value);
    }

    private static CustomData LongMarker(long id, long value) => new()
    {
        Id = id, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value }
    };

    internal static bool CanBuy(GameSession s, int commodity)
    {
        if (!CharacterAppearanceRules.IsCommodity(commodity)) return true;
        int item = CharacterAppearanceRules.ItemForCommodity(commodity);
        return s.CharacterAppearanceEnabled && s.CharacterAppearanceClientReady
            && (!CharacterAppearanceRules.IsArmour(item) || CharacterAppearanceRules.Palette(item) == 0
                || CharacterAppearanceRules.SupportsArmour(s.Progress?.Career ?? 0, s.Role?.Sex ?? 0));
    }

    private async Task<int?> TryUseAsync(GameSession s, ItemUseReq req, InvItem item, JsonElement row, int count)
    {
        if (!CharacterAppearanceRules.IsDye(item.Entry)) return null;
        if (!s.CharacterAppearanceEnabled || !s.CharacterAppearanceClientReady || s.CharacterAppearance == null)
            return ErrorCodes.FeatureUnavailable;
        if (count != 1 || req.ItemParam.Count != 0) return ErrorCodes.Parameter;
        bool armour = CharacterAppearanceRules.IsArmour(item.Entry);
        int palette = CharacterAppearanceRules.Palette(item.Entry);
        if (armour && palette != 0 && !CharacterAppearanceRules.SupportsArmour(s.Progress!.Career, s.Role!.Sex))
            return ErrorCodes.FeatureUnavailable;
        var next = armour ? s.CharacterAppearance with { Armour = palette } : s.CharacterAppearance with { Hair = palette };
        if (next == s.CharacterAppearance) return ErrorCodes.Parameter; // already selected: do not consume another dye
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        var spent = await _character.ExchangeLockedAsync(s, [], [], new Dictionary<long, int> { [item.Guid] = 1 }, popup: false,
            persist: tx =>
            {
                tx.RoleBlob(s.Role!.Id, "character_appearance", bytes);
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/wardrobe-dye", [(item.Entry, 1L)], [],
                    new { Item = item.Entry, StateHash = Convert.ToHexString(SHA256.HashData(bytes)) }));
            }, committed: () => s.CharacterAppearance = next, asyncCommit: true);
        if (spent == null) return ErrorCodes.ItemsShort;
        await PublishAsync(s);
        return 0;
    }

    private async Task AuraAsync(GameSession s, string[] args)
    {
        if (!s.CharacterAppearanceEnabled || !_options.GmAccounts.Contains(s.Account, StringComparer.Ordinal))
        { await GmCommands.ReplyAsync(s, "The aura is available only to explicitly configured GMs on enabled realms."); return; }
        if (args.Length != 1 || args[0] is not ("on" or "off"))
        { await GmCommands.ReplyAsync(s, "Use !aura on or !aura off."); return; }
        using var held = await CharacterService.Lock(s);
        var next = (s.CharacterAppearance ?? new State()) with { Aura = args[0] == "on" };
        var tx = _roles.NewTransaction();
        tx.RoleBlob(s.Role!.Id, "character_appearance", JsonSerializer.SerializeToUtf8Bytes(next));
        await _roles.CommitAsync(tx, "character appearance/aura", _ => s.CharacterAppearance = next);
        await PublishAsync(s);
        await GmCommands.ReplyAsync(s, "GM aura " + args[0] + ".");
    }

    private Task PublishAsync(GameSession s) => RoleFanout.EachAsync(_world.Viewers(s).Prepend(s), viewer =>
        viewer.SendGameAsync(WorldCoreService.CustomChange, new CustomDataChange { EntityId = s.Role!.Id, CustomDatas = { Marker(s) } }));
}
