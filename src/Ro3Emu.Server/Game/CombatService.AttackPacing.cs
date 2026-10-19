using System.Runtime.CompilerServices;
using System.Text;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Normal-attack pacing. A normal attack (_iIsNormalAtk 1) may cancel the running one (_bCancelCast 1), and its damage
/// lands at the template's attack point, early in the swing. A client that repeats the request as fast as a held key
/// restarts the swing right after each impact and deals one hit per request instead of one per swing time
/// (SkillMs, ASPD-scaled): players reported holding Normal Attack "does damage really fast".
/// Refusing the early requests (casefix.13) stalled the real client, which requests every swing itself, so every
/// request is still accepted and only the impact is judged: one sooner than NormalAttackMinGap swing times after the
/// previous normal-attack impact lands no damage. 0 only measures; the gap ratios are counted by who swung (player,
/// AI bot, the server's own attack loop) and read from the loopback POST /ro3emu/pacing.
/// </summary>
public sealed partial class CombatService
{
    private static readonly double[] GapEdges = [0.25, 0.5, 0.7, 0.85, 1.0, 1.5];
    private static readonly string[] GapKinds = ["player", "aibot", "loop"];

    /// <summary>Fraction of the swing time below which a normal-attack impact deals nothing; 0 = measure only.</summary>
    public double NormalAttackMinGap { get; set; }

    private readonly ConditionalWeakTable<GameSession, StrongBox<long>> lastNormalImpact = new();
    private readonly long[,] gapCounts = new long[3, 7];
    private readonly long[] gapSuppressed = new long[3];
    private long gapSinceMs;

    /// <summary>At a normal attack's impact: false when it comes too soon after the previous one (no damage).</summary>
    private bool PaceNormalAttack(GameSession s, ActiveSkillCast action)
    {
        if (action.Operator != null || GameData.Int(action.Row, "_iIsNormalAtk") != 1) return true;
        if (gapSinceMs == 0) gapSinceMs = Now;
        int kind = action.Automatic ? 2 : s.Account?.StartsWith("aibot", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0;
        var last = lastNormalImpact.GetOrCreateValue(s);
        double ratio = last.Value == 0 ? double.PositiveInfinity : (Now - last.Value) / (double)Math.Max(1, action.ActionMs);
        int bucket = 0;
        while (bucket < GapEdges.Length && ratio >= GapEdges[bucket]) bucket++;
        gapCounts[kind, bucket]++;
        if (NormalAttackMinGap > 0 && ratio < NormalAttackMinGap)
        {
            gapSuppressed[kind]++;
            return false;
        }
        last.Value = Now;
        return true;
    }

    /// <summary>The loopback /ro3emu/pacing report: "" reads, "set 0.5" sets the threshold, "reset" clears the counts.</summary>
    internal string PacingCommand(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["set", var value] && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double gap) && gap is >= 0 and < 1.5)
            NormalAttackMinGap = gap;
        else if (parts is ["reset"])
        {
            Array.Clear(gapCounts);
            Array.Clear(gapSuppressed);
            gapSinceMs = Now;
        }
        else if (parts.Length > 0) return "usage: (empty) | set <0..1.5> | reset";
        var text = new StringBuilder($"min gap {NormalAttackMinGap:0.##} of the swing time; counts over {(gapSinceMs == 0 ? 0 : (Now - gapSinceMs) / 60000)} min\n");
        text.Append("impact gap / swing time: <0.25 <0.5 <0.7 <0.85 <1.0 <1.5 >=1.5 | no damage\n");
        for (int kind = 0; kind < GapKinds.Length; kind++)
        {
            text.Append(GapKinds[kind].PadRight(7));
            for (int bucket = 0; bucket <= GapEdges.Length; bucket++) text.Append(' ').Append(gapCounts[kind, bucket]);
            text.Append(" | ").Append(gapSuppressed[kind]).Append('\n');
        }
        return text.ToString();
    }
}
