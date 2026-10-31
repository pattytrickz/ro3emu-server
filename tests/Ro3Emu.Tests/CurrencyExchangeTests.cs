using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>2005: the Currency Redemption window, an ItemExchangeConfig row spent for its target at the row's rate.</summary>
public sealed class CurrencyExchangeTests : IDisposable
{
    private const int Zeny = 1, Coin = 2, Rate = 10, Limit = 5000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private readonly string _data = Path.Combine(Path.GetTempPath(), "exchange-fixture-" + Guid.NewGuid().ToString("N"));

    public CurrencyExchangeTests()
    {
        foreach (var file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(_data, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        // Invented row, in the shape of the client's table (key = the item spent, _iTargetID = the item received).
        File.WriteAllText(Path.Combine(_data, "config", "ItemExchangeConfig.json"), string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"{Zeny}\":{{\"_iFunction\":7006,\"_iID\":{Zeny},\"_iRate\":{Rate},\"_iSingleLimit\":{Limit},\"_iTargetID\":{Coin}}}}}"));
    }

    public void Dispose() => Directory.Delete(_data, true);

    [Fact]
    public async Task ZenyIsSpentForMintedCoinAtTheRowsRateAndTheBalancesSurviveARestart()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = _data);
        await using var alice = await host.InWorldAsync("Alice");
        await Gm(host, "!zeny 1000");
        var session = Session(host);
        long zeny = session.Progress!.Currency.GetValueOrDefault(Zeny), coin = session.Progress.Currency.GetValueOrDefault(Coin);
        Assert.Equal(0, await Redeem(alice, Zeny, 300));
        Assert.Equal(zeny - 300, session.Progress.Currency[Zeny]);
        Assert.Equal(coin + 300 * Rate, session.Progress.Currency[Coin]);
        long roleId = session.Role!.Id;
        var saved = host.Service<Ro3Emu.Server.Accounts.RoleStore>().LoadProgress(roleId);
        Assert.Equal((zeny - 300, coin + 300 * Rate), (saved.Currency[Zeny], saved.Currency[Coin]));
    }

    [Fact]
    public async Task ARequestTheRowOrTheBalanceDoesNotAllowChangesNothing()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = _data);
        await using var alice = await host.InWorldAsync("Alice");
        await Gm(host, "!zeny 1000");
        var currency = Session(host).Progress!.Currency;
        long zeny = currency[Zeny], coin = currency.GetValueOrDefault(Coin);
        Assert.Equal(ErrorCodes.Parameter, await Redeem(alice, Coin, 10));          // no row spends Minted Coin
        Assert.Equal(ErrorCodes.Parameter, await Redeem(alice, Zeny, 0));
        Assert.Equal(ErrorCodes.Parameter, await Redeem(alice, Zeny, -5));
        Assert.Equal(ErrorCodes.Parameter, await Redeem(alice, Zeny, Limit + 1));   // over _iSingleLimit
        Assert.Equal(ErrorCodes.CurrencyShort, await Redeem(alice, Zeny, zeny + 1)); // the balance stays under the limit
        Assert.Equal((zeny, coin), (currency[Zeny], currency.GetValueOrDefault(Coin)));
    }

    private static GameSession Session(SyntheticHost host) => host.Service<SessionRegistry>().All().Single(s => s.Account == "alice");

    private static async Task<int> Redeem(BotClient bot, long item, long count)
    {
        await bot.SendGameAsync(CurrencyExchangeService.Exchange, new ItemExchangeReq { ItemId = item, Count = count });
        var packet = await bot.Inbox.WaitAsync(p => p.IsGame(CurrencyExchangeService.Exchange), Wait);
        return (int)ItemExchangeResp.Parser.ParseFrom(packet.Body).Result;
    }

    private static async Task Gm(SyntheticHost host, string command)
    {
        using var client = new HttpClient();
        using var response = await client.PostAsync(host.Http + "/ro3emu/gm?account=alice", new StringContent(command));
        response.EnsureSuccessStatusCode();
    }
}
