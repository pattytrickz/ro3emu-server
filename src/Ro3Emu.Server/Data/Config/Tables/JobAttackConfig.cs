using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's job, weapon and battle-mount attack mapping.</summary>
[ConfigTable("JobAttackConfig")]
public readonly partial record struct JobAttackConfig(JsonElement Row)
{
    [ConfigField("_iJobID")] public partial long JobId { get; }
    [ConfigField("_iMountID")] public partial long MountId { get; }
    [ConfigField("_iSkillID")] public partial long SkillId { get; }
    [ConfigField("_kWeaponType")] public partial long[] WeaponType { get; }
}
