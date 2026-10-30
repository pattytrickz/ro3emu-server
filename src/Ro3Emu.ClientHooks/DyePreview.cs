using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Ro3Emu.ClientHooks;

/// <summary>PTR material prototype. Owns its texture copies and never changes the shared source or combat tint.</summary>
internal sealed class DyePreview : MonoBehaviour
{
    private int _owner, _palette;
    private readonly List<Replacement> _textures = new List<Replacement>();
    private sealed class Replacement
    {
        internal Material Material;
        internal Texture Original;
        internal Texture2D Dyed;
    }

    internal static void Ensure(RD_Renderer renderer, int palette, int owner)
    {
        if (palette == 0 || !Ro3Emu.Shared.CharacterAppearanceRules.ValidPalette(palette)) { ResetOwned(renderer, owner); return; }
        var current = renderer.GetComponent<DyePreview>();
        if (current != null && current._owner == owner && current._palette == palette && current.Intact(renderer)) return;
        var materials = renderer.GetMaterials();
        if (renderer.GetRendererData().partType == RoleModelPartType.Body)
            foreach (var material in materials)
                if (material != null && material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") is Texture source
                    && !ClassArmourMask.Supports(source.name) && (current == null || !current.Owns(source)))
                { if (current != null) current.Restore(); return; }
        var colour = Ro3Emu.Shared.CharacterAppearanceRules.Colour(palette);
        Apply(renderer, new Color(colour[0], colour[1], colour[2], 1));
        current = renderer.GetComponent<DyePreview>();
        current._owner = owner; current._palette = palette;
    }

    internal static void ResetOwned(RD_Renderer renderer, int owner)
    {
        var current = renderer.GetComponent<DyePreview>();
        if (current != null && current._owner == owner) current.Restore();
    }

    private bool Owns(Texture texture)
    {
        foreach (var replacement in _textures) if (replacement.Dyed == texture) return true;
        return false;
    }

    private bool Intact(RD_Renderer renderer)
    {
        if (_textures.Count == 0) return false;
        var materials = renderer.GetMaterials();
        int textured = 0;
        foreach (var material in materials)
            if (material != null && material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != null) textured++;
        if (textured != _textures.Count) return false;
        foreach (var replacement in _textures)
            if (replacement.Material == null || !materials.Contains(replacement.Material)
                || replacement.Material.GetTexture("_BaseMap") != replacement.Dyed) return false;
        return true;
    }

    internal static void Apply(RD_Renderer renderer, Color colour)
    {
        if (!Valid(colour.r) || !Valid(colour.g) || !Valid(colour.b))
            throw new ArgumentException("Dye channels must be finite values from zero to one.");
        var data = renderer.GetRendererData();
        if (data == null || (data.partType != RoleModelPartType.Hair && data.partType != RoleModelPartType.Body))
            throw new ArgumentException("Only hair and verified default class armour can be previewed.");
        var preview = renderer.GetComponent<DyePreview>() ?? renderer.gameObject.AddComponent<DyePreview>();
        preview.Restore();
        try { preview.ApplyMaterials(renderer, data.partType == RoleModelPartType.Hair, colour); }
        catch { preview.Restore(); throw; }
    }

    private void ApplyMaterials(RD_Renderer renderer, bool hair, Color colour)
    {
        Color.RGBToHSV(colour, out float hue, out float saturation, out float brightness);
        foreach (var material in renderer.GetMaterials())
        {
            if (material == null || !material.HasProperty("_BaseMap")) continue;
            var source = material.GetTexture("_BaseMap");
            if (source == null) continue;
            // Fail closed: no generic body tint and no cosmetic-name prefix matching.
            if (!hair && !ClassArmourMask.Supports(source.name))
                throw new ArgumentException("This armour has no verified mask; its colours were left unchanged.");
            var dyed = Copy(source);
            try
            {
                var pixels = dyed.GetPixels32();
                for (int y = 0; y < dyed.height; y++)
                    for (int x = 0; x < dyed.width; x++)
                    {
                        int index = y * dyed.width + x;
                        if (!hair && !ClassArmourMask.Contains(source.name, (x + .5f) / dyed.width, 1 - (y + .5f) / dyed.height)) continue;
                        var original = (Color)pixels[index];
                        Color.RGBToHSV(original, out _, out _, out float value);
                        // Preserve painted shading and alpha; material _BaseColor remains owned by the game.
                        var result = Color.HSVToRGB(hue, saturation, value * brightness);
                        result.a = original.a;
                        pixels[index] = result;
                    }
                dyed.SetPixels32(pixels);
                dyed.Apply(true, true);
                _textures.Add(new Replacement { Material = material, Original = source, Dyed = dyed });
                material.SetTexture("_BaseMap", dyed);
            }
            catch { Destroy(dyed); throw; }
        }
    }

    private static bool Valid(float value) => !float.IsNaN(value) && value >= 0 && value <= 1;

    internal static void Reset(RD_Renderer renderer)
    {
        var preview = renderer.GetComponent<DyePreview>();
        if (preview != null) preview.Restore();
    }

    private static Texture2D Copy(Texture source)
    {
        var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, true);
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            copy.name = source.name + "_SolDyePreview";
            copy.wrapMode = source.wrapMode;
            copy.filterMode = source.filterMode;
            return copy;
        }
        catch { Destroy(copy); throw; }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
    }

    private void OnDisable() => Restore(); // pooled models must not carry a preview into another player's model
    private void OnDestroy() => Restore();
    private void Restore()
    {
        foreach (var replacement in _textures)
        {
            if (replacement.Material != null && replacement.Material.GetTexture("_BaseMap") == replacement.Dyed)
                replacement.Material.SetTexture("_BaseMap", replacement.Original);
            if (replacement.Dyed != null) Destroy(replacement.Dyed);
        }
        _textures.Clear();
    }
}
