using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Invented skills: the Skill Bar (Auto Mode presses it every 5 s) does not recast a buff the caster still has.</summary>
public sealed class LineupBuffTests
{
    private const int QueueGroup = 991, QueueRow = 99101, BuffGroup = 992, BuffRow = 99201, Buff = 99301;

    [Fact]
    public async Task ASoloCasterKeepsItsActiveBuffAndAPartyMemberGetsItRecast()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-lineupbuff-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            await alice.SendGameAsync(SkillService.QueueUpdate, new SkillQueueUpdateReq
            {
                Plan = 0, QueueIndex = 0, OpType = SkillLoadOperation.SkillLoadOpLoad,
                Info = new SkillData { SkillId = BuffGroup, SkillLevel = 1 }
            });
            Assert.Equal(0, SkillQueueUpdateResp.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(SkillService.QueueUpdate), TimeSpan.FromSeconds(8))).Body).Result);

            Assert.Equal(1, await PressAsync(alice, host)); // not active yet: cast
            Assert.Equal(0, await PressAsync(alice, host)); // still active, solo: skipped
            host.Service<CombatService>().InParty = _ => true;
            Assert.Equal(1, await PressAsync(alice, host)); // in a party the allies may lack it: cast
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Presses the Skill Bar and returns how many times the Lineup's buff was put on the caster.</summary>
    private static async Task<int> PressAsync(BotClient bot, SyntheticHost host)
    {
        int before = bot.Inbox.Snapshot().Count(IsBuff);
        long role = host.Service<SessionRegistry>().All().Single(s => s.Role != null).Role!.Id;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = QueueRow, TargetId = role });
        var reply = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), TimeSpan.FromSeconds(8));
        Assert.Equal(0, CastSkillRsp.Parser.ParseFrom(reply.Body).Result);
        await Task.Delay(1500); // the Lineup runs one action after the bar's own
        return bot.Inbox.Snapshot().Count(IsBuff) - before;
    }

    private static bool IsBuff(BotEvent p) => p.IsGame(BuffService.BuffUpdate)
        && BuffUpdateNotify.Parser.ParseFrom(p.Body).Buffs.Any(b => b.ConfigId == Buff);

    private static void PrepareData(string root)
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """
            {"1000":{"_iID":1000,"_iJobRank":1,"_iQueueSkillId":99101}}
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """
            {
              "99101":{"_iID":99101,"_iSkillID":991,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1,"_iSystemType":6},
              "99201":{"_iID":99201,"_iSkillID":992,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,
                "_iBasicType":1,"_iInitialLevel":1,"_iQueue":1,"_kBuff1":[[99301,10000,60000]]}
            }
            """);
        File.WriteAllText(Path.Combine(root, "config", "BuffConfig.json"), """
            {"99301":{"_iID":99301,"_iBuffID":9930,"_iTime":60000}}
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillTreeConfig.json"), """
            {"1":{"_iID":1,"_iJob":1000,"_iX1":992}}
            """);
    }
}
