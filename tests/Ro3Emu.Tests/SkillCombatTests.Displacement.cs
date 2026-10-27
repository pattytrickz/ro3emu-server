using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// SkillConfig._bMovement is enum_SkillLockMoveType (Lua_GameDefine.lua: 0 Lock, 1 UnLock, 2 WalkUnLock), "may the
/// caster walk while casting" (LC_Skill_RunTimeContext CheckCanSkillMove), never a displacement. Displacements are the
/// m_kPoint.m_bMovement EffectData of the skill's show, which 31112 must name (SkillStartMove ignores effectId 0).
/// </summary>
public sealed partial class SkillCombatTests
{
    private CombatService Combat => _app.Services.GetRequiredService<CombatService>();
    private static string Script(System.Text.Json.JsonElement row) =>
        row.TryGetProperty("_kScriptLogic", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";

    [ClientDataFact]
    public void DisplacementEffectsAreTheShowsMovementEffectData()
    {
        Assert.Equal((1510403, 180), Combat.MovementEffect(15104));    // BackSlide
        Assert.Equal((1300504, 100), Combat.MovementEffect(13005));    // ArrowRepel
        Assert.Equal((1332606, 160), Combat.MovementEffect(13326));    // WolvesDash
        Assert.Equal((1631405, 200), Combat.MovementEffect(16314));    // PileBunker knockback, not 1631406
        Assert.Equal((11308105, 250), Combat.MovementEffect(113081));  // PhantomThrust pull (8-digit show)
        Assert.Equal((1542903, 180), Combat.MovementEffect(15429));    // RetreatingShadows
        Assert.Equal((0, 0), Combat.MovementEffect(15112));            // Disguise: _bMovement 1, no displacement
        var data = _app.Services.GetRequiredService<GameData>();
        var skills = data.Table("SkillConfig").Values.ToList();
        // Every displacement rule meets a skill whose show has its effect and whose description has the distance.
        foreach (var (script, _) in CombatService.Displacements)
            Assert.Contains(skills, r => Script(r) == script
                && Combat.MovementEffect(GameData.Int(r, "_iScriptShow")).Id != 0);
        // The skills the old reading moved (Stasis, Choral, VolleyArrows, ShieldsProtection...) are no rules.
        var unlocked = skills.Where(r => GameData.Int(r, "_bMovement") == 1).Select(r => Script(r)).ToHashSet();
        Assert.True(unlocked.Count > 40);
        Assert.Equal(["BackSlide", "RetreatingShadows", "RetreatingShadowsS1"],
            unlocked.Where(CombatService.Displacements.ContainsKey).Order().ToArray());
    }

    [ClientDataFact]
    public async Task AnUnlockedMovementSkillDoesNotMoveItsCaster()
    {
        // Disguise 1511201: _bMovement 1 and _kRange [100,...]: the old reading moved the caster 1 m.
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5200"); await Teach("alice", 15112);
        var session = Session("alice");
        var start = session.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1511201, Face = 90 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var deadline = DateTime.UtcNow + Wait;
        while (await Loop.InvokeAsync(() => Task.FromResult(session.SkillCast is { Resolved: false })) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(100);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillMove));
        await Loop.InvokeAsync(() => { Assert.Equal(start, session.Position); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task ArrowRepelKnocksTheTargetBackWithItsEffect()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100"); await Teach("alice", 13005);
        var session = Session("alice");
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        var start = session.Position.Clone();
        // A stationary target 2 m away with walkable ground behind it.
        var (dx, dz) = new[] { (2f, 0f), (-2f, 0f), (0f, 2f), (0f, -2f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * 3, start.Z + o.Item2 * 3));
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            target.X = start.X + dx; target.Z = start.Z + dz; target.Hp = 1000000;
            return Task.CompletedTask;
        });
        var before = target.Pos;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1300501, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)target.Uid);
        Assert.Equal(1300504, moved.EffectId);
        Assert.Equal(2, moved.MoveType); // enum_SkillMoveType.Repel
        Assert.Equal(100, moved.MoveTimeMs);
        Assert.Equal(before, moved.StartPos);
        float pushed = MonsterService.Distance(before.X, before.Z, moved.EndPos.X, moved.EndPos.Z);
        Assert.InRange(pushed, 0.1f, 6.01f); // _kDescData[3] = 6 m knockback
        Assert.True(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z) > 2f);
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, target.Pos); return Task.CompletedTask; });
    }

    /// <summary>The first of four flat directions with walkable ground for <paramref name="distance"/> metres.</summary>
    private (float X, float Z) WalkableDirection(GameSession session, float distance)
    {
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        var start = session.Position;
        return new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }.First(d => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + d.Item1 * distance, start.Z + d.Item2 * distance));
    }

    /// <summary>
    /// Dark Illusion Lv 1 (1530101, hesehuanying, show 15401): "Charges to a point ${5} meters in front of the target ST
    /// enemy within ${4} meters" (_kDescData ${4} 12, ${5} 1.5). The show's displacement EffectData is 1540104 (100 ms).
    /// Reported: the skill deals damage but its caster stays where it was.
    /// </summary>
    [ClientDataFact]
    public async Task DarkIllusionChargesToAPointInFrontOfItsTarget()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15301);
        var session = Session("alice");
        var start = session.Position.Clone();
        var (dx, dz) = WalkableDirection(session, 7);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            target.Moving = false; target.Hp = 1_000_000;
            target.X = start.X + dx * 6; target.Y = start.Y; target.Z = start.Z + dz * 6;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530101, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == alice.Self!.Uuid);
        Assert.Equal(start, moved.StartPos);
        Assert.Equal(1540104, moved.EffectId);
        Assert.Equal(100, moved.MoveTimeMs);
        Assert.InRange(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z), 4.4f, 4.51f);
        Assert.InRange(MonsterService.Distance(target.X, target.Z, moved.EndPos.X, moved.EndPos.Z), 1.49f, 1.6f);
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, session.Position); return Task.CompletedTask; });
    }

    /// <summary>
    /// Flowing Shadow Lv 1 (1532101, yingliu, show 15421): "Choose a direction to dash ${1} meters as a phantom, dealing
    /// ... to up to @{1} enemies in a ${2}-meter-wide, ${3}-meter-long rectangular area in that direction" (_kDescData
    /// 7, 3, 9). Its row's own area is a 1 m fixed circle (_kRange [100]). One enemy stands 5 m ahead, one 5 m to the
    /// side: the caster dashes with effect 1542101 (150 ms) and only the one ahead is struck.
    /// </summary>
    [ClientDataFact]
    public async Task FlowingShadowDashesAndStrikesTheEnemiesInItsPath()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400"); await Teach("alice", 15321);
        var session = Session("alice");
        var start = session.Position.Clone();
        var (dx, dz) = WalkableDirection(session, 7);
        int face = (int)MathF.Round(MathF.Atan2(dx, dz) * 180 / MathF.PI);
        var ahead = await Target("alice", stationary: true);
        var aside = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            var map = _app.Services.GetRequiredService<World>().Map(session.WorldScene);
            foreach (var other in map.Units.OfType<MonsterUnit>().Where(m => m != ahead && m != aside))
            {
                other.Moving = false; other.X = start.X + 100; other.Z = start.Z + 100;
            }
            foreach (var m in new[] { ahead, aside }) { m.Moving = false; m.Hp = 1_000_000; m.Y = start.Y; }
            ahead.X = start.X + dx * 5; ahead.Z = start.Z + dz * 5;
            aside.X = start.X + dz * 5; aside.Z = start.Z - dx * 5;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532101, Face = face });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var moved = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == alice.Self!.Uuid);
        Assert.Equal(1542101, moved.EffectId);
        Assert.Equal(150, moved.MoveTimeMs);
        Assert.InRange(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z), 6.9f, 7.01f);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal([ahead.Uid], hit.Dsts.Where(d => d.Damage > 0).Select(d => d.DstID).ToArray());
    }
}
