# Currency Redemption: 2005

The Currency Redemption window (Zeny to Minted Coin, function 7006; the reverse rows need their own function, 7005)
sends `ItemExchangeReq {itemId, count}` and expects `ItemExchangeResp {result}`.

`ItemExchangeConfig` has one row per item that can be spent:

- `_iID` is the item spent and `_iTargetID` the item received;
- `_iRate` is the units received per unit spent (the window's "Redemption Rate 1:100");
- `_iSingleLimit` is the largest amount in one request;
- `_iFunction` is the function that must be open (`FunctionService.IsOpen`).

## Server (`CurrencyExchangeService`)

- The cost and the goods commit in one exchange with a `cd13a/character-currency-exchange` receipt, so a crash cannot
  pay without granting.
- Refusals: no row for `itemId`, `count` below 1 or above `_iSingleLimit`, or a product beyond an int (`Parameter`); the
  function closed (`FunctionLocked`); too little of the item (`CurrencyShort`).
- The client (`UI_Sys_CoinExchangeWidgetCtrl`): `_itemConvert` sends `itemId = m_iCostItemId` and `count = m_iExchangeNum`,
  the amount spent; `_refreshRightItem` shows `count * _iRate`; `_getMaxExchangeNum` caps the amount at `_iSingleLimit`;
  `_OnItemExchangeResult` treats `result` 0 as success.
- Until this was served 2005 answered `FeatureUnavailable`, which the client shows as "This feature is unavailable".
