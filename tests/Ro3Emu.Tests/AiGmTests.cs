using System.Text;
using System.Text.Json;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The AI GM's loopback POST /ro3emu/aigm (AiGmService): only within ServerOptions.AiGm, never on top of the
/// owner's events, gifts only from the owner's item pool. A Sol RO3 house rule, not RO3's.</summary>
public sealed class AiGmTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const int PoolItem = 1000000, OtherItem = 100;

    [Fact]
    public void ALimitCountsTheBusiestStretchOfItsSpan()
    {
        var week = TimeSpan.FromDays(7);
        DateTimeOffset[] done = [Now.AddDays(-6), Now.AddDays(-1)];
        Assert.False(AiGmService.Fits(done, Now, 2, week)); // three inside one week
        Assert.True(AiGmService.Fits(done, Now.AddDays(1.5), 2, week)); // the first has left the stretch
        // A planned action counts at its own time: one in five days fills that week too.
        Assert.False(AiGmService.Fits([Now.AddDays(5), Now.AddDays(6)], Now.AddDays(1), 2, week));
        Assert.True(AiGmService.Fits([], Now, 1, week));
    }

    [Fact]
    public async Task EveryActionIsRefusedWhileTheAiGmIsOff()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = SyntheticData.Dir, new ManualClock(Now));
        var refused = await Post(host, new { action = "announce", text = "Hello Midgard" });
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("off", refused.GetProperty("message").GetString());
        var limits = await Post(host, new { action = "limits" });
        Assert.True(limits.GetProperty("ok").GetBoolean());
        Assert.False(limits.GetProperty("data").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task RateWindowsStayWithinTheCapsAndNeverOverlapAnotherWindow()
    {
        await using var host = await StartAsync();
        Assert.False(await Ok(host, Rates(Now.AddHours(1), 2, 300))); // over MaxExpPercent
        Assert.False(await Ok(host, Rates(Now.AddHours(1), 6, 150))); // over MaxRateHours
        Assert.False(await Ok(host, Rates(Now.AddDays(1).AddHours(1), 2, 150))); // inside the owner's window
        Assert.False(await Ok(host, Rates(Now.AddDays(9), 2, 150))); // past MaxDaysAhead
        Assert.True(await Ok(host, Rates(Now.AddHours(1), 2, 150, "Lunch Rush")));
        Assert.False(await Ok(host, Rates(Now.AddHours(2), 2, 150))); // overlaps its own
        Assert.True(await Ok(host, Rates(Now.AddDays(3), 2, 120, "Night Owls")));
        Assert.False(await Ok(host, Rates(Now.AddDays(4), 2, 120))); // a third in one week
        Assert.True(await Ok(host, new { action = "cancel", kind = "rates", name = "Night Owls" }));
        Assert.True(await Ok(host, Rates(Now.AddDays(4), 2, 120, "Night Owls 2"))); // the planned one no longer counts
        var list = (await Post(host, new { action = "list" })).GetProperty("data").GetProperty("rates");
        Assert.Equal(["Lunch Rush", "Bonus Sunday", "Night Owls 2"],
            list.EnumerateArray().Select(w => w.GetProperty("name").GetString()!).ToArray());
        Assert.False(list[1].GetProperty("mine").GetBoolean()); // the owner's stays the owner's
    }

    [Fact]
    public async Task GiftsComeOnlyFromThePoolAndMvpsAndAnnouncementsStayWithinTheirLimits()
    {
        await using var host = await StartAsync();
        Assert.False(await Ok(host, new { action = "gift", item = OtherItem, count = 1 })); // not in the pool
        Assert.False(await Ok(host, new { action = "gift", item = PoolItem, count = 4 })); // over its MaxCount
        Assert.True(await Ok(host, new { action = "gift", item = PoolItem, count = 3, filter = "active 7", title = "Thanks!" }));
        Assert.False(await Ok(host, new { action = "logingift", id = "candy", item = PoolItem, count = 1, hours = 24 })); // a second this week
        Assert.False(await Ok(host, new { action = "mvp", cfg = 2 })); // not in the MVP pool
        Assert.False(await Ok(host, new { action = "announce", text = "Free stuff at www.example.com" }));
        Assert.False(await Ok(host, new { action = "announce", text = "!item 1 99" }));
        Assert.False(await Ok(host, new { action = "announce", text = "Hey @everyone" }));
        Assert.True(await Ok(host, new { action = "announce", text = "An MVP stirs in the fields of Prontera!" }));
        var used = (await Post(host, new { action = "limits" })).GetProperty("data").GetProperty("used");
        Assert.Equal(1, used.GetProperty("giftsThisWeek").GetInt32());
        Assert.Equal(1, used.GetProperty("announcementsToday").GetInt32());
    }

    [Fact]
    public async Task TheStaffPanelsSwitchRulesPoolAndEventsNeedItsKeyAndThenRule()
    {
        await using var host = await StartAsync();
        object Save(bool on, int item, string[] events) => new
        {
            action = "setconfig",
            config = new { enabled = on, rules = "- No gifts on weekdays\n- Keep it spooky", itemPool = new[] { new { item, maxCount = 1 } }, events, by = "Owner" },
        };
        Assert.False(await Ok(host, Save(true, OtherItem, ["gift"]))); // no key
        Assert.False(await Ok(host, Save(true, OtherItem, ["gift"]), "wrong")); // a wrong key
        Assert.False(await Ok(host, Save(true, 424242, ["gift"]), Key)); // not in the game data
        Assert.False(await Ok(host, Save(true, OtherItem, ["giveaway"]), Key)); // not an event
        Assert.True(await Ok(host, Save(false, OtherItem, ["gift"]), Key));
        Assert.False(await Ok(host, new { action = "gift", item = OtherItem, count = 1 })); // switched off
        Assert.True(await Ok(host, Save(true, OtherItem, ["gift"]), Key));
        Assert.False(await Ok(host, new { action = "gift", item = PoolItem, count = 1 })); // the panel's pool replaced the settings'
        Assert.True(await Ok(host, new { action = "gift", item = OtherItem, count = 1 }));
        Assert.False(await Ok(host, new { action = "announce", text = "Hello" })); // not an allowed event
        var limits = (await Post(host, new { action = "limits" })).GetProperty("data");
        Assert.True(limits.GetProperty("on").GetBoolean());
        Assert.Contains("Keep it spooky", limits.GetProperty("rules").GetString());
        Assert.Equal(["gift"], limits.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    private const string Key = "panel-key-for-tests";

    private static Task<SyntheticHost> StartAsync() => SyntheticHost.StartAsync(o =>
    {
        o.GameDataDir = SyntheticData.Dir;
        o.EventRates = [new EventRateWindow { Name = "Bonus Sunday", Start = Now.AddDays(1), End = Now.AddDays(1).AddHours(8), ExpPercent = 200 }];
        o.AiGm = new AiGmOptions
        {
            Enabled = true, ItemPool = [new AiGmItem { Item = PoolItem, MaxCount = 3 }], MaxGiftsPerWeek = 1, MvpPool = [1], ConfigKey = Key,
        };
    }, new ManualClock(Now));

    private static object Rates(DateTimeOffset start, double hours, int exp, string name = "Bonus") =>
        new { action = "rates", start, hours, expPercent = exp, name };

    private static async Task<bool> Ok(SyntheticHost host, object request, string? key = null) =>
        (await Post(host, request, key)).GetProperty("ok").GetBoolean();

    private static async Task<JsonElement> Post(SyntheticHost host, object request, string? key = null)
    {
        using var client = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, host.Http + "/ro3emu/aigm")
        {
            Content = new StringContent(JsonSerializer.Serialize(request, AiGmService.Json), Encoding.UTF8, "application/json"),
        };
        if (key != null) message.Headers.Add("X-AiGm-Key", key);
        var response = await client.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
