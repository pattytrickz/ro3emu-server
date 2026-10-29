using System;
using HappyEngine.Base;
using Ro3Emu.Shared;
using UnityEngine;

namespace Ro3Emu.ClientHooks;

/// <summary>Sol RO3 identity: a supporter-only portrait frame and server-labelled overhead names (inferred policy).</summary>
internal static class PlayerIdentity
{
    private delegate void SetCustom(Obf_R.Obf_ff self, object id, object value);
    private static readonly LuaPatches.SelfMethod RefreshWrapper = Refresh;
    private static readonly SetCustom SetCustomWrapper = SetCustomData;
    private static Obf_R.Obf_ff _global;
    private static Obf_R.Obf_Ef _refresh, _setCustom;
    private static object _nameProperty, _colorProperty, _maskProperty;
    private static bool _reported;
    private static int _check;

    internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
    {
        _global = global;
        CharacterAppearance.Install(global);
        _refresh = _setCustom = null;
        _reported = false;
        try
        {
            // RefreshAll receives the HUD component, which has GetUnit/GetProperty/SetProperty. Its existing
            // ShowName rules still control visibility; changing these two properties never reveals hidden players.
            var element = LuaPatches.Module(manager, global, "lc_hudelement_nametitle");
            var unitBase = LuaPatches.Module(manager, global, "lc_unitbase");
            _refresh = element?.Obf_RuA<Obf_R.Obf_Ef>("RefreshAll");
            _setCustom = unitBase?.Obf_RuA<Obf_R.Obf_Ef>("SetCustomData");
            if (_refresh == null || _setCustom == null) throw new InvalidOperationException("name/title HUD or unit module missing");
            _nameProperty = global.Obf_LuA<object>("enum_HudProperty.Name");
            _colorProperty = global.Obf_LuA<object>("enum_HudProperty.NameColor");
            _maskProperty = global.Obf_LuA<object>("enum_unitAttrClientMap.IsMaskFace");
            element.Obf_luA<string, LuaPatches.SelfMethod>("RefreshAll", RefreshWrapper);
            unitBase.Obf_luA<string, SetCustom>("SetCustomData", SetCustomWrapper);
            Debug.Log("[Ro3Emu.Identity] server-authorized supporter/GM nameplates installed");
        }
        catch (Exception e) { Report(e); }
    }

    internal static void ApplyCosmetics(Obf_R.Obf_ff dbm, Obf_R.Obf_ff texts)
    {
        CharacterAppearance.ApplyItems(dbm, texts);
        var rows = PassChallenges.Values(dbm, "m_kCFG_SocialOutfitConfig");
        if (rows == null || texts == null) return;
        PassChallenges.Text(texts, SupporterIdentityRules.FrameNameText, SupporterIdentityRules.FrameName);
        PassChallenges.Text(texts, SupporterIdentityRules.FrameDescriptionText, SupporterIdentityRules.FrameDescription);
        if (rows.Obf_ruA<long, Obf_R.Obf_ff>(SupporterIdentityRules.Frame) != null) return;
        var row = PassChallenges.Row();
        foreach (var field in SupporterIdentityRules.FrameRow())
        {
            object value = field.Value;
            if (value is long[] list) value = PassChallenges.List(Array.ConvertAll(list, n => (object)n));
            PassChallenges.RawSet(row, field.Key, value);
        }
        PassChallenges.RawSet(rows, (long)SupporterIdentityRules.Frame, row);
        var wrapper = dbm.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_SocialOutfitConfig");
        // Lua_CFG_Base.GetAllIDList and Lua_CFG_SocialOutfitConfig._initConfigByType cache their lists.
        wrapper.Obf_luA<string, object>("m_kAllIDList", null);
        wrapper.Obf_luA<string, object>("kConfigDic", null);
        Debug.Log("[Ro3Emu.Identity] Sol Supporter crown frame registered");
    }

    internal static void Tick()
    {
        if (_global == null || ++_check % 30 != 0) return;
        try
        {
            var db = _global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
            var result = db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db);
            if (result != null && result.Length > 0 && result[0] is Obf_R.Obf_ff dbm)
            {
                ApplyCosmetics(dbm, _global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV"));
                var rows = PassChallenges.Values(dbm, "m_kCFG_SocialOutfitConfig");
                var role = First(_global.Obf_LuA<Obf_R.Obf_Ef>("LC_UnitManager.GetLocalPlayerID")?.Obf_HuA());
                if (role != null && Convert.ToInt64(role) > 0 && rows?.Obf_ruA<long, Obf_R.Obf_ff>(SupporterIdentityRules.Frame) != null)
                    SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(SupporterIdentityRules.ClientReadyMessage, new byte[] { 1 });
            }
        }
        catch (Exception e) { Report(e); }
    }

    private static void SetCustomData(Obf_R.Obf_ff self, object id, object value)
    {
        _setCustom?.Obf_HuA(self, id, value);
        CharacterAppearance.Attach(self);
        try
        {
            // LC_UnitBase.SetCustomData stores values without dirtying the HUD for an unknown custom id.
            // RefreshHud marks the existing HUD dirty; initial data with no HUD is picked up when it is built.
            if (id != null && Convert.ToInt64(id) == SupporterIdentityRules.BadgeCustomData)
                self.Obf_RuA<Obf_R.Obf_Ef>("RefreshHud")?.Obf_HuA(self);
        }
        catch (Exception e) { Report(e); }
    }

    private static void Refresh(Obf_R.Obf_ff self)
    {
        _refresh?.Obf_HuA(self);
        try
        {
            var unit = First(self.Obf_RuA<Obf_R.Obf_Ef>("GetUnit")?.Obf_HuA(self)) as Obf_R.Obf_ff;
            if (unit == null || Convert.ToInt32(unit.Obf_RuA<object>("m_iUnitTypeEnum")) != 3) return;
            CharacterAppearance.Attach(unit);
            var mask = First(unit.Obf_RuA<Obf_R.Obf_Ef>("GetHeadInfoDataRaw")?.Obf_HuA(unit, _maskProperty));
            if (mask != null && !false.Equals(mask)) return; // Lua truthiness: masked identities keep the original presentation
            var raw = First(unit.Obf_RuA<Obf_R.Obf_Ef>("GetCustomDataValue")?.Obf_HuA(unit, SupporterIdentityRules.BadgeCustomData));
            int badge = raw == null ? SupporterIdentityRules.Ordinary : Convert.ToInt32(raw);
            var name = First(self.Obf_RuA<Obf_R.Obf_Ef>("GetProperty")?.Obf_HuA(self, _nameProperty)) as string;
            if (string.IsNullOrEmpty(name)) return;
            var set = self.Obf_RuA<Obf_R.Obf_Ef>("SetProperty");
            set?.Obf_HuA(self, _nameProperty, SupporterIdentityRules.DisplayName(badge, name));
            if (badge != SupporterIdentityRules.Supporter && badge != SupporterIdentityRules.GameMaster) return;
            var color = First(self.Obf_RuA<Obf_R.Obf_Ef>("GetProperty")?.Obf_HuA(self, _colorProperty)) as Obf_R.Obf_ff;
            object alpha = color?.Obf_RuA<object>("a") ?? 1.0;
            var rgb = badge == SupporterIdentityRules.GameMaster ? new[] { 0.27, 0.86, 1.0 } : new[] { 1.0, 0.82, 0.28 };
            var highlight = First(_global.Obf_LuA<Obf_R.Obf_Ef>("Color.New")?.Obf_HuA(rgb[0], rgb[1], rgb[2], alpha));
            if (highlight != null) set?.Obf_HuA(self, _colorProperty, highlight);
        }
        catch (Exception e) { Report(e); }
    }

    private static object First(object[] values) => values != null && values.Length > 0 ? values[0] : null;

    private static void Report(Exception e)
    {
        if (!_reported) Debug.LogWarning("[Ro3Emu.Identity] " + e.GetType().Name + ": " + e.Message);
        _reported = true;
    }
}
