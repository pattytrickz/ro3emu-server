namespace Ro3Emu.ClientHooks;

/// <summary>Authored UV regions for the first PTR prototype, in normalized top-down texture coordinates.
/// Unlisted textures (including every cosmetic outfit) are deliberately unsupported.</summary>
internal static class ClassArmourMask
{
    internal static bool Supports(string texture) => texture == "Model_Body_Novice_F_LOD0";

    internal static bool Contains(string texture, float u, float v)
    {
        if (!Supports(texture) || u < 0 || u > 1 || v < 0 || v > 1) return false;
        float x = u * 512, y = v * 512;
        // Conservative cloth panels. The skin island at x=135..242,y=0..205, metal, belts and boots stay untouched.
        return x >= 249 && x <= 402 && y >= 4 && y <= 119
            || x >= 244 && x <= 504 && y >= 386 && y <= 466;
    }
}
