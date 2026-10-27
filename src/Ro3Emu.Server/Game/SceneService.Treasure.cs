using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    private const int TreasureNpcId = 21070002;
    /// <summary>Treasure hunt NPC UIDs: 2^46 + role id, below 2^53 (the client keeps unit ids in a double array, see
    /// DungeonService.SceneActorUidBase).</summary>
    internal const long TreasureUidPrefix = 1L << 46;
    internal Func<GameSession, Task<int>>? TreasureComplete { get; set; }
    /// <summary>The scene in which a player stands inside its hunt's area; absent outside it.</summary>
    private readonly ConditionalWeakTable<GameSession, StrongBox<long>> _inTreasureArea = [];

    private static long TreasureUid(GameSession session) => TreasureUidPrefix | session.Role!.Id;

    /// <summary>The hunt's configured map point must exist in the actual scene-editor map export.</summary>
    internal (long Scene, MapPoint Position)? TreasurePoint(long pointId)
    {
        if (data.Row("TreasureHuntMapPointConfig", pointId) is not { } row) return null;
        long mapId = Data.GameData.Int(row, "_iMapId"), mapPoint = Data.GameData.Int(row, "_iMapPointId");
        if (worldData.Map(mapId) is not { } map || !map.Points.TryGetValue(mapPoint, out var position)) return null;
        return (mapId, position);
    }

    internal MapPoint? ConfiguredPoint(long sceneEntry, long pointId) =>
        worldData.Map(sceneEntry) is { } map && map.Points.TryGetValue(pointId, out var point) ? point : null;

    /// <summary>On scene load or map use, make this player's active hunt target visible as a real NPC entity.</summary>
    internal async Task SpawnTreasureNpcAsync(GameSession session)
    {
        if (session.Role == null || session.SceneLoading || ActiveTreasurePoint(session) is not { } target
            || data.Row("NPCConfig", TreasureNpcId) is not { } npcRow) return;
        var npc = new WorldNpc(TreasureUid(session), target.Scene, WorldNpcKind.Npc, TreasureNpcId,
            data.TextOf(npcRow, "_iName"), (int)Data.GameData.Int(npcRow, "_iCamp"),
            target.Position.X, target.Position.Y, target.Position.Z, target.Position.Face, default);
        var view = new AoIViewChangeData();
        view.Bases.Add(NpcEntity(npc));
        await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
        await TreasureAreaAsync(session); // a map used, or a scene entered, inside the area
    }

    /// <summary>This player's unexpired hunt point when it lies on the current scene.</summary>
    private (long Scene, MapPoint Position)? ActiveTreasurePoint(GameSession session)
    {
        var hunt = session.ActivityRuntime.AdventureTreasure;
        return hunt.PointId == 0 || hunt.Began + 10800 <= clock.GetUtcNow().ToUnixTimeSeconds()
            || TreasurePoint(hunt.PointId) is not { } target || target.Scene != session.SceneEntry ? null : target;
    }

    /// <summary>
    /// TreasureHuntMapPointConfig _iAreaId names a region of the point's map (scene editor "寻宝区域", a 40 m circle).
    /// ETreasureEventNum InScene 103 and Leave 101 are 83251 notices, and the client has the text "You have left the
    /// treasure-digging area." Sending them when the hunter walks into and out of that region, with the dig spot as
    /// pointPosition, is (inferred).
    /// </summary>
    internal async Task TreasureAreaAsync(GameSession session)
    {
        if (session.Role == null || session.SceneLoading) return;
        bool was = _inTreasureArea.TryGetValue(session, out var scene) && scene.Value == session.SceneEntry;
        var target = ActiveTreasurePoint(session);
        bool inside = target is { } t
            && TreasureHuntMapPointConfig.Get(data, session.ActivityRuntime.AdventureTreasure.PointId) is { } point
            && worldData.Map(t.Scene)?.Regions.TryGetValue(point.AreaId, out var area) == true
            && area.Contains(session.Position.X, session.Position.Z);
        if (inside) _inTreasureArea.AddOrUpdate(session, new StrongBox<long>(session.SceneEntry));
        else _inTreasureArea.Remove(session);
        if (inside == was) return;
        var notice = new TreasureEventNotify { Type = inside ? ETreasureEventNum.InScene : ETreasureEventNum.Leave };
        if (target is { } spot)
            notice.PointPosition = new Vec3 { X = spot.Position.X, Y = spot.Position.Y, Z = spot.Position.Z };
        await session.SendGameAsync(MsgId.TreasureEvent, notice);
    }

    /// <summary>The hunt is over (dug or expired): its spot leaves the view, without an area notice.</summary>
    internal Task RemoveTreasureNpcAsync(GameSession session)
    {
        _inTreasureArea.Remove(session);
        var view = new AoIViewChangeData();
        view.LeaveAoi.Add((ulong)TreasureUid(session));
        return session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    internal Task<MonsterUnit?> SpawnTreasureMonsterAsync(GameSession session, int npcId, MapPoint at) =>
        monsters.SpawnSingleAsync(session.SceneEntry, npcId, at);

    internal Task RemoveTreasureMonsterAsync(MonsterUnit monster) => world.RemoveUnitAsync(monster);

    internal bool HasTreasureMonster(long uid) => world.AllUnits().Any(u => u.Uid == uid && u is MonsterUnit { Dead: false });

    /// <summary>
    /// Only the owner's dynamic UID takes this branch. The client sends the dig spot's own interaction, NPCConfig 21070002
    /// _iInteractID [21070002] (dig skill 7300101, status 9); ActivityConstant.TreasureHuntingInteractID 4 is the
    /// InteractPerformance row of that status (dig animation 73001, both 1600 ms), not a request id (inferred).
    /// </summary>
    private async Task<bool> TryTreasureInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (session.Role == null || req.NpcUid != TreasureUid(session)) return false;
        if (data.Row("NPCConfig", TreasureNpcId) is not { } npcRow || !HasInteraction(npcRow, req.InteractionId, WorldNpcKind.Npc))
        {
            await session.SendGameAsync(NpcInteraction, new NpcInteractionRsp { Result = ErrorCodes.NpcParameter, Req = req });
            return true;
        }
        var hunt = session.ActivityRuntime.AdventureTreasure;
        int result = hunt.PointId == 0 || TreasurePoint(hunt.PointId) is not { } target
            ? ErrorCodes.NpcMissing
            : session.SceneLoading || target.Scene != session.SceneEntry ? ErrorCodes.SwitchingScenes
            : MonsterService.Distance(session.Position.X, session.Position.Z, target.Position.X, target.Position.Z) > 4f + InteractSlack
                ? ErrorCodes.NpcTooFar : TreasureComplete == null ? ErrorCodes.FeatureUnavailable
                : await TreasureComplete(session);
        await session.SendGameAsync(NpcInteraction, new NpcInteractionRsp { Result = result, Req = req });
        return true;
    }
}
