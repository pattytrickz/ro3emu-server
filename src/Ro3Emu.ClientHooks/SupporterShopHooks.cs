using HappyEngine.Base;
using Ro3Emu.Shared;
using UnityEngine;

namespace Ro3Emu.ClientHooks;

/// <summary>
/// The Supporter Merchant's rows (SupporterShopRules), registered with the dye rows (CharacterAppearance.ApplyItems):
/// the NPC, its shop and button, the four booster items and their commodities, the same rows the server overlays
/// (SupporterShopOverlay). The server shows the NPC only to these hooks (client version 3) and only with Supporter:Shop on.
/// </summary>
internal static class SupporterShopHooks
{
    internal static void Apply(Obf_R.Obf_ff dbm, Obf_R.Obf_ff texts, Obf_R.Obf_ff items, Obf_R.Obf_ff itemTemplate)
    {
        foreach (string table in new[] { "NPCConfig", "NPCInteractConfig", "ShopConfig" })
        {
            string config = "m_kCFG_" + table;
            var rows = PassChallenges.Values(dbm, config);
            var template = rows?.Obf_ruA<long, Obf_R.Obf_ff>(CharacterAppearanceRules.MerchantTemplate(table));
            if (template == null) return;
            var row = CharacterAppearance.CopyConfig(template);
            foreach (var field in SupporterShopRules.MerchantFields(table)) CharacterAppearance.Set(row, field.Key, field.Value);
            PassChallenges.RawSet(rows, (long)SupporterShopRules.MerchantId, row);
            var wrapper = dbm.Obf_RuA<Obf_R.Obf_ff>(config);
            wrapper.Obf_luA<string, object>("m_kAllIDList", null);
            wrapper.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas")?.Obf_HuA(wrapper);
        }
        PassChallenges.Text(texts, SupporterShopRules.MerchantNameText, SupporterShopRules.MerchantName);
        PassChallenges.Text(texts, SupporterShopRules.MerchantButtonText, "Supporter shop");
        foreach (var p in SupporterShopRules.Products)
        {
            var row = CharacterAppearance.CopyConfig(itemTemplate);
            foreach (var field in SupporterShopRules.ItemFields(p)) CharacterAppearance.Set(row, field.Key, field.Value);
            PassChallenges.RawSet(items, (long)p.Item, row);
            PassChallenges.Text(texts, SupporterShopRules.NameText(p.Item), p.Name);
            PassChallenges.Text(texts, SupporterShopRules.DescriptionText(p.Item), p.Description);
        }
        var goods = PassChallenges.Values(dbm, "m_kCFG_ShopCommodityConfig");
        var shape = goods?.Obf_ruA<long, Obf_R.Obf_ff>(CharacterAppearanceRules.ShopTemplate);
        if (shape == null) return;
        foreach (var p in SupporterShopRules.Products)
        {
            var row = CharacterAppearance.CopyConfig(shape);
            foreach (var field in SupporterShopRules.ShopFields(p)) CharacterAppearance.Set(row, field.Key, field.Value);
            PassChallenges.RawSet(goods, (long)p.Commodity, row);
        }
        var commodities = dbm.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_ShopCommodityConfig");
        commodities.Obf_luA<string, object>("m_kAllIDList", null);
        commodities.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas")?.Obf_HuA(commodities);
        Debug.Log("[Ro3Emu.SupporterShop] Supporter Merchant registered: " + SupporterShopRules.Products.Length + " goods");
    }
}
