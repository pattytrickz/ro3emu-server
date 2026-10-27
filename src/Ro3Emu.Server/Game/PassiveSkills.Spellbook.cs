using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Reading Spellbook (12319): "While active, casting [Comet], [Tetra Vortex], [Frost Blast], [Earth Strain], [Crimson
/// Rock], [Thunderbolt] ... triggers the corresponding skill effect one Extra time and consumes the corresponding Floors of
/// [Book Pages]. The effect cannot be triggered without enough [Book Pages]." Each of those skills says how many: "While in
/// the [Reading] state, consumes ${5} stacks of [Book Page] to cast this skill 1 Extra time" (Comet 5, Jack Frost 3), and
/// its _kSkillHighParam holds the same condition, "0:1231001:1&amp;2:240001:5": a BuffConfig row with its piles (type 0,
/// Reading 1231001) and an EnergyConfig meter with its amount (type 2, Book Pages 240001). The extra cast is the copy
/// the skill auto-learns at its own level (_kAutoLearnSkill: Comet 1231505 learns Comet (Copy) 1232505) whose group its
/// Spellbook buff names (_kBuff1 1231501 "Spellbook - Comet", _kLogicData [12325]), in the caster's mode, on the
/// monsters the cast hit; the pages go only when a hit monster still lives (inferred).
/// </summary>
public sealed partial class PassiveSkills
{
    private const int HighParamBuff = 0, HighParamEnergy = 2; // _kSkillHighParam term types

    private async Task SpellbookAsync(GameSession s, SkillConfig cast, IReadOnlyList<MonsterUnit> processed)
    {
        var living = processed.Where(m => m.Hp > 0).ToList();
        if (living.Count == 0 || SpellbookCopy(cast) is not { } copy) return;
        foreach (var terms in HighParamConditions(cast))
        {
            if (!terms.Any(t => t.Type == HighParamEnergy) || !terms.All(t => HighParamHolds(s, t))) continue;
            foreach (var (_, energy, amount) in terms.Where(t => t.Type == HighParamEnergy))
                if (!await _buffs.SpendEnergyAsync(s, (int)energy, amount)) return;
            if ((_combat.EffectiveSkillRow(s, (int)copy.Id) ?? copy.Row) is { } row) await _combat.CastExtraAsync(s, row, living);
            return;
        }
    }

    /// <summary>The auto-learned row whose group one of the cast's _kBuff1 buffs names in its _kLogicData.</summary>
    private SkillConfig? SpellbookCopy(SkillConfig cast)
    {
        var named = cast.Buff1.Where(b => b.Length > 0 && b[0] > 0).Select(b => BuffConfig.Get(_data, b[0])?.LogicData)
            .Where(logic => logic is { Length: > 0 }).Select(logic => logic![0]).ToHashSet();
        foreach (long id in cast.AutoLearnSkill)
            if (SkillConfig.Get(_data, id) is { } learned && named.Contains(learned.SkillId)) return learned;
        return null;
    }

    /// <summary>_kSkillHighParam entries ["type:id:count&amp;type:id:count", key] made only of buff and energy terms;
    /// the other form ["0", "id:count", key] (Elemental Attunement's mode) is not a spellbook condition.</summary>
    private static IEnumerable<(int Type, long Id, long Count)[]> HighParamConditions(SkillConfig cast)
    {
        if (cast.SkillHighParam is not { ValueKind: JsonValueKind.Array } entries) yield break;
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() == 0
                || entry[0].ValueKind != JsonValueKind.String) continue;
            var terms = new List<(int Type, long Id, long Count)>();
            foreach (string term in entry[0].GetString()!.Split('&'))
            {
                var parts = term.Split(':');
                if (parts.Length != 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out int type)
                    || type is not (HighParamBuff or HighParamEnergy)
                    || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out long id)
                    || !long.TryParse(parts[2], CultureInfo.InvariantCulture, out long count)) { terms.Clear(); break; }
                terms.Add((type, id, count));
            }
            if (terms.Count > 0) yield return [.. terms];
        }
    }

    private static bool HighParamHolds(GameSession s, (int Type, long Id, long Count) term) => term.Type == HighParamEnergy
        ? s.Energy.TryGetValue((int)term.Id, out var meter) && meter.Value >= term.Count
        : s.Buffs.Any(b => b.ConfigId == term.Id && b.ExpireMs > MonsterService.NowMs && b.PileCount >= term.Count);
}
