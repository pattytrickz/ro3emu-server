using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

public sealed class CharacterAppearanceTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "appearance-fixture-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private const int Hair = CharacterAppearanceRules.HairItemBase + 2;

    public CharacterAppearanceTests()
    {
        foreach (var file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(_data, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        // Hand-written template shape; no publisher row is copied into the fixture.
        string path = Path.Combine(_data, "config", "ItemConfig.json");
        var items = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        items[CharacterAppearanceRules.ItemTemplate.ToString(System.Globalization.CultureInfo.InvariantCulture)] = JsonNode.Parse("""
            {"_iID":1920003,"_iType":2,"_iSubType":218,"_iBagTab":1,"_iStackLimit":17,"_iIsUse":1}
            """);
        // Supporter Tokens, a currency like Zeny: the rare colours' only payment.
        items[CharacterAppearanceRules.RareCurrency.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
            JsonNode.Parse($$"""{"_iID":{{CharacterAppearanceRules.RareCurrency}},"_iType":1,"_iBagTab":1,"_iTrade":0}""");
        File.WriteAllText(path, items.ToJsonString());
        File.WriteAllText(Path.Combine(_data, "config", "ShopConfig.json"), """
            {"10":{"_iID":10,"_iClassID":301,"_iShopType":3,"_iFunctionId":0}}
            """);
        File.WriteAllText(Path.Combine(_data, "config", "ShopCommodityConfig.json"), """
            {"113":{"_iID":113,"_iClassID":301,"_iItemId":1000000,"_iItemPerMount":1,"_kItemPerPrice":[1,13]}}
            """);
        WriteMerchantFixture();
    }

    private void WriteMerchantFixture()
    {
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

    private Task<SyntheticHost> Start(bool enabled = true, int rarePrice = 0) => SyntheticHost.StartAsync(o =>
    {
        o.GameDataDir = _data; o.CharacterAppearanceEnabled = enabled; o.CharacterDyePrice = 7;
        o.CharacterDyeRarePrice = rarePrice;
        o.GmAccounts = ["alice"]; o.GmToolAccounts = ["bob"];
    });

    [Fact]
    public async Task PronteraMerchantIsPublishedOnlyAfterTheClientRegistersItsCatalogue()
    {
        await using var host = await Start();
        var world = host.Service<WorldData>();
        var merchant = Assert.Single(world.Map(CharacterAppearanceRules.MerchantMap)!.Npcs,
            n => n.NpcId == CharacterAppearanceRules.MerchantId);
        Assert.Equal(CharacterAppearanceRules.MerchantName, merchant.Name);
        Assert.DoesNotContain(world.Map(SyntheticData.Town)!.Npcs, n => n.NpcId == merchant.NpcId);
        await using var alice = await host.InWorldAsync("Alice");
        await Gm(host, "alice", "!warp " + CharacterAppearanceRules.MerchantMap);
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = CharacterAppearanceRules.MerchantMap });
        var legacy = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser);
        Assert.DoesNotContain(legacy.Bases, n => n.EntityTemplateID == (ulong)merchant.NpcId);
        await Enable(alice);
        var ready = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            p => p.Bases.Any(n => n.EntityTemplateID == (ulong)merchant.NpcId));
        Assert.Equal((ulong)merchant.Uid, Assert.Single(ready.Bases).Uuid);
        var data = host.Service<Ro3Emu.Server.Data.GameData>();
        Assert.Equal(CharacterAppearanceRules.MerchantId,
            Ro3Emu.Server.Data.GameData.Int(data.Row("ShopConfig", CharacterAppearanceRules.MerchantId)!.Value, "_iNpcId"));
        Assert.Equal(301, Ro3Emu.Server.Data.GameData.Int(data.Row("ShopCommodityConfig", 113)!.Value, "_iClassID"));
        Assert.Equal(CharacterAppearanceRules.MerchantId,
            Ro3Emu.Server.Data.GameData.Int(data.Row("ShopCommodityConfig", CharacterAppearanceRules.CommodityBase)!.Value, "_iClassID"));
    }

    [Fact]
    public async Task DisabledRealmDoesNotPlaceTheDyeMerchant()
    {
        await using var host = await Start(enabled: false);
        Assert.DoesNotContain(host.Service<WorldData>().Map(CharacterAppearanceRules.MerchantMap)!.Npcs,
            n => n.NpcId == CharacterAppearanceRules.MerchantId);
    }

    [Fact]
    public async Task PurchaseUseAndRelogKeepSelectionAndNotifyAnotherPlayer()
    {
        await using var host = await Start();
        await using var bob = await host.InWorldAsync("Bob");
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            Assert.NotEqual(0, await Buy(alice, Hair)); // cannot sell unknown item ids to an old client
            await alice.SendGameBytesAsync(CharacterAppearanceRules.ClientReadyMessage, [1]);
            Assert.NotEqual(0, await Buy(alice, Hair)); // the old Zeny-only catalogue capability is insufficient
            await Enable(alice);
            await Gm(host, "alice", "!item 1 100");
            long before = Session(host, "alice").Progress!.Currency[1];
            Assert.Equal(0, await Buy(alice, Hair));
            var session = Session(host, "alice");
            Assert.Equal(before - 7, session.Progress!.Currency[1]);
            long guid = session.Items.Single(i => i.Entry == Hair).Guid;
            Assert.Equal(0, await Use(alice, guid));
            Assert.DoesNotContain(session.Items, i => i.Guid == guid);
            Assert.Equal(2, session.CharacterAppearance!.Hair);
            await Next(bob, WorldCoreService.CustomChange, CustomDataChange.Parser,
                p => p.EntityId == session.Role!.Id && Selection(p.CustomDatas) == CharacterAppearanceRules.Pack(2, 0, false));
            Assert.NotEqual(0, await Use(alice, guid)); // replay of spent item cannot change anything
        }
        await host.RestartAsync();
        await using var again = await host.InWorldAsync("Alice", create: false);
        Assert.Equal(CharacterAppearanceRules.Pack(2, 0, false), Selection(again.Self!.CustomDatas));
    }

    [Fact]
    public async Task SavedDyesStayHiddenUntilTheClientRegistersItsItemRows()
    {
        await using var host = await Start();
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            await Enable(alice);
            await Gm(host, "alice", "!item " + Hair);
        }
        await host.RestartAsync();
        await using var again = await host.InWorldAsync("Alice", create: false);
        var initial = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.DoesNotContain(initial.PackInfo.Values.SelectMany(p => p.All.Items.Values), i => i.ItemEntry == Hair);
        Assert.Contains(Session(host, "alice").Items, i => i.Entry == Hair);
        await Enable(again);
        var registered = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            p => p.PackInfo.Values.Any(pack => pack.All?.Items.Values.Any(i => i.ItemEntry == Hair) == true));
        Assert.True(registered.SyncAllMark);
        Assert.Contains(registered.PackInfo.Values.SelectMany(p => p.All.Items.Values), i => i.ItemEntry == Hair);
    }

    [Fact]
    public async Task FailedSaveKeepsDyeAndOldSelectionThenRetryConsumesExactlyOnce()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        await Gm(host, "alice", "!item " + Hair + " 2");
        var s = Session(host, "alice");
        long guid = s.Items.Single(i => i.Entry == Hair).Guid;
        var accounts = host.Service<AccountStore>();
        FixtureSql.Execute(accounts, "CREATE TRIGGER reject_dye BEFORE UPDATE OF character_appearance ON role BEGIN SELECT RAISE(ABORT,'dye'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => host.Service<GameLoop>().InvokeAsync(() =>
                host.Service<CharacterService>().UseItemAsync(s, new ItemUseReq { ItemGuid = guid, ItemCount = 1 })));
            Assert.Equal(0, s.CharacterAppearance!.Hair);
            Assert.Equal(2, s.Items.Single(i => i.Guid == guid).Stack);
            Assert.Equal(2, host.Service<ItemStore>().Load(s.Role!.Id).Single(i => i.Guid == guid).Stack);
            Assert.Null(host.Service<RoleStore>().LoadCharacterAppearance(s.Role.Id));
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER reject_dye"); }
        Assert.Equal(0, await Use(alice, guid));
        Assert.NotEqual(0, await Use(alice, guid)); // duplicate colour must not consume a second bottle
        Assert.Equal(1, s.Items.Single(i => i.Guid == guid).Stack);
    }

    [Fact]
    public async Task UnsupportedArmourAndMultiUseRefuseWithoutConsuming()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        // Novice armour has masks for both sexes; a first job's body has none yet.
        await host.Service<GameLoop>().InvokeAsync(() => { Session(host, "alice").Progress!.Career = 1100; return Task.CompletedTask; });
        await Enable(alice);
        int armour = CharacterAppearanceRules.ArmourItemBase + 2;
        Assert.NotEqual(0, await Buy(alice, armour));
        await Gm(host, "alice", "!item " + armour);
        await Gm(host, "alice", "!item " + Hair + " 2");
        var s = Session(host, "alice");
        long armourGuid = s.Items.Single(i => i.Entry == armour).Guid;
        Assert.NotEqual(0, await Use(alice, armourGuid));
        Assert.NotEqual(0, await Use(alice, s.Items.Single(i => i.Entry == Hair).Guid, 2));
        Assert.Equal(1, s.Items.Single(i => i.Entry == armour).Stack);
        Assert.Equal(2, s.Items.Single(i => i.Entry == Hair).Stack);
    }

    [Fact]
    public async Task ExplicitGmAuraSurvivesRestartAndRevocationRemovesItFromViewers()
    {
        await using var host = await Start();
        await using var bob = await host.InWorldAsync("Bob");
        await Gm(host, "bob", "!aura on"); // loopback tool authority is insufficient
        Assert.False(Session(host, "bob").CharacterAppearance!.Aura);
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            await Gm(host, "alice", "!aura on");
            Assert.True(Session(host, "alice").CharacterAppearance!.Aura);
            await Next(bob, WorldCoreService.CustomChange, CustomDataChange.Parser, p => CharacterAppearanceRules.Aura(Selection(p.CustomDatas)));
        }
        await host.RestartAsync();
        await using var viewer = await host.InWorldAsync("Bob", create: false);
        await using var again = await host.InWorldAsync("Alice", create: false);
        Assert.True(CharacterAppearanceRules.Aura(Selection(again.Self!.CustomDatas)));
        host.Service<IOptions<ServerOptions>>().Value.GmAccounts = [];
        await Next(viewer, WorldCoreService.CustomChange, CustomDataChange.Parser,
            p => p.EntityId == (long)again.Self.Uuid && p.CustomDatas.Any(c => c.Id == SupporterIdentityRules.BadgeCustomData)
                && !CharacterAppearanceRules.Aura(Selection(p.CustomDatas)));
    }

    [Fact]
    public async Task RolloutDisabledAndForgedCustomDataCannotEnableAppearance()
    {
        await using var host = await Start(enabled: false);
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        await Gm(host, "alice", "!aura on");
        Assert.False(Session(host, "alice").CharacterAppearance!.Aura);
        Assert.NotEqual(0, await Buy(alice, Hair));
        foreach (long marker in new[] { CharacterAppearanceRules.SelectionCustomData, CharacterAppearanceRules.PriceCustomData,
                     CharacterAppearanceRules.RarePriceCustomData })
            await Assert.ThrowsAsync<ArgumentException>(() => host.Service<GameLoop>().InvokeAsync(() =>
                host.Service<WorldCoreService>().SetCustomAsync(Session(host, "alice"), new CustomData
                {
                    Id = marker,
                    CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = 256 }
                })));
    }

    [Fact]
    public async Task RareColoursAreHiddenWhileTheirTokenPriceIsZero()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        await Gm(host, "alice", "!item 1 1000");
        long before = Session(host, "alice").Progress!.Currency[1];
        foreach (int palette in new[] { 4, 5, 6 })
            foreach (int item in new[] { CharacterAppearanceRules.HairItemBase + palette, CharacterAppearanceRules.ArmourItemBase + palette })
            {
                Assert.True(CharacterAppearanceRules.IsRare(item));
                Assert.Null(host.Service<Ro3Emu.Server.Data.GameData>().Row("ShopCommodityConfig", CharacterAppearanceRules.CommodityForItem(item)));
                Assert.NotEqual(0, await Buy(alice, item, quote: 0));
            }
        Assert.Equal(before, Session(host, "alice").Progress!.Currency[1]);
    }

    [Fact]
    public async Task RareColoursCostOnlySupporterTokensAndRollBackPaymentWithARejectedGrant()
    {
        const int rare = CharacterAppearanceRules.HairItemBase + 5, tokens = CharacterAppearanceRules.RareCurrency;
        await using var host = await Start(rarePrice: 75);
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        Assert.Equal(new long[] { tokens, 75 }, host.Service<Ro3Emu.Server.Data.GameData>()
            .Row("ShopCommodityConfig", CharacterAppearanceRules.CommodityForItem(rare))!.Value.GetProperty("_kItemPerPrice")
            .EnumerateArray().Select(e => e.GetInt64()).ToArray());
        await Gm(host, "alice", "!item 1 1000");
        long before = Session(host, "alice").Progress!.Currency[1];
        Assert.NotEqual(0, await Buy(alice, rare)); // Zeny cannot substitute for Supporter Tokens.
        await Gm(host, "alice", "!item " + tokens + " 100");
        var session = Session(host, "alice");
        Task<long> Tokens() => host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(host.Service<CharacterService>().CountOf(session, tokens)));
        Assert.Equal(100, await Tokens());
        var accounts = host.Service<AccountStore>();
        FixtureSql.Execute(accounts, $"CREATE TRIGGER reject_rare_dye BEFORE INSERT ON item WHEN NEW.entry={rare} BEGIN SELECT RAISE(ABORT,'rare dye'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => host.Service<GameLoop>().InvokeAsync(() =>
                host.Service<ShopService>().BuyAsync(session, new BuyCommodityReq
                { CommodityId = CharacterAppearanceRules.CommodityForItem(rare), Count = 1, Price = 0 })));
            Assert.Equal(100, await Tokens());
            Assert.DoesNotContain(session.Items, i => i.Entry == rare);
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER reject_rare_dye"); }
        Assert.Equal(0, await Buy(alice, rare, quote: 0)); // An untrusted quote cannot lower the server-owned cost.
        Assert.Equal(25, await Tokens());
        Assert.Single(session.Items, i => i.Entry == rare);
        Assert.Equal(before, session.Progress!.Currency[1]); // no Zeny was taken for a rare colour
        Assert.NotEqual(0, await Buy(alice, rare, quote: 0)); // 25 tokens left: not enough for another
    }

    private static GameSession Session(SyntheticHost host, string account) => host.Service<SessionRegistry>().All().Single(s => s.Account == account);
    private static long Selection(IEnumerable<CustomData> values) => values.FirstOrDefault(d => d.Id == CharacterAppearanceRules.SelectionCustomData)?.CustomParam.LongParam ?? 0;
    private static async Task Enable(BotClient bot)
    {
        await bot.SendGameBytesAsync(CharacterAppearanceRules.ClientReadyMessage, [CharacterAppearanceRules.ClientVersion]);
        await Next(bot, WorldCoreService.CustomChange, CustomDataChange.Parser, p => p.CustomDatas.Any(c => c.Id == CharacterAppearanceRules.PriceCustomData));
    }
    private static async Task<int> Buy(BotClient bot, int item, int quote = 7)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = CharacterAppearanceRules.CommodityForItem(item), Count = 1, Price = quote });
        return (await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser)).Result;
    }
    private static async Task<int> Use(BotClient bot, long guid, int count = 1)
    {
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = count });
        return (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result;
    }
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
}
