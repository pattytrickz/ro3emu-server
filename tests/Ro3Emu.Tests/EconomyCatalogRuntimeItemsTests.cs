using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

public sealed class EconomyCatalogRuntimeItemsTests
{
    /// <summary>
    /// Every item the game can hand out must be one the economy decoder knows: a stored unknown item refused other
    /// players' economy operations and logins on 31 Oct 2026. A new overlay that adds items fails here.
    /// </summary>
    [ClientDataFact]
    public void EveryItemTheGameReadsIsCatalogued()
    {
        string dir = Path.Combine(ClientData.RepoRoot, "data");
        var data = new GameData(Options.Create(new ServerOptions { GameDataDir = dir, DataDir = dir }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GameData>.Instance);
        var catalog = EconomyCatalog.Load(dir);
        var missing = data.Table("ItemConfig").Keys
            .Where(k => !catalog.TryAsset(long.Parse(k, System.Globalization.CultureInfo.InvariantCulture), out _)).ToList();
        Assert.Empty(missing);
    }
}
