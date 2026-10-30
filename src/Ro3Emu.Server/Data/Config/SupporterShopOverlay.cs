using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The Supporter Merchant's rows (SupporterShopRules), cloned from the Dye Merchant's templates: the server
/// reads the same NPC, items and commodities the hooked client shows.</summary>
internal static class SupporterShopOverlay
{
    private static JsonElement Clone(JsonElement shape, Dictionary<string, object> fields)
    {
        var node = JsonNode.Parse(shape.GetRawText())!.AsObject();
        foreach (var (key, value) in fields) node[key] = JsonSerializer.SerializeToNode(value);
        return JsonSerializer.SerializeToElement(node);
    }

    internal static Dictionary<string, JsonElement> Merchant(IReadOnlyDictionary<string, JsonElement> source, string table)
    {
        var rows = new Dictionary<string, JsonElement>(source);
        if (source.TryGetValue(CharacterAppearanceRules.MerchantTemplate(table).ToString(CultureInfo.InvariantCulture), out var shape))
            rows[SupporterShopRules.MerchantId.ToString(CultureInfo.InvariantCulture)] = Clone(shape, SupporterShopRules.MerchantFields(table));
        return rows;
    }

    internal static Dictionary<string, JsonElement> Items(IReadOnlyDictionary<string, JsonElement> source)
    {
        var rows = new Dictionary<string, JsonElement>(source);
        if (source.TryGetValue(CharacterAppearanceRules.ItemTemplate.ToString(CultureInfo.InvariantCulture), out var shape))
            foreach (var p in SupporterShopRules.Products)
                rows[p.Item.ToString(CultureInfo.InvariantCulture)] = Clone(shape, SupporterShopRules.ItemFields(p));
        return rows;
    }

    internal static Dictionary<string, JsonElement> Shop(IReadOnlyDictionary<string, JsonElement> source)
    {
        var rows = new Dictionary<string, JsonElement>(source);
        if (source.TryGetValue(CharacterAppearanceRules.ShopTemplate.ToString(CultureInfo.InvariantCulture), out var shape))
            foreach (var p in SupporterShopRules.Products)
                rows[p.Commodity.ToString(CultureInfo.InvariantCulture)] = Clone(shape, SupporterShopRules.ShopFields(p));
        return rows;
    }
}
