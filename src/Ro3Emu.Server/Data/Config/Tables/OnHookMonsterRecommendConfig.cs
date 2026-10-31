using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's OnHookMonsterRecommendConfig (data/config/OnHookMonsterRecommendConfig.json).</summary>
[ConfigTable("OnHookMonsterRecommendConfig")]
public readonly partial record struct OnHookMonsterRecommendConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iMapID")] public partial long[] MapId { get; }
    [ConfigField("_iMonsterID")] public partial long MonsterId { get; }
}
