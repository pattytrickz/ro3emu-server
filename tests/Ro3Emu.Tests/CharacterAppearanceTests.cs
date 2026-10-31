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

/// <summary>
/// The Dye Merchant's tiered catalogue (CharacterAppearanceRules): colours are account unlocks bought once with Crystals
/// (or, Rare and Legendary, Supporter Tokens), owned colours switch for free, first-catalogue bottles and worn colours
/// carry over, and hooks before version 4 still see every dye in its nearest first-catalogue colour.
/// </summary>
public sealed class CharacterAppearanceTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "appearance-fixture-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private const int Crimson = 1, Azure = 2, Silver = 6, Ruby = 18, VoidPurple = 26;
    private const int Crystal = CharacterAppearanceRules.Crystal, Token = CharacterAppearanceRules.Token;

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
        // Crystals and Supporter Tokens, currencies like Zeny: the catalogue's two payments.
        foreach (int currency in new[] { Crystal, Token })
            items[currency.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                JsonNode.Parse($$"""{"_iID":{{currency}},"_iType":1,"_iBagTab":1,"_iTrade":0}""");
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

    private Task<SyntheticHost> Start(bool enabled = true) => SyntheticHost.StartAsync(o =>
    {
        o.GameDataDir = _data; o.CharacterAppearanceEnabled = enabled;
        o.GmAccounts = ["alice"]; o.GmToolAccounts = ["bob"];
    });

    [Fact]
    public void CatalogueFollowsTheGuide()
    {
        var dyes = CharacterAppearanceRules.Dyes;
        Assert.Equal(12, dyes.Count(d => d.Tier == DyeTier.Common));
        Assert.Equal(12, dyes.Count(d => d.Tier == DyeTier.Rare));
        Assert.Equal(6, dyes.Count(d => d.Tier == DyeTier.Legendary));
        Assert.All(dyes.Where(d => d.Tier == DyeTier.Common), d => Assert.True(d.Crystals is 350 or 500 or 700 && d.Tokens == 0));
        Assert.All(dyes.Where(d => d.Tier == DyeTier.Rare), d => Assert.Contains((d.Crystals, d.Tokens), new[] { (1400, 75), (1750, 100), (2100, 125) }));
        Assert.All(dyes.Where(d => d.Tier == DyeTier.Legendary), d => Assert.Equal((2800, 150), (d.Crystals, d.Tokens)));
        Assert.Equal(DyeTier.Retired, CharacterAppearanceRules.DyeOf(Silver).Tier);
        // The first catalogue's saved ids keep their meaning; every newer colour has a nearest first-catalogue colour.
        Assert.Equal(2 | 3 << 4 | 256, CharacterAppearanceRules.Pack(2, 3, true));
        Assert.All(Enumerable.Range(9, 23), p => Assert.InRange(CharacterAppearanceRules.LegacyOf(p), 1, 8));
        long full = CharacterAppearanceRules.PackFull(31, 26, true);
        Assert.Equal((31, 26, true), (CharacterAppearanceRules.FullHair(full), CharacterAppearanceRules.FullArmour(full), CharacterAppearanceRules.FullAura(full)));
    }

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
        Assert.NotNull(data.Row("ItemConfig", CharacterAppearanceRules.Item(VoidPurple, armour: true)));
    }

    [Fact]
    public async Task DisabledRealmDoesNotPlaceTheDyeMerchant()
    {
        await using var host = await Start(enabled: false);
        Assert.DoesNotContain(host.Service<WorldData>().Map(CharacterAppearanceRules.MerchantMap)!.Npcs,
            n => n.NpcId == CharacterAppearanceRules.MerchantId);
    }

    [Fact]
    public async Task AColourIsBoughtOnceAndEveryCharacterOfTheAccountSwitchesToItForFree()
    {
        await using var host = await Start();
        await using var bob = await host.InWorldAsync("Bob");
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            Assert.NotEqual(0, await Buy(alice, Crimson)); // hooks not ready: nothing is sold
            await Enable(alice);
            await Gm(host, "alice", $"!item {Crystal} 2000");
            var s = Session(host, "alice");
            Assert.Equal(0, await Buy(alice, Crimson, quote: 0)); // the catalogue prices it, not the quote
            Assert.Equal(1500, await Count(host, s, Crystal));
            Assert.Equal(Crimson, s.CharacterAppearance!.Hair);
            await Next(bob, WorldCoreService.CustomChange, CustomDataChange.Parser,
                p => p.EntityId == s.Role!.Id && Full(p.CustomDatas) == CharacterAppearanceRules.PackFull(Crimson, 0, false));
            Assert.Equal(0, await Buy(alice, Azure));
            Assert.Equal(800, await Count(host, s, Crystal));
            Assert.Equal(0, await Buy(alice, Crimson)); // owned: free switch back
            Assert.Equal(800, await Count(host, s, Crystal));
            Assert.Equal(Crimson, s.CharacterAppearance!.Hair);
            Assert.NotEqual(0, await Buy(alice, Crimson)); // already wearing it
            Assert.True(CharacterAppearanceRules.Owns(s.DyeUnlocks, Azure));
        }
        await host.RestartAsync();
        await using (var again = await host.InWorldAsync("Alice", create: false))
        {
            Assert.Equal(CharacterAppearanceRules.Pack(Crimson, 0, false), Selection(again.Self!.CustomDatas));
            Assert.Equal(CharacterAppearanceRules.PackFull(Crimson, 0, false), Full(again.Self!.CustomDatas));
        }
        // A second character of the account owns the same colours.
        await using var second = host.NewBot();
        await second.LoginAsync("alice", "pw");
        await second.EnterGateAsync();
        await second.EnterWorldAsync((await second.CreateRoleAsync("Alicetwo")).RoleBaseInfo.Id);
        long owned = await Enable(second);
        Assert.True(CharacterAppearanceRules.Owns(owned, Crimson) && CharacterAppearanceRules.Owns(owned, Azure));
        Assert.Equal(0, await Buy(second, Azure));
        Assert.Equal(0, await Count(host, Session(host, "alice"), Crystal));
    }

    [Fact]
    public async Task RareAndLegendaryTakeCrystalsOrTokensAndCommonOnlyCrystals()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        var s = Session(host, "alice");
        await Gm(host, "alice", $"!item {Token} 100");
        Assert.NotEqual(0, await Buy(alice, Crimson, tokens: true)); // Common colours are not sold for tokens
        Assert.NotEqual(0, await Buy(alice, VoidPurple, tokens: true)); // 150 tokens; 100 is not enough
        Assert.False(CharacterAppearanceRules.Owns(s.DyeUnlocks, VoidPurple));
        Assert.Equal(0, await Buy(alice, Ruby, tokens: true));
        Assert.Equal(0, await Count(host, s, Token));
        await Gm(host, "alice", $"!item {Crystal} 2800");
        Assert.Equal(0, await Buy(alice, VoidPurple));
        Assert.Equal(0, await Count(host, s, Crystal));
        Assert.Equal(VoidPurple, s.CharacterAppearance!.Hair);
        Assert.Equal(CharacterAppearanceRules.LegacyOf(VoidPurple),
            CharacterAppearanceRules.Hair(Selection([CharacterAppearanceService.Marker(s)])));
    }

    [Fact]
    public async Task SilverIsNoLongerSoldButAGrantedOwnerSwitchesToIt()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        await Gm(host, "alice", $"!item {Crystal} 5000");
        Assert.NotEqual(0, await Buy(alice, Silver));
        Assert.NotEqual(0, await Buy(alice, Silver, tokens: true));
        await Gm(host, "alice", "!dye grant alice Silver");
        Assert.Equal(0, await Buy(alice, Silver));
        Assert.Equal(5000, await Count(host, Session(host, "alice"), Crystal));
        Assert.Equal(Silver, Session(host, "alice").CharacterAppearance!.Hair);
    }

    [Fact]
    public async Task AFirstCatalogueBottleUnlocksItsColourAndWornColoursAreKept()
    {
        await using var host = await Start();
        int bottle = CharacterAppearanceRules.Item(Azure, armour: false);
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            await Enable(alice);
            await Gm(host, "alice", "!item " + bottle + " 2");
            var s = Session(host, "alice");
            long guid = s.Items.Single(i => i.Entry == bottle).Guid;
            Assert.Equal(0, await Use(alice, guid));
            Assert.Equal(1, s.Items.Single(i => i.Guid == guid).Stack);
            Assert.True(CharacterAppearanceRules.Owns(s.DyeUnlocks, Azure));
            Assert.NotEqual(0, await Use(alice, guid)); // already wearing it: the second bottle is kept
            Assert.Equal(1, s.Items.Single(i => i.Guid == guid).Stack);
        }
        // A colour worn from before the unlocks (no dye_unlock row) is unlocked on the next world entry.
        FixtureSql.Execute(host.Service<AccountStore>(), "DELETE FROM dye_unlock");
        await host.RestartAsync();
        await using var again = await host.InWorldAsync("Alice", create: false);
        Assert.True(CharacterAppearanceRules.Owns(await Enable(again), Azure));
    }

    [Fact]
    public async Task AFailedSaveTakesNoPaymentAndUnlocksNothing()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await Enable(alice);
        await Gm(host, "alice", $"!item {Crystal} 500");
        var s = Session(host, "alice");
        var accounts = host.Service<AccountStore>();
        FixtureSql.Execute(accounts, "CREATE TRIGGER reject_dye BEFORE UPDATE OF character_appearance ON role BEGIN SELECT RAISE(ABORT,'dye'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => host.Service<GameLoop>().InvokeAsync(() =>
                host.Service<ShopService>().BuyAsync(s, new BuyCommodityReq
                { CommodityId = CharacterAppearanceRules.Commodity(Crimson, false, false), Count = 1 })));
            Assert.Equal(500, await Count(host, s, Crystal));
            Assert.Equal(0, s.CharacterAppearance!.Hair);
            Assert.False(CharacterAppearanceRules.Owns(s.DyeUnlocks, Crimson));
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER reject_dye"); }
        Assert.Equal(0, await Buy(alice, Crimson));
        Assert.Equal(0, await Count(host, s, Crystal));
    }

    [Fact]
    public async Task AnUnlockRowThatFailsAfterPaymentComesBackAtTheNextWorldEntry()
    {
        await using var host = await Start();
        var accounts = host.Service<AccountStore>();
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            await Enable(alice);
            await Gm(host, "alice", $"!item {Crystal} 500");
            FixtureSql.Execute(accounts, "CREATE TRIGGER reject_unlock BEFORE INSERT ON dye_unlock BEGIN SELECT RAISE(ABORT,'unlock'); END");
            try { Assert.Equal(0, await Buy(alice, Crimson)); }
            finally { FixtureSql.Execute(accounts, "DROP TRIGGER reject_unlock"); }
            Assert.Equal(Crimson, Session(host, "alice").CharacterAppearance!.Hair);
        }
        await host.RestartAsync();
        await using var again = await host.InWorldAsync("Alice", create: false);
        Assert.True(CharacterAppearanceRules.Owns(await Enable(again), Crimson));
    }

    [Fact]
    public async Task UnsupportedArmourAndMultiBuyRefuseWithoutPayment()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        // Novice armour has masks for both sexes; a first job's body has none yet.
        await host.Service<GameLoop>().InvokeAsync(() => { Session(host, "alice").Progress!.Career = 1100; return Task.CompletedTask; });
        await Enable(alice);
        await Gm(host, "alice", $"!item {Crystal} 1000");
        Assert.NotEqual(0, await Buy(alice, Crimson, armour: true));
        Assert.NotEqual(0, await Buy(alice, Crimson, count: 2));
        Assert.Equal(1000, await Count(host, Session(host, "alice"), Crystal));
    }

    [Fact]
    public async Task OlderHooksGetNoMerchantButStillSeeTheNearestColour()
    {
        await using var host = await Start();
        await using var alice = await host.InWorldAsync("Alice");
        await alice.SendGameBytesAsync(CharacterAppearanceRules.ClientReadyMessage, [CharacterAppearanceRules.SupporterShopClientVersion]);
        var ready = await Next(alice, WorldCoreService.CustomChange, CustomDataChange.Parser,
            p => p.CustomDatas.Any(c => c.Id == CharacterAppearanceRules.PriceCustomData));
        Assert.Equal(0, ready.CustomDatas.Single(c => c.Id == CharacterAppearanceRules.PriceCustomData).CustomParam.LongParam);
        Assert.DoesNotContain(ready.CustomDatas, c => c.Id == CharacterAppearanceRules.OwnedCustomData);
        await Gm(host, "alice", $"!item {Crystal} 1000");
        Assert.NotEqual(0, await Buy(alice, Crimson));
        Assert.Equal(1000, await Count(host, Session(host, "alice"), Crystal));
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
        await Enable(alice, owned: false);
        await Gm(host, "alice", "!aura on");
        Assert.False(Session(host, "alice").CharacterAppearance!.Aura);
        Assert.NotEqual(0, await Buy(alice, Crimson));
        foreach (long marker in new[] { CharacterAppearanceRules.SelectionCustomData, CharacterAppearanceRules.PriceCustomData,
                     CharacterAppearanceRules.RarePriceCustomData, CharacterAppearanceRules.FullSelectionCustomData, CharacterAppearanceRules.OwnedCustomData })
            await Assert.ThrowsAsync<ArgumentException>(() => host.Service<GameLoop>().InvokeAsync(() =>
                host.Service<WorldCoreService>().SetCustomAsync(Session(host, "alice"), new CustomData
                {
                    Id = marker,
                    CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = 256 }
                })));
    }

    private static GameSession Session(SyntheticHost host, string account) => host.Service<SessionRegistry>().All().Single(s => s.Account == account);
    private static Task<long> Count(SyntheticHost host, GameSession s, int entry) =>
        host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(host.Service<CharacterService>().CountOf(s, entry)));
    private static long Selection(IEnumerable<CustomData> values) => values.FirstOrDefault(d => d.Id == CharacterAppearanceRules.SelectionCustomData)?.CustomParam.LongParam ?? 0;
    private static long Full(IEnumerable<CustomData> values) => values.FirstOrDefault(d => d.Id == CharacterAppearanceRules.FullSelectionCustomData)?.CustomParam.LongParam ?? -1;

    /// <summary>Sends the version-4 ready message; returns the owned-colours marker (0 when <paramref name="owned"/> is false).</summary>
    private static async Task<long> Enable(BotClient bot, bool owned = true)
    {
        await bot.SendGameBytesAsync(CharacterAppearanceRules.ClientReadyMessage, [CharacterAppearanceRules.ClientVersion]);
        var ready = await Next(bot, WorldCoreService.CustomChange, CustomDataChange.Parser, p => p.CustomDatas.Any(c => c.Id == CharacterAppearanceRules.PriceCustomData));
        return owned ? ready.CustomDatas.Single(c => c.Id == CharacterAppearanceRules.OwnedCustomData).CustomParam.LongParam : 0;
    }

    private static async Task<int> Buy(BotClient bot, int palette, bool tokens = false, bool armour = false, int count = 1, int quote = 0)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq
        { CommodityId = CharacterAppearanceRules.Commodity(palette, armour, tokens), Count = count, Price = quote });
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
