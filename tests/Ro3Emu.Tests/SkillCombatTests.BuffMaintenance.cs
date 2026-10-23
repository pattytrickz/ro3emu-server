using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task ExpiredBuffsStayBoundedWhileOwnershipIsBlockedAndExpireAfterRelease()
    {
        await using var bot = await InWorld("Alice");
        var session = Session("alice");
        var owners = app.Services.GetRequiredService<OwnerRegistry>();
        var buffs = app.Services.GetRequiredService<BuffService>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = Loop.InvokeAsync(() => owners.RunAsync("held player", [ReservationKey.Role(session.Role!.Id)], async () =>
        { held.TrySetResult(); await release.Task; }));
        await held.Task.WaitAsync(Wait);
        try
        {
            await Loop.InvokeAsync(() =>
            {
                session.Buffs.Add(new ActiveBuff(999991, 1, MonsterService.NowMs - 1, 1, [], 0, ShowIcon: false));
                for (int i = 0; i < 1000; i++) buffs.Tick(default);
                var debt = Loop.DrainStatus.Debt;
                Assert.Single(debt, d => d.Name == "buff maintenance");
                Assert.DoesNotContain(debt, d => d.Name == "buff expiration");
                Assert.Contains(session.Buffs, b => b.Guid == 999991);
                return Task.CompletedTask;
            }).WaitAsync(Wait);
        }
        finally { release.TrySetResult(); }
        await blocker.WaitAsync(Wait);
        var until = DateTime.UtcNow.Add(Wait);
        while (await Loop.InvokeAsync(() => Task.FromResult(session.Buffs.Any(b => b.Guid == 999991))))
        {
            Assert.True(DateTime.UtcNow < until, "expired buff never drained");
            await Task.Delay(20);
        }
        // The network/login path still works after the pending sweep drains.
        await using var second = await InWorld("Bob");
    }
}
