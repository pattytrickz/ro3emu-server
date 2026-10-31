using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class SkillAttributeDamageTests
{
    private const int Attribute = AttributeId.BaseAspd;

    private static SkillConfig Skill(string formula, string extra) =>
        new(JsonDocument.Parse($$"""{"_kDamageFormulaExtra": ["{{formula}}"], "_kDamageParamExtra": {{extra}}}""").RootElement);

    private static StatSheet Stats(long value)
    {
        var stats = new StatSheet();
        stats[Attribute] = value;
        return stats;
    }

    [Theory]
    [InlineData(200, 1500)] // 1000 * (1 + 25 * 200 / 10000)
    [InlineData(400, 2000)]
    [InlineData(900, 2000)] // the limit of 400 holds
    public void TheBonusGrowsWithTheAttributeUpToTheLimit(long value, long expected) =>
        Assert.Equal(expected, SkillAttributeDamage.Apply(Skill("AXaddBplus1_withlimit", "[[25, 115, 10000, 400]]"), Stats(value), 1000));

    [Fact]
    public void ARowWithoutALimitKeepsGrowing() =>
        Assert.Equal(3250, SkillAttributeDamage.Apply(Skill("AXaddBplus1", "[[25, 115, 10000]]"), Stats(900), 1000));

    [Fact]
    public void OtherFormulasAndMissesAreLeftAlone()
    {
        Assert.Equal(1000, SkillAttributeDamage.Apply(Skill("T_AXadd0_etc", "[[2, 10000, 101, 3]]"), Stats(900), 1000));
        Assert.Equal(1000, SkillAttributeDamage.Apply(Skill("", "{}"), Stats(900), 1000));
        Assert.Equal(0, SkillAttributeDamage.Apply(Skill("AXaddBplus1", "[[25, 115, 10000]]"), Stats(900), 0));
    }
}
