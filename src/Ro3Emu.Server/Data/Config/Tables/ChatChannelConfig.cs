using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ChatChannelConfig (data/config/ChatChannelConfig.json).</summary>
[ConfigTable("ChatChannelConfig")]
public readonly partial record struct ChatChannelConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_KIsBarrage")] public partial long[] IsBarrage { get; }
}
