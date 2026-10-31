using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

/// <summary>
/// Timed items (ItemConfig _iTimeBoundEnum 1, _kTimeBoundPara [minutes]): they lapse their minutes after they are
/// received, the client is told when (ItemBaseInfo.expireTm), an expired one cannot be used and a lapse return turns it
/// into its _iSeasonLapseProduct. The fixture's 1950900 lasts 30 minutes and lapses into 7 of currency 2.
/// </summary>
public sealed class TimedItemTests
{
    private const int Timed = 1950900, LapseCurrency = 2;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ATimedItemLapsesItsMinutesAfterItIsReceived()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        var session = Session(host);
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await OnLoop(host, () => host.Service<CharacterService>().AddItemAsync(session, Timed, 1));
        await OnLoop(host, () => host.Service<CharacterService>().GrantAsync(session, [(Timed, 1)]));
        long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var copies = session.Items.Where(i => i.Entry == Timed).ToList();
        Assert.Equal(2, copies.Count);
        Assert.All(copies, i => Assert.InRange(i.ExpireTm, before + 30 * 60, after + 30 * 60));
        Assert.All(host.Service<ItemStore>().Load(session.Role!.Id).Where(i => i.Entry == Timed),
            i => Assert.InRange(i.ExpireTm, before + 30 * 60, after + 30 * 60));
    }

    [Fact]
    public async Task AnExpiredTimedItemCannotBeUsedAndLapsesIntoItsProduct()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        var session = Session(host);
        await OnLoop(host, () => host.Service<CharacterService>().GrantAsync(session, [(Timed, 2)]));
        var copies = session.Items.Where(i => i.Entry == Timed).ToList();
        var expired = copies[0];
        long fresh = copies[1].Guid;
        await OnLoop(host, () =>
        {
            expired.ExpireTm = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
            host.Service<ItemStore>().Update(expired);
            return Task.CompletedTask;
        });

        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = expired.Guid, ItemCount = 1 });
        Assert.Equal(ErrorCodes.ItemExpired, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.Contains(session.Items, i => i.Guid == expired.Guid);

        long coins = session.Progress!.Currency.GetValueOrDefault(LapseCurrency);
        await alice.SendGameAsync(EquipmentService.ItemDecompose,
            new ItemDecomposeReq { IsLapseDecompose = true, DecomposeItem = { [fresh] = 1 } });
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(alice, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
        Assert.Contains(session.Items, i => i.Guid == fresh);

        await alice.SendGameAsync(EquipmentService.ItemDecompose,
            new ItemDecomposeReq { IsLapseDecompose = true, DecomposeItem = { [expired.Guid] = 1 } });
        Assert.Equal(0, (await Next(alice, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
        Assert.DoesNotContain(session.Items, i => i.Guid == expired.Guid);
        Assert.Equal(coins + 7, session.Progress.Currency.GetValueOrDefault(LapseCurrency));
    }

    [Fact]
    public async Task ACopyReceivedBeforeExpiryWasStoredGetsItsFullDurationAtLogin()
    {
        await using var host = await SyntheticHost.StartAsync();
        long guid;
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            var session = Session(host);
            await OnLoop(host, () => host.Service<CharacterService>().GrantAsync(session, [(Timed, 1)]));
            var copy = session.Items.Single(i => i.Entry == Timed);
            guid = copy.Guid;
            await OnLoop(host, () =>
            {
                copy.ExpireTm = 0;
                host.Service<ItemStore>().Update(copy);
                return Task.CompletedTask;
            });
        }
        await host.RestartAsync();
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var again = await host.InWorldAsync("Alice", create: false);

        var bag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.Values.Any(p => p.All?.Items.ContainsKey(guid) == true));
        long sent = bag.PackInfo.Values.First(p => p.All?.Items.ContainsKey(guid) == true).All.Items[guid].ExpireTm;
        Assert.InRange(sent, before + 30 * 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30 * 60);
        Assert.Equal(sent, host.Service<ItemStore>().Load(Session(host).Role!.Id).Single(i => i.Guid == guid).ExpireTm);
    }

    private static GameSession Session(SyntheticHost host) =>
        host.Service<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");

    private static Task OnLoop(SyntheticHost host, Func<Task> action) =>
        host.Service<GameLoop>().InvokeAsync(action);

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }
}
