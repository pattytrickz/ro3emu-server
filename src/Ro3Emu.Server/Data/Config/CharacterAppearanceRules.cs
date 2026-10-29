using System.Collections.Generic;

namespace Ro3Emu.Shared;

/// <summary>Sol cosmetic policy (inferred), shared with the client. Palette ids are stable saved values.</summary>
public static class CharacterAppearanceRules
{
    public const uint ClientReadyMessage = 77018;
    public const long SelectionCustomData = 9_930_001, PriceCustomData = 9_930_002;
    public const int HairItemBase = 1_999_100, ArmourItemBase = 1_999_200, CommodityBase = 9_930_100;
    public const int PaletteCount = 9, ClothingSlot = 4, ItemTemplate = 1_920_003, ShopTemplate = 113;
    public const int MerchantId = 993001, MerchantMap = 10100;
    public const long MerchantNameText = 99_300_003_001, MerchantButtonText = 99_300_003_002;
    public const string MerchantName = "Dye Merchant";
    public const long NameTextBase = 99_300_001_000, DescriptionTextBase = 99_300_002_000;

    public static bool ValidPalette(int palette) => palette >= 0 && palette < PaletteCount;
    public static bool IsDye(int item) => ValidPalette(item - HairItemBase) || ValidPalette(item - ArmourItemBase);
    public static bool IsArmour(int item) => ValidPalette(item - ArmourItemBase);
    public static int Palette(int item) => item - (IsArmour(item) ? ArmourItemBase : HairItemBase);
    public static bool IsCommodity(int id) => id >= CommodityBase && id < CommodityBase + PaletteCount * 2;
    public static int ItemForCommodity(int id) => id - CommodityBase < PaletteCount
        ? HairItemBase + id - CommodityBase : ArmourItemBase + id - CommodityBase - PaletteCount;
    public static int CommodityForItem(int item) => CommodityBase + Palette(item) + (IsArmour(item) ? PaletteCount : 0);
    public static long NameText(int item) => NameTextBase + item;
    public static long DescriptionText(int item) => DescriptionTextBase + item;
    // Until each atlas has its own checked mask, reject paid armour applications for other bodies.
    public static bool SupportsArmour(int career, int sex) => career == 1000 && sex == 2;
    public static long Pack(int hair, int armour, bool aura) => hair | (armour << 4) | (aura ? 256 : 0);
    public static int Hair(long state) => (int)(state & 15);
    public static int Armour(long state) => (int)((state >> 4) & 15);
    public static bool Aura(long state) => (state & 256) != 0;

    public static string ColourName(int palette) => palette switch
    {
        0 => "Original",
        1 => "Crimson",
        2 => "Azure",
        3 => "Emerald",
        4 => "Violet",
        5 => "Gold",
        6 => "Silver",
        7 => "Onyx",
        8 => "Rose",
        _ => "Unknown"
    };

    public static float[] Colour(int palette) => palette switch
    {
        1 => new[] { .9f, .18f, .25f },
        2 => new[] { .2f, .6f, .95f },
        3 => new[] { .15f, .7f, .4f },
        4 => new[] { .6f, .3f, .9f },
        5 => new[] { 1f, .8f, .25f },
        6 => new[] { .85f, .9f, .95f },
        7 => new[] { .2f, .23f, .28f },
        8 => new[] { 1f, .4f, .65f },
        _ => new[] { 1f, 1f, 1f }
    };

    public static string ItemName(int item) => ColourName(Palette(item)) + (IsArmour(item) ? " Class Armour Dye" : " Hair Dye");
    public static string Description(int item) => (Palette(item) == 0 ? "Restores the original colour. " : "Changes this character's colour. ")
        + (IsArmour(item) ? "PTR: female Novice cloth panels only. Cosmetic outfits are never dyed. " : "Keeps your hairstyle. ")
        + "Consumed on use. Replaces your previous dye. Buy from the Dye Merchant at Prontera's central fountain.";

    public static int MerchantTemplate(string table) => table switch
    {
        "NPCConfig" => 100002,
        "NPCInteractConfig" => 2900002,
        "ShopConfig" => 10,
        _ => 0
    };

    // Clone the resident merchant model and shop shape; the placement and dedicated catalogue are house policy.
    public static Dictionary<string, object> MerchantFields(string table) => table switch
    {
        "NPCConfig" => new Dictionary<string, object>
        {
            { "_iID", (long)MerchantId }, { "_kName", new[] { MerchantNameText } }, { "_kTitle", new long[] { 0 } },
            { "_iInteractID", new long[] { MerchantId } }, { "_iInteractDistance", 3L }, { "_kShowIcon", "" }
        },
        "NPCInteractConfig" => new Dictionary<string, object>
        {
            { "_iInterID", (long)MerchantId }, { "_iButtonName", new[] { MerchantButtonText } },
            // DialogueConfig 40001402 opens the resident NPC shop panel with action 25, shop id, class id, page 1.
            { "_iEXECAction", new[] { new[] { "25", "70110", "TOPBAR", "UI_Sys_ShopNpcPanel", "993001", "993001", "1" } } }
        },
        "ShopConfig" => new Dictionary<string, object>
        {
            { "_iID", (long)MerchantId }, { "_iClassID", (long)MerchantId }, { "_iShopId", (long)MerchantId },
            { "_iNpcId", (long)MerchantId }, { "_iShopName", new[] { MerchantNameText } }, { "_kCurrencyDisplay", new long[] { 1 } }
        },
        _ => new Dictionary<string, object>()
    };

    public static Dictionary<string, object> ItemFields(int item) => new Dictionary<string, object>
    {
        { "_iID", (long)item }, { "_iName", new long[] { NameText(item) } },
        // PCClickUseItemType admits cosmetic consumables (2,210), not the social-frame template's (2,218).
        { "_iType", 2L }, { "_iSubType", 210L },
        { "_iDescription", new long[] { DescriptionText(item) } }, { "_iDescription2", new long[] { 0 } },
        { "_iIsUse", 1L }, { "_iNeedConfirm", 1L }, { "_iGroupUse", 0L }, { "_iStackLimit", 99L },
        { "_iTrade", 0L }, { "_iAuction", 0L }, { "_iCanStorehouse", 0L }, { "_kCanCart", 0L },
        { "_iShortcutBar", 0L }, { "_iCD", 0L }, { "_kCDGroup", System.Array.Empty<long>() },
        { "_iUseEffect", System.Array.Empty<long>() }, { "_iAward", 0L },
        { "_kParam1", "" }, { "_kParam2", "" }, { "_kParam3", "" }, { "_kParam4", "" },
        { "_kIcon", "item_1001000.png" }, { "_iSort", 990L }, { "_iQuality", 2L }
    };

    public static Dictionary<string, object> ShopFields(int item, int price) => new Dictionary<string, object>
    {
        { "_iID", (long)CommodityForItem(item) }, { "_iClassID", (long)MerchantId }, { "_iItemId", (long)item }, { "_iItemPerMount", 1L },
        { "_kItemPerPrice", new long[] { 1, price } }, { "_iDiscount", 0L }, { "_iPurchaseTimesLimit", 0L },
        { "_iPurchaseNum", 0L }, { "_iSortId", (long)(990 + item - HairItemBase) }
    };
}
