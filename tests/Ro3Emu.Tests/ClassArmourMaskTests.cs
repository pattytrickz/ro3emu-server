using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

public sealed class ClassArmourMaskTests
{
    [Theory]
    [InlineData("Model_Body_Novice_F_LOD0_Copy")]
    [InlineData("Model_Body_Novice_F_LOD1")]
    [InlineData("Model_Body_Fashion_F_LOD0")]
    [InlineData("Model_Body_Novice_M_LOD1")]
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

    private const string Male = "Model_Body_Novice_M_LOD0";

    [Fact]
    public void EveryPixelInTheMaleSkinIslandIsExcluded()
    {
        for (int y = 0; y <= 90; y++)
            for (int x = 262; x <= 330; x++)
                Assert.False(ClassArmourMask.Contains(Male, x / 512f, y / 512f));
    }

    [Theory]
    [InlineData(348, 180)] // gold trim beside the cape
    [InlineData(380, 140)] // gold bar across it
    [InlineData(430, 135)] // white edging
    [InlineData(490, 230)] // beige shoulder piece
    [InlineData(465, 75)] // shoe sole
    [InlineData(180, 100)] // metal plate
    [InlineData(150, 400)] // tan trousers
    public void MaleNonClothDetailsKeepTheirOriginalColours(int x, int y) =>
        Assert.False(ClassArmourMask.Contains(Male, x / 512f, y / 512f));

    [Theory]
    [InlineData(388, 200)]
    [InlineData(380, 275)]
    [InlineData(440, 230)]
    [InlineData(480, 150)]
    [InlineData(480, 300)]
    [InlineData(390, 40)]
    public void MaleCapePanelsAreIncluded(int x, int y) =>
        Assert.True(ClassArmourMask.Contains(Male, x / 512f, y / 512f));
}
