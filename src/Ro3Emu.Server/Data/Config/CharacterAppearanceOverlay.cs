using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Data.Config;

internal static class CharacterAppearanceOverlay
{
    internal static Dictionary<string, JsonElement> Apply(IReadOnlyDictionary<string, JsonElement> source, bool shop, int price)
    {
        var rows = new Dictionary<string, JsonElement>(source);
        int template = shop ? CharacterAppearanceRules.ShopTemplate : CharacterAppearanceRules.ItemTemplate;
        if (!source.TryGetValue(template.ToString(CultureInfo.InvariantCulture), out var shape)) return rows;
        for (int i = 0; i < CharacterAppearanceRules.PaletteCount * 2; i++)
        {
            int commodity = CharacterAppearanceRules.CommodityBase + i;
            int item = CharacterAppearanceRules.ItemForCommodity(commodity);
            var node = JsonNode.Parse(shape.GetRawText())!.AsObject();
            var fields = shop ? CharacterAppearanceRules.ShopFields(item, price) : CharacterAppearanceRules.ItemFields(item);
            foreach (var (key, value) in fields) node[key] = JsonSerializer.SerializeToNode(value);
            rows[(shop ? commodity : item).ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToElement(node);
        }
        return rows;
    }
}
