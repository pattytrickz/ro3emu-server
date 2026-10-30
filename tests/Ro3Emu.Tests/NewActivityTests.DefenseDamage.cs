using System.Collections.Concurrent;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class NewActivityTests
{
    /// <summary>Invader damage commits each map's defense-point row, so the advance step must reserve it up front;
    /// before, every second of a live round threw "Async owner commit did not reserve every entity".</summary>
    [ClientDataFact]
    public async Task DefenseInvaderDamageReservesTheMapPointRowItWrites()
    {
        await using var bot = await InWorld("DefDamage");
        Assert.True(Service<ActivityClock>().TryWindow(1012, out var start, out _));
        long first = start.ToUnixTimeSeconds() + 122400;
        var audit = new ConcurrentQueue<string>();
        Service<OwnerRegistry>().UncoveredForTest = audit.Enqueue;
        try
        {
            _clock.AdvanceSeconds(checked((int)(first + 5 - _clock.GetUtcNow().ToUnixTimeSeconds())));
            for (int minute = 0; minute < 4; minute++)
            {
                await Task.Delay(1500);
                _clock.AdvanceSeconds(60);
            }
            await Task.Delay(1500);
        }
        finally { Service<OwnerRegistry>().UncoveredForTest = null; }
        Assert.DoesNotContain(audit, line => line.Contains("prontera", StringComparison.Ordinal));
    }
}
