using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Ro3Emu.Patcher;
using Ro3Emu.Setup;
using Ro3Emu.Setup.Install;

namespace Ro3Emu.Launcher;

/// <summary>
/// The launcher's logic behind the page: updates the launcher itself, checks and installs/updates/repairs the client
/// (from the repository with <see cref="Updater"/>, or from the publisher with <see cref="PublisherGame"/>), pings the
/// game server and starts ro3.exe. Messages to the page: state, progress, server, settings, news (see ui/app.js).
/// </summary>
public sealed class LauncherController : IDisposable
{
    readonly LauncherConfig _config;
    readonly string _exePath;
    readonly Action<object> _post;
    readonly Func<string, string?> _pickFolder;
    readonly Action _minimize;
    readonly UserSettings _settings = UserSettings.Load();
    readonly RepositoryClient _repo;
    readonly PublisherGame? _publisher;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly string _version = typeof(LauncherController).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    readonly CancellationTokenSource _life = new();
    InstallLayout _layout;
    ChannelInfo? _channel;
    LocalState? _installed;
    // "publisher" mode: the publisher's current release and what the folder holds, as shown on the page.
    ClientInstaller.Manifests? _official;
    string? _officialInstalled;
    string _phase = "checking";
    CancellationTokenSource? _work;
    bool _started;

    public LauncherController(LauncherConfig config, string exePath, Action<object> post, Func<string, string?> pickFolder, Action minimize)
    {
        _config = config;
        _exePath = exePath;
        _post = post;
        _pickFolder = pickFolder;
        _minimize = minimize;
        _repo = new RepositoryClient(new Uri(config.Repository));
        if (config.PublisherGame)
        {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RO3Emu", "downloads");
            _publisher = new PublisherGame(cache);
        }
        string defaultDir = config.PublisherGame ? PublisherGame.DefaultInstallFolder() : Path.Combine(Path.GetDirectoryName(exePath)!, "Client");
        _layout = new InstallLayout(_settings.ClientDirectory ?? defaultDir);
    }

    string? InstalledLabel => _publisher is not null ? _officialInstalled : _installed?.Version;
    string? RemoteLabel => _publisher is not null ? (_official is { } o ? $"release {o.Full.Release.Sequence}" : null) : _channel?.Version;

    public async Task HandleAsync(string cmd)
    {
        switch (cmd)
        {
            case "ready":
                if (!_started)
                {
                    _started = true;
                    if (_publisher is not null && _settings.ClientDirectory is null) await AdoptExistingAsync();
                    PostSettings();
                    _ = PingLoopAsync();
                    _ = LoadNewsAsync();
                }
                else PostSettings();
                await CheckAsync();
                break;
            case "primary":
                if (_work != null) return;
                if (_phase is "ready" or "running") await PlayAsync();
                else if (_phase is "install" or "update" or "repair") await UpdateAsync(verifyAll: _phase == "repair");
                else await CheckAsync();
                break;
            case "repair":
                if (_work == null) await UpdateAsync(verifyAll: true);
                break;
            case "pause":
                _work?.Cancel();
                break;
            case "changeDir":
                await ChangeDirectoryAsync();
                break;
            case "openDir":
                if (Directory.Exists(_layout.GameDirectory))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_layout.GameDirectory}\"") { UseShellExecute = true });
                break;
        }
    }

    /// <summary>"publisher" mode, first start: a game already on this PC is used instead of downloading it again.</summary>
    async Task AdoptExistingAsync()
    {
        PostState("checking", "Looking for Ragnarok Online 3 on this PC…");
        string? found = await Task.Run(PublisherGame.FindExisting);
        if (found is null) return;
        _settings.ClientDirectory = found;
        _settings.Save();
        _layout = new InstallLayout(found);
    }

    void PostSettings() => _post(new
    {
        type = "settings",
        dir = _layout.GameDirectory,
        launcher = _version,
        serverName = _config.ServerName,
        game = _config.PublisherGame ? "publisher" : "repository",
        links = _config.Links.Where(l => Uri.TryCreate(l.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            .Select(l => new { label = l.Label, url = l.Url, icon = l.Icon }),
    });

    void PostState(string phase, string? message = null)
    {
        _phase = phase;
        _post(new { type = "state", phase, installed = InstalledLabel, remote = RemoteLabel, message });
    }

    async Task CheckAsync()
    {
        PostState("checking");
        if (_publisher is not null)
        {
            if (await SelfUpdateAsync()) return;
            await CheckPublisherAsync();
            return;
        }
        try
        {
            var updater = new Updater(_layout, _repo);
            var check = await updater.CheckAsync(_config.Channel, _life.Token);
            _channel = check.Channel;
            _installed = check.Installed;
            if (await SelfUpdateAsync(_channel)) return;
            if (check.Installed is null)
            {
                bool existing = File.Exists(Path.Combine(_layout.GameDirectory, "ro3.exe"));
                PostState("install", existing ? "Found an RO3 client in this folder: only the changed files will be downloaded." : null);
            }
            else if (check.UpdateRequired) PostState("update", $"Version {_channel.Version} is available.");
            else if (updater.QuickVerify(check.Installed) is { Count: > 0 } changed) PostState("repair", $"{changed.Count} game files differ from the release.");
            else PostState("ready");
        }
        catch (Exception e) when (e is PatcherException or HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _installed = new Updater(_layout, _repo).LoadState();
            // Offline but installed: the game can still start (the update server and the game server are separate).
            PostState(_installed is not null ? "ready" : "offline", _installed is not null ? "Can't reach the update server; playing the installed version." : null);
        }
    }

    /// <summary>
    /// A newer launcher on the channel replaces this one and restarts it. Returns true when the app is shutting down.
    /// "publisher" mode reads the channel only for this, and plays on when the repository can't be reached.
    /// </summary>
    async Task<bool> SelfUpdateAsync(ChannelInfo? channel = null)
    {
        try
        {
            channel ??= await _repo.GetChannelAsync(_config.Channel, _life.Token);
            if (channel.Launcher is not { } release || !SelfUpdate.IsNewer(release.Version, _version)) return false;
            PostState("checking", $"Updating the launcher to {release.Version}…");
            if (!await SelfUpdate.ApplyAsync(_repo, release, _version, _exePath, _life.Token)) return false;
            SelfUpdate.Restart(_exePath);
            System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
            return true;
        }
        catch (Exception e) when (_publisher is not null && e is PatcherException or HttpRequestException or TaskCanceledException or IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    async Task CheckPublisherAsync()
    {
        string dir = _layout.GameDirectory;
        bool present = PublisherGame.IsClient(dir);
        long? installed = present ? PublisherGame.InstalledSequence(_settings, dir) : null;
        _officialInstalled = installed is { } i ? $"release {i}" : null;
        try
        {
            _official = await _publisher!.FetchAsync(_life.Token);
        }
        catch (Exception e) when (e is SetupException or HttpRequestException or TaskCanceledException or IOException)
        {
            _official = null;
        }

        if (!present)
        {
            if (_official is null) PostState("offline", "Can't reach the official Ragnarok Online 3 download servers. Try again in a moment.");
            else
            {
                long size = _official.Full.Sizes.Download > 0 ? _official.Full.Sizes.Download : _official.Full.Sizes.Expanded;
                PostState("install", (size > 0 ? $"About {size / (double)(1L << 30):0} GB" : "The game") + $" from the official servers into {dir}. You can change the folder in Settings."
                    + (Unsupported(_official.Full.Release.Sequence) ? $" The current release isn't supported by {_config.ServerName} yet: logging in will work after a launcher update." : ""));
            }
            return;
        }
        if (_official is null)
        {
            PostState("ready", "Can't reach the official update servers; playing the installed version.");
            return;
        }
        long remote = _official.Full.Release.Sequence;
        if (Unsupported(remote))
        {
            // Keep the folder as it is: this release's client doesn't reach our server.
            if (installed is { } held && Unsupported(held))
                PostState("ready", $"This game folder has release {held}, which {_config.ServerName} doesn't support yet. Watch Discord for a launcher update.");
            else PostState("ready", $"Release {remote} isn't supported by {_config.ServerName} yet, so the game stays on {(installed is { } kept ? $"release {kept}" : "the installed release")}.");
            return;
        }
        if (installed is null) PostState("update", "Check this game folder against the official release before the first launch.");
        else if (installed < remote) PostState("update", $"A game update is available (release {remote}).");
        else PostState("ready");
    }

    /// <summary>A publisher release newer than launcher.json "maxGameRelease".</summary>
    bool Unsupported(long release) => _config.MaxGameRelease > 0 && release > _config.MaxGameRelease;

    async Task UpdateAsync(bool verifyAll)
    {
        if (Process.GetProcessesByName("ro3").Length > 0)
        {
            PostState("running", "Close the game before updating or repairing its files.");
            return;
        }
        if (_publisher is null && _channel is null)
        {
            await CheckAsync();
            if (_channel is null) return;
        }
        if (_publisher is not null && _official is null)
        {
            await CheckAsync();
            if (_official is null) return;
        }
        // A repair would install the unsupported release over the game folder. A new install has nothing to keep.
        if (_publisher is not null && PublisherGame.IsClient(_layout.GameDirectory) && Unsupported(_official!.Full.Release.Sequence))
        {
            PostState("ready", $"Release {_official.Full.Release.Sequence} isn't supported by {_config.ServerName} yet, so the game files are left as they are.");
            return;
        }
        Directory.CreateDirectory(_layout.GameDirectory);
        _work = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        PostState("updating");
        var meter = new SpeedMeter();
        var progress = new ThrottledProgress(p =>
        {
            var (rate, eta) = p.Stage == UpdateStage.Downloading ? meter.Sample(p.BytesDone, p.BytesTotal) : (0, 0);
            _post(new { type = "progress", stage = p.Stage.ToString().ToLowerInvariant(), done = p.BytesDone, total = p.BytesTotal, rate, eta });
        });
        try
        {
            var token = _work.Token;
            if (_publisher is not null)
            {
                string dir = _layout.GameDirectory;
                var official = _official!;
                await _publisher.InstallAsync(official, dir, (stage, done, total) => progress.Report(new UpdateProgress(stage switch
                {
                    "Downloading" => UpdateStage.Downloading,
                    "Checking the download" => UpdateStage.Verifying,
                    _ => UpdateStage.Scanning,
                }, done, total)), token);
                _settings.InstalledSequence[PublisherGame.Key(dir)] = official.Full.Release.Sequence;
                // Replaced originals need the patch again.
                _settings.PatchStamp.Remove(PublisherGame.Key(dir));
                _settings.Save();
            }
            else
            {
                var updater = new Updater(_layout, _repo);
                var channel = _channel!;
                await Task.Run(() => updater.UpdateAsync(channel, verifyAll, progress, token));
            }
            _work.Dispose();
            _work = null;
            await CheckAsync();
        }
        catch (OperationCanceledException)
        {
            _work?.Dispose();
            _work = null;
            bool installed = _publisher is not null ? PublisherGame.IsClient(_layout.GameDirectory) : _installed is not null;
            PostState(installed ? "update" : "install", "Paused. Downloaded parts are kept; continue any time.");
        }
        catch (Exception e)
        {
            _work?.Dispose();
            _work = null;
            PostState("error", e is SetupException s ? $"{s.Message} {s.Fix}" : e.Message);
        }
    }

    async Task PlayAsync()
    {
        PostState("launching");
        try
        {
            if (_publisher is not null && !await PrepareForServerAsync()) return;
            string? failure = await GameStarter.StartAsync(_layout.GameDirectory);
            if (failure is null)
            {
                PostState("running");
                _minimize();
            }
            else PostState("repair", $"The game did not start: {failure}.");
        }
        catch (Exception e)
        {
            PostState("error", e.Message);
        }
    }

    /// <summary>
    /// "publisher" mode: patches the client for our server when it isn't yet, or when the server key, this launcher's
    /// client DLLs or the game files changed since. Returns false (with the state posted) when the game can't start.
    /// </summary>
    async Task<bool> PrepareForServerAsync()
    {
        string dir = _layout.GameDirectory;
        if (Process.GetProcessesByName("ro3").Length > 0)
        {
            PostState("running", "The game is already running.");
            return false;
        }
        if (string.IsNullOrEmpty(_config.Server))
        {
            PostState("error", "This launcher has no game server set (launcher.json \"server\").");
            return false;
        }
        string pem;
        try
        {
            pem = await _publisher!.GateKeyAsync(_config.Server, _life.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            PostState("ready", $"{_config.ServerName} isn't answering right now. Check the server status and try again in a moment.");
            return false;
        }
        string key = PublisherGame.Key(dir);
        string stamp = PublisherGame.PatchStamp(_config.Server, pem);
        bool current = _settings.PatchStamp.TryGetValue(key, out var done) && done == stamp && await Task.Run(() => PublisherGame.PatchIntact(dir));
        if (current) return true;

        PostState("launching", $"Preparing the game for {_config.ServerName}…");
        try
        {
            await Task.Run(() => PublisherGame.Patch(dir, _config.Server, pem));
        }
        catch (UnauthorizedAccessException)
        {
            PostState("error", "The game folder can't be written. Close the game, or move it out of Program Files and choose the new folder in Settings.");
            return false;
        }
        catch (IOException e) when (e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
        {
            PostState("error", "A game file is in use. Close the game and try again.");
            return false;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or Mono.Cecil.AssemblyResolutionException or BadImageFormatException)
        {
            PostState("repair", $"The game files don't match what {_config.ServerName} expects ({e.Message}). Repair them, then try again.");
            return false;
        }
        _settings.PatchStamp[key] = stamp;
        _settings.Save();
        return true;
    }

    async Task ChangeDirectoryAsync()
    {
        if (_work != null) return;
        string? folder = _pickFolder(_publisher is not null
            ? "Game folder (an existing Ragnarok Online 3 folder with ro3.exe, or an empty one)"
            : "Game folder (an existing ROO_PC folder or an empty one)");
        if (folder is null) return;
        // An existing client (ro3.exe) or an empty folder is used as is; anything else gets its own subfolder.
        bool usable = File.Exists(Path.Combine(folder, "ro3.exe")) || !Directory.EnumerateFileSystemEntries(folder).Any();
        if (!usable && File.Exists(Path.Combine(folder, "ROO_PC", "ro3.exe"))) folder = Path.Combine(folder, "ROO_PC");
        else if (!usable) folder = Path.Combine(folder, _publisher is not null ? "Ragnarok3" : "RO3Emu");
        _settings.ClientDirectory = folder;
        _settings.Save();
        _layout = new InstallLayout(folder);
        _installed = null;
        PostSettings();
        await CheckAsync();
    }

    async Task PingLoopAsync()
    {
        if (string.IsNullOrEmpty(_config.Server)) return;
        string server = _config.Server.TrimEnd('/');
        while (!_life.IsCancellationRequested)
        {
            bool online = false;
            int? players = null;
            try
            {
                using var status = await _http.GetAsync(server + "/ro3emu/status", _life.Token);
                if (status.IsSuccessStatusCode)
                {
                    online = true;
                    using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(_life.Token));
                    if (json.RootElement.TryGetProperty("players", out var p)) players = p.GetInt32();
                }
                else
                {
                    // Servers without /ro3emu/status: any answer from the gate-key endpoint means up.
                    using var key = await _http.GetAsync(server + "/ro3emu/gate-key.pem", _life.Token);
                    online = key.IsSuccessStatusCode;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
            if (_life.IsCancellationRequested) return;
            _post(new { type = "server", online, players });
            try { await Task.Delay(TimeSpan.FromSeconds(30), _life.Token); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// news.json in the repository root: { "headline": { kicker, title }, "items": [ { tag, color, title, text, url?, image? } ] }.
    /// A relative image is resolved against the repository.
    /// </summary>
    async Task LoadNewsAsync()
    {
        try
        {
            byte[]? bytes = await _repo.TryGetBytesAsync("news.json", _life.Token);
            if (bytes is null) return;
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            object? headline = root.TryGetProperty("headline", out var h) ? h.Clone() : null;
            var items = new List<Dictionary<string, string>>();
            if (root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var entry = new Dictionary<string, string>();
                    foreach (var field in new[] { "tag", "color", "title", "text", "url", "image", "date" })
                        if (item.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String) entry[field] = v.GetString()!;
                    if (entry.TryGetValue("image", out var image) && Uri.TryCreate(_repo.BaseUri, image, out var resolved) && resolved.Scheme is "http" or "https")
                        entry["image"] = resolved.AbsoluteUri;
                    else entry.Remove("image");
                    items.Add(entry);
                }
            }
            _post(new { type = "news", items, headline });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or PatcherException) { }
    }

    public void Dispose()
    {
        _life.Cancel();
        _work?.Cancel();
        _publisher?.Dispose();
    }

    /// <summary>Download speed over the last ~5 seconds and the remaining seconds at that speed.</summary>
    sealed class SpeedMeter
    {
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Queue<(double At, long Bytes)> _samples = new();

        public (double Rate, double Eta) Sample(long done, long total)
        {
            double now = _clock.Elapsed.TotalSeconds;
            _samples.Enqueue((now, done));
            while (_samples.Count > 2 && now - _samples.Peek().At > 5) _samples.Dequeue();
            var first = _samples.Peek();
            double seconds = now - first.At;
            if (seconds < 1) return (0, 0);
            double rate = (done - first.Bytes) / seconds;
            return rate <= 0 ? (0, 0) : (rate, (total - done) / rate);
        }
    }
}
