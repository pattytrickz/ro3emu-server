using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.AiBots;

/// <summary>One job of the tree (JobConfig): Novice, the six first jobs and their second, transcendent and third jobs.</summary>
public sealed record Job(int Id, string Name, int Rank, int Parent, int Gender);

/// <summary>
/// How a bot grows, from the game tables: the job tree and the base/job level each rank needs, stat points and their
/// cost, the job's normal attack, and gear for a job and level. Read only and shared.
/// </summary>
public sealed class Progression
{
    public const int Novice = 1000;
    private const int ItemTypeEquip = 5, LevelUnobtainable = 999, MaxQuality = 4, MaxStat = 99, StartingStatPoints = 48;

    // CharacterService.TransformJobLevelLimit: base / job level needed to advance to a job of that rank.
    private static readonly Dictionary<int, (int Base, int Job)> RankNeeds = new()
    {
        [2] = (10, 10), [3] = (30, 40), [4] = (40, 40), [5] = (50, 40),
    };

    // Stat weights per first job, STR AGI VIT INT DEX LUK (AttributeConfig 1-6).
    private static readonly Dictionary<int, int[]> StatWeights = new()
    {
        [1100] = [5, 2, 4, 0, 2, 0], // Swordman: STR, VIT
        [2100] = [0, 0, 2, 6, 3, 0], // Magician: INT, DEX
        [3100] = [1, 3, 1, 0, 6, 1], // Archer: DEX, AGI
        [4100] = [1, 0, 4, 5, 2, 0], // Acolyte: INT, VIT
        [5100] = [4, 6, 1, 0, 2, 1], // Thief: AGI, STR
        [6100] = [5, 1, 3, 0, 3, 1], // Merchant: STR, VIT, DEX
        [Novice] = [3, 2, 3, 0, 2, 0],
    };

    private readonly GameData _data;
    private readonly Dictionary<int, Job> _jobs = new();
    private readonly List<(int Item, int Part, int Level, int Sex, int[] Jobs)> _gear = new();
    private readonly Dictionary<int, int> _subTypes = new();
    private readonly Dictionary<int, int> _normal = new();
    private readonly List<(int Job, int[] Weapons, int Skill)> _attacks = new();

    public Progression(GameData data)
    {
        _data = data;
        foreach (var (key, row) in data.Table("JobConfig"))
        {
            if (!int.TryParse(key, out int id)) continue;
            // _iNotTransformJob: 1 = a job you can advance to (Sage, Bard, Dancer, Monk and Rogue are not open).
            if (id != Novice && GameData.Int(row, "_iNotTransformJob") != 1) continue;
            if (row.TryGetProperty("_kSkillNormalID", out var normal) && normal.ValueKind == JsonValueKind.Array
                && normal.EnumerateArray().Select(GameData.Number).FirstOrDefault() is { } skill) _normal[id] = (int)skill;
            string name = _data.TextOf(row, "_iName");
            if (name.Length == 0 || name.StartsWith('[')) name = row.TryGetProperty("_kUniqueName", out var u) ? u.GetString() ?? key : key;
            _jobs[id] = new Job(id, name,
                (int)Math.Max(1, GameData.Int(row, "_iJobRank")), (int)GameData.Int(row, "_iParentId"),
                (int)GameData.Int(row, "_iGenderLimit"));
        }
        foreach (var (_, row) in data.Table("ItemConfig"))
        {
            if (GameData.Int(row, "_iType") != ItemTypeEquip) continue;
            int part = (int)GameData.Int(row, "_iEquipPart"), level = (int)GameData.Int(row, "_iLevelNeed");
            if (part is < 1 or > 7 || level >= LevelUnobtainable || GameData.Int(row, "_iQuality") > MaxQuality) continue;
            // Regular equipment is 2xxxxxx; the 9000xxxx pieces are special items far stronger than their level.
            if (GameData.Int(row, "_iID") is < 2_000_000 or >= 3_000_000) continue;
            // Gear that must be appraised first cannot be worn straight from the bag.
            if (data.Row("EquipConfig", GameData.Int(row, "_iEquipId")) is { } equip && GameData.Int(equip, "_iIfAppraise") == 1) continue;
            int[] jobs = row.TryGetProperty("_kJobNeed", out var need) && need.ValueKind == JsonValueKind.Array
                ? need.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : [0];
            int id = (int)GameData.Int(row, "_iID");
            _gear.Add((id, part, level, (int)GameData.Int(row, "_iSexNeed"), jobs));
            _subTypes[id] = (int)GameData.Int(row, "_iSubType");
        }
        // JobAttackConfig: a job's normal attack by weapon type ({} any, [0] bare hands, [type] or [main, off hand]).
        foreach (var (_, row) in data.Table("JobAttackConfig"))
        {
            if (GameData.Int(row, "_iMountID") != 0) continue;
            int[] weapons = row.TryGetProperty("_kWeaponType", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : [];
            _attacks.Add(((int)GameData.Int(row, "_iJobID"), weapons, (int)GameData.Int(row, "_iSkillID")));
        }
    }

    public Job? JobOf(int id) => _jobs.GetValueOrDefault(id);

    /// <summary>ItemConfig._iSubType: the weapon type JobAttackConfig keys on.</summary>
    public int WeaponType(int item) => _subTypes.GetValueOrDefault(item);

    /// <summary>
    /// The normal attack for a job holding a weapon of this type (0: bare hands) and how close it must be cast
    /// (SkillConfig._iDistanceMax, less a margin for movement).
    /// </summary>
    public IReadOnlyList<(int Skill, float Range)> Attacks(int job, int weaponType)
    {
        // The server counts only JobConfig._kSkillNormalID as learned (SkillService.NormalAttack), and for some jobs that
        // row does not exist: try the weapon's attack, then the job's, then the Novice one (AiBot keeps what works).
        var list = new List<(int, float)> { Attack(job, weaponType) };
        if (_normal.TryGetValue(job, out int normal) && _data.Row("SkillConfig", normal) != null) list.Add(WithRange(normal));
        list.Add(WithRange(1000001));
        return list.DistinctBy(a => a.Item1).ToArray();
    }

    private (int Skill, float Range) WithRange(int skill)
    {
        float range = _data.Row("SkillConfig", skill) is { } s ? GameData.Int(s, "_iDistanceMax") / 100f : 3f;
        return (skill, Math.Max(1.5f, range - 0.8f));
    }

    private (int Skill, float Range) Attack(int job, int weaponType)
    {
        var rows = _attacks.Where(a => a.Job == job).ToArray();
        var row = rows.FirstOrDefault(a => a.Weapons.Length == 2 && a.Weapons[0] == weaponType && a.Weapons[1] == 0);
        if (row.Skill == 0) row = rows.FirstOrDefault(a => a.Weapons.Length == 1 && a.Weapons[0] == weaponType);
        if (row.Skill == 0) row = rows.FirstOrDefault(a => a.Weapons.Length == 0);
        if (row.Skill == 0) row = rows.FirstOrDefault(a => a.Weapons.Length == 1 && a.Weapons[0] == 0);
        return WithRange(row.Skill != 0 ? row.Skill : 1000001);
    }

    public string JobName(int id) => _jobs.TryGetValue(id, out var j) ? j.Name : $"job {id}";

    /// <summary>The first job on the line to <paramref name="job"/> (Novice for Novice).</summary>
    public int FirstJobOf(int job) => Line(job).FirstOrDefault(j => _jobs[j].Parent == Novice, Novice);

    public IReadOnlyList<int> FirstJobs => _jobs.Values.Where(j => j.Parent == Novice).Select(j => j.Id).Order().ToArray();

    /// <summary>The highest rank a bot of this base level can hold.</summary>
    public static int RankFor(int level) => RankNeeds.Where(r => level >= r.Value.Base).Select(r => r.Key).DefaultIfEmpty(1).Max();

    public static (int Base, int Job) Needs(int rank) => RankNeeds.GetValueOrDefault(rank, (1, 1));

    /// <summary>A job of <paramref name="rank"/> descending from <paramref name="from"/> (itself if it is that high), a random branch at each step.</summary>
    public int PlanJob(int from, int rank, int sex, Random random)
    {
        int job = from;
        while (_jobs[job].Rank < rank)
        {
            var children = _jobs.Values.Where(j => j.Parent == job && (j.Gender == 0 || j.Gender == sex)).ToArray();
            if (children.Length == 0) break;
            job = children[random.Next(children.Length)].Id;
        }
        return job;
    }

    /// <summary>The jobs from Novice down to <paramref name="job"/>, Novice first.</summary>
    public List<int> Line(int job)
    {
        var line = new List<int>();
        for (int j = job; _jobs.ContainsKey(j); j = _jobs[j].Parent)
        {
            line.Insert(0, j);
            if (j == Novice) break;
        }
        return line;
    }

    /// <summary>The next job on the way from <paramref name="current"/> to <paramref name="goal"/>, or 0.</summary>
    public int NextJob(int current, int goal)
    {
        var line = Line(goal);
        int i = line.IndexOf(current);
        return i >= 0 && i + 1 < line.Count ? line[i + 1] : 0;
    }

    // ---- stats ----

    public int TotalPoints(int level) => StartingStatPoints + Enumerable.Range(2, Math.Max(0, level - 1))
        .Sum(lv => (int)(_data.Row("BaseLvConfig", lv) is { } row ? GameData.Int(row, "_iStatusPoint") : 0));

    private int StepCost(int value) => (int)(_data.Row("BasePointConfig", value) is { } row ? GameData.Int(row, "_iStatusPoint") : 0);

    /// <summary>
    /// Stats for a bot of this job line and level: one point at a time to the stat furthest behind its weight, stopping
    /// at the first point that does not fit. The same steps every time, so a higher level only ever adds points (the
    /// server refuses a lowered stat).
    /// </summary>
    public int[] Stats(int firstJob, int level)
    {
        var weights = StatWeights.GetValueOrDefault(firstJob, StatWeights[Novice]);
        var stats = new[] { 1, 1, 1, 1, 1, 1 };
        int budget = TotalPoints(level);
        while (true)
        {
            int best = -1;
            for (int i = 0; i < 6; i++)
                if (weights[i] > 0 && stats[i] < MaxStat && (best < 0 || stats[i] * weights[best] < stats[best] * weights[i])) best = i;
            if (best < 0) return stats;
            int cost = StepCost(stats[best] + 1);
            if (cost <= 0 || cost > budget) return stats;
            budget -= cost;
            stats[best]++;
        }
    }

    // ---- gear ----

    /// <summary>
    /// Gear for the job at this level: per slot the highest-level piece the job may wear (common to rare), a shield only
    /// with a one-handed weapon, two accessories. (Equipment part: 1 two-handed, 2 one-handed, 3 off hand, 4-6 armour,
    /// 7 accessory; CharacterService.GridsFor.)
    /// </summary>
    public List<(int Item, int Part)> Gear(int job, int level, int sex, Random random)
    {
        var fits = _gear.Where(g => g.Level <= level && (g.Sex == 0 || g.Sex == sex) && (g.Jobs.Contains(job) || g.Jobs.Contains(0))).ToArray();
        (int Item, int Part)? Best(params int[] parts)
        {
            var options = fits.Where(g => parts.Contains(g.Part)).ToArray();
            if (options.Length == 0) return null;
            int top = options.Max(g => g.Level);
            var pick = options.Where(g => g.Level == top).ToArray()[random.Next(options.Count(g => g.Level == top))];
            return (pick.Item, pick.Part);
        }
        var gear = new List<(int, int)>();
        if (Best(1, 2) is { } weapon)
        {
            gear.Add(weapon);
            if (weapon.Part == 2 && Best(3) is { } shield) gear.Add(shield);
        }
        foreach (int part in new[] { 4, 5, 6 }) if (Best(part) is { } piece) gear.Add(piece);
        if (Best(7) is { } accessory) { gear.Add(accessory); gear.Add(Best(7)!.Value); }
        return gear;
    }
}
