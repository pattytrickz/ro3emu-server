using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.AiBots;

/// <summary>A field map bots can hunt on: its monsters' level range and where they spawn.</summary>
public sealed record HuntMap(long Id, string Name, int MinLevel, int MedianLevel, IReadOnlyList<(SpawnPoint Point, int Level)> Spawns);

/// <summary>What the bots know about the world, from the game tables and nav grids (read only, shared).</summary>
public sealed class Atlas
{
    private const int SceneTypeField = 1;
    private readonly GameData _data;
    private readonly WorldData _world;
    private readonly AiBotsOptions _options;
    private readonly Lazy<IReadOnlyList<HuntMap>> _huntMaps;

    public Atlas(AiBotsOptions options)
    {
        _options = options;
        var server = Options.Create(new ServerOptions { GameDataDir = options.GameDataDir, DataDir = options.GameDataDir });
        _data = new GameData(server, NullLogger<GameData>.Instance);
        _world = new WorldData(server, _data, NullLogger<WorldData>.Instance);
        _huntMaps = new(LoadHuntMaps);
    }

    public GameData Data => _data;

    public MapWorld? Map(long sceneEntry) => _world.Map(sceneEntry);

    public string MapName(long sceneEntry)
    {
        string name = _data.MapName(sceneEntry);
        return string.IsNullOrWhiteSpace(name) || name.StartsWith('[') ? $"map {sceneEntry}" : name;
    }

    public string MonsterName(ulong templateId) =>
        _data.Row("NPCConfig", (long)templateId) is { } row ? _data.TextOf(row, "_kName") : "";

    public IReadOnlyList<HuntMap> HuntMaps => _huntMaps.Value;

    public HuntMap? Hunt(long sceneEntry) => HuntMaps.FirstOrDefault(m => m.Id == sceneEntry);

    /// <summary>
    /// A field whose monsters suit a bot of <paramref name="level"/>: of the four closest in level, the one with the
    /// fewest hunters (ties at random).
    /// </summary>
    public HuntMap? Choose(long level, Random random, Func<long, int> hunters)
    {
        var fitting = Fitting(level).Take(4).ToArray();
        return fitting.Length == 0 ? HuntMaps.OrderBy(m => m.MinLevel).FirstOrDefault()
            : fitting.OrderBy(m => hunters(m.Id)).ThenBy(_ => random.Next()).First();
    }

    /// <summary>Fields a bot of <paramref name="level"/> can take on, best level match first.</summary>
    public IEnumerable<HuntMap> Fitting(long level) =>
        HuntMaps.Where(m => m.MinLevel <= level).OrderBy(m => Math.Abs(m.MedianLevel - (level - 6)));

    private IReadOnlyList<HuntMap> LoadHuntMaps()
    {
        var maps = new List<HuntMap>();
        foreach (var (key, row) in _data.Table("MapConfig"))
        {
            if (!long.TryParse(key, out long id)) continue;
            bool listed = _options.HuntMaps.Count > 0
                ? _options.HuntMaps.Contains(id)
                : GameData.Int(row, "_iSceneType") == SceneTypeField && GameData.Int(row, "_iAllowCreateLine") == 1
                  && (GameData.Int(row, "_iWorldMapTag") > 0 || GameData.Int(row, "_iCaveBelongsTo") > 0);
            if (!listed || _world.Map(id) is not { Nav: not null, Born: not null } map) continue;
            var spawns = new List<(SpawnPoint, int)>();
            foreach (var group in map.SpawnGroups)
            {
                var levels = group.Limits.Select(l => Level(l.NpcId)).Where(l => l > 0).ToArray();
                foreach (var point in group.Points)
                {
                    int level = Level(point.ConfigId) is > 0 and var own ? own : levels.DefaultIfEmpty(0).Max();
                    if (level > 0) spawns.Add((point, level));
                }
            }
            if (spawns.Count == 0) continue;
            var sorted = spawns.Select(s => s.Item2).Order().ToArray();
            maps.Add(new HuntMap(id, MapName(id), sorted[0], sorted[sorted.Length / 2], spawns));
        }
        return maps;
    }

    private int Level(int npcId) =>
        npcId > 0 && _data.Row("NPCConfig", npcId) is { } npc && GameData.Int(npc, "_iNPCType") == 1 ? (int)GameData.Int(npc, "_iLevel") : 0;
}
