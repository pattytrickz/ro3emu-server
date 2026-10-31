using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Server-owned cosmetic colours and explicit-GM aura preference. No character stats are changed. A colour is unlocked
/// once per account (dye_unlock) by buying it at the Dye Merchant (CharacterAppearanceRules' tiers and prices), by using
/// a first-catalogue dye bottle, or by staff; every character of the account then switches between its owned colours for
/// free at the merchant. Colours a character wears are unlocked for its account on its next world entry: the colours
/// worn when unlocks began, and the safety net for the unlock row itself, which is written just after the payment's
/// transaction (the family economy ledger admits only role state in it) while the colour is already saved as worn.
/// </summary>
public sealed partial class CharacterAppearanceService
{
    public sealed record State(int Hair = 0, int Armour = 0, bool Aura = false);
    private readonly RoleStore _roles;
    private readonly AccountStore _accounts;
    private readonly CharacterService _character;
    private readonly World _world;
    private readonly WorldData _worldData;
    private readonly SessionRegistry _sessions;
    private readonly ServerOptions _options;
    private readonly ILogger<CharacterAppearanceService> _log;

    public CharacterAppearanceService(RoleStore roles, AccountStore accounts, CharacterService character, World world, WorldData worldData,
        SessionRegistry sessions, IOptions<ServerOptions> options, GmCommands gm, ILogger<CharacterAppearanceService> log)
    {
        _roles = roles; _accounts = accounts; _character = character; _world = world; _worldData = worldData; _sessions = sessions;
        _options = options.Value; _log = log;
        character.SpecialItemUse.Insert(0, TryUseAsync);
        gm.Register("aura", "on|off (explicit GM cosmetic aura)", AuraAsync);
        gm.Register("dye", "list <account> | grant <account> <colour>", DyeAsync);
    }

    private static string Key(string account) => account.ToLowerInvariant();
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
        if (body.Length != 1 || body.Span[0] is < CharacterAppearanceRules.FirstClientVersion or > CharacterAppearanceRules.ClientVersion
            || s.CharacterAppearanceClientReady || s.CharacterAppearance == null) return;
        using var held = await CharacterService.Lock(s);
        s.CharacterAppearanceClientReady = true;
        s.ClientHookVersion = body.Span[0];
        if (s.CharacterAppearanceEnabled) s.DyeUnlocks = await UnlocksWithWornAsync(s);
        // Hooks before 4 read the price markers as their bottle prices: 0 empties their old catalogue.
        var change = new CustomDataChange
        {
            EntityId = s.Role!.Id,
            CustomDatas = { Marker(s), FullMarker(s), LongMarker(CharacterAppearanceRules.PriceCustomData, 0),
                LongMarker(CharacterAppearanceRules.RarePriceCustomData, 0) }
        };
        if (s.CharacterAppearanceEnabled && s.ClientHookVersion >= CharacterAppearanceRules.UnlockClientVersion) change.CustomDatas.Add(OwnedMarker(s));
        await s.SendGameAsync(WorldCoreService.CustomChange, change);
        await _character.ResendAppearanceItemsAsync(s);
        foreach (var merchant in _worldData.Map(s.SceneEntry)?.Npcs.Where(n => n.NpcId is CharacterAppearanceRules.MerchantId
            or SupporterShopRules.MerchantId && NpcVisible(s, n)) ?? [])
            await s.SendGameAsync(GameAoiSink.SyncAoiView, new AoIViewChangeData { Bases = { SceneService.NpcEntity(merchant) } });
    }

    /// <summary>The account's unlocks, adding the colours this character wears (grandfathered from the bottle dyes).</summary>
    private async Task<long> UnlocksWithWornAsync(GameSession s)
    {
        string account = Key(s.Account!);
        long owned = await _accounts.ReadAsync<long>(new DyeUnlockReadPlan(account));
        var state = s.CharacterAppearance!;
        int[] worn = [.. new[] { state.Hair, state.Armour }.Where(p => p > 0 && !CharacterAppearanceRules.Owns(owned, p)).Distinct()];
        if (worn.Length == 0) return owned;
        await _accounts.WriteAsync<int>(new DyeUnlockPlan(account, worn, "worn", Now));
        foreach (int palette in worn) owned |= 1L << palette;
        return owned;
    }

    /// <summary>House NPCs only for hooks that have their rows: the Dye Merchant from version 4, the Supporter Merchant from 3.</summary>
    internal static bool NpcVisible(GameSession s, WorldNpc npc) => npc.NpcId switch
    {
        CharacterAppearanceRules.MerchantId => s.CharacterAppearanceClientReady && s.ClientHookVersion >= CharacterAppearanceRules.UnlockClientVersion,
        SupporterShopRules.MerchantId => s.CharacterAppearanceClientReady && s.ClientHookVersion >= CharacterAppearanceRules.SupporterShopClientVersion,
        _ => true,
    };

    /// <summary>The first catalogue's selection marker, for hooks before version 4 (nearest first-catalogue colours).</summary>
    internal static CustomData Marker(GameSession s)
    {
        var (hair, armour, aura) = Selection(s);
        return LongMarker(CharacterAppearanceRules.SelectionCustomData, s.CharacterAppearanceEnabled ? CharacterAppearanceRules.Pack(hair, armour, aura) : 0);
    }

    internal static CustomData FullMarker(GameSession s)
    {
        var (hair, armour, aura) = Selection(s);
        return LongMarker(CharacterAppearanceRules.FullSelectionCustomData, s.CharacterAppearanceEnabled ? CharacterAppearanceRules.PackFull(hair, armour, aura) : 0);
    }

    private static (int Hair, int Armour, bool Aura) Selection(GameSession s)
    {
        var state = s.CharacterAppearance ?? new State();
        return (state.Hair, state.Armour, state.Aura && s.PublicIdentityBadge == SupporterIdentityRules.GameMaster);
    }

    private static CustomData OwnedMarker(GameSession s) => LongMarker(CharacterAppearanceRules.OwnedCustomData, s.DyeUnlocks
        | (CharacterAppearanceRules.SupportsArmour(s.Progress?.Career ?? 0, s.Role?.Sex ?? 0) ? 1L << CharacterAppearanceRules.ArmourSupportedBit : 0));

    private static CustomData LongMarker(long id, long value) => new()
    {
        Id = id, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value }
    };

    /// <summary>
    /// A Dye Merchant commodity (ShopService.BuyOneAsync, under the character lock): an owned colour is applied for free;
    /// an unowned one is paid in its currency, unlocked for the account and applied in one transaction. The price is the
    /// catalogue's, never the client's quote.
    /// </summary>
    internal async Task<(int Result, int Price)> BuyLockedAsync(GameSession s, int commodity, int count)
    {
        var (result, price) = await BuyCoreAsync(s, commodity, count);
        LogBuy(_log, s.Role?.Name ?? "", commodity, price, result);
        return (result, price);
    }

    private async Task<(int Result, int Price)> BuyCoreAsync(GameSession s, int commodity, int count)
    {
        if (!s.CharacterAppearanceEnabled || !s.CharacterAppearanceClientReady || s.CharacterAppearance == null
            || s.ClientHookVersion < CharacterAppearanceRules.UnlockClientVersion) return (ErrorCodes.FeatureUnavailable, 0);
        if (count != 1) return (ErrorCodes.Parameter, 0);
        int palette = CharacterAppearanceRules.CommodityPalette(commodity);
        bool armour = CharacterAppearanceRules.CommodityArmour(commodity), tokens = CharacterAppearanceRules.CommodityTokens(commodity);
        var dye = CharacterAppearanceRules.DyeOf(palette);
        if (armour && palette != 0 && !CharacterAppearanceRules.SupportsArmour(s.Progress!.Career, s.Role!.Sex)) return (ErrorCodes.FeatureUnavailable, 0);
        var next = armour ? s.CharacterAppearance with { Armour = palette } : s.CharacterAppearance with { Hair = palette };
        if (CharacterAppearanceRules.Owns(s.DyeUnlocks, palette))
        {
            if (next == s.CharacterAppearance) return (ErrorCodes.Parameter, 0); // already wearing it
            await SaveAsync(s, next, "character appearance/switch");
            return (0, 0);
        }
        if (!dye.ForSale || tokens && dye.Tokens == 0) return (ErrorCodes.ShopNoConfig, 0);
        int currency = tokens ? CharacterAppearanceRules.Token : CharacterAppearanceRules.Crystal, price = tokens ? dye.Tokens : dye.Crystals;
        (int, long)[] cost = [(currency, price)];
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        var paid = await _character.ExchangeLockedAsync(s, cost, [], popup: false,
            persist: tx =>
            {
                tx.RoleBlob(s.Role!.Id, "character_appearance", bytes);
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/wardrobe-dye-unlock", cost, [],
                    new
                    {
                        Commodity = commodity, Palette = palette, Armour = armour, Payment = currency, Price = price,
                        StateHash = Convert.ToHexString(SHA256.HashData(bytes))
                    }));
            }, committed: () => { s.CharacterAppearance = next; s.DyeUnlocks |= 1L << palette; }, asyncCommit: true);
        if (paid == null) return (_character.ShortageOf(s, cost), price);
        await UnlockAsync(s, palette, tokens ? "tokens" : "crystals");
        await PublishAsync(s);
        await SendOwnedAsync(s);
        return (0, price);
    }

    /// <summary>A first-catalogue dye bottle: unlocks its colour for the account and applies it, spending the bottle; a
    /// colour the account owns is applied without spending it.</summary>
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
        if (CharacterAppearanceRules.Owns(s.DyeUnlocks, palette))
        {
            await SaveAsync(s, next, "character appearance/owned bottle");
            return 0;
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        var spent = await _character.ExchangeLockedAsync(s, [], [], new Dictionary<long, int> { [item.Guid] = 1 }, popup: false,
            persist: tx =>
            {
                tx.RoleBlob(s.Role!.Id, "character_appearance", bytes);
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/wardrobe-dye", [(item.Entry, 1L)], [],
                    new { Item = item.Entry, StateHash = Convert.ToHexString(SHA256.HashData(bytes)) }));
            }, committed: () => { s.CharacterAppearance = next; s.DyeUnlocks |= 1L << palette; }, asyncCommit: true);
        if (spent == null) return ErrorCodes.ItemsShort;
        await UnlockAsync(s, palette, "bottle");
        await PublishAsync(s);
        await SendOwnedAsync(s);
        return 0;
    }

    /// <summary>The unlock row of a colour just paid for and saved as worn. Should it fail, the worn colour is unlocked
    /// again on the character's next world entry (UnlocksWithWornAsync).</summary>
    private async Task UnlockAsync(GameSession s, int palette, string source)
    {
        try { await _accounts.WriteAsync<int>(new DyeUnlockPlan(Key(s.Account!), [palette], source, Now)); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            LogUnlockNotSaved(_log, e, palette, s.Account);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dye unlock {Palette} of {Account} not saved; the worn colour is unlocked at the next world entry")]
    private static partial void LogUnlockNotSaved(ILogger logger, Exception e, int palette, string? account);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} buys dye commodity {Commodity} at {Price}: {Result}")]
    private static partial void LogBuy(ILogger logger, string name, int commodity, int price, int result);

    private async Task SaveAsync(GameSession s, State next, string label)
    {
        var tx = _roles.NewTransaction();
        tx.RoleBlob(s.Role!.Id, "character_appearance", JsonSerializer.SerializeToUtf8Bytes(next));
        await _roles.CommitAsync(tx, label, _ => s.CharacterAppearance = next);
        await PublishAsync(s);
    }

    private async Task AuraAsync(GameSession s, string[] args)
    {
        if (!s.CharacterAppearanceEnabled || !_options.GmAccounts.Contains(s.Account, StringComparer.Ordinal))
        { await GmCommands.ReplyAsync(s, "The aura is available only to explicitly configured GMs on enabled realms."); return; }
        if (args.Length != 1 || args[0] is not ("on" or "off"))
        { await GmCommands.ReplyAsync(s, "Use !aura on or !aura off."); return; }
        using var held = await CharacterService.Lock(s);
        await SaveAsync(s, (s.CharacterAppearance ?? new State()) with { Aura = args[0] == "on" }, "character appearance/aura");
        await GmCommands.ReplyAsync(s, "GM aura " + args[0] + ".");
    }

    /// <summary>"!dye list name" shows an account's colours; "!dye grant name colour" unlocks one (past buyers of a colour
    /// the catalogue moved or retired, support cases). An online account sees it at once.</summary>
    private async Task DyeAsync(GameSession s, string[] args)
    {
        if (args.Length < 2 || args[0] is not ("list" or "grant") || args[0] == "grant" && args.Length < 3)
        { await GmCommands.ReplyAsync(s, "Use !dye list <account> or !dye grant <account> <colour>."); return; }
        string account = Key(args[1]);
        if (args[0] == "grant")
        {
            string name = string.Join(' ', args[2..]);
            var dye = CharacterAppearanceRules.Dyes.FirstOrDefault(d => d.Palette > 0
                && (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) || d.Palette.ToString(System.Globalization.CultureInfo.InvariantCulture) == name));
            if (dye == null) { await GmCommands.ReplyAsync(s, "Unknown colour: " + name + "."); return; }
            int added = await _accounts.WriteAsync<int>(new DyeUnlockPlan(account, [dye.Palette], "gm", Now));
            foreach (var online in _sessions.All().Where(o => o.Account != null && Key(o.Account) == account && o.CharacterAppearanceClientReady))
            {
                online.DyeUnlocks |= 1L << dye.Palette;
                if (online.ClientHookVersion >= CharacterAppearanceRules.UnlockClientVersion) await SendOwnedAsync(online);
            }
            await GmCommands.ReplyAsync(s, added > 0 ? $"{dye.Name} unlocked for {account}." : $"{account} already owns {dye.Name}.");
            return;
        }
        long owned = await _accounts.ReadAsync<long>(new DyeUnlockReadPlan(account));
        var names = CharacterAppearanceRules.Dyes.Where(d => d.Palette > 0 && CharacterAppearanceRules.Owns(owned, d.Palette)).Select(d => d.Name).ToList();
        await GmCommands.ReplyAsync(s, $"{account}: " + (names.Count == 0 ? "no colours unlocked." : string.Join(", ", names) + "."));
    }

    private static Task SendOwnedAsync(GameSession s) => s.ClientHookVersion < CharacterAppearanceRules.UnlockClientVersion ? Task.CompletedTask
        : s.SendGameAsync(WorldCoreService.CustomChange, new CustomDataChange { EntityId = s.Role!.Id, CustomDatas = { OwnedMarker(s) } });

    private Task PublishAsync(GameSession s) => RoleFanout.EachAsync(_world.Viewers(s).Prepend(s), viewer =>
        viewer.SendGameAsync(WorldCoreService.CustomChange, new CustomDataChange { EntityId = s.Role!.Id, CustomDatas = { Marker(s), FullMarker(s) } }));
}
