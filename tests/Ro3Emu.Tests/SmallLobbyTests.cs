using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Sol RO3 house rule: ServerOptions.DragonIslandPlayers/TeamSize and SurvivorPlayers/TeamSize start the timed PvP
/// battles with a lobby a small realm can fill (the config asks for 30 and 100).
/// </summary>
public sealed class SmallLobbyTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-lobby-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (port, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = port; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05";
            o.DragonIslandPlayers = 4; o.DragonIslandTeamSize = 2;
            o.SurvivorPlayers = 2; o.SurvivorTeamSize = 1;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private async Task Queue(BotClient bot, int entry)
    {
        await bot.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = entry });
        Assert.Equal(0, (await Next(bot, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task DragonIslandStartsWithTheHouseRuleLobbyAndSplitsItsSquadsBetweenTheCamps()
    {
        var bots = new List<BotClient>();
        try
        {
            foreach (var name in new[] { "Ann", "Ben", "Cid", "Dee" }) bots.Add(await InWorld(name));
            _clock.Set(ScheduleRows.Weekly(126, new DateTime(2026, 11, 10), _clock.LocalTimeZone, Service<GameData>()).Start + 300);
            for (int i = 0; i < 3; i++) await Queue(bots[i], 6);
            await Task.Delay(1000); // three of four: no ready check yet
            Assert.DoesNotContain(bots[0].Inbox.Snapshot(), e => e.IsGame(TimedBattleService.ReadyNotify));
            await Queue(bots[3], 6);
            foreach (var bot in bots) await Next(bot, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser);
            foreach (var bot in bots)
            {
                await bot.SendGameAsync(TimedBattleService.Ready, new MatchReadyReq { MatchEntryId = 6, PlayerId = (long)bot.Self!.Uuid, IsReady = true });
                Assert.Equal(0, (await Next(bot, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
            }
            foreach (var bot in bots) await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SwitchSceneLine)
                && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == 3010303, Wait);
            var camps = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(bots
                .Select(b => Service<SessionRegistry>().All().Single(s => s.Account == b.Account && s.Role != null).EventFaction)
                .ToArray()));
            Assert.Equal([1, 1, 2, 2], camps);
        }
        finally { foreach (var bot in bots) await bot.DisposeAsync(); }
    }

    [ClientDataFact]
    public async Task SurvivorBattleStartsWithTheHouseRuleLobby()
    {
        await using var ann = await InWorld("Ann");
        await using var ben = await InWorld("Ben");
        _clock.Set(ScheduleRows.Weekly(114, new DateTime(2026, 11, 10), _clock.LocalTimeZone).Start + 300);
        await Queue(ann, 2);
        await Queue(ben, 2);
        await Next(ann, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser);
        await Next(ben, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }
}
