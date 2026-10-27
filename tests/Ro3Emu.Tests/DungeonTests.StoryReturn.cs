using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    /// <summary>
    /// A cleared story instance (DungeonConfig _iGamePlayType 6) returns its player to the map: the next quest's plot
    /// waits on the field (1051001 "Ferocious Mother Bird" → 10103130's plot 130141 in 10510), and players stayed
    /// inside not knowing they had to leave. A banner notice first, then the return after StoryReturnSeconds. Desert
    /// Crossroad (elite) is one wave with one monster (Payon Forest 1022001 plays as a scripted scene since the
    /// 26 Oct upstream sync; it returns the same way, through the same finish).
    /// </summary>
    [ClientDataFact]
    public async Task ClearedStoryInstanceAnnouncesAndReturnsThePlayerToTheMap()
    {
        const long story = 1051051;
        Assert.True(DungeonService.StoryInstance(Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = story })!));
        await using var alice = await InWorld("Alice");
        long outside = Session(alice).SceneEntry;
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = story }));
        long instance = await Load(alice, story);
        var boss = await BossIn(instance, 5000022); // the elite Scorpion (dungeon-monsters.json)
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice),
            new MapPoint(boss.X + 1, boss.Y, boss.Z, boss.Face)));
        await Kill(alice, boss);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);

        var notice = await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser, n => n.Content.StartsWith("Area cleared"));
        Assert.Contains($"{DungeonService.StoryReturnSeconds} seconds", notice.Content);
        Assert.Equal(instance, Session(alice).InstanceId); // not before the countdown

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(DungeonService.StoryReturnSeconds + 5);
        while (Session(alice).InstanceId != 0 && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.Equal(0, Session(alice).InstanceId);
        Assert.Equal(outside, Session(alice).SceneEntry);
    }

    [ClientDataFact]
    public void OnlySoloStoryInstancesReturnOnTheirOwn()
    {
        var catalog = Service<DungeonCatalog>();
        Assert.True(DungeonService.StoryInstance(catalog.Resolve(new EnterDungeonReq { SceneEntry = 1051051 })!));
        // A multiplayer boss run keeps its own exit (vote, auction, leave button).
        Assert.False(DungeonService.StoryInstance(catalog.Resolve(Request(100101, 1, 3020101))!));
    }
}
