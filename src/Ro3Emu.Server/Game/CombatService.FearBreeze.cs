using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const long FearBreezeBuff = 13301; // BuffConfig._iBuffID of Fear Breeze (SkillConfig 13303 _kBuff1 13301NN)
    private const long FearBreezeArrows = 13304; // SkillConfig "Fear Breeze", _kScriptLogic FearBreeze_: the arrow spike
    private const long BowTag = 702; // SkillTagConfig 702 "Bow"

    /// <summary>
    /// Fear Breeze (13303): "While the effect lasts, when a targeted bow skill hits, the target has a ${1}% Chance to be
    /// struck by piercing arrows, taking ${2} hits of Neutral ranged Physical damage totaling Physical ATK*${3}%." The
    /// buff's own _kDescData holds ${1} ${2} ${3}. The arrows are the 13304 row of the buff's level: its _kDamageParam1
    /// is ${3} (4200 to 5000) and its _kShowDmg is ${2} (1 to 3), so one extra cast of it is the whole spike (inferred
    /// from the matching numbers). A targeted bow skill is a lock release with the Bow tag: Arrow Attack, Double Strafe,
    /// Charge Arrow, Sharp Shooting and Focused Arrow Strike, not Arrow Shower or Arrow Storm (inferred from the
    /// description's wording).
    /// </summary>
    private async Task FearBreezeAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        var cast = new SkillConfig(action.Row);
        if (cast.Release != SkillRelease.Lock || !cast.Skilltag.Contains(BowTag)) return;
        long now = Now;
        if (s.Buffs.Select(b => b.ExpireMs > now ? BuffConfig.Get(_data, b.ConfigId) : null)
                .FirstOrDefault(b => b?.BuffId == FearBreezeBuff) is not { } buff
            || buff.DescData is not [var chanceText, ..]
            || !double.TryParse(chanceText, NumberStyles.Float, CultureInfo.InvariantCulture, out double chance)
            || SkillConfig.Get(_data, FearBreezeArrows * 100 + buff.Level) is not { } arrows) return;
        foreach (var target in processed.Where(m => m.Hp > 0 && hit.Dsts.Any(d => d.DstID == m.Uid && d.Damage > 0)))
            if (Rolls.Proc(100) < chance) await CastExtraAsync(s, arrows.Row, [target]);
    }
}
