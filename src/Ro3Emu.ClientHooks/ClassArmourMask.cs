namespace Ro3Emu.ClientHooks;

/// <summary>Authored UV regions of the default Novice class armour, in normalized top-down texture coordinates.
/// Unlisted textures (including every cosmetic outfit) are deliberately unsupported.</summary>
internal static partial class ClassArmourMask
{
    private const string NoviceFemaleTexture = "Model_Body_Novice_F_LOD0", NoviceMaleTexture = "Model_Body_Novice_M_LOD0";
    private const int Size = 512;
    // Built on first use: static field order across partial files is undefined, and the lookup costs 256 KB.
    private static readonly System.Lazy<bool[]> NoviceMaleTexels = new System.Lazy<bool[]>(() => Texels(NoviceMale!));

    internal static bool Supports(string texture) => texture == NoviceFemaleTexture || texture == NoviceMaleTexture;

    internal static bool Contains(string texture, float u, float v)
    {
        if (!Supports(texture) || u < 0 || u > 1 || v < 0 || v > 1) return false;
        float x = u * Size, y = v * Size;
        if (texture == NoviceMaleTexture)
            return NoviceMaleTexels.Value[System.Math.Min(Size - 1, (int)y) * Size + System.Math.Min(Size - 1, (int)x)];
        // Conservative cloth panels. The skin island at x=135..242,y=0..205, metal, belts and boots stay untouched.
        return x >= 249 && x <= 402 && y >= 4 && y <= 119
            || x >= 244 && x <= 504 && y >= 386 && y <= 466;
    }

    /// <summary>A generated mask's rectangles as one lookup (a dye tests every texel of the atlas).</summary>
    private static bool[] Texels(short[] rectangles)
    {
        var texels = new bool[Size * Size];
        for (int i = 0; i + 3 < rectangles.Length; i += 4)
            for (int y = rectangles[i + 1]; y <= rectangles[i + 3]; y++)
                for (int x = rectangles[i]; x <= rectangles[i + 2]; x++)
                    texels[y * Size + x] = true;
        return texels;
    }
}
