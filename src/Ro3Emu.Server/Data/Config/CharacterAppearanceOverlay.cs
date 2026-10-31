using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The Dye Merchant (NPC, interaction, shop) and the dye items the shop rows name. The rows themselves are per
/// account (owned colours at 0) and built by the client hook from CharacterAppearanceRules.ShopRows; the server prices a
/// purchase from the same catalogue (CharacterAppearanceService.BuyLockedAsync).</summary>
internal static class CharacterAppearanceOverlay
{
    internal static Dictionary<string, JsonElement> Merchant(IReadOnlyDictionary<string, JsonElement> source, string table)
    {
        var rows = new Dictionary<string, JsonElement>(source);
        if (!source.TryGetValue(CharacterAppearanceRules.MerchantTemplate(table).ToString(CultureInfo.InvariantCulture), out var shape)) return rows;
        var node = JsonNode.Parse(shape.GetRawText())!.AsObject();
        foreach (var (key, value) in CharacterAppearanceRules.MerchantFields(table)) node[key] = JsonSerializer.SerializeToNode(value);
        rows[CharacterAppearanceRules.MerchantId.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToElement(node);
        return rows;
    }

    internal static Dictionary<string, JsonElement> Items(IReadOnlyDictionary<string, JsonElement> source)
    {
        var rows = new Dictionary<string, JsonElement>(source);
        if (!source.TryGetValue(CharacterAppearanceRules.ItemTemplate.ToString(CultureInfo.InvariantCulture), out var shape)) return rows;
        foreach (bool armour in new[] { false, true })
            for (int palette = 0; palette < CharacterAppearanceRules.PaletteCount; palette++)
            {
                int item = CharacterAppearanceRules.Item(palette, armour);
                var node = JsonNode.Parse(shape.GetRawText())!.AsObject();
                foreach (var (key, value) in CharacterAppearanceRules.ItemFields(item)) node[key] = JsonSerializer.SerializeToNode(value);
                rows[item.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToElement(node);
            }
        return rows;
    }
}
