using System;
using HappyEngine.Base;
using Ro3Emu.Shared;
using UnityEngine;

namespace Ro3Emu.ClientHooks;

internal static class CharacterAppearance
{
    private static Obf_R.Obf_ff _global;
    private static bool _itemsReady, _reported;
    private static float _nextReady;
    private static int _shopPrice = -1;
    internal static object MaskProperty { get; private set; }

    internal static void Install(Obf_R.Obf_ff global)
    {
        _global = global; _itemsReady = false; _shopPrice = -1; _reported = false; _nextReady = 0;
        MaskProperty = global.Obf_LuA<object>("enum_unitAttrClientMap.IsMaskFace");
    }

    internal static void ApplyItems(Obf_R.Obf_ff dbm, Obf_R.Obf_ff texts)
    {
        if (texts == null) return;
        var rows = PassChallenges.Values(dbm, "m_kCFG_ItemConfig");
        // The database can replace its lazy config tables between login and entering a realm.
        // Check the current table, not just the previous injection's process-wide flag.
        if (_itemsReady && First(_global.Obf_LuA<Obf_R.Obf_Ef>("rawget")?.Obf_HuA(rows, (long)CharacterAppearanceRules.HairItemBase)) != null) return;
        _itemsReady = false;
        _shopPrice = -1;
        if (!ApplyMerchant(dbm, texts)) return;
        var template = rows?.Obf_ruA<long, Obf_R.Obf_ff>(CharacterAppearanceRules.ItemTemplate);
        if (template == null) return;
        for (int i = 0; i < CharacterAppearanceRules.PaletteCount * 2; i++)
        {
            int item = CharacterAppearanceRules.ItemForCommodity(CharacterAppearanceRules.CommodityBase + i);
            var row = PassChallenges.Copy(template);
            foreach (var field in CharacterAppearanceRules.ItemFields(item)) Set(row, field.Key, field.Value);
            PassChallenges.RawSet(rows, (long)item, row);
            PassChallenges.Text(texts, CharacterAppearanceRules.NameText(item), CharacterAppearanceRules.ItemName(item));
            PassChallenges.Text(texts, CharacterAppearanceRules.DescriptionText(item), CharacterAppearanceRules.Description(item));
        }
        dbm.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_ItemConfig")?.Obf_luA<string, object>("m_kAllIDList", null);
        _itemsReady = true;
        Debug.Log("[Ro3Emu.Appearance] dye items registered: " + Call(dbm.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_ItemConfig"), "GetName", CharacterAppearanceRules.HairItemBase));
    }

    private static bool ApplyMerchant(Obf_R.Obf_ff dbm, Obf_R.Obf_ff texts)
    {
        foreach (string table in new[] { "NPCConfig", "NPCInteractConfig", "ShopConfig" })
        {
            string config = "m_kCFG_" + table;
            var rows = PassChallenges.Values(dbm, config);
            var template = rows?.Obf_ruA<long, Obf_R.Obf_ff>(CharacterAppearanceRules.MerchantTemplate(table));
            if (template == null) return false;
            var row = PassChallenges.Copy(template);
            foreach (var field in CharacterAppearanceRules.MerchantFields(table)) Set(row, field.Key, field.Value);
            PassChallenges.RawSet(rows, (long)CharacterAppearanceRules.MerchantId, row);
            var wrapper = dbm.Obf_RuA<Obf_R.Obf_ff>(config);
            wrapper.Obf_luA<string, object>("m_kAllIDList", null);
            wrapper.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas")?.Obf_HuA(wrapper);
        }
        PassChallenges.Text(texts, CharacterAppearanceRules.MerchantNameText, CharacterAppearanceRules.MerchantName);
        PassChallenges.Text(texts, CharacterAppearanceRules.MerchantButtonText, "Buy dyes");
        return true;
    }

    internal static void Tick()
    {
        if (_global == null || !_itemsReady || Time.unscaledTime < _nextReady) return;
        _nextReady = Time.unscaledTime + 1;
        try
        {
            var unit = First(_global.Obf_LuA<Obf_R.Obf_Ef>("LC_UnitManager.GetLocalPlayer")?.Obf_HuA()) as Obf_R.Obf_ff;
            if (unit == null) return;
            Attach(unit);
            var price = Custom(unit, CharacterAppearanceRules.PriceCustomData);
            if (price == null)
            {
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(CharacterAppearanceRules.ClientReadyMessage, new byte[] { 1 });
                return;
            }
            int amount = Convert.ToInt32(price);
            if (amount == _shopPrice) return;
            var db = _global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
            var dbm = First(db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db)) as Obf_R.Obf_ff;
            if (ApplyShop(dbm, amount)) _shopPrice = amount;
        }
        catch (Exception e) { Report(e); }
    }

    private static bool ApplyShop(Obf_R.Obf_ff dbm, int price)
    {
        if (dbm == null) return false;
        var rows = PassChallenges.Values(dbm, "m_kCFG_ShopCommodityConfig");
        var template = rows?.Obf_ruA<long, Obf_R.Obf_ff>(CharacterAppearanceRules.ShopTemplate);
        if (template == null) return false;
        for (int i = 0; i < CharacterAppearanceRules.PaletteCount * 2; i++)
        {
            int id = CharacterAppearanceRules.CommodityBase + i;
            if (price <= 0) { PassChallenges.RawSet(rows, (long)id, null); continue; }
            int item = CharacterAppearanceRules.ItemForCommodity(id);
            var row = PassChallenges.Copy(template);
            foreach (var field in CharacterAppearanceRules.ShopFields(item, price)) Set(row, field.Key, field.Value);
            PassChallenges.RawSet(rows, (long)id, row);
        }
        var wrapper = dbm.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_ShopCommodityConfig");
        wrapper.Obf_luA<string, object>("m_kAllIDList", null);
        wrapper.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas")?.Obf_HuA(wrapper);
        Debug.Log("[Ro3Emu.Appearance] dye shop " + (price > 0 ? "enabled" : "disabled") + " by server");
        return true;
    }

    internal static void Attach(Obf_R.Obf_ff unit)
    {
        try
        {
            if (_global == null || unit == null || Convert.ToInt32(unit.Obf_RuA<object>("m_iUnitTypeEnum")) != 3) return;
            var obj = unit.Obf_RuA<GameObject>("gameObject");
            if (obj == null) return;
            var component = obj.GetComponent<CharacterAppearanceView>() ?? obj.AddComponent<CharacterAppearanceView>();
            component.Bind(unit);
        }
        catch (Exception e) { Report(e); }
    }

    internal static object Call(Obf_R.Obf_ff self, string method, params object[] args)
    {
        var parameters = new object[args.Length + 1];
        parameters[0] = self;
        Array.Copy(args, 0, parameters, 1, args.Length);
        return First(self?.Obf_RuA<Obf_R.Obf_Ef>(method)?.Obf_HuA(parameters));
    }
    internal static object Custom(Obf_R.Obf_ff self, long id) => Call(self, "GetCustomDataValue", id);
    internal static void ProbeUseBagItem(long guid)
    {
        var lookup = _global.Obf_LuA<Obf_R.Obf_Ef>("LC_ItemHelper.GetItemBaseInfo");
        var item = First(lookup?.Obf_HuA(guid))
            ?? First(lookup?.Obf_HuA(guid.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            ?? First(lookup?.Obf_HuA((double)guid));
        if (item == null) throw new ArgumentException("The test item is not in this character's bag.");
        _global.Obf_LuA<Obf_R.Obf_Ef>("LC_ItemHelper.ClickUseItem").Obf_HuA(item);
    }
    internal static long Number(object value) => value == null ? 0 : Convert.ToInt64(value);
    private static object First(object[] values) => values != null && values.Length > 0 ? values[0] : null;
    private static void Set(Obf_R.Obf_ff row, string key, object value)
    {
        if (value is long[] list) value = PassChallenges.List(Array.ConvertAll(list, n => (object)n));
        else if (value is string[][] actions) value = PassChallenges.List(Array.ConvertAll(actions, row => (object)PassChallenges.List(row)));
        PassChallenges.RawSet(row, key, value);
    }
    internal static void Report(Exception e)
    {
        if (!_reported) Debug.LogWarning("[Ro3Emu.Appearance] " + e.GetType().Name + ": " + e.Message);
        _reported = true;
    }
}
