using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Thief Double Attack (skill group 15002, a passive). Its Katar CRIT is SpecialEffectConfig op 16, applied with the
/// other weapon-conditional passive attributes (PassiveAttributes). Its Dagger proc is in no effect table, only in the
/// description the client prints from SkillConfig._kDescData: "When a Dagger is equipped, each Basic Attack has a {1}%
/// Chance to deal {2} Extra hits of Neutral melee Physical damage equal to PATK * {3}%".
/// (inferred) The extra hits join the Basic Attack's damage number like the totem extra-hit passives, each
/// floor(PATK * {3} / 100), with no defense or element step: the text gives the amount, not a formula.
/// </summary>
public sealed partial class CombatService
{
    private const int DoubleAttackGroup = 15002, DaggerType = 1;

    /// <summary>Rolls a percent chance; tests make it deterministic.</summary>
    internal Func<int, bool> DoubleAttackRoll { get; set; } = chance => Random.Shared.Next(100) < chance;

    private long DoubleAttackDamage(GameSession s, MonsterUnit target, JsonElement row, long amount)
    {
        if (s.Progress == null || WeaponType(s, 1) != DaggerType
            || !skills.IsNormalAttack(s.Progress.Career, (int)GameData.Int(row, "_iID"))) return 0;
        int level = skills.Level(s.Progress, DoubleAttackGroup);
        if (level <= 0 || data.Row("SkillConfig", skills.RealId(DoubleAttackGroup, level)) is not { } skill
            || !skill.TryGetProperty("_kDescData", out var desc) || desc.ValueKind != JsonValueKind.Array
            || desc.GetArrayLength() < 3) return 0;
        double chance = GameData.Number(desc[0]) ?? 0, hits = GameData.Number(desc[1]) ?? 0, percent = GameData.Number(desc[2]) ?? 0;
        if (chance <= 0 || hits <= 0 || percent <= 0 || !DoubleAttackRoll((int)chance)) return 0;
        return (long)hits * (long)Math.Floor(character.Stats(s)[103] * percent / 100d);
    }

    /// <summary>
    /// SpecialEffectConfig op 16 on a learned passive's _iSpecialEffect: [16, main-hand ItemConfig._iSubType, attribute,
    /// value], the attribute while that weapon type is held (Double Attack: Katar 12, CRIT 111, +100..+500 = 1..5 shown).
    /// </summary>
    private IEnumerable<(long Id, double Value)> WeaponPassiveAttributes(GameSession session, JsonElement row)
    {
        if (!row.TryGetProperty("_iSpecialEffect", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        int weapon = WeaponType(session, 1);
        foreach (var id in list.EnumerateArray())
        {
            if (data.Row("SpecialEffectConfig", (long)(GameData.Number(id) ?? 0)) is not { } effect
                || !effect.TryGetProperty("_kSpecialEffect", out var ops) || ops.ValueKind != JsonValueKind.Array) continue;
            foreach (var op in ops.EnumerateArray())
                if (op.ValueKind == JsonValueKind.Array && op.GetArrayLength() == 4 && GameData.Number(op[0]) == 16
                    && weapon != 0 && GameData.Number(op[1]) == weapon
                    && GameData.Number(op[2]) is { } attr && GameData.Number(op[3]) is { } value)
                    yield return ((long)attr, value);
        }
    }
}
