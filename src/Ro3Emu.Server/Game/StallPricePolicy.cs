using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

internal static class StallPricePolicy
{
    /// <summary>LC_DAL_Stall.ReceiveItemPrice: Top uses 10/10, other prices are floor(base*step/10+0.5).
    /// Selecting the stable range without hidden supply dynamics is inferred.</summary>
    internal static (int Min, int Max) Range(TradeConfig item)
    {
        var range = item.StableRange;
        if (item.TradeType == 1 || range.Length < 2) return (10, 10);
        int min = (int)Math.Clamp(range[0], 1, 1000);
        return (min, (int)Math.Clamp(range[1], min, 1000));
    }

    internal static bool IsValid(TradeConfig item, int price, int currency, long limit, bool flexible)
    {
        if (price <= 0 || price > limit || currency != item.Money) return false;
        // Opt-in Sol house rule; eligibility and instance protections are enforced by the caller.
        if (flexible) return true;
        var (min, max) = Range(item);
        return Enumerable.Range(min, max - min + 1).Any(step => (item.BasePrice * step + 5) / 10 == price);
    }
}
