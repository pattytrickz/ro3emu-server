using System;
using Ro3Emu.Shared;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The free-to-play realm's shop (server ServerOptions.F2P, rules in Ro3Emu.Shared.F2PRules): the Crystal Shop's
    /// weekly caps and the cheaper cosmetics go into the client's own ShopCommodityConfig rows, so the shop shows (and
    /// checks the balance against) the price the server charges. Rows are changed in place with rawset, like NovicePass
    /// does, then Lua_CFG_ShopCommodityConfig.OnInitDatas rebuilds its per-shop lists (ShopItemsMap holds the row
    /// tables). Injected with the pass challenge rows, so a config reload (ReLoadAllCfg) gets them again.
    /// </summary>
    internal static class ShopPrices
    {
        private const string Config = "m_kCFG_ShopCommodityConfig";

        internal static void Apply(Obf_R.Obf_ff dbm, Obf_R.Obf_ff texts)
        {
            try
            {
                PassChallenges.Text(texts, F2PRules.CrystalDescriptionText, F2PRules.CrystalDescription);
                var rows = PassChallenges.Values(dbm, Config);
                if (rows == null)
                {
                    Debug.LogWarning("[Ro3Emu.F2P] ShopCommodityConfig missing; shop prices unchanged");
                    return;
                }
                int set = 0;
                foreach (var rule in F2PRules.Commodities)
                {
                    var row = rows.Obf_ruA<long, Obf_R.Obf_ff>(rule.Id);
                    if (row == null) continue;
                    var price = row.Obf_RuA<Obf_R.Obf_ff>("_kItemPerPrice");
                    long payment = price != null && price.Obf_ruA<long, object>(1) is { } p ? Convert.ToInt64(p) : F2PRules.Crystal;
                    PassChallenges.RawSet(row, "_kItemPerPrice", PassChallenges.List(payment, (long)rule.Price));
                    PassChallenges.RawSet(row, "_iDiscount", 0L);
                    PassChallenges.RawSet(row, "_iPurchaseTimesLimit", (long)rule.Period);
                    PassChallenges.RawSet(row, "_iPurchaseNum", (long)rule.Count);
                    set++;
                }
                var wrapper = dbm.Obf_RuA<Obf_R.Obf_ff>(Config);
                wrapper?.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas")?.Obf_HuA(wrapper);
                Debug.Log("[Ro3Emu.F2P] " + set + "/" + F2PRules.Commodities.Length + " shop prices set; Coin Bag now "
                    + Describe(rows, 13123) + ", first outfit " + Describe(rows, 5000002));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.F2P] shop prices failed: " + e.Message);
            }
        }

        /// <summary>A row's price and cap read back the way the shop reads them (for Player.log).</summary>
        private static string Describe(Obf_R.Obf_ff rows, long id)
        {
            var row = rows.Obf_ruA<long, Obf_R.Obf_ff>(id);
            var price = row?.Obf_RuA<Obf_R.Obf_ff>("_kItemPerPrice");
            if (price == null) return "?";
            return price.Obf_ruA<long, object>(2) + " (cap " + row.Obf_RuA<object>("_iPurchaseNum") + ")";
        }
    }
}
