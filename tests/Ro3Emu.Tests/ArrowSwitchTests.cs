using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// 31324 ArrowSwitch: the default arrow (PublicParamConfig.ArrowsDefaultItemId) is never held in the bag, so it can
/// always be chosen; any other arrow needs an owned stack.
/// </summary>
public sealed class ArrowSwitchTests
{
    private const int DefaultArrow = 1009000, FireArrow = 1009001;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TheDefaultArrowCanBeChosenWithoutABagStack()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = SyntheticData.Dir);
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            var unowned = await Switch(alice, FireArrow);
            Assert.Equal(ErrorCodes.ItemMissing, unowned.Result);

            var plain = await Switch(alice, DefaultArrow);
            Assert.Equal(0, plain.Result);
            Assert.Equal(DefaultArrow, plain.ItemId);
        }
        await using var again = await host.InWorldAsync("Alice", create: false);
        var frame = await again.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemsSync), Wait);
        Assert.Equal(DefaultArrow, DBItemsInfoIncrement.Parser.ParseFrom(frame.Body).CurrentArrowID);
    }

    private static async Task<ItemArrowSwitchResp> Switch(BotClient bot, int arrow)
    {
        await bot.SendGameAsync(CharacterService.ArrowSwitch, new ItemArrowSwitchReq { ItemId = arrow });
        var frame = await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.ArrowSwitch), Wait);
        return ItemArrowSwitchResp.Parser.ParseFrom(frame.Body);
    }
}
