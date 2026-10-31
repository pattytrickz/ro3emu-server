using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Sol RO3 house events (EventService): rate windows, login gifts and the GM gift tool. None of it is RO3's.</summary>
public sealed class EventTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-events-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(Start);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.StatusExcludedAccounts = ["aibot*"];
            o.LoginGifts = [new LoginGift { Id = "pumpkin-1", Start = Start.AddHours(-1), End = Start.AddDays(1), Item = 1250709, Count = 3, Title = "Happy Halloween" }];
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public void OverlappingWindowsTakeTheHighestOfEachRate()
    {
        var weekend = new EventRateWindow { Start = Start, End = Start.AddDays(2), ExpPercent = 200, CardRolls = 2 };
        var hour = new EventRateWindow { Start = Start, End = Start.AddHours(1), ExpPercent = 150, DropRolls = 2 };
        Assert.Equal((200, 2, 2), EventService.Combine([weekend, hour], Start.AddMinutes(30)));
        Assert.Equal((200, 1, 2), EventService.Combine([weekend, hour], Start.AddHours(1))); // the end is exclusive
        Assert.Equal((100, 1, 1), EventService.Combine([weekend, hour], Start.AddMinutes(-1)));
        // Out-of-range numbers are clamped rather than trusted.
        Assert.Equal((1000, 5, 1), EventService.Combine([new EventRateWindow { Start = Start, End = Start.AddDays(1), ExpPercent = 9000, DropRolls = 50, CardRolls = 0 }], Start));
        Assert.Equal("x2 EXP, x2 loot, x3 card chance", EventService.Describe(200, 2, 3));
        Assert.Equal("+50% EXP", EventService.Describe(150, 1, 1));
    }

    [Fact]
    public void EventLootRollsKeepTheirItemsButNeverTheirExp()
    {
        const int Card = 1440100, Zeny = 2, BaseExp = 100, JobExp = 101;
        int rolls = 0;
        void Roll(List<(int Entry, int Count)> into)
        {
            rolls++;
            into.Add((BaseExp, 50));
            into.Add((JobExp, 50));
            into.Add((Zeny, 10));
            into.Add((Card, 1));
        }

        // Two loot rolls and three card rolls: the second roll keeps Zeny and its card, the third only its card.
        var drops = new List<(int Entry, int Count)>();
        RewardService.RollWithExtraCards(drops, Roll, e => e == Card, cardRolls: 3, dropRolls: 2);
        Assert.Equal(3, rolls);
        Assert.Equal([(BaseExp, 50), (JobExp, 50), (Zeny, 10), (Card, 1), (Zeny, 10), (Card, 1), (Card, 1)], drops);
    }

    [Fact]
    public void GiftFiltersMatchTheirRoles()
    {
        Role R(int level, long lastLogin) => new(1, "a", 1, "A", 1, 1000, level, 1, 0, lastLogin, 0, 0, 0, 0, 0);
        var now = Start;
        var (level, used) = EventService.GiftFilter(["level", "40-60", "Halloween"], now);
        Assert.Equal(2, used);
        Assert.True(level(R(40, 0), false));
        Assert.False(level(R(61, 0), true));
        var (active, _) = EventService.GiftFilter(["active", "7"], now);
        Assert.True(active(R(1, now.AddDays(-6).ToUnixTimeSeconds()), false));
        Assert.False(active(R(1, now.AddDays(-8).ToUnixTimeSeconds()), false));
        Assert.True(active(R(1, 0), true)); // online now counts as active
        Assert.Equal(1, EventService.GiftFilter(["online"], now).Used);
        Assert.Throws<ArgumentException>(() => EventService.GiftFilter(["everyone"], now));
    }

    /// <summary>
    /// A login gift arrives once per role, however often it logs in; "!gift" reaches offline roles; the AI players
    /// get neither.
    /// </summary>
    [ClientDataFact]
    public async Task LoginGiftsArriveOnceAndGmGiftsReachOfflineRolesButNoBots()
    {
        long bobId;
        await using (var bob = await InWorld("Bob"))
        {
            bobId = (long)bob.Self!.Uuid;
            await Letters(bobId, 1);
        }
        await using (var again = await InWorld("Bob", create: false)) await Task.Delay(500);
        var gift = Assert.Single(await Letters(bobId, 1));
        Assert.Equal("Happy Halloween", gift.Title);
        Assert.Equal(new MailService.Attachment(1250709, 3), Assert.Single(gift.Attachments));

        await using var bot = await InWorld("aibot1");
        await using var alice = await InWorld("Alice");
        await Letters((long)alice.Self!.Uuid, 1);
        await Gm(alice.Account!, "!gift 1000001 5 all Candy for everyone");
        var bobs = await Letters(bobId, 2);
        Assert.Contains(bobs, l => l.Title == "Candy for everyone" && l.Attachments.Single() == new MailService.Attachment(1000001, 5));
        Assert.Empty(Box((long)bot.Self!.Uuid)?.Letters ?? []);

        await Gm(alice.Account!, "!gift 1000001 5 level 50-99 Veterans only");
        await Task.Delay(300);
        Assert.Equal(2, Box(bobId)!.Letters.Count);
    }

    /// <summary>A bundle gift: one mail with every attachment in order and its own message, still claimed once.</summary>
    [ClientDataFact]
    public async Task ABundleGiftIsOneMailWithItsMessageAndEveryAttachment()
    {
        await using var alice = await InWorld("Alice");
        long id = (long)alice.Self!.Uuid;
        await Letters(id, 1); // the Halloween login gift
        var thanks = new LoginGift
        {
            Id = "thanks-1", Start = Start.AddHours(-1), End = Start.AddDays(30), Item = 1002001, Count = 500,
            Title = "Thank you", Message = "Thanks for reporting bugs.",
            More = [new LoginGiftItem { Item = 1294703, Count = 3 }, new LoginGiftItem { Item = 1250709, Count = 5 }, new LoginGiftItem { Item = 0, Count = 9 }],
        };
        Assert.Equal(1, await Service<EventService>().ClaimAsync(id, "Alice", [thanks]));
        Assert.Equal(0, await Service<EventService>().ClaimAsync(id, "Alice", [thanks]));
        var letter = Assert.Single(await Letters(id, 2), l => l.Title == "Thank you");
        Assert.Equal("Thanks for reporting bugs.", letter.Content);
        Assert.Equal(new MailService.Attachment[] { new(1002001, 500), new(1294703, 3), new(1250709, 5) }, letter.Attachments);
    }

    [ClientDataFact]
    public async Task GmRatesRunForTheirHoursAndAreKeptInTheDatabase()
    {
        await using var alice = await InWorld("Alice");
        Assert.Equal((100, 1, 1), Service<EventService>().Current);
        await Gm(alice.Account!, "!rates 2 200 1 2 Bonus Weekend");
        Assert.Equal((200, 1, 2), Service<EventService>().Current);
        var kept = JsonSerializer.Deserialize<List<EventRateWindow>>(Service<RoleStore>().ServerState(EventService.WindowsKey)!)!;
        Assert.Equal("Bonus Weekend", Assert.Single(kept).Name);

        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal((100, 1, 1), Service<EventService>().Current);
        await Gm(alice.Account!, "!rates 1 150");
        await Gm(alice.Account!, "!rates stop");
        Assert.Equal((100, 1, 1), Service<EventService>().Current);
    }

    private MailService.Mailbox? Box(long id) =>
        Service<RoleStore>().LoadMailbox(id) is { } bytes ? JsonSerializer.Deserialize<MailService.Mailbox>(bytes) : null;

    /// <summary>The role's letters once it has at least <paramref name="count"/> (gifts are mailed just after login).</summary>
    private async Task<List<MailService.Letter>> Letters(long role, int count)
    {
        for (int i = 0; i < 50; i++)
        {
            if (Box(role)?.Letters is { } letters && letters.Count >= count) return letters;
            await Task.Delay(100);
        }
        return Box(role)?.Letters ?? [];
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(string account, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
