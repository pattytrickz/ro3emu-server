using System.Globalization;
using System.Text.Json;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Data.Config;

internal static class SupporterIdentityOverlay
{
    internal static Dictionary<string, JsonElement> Apply(Dictionary<string, JsonElement> source)
    {
        var rows = new Dictionary<string, JsonElement>(source)
        {
            [SupporterIdentityRules.Frame.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToElement(SupporterIdentityRules.FrameRow())
        };
        return rows;
    }
}
