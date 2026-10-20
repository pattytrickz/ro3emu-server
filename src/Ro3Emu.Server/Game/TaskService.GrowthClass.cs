using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Become Stronger growth classroom ("Growth Curriculum"). BecomeStrongerConfig names six modules, each opening
/// with its function (_iFunction) at its level (_iUnlocklevel). Their tasks are the 155 TaskConfig rows of type 20,
/// grouped by module (_iTaskSonType 1001-1006) and stage (_iStrongerRank 1-6), with no accept mode and an automatic
/// delivery. The panel lists every such row of the module and reads each one's progress or completion from the role's
/// own quests (LC_BecomeStrongerHelper.GetGrowthTaskDataList, LC_TaskHelper.GetTaskCompleteInfo), so the server offers
/// a module's tasks once it opens and the ordinary objective engine counts them.
///
/// DBGrowthClassInfo (AUTO_SYNC 5045, LC_DAL_GrowthClassroom) carries the rest: rankProgress, the number of completed
/// tasks ("Objectives Completed"); moduleTaskCompleteNum per module; moduleUnlockedStage per module (the client's default
/// is 1; here 1 + the stages whose tasks are all done); rankReward, the claimed ranks. A rank of
/// BecomeStrongerRankConfig is reached when the completed tasks cover its _iStandard and every earlier rank's
/// (LC_BecomeStrongerHelper.GetNeedTaskCountToNextRank subtracts them in turn); GROWTHCLASS_GET_RANK_REWARD (33830)
/// then pays its _iReward (a MultiverseGroupConfig award) once.
/// </summary>
public sealed partial class TaskService
{
    public const uint GrowthClassSync = 5045, GrowthClassRankReward = 33830;
    private const int TaskTypeGrowthClass = 20;
    private List<(long Id, long Module, long Stage)>? _growthClassTasks;

    private List<(long Id, long Module, long Stage)> GrowthClassTasks() => _growthClassTasks ??= data.Table("TaskConfig").Values
        .Where(r => r.ValueKind == System.Text.Json.JsonValueKind.Object && GameData.Int(r, "_iTaskType") == TaskTypeGrowthClass
            && GameData.Int(r, "_iTaskSonType") > 0 && GameData.Int(r, "_iStrongerRank") > 0)
        .Select(r => (GameData.Int(r, "_iTaskID"), GameData.Int(r, "_iTaskSonType"), GameData.Int(r, "_iStrongerRank")))
        .Where(t => Defs().ContainsKey(t.Item1)).OrderBy(t => t.Item1).ToList();

    /// <summary>BecomeStrongerConfig: the module's function is open and the role has its level.</summary>
    private bool GrowthModuleOpen(GameSession session, long module) =>
        data.Row("BecomeStrongerConfig", module) is { } row && session.Progress is { } progress
        && progress.Level >= GameData.Int(row, "_iUnlocklevel") && functions.IsOpen(session, GameData.Int(row, "_iFunction"));

    /// <summary>Offers the tasks of every open module; returns how many were accepted.</summary>
    private int AcceptGrowthClass(GameSession session)
    {
        int accepted = 0;
        foreach (var group in GrowthClassTasks().GroupBy(t => t.Module))
        {
            if (!GrowthModuleOpen(session, group.Key)) continue;
            foreach (var (id, _, _) in group)
            {
                if (Active(session, id) || IsDelivered(session, id)) continue;
                Accept(session, id);
                accepted++;
            }
        }
        return accepted;
    }

    /// <summary>BecomeStrongerRankConfig in id order: (rank, tasks needed in total to reach it, reward group).</summary>
    private List<(long Rank, long Needed, long Reward)> GrowthRanks()
    {
        long needed = 0;
        var ranks = new List<(long, long, long)>();
        foreach (var row in data.Table("BecomeStrongerRankConfig").Values.Where(r => r.ValueKind == System.Text.Json.JsonValueKind.Object)
                     .OrderBy(r => GameData.Int(r, "_iID")))
        {
            needed += Math.Max(0, GameData.Int(row, "_iStandard"));
            ranks.Add((GameData.Int(row, "_iID"), needed, GameData.Int(row, "_iReward")));
        }
        return ranks;
    }

    private int GrowthCompleted(GameSession session) => GrowthClassTasks().Count(t => IsDelivered(session, t.Id));

    /// <summary>The 5045 view of the role's classroom (see the class remarks).</summary>
    public DBGrowthClassInfo GrowthClassInfo(GameSession session)
    {
        var info = new DBGrowthClassInfo();
        if (session.Tasks == null) return info;
        var tasks = GrowthClassTasks();
        info.RankProgress = tasks.Count(t => IsDelivered(session, t.Id));
        foreach (var module in tasks.GroupBy(t => t.Module))
        {
            info.ModuleTaskCompleteNum[(int)module.Key] = module.Count(t => IsDelivered(session, t.Id));
            info.ModuleProgress[(int)module.Key] = info.ModuleTaskCompleteNum[(int)module.Key];
            long stage = 1, last = module.Max(t => t.Stage);
            while (stage < last && module.Where(t => t.Stage == stage).All(t => IsDelivered(session, t.Id))) stage++;
            info.ModuleUnlockedStage[(int)module.Key] = (int)stage;
        }
        foreach (long rank in session.TaskRuntime.GrowthRanksClaimed) info.RankReward[(int)rank] = 1;
        return info;
    }

    private Task SyncGrowthClassAsync(GameSession session) =>
        session.Tasks == null ? Task.CompletedTask : CharacterService.SyncAsync(session, GrowthClassSync, GrowthClassInfo(session));

    /// <summary>33830: pays a reached, unclaimed rank's reward once (the claim is saved with the grant).</summary>
    public async Task ClaimGrowthRankAsync(GameSession session, GrowthClassGetRankRewardReq req)
    {
        using var gate = await LockTasksAsync(session);
        int result;
        var rank = GrowthRanks().FirstOrDefault(r => r.Rank == req.RankId);
        if (session.Tasks == null || session.Progress == null) result = ErrorCodes.FeatureUnavailable;
        else if (rank.Rank == 0) result = ErrorCodes.Parameter;
        else if (session.TaskRuntime.GrowthRanksClaimed.Contains(rank.Rank)) result = ErrorCodes.RewardClaimed;
        else if (GrowthCompleted(session) < rank.Needed) result = ErrorCodes.TaskRequirements;
        else
        {
            var next = session.TaskRuntime.Clone();
            next.GrowthRanksClaimed.Add(rank.Rank);
            try
            {
                var plan = rank.Reward > 0 ? awards.Resolve(session, rank.Reward) : new AwardPlan(0, 0, []);
                result = await character.ApplyAwardAsync(session, plan,
                    taskRuntimeState: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(next)) ? 0 : ErrorCodes.Unknown;
            }
            catch (AwardResolutionException e)
            {
                log.LogWarning("Growth rank {Rank} reward held: {Reason}", rank.Rank, e.Message);
                result = ErrorCodes.RewardMissing;
            }
        }
        log.LogInformation("{Name} claims growth rank {Rank}: {Result}", session.Role?.Name, req.RankId, result);
        await session.SendGameAsync(GrowthClassRankReward, new GrowthClassGetRankRewardRsp { Result = result, RankId = req.RankId });
        if (result == 0) await SyncGrowthClassAsync(session);
    }
}
