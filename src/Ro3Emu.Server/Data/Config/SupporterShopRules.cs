// Shared with Ro3Emu.ClientHooks, which builds for the client's runtime: explicit constructors, as F2PRules.
#pragma warning disable IDE0290
#nullable enable
using System.Collections.Generic;

namespace Ro3Emu.Shared;

/// <summary>
/// The Supporter Merchant (owner's plan v1.1 §2.2), shared with the client hook: a resident NPC beside the Dye Merchant
/// that sells token-priced booster items to active supporters. Each product is an item used from the bag; the server
/// checks the account-wide weekly limit at purchase and active Supporter status at purchase and at use. The colour
/// voucher of the plan is the Dye Merchant's rare colours (75 Supporter Tokens, CharacterAppearanceRules).
/// </summary>
public static class SupporterShopRules
{
    public const int MerchantId = 993002, MerchantMap = 10100;
    public const long MerchantNameText = 99_300_013_001, MerchantButtonText = 99_300_013_002;
    public const string MerchantName = "Supporter Merchant";
    public const long NameTextBase = 99_300_011_000, DescriptionTextBase = 99_300_012_000;
    public const string Recharge = "recharge", Exp = "exp", Drop = "drop";
    /// <summary>The two EXP boosters together: at most this many a week per account (each also 1 a week).</summary>
    public const int ExpWeeklyLimit = 2;

    public sealed class Product
    {
        public Product(int item, int commodity, string kind, int price, int weeklyLimit, int percent, int minutes, string name, string description)
        {
            Item = item; Commodity = commodity; Kind = kind; Price = price; WeeklyLimit = weeklyLimit; Percent = percent;
            Minutes = minutes; Name = name; Description = description;
        }

        public int Item { get; }
        public int Commodity { get; }
        public string Kind { get; }
        public int Price { get; }
        public int WeeklyLimit { get; }
        public int Percent { get; }
        public int Minutes { get; }
        public string Name { get; }
        public string Description { get; }
    }

    private const string Rules = " Use needs active Supporter status. Bound to this character; cannot be traded.";

    public static readonly Product[] Products =
    {
        new Product(1_999_301, 9_930_301, Recharge, 60, 2, 0, 60, "Auto-Battle Time Recharge",
            "Adds 60 minutes of auto-battle (Bonus) time to this character, up to your bank limit. Not used if it would not fit." + Rules),
        new Product(1_999_302, 9_930_302, Exp, 80, 1, 50, 60, "Auto EXP Booster +50%",
            "+50% Base and Job EXP from monsters you defeat in Auto Mode, for 60 minutes of active auto-battle. The time only "
            + "runs while auto-battle time is being used. One booster at a time." + Rules),
        new Product(1_999_303, 9_930_303, Exp, 100, 1, 100, 30, "Auto EXP Booster +100%",
            "+100% Base and Job EXP from monsters you defeat in Auto Mode, for 30 minutes of active auto-battle. The time only "
            + "runs while auto-battle time is being used. One booster at a time." + Rules),
        new Product(1_999_304, 9_930_304, Drop, 40, 2, 50, 15, "Drop Booster 1.5x",
            "1.5x the chance of every item drop from monsters you defeat in Auto Mode, for 15 minutes of active auto-battle. "
            + "The time only runs while auto-battle time is being used. One booster at a time." + Rules),
    };

    public static Product? ByItem(int item)
    {
        foreach (var p in Products) if (p.Item == item) return p;
        return null;
    }

    public static Product? ByCommodity(int commodity)
    {
        foreach (var p in Products) if (p.Commodity == commodity) return p;
        return null;
    }

    public static long NameText(int item) => NameTextBase + item;
    public static long DescriptionText(int item) => DescriptionTextBase + item;

    // The Dye Merchant's templates (CharacterAppearanceRules.MerchantTemplate): the resident merchant model and shop shape.
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
            { "_iEXECAction", new[] { new[] { "25", "70110", "TOPBAR", "UI_Sys_ShopNpcPanel", "993002", "993002", "1" } } }
        },
        "ShopConfig" => new Dictionary<string, object>
        {
            { "_iID", (long)MerchantId }, { "_iClassID", (long)MerchantId }, { "_iShopId", (long)MerchantId },
            { "_iNpcId", (long)MerchantId }, { "_iShopName", new[] { MerchantNameText } },
            { "_kCurrencyDisplay", new long[] { F2PRules.SupporterToken } }
        },
        _ => new Dictionary<string, object>()
    };

    public static Dictionary<string, object> ItemFields(Product p) => new Dictionary<string, object>
    {
        { "_iID", (long)p.Item }, { "_iName", new long[] { NameText(p.Item) } },
        // The click-use consumable type the dyes use (PCClickUseItemType admits 2,210).
        { "_iType", 2L }, { "_iSubType", 210L },
        { "_iDescription", new long[] { DescriptionText(p.Item) } }, { "_iDescription2", new long[] { 0 } },
        { "_iIsUse", 1L }, { "_iNeedConfirm", 1L }, { "_iGroupUse", 0L }, { "_iStackLimit", 99L },
        { "_iTrade", 0L }, { "_iAuction", 0L }, { "_iCanStorehouse", 0L }, { "_kCanCart", 0L },
        { "_iShortcutBar", 0L }, { "_iCD", 0L }, { "_kCDGroup", System.Array.Empty<long>() },
        { "_iUseEffect", System.Array.Empty<long>() }, { "_iAward", 0L },
        { "_kParam1", "" }, { "_kParam2", "" }, { "_kParam3", "" }, { "_kParam4", "" },
        { "_kIcon", "item_1001000.png" }, { "_iSort", 991L }, { "_iQuality", 3L }
    };

    public static Dictionary<string, object> ShopFields(Product p) => new Dictionary<string, object>
    {
        { "_iID", (long)p.Commodity }, { "_iClassID", (long)MerchantId }, { "_iItemId", (long)p.Item }, { "_iItemPerMount", 1L },
        { "_kItemPerPrice", new long[] { F2PRules.SupporterToken, p.Price } },
        // The weekly limits are per account, kept by the server (Game.SupporterShop); the client's per-character counter is off.
        { "_iDiscount", 0L }, { "_iMerchantDiscount", 0L }, { "_iPurchaseTimesLimit", 0L },
        { "_iPurchaseNum", 0L }, { "_iSortId", (long)(p.Commodity - 9_930_300) }
    };
}
