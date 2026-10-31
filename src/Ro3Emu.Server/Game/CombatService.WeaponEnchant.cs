using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const long BasicAttacksBecome = 10220300166; // Localization: "Fire damage +@{1}%. Basic Attacks become Fire."
    private const int FireElement = 4; // SkillConfig._iEleType: 2 wind, 3 earth, 4 fire, 5 water

    /// <summary>
    /// A cast's element for the caster: a Basic Attack takes the element a live buff turns it to, any other cast the
    /// arrow's (<see cref="AttackElement(GameSession)"/>).
    /// </summary>
    internal int AttackElement(GameSession s, JsonElement row) =>
        s.Progress != null && _skills.IsNormalAttack(s.Progress.Career, (int)GameData.Int(row, "_iID"))
            && BasicAttackEnchant(s) is { } enchant ? enchant : AttackElement(s);

    /// <summary>
    /// Magnum Break's buff (1100201..10, and the card's 2092101): "Basic Attacks become Fire". The element is
    /// _kLogicData [11000, 4, 11400] 's middle number (4 fire, the _iEleType numbering); the card's buff has no
    /// _kLogicData and takes the text's Fire (inferred).
    /// </summary>
    private int? BasicAttackEnchant(GameSession s)
    {
        long now = Now;
        foreach (var buff in s.Buffs)
            if (buff.ExpireMs > now && BuffConfig.Get(_data, buff.ConfigId) is { Description: { ValueKind: JsonValueKind.Array } description } row
                && description.GetArrayLength() > 0 && GameData.Number(description[0]) == BasicAttacksBecome)
                return row.LogicData is [_, var element, ..] ? (int)element : FireElement;
        return null;
    }

    /// <summary>
    /// The element damage attributes (AttributeConfig 301..310, "Fire Damage%", per ten thousand) raise a hit of that
    /// element: Magnum Break's buff [[304, 500]] is "Fire damage +5%". The hit's element is the cast's own
    /// (_iEleType) or else the caster's (inferred: the same pick as BaseDamage).
    /// </summary>
    internal long ElementDamage(GameSession s, JsonElement row, long damage)
    {
        int element = GameData.Int(row, "_iEleType") is var own and > 1 ? (int)own : AttackElement(s, row);
        if (element is < 1 or > AttributeId.ElementCount) return damage;
        long bonus = _character.Stats(s)[AttributeId.NoneEle + element - 1];
        return bonus == 0 ? damage : (long)Math.Floor(damage * (1 + bonus / 10000d));
    }
}
