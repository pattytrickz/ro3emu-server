// Shared with Ro3Emu.ClientHooks, which builds for the client's runtime: keep explicit constructors (as F2PRules).
#pragma warning disable IDE0290
using System.Collections.Generic;

namespace Ro3Emu.Shared;

/// <summary>Dye tiers of the Dye Merchant's catalogue (owner's dye guide, 31 Oct 2026).</summary>
public enum DyeTier { Original, Common, Rare, Legendary, Retired }

/// <summary>One colour of the catalogue: its stable palette id, name, sRGB hex, tier and prices.</summary>
public sealed class Dye
{
    public Dye(int palette, string name, string hex, DyeTier tier, int crystals, int tokens)
    {
        Palette = palette; Name = name; Hex = hex; Tier = tier; Crystals = crystals; Tokens = tokens;
    }

    public int Palette { get; }
    public string Name { get; }
    public string Hex { get; }
    public DyeTier Tier { get; }
    /// <summary>The unlock's price in Sacred Relic Crystals; 0 for the Original and retired colours.</summary>
    public int Crystals { get; }
    /// <summary>The unlock's alternative price in Supporter Tokens; 0 where Crystals are the only payment.</summary>
    public int Tokens { get; }
    public bool ForSale => Tier is DyeTier.Common or DyeTier.Rare or DyeTier.Legendary;
}

/// <summary>
/// Sol cosmetic policy, shared with the client. Palette ids are stable saved values: 1-8 are the first catalogue's
/// colours (re-tinted to the guide's hex codes; Silver, 6, is no longer sold but still renders), 9-31 were added by the
/// tiered catalogue. A colour is bought once per account at the Dye Merchant (an unlock, Accounts' dye_unlock) and
/// every character of the account then applies it to hair or Novice class armour for free; the merchant's "purchase"
/// of an owned colour is that free switch. Dye bottles from the first catalogue still work: using one unlocks its colour.
/// </summary>
public static class CharacterAppearanceRules
{
    public const uint ClientReadyMessage = 77018;
    /// <summary>The hooks' version, sent in the client-ready message: 2 has the bottle dyes, 3 also the Supporter Merchant
    /// (SupporterShopRules), 4 the tiered unlock catalogue. The Dye Merchant needs 4; older hooks still see every dye,
    /// drawn in the nearest of the first catalogue's colours (<see cref="SelectionCustomData"/>).</summary>
    public const byte ClientVersion = 4, FirstClientVersion = 2, SupporterShopClientVersion = 3, UnlockClientVersion = 4;
    /// <summary>Hair and armour palettes for hooks before 4 (4 bits each, aura bit 8, first-catalogue colours only).</summary>
    public const long SelectionCustomData = 9_930_001;
    /// <summary>Hooks before 4 read these as the bottle prices; the server sends 0 there, which empties their old shop rows.</summary>
    public const long PriceCustomData = 9_930_002, RarePriceCustomData = 9_930_003;
    /// <summary>Hair and armour palettes, 5 bits each, aura bit 10 (<see cref="PackFull"/>).</summary>
    public const long FullSelectionCustomData = 9_930_004;
    /// <summary>To the owner only: bit n = palette n unlocked for the account, bit <see cref="ArmourSupportedBit"/> = this
    /// character's body takes armour dye. Its arrival is also the version-4 catalogue's go-ahead.</summary>
    public const long OwnedCustomData = 9_930_005;
    public const int ArmourSupportedBit = 40;
    public const int Crystal = F2PRules.Crystal, Token = F2PRules.SupporterToken;
    public const int HairItemBase = 1_999_100, ArmourItemBase = 1_999_200;
    /// <summary>Version-4 commodities: base + palette + 32 (armour) + 64 (paid in tokens).</summary>
    public const int CommodityBase = 9_930_400, CommodityCount = 128;
    public const int PaletteCount = 32, ClothingSlot = 4, ItemTemplate = 1_920_003, ShopTemplate = 113;
    public const int MerchantId = 993001, MerchantMap = 10100;
    public const long MerchantNameText = 99_300_003_001, MerchantButtonText = 99_300_003_002;
    public const string MerchantName = "Dye Merchant";
    public const long NameTextBase = 99_300_001_000, DescriptionTextBase = 99_300_002_000;

    private static readonly Dye[] Catalogue =
    {
        new Dye(0, "Original", "", DyeTier.Original, 0, 0),
        new Dye(1, "Crimson", "C83B42", DyeTier.Common, 500, 0),
        new Dye(2, "Azure", "3378B9", DyeTier.Common, 700, 0),
        new Dye(3, "Emerald", "397C4B", DyeTier.Common, 500, 0),
        new Dye(4, "Violet", "9873C4", DyeTier.Common, 700, 0),
        new Dye(5, "Gold", "EAC243", DyeTier.Common, 500, 0),
        new Dye(6, "Silver", "D9E6F2", DyeTier.Retired, 0, 0),
        new Dye(7, "Onyx", "38363B", DyeTier.Common, 700, 0),
        new Dye(8, "Rose", "DE83A4", DyeTier.Common, 700, 0),
        new Dye(9, "Ivory", "F1EADB", DyeTier.Common, 350, 0),
        new Dye(10, "Ash Grey", "8D9097", DyeTier.Common, 350, 0),
        new Dye(11, "Chestnut", "794C36", DyeTier.Common, 350, 0),
        new Dye(12, "Sand", "C9AF8F", DyeTier.Common, 350, 0),
        new Dye(13, "Tangerine", "DD7B35", DyeTier.Common, 500, 0),
        new Dye(14, "Burgundy", "7A1F35", DyeTier.Rare, 1400, 75),
        new Dye(15, "Forest Jade", "1F6E54", DyeTier.Rare, 1400, 75),
        new Dye(16, "Midnight Navy", "1D2B53", DyeTier.Rare, 1400, 75),
        new Dye(17, "Copper", "A55A2A", DyeTier.Rare, 1400, 75),
        new Dye(18, "Ruby", "A3122A", DyeTier.Rare, 1750, 100),
        new Dye(19, "Sapphire", "1E4FA8", DyeTier.Rare, 1750, 100),
        new Dye(20, "Amethyst", "6B33A1", DyeTier.Rare, 1750, 100),
        new Dye(21, "Deep Teal", "0C6F73", DyeTier.Rare, 1750, 100),
        new Dye(22, "Rose Gold", "B76E79", DyeTier.Rare, 2100, 125),
        new Dye(23, "Amber", "C8811A", DyeTier.Rare, 2100, 125),
        new Dye(24, "Plum", "5B2A55", DyeTier.Rare, 2100, 125),
        new Dye(25, "Ice Silver", "B9C6D3", DyeTier.Rare, 2100, 125),
        new Dye(26, "Void Purple", "5700D4", DyeTier.Legendary, 2800, 150),
        new Dye(27, "Celestial Cyan", "35E9F5", DyeTier.Legendary, 2800, 150),
        new Dye(28, "Solar Gold", "FFD338", DyeTier.Legendary, 2800, 150),
        new Dye(29, "Phantom Pink", "FF59B6", DyeTier.Legendary, 2800, 150),
        new Dye(30, "Spectral Mint", "66F0AB", DyeTier.Legendary, 2800, 150),
        new Dye(31, "Astral Blue", "7183FF", DyeTier.Legendary, 2800, 150),
    };

    /// <summary>The nearest first-catalogue colour (1-8) of each palette, for hooks before version 4.</summary>
    private static readonly int[] Legacy = BuildLegacy();

    public static IReadOnlyList<Dye> Dyes => Catalogue;
    public static Dye DyeOf(int palette) => Catalogue[ValidPalette(palette) ? palette : 0];
    public static bool ValidPalette(int palette) => palette >= 0 && palette < PaletteCount;
    public static bool IsServerMarker(long id) => id == SelectionCustomData || id == PriceCustomData || id == RarePriceCustomData
        || id == FullSelectionCustomData || id == OwnedCustomData;
    public static bool IsDye(int item) => ValidPalette(item - HairItemBase) || ValidPalette(item - ArmourItemBase);
    public static bool IsArmour(int item) => ValidPalette(item - ArmourItemBase);
    public static int Palette(int item) => item - (IsArmour(item) ? ArmourItemBase : HairItemBase);
    public static int Item(int palette, bool armour) => (armour ? ArmourItemBase : HairItemBase) + palette;

    public static bool IsCommodity(int id) => id >= CommodityBase && id < CommodityBase + CommodityCount;
    public static int Commodity(int palette, bool armour, bool tokens) => CommodityBase + palette + (armour ? 32 : 0) + (tokens ? 64 : 0);
    public static int CommodityPalette(int id) => (id - CommodityBase) % 32;
    public static bool CommodityArmour(int id) => (id - CommodityBase) / 32 % 2 == 1;
    public static bool CommodityTokens(int id) => id - CommodityBase >= 64;
    public static long NameText(int item) => NameTextBase + item;
    public static long DescriptionText(int item) => DescriptionTextBase + item;
    // Until each atlas has its own checked mask, reject armour applications for other bodies. Novice: both sexes
    // (Model_Body_Novice_F_LOD0 and _M_LOD0, Ro3Emu.ClientHooks.ClassArmourMask).
    public static bool SupportsArmour(int career, int sex) => career == 1000 && sex is 1 or 2;

    /// <summary>The first catalogue's marker: palettes above 8 become their nearest first-catalogue colour.</summary>
    public static long Pack(int hair, int armour, bool aura) => LegacyOf(hair) | (LegacyOf(armour) << 4) | (aura ? 256 : 0);
    public static int Hair(long state) => (int)(state & 15);
    public static int Armour(long state) => (int)((state >> 4) & 15);
    public static bool Aura(long state) => (state & 256) != 0;
    public static long PackFull(int hair, int armour, bool aura) => (long)hair | ((long)armour << 5) | (aura ? 1024L : 0);
    public static int FullHair(long state) => (int)(state & 31);
    public static int FullArmour(long state) => (int)((state >> 5) & 31);
    public static bool FullAura(long state) => (state & 1024) != 0;
    public static int LegacyOf(int palette) => ValidPalette(palette) ? Legacy[palette] : 0;

    public static bool Owns(long owned, int palette) => palette == 0 || ValidPalette(palette) && (owned & (1L << palette)) != 0;

    public static string ColourName(int palette) => DyeOf(palette).Name;

    public static float[] Colour(int palette)
    {
        string hex = DyeOf(palette).Hex;
        if (palette == 0 || hex.Length != 6) return new[] { 1f, 1f, 1f };
        return new[] { Channel(hex, 0), Channel(hex, 2), Channel(hex, 4) };
    }

    private static float Channel(string hex, int at) => System.Convert.ToInt32(hex.Substring(at, 2), 16) / 255f;

    private static int[] BuildLegacy()
    {
        var map = new int[PaletteCount];
        for (int p = 0; p < PaletteCount; p++)
        {
            if (p <= 8) { map[p] = p; continue; }
            float[] c = Colour(p);
            int best = 1;
            float bestDistance = float.MaxValue;
            for (int q = 1; q <= 8; q++)
            {
                float[] d = Colour(q);
                float distance = (c[0] - d[0]) * (c[0] - d[0]) + (c[1] - d[1]) * (c[1] - d[1]) + (c[2] - d[2]) * (c[2] - d[2]);
                if (distance < bestDistance) { bestDistance = distance; best = q; }
            }
            map[p] = best;
        }
        return map;
    }

    public static string ItemName(int item) => ColourName(Palette(item)) + (IsArmour(item) ? " Class Armour Dye" : " Hair Dye");

    public static string TierName(DyeTier tier) => tier switch
    {
        DyeTier.Common => "Common",
        DyeTier.Rare => "Rare",
        DyeTier.Legendary => "Legendary",
        DyeTier.Retired => "Retired",
        _ => "Original"
    };

    public static string Description(int item, bool owned = false)
    {
        var dye = DyeOf(Palette(item));
        string part = IsArmour(item) ? "Novice class armour (cosmetic outfits are never dyed)" : "hair (keeps your hairstyle)";
        if (dye.Tier == DyeTier.Original) return "Restores the original colour of your " + part + ". Always free at the Dye Merchant.";
        string price = dye.Tier == DyeTier.Retired ? "No longer sold; owners keep it."
            : dye.Tokens > 0 ? $"{TierName(dye.Tier)} colour: {dye.Crystals:N0} Crystals or {dye.Tokens} Supporter Tokens, once."
            : $"{TierName(dye.Tier)} colour: {dye.Crystals:N0} Crystals, once.";
        return (owned ? "Owned: switch to it for free. " : "")
            + "Unlocks " + dye.Name + " for every character on your account; switching between owned colours is free at the Dye "
            + "Merchant (Prontera's central fountain). Colours your " + part + ". " + price;
    }

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
            { "_iNpcId", (long)MerchantId }, { "_iShopName", new[] { MerchantNameText } },
            { "_kCurrencyDisplay", new long[] { Crystal, Token } }
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
        { "_kIcon", "item_1001000.png" }, { "_iSort", 990L }, { "_iQuality", (long)Quality(DyeOf(Palette(item)).Tier) }
    };

    private static int Quality(DyeTier tier) => tier switch { DyeTier.Legendary => 5, DyeTier.Rare => 4, DyeTier.Common => 2, _ => 1 };

    /// <summary>
    /// The commodity rows a version-4 client shows for an account that owns <paramref name="owned"/>: every owned colour
    /// once at 0 Crystals (the free switch), every unowned colour on sale at its Crystal price, and a Rare or Legendary one
    /// a second time at its token price. Armour rows only for a body that takes armour dye.
    /// </summary>
    public static IEnumerable<KeyValuePair<int, Dictionary<string, object>>> ShopRows(long owned, bool armourSupported)
    {
        foreach (bool armour in new[] { false, true })
        {
            if (armour && !armourSupported) continue;
            foreach (var dye in Catalogue)
            {
                bool mine = Owns(owned, dye.Palette);
                if (mine) { yield return Row(dye, armour, false, 0); continue; }
                if (!dye.ForSale) continue;
                yield return Row(dye, armour, false, dye.Crystals);
                if (dye.Tokens > 0) yield return Row(dye, armour, true, dye.Tokens);
            }
        }
    }

    private static KeyValuePair<int, Dictionary<string, object>> Row(Dye dye, bool armour, bool tokens, int price)
    {
        int id = Commodity(dye.Palette, armour, tokens);
        return new KeyValuePair<int, Dictionary<string, object>>(id, new Dictionary<string, object>
        {
            { "_iID", (long)id }, { "_iClassID", (long)MerchantId }, { "_iItemId", (long)Item(dye.Palette, armour) }, { "_iItemPerMount", 1L },
            { "_kItemPerPrice", new long[] { tokens ? Token : Crystal, price } },
            { "_iDiscount", 0L }, { "_iMerchantDiscount", 0L }, { "_iPurchaseTimesLimit", 0L },
            { "_iPurchaseNum", 0L }, { "_iSortId", (long)((armour ? 1000 : 0) + (int)dye.Tier * 100 + dye.Palette * 2 + (tokens ? 1 : 0)) }
        });
    }
}
