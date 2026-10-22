using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private async Task StartRelaxAsync(GameSession s, ActiveSkillCast action)
    {
        // Tension Relax: the description gives duration and HP per second; its matching buff's
        // flat recovery formula confirms the amount. It is active healing, independent of natural regen.
        if (GameData.Int(action.Row, "_iSkillID") != 11207) return;
        var values = Numbers(action.Row, "_kDescData");
        if (values.Length < 2 || values[0] <= 0 || values[1] <= 0) return;
        var buff = s.Buffs.FirstOrDefault(b => b.ConfigId == action.Request.SkillId);
        if (buff == null) return;
        var role = s.Role;
        var attachment = s.Attachment;
        long amount = (long)values[1];
        int ticks = (int)values[0];

        async Task HealAsync()
        {
            if (s.Role != role || !ReferenceEquals(s.Attachment, attachment) || attachment?.Cancelled == true) return;
            using var held = await CharacterService.Lock(s);
            if (s.Role != role || s.Role == null || s.Dead || s.SceneLoading || s.SceneRecoveryRequired
                || !ReferenceEquals(s.Attachment, attachment) || attachment?.Cancelled == true
                || !s.Buffs.Any(b => b.Guid == buff.Guid && b.ExpireMs > MonsterService.NowMs)) return;
            var stats = character.Stats(s);
            long actual = Math.Min(amount, Math.Max(0, stats[101] - stats[2000]));
            if (actual == 0) return;
            await character.SetHpSpAsync(s, stats[2000] + actual, null);
            foreach (var handler in Healed) handler(s, actual);
            foreach (var handler in HealedAsync) await handler(s, actual);
            await RecordDamageStatisticAsync(s, new DamageStatistics
            {
                DamageSourceId = role!.Id, DamageTargetId = role.Id, DamageValue = actual,
                DamageType = DamageType.Heal, SkillId = action.Request.SkillId,
            });
            await ToPlayerAndViewersAsync(s, SkillHit, new SkillHitInfo
            {
                SrcID = role.Id, SessionId = action.Id, SkillId = action.Request.SkillId,
                Dsts = { new DamageInfo { DstID = role.Id, Damage = actual, DamageType = DamageType.Heal, DstElement = 1 } },
            });
        }

        // (inferred) First pulse on application, then once each second: five pulses fit inside the
        // five-second buff without racing its expiry. Removal/refresh/death cancels the old pulses.
        await HealAsync();
        for (int tick = 1; tick < ticks; tick++)
            loop.ScheduleRuntime(TimeSpan.FromSeconds(tick), HealAsync);
    }
}
