using System.Globalization;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Shared;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Delivers a paid order's Supporter Tokens by in-game mail (owner's plan v1.1 §4.4). Supporter time is active from
/// the capture itself; the tokens follow as a letter that never expires, to the account's most recently played
/// character, online or offline. An account with no character yet keeps its tokens owed until it has one. Each order
/// sends one letter, ever: its supporter_token_mail row is written in the letter's own transaction, and the letter's
/// delivery key ("supporter/&lt;provider&gt;/&lt;capture&gt;/tokens") stops a retry from adding a second. Runs right after
/// a capture and once a minute (restarts, accounts that had no character, a failed attempt).
/// </summary>
public sealed class SupporterTokenMail(AccountStore accounts, RoleStore roles, MailService mail, SupporterCheckout checkout,
    TimeProvider clock, ILogger<SupporterTokenMail> log) : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly SemaphoreSlim _kick = new(0, int.MaxValue);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        checkout.Paid += () => _kick.Release();
        while (!stop.IsCancellationRequested)
        {
            try { await DeliverPendingAsync(); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Supporter token mail sweep failed"); }
            try { await _kick.WaitAsync(TimeSpan.FromMinutes(1), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal static string Letter(SupporterOrderRow order) => string.Create(CultureInfo.InvariantCulture,
        $"Your CAD ${order.TierCad} contribution added {order.Days} days of Supporter time. Attached are your {order.Tokens:N0} Supporter Tokens: they never expire and stay with this character. Thank you for keeping Sol RO3 running!");

    /// <summary>Mails every paid order's tokens that are still owed. Returns the number of letters sent.</summary>
    internal async Task<int> DeliverPendingAsync()
    {
        await _running.WaitAsync();
        try
        {
            int sent = 0;
            foreach (var order in await accounts.ReadAsync<List<SupporterOrderRow>>(new SupporterTokenMailPendingReadPlan()))
            {
                if (await accounts.FindNameAsync(order.Account) is not { } name) continue;
                var role = (await roles.ForAccountAsync(name)).FirstOrDefault();
                if (role == null) continue; // no character yet: tried again each minute
                string key = $"supporter/{order.Provider}/{order.CaptureId}/tokens";
                bool sandbox = order.Provider.EndsWith("-sandbox", StringComparison.Ordinal);
                try
                {
                    await mail.SendRawOnceAsync(role.Id, (sandbox ? "[Test] " : "") + "Thank you, Supporter!", Letter(order),
                        [new MailService.Attachment(F2PRules.SupporterToken, order.Tokens)], key,
                        tx => tx.Add(new SupporterTokenMailRecordPlan(order.Id, order.Account, role.Id, order.Tokens, key,
                            clock.GetUtcNow().ToUnixTimeMilliseconds())));
                    sent++;
                    log.LogInformation("Supporter tokens of order {Order}: {Tokens} mailed to {Role} ({Name})", order.Id, order.Tokens, role.Id, role.Name);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "Supporter tokens of order {Order} not mailed yet; tried again next minute", order.Id);
                }
            }
            return sent;
        }
        finally { _running.Release(); }
    }
}
