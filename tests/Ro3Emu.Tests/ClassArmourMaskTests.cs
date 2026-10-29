using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

public sealed class ClassArmourMaskTests
{
    [Theory]
    [InlineData("Model_Body_Novice_F_LOD0_Copy")]
    [InlineData("Model_Body_Novice_F_LOD1")]
    [InlineData("Model_Body_Fashion_F_LOD0")]
    [InlineData("Model_Body_Novice_M_LOD0")]
    [InlineData("")]
    public void UnknownAndCosmeticTexturesCannotBeDyed(string texture)
    {
        Assert.False(ClassArmourMask.Supports(texture));
        Assert.False(ClassArmourMask.Contains(texture, .6f, .1f));
        Assert.False(ClassArmourMask.Contains(texture, .7f, .8f));
    }

    [Fact]
    public void EveryPixelInTheSkinIslandIsExcluded()
    {
        for (int y = 0; y <= 205; y++)
            for (int x = 135; x <= 242; x++)
                Assert.False(ClassArmourMask.Contains("Model_Body_Novice_F_LOD0", x / 512f, y / 512f));
    }

    [Theory]
    [InlineData(80, 90)] // metal breastplate
    [InlineData(354, 237)] // belt
    [InlineData(452, 287)] // boot
    [InlineData(134, 456)] // metal trim
    public void NonClothDetailsKeepTheirOriginalColours(int x, int y) =>
        Assert.False(ClassArmourMask.Contains("Model_Body_Novice_F_LOD0", x / 512f, y / 512f));

    [Theory]
    [InlineData(320, 50)]
    [InlineData(380, 420)]
    public void AuthoredClothPanelsAreIncluded(int x, int y) =>
        Assert.True(ClassArmourMask.Contains("Model_Body_Novice_F_LOD0", x / 512f, y / 512f));
}
