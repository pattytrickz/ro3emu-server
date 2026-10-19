using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Sonic Blow (_kScriptLogic "SonicBlow") carries three damage branches in _kDamageParam1/2: index 0 the plain hit,
/// 1 = index 0 * 1.2 ("if the target's HP is at or below {4}% [_kLogicData[2] 5000 = 50%], increases the multiplier
/// by {5}% [20]"), 2 = index 0 * 1.25: Supersonic Blow (15103), "allows Sonic Blow to trigger its damage increase when
/// the target's Life is at or below {1}% [62..70] and changes the increase to {2}% [25]". The server always used
/// index 0. The job talents' Sonic Blow modifiers already target each index (SkillParameterRules).
/// </summary>
public sealed partial class CombatService
{
    private const int SupersonicBlowGroup = 15103;

    internal int SonicBlowBranch(GameSession s, MonsterUnit target, JsonElement row)
    {
        long max = target.Template.MaxHp;
        if (max <= 0 || s.Progress == null) return 0;
        double percent = target.Hp * 100d / max; // before this hit, like the tooltip's "target's HP"
        int level = skills.Level(s.Progress, SupersonicBlowGroup);
        if (level > 0 && data.Row("SkillConfig", skills.RealId(SupersonicBlowGroup, level)) is { } super
            && super.TryGetProperty("_kDescData", out var desc) && desc.ValueKind == JsonValueKind.Array && desc.GetArrayLength() > 0
            && GameData.Number(desc[0]) is { } threshold && percent <= threshold)
            return 2;
        var logic = Numbers(row, "_kLogicData");
        double plain = logic.Length > 2 && logic[2] > 0 ? logic[2] / 100d : 50;
        return percent <= plain ? 1 : 0;
    }
}
