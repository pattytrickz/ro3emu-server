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
    private const int CoinBag = 13123, BlessedWeaponOreCommodity = 13104, TeaParty = 5000002, TeaPartySuit = 40050010;
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

        // A Fashion-tab outfit at a quarter of its price (1880 -> 450) unlocks the look.
        var outfit = await Buy(alice, TeaParty);
        Assert.Equal((0, 450), (outfit.Result, outfit.Price));
        Assert.Equal(crystals - 1000 - 600 - 450, await Crystals(alice));
        Assert.True(await OnLoop(() => Session(alice).Wardrobe!.AllLightUpAppearances.ContainsKey(TeaPartySuit)));
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
            Assert.Equal((F2PRules.Crystal, rule.Price), (price[0].GetInt32(), price[1].GetInt32()));
            Assert.Equal((rule.Period, rule.Count, 0), ((int)GameData.Int(row.Value, "_iPurchaseTimesLimit"),
                (int)GameData.Int(row.Value, "_iPurchaseNum"), (int)GameData.Int(row.Value, "_iDiscount")));
        }
        F2P.Enabled = false;
        Assert.Equal(100, data.Row("ShopCommodityConfig", CoinBag)!.Value.GetProperty("_kItemPerPrice")[1].GetInt32());
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
