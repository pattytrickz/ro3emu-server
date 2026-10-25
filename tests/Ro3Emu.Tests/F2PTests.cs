using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

/// <summary>
/// ServerOptions.F2P (house rules, Ro3Emu.Shared.F2PRules): Crystals and Meow Fish Jerky from the pass challenges and
/// MVP fights, refine ores from MVPs and dungeons, the Crystal Shop's weekly caps and the cheaper cosmetics; and the GM
/// audit log. The realm's settings: OpenContent, Pass:Play, F2P and GmAudit on.
/// </summary>
public sealed class F2PTests : IAsyncLifetime
{
    private const int CoinBag = 13123, BlessedWeaponOreCommodity = 13104, Stroll = 5000005, StrollSuit = 40040030;
    private const int BlessedWeaponOre = 1200100, BlessedArmorOre = 1200102, HighDensityOridecon = 1200004, HighDensityElunium = 1200005;
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-f2p-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication app = null!;
    private string http = "";
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.OpenContent.Enabled = true;
            o.Pass.FreePremium = true;
            o.Pass.Play = true;
            o.F2P.Enabled = true;
            o.GmAudit = true;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private F2POptions F2P => Service<IOptions<ServerOptions>>().Value.F2P;

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
    private Task<long> Crystals(BotClient bot) => OnLoop(() => Service<CharacterService>().CountOf(Session(bot), F2PRules.Crystal));
    private Task<int> Bag(BotClient bot, params int[] entries) =>
        OnLoop(() => Session(bot).Items.Where(i => entries.Contains(i.Entry)).Sum(i => i.Stack));
    private Task<PassPlayState> Play(BotClient bot) => OnLoop(() => Session(bot).ActivityRuntime!.PassPlay!);

    /// <summary>Posts one pass event and waits until it has been committed (PassPlayTests.Event).</summary>
    private async Task Event(BotClient bot, (NewActivityService.PassGoal, long)[] progress, long dungeons = 0, long mvps = 0)
    {
        long before = await OnLoop(() => Session(bot).ActivityRuntime?.PassPlay?.Sequence ?? 0);
        int unsaved = await OnLoop(() => Session(bot).ActivityRuntime?.PassPlay?.Unsaved ?? 0);
        await OnLoop(() => { Service<NewActivityService>().PassEvent(Session(bot), progress, dungeons: dungeons, mvps: mvps); return 0; });
        for (int i = 0; i < 500; i++)
        {
            var (sequence, now) = await OnLoop(() => (Session(bot).ActivityRuntime?.PassPlay?.Sequence ?? 0,
                Session(bot).ActivityRuntime?.PassPlay?.Unsaved ?? 0));
            if (sequence != before || now != unsaved) return;
            await Task.Delay(20);
        }
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static async Task<BuyCommodityResp> Buy(BotClient bot, int commodity, int count = 1)
    {
        await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = commodity, Count = count });
        return await Next(bot, ShopService.BuyCommodity, BuyCommodityResp.Parser);
    }

    [ClientDataFact]
    public async Task ChallengesAndMvpFightsPayCrystalsAndMvpsAndDungeonsDropRefineOres()
    {
        F2P.MvpBlessedOreChance = F2P.MvpRefineOreChance = F2P.DungeonBlessedOreChance = 1;
        await using var alice = await InWorld("Alice");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        await Event(alice, []);
        long crystals = await Crystals(alice);
        var play = await Play(alice);

        // Five dungeons finish "Clear 5 dungeons" (80 Crystals, 1 Jerky), "Clear a dungeon" when drawn today (20), and
        // each paid dungeon (five a day) drops one Blessed Ore at chance 1; the sixth pays nothing.
        for (int i = 0; i < 6; i++) await Event(alice, [(NewActivityService.PassGoal.Dungeons, 1)], dungeons: 1);
        long expected = F2PRules.WeeklyChallengeCrystals + (play.Daily.Contains("d.dungeon") ? F2PRules.DailyChallengeCrystals : 0);
        Assert.Equal(crystals + expected, await Crystals(alice));
        Assert.Equal(F2PRules.WeeklyChallengeJerky, await Bag(alice, F2PRules.MeowFishJerky));
        Assert.Equal(5, await Bag(alice, BlessedWeaponOre, BlessedArmorOre));

        // MVP fights: 20 Crystals for the first three of a day, a Blessed Ore and 1-2 high-density ores every fight;
        // the third completes "Take part in 3 MVP fights" (80 more and a Jerky).
        crystals = await Crystals(alice);
        for (int i = 0; i < 4; i++) await Event(alice, [(NewActivityService.PassGoal.Mvps, 1)], mvps: 1);
        Assert.Equal(crystals + 3 * F2PRules.MvpCrystals + F2PRules.WeeklyChallengeCrystals, await Crystals(alice));
        Assert.Equal(2 * F2PRules.WeeklyChallengeJerky, await Bag(alice, F2PRules.MeowFishJerky));
        Assert.Equal(9, await Bag(alice, BlessedWeaponOre, BlessedArmorOre));
        Assert.InRange(await Bag(alice, HighDensityOridecon, HighDensityElunium), 4, 8);
    }

    [ClientDataFact]
    public async Task SupportersEarnSupporterTokensOneForOneWithCrystalsAndKeepThem()
    {
        var access = Service<Ro3Emu.Server.Accounts.AccessList>();
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        Assert.Contains("is a supporter", access.Run("supporter alice"));
        foreach (var bot in new[] { alice, bob })
        {
            await Service<NewActivityService>().ReconcileAsync(Session(bot));
            await Event(bot, []);
        }
        Task<long> Tokens(BotClient bot) => OnLoop(() => Service<CharacterService>().CountOf(Session(bot), F2PRules.SupporterToken));

        // An MVP fight pays 20 Crystals to both; only the supporter also gets 20 tokens.
        long aliceCrystals = await Crystals(alice), bobCrystals = await Crystals(bob);
        await Event(alice, [(NewActivityService.PassGoal.Mvps, 1)], mvps: 1);
        await Event(bob, [(NewActivityService.PassGoal.Mvps, 1)], mvps: 1);
        Assert.Equal(aliceCrystals + F2PRules.MvpCrystals, await Crystals(alice));
        Assert.Equal(bobCrystals + F2PRules.MvpCrystals, await Crystals(bob));
        Assert.Equal(F2PRules.MvpCrystals, await Tokens(alice));
        Assert.Equal(0, await Tokens(bob));

        // No longer a supporter: new Crystals bring no tokens, the earned ones stay.
        access.Run("unsupporter alice");
        await Event(alice, [(NewActivityService.PassGoal.Mvps, 1)], mvps: 1);
        Assert.Equal(aliceCrystals + 2 * F2PRules.MvpCrystals, await Crystals(alice));
        Assert.Equal(F2PRules.MvpCrystals, await Tokens(alice));
    }

    [ClientDataFact]
    public async Task WithoutF2PTheChallengesPayPassExpOnly()
    {
        F2P.Enabled = false;
        await using var alice = await InWorld("Alice");
        await Service<NewActivityService>().ReconcileAsync(Session(alice));
        await Event(alice, []);
        long crystals = await Crystals(alice);
        for (int i = 0; i < 5; i++) await Event(alice, [(NewActivityService.PassGoal.Dungeons, 1)], dungeons: 1);
        await Event(alice, [(NewActivityService.PassGoal.Mvps, 1)], mvps: 1);
        Assert.Contains("w.dungeons", (await Play(alice)).WeeklyDone);
        Assert.Equal(crystals, await Crystals(alice));
        Assert.Equal(0, await Bag(alice, F2PRules.MeowFishJerky, BlessedWeaponOre, BlessedArmorOre, HighDensityOridecon, HighDensityElunium));
    }

    [ClientDataFact]
    public async Task ShopChargesTheF2PPriceAndCapsTheWeek()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 50");
        await Gm(alice, "!item 3 5000");
        long crystals = await Crystals(alice);

        // Coin Bag: 1000 Crystals (was 100), one a week (was unlimited).
        var bought = await Buy(alice, CoinBag);
        Assert.Equal((0, 1000), (bought.Result, bought.Price));
        Assert.Equal(crystals - 1000, await Crystals(alice));
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(alice, CoinBag)).Result);

        // Blessed Weapon Ore: still 300, two a week.
        Assert.Equal(ErrorCodes.PurchaseLimit, (await Buy(alice, BlessedWeaponOreCommodity, 3)).Result);
        Assert.Equal(0, (await Buy(alice, BlessedWeaponOreCommodity, 2)).Result);
        Assert.Equal(crystals - 1000 - 600, await Crystals(alice));

        // A Fashion-tab outfit still on Crystals, at a quarter of its price (1880 -> 450), unlocks the look.
        var outfit = await Buy(alice, Stroll);
        Assert.Equal((0, 450), (outfit.Result, outfit.Price));
        Assert.Equal(crystals - 1000 - 600 - 450, await Crystals(alice));
        Assert.True(await OnLoop(() => Session(alice).Wardrobe!.AllLightUpAppearances.ContainsKey(StrollSuit)));
    }

    [ClientDataFact]
    public async Task SupporterCosmeticsCostSupporterTokensNotCrystals()
    {
        const int SupporterMount = 5000008, SupporterMountLook = 60040010;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 50");
        await Gm(alice, "!item 3 5000");
        long crystals = await Crystals(alice);

        // Crystals alone do not buy it: the price is in Supporter Tokens.
        Assert.NotEqual(0, (await Buy(alice, SupporterMount)).Result);
        Assert.Equal(crystals, await Crystals(alice));

        await Gm(alice, $"!item {F2PRules.SupporterToken} 700");
        var bought = await Buy(alice, SupporterMount);
        Assert.Equal((0, 650), (bought.Result, bought.Price));
        Assert.Equal(50, await OnLoop(() => Service<CharacterService>().CountOf(Session(alice), F2PRules.SupporterToken)));
        Assert.Equal(crystals, await Crystals(alice));
        Assert.True(await OnLoop(() => Session(alice).Wardrobe!.AllLightUpAppearances.ContainsKey(SupporterMountLook)));
    }

    /// <summary>Owner's choice (24 Oct 2026): 70% of the Fashion tab is for Supporter Tokens only, and every kind of
    /// appearance (outfit, mount, hairstyle, headwear, backwear) keeps at least one on Crystals.</summary>
    [ClientDataFact]
    public void SeventyPercentOfTheFashionTabIsForSupporterTokensOnly()
    {
        var data = Service<GameData>();
        var fashion = F2PRules.Commodities.Where(c => c.Id / 100 == 50000).ToList();
        Assert.Equal(27, fashion.Count);
        Assert.Equal(19, fashion.Count(c => c.Payment == F2PRules.SupporterToken));
        var crystalKinds = fashion.Where(c => c.Payment == 0)
            .Select(c => GameData.Int(data.Row("ShopCommodityConfig", c.Id)!.Value, "_iClassID")).ToHashSet();
        long[] kinds = [50201, 50202, 50203, 50204, 50207];
        Assert.Equal(kinds, crystalKinds.Order());
    }

    [ClientDataFact]
    public async Task EveryRuleChangesAnExistingCrystalRowAndOnlyWhileF2PIsOn()
    {
        var data = Service<GameData>();
        foreach (var rule in F2PRules.Commodities)
        {
            var row = data.Row("ShopCommodityConfig", rule.Id);
            Assert.True(row.HasValue, $"commodity {rule.Id}");
            var price = row.Value.GetProperty("_kItemPerPrice");
            Assert.Equal((rule.Payment > 0 ? rule.Payment : F2PRules.Crystal, rule.Price), (price[0].GetInt32(), price[1].GetInt32()));
            Assert.Equal((rule.Period, rule.Count, 0), ((int)GameData.Int(row.Value, "_iPurchaseTimesLimit"),
                (int)GameData.Int(row.Value, "_iPurchaseNum"), (int)GameData.Int(row.Value, "_iDiscount")));
        }
        F2P.Enabled = false;
        Assert.Equal(100, data.Row("ShopCommodityConfig", CoinBag)!.Value.GetProperty("_kItemPerPrice")[1].GetInt32());
    }

    [ClientDataFact]
    public async Task ZenyShopSellsInstantPotionsAndElementalArrowsAndTheSelectedArrowSetsTheAttackElement()
    {
        const int YellowPotionRow = 12114, YellowPotion = 1000002, FireArrowRow = 12117, FireArrow = 1002012, Fire = 4;
        var data = Service<GameData>();
        foreach (var added in F2PRules.Added)
        {
            var row = data.Row("ShopCommodityConfig", added.Id);
            Assert.True(row.HasValue, $"commodity {added.Id}");
            var price = row.Value.GetProperty("_kItemPerPrice");
            Assert.Equal((F2PRules.Zeny, added.Price, 120, added.ItemId, 0, 0),
                (price[0].GetInt32(), price[1].GetInt32(), (int)GameData.Int(row.Value, "_iClassID"),
                 (int)GameData.Int(row.Value, "_iItemId"), (int)GameData.Int(row.Value, "_iPurchaseNum"),
                 (int)GameData.Int(row.Value, "_iPurchaseTimesLimit")));
        }

        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 10"); // the Zeny Shop (function 70103) opens at Base Level 10
        await Gm(alice, "!item 1 1000");
        long zeny = await OnLoop(() => Service<CharacterService>().CountOf(Session(alice), F2PRules.Zeny));
        Assert.Equal(0, (await Buy(alice, YellowPotionRow, 3)).Result);
        Assert.Equal(3, await Bag(alice, YellowPotion));
        Assert.Equal(0, (await Buy(alice, FireArrowRow)).Result);
        Assert.Equal(zeny - 400, await OnLoop(() => Service<CharacterService>().CountOf(Session(alice), F2PRules.Zeny)));

        Assert.Equal(1, await OnLoop(() => Service<CombatService>().AttackElement(Session(alice))));
        await alice.SendGameAsync(CharacterService.ArrowSwitch, new ItemArrowSwitchReq { ItemId = FireArrow });
        Assert.Equal(0, (await Next(alice, CharacterService.ArrowSwitch, ItemArrowSwitchResp.Parser)).Result);
        Assert.Equal(Fire, await OnLoop(() => Service<CombatService>().AttackElement(Session(alice))));
    }

    [ClientDataFact]
    public async Task GmAuditLogsEveryCommandWithItsAccount()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!item 1300076 1");
        var lines = File.ReadAllLines(Path.Combine(dataDir, "logs", "gm-audit.log"));
        var line = Assert.Single(lines, l => l.EndsWith("\t!item 1300076 1", StringComparison.Ordinal)).Split('\t');
        Assert.Equal(("alice", "Alice", "tool"), (line[1], line[2], line[3]));
    }
}
