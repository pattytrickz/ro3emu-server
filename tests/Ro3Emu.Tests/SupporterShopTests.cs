using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

/// <summary>
/// The Supporter Merchant (owner's plan v1.1 §2.2, §6 "Balance and game mechanics") on hand-written synthetic data:
/// shown only to v3 hooks, sold only to active supporters for Supporter Tokens within account-wide weekly limits, and
/// boosters that run only in active Auto Mode, one at a time, needing active status to start.
/// </summary>
public sealed class SupporterShopTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "supporter-shop-fixture-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private const int Material = 1000000;
    private static readonly SupporterShopRules.Product Recharge = SupporterShopRules.Products[0], Exp50 = SupporterShopRules.Products[1],
        Drop = SupporterShopRules.Products[3];

    public SupporterShopTests()
    {
        foreach (var file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(_data, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        // The Dye Merchant's hand-written template shapes (CharacterAppearanceTests); no publisher row is copied.
        string path = Path.Combine(_data, "config", "ItemConfig.json");
        var items = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        items[CharacterAppearanceRules.ItemTemplate.ToString(System.Globalization.CultureInfo.InvariantCulture)] = JsonNode.Parse("""
            {"_iID":1920003,"_iType":2,"_iSubType":218,"_iBagTab":1,"_iStackLimit":17,"_iIsUse":1}
            """);
        items[F2PRules.SupporterToken.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
            JsonNode.Parse($$"""{"_iID":{{F2PRules.SupporterToken}},"_iType":1,"_iBagTab":1,"_iTrade":0}""");
        File.WriteAllText(path, items.ToJsonString());
        File.WriteAllText(Path.Combine(_data, "config", "ShopConfig.json"), """
            {"10":{"_iID":10,"_iClassID":301,"_iShopType":3,"_iFunctionId":0}}
            """);
        File.WriteAllText(Path.Combine(_data, "config", "ShopCommodityConfig.json"), """
            {"113":{"_iID":113,"_iClassID":301,"_iItemId":1000000,"_iItemPerMount":1,"_kItemPerPrice":[1,13]}}
            """);
        File.WriteAllText(Path.Combine(_data, "config", "NPCConfig.json"), """
            {"100002":{"_iID":100002,"_iUnitID":1,"_iCamp":0,"_iInteractID":[2900002]}}
            """);
        File.WriteAllText(Path.Combine(_data, "config", "NPCInteractConfig.json"), """
            {"2900002":{"_iInterID":2900002,"_iEXECAction":[["53","1"]]}}
            """);
        var mapsPath = Path.Combine(_data, "config", "MapConfig.json");
        var maps = JsonNode.Parse(File.ReadAllText(mapsPath))!.AsObject();
        maps["10100"] = JsonNode.Parse("""{"_iID":10100,"_kResSceneEditorName":"synthetic_town"}""");
        File.WriteAllText(mapsPath, maps.ToJsonString());
    }

    public void Dispose() => Directory.Delete(_data, true);

    private Task<SyntheticHost> Start(bool shop = true) => SyntheticHost.StartAsync(o =>
    {
        o.GameDataDir = _data; o.CharacterAppearanceEnabled = true; o.CharacterDyePrice = 7;
        o.Supporter.Shop = shop;
        o.GmAccounts = ["alice"];
    });

    private static GameSession Session(SyntheticHost host, string account) => host.Service<SessionRegistry>().All().Single(s => s.Account == account);
    private static Task<T> OnLoop<T>(SyntheticHost host, Func<T> read) => host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private static async Task Enable(BotClient bot, byte version = CharacterAppearanceRules.ClientVersion)
    {
        await bot.SendGameBytesAsync(CharacterAppearanceRules.ClientReadyMessage, [version]);
        await Next(bot, WorldCoreService.CustomChange, CustomDataChange.Parser, p => p.CustomDatas.Any(c => c.Id == CharacterAppearanceRules.PriceCustomData));
    }

    private static async Task<int> Buy(BotClient bot, SupporterShopRules.Product p)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = p.Commodity, Count = 1, Price = p.Price });
        return (await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result;
    }

    private static async Task<int> Use(SyntheticHost host, BotClient bot, SupporterShopRules.Product p)
    {
        long guid = await OnLoop(host, () => Session(host, bot.Account!).Items.First(i => i.Entry == p.Item).Guid);
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        return (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result;
    }

    private static Task<int> Bag(SyntheticHost host, string account, int item) =>
        OnLoop(host, () => Session(host, account).Items.Where(i => i.Entry == item).Sum(i => i.Stack));

    private static Task<long> Tokens(SyntheticHost host, string account) =>
        OnLoop(host, () => host.Service<CharacterService>().CountOf(Session(host, account), F2PRules.SupporterToken));

    private static async Task Gm(SyntheticHost host, string account, string command)
    {
        using var client = new HttpClient();
        using var response = await client.PostAsync(host.Http + "/ro3emu/gm?account=" + account, new StringContent(command));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(p => p.IsGame(id) && (match == null || match(parser.ParseFrom(p.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    [Fact]
    public async Task TheMerchantStandsBesideTheDyeMerchantOnlyWhenTheShopIsOnAndOnlyForVersion3Hooks()
    {
        await using (var off = await Start(shop: false))
            Assert.DoesNotContain(off.Service<WorldData>().Map(SupporterShopRules.MerchantMap)!.Npcs, n => n.NpcId == SupporterShopRules.MerchantId);
        await using var host = await Start();
        var merchant = Assert.Single(host.Service<WorldData>().Map(SupporterShopRules.MerchantMap)!.Npcs, n => n.NpcId == SupporterShopRules.MerchantId);
        var data = host.Service<Ro3Emu.Server.Data.GameData>();
        var row = data.Row("ShopCommodityConfig", Recharge.Commodity)!.Value;
        Assert.Equal(SupporterShopRules.MerchantId, Ro3Emu.Server.Data.GameData.Int(row, "_iClassID"));
        Assert.Equal([F2PRules.SupporterToken, 60L], [.. row.GetProperty("_kItemPerPrice").EnumerateArray().Select(v => v.GetInt64())]);
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice, CharacterAppearanceRules.FirstClientVersion); // dye-only hooks: the dyes work, this NPC stays hidden
        Assert.False(await OnLoop(host, () => CharacterAppearanceService.NpcVisible(Session(host, "alice"), merchant)));
        await using var bob = await host.InWorldAsync("Bob");
        await Enable(bob);
        Assert.True(await OnLoop(host, () => CharacterAppearanceService.NpcVisible(Session(host, "bob"), merchant)));
    }

    [Fact]
    public async Task OnlyActiveSupportersBuyAndTheWeeklyLimitIsPerAccount()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        await Gm(host, "alice", $"!item {F2PRules.SupporterToken} 1000");
        Assert.NotEqual(0, await Buy(alice, Recharge)); // not a supporter
        Assert.Equal(1000, await Tokens(host, "alice"));
        host.Service<AccessList>().Run("supporter alice");
        Assert.Equal(0, await Buy(alice, Recharge));
        Assert.Equal(0, await Buy(alice, Recharge));
        Assert.NotEqual(0, await Buy(alice, Recharge)); // 2 a week
        Assert.Equal(1000 - 2 * Recharge.Price, await Tokens(host, "alice"));
        Assert.Equal(2, await Bag(host, "alice", Recharge.Item));
        long week = NewActivityService.PassPeriod(DateTimeOffset.Now).Week;
        var counted = await host.Service<AccountStore>().ReadAsync<Dictionary<int, int>>(new SupporterPurchaseReadPlan("alice", week));
        Assert.Equal(2, counted[Recharge.Commodity]);
    }

    [Fact]
    public async Task BoostersNeedStatusRunOnlyInActiveAutoModeAndOneAtATime()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        await Gm(host, "alice", $"!item {F2PRules.SupporterToken} 1000");
        var access = host.Service<AccessList>();
        access.Run("supporter alice");
        Assert.Equal(0, await Buy(alice, Exp50));
        Assert.Equal(0, await Buy(alice, Drop));

        // Lapsed: the booster stays in the bag and cannot start.
        access.Run("unsupporter alice");
        Assert.NotEqual(0, await Use(host, alice, Exp50));
        Assert.Equal(1, await Bag(host, "alice", Exp50.Item));
        access.Run("supporter alice");
        Assert.Equal(0, await Use(host, alice, Exp50));
        Assert.Equal(0, await Bag(host, "alice", Exp50.Item));
        Assert.NotEqual(0, await Use(host, alice, Drop)); // one booster at a time
        Assert.Equal(1, await Bag(host, "alice", Drop.Item));

        var rewards = host.Service<RewardService>();
        var shop = host.Service<SupporterShop>();
        Assert.Equal(0, await OnLoop(host, () => rewards.SupporterExpPercent!(Session(host, "alice")))); // not in Auto Mode
        await OnLoop(host, () =>
        {
            var info = Session(host, "alice").WildAfk!.Info;
            info.IsHighMagnification = true; info.IsSuspended = false; info.HighMultiplierTimeSeconds = 7200;
            return 0;
        });
        Assert.Equal(50, await OnLoop(host, () => rewards.SupporterExpPercent!(Session(host, "alice"))));
        Assert.Null(await OnLoop(host, () => rewards.SupporterDropBoost!(Session(host, "alice")))); // the EXP booster, not the drop one

        // Only used auto-battle time runs it down: 59 minutes leave one, the 60th ends it.
        await OnLoop(host, () => { shop.OnConsumed(Session(host, "alice"), 59 * 60); return 0; });
        Assert.Equal(60, shop.Running("alice")!.RemainingSeconds);
        await OnLoop(host, () => { shop.OnConsumed(Session(host, "alice"), 60); return 0; });
        Assert.Null(shop.Running("alice"));
        Assert.Equal(0, await OnLoop(host, () => rewards.SupporterExpPercent!(Session(host, "alice"))));

        Assert.Equal(0, await Use(host, alice, Drop));
        var drop = await OnLoop(host, () => rewards.SupporterDropBoost!(Session(host, "alice")));
        Assert.Equal(50, drop!.Value.Percent);
        Assert.Null(drop.Value.Items); // no list: every item the kill drops
        host.Service<IOptions<ServerOptions>>().Value.Supporter.DropBoosterItems = [Material];
        drop = await OnLoop(host, () => rewards.SupporterDropBoost!(Session(host, "alice")));
        Assert.Equal([Material], [.. drop!.Value.Items!]); // a list narrows it
        Assert.Equal(Drop.Minutes * 60L, shop.Running("alice")!.RemainingSeconds);
        // Saved with the item's use, so a relog or a restart keeps it.
        Assert.Contains("\"drop\"", host.Service<RoleStore>().ServerState(SupporterShop.StateKey("alice")));
    }

    [Fact]
    public async Task TheRechargeAddsAnHourWithinTheBankLimitOrStaysInTheBag()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        await Gm(host, "alice", $"!item {F2PRules.SupporterToken} 1000");
        host.Service<AccessList>().Run("supporter alice");
        Assert.Equal(0, await Buy(alice, Recharge));
        Assert.Equal(0, await Buy(alice, Recharge));
        long before = await OnLoop(host, () => Session(host, "alice").WildAfk!.Info.HighMultiplierTimeSeconds);
        Assert.Equal(0, await Use(host, alice, Recharge));
        Assert.Equal(before + 3600, await OnLoop(host, () => Session(host, "alice").WildAfk!.Info.HighMultiplierTimeSeconds));
        Assert.Equal(1, await Bag(host, "alice", Recharge.Item));
        // Full bank (the default 180-minute limit): refused, and the recharge is kept.
        await OnLoop(host, () => { Session(host, "alice").WildAfk!.Info.HighMultiplierTimeSeconds = 10800 - 100; return 0; });
        Assert.NotEqual(0, await Use(host, alice, Recharge));
        Assert.Equal(1, await Bag(host, "alice", Recharge.Item));
        Assert.Equal(10800 - 100, await OnLoop(host, () => Session(host, "alice").WildAfk!.Info.HighMultiplierTimeSeconds));
    }
}
