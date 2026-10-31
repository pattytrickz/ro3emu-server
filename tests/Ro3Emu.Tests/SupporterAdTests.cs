using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The Supporter login poster (ServerOptions.Supporter.Ad): the answer to the client's POPUPIMAGE request.</summary>
public sealed class SupporterAdTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private const string Image = "https://patch.example/ads/supporter.png";

    private static Task<SyntheticHost> Start(bool enabled, int minLevel = 1) => SyntheticHost.StartAsync(o =>
    {
        o.Supporter.Ad = new SupporterAdOptions { Enabled = enabled, ImageUrl = Image, MinLevel = minLevel };
        o.StatusExcludedAccounts = ["aibot*"];
    });

    private static async Task<PopupImageRes> Ask(BotClient bot)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(SupporterAd.PopupImage));
        await bot.SendGameAsync(SupporterAd.PopupImage, new PopupImageReq { Language = "en", Channel = "pc" });
        return PopupImageRes.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
    }

    [Fact]
    public async Task APlayerGetsThePosterLinkingToTheSupporterPage()
    {
        await using var host = await Start(enabled: true);
        await using var alice = await host.InWorldAsync("Alice");
        var image = Assert.Single((await Ask(alice)).Images).ImageInfo;
        Assert.Equal(Image, image.ImageUrl);
        Assert.Equal("https://solro3.com/supporter", image.ExJump);
    }

    [Fact]
    public async Task NoPosterForSupportersAiPlayersNewCharactersOrWhenOff()
    {
        await using (var off = await Start(enabled: false))
        {
            await using var alice = await off.InWorldAsync("Alice");
            Assert.Empty((await Ask(alice)).Images);
        }
        await using (var host = await Start(enabled: true))
        {
            host.Service<AccessList>().Run("supporter alice");
            await using var alice = await host.InWorldAsync("Alice");
            Assert.Empty((await Ask(alice)).Images);
            await using var bot = await host.InWorldAsync("Aibot01");
            Assert.Empty((await Ask(bot)).Images);
        }
        await using (var high = await Start(enabled: true, minLevel: 50))
        {
            await using var bob = await high.InWorldAsync("Bob");
            Assert.Empty((await Ask(bob)).Images);
        }
    }
}
