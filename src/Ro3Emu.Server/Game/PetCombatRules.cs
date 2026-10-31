using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>Client-derived pet inputs, kept separate from the inferred server combat policy.</summary>
internal static class PetCombatRules
{
    internal enum SupportKind { Buff, AttributeHeal, MagicHeal }
    internal sealed record Support(SupportKind Kind, bool SelfOnly = false);

    /// <summary>
    /// Per GameData (its tables load once): PetStarConfig by pet, star and stage, each panel by its inputs, and the
    /// PublicParamConfig id lists. A character's stats add every deployed pet's panel on each cast, hit and HP change;
    /// scanning PetStarConfig and re-reading the panel's JSON for that was the game loop's largest CPU cost
    /// (31 Oct 2026 profile: about a quarter of the loop).
    /// </summary>
    private sealed class Cache(GameData data)
    {
        internal readonly Dictionary<(int Pet, int Star, int Stage), JsonElement> Stars = StarIndex(data);
        internal readonly ConcurrentDictionary<(int Pet, int Level, int Star, int Stage), Dictionary<int, double>?> Panels = new();
        internal readonly ConcurrentDictionary<string, IReadOnlySet<int>> Params = new(StringComparer.Ordinal);

        private static Dictionary<(int, int, int), JsonElement> StarIndex(GameData data)
        {
            Dictionary<(int, int, int), JsonElement> stars = [];
            // The first row of a key wins, as the scan this replaces did.
            foreach (var row in data.Table("PetStarConfig").Values)
                stars.TryAdd(((int)GameData.Int(row, "_iPetID"), (int)GameData.Int(row, "_iStar"), (int)GameData.Int(row, "_iStage")), row);
            return stars;
        }
    }

    private static readonly ConditionalWeakTable<GameData, Cache> Caches = [];

    private static Cache For(GameData data) => Caches.GetValue(data, d => new Cache(d));

    /// <summary>
    /// (verified) LC_PetHelper.lua:1119-1237 and Lua_CFG_AttriMonsterConfig.lua:20-48 select
    /// variation * 1000 + level and PetStarConfig._iBaseAttr / 10000. The display helper uses
    /// _iAttributeVariated. (inferred) A fighting pet overlays _iBattleAttributeVariated's explicit
    /// attributes, retaining unspecified display attributes (SP), and uses the same star multiplier.
    /// No owner equipment/share formula is established by that helper.
    /// PetAttrChangeConfig (63 rows, no client reader) holds per-quality multipliers of HP / PATK / MATK (101/103/104) by
    /// star and stage; its quality-3 HP column is PetStarConfig._iBaseAttr itself. (inferred) A fighting pet uses those
    /// for the three attributes (PetBaseConfig._iQuality, star, stage = StarProgress) and _iBaseAttr for the rest, so
    /// quality 4/5 pets scale their HP far more (to 450% at quality 5 star 6) than their attack.
    /// </summary>
    internal static Dictionary<int, double>? Attributes(GameData data, int id, PetData pet)
    {
        if (data.Row("PetBaseConfig", id) is not { } cfg
            || data.Row("NPCConfig", GameData.Int(cfg, "_iMonster")) is not { } npc) return null;
        int level = Math.Max(1, pet.Level); // (verified) the client maps level zero to one; no level-99 clamp.
        if (data.Row("AttriMonsterConfig", GameData.Int(npc, "_iAttributeVariated") * 1000 + level) is not { } display)
            return null;
        var star = Star(data, id, pet);
        if (star == null) return null;
        var values = GameData.Pairs(display, "_kAttri").ToDictionary(p => (int)p.Id, p => p.Value);
        long battleType = GameData.Int(npc, "_iBattleAttributeVariated");
        if (battleType > 0)
        {
            if (data.Row("AttriMonsterConfig", battleType * 1000 + level) is not { } battle) return null;
            foreach (var (attr, value) in GameData.Pairs(battle, "_kAttri")) values[(int)attr] = value;
        }
        var unscaled = ParamIds(data, "PetNotImproveBaseAttr");
        var change = AttrChange(data, (int)GameData.Int(cfg, "_iQuality"), pet.Star, pet.StarProgress);
        foreach (int attr in values.Keys.ToArray())
            if (!unscaled.Contains(attr))
                values[attr] *= (change != null && change.TryGetValue(attr, out long perc) ? perc : GameData.Int(star.Value, "_iBaseAttr")) / 10000.0;
        // (inferred) Missing HIT/FLEE retain the existing level fallback; movement comes from NPCConfig.
        values.TryAdd(109, level + 1);
        values.TryAdd(110, level);
        values.TryAdd(115, 100); // (inferred) Neutral attack-speed base, as in CharacterService's attribute pipeline.
        values[113] = GameData.Int(npc, "_iSpeed");
        return values;
    }

    /// <summary>
    /// (verified) The pet panel's stats: the display helper's variation row at the pet's level times
    /// PetStarConfig._iBaseAttr / 10000, as above, without the battle overlay or PetAttrChangeConfig. Elena Bokba
    /// (32003) Lv.55 at star 3 (13300) shows HP 3534, SP 1463 and PDEF 34 for her row's 2657, 1100 and 25: (inferred)
    /// each is rounded up.
    /// </summary>
    internal static IReadOnlyDictionary<int, double>? PanelAttributes(GameData data, int id, PetData pet) =>
        For(data).Panels.GetOrAdd((id, Math.Max(1, pet.Level), pet.Star, pet.StarProgress), _ => Panel(data, id, pet));

    private static Dictionary<int, double>? Panel(GameData data, int id, PetData pet)
    {
        if (PetBaseConfig.Get(data, id) is not { } cfg || NPCConfig.Get(data, cfg.Monster) is not { } npc
            || AttriMonsterConfig.Get(data, npc.AttributeVariated * 1000 + Math.Max(1, pet.Level)) is not { } display
            || Star(data, id, pet) is not { } star) return null;
        var unscaled = ParamIds(data, "PetNotImproveBaseAttr");
        long scale = new PetStarConfig(star).BaseAttr;
        var values = new Dictionary<int, double>();
        foreach (var pair in display.Attri)
            if (pair is [var attr, var value, ..])
                values[(int)attr] = unscaled.Contains((int)attr) ? value : Math.Ceiling(value * scale / 10000.0);
        return values;
    }

    /// <summary>A PublicParamConfig list of attribute ids (empty when absent).</summary>
    internal static IReadOnlySet<int> ParamIds(GameData data, string name) => For(data).Params.GetOrAdd(name, key =>
        data.Table("PublicParamConfig").GetValueOrDefault(key) is { ValueKind: JsonValueKind.Array } ids
            ? ids.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToHashSet() : []);

    /// <summary>PetAttrChangeConfig._kAttrPerc of a quality, star and stage (attribute id to per-10000), or null.</summary>
    internal static Dictionary<long, long>? AttrChange(GameData data, int quality, int star, int stage)
    {
        foreach (var row in data.Table("PetAttrChangeConfig").Values)
            if (GameData.Int(row, "_iQuality") == quality && GameData.Int(row, "_iStar") == star && GameData.Int(row, "_iStage") == stage)
                return GameData.Pairs(row, "_kAttrPerc").ToDictionary(p => p.Id, p => (long)p.Value);
        return null;
    }

    internal static JsonElement? Star(GameData data, int id, PetData pet) =>
        For(data).Stars.TryGetValue((id, pet.Star, pet.StarProgress), out var row) ? row : null;

    /// <summary>
    /// (verified) These SkillConfig scripts describe allies/self, even when _kDamageParam1 is nonzero.
    /// Buff1 gives concrete duration/attribute/shield data. (inferred) Only these recovered support
    /// families are interpreted; opaque reactive/protect scripts are not treated as damage formulas.
    /// </summary>
    internal static Support? SupportFor(JsonElement skill) => (skill.TryGetProperty("_kScriptLogic", out var script)
        ? script.GetString() : "") switch
    {
        "BoLiPaoPaoTower" or "manzushouhu" or "PhantomHeal" or "YaoXianNv_PaTa"
            or "PetSkill_1_QiaoKeHou" or "MiYaBi_PaTa" or "BaXiLiSiKe_PaTa" => new(SupportKind.Buff),
        "HarvestSickle" => new(SupportKind.Buff, SelfOnly: true),
        "YaoGunPT" or "SenLinYaoJingPaTa" => new(SupportKind.AttributeHeal),
        "ChaoXiSongGe" or "Alice_Huzhu" => new(SupportKind.MagicHeal),
        _ => null,
    };

    /// <summary>
    /// (verified) Bobo/Goat/Fairy descriptions name caster HP, Phantom names each recipient's HP;
    /// their T_AXadd0_etc tuples contain [source, coefficient, attribute, 3]. Rocker uses recipient HP.
    /// (inferred) Source 0/1 means caster, 2 recipient; other formula shapes remain unsupported.
    /// Integer truncation is inferred. Bobo Lv12: floor(3363 * 650 / 10000) = 218.
    /// </summary>
    internal static long AttributeAmount(JsonElement row, StatSheet caster, StatSheet recipient)
    {
        if (!row.TryGetProperty("_kDamageFormulaExtra", out var formula) || formula.ValueKind != JsonValueKind.Array
            || formula.GetArrayLength() != 1 || formula[0].GetString() != "T_AXadd0_etc"
            || !row.TryGetProperty("_kDamageParamExtra", out var extra) || extra.ValueKind != JsonValueKind.Array
            || extra.GetArrayLength() != 1 || extra[0].ValueKind != JsonValueKind.Array || extra[0].GetArrayLength() != 4)
            return 0;
        var tuple = extra[0];
        int source = (int)(GameData.Number(tuple[0]) ?? -1), attr = (int)(GameData.Number(tuple[2]) ?? 0);
        if (source is < 0 or > 2 || GameData.Number(tuple[3]) != 3) return 0;
        return Math.Max(0, (long)Math.Floor((source == 2 ? recipient : caster)[attr]
            * (GameData.Number(tuple[1]) ?? 0) / 10000.0));
    }
}
