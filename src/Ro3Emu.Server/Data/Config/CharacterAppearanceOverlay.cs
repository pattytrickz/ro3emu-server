using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Data.Config;

internal static class CharacterAppearanceOverlay
{
    internal static Dictionary<string, JsonElement> Merchant(IReadOnlyDictionary<string, JsonElement> source, string table, int rarePrice = 0)
    {
        var rows = new Dictionary<string, JsonElement>(source);
        if (!source.TryGetValue(CharacterAppearanceRules.MerchantTemplate(table).ToString(CultureInfo.InvariantCulture), out var shape)) return rows;
        var node = JsonNode.Parse(shape.GetRawText())!.AsObject();
        foreach (var (key, value) in CharacterAppearanceRules.MerchantFields(table, rarePrice)) node[key] = JsonSerializer.SerializeToNode(value);
        rows[CharacterAppearanceRules.MerchantId.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToElement(node);
        return rows;
    }

    internal static Dictionary<string, JsonElement> Apply(IReadOnlyDictionary<string, JsonElement> source, bool shop, int price, int rarePrice = 0)
    {
        var rows = new Dictionary<string, JsonElement>(source);
        int template = shop ? CharacterAppearanceRules.ShopTemplate : CharacterAppearanceRules.ItemTemplate;
        if (!source.TryGetValue(template.ToString(CultureInfo.InvariantCulture), out var shape)) return rows;
        for (int i = 0; i < CharacterAppearanceRules.PaletteCount * 2; i++)
        {
            int commodity = CharacterAppearanceRules.CommodityBase + i;
            int item = CharacterAppearanceRules.ItemForCommodity(commodity);
            if (shop && CharacterAppearanceRules.IsRare(item) && rarePrice <= 0) continue;
            var node = JsonNode.Parse(shape.GetRawText())!.AsObject();
            var fields = shop ? CharacterAppearanceRules.ShopFields(item, price, rarePrice) : CharacterAppearanceRules.ItemFields(item);
            foreach (var (key, value) in fields) node[key] = JsonSerializer.SerializeToNode(value);
            rows[(shop ? commodity : item).ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToElement(node);
        }
        return rows;
    }
}
