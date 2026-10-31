using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// SkillConfig._kDamageFormulaExtra AXaddBplus1 and AXaddBplus1_withlimit: a skill's damage grows with one of the caster's
/// attributes. _kDamageParamExtra holds [A, attribute, B, limit]. Warg Strike's row [30, 115, 10000, 500], with _kDescData
/// 1, 0.3 and 500, reads "every 1% ASPD grants an Extra 0.3% Skill Damage": the factor is 1 + A * attribute / B, the
/// attribute capped at the limit when the row has one. That the whole ASPD counts, not only what is above 100, is
/// (inferred): the text says "every" and the limit 500 is on the scale of FinalAtkSpdThdAndCastSpdThd's 500.
/// </summary>
internal static class SkillAttributeDamage
{
    private const string Formula = "AXaddBplus1";

    internal static long Apply(SkillConfig skill, StatSheet caster, long amount)
    {
        if (amount <= 0) return amount;
        int index = Array.FindIndex(skill.DamageFormulaExtra, f => f is Formula or $"{Formula}_withlimit");
        if (index < 0 || index >= skill.DamageParamExtra.Length || skill.DamageParamExtra[index] is not [var a, var attribute, > 0 and var b, .. var rest])
            return amount;
        double value = Math.Max(0, caster[(int)attribute]);
        if (rest is [> 0 and var limit, ..]) value = Math.Min(value, limit);
        return (long)Math.Round(amount * (1 + a * value / b));
    }
}
