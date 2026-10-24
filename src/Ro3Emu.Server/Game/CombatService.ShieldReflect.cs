using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Crusader Shield Reflect (skill group 11410, buff 1140401 at every level). Its buff has no attributes or script; the
/// effect is only in the description the client prints from SkillConfig._kDescData: "When you take Melee Physical DMG
/// from an attacker within ${5} meters, deals Neutral Melee Physical DMG equal to your PATK * ${2}% + ${6} to the
/// attacker. This effect can trigger up to ${4} times per second." (Lv 1: 11% + 32, Lv 10: 20% + 50; 5 m; once a second.)
/// The reflected hit goes through the attacker's DEF like any physical hit. (inferred) It never lands the killing blow:
/// the monster keeps at least 1 HP, so kills and their rewards always come from a real hit.
/// </summary>
public sealed partial class CombatService
{
    private const int ShieldReflectBuff = 1140401, ShieldReflectGroup = 11410;
    private readonly ConditionalWeakTable<GameSession, ReflectWindow> reflectWindows = new();
    private sealed class ReflectWindow { public long Second; public int Count; }

    private async Task ShieldReflectAsync(MonsterUnit m, GameSession p, JsonElement? attack)
    {
        if (m.Dead || m.Hp <= 1 || p.Dead || p.Progress == null || p.Role is not { } role
            || attack is not { } row || GameData.Int(row, "_iDmgType") != 1
            || !p.Buffs.Any(b => b.ConfigId == ShieldReflectBuff && b.ExpireMs > Now)) return;
        int level = _skills.Level(p.Progress, ShieldReflectGroup);
        if (level <= 0 || _data.Row("SkillConfig", _skills.RealId(ShieldReflectGroup, level)) is not { } skill) return;
        var desc = Numbers(skill, "_kDescData"); // [.., ratio %, seconds, per second, metres, flat]
        if (desc.Length < 6 || MonsterService.Distance(m, p) > desc[4] + 0.5f) return;
        var window = reflectWindows.GetOrCreateValue(p);
        long second = Now / 1000;
        if (window.Second != second) { window.Second = second; window.Count = 0; }
        if (window.Count >= Math.Max(1, desc[3])) return;
        window.Count++;

        double def = m.Template.Def;
        double amount = (_character.Stats(p)[103] * desc[1] / 100 + desc[5])
            * (1 - def / (def + 20.0 * Math.Max(1, m.Template.Level)))
            * Math.Clamp(1 + ElementFactor(1, m.Template.Element) / 10000.0, 0.1, 10);
        long damage = Math.Min(m.Hp - 1, Math.Max(1, (long)Math.Round(amount)));
        if (damage <= 0) return;
        long before = m.Hp;
        m.Hp -= damage;
        try { await SettleMonsterHitAsync(p, m, before, damage); }
        catch (OperationCanceledException) { return; } // the player left or changed scene meanwhile
        var hit = new SkillHitInfo
        {
            SrcID = role.Id, SkillId = (int)GameData.Int(skill, "_iID"), ShowPoint = 1000002, SessionId = ++_sessionIds, SrcElement = 1,
            Dsts = { new DamageInfo { DstID = m.Uid, Damage = damage, HitResult = DamageHitResult.Normal, DstElement = m.Template.Element } },
        };
        foreach (var viewer in m.Viewers.Append(p).Distinct()) await viewer.SendGameAsync(SkillHit, hit);
        await _world.ToUnitViewersAsync(m, AoiAttrChange, MonsterService.Hp(m));
    }
}
