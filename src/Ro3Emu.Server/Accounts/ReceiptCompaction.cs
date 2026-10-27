using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Keeps the realm database from growing without bound: monster-kill receipts (about 175,000 a day on the live
/// server, 6 KB each with their owner acknowledgement) and pass/play awards (about 8,000 a day, 20 KB each) are
/// compacted once they are older than <see cref="ServerOptions.ReceiptRetentionHours"/> (see
/// <see cref="ReceiptCompactionRule"/> and <see cref="ReceiptCompactionPlan"/>).
/// Small lazy batches with a pause between them, so the writer keeps serving the game.
/// </summary>
public sealed class ReceiptCompaction(IOptions<ServerOptions> options, AccountStore accounts, ILogger<ReceiptCompaction> log) : BackgroundService
{
    internal const int Batch = 1000;
    private static readonly TimeSpan FirstPass = TimeSpan.FromMinutes(2), Interval = TimeSpan.FromMinutes(15),
        BetweenBatches = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        int hours = options.Value.ReceiptRetentionHours;
        if (hours <= 0) return;
        try { await Task.Delay(FirstPass, ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await PassAsync(TimeSpan.FromHours(hours), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Receipt compaction failed"); }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Compacts everything older than <paramref name="retention"/>, rule by rule; returns the receipts compacted.</summary>
    internal async Task<int> PassAsync(TimeSpan retention, CancellationToken ct = default)
    {
        long cutoff = DateTimeOffset.UtcNow.Add(-retention).ToUnixTimeMilliseconds();
        int total = 0;
        foreach (var rule in ReceiptCompactionRule.All)
        {
            int compacted = 0, batches = 0;
            while (!ct.IsCancellationRequested)
            {
                var batch = await accounts.WriteLazyAsync<ReceiptCompactionBatch>(new ReceiptCompactionPlan(cutoff, Batch, rule));
                compacted += batch.Compacted;
                batches++;
                if (batch.Caught) break;
                await Task.Delay(BetweenBatches, ct);
            }
            if (compacted > 0)
                log.LogInformation("Compacted {Count} {Rule} receipts older than {Hours} h in {Batches} batches", compacted, rule.Name,
                    retention.TotalHours, batches);
            total += compacted;
        }
        return total;
    }
}
