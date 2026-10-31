using System.Globalization;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Shared;

namespace Ro3Emu.AiBots;

/// <summary>
/// In-game checks of the Supporter test plan (G:\RO3Emu\plans\supporter-e2e-test-plan.md, sections E, J, K, L and the
/// thank-you gift), run as the staff QA account on the live realm. The bot logs in like a player and plays each step
/// over the real protocol; the server's own replies (the notices a player reads) and responses decide each check.
/// It needs no GM powers: tokens come from the test payments' token mail, Fly Wings from the thank-you gift, and
/// Supporter status is set outside (ro3-access supporter|unsupporter).
///   QA_ACCOUNT=... QA_PASSWORD=... Ro3Emu.AiBots qa [--server http://127.0.0.1:5380] check...
/// Checks: nonsupporter-merchant, nonsupporter-wing, supporter-wing, merchant, boosters, recharge, mail:&lt;title&gt;.
/// Prints PASS/FAIL per step; the exit code is the number of failures.
/// </summary>
public static class Qa
{
    private const int FlyWing = 1002001;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    public static async Task<int> RunAsync(string[] args)
    {
        string server = "http://127.0.0.1:5380";
        string? keyFile = null, gmSsh = null;
        var checks = new List<string>();
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
            else if (args[i] == "--key" && i + 1 < args.Length) keyFile = args[++i];
            else if (args[i] == "--gm-ssh" && i + 1 < args.Length) gmSsh = args[++i];
            else checks.Add(args[i]);
        string account = Environment.GetEnvironmentVariable("QA_ACCOUNT") ?? "", password = Environment.GetEnvironmentVariable("QA_PASSWORD") ?? "";
        if (account.Length == 0 || password.Length == 0 || checks.Count == 0)
        {
            Console.WriteLine("usage: QA_ACCOUNT=... QA_PASSWORD=... Ro3Emu.AiBots qa [--server http://127.0.0.1:5380] [--key gate_rsa_public.pem] [--gm-ssh root@host] <check>...");
            return 2;
        }

        // The gate's public key: from the file, else from the server (its metrics endpoint answers only on the host).
        using var http = new HttpClient { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(15) };
        string pem = keyFile != null ? await File.ReadAllTextAsync(keyFile) : await http.GetStringAsync("/ro3emu/metrics/public-key");
        var view = new SceneView();
        await using var bot = new BotClient(server, pem) { Timeout = TimeSpan.FromSeconds(30) };
        bot.EnableLoadMode(32, view.Observe);
        await bot.LoginAsync(account, password);
        await bot.EnterGateAsync();
        long role = bot.Roles.FirstOrDefault()?.Id ?? throw new InvalidOperationException("the QA account has no character");
        view.SelfUid = (ulong)role;
        await bot.EnterWorldAsync(role);
        // The current client hooks: the Supporter Merchant's goods exist only for hooks v3. The server answers with the
        // dye prices once it has the version; buying before that finds the shop closed.
        using (var ready = bot.Inbox.Expect(e => e.IsGame(WorldCoreService.CustomChange)))
        {
            await bot.SendGameBytesAsync(CharacterAppearanceRules.ClientReadyMessage, [CharacterAppearanceRules.ClientVersion]);
            await ready.WaitAsync(Wait);
        }
        await bot.JoinChatAsync($"4_{view.SceneId}");
        using var stop = new CancellationTokenSource();
        var beat = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(5000, stop.Token); await bot.SendGameAsync(1, new Ping()); }
                catch { return; }
            }
        });
        Console.WriteLine($"{account}: role {role} on scene {view.SceneEntry}");

        var run = new Run(bot, view, role, account, gmSsh);
        try
        {
            foreach (var check in checks) await run.CheckAsync(check);
        }
        finally { stop.Cancel(); }
        Console.WriteLine(run.Failures == 0 ? $"ALL PASSED ({run.Passes})" : $"{run.Failures} FAILED, {run.Passes} passed");
        return run.Failures;
    }

    private sealed class Run(BotClient bot, SceneView view, long role, string account, string? gmSsh)
    {
        public int Passes, Failures;

        private void Expect(bool ok, string step, string seen)
        {
            if (ok) Passes++; else Failures++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {step}{(seen.Length > 0 ? $"  [{seen}]" : "")}");
        }

        private string Notice() { lock (view.Sync) return view.LastNotice; }

        /// <summary>Runs <paramref name="act"/> and returns its result with the first notice the server sent after it
        /// (empty when none came within a few seconds).</summary>
        private async Task<(T Result, string Notice)> WithNotice<T>(Func<Task<T>> act)
        {
            lock (view.Sync) view.LastNotice = "";
            var result = await act();
            for (int i = 0; i < 30 && Notice().Length == 0; i++) await Task.Delay(100);
            return (result, Notice());
        }

        /// <summary>
        /// A GM line for the QA account through the game host's loopback GM tool (the account must be in GmToolAccounts),
        /// over ssh when the bot runs elsewhere. Every line is printed, so the run shows what it set up.
        /// </summary>
        private async Task Gm(string line)
        {
            if (gmSsh == null) throw new InvalidOperationException("GM setup needs --gm-ssh <host> (or run on the game host)");
            Console.WriteLine($"     gm: {line}");
            var start = new System.Diagnostics.ProcessStartInfo("ssh", [gmSsh,
                $"curl -sf -X POST --data-binary @- 'http://127.0.0.1:5380/ro3emu/gm?account={Uri.EscapeDataString(account)}'"])
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var ssh = System.Diagnostics.Process.Start(start)!;
            await ssh.StandardInput.WriteAsync(line);
            ssh.StandardInput.Close();
            string output = await ssh.StandardOutput.ReadToEndAsync();
            await ssh.WaitForExitAsync();
            if (ssh.ExitCode != 0) throw new InvalidOperationException($"GM tool refused \"{line}\" (exit {ssh.ExitCode}): is {account} in GmToolAccounts?");
            await Task.Delay(800);
        }

        private async Task<int> Buy(SupporterShopRules.Product p)
        {
            using var reply = bot.Inbox.Expect(e => e.IsGame(ShopService.BuyCommodity));
            await bot.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityReq { CommodityId = p.Commodity, Count = 1, Price = p.Price });
            return BuyCommodityResp.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body).Result;
        }

        /// <summary>Uses one of <paramref name="entry"/> (the bag guid the server last synced); -1 when none is known.</summary>
        private async Task<int> Use(int entry, long guid = 0)
        {
            if (guid == 0) guid = view.Guids(entry).LastOrDefault();
            if (guid == 0) return -1;
            using var reply = bot.Inbox.Expect(e => e.IsGame(CharacterService.ItemUse));
            await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
            return ItemUseResp.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body).Result;
        }

        private async Task<string> Command(string line)
        {
            var (_, notice) = await WithNotice(async () => { await bot.SendChatAsync($"4_{view.SceneId}", 2, line, channelIndex: 4); return 0; });
            return notice;
        }

        private static SupporterShopRules.Product P(string kind, int percent) =>
            SupporterShopRules.Products.First(p => p.Kind == kind && (kind != SupporterShopRules.Exp || p.Percent == percent));

        public async Task CheckAsync(string check)
        {
            Console.WriteLine($"-- {check}");
            try
            {
                switch (check)
                {
                    case "nonsupporter-merchant": await NonSupporterMerchant(); break;
                    case "nonsupporter-wing": await Wing(free: false); break;
                    case "supporter-wing": await Wing(free: true); break;
                    case "merchant": await Merchant(); break;
                    case "boosters": await Boosters(); break;
                    case "recharge": await Recharge(); break;
                    case "setup": await Setup(); break;
                    default:
                        if (check.StartsWith("mail:", StringComparison.Ordinal)) await Mail(check[5..]);
                        else if (check.StartsWith("gm:", StringComparison.Ordinal)) await Gm(check[3..]);
                        else Expect(false, $"unknown check {check}", "");
                        break;
                }
            }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException or HttpRequestException)
            {
                Expect(false, $"{check} could not finish", e.Message);
            }
        }

        // A fresh QA character: past the starting quest that opens NPC shops (FunctionConfig 701101's condition), at a
        // mid level, beside the Supporter and Dye Merchants at Prontera's fountain.
        private async Task Setup()
        {
            await Gm("!lvl 40 30");
            await Gm("!quest done 1002");
            await Gm($"!warp {SupporterShopRules.MerchantMap}");
            Expect(true, "set up (level, starting quest, Prontera)", "");
        }

        // J3: a non-supporter's purchase is refused, with the reason in plain words.
        private async Task NonSupporterMerchant()
        {
            var (result, notice) = await WithNotice(() => Buy(P(SupporterShopRules.Recharge, 0)));
            Expect(result != 0 && notice.Contains("Supporter", StringComparison.Ordinal), "a non-supporter cannot buy at the Supporter Merchant", $"{result}: {notice}");
        }

        // L1/L2: a stack of Fly Wings in the bag (the thank-you gift's). A supporter's use leaves the stack as it was;
        // anyone else's takes one.
        private async Task Wing(bool free)
        {
            long guid = view.Guids(FlyWing).FirstOrDefault(g => view.Stack(g) > 1);
            if (guid == 0) { Expect(false, "a stack of Fly Wings in the bag (claim the thank-you gift first)", ""); return; }
            int before = view.Stack(guid);
            float x0, z0;
            lock (view.Sync) (x0, z0) = (view.X, view.Z);
            int used = await Use(FlyWing, guid);
            for (int i = 0; i < 30 && !free && view.Stack(guid) == before; i++) await Task.Delay(100);
            await Task.Delay(1500);
            float x1, z1;
            lock (view.Sync) (x1, z1) = (view.X, view.Z);
            Expect(used == 0, "the Fly Wing flies", $"result {used}, moved {MathF.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)):F0}");
            int after = view.Stack(guid);
            Expect(free ? after == before : after == before - 1, free ? "a supporter's wing is not used up" : "a non-supporter's wing is used up", $"{before} -> {after}");
        }

        // J4: the weekly limits (per account): Recharge 2, each EXP booster 1, the drop booster 2.
        private async Task Merchant()
        {
            var recharge = P(SupporterShopRules.Recharge, 0);
            Expect(await Buy(recharge) == 0 && await Buy(recharge) == 0, "two Recharges this week", "");
            var (third, why) = await WithNotice(() => Buy(recharge));
            Expect(third != 0, "a third Recharge is refused", $"{third}: {why}");
            var exp50 = P(SupporterShopRules.Exp, 50);
            Expect(await Buy(exp50) == 0, "one Auto EXP Booster +50%", "");
            Expect(await Buy(exp50) != 0, "a second +50% this week is refused", "");
            var drop = P(SupporterShopRules.Drop, 0);
            Expect(await Buy(drop) == 0 && await Buy(drop) == 0, "two Drop Boosters", "");
            Expect(await Buy(drop) != 0, "a third Drop Booster is refused", "");
        }

        // K1, K3: a booster starts (paused outside Auto Mode); a second one waits its turn and stays in the bag.
        private async Task Boosters()
        {
            var (started, said) = await WithNotice(() => Use(P(SupporterShopRules.Exp, 50).Item));
            Expect(started == 0, "the +50% EXP booster starts", said);
            var (second, why) = await WithNotice(() => Use(P(SupporterShopRules.Drop, 0).Item));
            Expect(second != 0, "one booster at a time", $"{second}: {why}");
            string boost = await Command("/boost");
            Expect(boost.Contains("50", StringComparison.Ordinal), "/boost lists the running booster", boost);
        }

        // K7 and the owner's rule: the Recharge adds an hour (or is kept when the bank is full) for anyone.
        private async Task Recharge()
        {
            var (result, said) = await WithNotice(() => Use(P(SupporterShopRules.Recharge, 0).Item));
            Expect(result == 0 ? said.Contains("+60 minutes", StringComparison.Ordinal) : said.Contains("too full", StringComparison.Ordinal),
                "the Recharge adds 60 minutes, or stays when the bank is full", $"{result}: {said}");
        }

        // E and the gift: the newest letter whose title starts with the text arrives once, with its attachments, and claims.
        private async Task Mail(string title)
        {
            var list = await bot.MicroAsync("MailService", "getUserMailList", new Dictionary<string, object?>
            { ["Uid"] = role.ToString(CultureInfo.InvariantCulture), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1 });
            var letters = ((object?[])list["MailList"]!).Cast<Dictionary<object, object?>>()
                .Where(l => (l["Title"] as string ?? "").StartsWith(title, StringComparison.Ordinal)).ToList();
            Expect(letters.Count > 0, $"a letter \"{title}…\" arrived", $"{letters.Count} found");
            if (letters.Count == 0) return;
            var letter = letters[0];
            Console.WriteLine($"     {letter["Title"]}: {string.Join(", ", letter.Where(p => p.Key is string k && k != "Title" && k != "MailId").Select(p => $"{p.Key}={Show(p.Value)}"))}");
            var claim = new ClaimMailReq();
            claim.MailUid.Add((string)letter["MailId"]!);
            using var reply = bot.Inbox.Expect(e => e.IsGame(MailService.ClaimMail));
            await bot.SendGameAsync(MailService.ClaimMail, claim);
            var claimed = ClaimMailRsp.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
            Expect(claimed.Result == 0, "its attachments claim", $"result {claimed.Result}");
        }

        private static string Show(object? value) => value switch
        {
            null => "null",
            Dictionary<object, object?> map => "{" + string.Join(", ", map.Select(p => $"{p.Key}={Show(p.Value)}")) + "}",
            object?[] list => "[" + string.Join(", ", list.Select(Show)) + "]",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }
}
