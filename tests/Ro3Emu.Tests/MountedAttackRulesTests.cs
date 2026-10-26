using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class MountedAttackRulesTests
{
    [Fact]
    public async Task WeaponKeySelectsItsRiddenMountAndFallsBackOnlyToItsWalkingVariant()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-attack-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            WriteAttackData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            var skills = host.Service<SkillService>();
            Assert.Equal(99101, skills.NormalAttack(1000, 0, 0));
            Assert.Equal(99201, skills.NormalAttack(1000, 0, 0, 77));
            Assert.Equal(99301, skills.NormalAttack(1000, 1, 0));
            Assert.Equal(99401, skills.NormalAttack(1000, 1, 0, 77));
            Assert.Equal(99301, skills.NormalAttack(1000, 1, 0, 88)); // an unrelated/cosmetic mount
            Assert.Equal(99101, skills.NormalAttack(1000, 0, 0, 88));
            Assert.True(skills.IsMountOnlyAttack(1000, 99401));
            Assert.False(skills.IsMountOnlyAttack(1000, 99301));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WriteAttackData(string root)
    {
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """
            {"1000":{"_iID":1000,"_kSkillNormalID":[99101]}}
            """);
        File.WriteAllText(Path.Combine(root, "config", "JobAttackConfig.json"), """
            {
              "1":{"_iID":1,"_iJobID":1000,"_iMountID":0,"_iSkillID":99101,"_kWeaponType":{}},
              "2":{"_iID":2,"_iJobID":1000,"_iMountID":77,"_iSkillID":99201,"_kWeaponType":{}},
              "3":{"_iID":3,"_iJobID":1000,"_iMountID":0,"_iSkillID":99301,"_kWeaponType":[1,0]},
              "4":{"_iID":4,"_iJobID":1000,"_iMountID":77,"_iSkillID":99401,"_kWeaponType":[1,0]}
            }
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """
            {
              "99101":{"_iID":99101,"_iSkillID":991,"_iLevel":1,"_iMode":0},
              "99201":{"_iID":99201,"_iSkillID":992,"_iLevel":1,"_iMode":0},
              "99301":{"_iID":99301,"_iSkillID":993,"_iLevel":1,"_iMode":0},
              "99401":{"_iID":99401,"_iSkillID":994,"_iLevel":1,"_iMode":0}
            }
            """);
    }
}
