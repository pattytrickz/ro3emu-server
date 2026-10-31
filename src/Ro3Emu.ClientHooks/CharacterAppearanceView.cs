using System;
using System.Collections.Generic;
using Ro3Emu.Shared;
using UnityEngine;

namespace Ro3Emu.ClientHooks;

/// <summary>Attached to the Lua unit's GameObject, so its lifetime follows that player, not a pooled body mesh.</summary>
internal sealed class CharacterAppearanceView : MonoBehaviour
{
    private Obf_R.Obf_ff _unit;
    private List<RD_Renderer> _renderers = new List<RD_Renderer>();
    private readonly HashSet<RD_Renderer> _owned = new HashSet<RD_Renderer>();
    private readonly List<RD_Renderer> _removed = new List<RD_Renderer>();
    private RD_Renderer _auraBody;
    private float _next;

    internal void Bind(Obf_R.Obf_ff unit) => _unit = unit;

    private void LateUpdate()
    {
        if (_unit == null || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + .2f;
        try { Refresh(); }
        catch (Exception e) { Clear(); CharacterAppearance.Report(e); }
    }

    private void Refresh()
    {
        var visible = CharacterAppearance.Call(_unit, "GetVisibleData") as RD_VisibleBase;
        if (visible == null || !visible.gameObject.activeInHierarchy) { Clear(); return; }
        _renderers.Clear();
        visible.GetAllRenders(ref _renderers);
        // Servers since the tiered catalogue send the full selection (5-bit palettes); older ones only the first catalogue's.
        var full = CharacterAppearance.Custom(_unit, CharacterAppearanceRules.FullSelectionCustomData);
        long state = CharacterAppearance.Number(full ?? CharacterAppearance.Custom(_unit, CharacterAppearanceRules.SelectionCustomData));
        int hair = full != null ? CharacterAppearanceRules.FullHair(state) : CharacterAppearanceRules.Hair(state);
        int armour = full != null ? CharacterAppearanceRules.FullArmour(state) : CharacterAppearanceRules.Armour(state);
        bool auraOn = full != null ? CharacterAppearanceRules.FullAura(state) : CharacterAppearanceRules.Aura(state);
        var worn = _unit.Obf_RuA<Obf_R.Obf_ff>("m_kCurUseAppearances");
        bool cosmetic = CharacterAppearance.Number(worn?.Obf_ruA<long, object>(CharacterAppearanceRules.ClothingSlot)) != 0;
        RD_Renderer body = null;
        foreach (var renderer in _renderers)
        {
            if (renderer == null || renderer.GetRendererData() == null) continue;
            var part = renderer.GetRendererData().partType;
            if (part != RoleModelPartType.Hair && part != RoleModelPartType.Body) continue;
            if (part == RoleModelPartType.Body) body = renderer;
            int palette = part == RoleModelPartType.Hair ? hair : cosmetic ? 0 : armour;
            DyePreview.Ensure(renderer, palette, GetInstanceID());
            _owned.Add(renderer);
        }
        _removed.Clear();
        foreach (var previous in _owned) if (previous == null || !_renderers.Contains(previous)) _removed.Add(previous);
        foreach (var previous in _removed)
        {
            if (previous != null) DyePreview.ResetOwned(previous, GetInstanceID());
            _owned.Remove(previous);
        }
        bool aura = auraOn
            && CharacterAppearance.Number(CharacterAppearance.Custom(_unit, SupporterIdentityRules.BadgeCustomData)) == SupporterIdentityRules.GameMaster
            && AuraVisible();
        if (_auraBody != null && (_auraBody != body || !aura)) AuraPreview.HideOwned(_auraBody, GetInstanceID());
        _auraBody = aura ? body : null;
        if (_auraBody != null) AuraPreview.Ensure(_auraBody, GetInstanceID());
    }

    private bool AuraVisible()
    {
        var mask = CharacterAppearance.Call(_unit, "GetHeadInfoDataRaw", CharacterAppearance.MaskProperty);
        return (mask == null || false.Equals(mask))
            && CharacterAppearance.Number(_unit.Obf_RuA<object>("m_iHideMask")) == 0
            && CharacterAppearance.Number(_unit.Obf_RuA<object>("m_iCurHideMask")) == 0
            && CharacterAppearance.Number(_unit.Obf_RuA<object>("m_iShowStealth")) == 0;
    }

    private void OnDisable() => Clear();
    private void OnDestroy() => Clear();
    private void Clear()
    {
        foreach (var renderer in _owned) if (renderer != null) DyePreview.ResetOwned(renderer, GetInstanceID());
        _owned.Clear();
        if (_auraBody != null) AuraPreview.HideOwned(_auraBody, GetInstanceID());
        _auraBody = null;
    }
}
