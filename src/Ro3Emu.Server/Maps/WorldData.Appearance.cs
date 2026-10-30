using System.Text.Json;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Maps;

public sealed partial class WorldData
{
    private void AddDyeMerchant(long sceneEntry, MapWorld map, List<WorldNpc> npcs)
    {
        if (!options.Value.CharacterAppearanceEnabled || sceneEntry != CharacterAppearanceRules.MerchantMap
            || data.Row("NPCConfig", CharacterAppearanceRules.MerchantId) == null) return;
        // House placement beside Prontera's central fountain; use the nav floor rather than a floating editor height.
        var position = map.Standable(new MapPoint(164, .2f, 190, 180));
        npcs.Add(new WorldNpc(NpcUidBase | sceneEntry << 16 | (long)npcs.Count, sceneEntry, WorldNpcKind.Npc,
            CharacterAppearanceRules.MerchantId, CharacterAppearanceRules.MerchantName, 0,
            position.X, position.Y, position.Z, position.Face, JsonSerializer.SerializeToElement(new { })));
    }
}
