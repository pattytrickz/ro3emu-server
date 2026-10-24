// Writes what the Sol RO3 website's wiki (apps/solro3/scripts/build-*-data.py in GEM11) needs from the server itself:
// every map's monster spawns, NPCs, portals and Kafras, read with the server's own scene-editor parser, and the shop
// catalogue as the live realm prices it (F2P and OpenContent on, as in production), so the wiki shows what players meet.
// Usage: WikiExport <exported data dir> <output .json>
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: WikiExport <exported data dir> <output .json>");
    return 2;
}
var options = new ServerOptions { GameDataDir = args[0], DataDir = args[0] };
options.F2P.Enabled = true;
options.OpenContent.Enabled = true;
var server = Options.Create(options);
var data = new GameData(server, NullLogger<GameData>.Instance);
var world = new WorldData(server, data, NullLogger<WorldData>.Instance);

var maps = new List<object>();
foreach (var (table, kind) in new[] { ("MapConfig", "field"), ("DungeonConfig", "dungeon") })
{
    foreach (string key in data.Table(table).Keys)
    {
        if (!long.TryParse(key, out long scene) || world.Map(scene) is not { } map) continue;
        var groups = map.SpawnGroups.Where(g => g.Points.Count > 0).Select(g => new
        {
            id = g.Id,
            respawn = g.RespawnSeconds,
            // A pool keeps min-max of each kind alive; fixed points keep Total of them alive at once.
            monsters = g.Pool
                ? g.Limits.Select(l => new { npc = l.NpcId, min = Capped(g, l.Min), max = Capped(g, l.Max) }).ToArray()
                : g.Points.GroupBy(p => p.ConfigId).Select(p => new
                {
                    npc = p.Key,
                    min = Alive(g, p.Count()),
                    max = Alive(g, p.Count()),
                }).ToArray(),
            // Where the group spawns: a pool's monsters take any of its points.
            points = g.Points.Select(p => new[] { MathF.Round(p.X, 1), MathF.Round(p.Z, 1) }).ToArray(),
        }).Where(g => g.monsters.Length > 0).ToArray();
        var npcs = map.Npcs.Select(n => new
        {
            npc = n.NpcId,
            kind = n.Kind.ToString().ToLowerInvariant(),
            name = n.Name,
            x = MathF.Round(n.X, 1),
            z = MathF.Round(n.Z, 1),
            to = n.Target?.Map ?? 0,
        }).ToArray();
        if (groups.Length == 0 && npcs.Length == 0) continue;
        maps.Add(new { scene, kind, name = data.MapName(scene), width = map.Nav?.Width ?? 0, height = map.Nav?.Height ?? 0, groups, npcs });
    }
}
// The commodity table after GameData's F2P overlay (Sol RO3 prices and caps, and the Zeny Shop rows it adds).
var shop = data.Table("ShopCommodityConfig").Values.ToArray();
File.WriteAllText(args[1], JsonSerializer.Serialize(new { maps, shop, openFunctions = options.OpenContent.Functions }));
Console.WriteLine($"{maps.Count} maps, {shop.Length} shop commodities -> {args[1]}");
return 0;

// A pool spawns each kind's count on its own free points, so together they never exceed the group's points.
static int Capped(SpawnGroup group, int count)
{
    int wanted = group.Limits.Sum(l => l.Max);
    return wanted <= group.Points.Count ? count : (int)Math.Round(count * (double)group.Points.Count / wanted);
}

// Fixed points: the group keeps Total alive, chosen at random among its points, so each kind's share is proportional.
static int Alive(SpawnGroup group, int points)
{
    int alive = Math.Min(Math.Max(1, group.Total), group.Points.Count);
    return Math.Max(1, (int)Math.Round(points * (double)alive / group.Points.Count));
}
