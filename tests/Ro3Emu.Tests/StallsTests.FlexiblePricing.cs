using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class StallsTests
{
    [ClientDataFact]
    public async Task FlexiblePricesSurviveListingEditingAndPurchaseWithExistingProtections()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await StartServer(flexiblePricing: true);
        await using var seller = await InWorld("Seller");
        await using var buyer = await InWorld("Buyer");
        await Ready(seller); await Ready(buyer);
        await Gm(seller, "!item 1000100 5");
        await Gm(buyer, "!item 2 100000");
        long guid = Items(seller).Single(i => i.Entry == 1000100).Guid;
        foreach (int price in new[] { -1, 0, 1000000000, int.MaxValue })
            Assert.Equal(ErrorCodes.TradePrice, (await Open(seller, Offer(guid, price: price))).Result);
        Assert.Equal(ErrorCodes.TradePrice, (await Open(seller, Offer(guid, price: 12347, currency: 1))).Result);
        await Edit(seller, p => p.Items.Single(i => i.Guid == guid).BindType = 1);
        Assert.Equal(ErrorCodes.TradeBound, (await Open(seller, Offer(guid, price: 12347))).Result);
        await Edit(seller, p => { p.Items.Single(i => i.Guid == guid).BindType = 0; p.Items.Single(i => i.Guid == guid).IsLocked = true; });
        Assert.Equal(ErrorCodes.ItemLocked, (await Open(seller, Offer(guid, price: 12347))).Result);
        await Edit(seller, p => p.Items.Single(i => i.Guid == guid).IsLocked = false);
        Assert.Equal(ErrorCodes.ItemMissing, (await Open(buyer, Offer(guid, price: 12347))).Result);
        Assert.Equal(5, Items(seller).Single(i => i.Guid == guid).Stack);
        Assert.Equal(1000, Money(seller, 1));
        var opened = await Open(seller, Offer(guid, price: 12347));
        Assert.Equal(0, opened.Result);
        var listing = Assert.Single(opened.Info.Itembase);
        Assert.Equal(12347, listing.Price);
        Assert.Equal(980, Money(seller, 1)); // Opening fee is unchanged.
        await AssertFlexibleEdit(seller, listing.StallsItemID);
        Assert.Equal(ErrorCodes.StallPriceChanged, await Purchase(buyer, Id(seller), listing.StallsItemID, price: 12347));
        Assert.Equal(0, await Purchase(buyer, Id(seller), listing.StallsItemID, price: 23457));
        Assert.Equal(76543, Money(buyer, 2));
        await Claim(buyer, 1028);
        Assert.Equal(1, Items(buyer).Single(i => i.Entry == 1000100).Stack);
        await Claim(seller, 1029);
        Assert.Equal(21345, Money(seller, 2)); // Original 9% tax, rounded up: 2,112.
        await AssertFlexiblePut(seller, guid);
    }

    private static async Task AssertFlexibleEdit(Ro3Emu.Bot.BotClient seller, long item)
    {
        foreach (int price in new[] { 1, 999999999, 23457 })
        {
            await seller.SendGameAsync(StallsService.Update, new PlayerStallsUpdateItemPriceReq { StallsItemID = item, Price = price });
            Assert.Equal(0, (await Next(seller, StallsService.Update, PlayerStallsUpdateItemPriceItemRsp.Parser)).Result);
        }
        foreach (int price in new[] { 0, 1000000000 })
        {
            await seller.SendGameAsync(StallsService.Update, new PlayerStallsUpdateItemPriceReq { StallsItemID = item, Price = price });
            Assert.Equal(ErrorCodes.TradePrice, (await Next(seller, StallsService.Update, PlayerStallsUpdateItemPriceItemRsp.Parser)).Result);
        }
    }

    private static async Task AssertFlexiblePut(Ro3Emu.Bot.BotClient seller, long guid)
    {
        await seller.SendGameAsync(StallsService.Put, new PlayerStallsPutItemReq
        { PutItem = new StallsPutItem { Itemid = guid, Itemnum = 1, Price = 54321, Pricetype = 2 } });
        var added = await Next(seller, StallsService.Put, PlayerStallsPutItemRsp.Parser);
        Assert.Equal(0, added.Result);
        Assert.Equal(54321, added.Baseinfo.Price);
    }
}
