using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Quest auto-battle: a player who clicks a quest with a kill objective walks to its area as before and, on
    /// arrival, the client's own Auto Battle starts on that quest's monsters; it stops once the kills are done.
    /// The client already does this, but only for Ring Tasks (LC_System_Task, decompiled):
    /// <list type="bullet">
    /// <item>clicking a quest raises StartTrackTask -> _startTrackTask, which calls self:ChangeTrackState(id, true);</item>
    /// <item>arriving raises _onAutoFindSuccessful -> self:CheckRingTaskExecute(m_iLastTrackTaskId), which for a ring
    /// task in full-auto mode calls LC_AutoAttackHelper:SetMonsterIdAndStartAutoBattle(monster) for each kill target
    /// (_processTaskProgressExecute);</item>
    /// <item>_onRingTaskSuccess / _clearRingTaskTrackContext set autoBattleInfo.isAutoBattle = false again.</item>
    /// </list>
    /// Both methods are looked up through self at call time, so they are wrapped on the class table (BaseClass's
    /// __newindex writes the shared vtbl the live system reads). ChangeTrackState(id, true) arms the clicked quest; the
    /// next CheckRingTaskExecute for that id (the arrival) starts the hunt once, so a later unrelated walk never does.
    /// The hunt also needs the player on the quest's map (QuestKillTargets.MapId), an unfinished kill objective, and a
    /// quest that is not a ring task (those keep the client's own flow).
    /// SetMonsterIdAndStartAutoBattle overwrites the player's saved target filter (monsterIds / isAllEnemy, kept in
    /// PlayerPrefs by LC_DAL_AutoAttack.SaveAutoBattleSettings), so the hunt remembers the player's own filter and puts it
    /// back when it ends. Tick ends the hunt when the kills are done, the quest is gone or finished, or the player is on
    /// another map: Auto Battle off, the player's filter back, SK_AutoAttackSetting raised (as the ring flow does). A
    /// player who changes the filter in the settings panel takes over: the hunt is dropped without touching it. A player
    /// who only switches Auto Battle off keeps the quest filter until the quest is done, so switching it back on resumes
    /// the hunt. The hunt is also recorded in Unity's PlayerPrefs, so a game closed mid-hunt gets its filter back on the
    /// next login.
    /// </summary>
    public static class QuestAutoBattle
    {
        public delegate void TaskCheck(object self, object taskId);
        public delegate void TrackChange(object self, object taskId, object tracking);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly TaskCheck CheckWrapper = CheckRingTaskExecute;
        private static readonly TrackChange TrackWrapper = ChangeTrackState;

        private const string Log = "[Ro3Emu.QuestAutoBattle] ";
        private const string PrefActive = "Ro3Emu.QuestHunt.Active", PrefIds = "Ro3Emu.QuestHunt.Ids",
            PrefPrevIds = "Ro3Emu.QuestHunt.PrevIds", PrefPrevAll = "Ro3Emu.QuestHunt.PrevAll";
        /// <summary>Tick runs every 10th frame (UiTweaks); this checks about twice a second.</summary>
        private const int CheckEvery = 3;

        private sealed class Hunt
        {
            public long TaskId, MapId;
            public QuestKillTargets Targets;
            public string Ids;          // the filter the hunt set
            public string PrevIds;      // the player's own filter
            public bool PrevAll;
        }

        private static Obf_R.Obf_ff _global;
        private static Obf_R.Obf_Ef _originalCheck, _originalTrack;
        private static bool _wrapped;
        private static long _armed;
        private static Hunt _hunt;
        private static int _tick;

        /// <summary>From LuaPatches.Tick, once per LuaEnv. The task system loads later; Tick wraps it then.</summary>
        internal static void Install(Obf_R.Obf_ff global)
        {
            _global = global;
            _wrapped = false;
            _originalCheck = _originalTrack = null;
            _armed = 0;
            _hunt = null; // its Lua side went with the old LuaEnv; the PlayerPrefs record restores the filter
        }

        /// <summary>From UiTweaks.Tick (every 10th frame).</summary>
        internal static void Tick()
        {
            if (_global == null || ++_tick % CheckEvery != 0) return;
            try
            {
                if (!_wrapped) Wrap();
                if (!_wrapped) return;
                if (_hunt != null) Follow(_hunt);
                else RecoverRecord();
            }
            catch (Exception e)
            {
                _hunt = null;
                Once("tick: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Wrap()
        {
            var system = _global.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lc_system_task");
            if (system == null || Helper() == null || Dal() == null) return;
            _originalCheck = system.Obf_RuA<Obf_R.Obf_Ef>("CheckRingTaskExecute");
            _originalTrack = system.Obf_RuA<Obf_R.Obf_Ef>("ChangeTrackState");
            _wrapped = true;
            if (_originalCheck == null || _originalTrack == null)
            {
                Debug.LogWarning(Log + "LC_System_Task methods not found; quest auto-battle is off");
                return;
            }
            system.Obf_luA<string, TaskCheck>("CheckRingTaskExecute", CheckWrapper);
            system.Obf_luA<string, TrackChange>("ChangeTrackState", TrackWrapper);
            Debug.Log(Log + "installed");
        }

        /// <summary>LC_System_Task:ChangeTrackState(id, tracking): the original, then arms the clicked quest.</summary>
        private static void ChangeTrackState(object self, object taskId, object tracking)
        {
            _originalTrack?.Obf_HuA(self, taskId, tracking);
            if (tracking is bool on && on) _armed = QuestKillTargets.Number(taskId);
        }

        /// <summary>LC_System_Task:CheckRingTaskExecute(id): the original (ring tasks), then the armed quest's hunt.</summary>
        private static void CheckRingTaskExecute(object self, object taskId)
        {
            _originalCheck?.Obf_HuA(self, taskId);
            long id = QuestKillTargets.Number(taskId);
            if (id <= 0 || id != _armed) return;
            _armed = 0;
            try { Start(self as Obf_R.Obf_ff, id); }
            catch (Exception e) { Once("start: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Start(Obf_R.Obf_ff system, long id)
        {
            var info = TaskInfo(id);
            if (info == null || system == null || Finished(id)) return;
            if (First(system.Obf_RuA<Obf_R.Obf_Ef>("_isRingTask")?.Obf_HuA(system, info)) is bool ring && ring) return;
            var targets = Targets(id);
            if (targets == null) return;
            Func<int, long> progress = Progress(info);
            if (targets.Done(progress)) return;
            long map = CurrentMap();
            if (targets.MapId != 0 && map != targets.MapId)
            {
                Debug.Log(Log + "quest " + id + ": not on its map (" + map + ", quest map " + targets.MapId + ")");
                return;
            }
            var ids = targets.MonsterIds(progress);
            var battle = BattleInfo();
            if (ids == null || battle == null) return;
            // A hunt still on (its quest left unfinished) already holds the player's own filter.
            string prevIds = _hunt != null ? _hunt.PrevIds : battle.Obf_RuA<object>("monsterIds") as string ?? "";
            bool prevAll = _hunt != null ? _hunt.PrevAll : battle.Obf_RuA<object>("isAllEnemy") is bool all && all;
            var helper = Helper();
            helper.Obf_RuA<Obf_R.Obf_Ef>("SetMonsterIdAndStartAutoBattle").Obf_HuA(helper, ids);
            _hunt = new Hunt { TaskId = id, MapId = targets.MapId, Targets = targets, Ids = ids, PrevIds = prevIds, PrevAll = prevAll };
            PlayerPrefs.SetInt(PrefActive, 1);
            PlayerPrefs.SetString(PrefIds, ids);
            PlayerPrefs.SetString(PrefPrevIds, prevIds);
            PlayerPrefs.SetInt(PrefPrevAll, prevAll ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log(Log + "quest " + id + ": auto-battle on " + (ids.Length == 0 ? "every enemy" : ids) + " (map " + map + ")");
        }

        private static void Follow(Hunt hunt)
        {
            var battle = BattleInfo();
            if (battle == null) return;
            string current = battle.Obf_RuA<object>("monsterIds") as string ?? "";
            if (current != hunt.Ids)
            {
                Debug.Log(Log + "quest " + hunt.TaskId + ": target filter changed by the player; hunt dropped");
                Forget();
                return;
            }
            var info = TaskInfo(hunt.TaskId);
            Func<int, long> progress = info == null ? (i => 0) : Progress(info);
            long map = CurrentMap();
            string reason = info == null ? "quest gone" : Finished(hunt.TaskId) || hunt.Targets.Done(progress) ? "kills done"
                : hunt.MapId != 0 && map != 0 && map != hunt.MapId ? "left the quest map" : null;
            if (reason != null)
            {
                battle.Obf_luA<string, object>("isAutoBattle", false);
                Restore(battle, hunt.PrevIds, hunt.PrevAll);
                Debug.Log(Log + "quest " + hunt.TaskId + ": " + reason + "; auto-battle off, the player's filter back");
                Forget();
                return;
            }
            // One objective of several done: hunt only what is left.
            var left = hunt.Targets.MonsterIds(progress);
            if (left != null && left != hunt.Ids)
            {
                battle.Obf_luA<string, object>("monsterIds", left);
                battle.Obf_luA<string, object>("isAllEnemy", left.Length == 0);
                Save(battle);
                hunt.Ids = left;
                PlayerPrefs.SetString(PrefIds, left);
                PlayerPrefs.Save();
            }
        }

        /// <summary>A game closed mid-hunt: the saved filter still is the quest's, so the player's goes back.</summary>
        private static void RecoverRecord()
        {
            if (PlayerPrefs.GetInt(PrefActive, 0) != 1) return;
            var battle = BattleInfo();
            if (battle == null) return; // not in the world yet
            string current = battle.Obf_RuA<object>("monsterIds") as string ?? "";
            bool on = battle.Obf_RuA<object>("isAutoBattle") is bool b && b;
            if (!on && current == PlayerPrefs.GetString(PrefIds, ""))
            {
                Restore(battle, PlayerPrefs.GetString(PrefPrevIds, ""), PlayerPrefs.GetInt(PrefPrevAll, 0) == 1);
                Debug.Log(Log + "the player's target filter put back after an unfinished hunt");
            }
            Forget();
        }

        private static void Restore(Obf_R.Obf_ff battle, string ids, bool all)
        {
            battle.Obf_luA<string, object>("monsterIds", ids ?? "");
            battle.Obf_luA<string, object>("isAllEnemy", all);
            Save(battle);
        }

        /// <summary>LC_DAL_AutoAttack:SaveAutoBattleSettings(info), then SK_AutoAttackSetting (the actor rereads it).</summary>
        private static void Save(Obf_R.Obf_ff battle)
        {
            var dal = Dal();
            dal.Obf_RuA<Obf_R.Obf_Ef>("SaveAutoBattleSettings").Obf_HuA(dal, battle);
            var events = _global.Obf_RuA<Obf_R.Obf_ff>("LC_Event");
            events?.Obf_RuA<Obf_R.Obf_Ef>("DispatchEvent")?.Obf_HuA(events, _global.Obf_LuA<object>("LC_ModuleId.Battle"),
                _global.Obf_LuA<object>("LC_NotifyId.Battle.SK_AutoAttackSetting"));
        }

        private static void Forget()
        {
            _hunt = null;
            PlayerPrefs.DeleteKey(PrefActive);
            PlayerPrefs.DeleteKey(PrefIds);
            PlayerPrefs.DeleteKey(PrefPrevIds);
            PlayerPrefs.DeleteKey(PrefPrevAll);
            PlayerPrefs.Save();
        }

        private static Obf_R.Obf_ff Helper() => _global.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lc_autoattackhelper");
        private static Obf_R.Obf_ff Dal() => _global.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lc_dal_autoattack");

        /// <summary>LC_DAL_AutoAttack:GetAutoBattleInfo(): the live settings table (nil before the login pipeline).</summary>
        private static Obf_R.Obf_ff BattleInfo()
        {
            var dal = Dal();
            return First(dal?.Obf_RuA<Obf_R.Obf_Ef>("GetAutoBattleInfo")?.Obf_HuA(dal)) as Obf_R.Obf_ff;
        }

        /// <summary>LC_TaskHelper.GetTaskInfo(id): AllTaskInfo.TaskInfos[id] (status, completeNum), nil when not held.</summary>
        private static Obf_R.Obf_ff TaskInfo(long id) =>
            First(_global.Obf_LuA<Obf_R.Obf_Ef>("LC_TaskHelper.GetTaskInfo")?.Obf_HuA(id)) as Obf_R.Obf_ff;

        private static bool Finished(long id) =>
            First(_global.Obf_LuA<Obf_R.Obf_Ef>("LC_TaskHelper.IsTaskFinishOrDelivery")?.Obf_HuA(id)) is bool done && done;

        /// <summary>LC_AutoAttackHelper.GetNowMapIdOrNil(): 0 while unknown (a loading screen).</summary>
        private static long CurrentMap() =>
            QuestKillTargets.Number(First(Helper()?.Obf_RuA<Obf_R.Obf_Ef>("GetNowMapIdOrNil")?.Obf_HuA()));

        /// <summary>completeNum[i + 1] of the task info (Lua's list is 1-based).</summary>
        private static Func<int, long> Progress(Obf_R.Obf_ff info)
        {
            var counts = info.Obf_RuA<Obf_R.Obf_ff>("completeNum");
            return i => counts == null ? 0 : QuestKillTargets.Number(counts.Obf_ruA<long, object>(i + 1));
        }

        /// <summary>m_kCFG_TaskConfig:GetTaskProgressTarget(id) and :GetTaskTrack(id), as the client's own code reads them.</summary>
        private static QuestKillTargets Targets(long id)
        {
            var db = _global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
            var manager = First(db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db)) as Obf_R.Obf_ff;
            var config = manager?.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_TaskConfig");
            if (config == null) return null;
            var targets = Rows(First(config.Obf_RuA<Obf_R.Obf_Ef>("GetTaskProgressTarget")?.Obf_HuA(config, id)) as Obf_R.Obf_ff);
            var track = Rows(First(config.Obf_RuA<Obf_R.Obf_Ef>("GetTaskTrack")?.Obf_HuA(config, id)) as Obf_R.Obf_ff);
            return QuestKillTargets.From(targets, track.Count > 0 ? track[0] : null);
        }

        /// <summary>A Lua list of lists of numbers.</summary>
        private static List<long[]> Rows(Obf_R.Obf_ff list)
        {
            var rows = new List<long[]>();
            for (long i = 1; list != null; i++)
            {
                if (!(list.Obf_ruA<long, object>(i) is Obf_R.Obf_ff row)) break;
                var values = new List<long>();
                for (long j = 1; ; j++)
                {
                    object value = row.Obf_ruA<long, object>(j);
                    if (value == null) break;
                    values.Add(QuestKillTargets.Number(value));
                }
                rows.Add(values.ToArray());
            }
            return rows;
        }

        private static object First(object[] results) => results != null && results.Length > 0 ? results[0] : null;

        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static void Once(string message) { if (Reported.Add(message)) Debug.LogWarning(Log + message); }
    }
}
