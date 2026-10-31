using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 2005 ITEM_EXCHANGE: the Currency Redemption window (Zeny to Minted Coin, function 7006). An ItemExchangeConfig row is
/// keyed by the item that is spent (_iID) and names the item that is received (_iTargetID); one spent unit gives _iRate
/// received units, the 1:100 the window shows. UI_Sys_CoinExchangeWidgetCtrl._itemConvert sends itemId = m_iCostItemId and
/// count = m_iExchangeNum, the amount spent; _refreshRightItem shows that amount times _iRate as obtained.
/// </summary>
public sealed partial class CurrencyExchangeService(GameData data, CharacterService character, FunctionService functions,
    ILogger<CurrencyExchangeService> log)
{
    public const uint Exchange = 2005;

    public async Task ExchangeAsync(GameSession s, ItemExchangeReq req)
    {
        using var guard = await CharacterService.Lock(s);
        var row = ItemExchangeConfig.Get(data, req.ItemId);
        int result = row is not { } r || ItemConfig.Get(data, r.TargetId) == null ? ErrorCodes.Parameter
            : !functions.IsOpen(s, r.Function) ? ErrorCodes.FunctionLocked
            // _getMaxExchangeNum caps m_iExchangeNum, the amount spent, at _iSingleLimit.
            : req.Count < 1 || req.Count > r.SingleLimit || r.Rate < 1 || req.Count * r.Rate > int.MaxValue ? ErrorCodes.Parameter
            : 0;
        if (result == 0) result = await PayAsync(s, row!.Value, req.Count);
        LogRedeem(log, s.Role!.Name, req.Count, req.ItemId, result);
        await s.SendGameAsync(Exchange, new ItemExchangeResp { Result = result });
    }

    private async Task<int> PayAsync(GameSession s, ItemExchangeConfig row, long count)
    {
        int item = (int)row.Id, target = (int)row.TargetId;
        (int, long)[] cost = [(item, count)];
        (int, int)[] goods = [(target, (int)(count * row.Rate))];
        var paid = await character.ExchangeLockedAsync(s, cost, goods, popup: false,
            persist: tx => tx.WithEconomy(character.FamilyEconomy(s, "cd13a/character-currency-exchange", cost, goods,
                new { Item = item, Count = count, Target = target, row.Rate })),
            asyncCommit: true);
        return paid == null ? character.ShortageOf(s, cost) : 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} redeems {Count} of item {Item}: {Result}")]
    private static partial void LogRedeem(ILogger logger, string? name, long count, long item, int result);
}
