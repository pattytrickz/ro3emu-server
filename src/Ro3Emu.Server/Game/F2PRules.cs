// Shared with Ro3Emu.ClientHooks, which builds for the client's runtime: keep the block namespace and explicit constructors.
#pragma warning disable IDE0161, IDE0290
namespace Ro3Emu.Shared
{
    /// <summary>
    /// House rules for a free-to-play realm (ServerOptions.F2P): Sacred Relic Crystals (item 3, the original's paid
    /// currency) are earned in play, the Crystal Shop's shortcuts are capped, and cosmetics cost about a quarter.
    /// Compiled into both the server (GameData overlay, NewActivityService.PassPlay) and the client hook
    /// (Ro3Emu.ClientHooks.ShopPrices, pass challenge previews), so the price the shop shows is the price charged.
    /// Not evidenced in RO3: the original sold Crystals for money (RechargeConfig).
    /// </summary>
    public static class F2PRules
    {
        public const int Crystal = 3;
        public const int MeowFishJerky = 1250717; // Mining Star Treasure Hunt ticket (DrawConfig 201); nothing in the data grants it

        public const int DailyChallengeCrystals = 20, WeeklyChallengeCrystals = 80, WeeklyChallengeJerky = 1;
        /// <summary>Paid for the first NewActivityService.MvpsPaidPerDay MVP fights of a day, with their Pass EXP.</summary>
        public const int MvpCrystals = 20;

        /// <summary>
        /// ItemConfig 4 "Credit Points": a currency nothing in the data grants or spends (trade credit is a separate
        /// score), renamed by the client hook. Supporters earn it 1:1 with the Crystals they earn in play (admins can also
        /// grant it, "!item 4 n"); it buys only the Supporter cosmetics below, never anything with stats.
        /// </summary>
        public const int SupporterToken = 4;
        public const long SupporterTokenNameText = 12390000245, SupporterTokenDescriptionText = 12390100004;
        public const string SupporterTokenName = "Supporter Token";
        public const string SupporterTokenDescription =
            "A thank-you for supporting the server: supporters earn one with every Crystal. Buys Supporter cosmetics in the Fashion tab; it has no other use.";

        /// <summary>ItemConfig 3/16 _iDescription: says where Crystals come from on this realm.</summary>
        public const long CrystalDescriptionText = 12390100003;
        public const string CrystalDescription =
            "Earned by playing: daily and weekly pass challenges (type /pass) and MVP fights. Spend it in the Crystal Shop and the Fashion tab.";

        /// <summary>
        /// A ShopCommodityConfig row's new price, _iPurchaseTimesLimit period (0 none, 2 weekly, 4 lifetime) and
        /// _iPurchaseNum cap (0 none). _iDiscount becomes 0: <see cref="Price"/> is final. <see cref="Payment"/> 0 keeps the
        /// row's own payment item.
        /// </summary>
        public sealed class Commodity
        {
            public readonly int Id, Price, Period, Count, Payment;
            public Commodity(int id, int price, int period, int count, int payment = 0)
            { Id = id; Price = price; Period = period; Count = count; Payment = payment; }
        }

        private const int None = 0, Weekly = 2, Lifetime = 4;

        public static readonly Commodity[] Commodities =
        {
            // Crystal Shop (class 130): shortcuts to power, capped per week.
            new Commodity(13101, 400, Weekly, 2),   // Surprise Chest (was 20 a week)
            new Commodity(13102, 10, Weekly, 20),   // Oridecon (was 150)
            new Commodity(13103, 10, Weekly, 20),   // Elunium (was 200)
            new Commodity(13104, 300, Weekly, 2),   // Blessed Weapon Ore: refine without losing levels (was unlimited)
            new Commodity(13105, 300, Weekly, 2),   // Blessed Armor Ore (was unlimited)
            new Commodity(13106, 50, Weekly, 5),    // Pet Egg: card and pet draws (was unlimited)
            new Commodity(13108, 560, Weekly, 1),   // Refinement Material Pack (700 at 80%, 5 a week)
            new Commodity(13109, 80, Weekly, 2),    // Echo Material Pack (100 at 80%, 5 a week)
            new Commodity(13110, 270, Weekly, 3),   // Zeny Bag (300 at 90%, 30 a week)
            new Commodity(13123, 1000, Weekly, 1),  // Coin Bag: 1,000,000 Minted Coin, the refine currency (was 100, unlimited)

            // Magic Fashion (class 135): emotes, scenes, profile stickers.
            new Commodity(13501, 50, Lifetime, 1), new Commodity(13502, 50, Lifetime, 1),     // emote packs (200)
            new Commodity(13503, 75, Lifetime, 1),                                            // scene (300)
            new Commodity(13504, 40, Lifetime, 1), new Commodity(13505, 40, Lifetime, 1), new Commodity(13506, 40, Lifetime, 1), // (150)
            new Commodity(13507, 15, None, 0), new Commodity(13508, 15, None, 0), new Commodity(13509, 15, None, 0),
            new Commodity(13510, 15, None, 0), new Commodity(13511, 15, None, 0), new Commodity(13512, 15, None, 0),
            new Commodity(13513, 15, None, 0),                                                // stickers (50)

            // Fashion tab (shop 502, appearances). Owner's choice (24 Oct 2026): 19 of its 27 appearances (70%) are Supporter
            // cosmetics, for Supporter Tokens only; at least one of each kind stays on Crystals, about a quarter of the
            // original. Supporters earn tokens 1:1 with their Crystals (server AccessList "supporter"), so token prices are on
            // the Crystal scale: rarer (_iQuality 5) and higher _iFashionScore appearances cost more. Appearances bought
            // with Crystals before stay owned.
            // Crystals:
            new Commodity(5000005, 450, Lifetime, 0),                                            // outfit Afternoon Stroll (1880)
            new Commodity(5000007, 160, Lifetime, 0),                                            // outfit Master of the Game (680)
            new Commodity(5000038, 250, Lifetime, 0),                                            // mount Big Poring (980)
            new Commodity(5000015, 120, Lifetime, 0),                                            // hairstyle Strawberry Macaron (480)
            new Commodity(5000020, 120, Lifetime, 0), new Commodity(5000024, 120, Lifetime, 0), // headwear Sage's Stone Helm, Black Cat Ears (480)
            new Commodity(5000032, 160, Lifetime, 0), new Commodity(5000033, 160, Lifetime, 0), // backwear Bonnie's Afternoon, Fluffy Tail (680)
            // Supporter Tokens:
            new Commodity(5000002, 500, Lifetime, 0, SupporterToken), new Commodity(5000003, 500, Lifetime, 0, SupporterToken),
            new Commodity(5000004, 500, Lifetime, 0, SupporterToken), new Commodity(5000006, 500, Lifetime, 0, SupporterToken), // outfits, fashion score 500
            new Commodity(5000008, 650, Lifetime, 0, SupporterToken), new Commodity(5000009, 650, Lifetime, 0, SupporterToken),
            new Commodity(5000013, 650, Lifetime, 0, SupporterToken),                            // mounts, fashion score 500
            new Commodity(5000010, 550, Lifetime, 0, SupporterToken), new Commodity(5000011, 550, Lifetime, 0, SupporterToken),
            new Commodity(5000012, 550, Lifetime, 0, SupporterToken), new Commodity(5000014, 550, Lifetime, 0, SupporterToken), // mounts, score 200
            new Commodity(5000017, 380, Lifetime, 0, SupporterToken), new Commodity(5000018, 380, Lifetime, 0, SupporterToken), // headwear, quality 5
            new Commodity(5000025, 150, Lifetime, 0, SupporterToken),                            // headwear, quality 4
            new Commodity(5000030, 520, Lifetime, 0, SupporterToken), new Commodity(5000031, 520, Lifetime, 0, SupporterToken), // backwear, quality 5
            new Commodity(5000034, 190, Lifetime, 0, SupporterToken), new Commodity(5000035, 190, Lifetime, 0, SupporterToken), // backwear, quality 4
            new Commodity(5000037, 320, Lifetime, 0, SupporterToken),                            // character effect Sweet Waltz
        };

        /// <summary>The Fashion tab's ShopConfig rows (shop 502): their currency bar also shows Supporter Tokens.</summary>
        public static readonly int[] FashionShopRows = { 25, 26, 33, 36, 39, 410 };

        /// <summary>
        /// A ShopCommodityConfig row the data doesn't have: a copy of <see cref="AddedTemplate"/> (a Zeny Shop row) selling
        /// one <see cref="ItemId"/> for <see cref="Price"/> Zeny, no purchase cap. Owner's choice (21 Oct 2026), not RO3:
        /// instant potions and the elemental arrows were in the data but no shop sold them.
        /// </summary>
        public sealed class AddedCommodity
        {
            public readonly int Id, ItemId, Price, Sort;
            public AddedCommodity(int id, int itemId, int price, int sort)
            { Id = id; ItemId = itemId; Price = price; Sort = sort; }
        }

        public const int Zeny = 1;
        /// <summary>Zeny Shop (ShopConfig 1201, class 120) row 12109 (Oridecon): the shape every added row copies.</summary>
        public const int AddedTemplate = 12109;

        public static readonly AddedCommodity[] Added =
        {
            // Potions that heal at once (ItemConfig _iUseEffect 100: HP or SP, % of max), 100 Zeny each.
            new AddedCommodity(12113, 1000001, 100, 101), // Orange Potion: 15% HP
            new AddedCommodity(12114, 1000002, 100, 102), // Yellow Potion: 20% HP
            new AddedCommodity(12115, 1001000, 100, 103), // Blue Potion: SP
            new AddedCommodity(12116, 1001001, 100, 104), // Condensed Blue Potion: SP
            // Elemental arrows (_kParam1 = element); the selected arrow is not used up and sets a bow attack's element.
            new AddedCommodity(12117, 1002012, 100, 111), // Fire Arrow
            new AddedCommodity(12118, 1002013, 100, 112), // Crystal Arrow (water)
            new AddedCommodity(12119, 1002015, 100, 113), // Arrow of Wind
            new AddedCommodity(12120, 1002016, 100, 114), // Stone Arrow (earth)
            new AddedCommodity(12121, 1002014, 100, 115), // Silver Arrow (holy)
            new AddedCommodity(12122, 1002011, 100, 116), // Steel Arrow
        };
    }
}
