using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.ClientHooks;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>
/// The quest auto-battle hook (Ro3Emu.ClientHooks.QuestAutoBattle) reads a quest's kill objectives through
/// QuestKillTargets, linked here from the hooks project (which builds against the client's own DLLs). The Lua side is
/// the client's own Ring Task flow; these tests pin what the hook asks it to hunt and when it stops.
/// </summary>
public sealed class QuestAutoBattleTests
{
    private static readonly GameData Data = new(Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }),
        NullLogger<GameData>.Instance);

    private static Func<int, long> Counts(params long[] counts) => i => i < counts.Length ? counts[i] : 0;

    [Fact]
    public void AKillObjectiveHuntsItsMonsterOnTheTrackedMap()
    {
        // TaskConfig 10101050: [[104014, 5000001, 5]], track [["142", "10111", "1010105001", "-1"]].
        var quest = QuestKillTargets.From([[104014, 5000001, 5]], [142, 10111, 1010105001, -1])!;
        Assert.Equal(10111, quest.MapId);
        Assert.Equal("5000001", quest.MonsterIds(Counts(0)));
        Assert.False(quest.Done(Counts(4)));
        Assert.True(quest.Done(Counts(5)));
        Assert.Null(quest.MonsterIds(Counts(5)));
    }

    [Fact]
    public void SeveralMonstersInOneObjectiveAreAllHunted()
    {
        // TaskConfig 10104080: [[104014, 5000056, 5000055, 15]].
        var quest = QuestKillTargets.From([[104014, 5000056, 5000055, 15]], [142, 10514, 1010407001, -1])!;
        Assert.Equal("5000056,5000055", quest.MonsterIds(Counts(0)));
    }

    [Fact]
    public void AFinishedObjectiveLeavesOnlyTheOthersMonsters()
    {
        // TaskConfig 182190: [[104002, 5000055, 30], [104002, 5000056, 30]].
        var quest = QuestKillTargets.From([[104002, 5000055, 30], [104002, 5000056, 30]], [142, 10540, 5000055, -1])!;
        Assert.Equal("5000055,5000056", quest.MonsterIds(Counts(3, 29)));
        Assert.Equal("5000056", quest.MonsterIds(Counts(30, 29)));
        Assert.False(quest.Done(Counts(30, 29)));
        Assert.True(quest.Done(Counts(30, 30)));
    }

    [Fact]
    public void AnyMonsterOnAMapHuntsEveryEnemyThere()
    {
        // 103043 [type, map, count] (TaskConfig 24-type rows); 310001 [type, count, map] (TaskConfig 10101220).
        var onMap = QuestKillTargets.From([[103043, 10540, 20]], null)!;
        Assert.Equal(10540, onMap.MapId);
        Assert.Equal("", onMap.MonsterIds(Counts(0)));
        var lastHit = QuestKillTargets.From([[310001, 20, 10111]], [142, 10111, 1010122001, -1])!;
        Assert.Equal(10111, lastHit.MapId);
        Assert.Equal("", lastHit.MonsterIds(Counts(19)));
        Assert.True(lastHit.Done(Counts(20)));
    }

    [Fact]
    public void OtherObjectivesAreNotHuntedAndDoNotHoldTheHunt()
    {
        // A dialogue (104001) beside a kill: only the kill counts, at its own index.
        var quest = QuestKillTargets.From([[104001, 1, 2, 3], [104002, 5000001, 3]], [142, 10111, 1, -1])!;
        Assert.Equal("5000001", quest.MonsterIds(Counts(0, 0)));
        Assert.True(quest.Done(Counts(0, 3)));
        Assert.Null(QuestKillTargets.From([[104001, 1, 2, 3]], [142, 10111, 1, -1]));
        Assert.Null(QuestKillTargets.From([[104002, 5000001, 0]], null)); // no count: nothing to hunt
    }

    [Fact]
    public void ATrackWithoutAPathfindingEntryNamesNoMap()
    {
        Assert.Equal(0, QuestKillTargets.From([[104002, 4940017, 1]], [0])!.MapId);
        Assert.Equal(0, QuestKillTargets.From([[104002, 4940017, 1]], null)!.MapId);
    }

    [Theory]
    [InlineData(5000001L, 5000001L)]
    [InlineData(" 10111", 10111L)]
    [InlineData(10111.0, 10111L)]
    [InlineData("", 0L)]
    [InlineData(null, 0L)]
    public void ConfigValuesReadAsTheClientHoldsThem(object? value, long expected) =>
        Assert.Equal(expected, QuestKillTargets.Number(value!));

    /// <summary>
    /// Every quest with a kill objective in the shipped TaskConfig yields a hunt whose monsters are real NPCs and whose
    /// map is a real map; every main-story (type 1) kill quest names its map, so the on-the-map check always applies.
    /// </summary>
    [ClientDataFact]
    public void EveryKillQuestInTheConfigGivesAHuntOnRealMonstersAndMaps()
    {
        int quests = 0, story = 0;
        foreach (var (id, row) in Data.Table("TaskConfig"))
        {
            var targets = Lists(row, "_kProgressTarget");
            if (!targets.Any(t => t.Length > 0 && t[0] is QuestKillTargets.Kill or QuestKillTargets.KillInvolved
                    or QuestKillTargets.MapKill or QuestKillTargets.KillOnMap)) continue;
            var track = Lists(row, "_kTrack").FirstOrDefault();
            var quest = QuestKillTargets.From(targets, track);
            Assert.True(quest != null, $"quest {id}: kill objective not read");
            quests++;
            string ids = quest!.MonsterIds(_ => 0)!;
            Assert.NotNull(ids);
            foreach (var monster in ids.Split(',', StringSplitOptions.RemoveEmptyEntries))
                Assert.True(Data.Row("NPCConfig", long.Parse(monster)) != null, $"quest {id}: monster {monster} not in NPCConfig");
            if (quest.MapId != 0) Assert.True(Data.Row("MapConfig", quest.MapId) != null, $"quest {id}: map {quest.MapId} not in MapConfig");
            if (GameData.Int(row, "_iTaskType") == 1)
            {
                story++;
                Assert.True(quest.MapId != 0, $"story quest {id} names no map");
            }
            Assert.False(quest.Done(_ => 0));
        }
        Assert.True(quests >= 50, $"only {quests} kill quests found");
        Assert.True(story >= 30, $"only {story} story kill quests found");
    }

    /// <summary>A config list of lists, numbers or numeric strings (the client's tables hold both).</summary>
    private static List<long[]> Lists(JsonElement row, string field)
    {
        var lists = new List<long[]>();
        if (!row.TryGetProperty(field, out var outer) || outer.ValueKind != JsonValueKind.Array) return lists;
        foreach (var inner in outer.EnumerateArray())
            if (inner.ValueKind == JsonValueKind.Array)
                lists.Add(inner.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String
                    ? QuestKillTargets.Number(v.GetString()!) : (long)(GameData.Number(v) ?? 0)).ToArray());
        return lists;
    }
}
