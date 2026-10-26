using System.Text.Json;
using Ro3Emu.Server;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class StallPricePolicyTests
{
    // Invented rows: no client data required to verify the pricing policy in CI.
    private static readonly int[] StableRange = [8, 12];

    private static TradeConfig Item(bool top = false) => new(JsonSerializer.SerializeToElement(new
    {
        _iBasePrice = 120, _iMoney = 2, _iTradeType = top ? 1 : 2, _kStableRange = StableRange,
    }));

    [Theory]
    [InlineData(1)]
    [InlineData(113)]
    [InlineData(999)]
    [InlineData(999999999)]
    public void FlexiblePricingAcceptsUnroundedPricesOutsideOriginalBands(int price)
    {
        foreach (bool top in new[] { false, true })
        {
            Assert.True(StallPricePolicy.IsValid(Item(top), price, 2, 999999999, flexible: true));
            Assert.False(StallPricePolicy.IsValid(Item(top), price, 2, 999999999, flexible: false));
        }
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(1000000000, 2)]
    [InlineData(int.MaxValue, 2)]
    [InlineData(120, 1)]
    [InlineData(120, 0)]
    public void BothModesEnforcePositivePricesCurrencyAndTheLimit(int price, int currency)
    {
        foreach (bool flexible in new[] { false, true })
            Assert.False(StallPricePolicy.IsValid(Item(), price, currency, 999999999, flexible));
    }

    [Fact]
    public void OriginalPricingRemainsTheDefaultAndAdvertisesBoundedBands()
    {
        Assert.False(new ServerOptions().FlexibleStallPricing);
        Assert.Equal((8, 12), StallPricePolicy.Range(Item()));
        Assert.Equal((10, 10), StallPricePolicy.Range(Item(top: true)));
        Assert.True(StallPricePolicy.IsValid(Item(), 96, 2, 1000, flexible: false));
        Assert.True(StallPricePolicy.IsValid(Item(), 144, 2, 1000, flexible: false));
        Assert.False(StallPricePolicy.IsValid(Item(top: true), 96, 2, 1000, flexible: false));
        Assert.True(StallPricePolicy.IsValid(Item(top: true), 120, 2, 1000, flexible: false));
        Assert.True(StallPricePolicy.IsValid(Item(), 137, 2, 137, flexible: true));
        Assert.False(StallPricePolicy.IsValid(Item(), 138, 2, 137, flexible: true));
    }
}
