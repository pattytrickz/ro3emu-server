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
                PassChallenges.Text(texts, F2PRules.SupporterTokenNameText, F2PRules.SupporterTokenName);
                PassChallenges.Text(texts, F2PRules.SupporterTokenDescriptionText, F2PRules.SupporterTokenDescription);
                // The Fashion tab's currency bar: Crystals and Supporter Tokens.
                var shops = PassChallenges.Values(dbm, "m_kCFG_ShopConfig");
                if (shops != null)
                    foreach (int id in F2PRules.FashionShopRows)
                        if (shops.Obf_ruA<long, Obf_R.Obf_ff>(id) is { } shop)
                            PassChallenges.RawSet(shop, "_kCurrencyDisplay", PassChallenges.List((long)F2PRules.Crystal, (long)F2PRules.SupporterToken));
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
                    long payment = rule.Payment > 0 ? rule.Payment
                        : price != null && price.Obf_ruA<long, object>(1) is { } p ? Convert.ToInt64(p) : F2PRules.Crystal;
                    PassChallenges.RawSet(row, "_kItemPerPrice", PassChallenges.List(payment, (long)rule.Price));
                    PassChallenges.RawSet(row, "_iDiscount", 0L);
                    PassChallenges.RawSet(row, "_iPurchaseTimesLimit", (long)rule.Period);
                    PassChallenges.RawSet(row, "_iPurchaseNum", (long)rule.Count);
                    set++;
                }
                int added = Add(rows);
                var wrapper = dbm.Obf_RuA<Obf_R.Obf_ff>(Config);
                wrapper?.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas")?.Obf_HuA(wrapper);
                Debug.Log("[Ro3Emu.F2P] " + set + "/" + F2PRules.Commodities.Length + " shop prices set; Coin Bag now "
                    + Describe(rows, 13123) + ", first outfit " + Describe(rows, 5000002) + ", supporter mount " + Describe(rows, 5000008)
                    + "; " + added + "/" + F2PRules.Added.Length + " Zeny Shop rows added, Yellow Potion " + Describe(rows, 12114));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.F2P] shop prices failed: " + e.Message);
            }
        }

        /// <summary>
        /// The Zeny Shop rows the data doesn't have (F2PRules.Added, the same rows the server adds): each a copy of a Zeny
        /// Shop row with its own id, item and price, rawset into the rows so OnInitDatas files them under the shop's class.
        /// </summary>
        private static int Add(Obf_R.Obf_ff rows)
        {
            var template = rows.Obf_ruA<long, Obf_R.Obf_ff>(F2PRules.AddedTemplate);
            if (template == null) return 0;
            int added = 0;
            foreach (var a in F2PRules.Added)
            {
                var row = PassChallenges.Copy(template);
                PassChallenges.RawSet(row, "_iID", (long)a.Id);
                PassChallenges.RawSet(row, "_iItemId", (long)a.ItemId);
                PassChallenges.RawSet(row, "_iItemPerMount", 1L);
                PassChallenges.RawSet(row, "_kItemPerPrice", PassChallenges.List((long)F2PRules.Zeny, (long)a.Price));
                PassChallenges.RawSet(row, "_iDiscount", 0L);
                PassChallenges.RawSet(row, "_iPurchaseTimesLimit", 0L);
                PassChallenges.RawSet(row, "_iPurchaseNum", 0L);
                PassChallenges.RawSet(row, "_iSortId", (long)a.Sort);
                PassChallenges.RawSet(rows, (long)a.Id, row);
                added++;
            }
            return added;
        }

        /// <summary>A row's price and cap read back the way the shop reads them (for Player.log).</summary>
        private static string Describe(Obf_R.Obf_ff rows, long id)
        {
            var row = rows.Obf_ruA<long, Obf_R.Obf_ff>(id);
            var price = row?.Obf_RuA<Obf_R.Obf_ff>("_kItemPerPrice");
            if (price == null) return "?";
            return price.Obf_ruA<long, object>(2) + " of item " + price.Obf_ruA<long, object>(1) + " (cap " + row.Obf_RuA<object>("_iPurchaseNum") + ")";
        }
    }
}
