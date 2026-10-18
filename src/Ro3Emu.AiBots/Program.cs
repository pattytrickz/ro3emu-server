using System.Runtime.InteropServices;
using Ro3Emu.AiBots;

// AI players for an RO3Emu server: they log in like real clients, level up on fields, hang out in town, join parties
// and chat (canned lines, or a language model when an API key is set). See docs/guide/ai-bots.md.
// usage: Ro3Emu.AiBots [--config ~/aibots/aibots.json] [--count N] [--server http://host:port]
string configPath = Arg("--config") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "aibots", "aibots.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
var options = AiBotsOptions.Load(configPath);
if (Arg("--count") is { } count) options.Count = int.Parse(count);
if (Arg("--server") is { } server) options.Server = server;
if (Arg("--game-data") is { } gameData) options.GameDataDir = gameData;

using var logFile = new DailyLog(Path.Combine(options.Directory, "logs"));
void Log(string line) => logFile.Write(line);

string rosterPath = Path.Combine(options.Directory, "roster.json");
var roster = Roster.LoadOrGrow(options, rosterPath);
var rosterLock = new object();
void SaveRoster() { lock (rosterLock) Json.WritePrivate(rosterPath, roster); }

using var http = new HttpClient { BaseAddress = new Uri(options.Server), Timeout = TimeSpan.FromSeconds(15) };
string pem = await http.GetStringAsync("/ro3emu/metrics/public-key");
var atlas = new Atlas(options);
var progression = new Progression(atlas.Data);
if (Planner.Plan(roster, progression, new Random())) SaveRoster();
var gm = new GmTool(options.Server, Log);
Log($"{atlas.HuntMaps.Count} hunting maps: {string.Join(", ", atlas.HuntMaps.OrderBy(m => m.MinLevel).Select(m => $"{m.Name} {m.MinLevel}-{m.MedianLevel}"))}");
var hub = new ChatHub(options.Chat, new ChatModel(options.Llm, options.Directory, Log));
var crowd = new Crowd();

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });

var bots = roster.Take(options.Count)
    .Select(entry => new AiBot(entry, options, atlas, progression, gm, hub, crowd, pem, Log, SaveRoster)).ToArray();
Log($"starting {bots.Length} bot(s) against {options.Server}");
var running = bots.Select(async (bot, i) =>
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(i * options.LoginSpacingSeconds), stop.Token);
        await bot.RunAsync(stop.Token);
    }
    catch (OperationCanceledException) { }
}).ToArray();
var status = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(options.StatusMinutes), stop.Token); } catch (OperationCanceledException) { return; }
        Log($"status: {bots.Count(b => b.Online)}/{bots.Length} online, {hub.Model.CallsToday} model calls today, " +
            $"heap {GC.GetTotalMemory(false) >> 20} MB, rss {Environment.WorkingSet >> 20} MB, " +
            $"paths {Navigator.Calls} ({Navigator.Expanded} cells, {Navigator.Ticks * 1000 / System.Diagnostics.Stopwatch.Frequency} ms)");
        foreach (var bot in bots) Log("  " + bot.Status);
    }
});
await Task.WhenAll(running);
await status;
Log("stopped");

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

/// <summary>Console plus logs/aibots-yyyyMMdd.log; files older than a week are deleted.</summary>
sealed class DailyLog(string directory) : IDisposable
{
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public void Write(string line)
    {
        var now = DateTime.UtcNow;
        string text = $"{now:yyyy-MM-dd HH:mm:ss} {line}";
        lock (_sync)
        {
            Console.WriteLine(text);
            var day = DateOnly.FromDateTime(now);
            if (_writer == null || day != _day)
            {
                _writer?.Dispose();
                Directory.CreateDirectory(directory);
                _writer = new StreamWriter(Path.Combine(directory, $"aibots-{now:yyyyMMdd}.log"), append: true) { AutoFlush = true };
                _day = day;
                foreach (var old in Directory.GetFiles(directory, "aibots-*.log"))
                    if (File.GetLastWriteTimeUtc(old) < now.AddDays(-7)) File.Delete(old);
            }
            _writer.WriteLine(text);
        }
    }

    public void Dispose() { lock (_sync) _writer?.Dispose(); }
}
