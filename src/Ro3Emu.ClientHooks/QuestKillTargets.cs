#nullable disable
using System;
using System.Collections.Generic;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The kill objectives of one quest, read from its TaskConfig rows: what the auto-battle should hunt, on which map,
    /// and when the hunt is over. Pure data, no Lua: QuestAutoBattle feeds it the client's own config and progress, the
    /// tests feed it data/config/TaskConfig.json. The layouts are the server's (TaskObjectives.Required / the kill match):
    /// <list type="bullet">
    /// <item>104002 kill (last hit) and 104014 involved kill: [type, monster..., count];</item>
    /// <item>103043 any monster on a map: [type, map, count];</item>
    /// <item>310001 any monster on a map, last hit: [type, count, map].</item>
    /// </list>
    /// The track row (_kTrack / _kPCTrack, first entry) names the quest's map for 142 PathFindingNormal and
    /// 143 PathFindingForbidCapra: [type, map, target, ...].
    /// </summary>
    public sealed class QuestKillTargets
    {
        public const long Kill = 104002, KillInvolved = 104014, MapKill = 103043, KillOnMap = 310001;
        public const long PathFindingNormal = 142, PathFindingForbidCapra = 143;

        private readonly List<Objective> _objectives;

        /// <summary>The map the quest's monsters are on, 0 when neither a target nor the track names one.</summary>
        public long MapId { get; }

        private QuestKillTargets(List<Objective> objectives, long mapId) { _objectives = objectives; MapId = mapId; }

        private sealed class Objective
        {
            public int Index;          // into the progress list (0-based; Lua's completeNum is 1-based)
            public long[] Monsters;    // empty: any monster
            public long Required;
        }

        /// <summary>Null when the quest has no kill objective this hook can hunt.</summary>
        public static QuestKillTargets From(IList<long[]> targets, long[] track)
        {
            if (targets == null) return null;
            var objectives = new List<Objective>();
            long map = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t == null || t.Length < 3) continue;
                if ((t[0] == Kill || t[0] == KillInvolved) && t[t.Length - 1] > 0)
                {
                    var monsters = new long[t.Length - 2];
                    Array.Copy(t, 1, monsters, 0, monsters.Length);
                    if (Array.TrueForAll(monsters, m => m > 0))
                        objectives.Add(new Objective { Index = i, Monsters = monsters, Required = t[t.Length - 1] });
                }
                else if (t[0] == MapKill && t.Length == 3 && t[1] > 0 && t[2] > 0)
                {
                    objectives.Add(new Objective { Index = i, Monsters = new long[0], Required = t[2] });
                    if (map == 0) map = t[1];
                }
                else if (t[0] == KillOnMap && t.Length == 3 && t[1] > 0 && t[2] > 0)
                {
                    objectives.Add(new Objective { Index = i, Monsters = new long[0], Required = t[1] });
                    if (map == 0) map = t[2];
                }
            }
            if (objectives.Count == 0) return null;
            if (map == 0 && track != null && track.Length >= 2
                && (track[0] == PathFindingNormal || track[0] == PathFindingForbidCapra) && track[1] > 0)
                map = track[1];
            return new QuestKillTargets(objectives, map);
        }

        /// <summary>True when every kill objective has its count (progress(i) = completeNum of target i, 0-based).</summary>
        public bool Done(Func<int, long> progress)
        {
            foreach (var o in _objectives) if (progress(o.Index) < o.Required) return false;
            return true;
        }

        /// <summary>
        /// The auto-battle filter for the objectives still open: the client's monsterIds string, comma-separated
        /// (LC_AutoAttackAutoState splits on ","); "" means every enemy, which an open any-monster objective needs.
        /// Null when nothing is open.
        /// </summary>
        public string MonsterIds(Func<int, long> progress)
        {
            var ids = new List<long>();
            bool any = false;
            foreach (var o in _objectives)
            {
                if (progress(o.Index) >= o.Required) continue;
                any = true;
                if (o.Monsters.Length == 0) return "";
                foreach (var m in o.Monsters) if (!ids.Contains(m)) ids.Add(m);
            }
            return any ? string.Join(",", ids) : null;
        }

        /// <summary>A config value as the client's tables hold it (Lua integer, float or numeric string).</summary>
        public static long Number(object value) => value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            float f => (long)f,
            string s when long.TryParse(s.Trim(), out var parsed) => parsed,
            _ => 0,
        };
    }
}
