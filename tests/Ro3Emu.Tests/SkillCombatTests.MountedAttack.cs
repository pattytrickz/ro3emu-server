using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // Discord 26 Oct: mounted Rune Knight Basic Attack and Ranger Warg Strike both showed 0/1.
    [ClientDataTheory]
    [InlineData(1400, 1500205, 11307, 1001101, 1100001, 0)]
    [InlineData(3400, 1500400, 13310, 1000001, 1003201, 0)]
    [InlineData(3400, 1500400, 13310, 1300001, 1003201, 2110120)]
    public async Task MountedAttackIsLearnedBindableAndRestoredAcrossRelogAndDismount(
        int career, int mount, int ridingSkill, int walkingAttack, int mountedAttack, int weapon)
    {
        var skills = _app.Services.GetRequiredService<SkillService>();
        await using (var alice = await InWorld("Alice"))
        {
            await Gm("alice", "!lvl 99 40");
            await Gm("alice", $"!job {career}");
            await Teach("alice", ridingSkill);
            if (weapon > 0) await EquipMountedTestWeapon(alice, weapon);
            await BindMountedTestAttack(alice, walkingAttack / 100);
            await alice.SendGameAsync(CareerSpecialService.Lease, new LeaseCareerSpecialItemReq { CartRemouldCfgId = mount });
            Assert.Equal(0, (await Next(alice, CareerSpecialService.Lease, LeaseCareerSpecialItemRsp.Parser)).ErrCode);
            Assert.Equal(walkingAttack, await Loop.InvokeAsync(() => Task.FromResult(skills.NormalAttack(Session("alice")))));
            await alice.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 });
            Assert.Equal(0, (await Next(alice, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
            var mounted = await Loop.InvokeAsync(() => Task.FromResult(skills.Build(Session("alice"))));
            AssertMountedAttack(mounted, mountedAttack);
            await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
                m => m.HasCurrentAtkSkillId && m.CurrentAtkSkillId == mountedAttack);
            await BindMountedTestAttack(alice, mountedAttack / 100);
            var target = await Target("alice", stationary: true);
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = mountedAttack, TargetId = target.Uid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == mountedAttack);
        }
        await using var again = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await again.LoginAsync("alice", "pw");
        await again.EnterGateAsync();
        await again.EnterWorldAsync(again.Roles.Single().Id);
        var restored = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.Equal(mountedAttack, restored.CurrentAtkSkillId);
        Assert.Equal(mountedAttack, restored.SkillWheel.All.WhellSkills[6].SkillConfigId);
        await again.SendGameAsync(CareerSpecialService.CancelLease, new CancelLeaseCareerSpecialItemReq { CartRemouldCfgId = mount });
        Assert.Equal(0, (await Next(again, CareerSpecialService.CancelLease, CancelLeaseCareerSpecialItemRsp.Parser)).ErrCode);
        await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => m.HasCurrentAtkSkillId && m.CurrentAtkSkillId == walkingAttack);
        var walking = await Loop.InvokeAsync(() => Task.FromResult(skills.Build(Session("alice"))));
        AssertMountedAttack(walking, walkingAttack);
        var unmountedTarget = await Target("alice", stationary: true);
        await again.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = mountedAttack, TargetId = unmountedTarget.Uid });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(again, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await again.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
        {
            OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = true, SocketNum = 5,
            Info = new SkillData { SkillId = mountedAttack / 100, SkillLevel = 1 }
        });
        Assert.NotEqual(0, (await Next(again, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
    }

    private async Task EquipMountedTestWeapon(BotClient bot, int weapon)
    {
        await Gm("alice", $"!item {weapon} 1");
        long guid = await Loop.InvokeAsync(() =>
        {
            var item = Session("alice").Items.Single(i => i.Entry == weapon);
            (item.Attrs ??= new())[CharacterService.AttrAppraise] = 1;
            return Task.FromResult(item.Guid);
        });
        await bot.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { ItemGUID = guid, Pos = (EquipGridType)1, Opr = (EquipItemOprType)0 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
    }

    private static void AssertMountedAttack(DBSkillData state, int attack)
    {
        Assert.Equal(attack, state.CurrentAtkSkillId);
        Assert.Equal(attack, state.SkillWheel.AtkSkillId);
        Assert.Equal(1, state.SkillAssignPlans[state.CurrentPlanIndex].SkillAssigns[attack / 100].SkillLevel);
        Assert.Equal(attack, state.SkillWheel.WhellSkills[6].SkillConfigId);
        Assert.Equal(attack, state.OriginSkillWheel.WhellSkills[6].SkillConfigId);
    }

    private static async Task BindMountedTestAttack(BotClient bot, int group)
    {
        await bot.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
        {
            OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = true, SocketNum = 5,
            Info = new SkillData { SkillId = group, SkillLevel = 1 }
        });
        Assert.Equal(0, (await Next(bot, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
    }
}
