using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The area a rectangular player skill hits, on rows shaped like the client's (values invented).</summary>
public sealed class SkillAreaTests
{
    private static JsonElement Row(int release, params int[] range) => JsonSerializer.SerializeToElement(new Dictionary<string, object>
    {
        ["_iRelease"] = release, ["_iRangeType"] = SkillRelease.Area, ["_iAreaHeightRange"] = 800, ["_kRange"] = range,
    });

    private static Vec3 At(float x, float z) => new() { X = x, Y = 0, Z = z };

    /// <summary>
    /// Earth Strain (1231001, _iRelease 6 Rect360, _kRange [1550, 750, 300]): "... to up to ${3} enemies in a ${1}-meter by
    /// ${2}-meter area in front of You" (_kDescData ${1} 7, ${2} 15). The first _kRange value is the length ahead of the
    /// caster, the second the width across; the 7.5 m wide, 15.5 m long strip lies in front and not behind. Reported:
    /// the monsters inside the drawn strain often took no damage.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 14f, true)]    // ahead, near the far end
    [InlineData(0f, 3f, 10f, true)]    // ahead, beside the line (half the width is 3.75 m)
    [InlineData(0f, 5f, 10f, false)]   // too far to the side
    [InlineData(0f, 0f, 16f, false)]   // beyond the length
    [InlineData(0f, 0f, -3f, false)]   // behind the caster
    [InlineData(90f, 14f, 0f, true)]   // facing +X: the strip turns with the caster
    [InlineData(90f, 0f, 10f, false)]
    public void ARectangleReachesAheadByItsFirstRangeValueAndAcrossBySecond(float face, float x, float z, bool hit)
    {
        var row = Row(SkillRelease.Rect360, 1550, 750, 300);
        Assert.Equal(hit, CombatService.SkillAreaContains(row, At(0, 0), At(0, 0), face, At(x, z)));
    }

    [Theory]
    [InlineData(SkillRelease.FixRect)]
    [InlineData(SkillRelease.Arrow)]
    public void TheFixedRectangleAndTheArrowShareTheShape(int release)
    {
        var row = Row(release, 1500, 300, 0);
        Assert.True(CombatService.SkillAreaContains(row, At(0, 0), At(0, 0), 0f, At(1f, 14f)));
        Assert.False(CombatService.SkillAreaContains(row, At(0, 0), At(0, 0), 0f, At(4f, 5f)));
    }
}
