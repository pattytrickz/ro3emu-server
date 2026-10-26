using System.Globalization;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const string PoisonBlade = "PoisonBlade"; // SkillConfig._kScriptLogic of Envenomed Blade (15105)
    // MG_Skill.proto SkillAffectValueType.SkillLogicData. The talent BuffConfig 1523001 [0, 15105, 31, add, 1, 0] is
    // "[Envenomed Blade]'s ricochet effect gains an Extra ${1} bounce."
    private const int SkillLogicDataType = 31;

    /// <summary>
    /// Envenomed Blade: "Then Launches a poisoned blade at a random enemy within ${4} meters of the target, dealing Poison
    /// Ranged Physical damage equal to PATK * ${5}% + ${6}, up to ${7} times." ${5}/${6} are the first blade's own ratio
    /// and flat, so each bounce is an extra cast of the same row. _kLogicData [4] counts the first blade and the ${7} = 3
    /// bounces (inferred: both are the same at every level, and the talent adds its bounce to _kLogicData[0]). Each bounce
    /// strikes a different enemy than the target and the earlier bounces (inferred).
    /// </summary>
    private async Task RicochetAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed)
    {
        var skill = new SkillConfig(action.Row);
        if (skill.ScriptLogic != PoisonBlade || skill.LogicData is not [var blades, ..] || skill.DescData.Length < 4
            || !float.TryParse(skill.DescData[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float radius)
            || (processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? processed.FirstOrDefault()) is not { } target
            || SkillConfig.Get(_data, skill.Id) is not { } row) return;
        var tags = SkillParameterRules.CategoriesOf(_data, action.Row);
        foreach (var mod in SkillParameterRules.Active(s, _data))
            if (mod.Type == SkillLogicDataType && mod.Index == 0 && SkillParameterRules.Matches(mod, (int)skill.SkillId, tags))
                blades = SkillParameterRules.Apply(blades, mod);
        var center = target.Pos.Clone();
        var struck = new HashSet<long> { target.Uid };
        for (long bounce = 1; bounce < blades; bounce++)
        {
            var near = MonstersNear(s, center, radius).Where(m => !struck.Contains(m.Uid)).ToList();
            if (near.Count == 0) return;
            var next = near[Rolls.Proc(near.Count)];
            struck.Add(next.Uid);
            await CastExtraAsync(s, row.Row, [next]);
        }
    }
}
