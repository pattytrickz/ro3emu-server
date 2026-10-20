using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Ro3Emu.ClientTools;
using Ro3Emu.DataExport;
using Ro3Emu.Setup;
using Ro3Emu.Setup.Install;

namespace Ro3Emu.Launcher;

/// <summary>
/// launcher.json "game": "publisher". The official client comes from the publisher's update service with its signed
/// manifests (what RO3Emu.exe install does) and is patched on this PC for our server (what RO3Emu.exe join does):
/// the hooks and Loc DLLs and our translations travel inside the launcher, so a launcher update is a client update.
/// </summary>
public sealed class PublisherGame : IDisposable
{
    readonly HttpClient _http;
    readonly ClientInstaller _installer;
    readonly PublisherConfig _config = PublisherConfig.Genesis;

    public PublisherGame(string cacheDir)
    {
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(20) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"RO3Emu/{typeof(PublisherGame).Assembly.GetName().Version}");
        _installer = new ClientInstaller(_config, _http, cacheDir);
    }

    /// <summary>The folder holds a game (ro3.exe and its managed DLLs).</summary>
    public static bool IsClient(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "ro3.exe")) && Directory.Exists(Path.Combine(dir, "ro3_Data", "Managed"));

    public static string Key(string dir) => Path.GetFullPath(dir).TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>The signed manifests of the publisher's current release.</summary>
    public async Task<ClientInstaller.Manifests> FetchAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        return await _installer.FetchAsync(timeout.Token);
    }

    /// <summary>
    /// The release the folder holds: our record, else the one the publisher's launcher wrote
    /// (.ro3-update/installed-release.txt). Null when unknown.
    /// </summary>
    public static long? InstalledSequence(UserSettings settings, string dir)
    {
        if (settings.InstalledSequence.TryGetValue(Key(dir), out long ours)) return ours;
        var path = Path.Combine(dir, ".ro3-update", "installed-release.txt");
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && parts[0] == "release_sequence" && long.TryParse(parts[1], out long seq)) return seq;
            }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>
    /// Compares the folder with the release (size, then SHA-256 of every file) and downloads what is missing or
    /// different, package by package, each checked before it replaces anything. Interrupted downloads resume.
    /// </summary>
    public async Task<int> InstallAsync(ClientInstaller.Manifests manifests, string dir, Action<string, long, long> progress, CancellationToken ct)
    {
        _installer.Progress = progress;
        var plan = await Task.Run(() => _installer.MakePlan(manifests, dir), ct);
        ct.ThrowIfCancellationRequested();
        if (plan.Needed.Count == 0) return 0;
        CheckSpace(dir, plan);
        await _installer.ApplyAsync(plan, dir, ct);
        return plan.Needed.Count;
    }

    /// <summary>The files plus the largest package (each is deleted once extracted) must fit, with a margin.</summary>
    void CheckSpace(string dir, ClientInstaller.Plan plan)
    {
        const long margin = 1L << 30;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            long need = plan.NeededBytes + plan.LargestPackage + margin;
            if (drive.AvailableFreeSpace < need)
                throw new InstallException($"Drive {drive.Name} has {Gb(drive.AvailableFreeSpace)} free; the game needs {Gb(need)}.",
                    "Free some space, or choose a folder on another drive in Settings.");
        }
        catch (ArgumentException) { }
    }

    static string Gb(long bytes) => $"{bytes / (double)(1L << 30):0.0} GB";

    /// <summary>Where a new install goes: the fixed drive with the most free space, in a plain folder (no admin needed).</summary>
    public static string DefaultInstallFolder()
    {
        var drive = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
        return Path.Combine(drive?.RootDirectory.FullName ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Games", "Ragnarok3");
    }

    /// <summary>An installed game, if the PC has one: installed programs, then the usual folders on every fixed drive.</summary>
    public static string? FindExisting()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Check(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try
            {
                dir = Path.GetFullPath(dir.Trim().Trim('"'));
                foreach (var d in new[] { dir, Path.Combine(dir, "ROO_PC") })
                    if (seen.Add(d) && IsClient(d)) return d;
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { }
            return null;
        }

        foreach (var location in UninstallLocations())
            if (Check(location) is { } found) return found;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            foreach (var pattern in new[] { "Games", "RO3", "Genesis", "Program Files", "Program Files (x86)" })
            {
                var baseDir = Path.Combine(drive.RootDirectory.FullName, pattern);
                if (Check(baseDir) is { } atBase) return atBase;
                foreach (var sub in Children(baseDir).Where(LooksLikeGame))
                {
                    if (Check(sub) is { } found) return found;
                    foreach (var sub2 in Children(sub).Where(LooksLikeGame))
                        if (Check(sub2) is { } deeper) return deeper;
                }
            }
        }
        return null;
    }

    static bool LooksLikeGame(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.Contains("RO3", StringComparison.OrdinalIgnoreCase) || name.Contains("ROO", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ragnarok", StringComparison.OrdinalIgnoreCase) || name.Contains("Genesis", StringComparison.OrdinalIgnoreCase);
    }

    static IEnumerable<string> Children(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static IEnumerable<string> UninstallLocations()
    {
        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            var found = new List<string>();
            try
            {
                using var key = hive.OpenSubKey(path);
                foreach (var name in key?.GetSubKeyNames() ?? [])
                {
                    using var app = key!.OpenSubKey(name);
                    var display = app?.GetValue("DisplayName") as string ?? "";
                    if ((display.Contains("Ragnarok", StringComparison.OrdinalIgnoreCase) || display.Contains("RO3", StringComparison.OrdinalIgnoreCase)
                         || display.Contains("Genesis", StringComparison.OrdinalIgnoreCase)) && app?.GetValue("InstallLocation") is string location)
                        found.Add(location);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            foreach (var location in found) yield return location;
        }
    }

    // ---------- The patch for our server ----------

    static readonly Assembly Self = typeof(PublisherGame).Assembly;

    /// <summary>Our translations, embedded in the launcher, in file name order (as RO3Emu.exe passes them).</summary>
    static List<string> Translations() =>
        Self.GetManifestResourceNames().Where(n => n.StartsWith("translations/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var reader = new StreamReader(Self.GetManifestResourceStream(n)!);
                return reader.ReadToEnd();
            }).ToList();

    /// <summary>
    /// What a patch depends on: the server, its gate key, and the hooks, Loc DLLs and translations inside this launcher.
    /// A different stamp means the folder needs patching again.
    /// </summary>
    public static string PatchStamp(string serverUrl, string pem)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(serverUrl + "\n" + pem.Replace("\r\n", "\n").Trim() + "\n"));
        hash.AppendData(Encoding.UTF8.GetBytes($"patch revision {ClientPatcher.Revision}\n"));
        foreach (var name in Self.GetManifestResourceNames().Where(n => n.StartsWith("translations/", StringComparison.Ordinal) || n.StartsWith("Ro3Emu.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = Self.GetManifestResourceStream(name)!;
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The patched DLLs are still the ones the patch wrote (ro3emu-backup/patched.sha256) and our DLLs are in place.
    /// A game update or a repair by another launcher replaces them, and the folder needs patching again.
    /// </summary>
    public static bool PatchIntact(string dir)
    {
        var managed = Path.Combine(dir, "ro3_Data", "Managed");
        var record = Path.Combine(dir, "ro3emu-backup", "patched.sha256");
        if (!File.Exists(record) || !File.Exists(Path.Combine(managed, "Ro3Emu.ClientHooks.dll")) || !File.Exists(Path.Combine(managed, "Ro3Emu.Loc.dll")))
            return false;
        int checkedFiles = 0;
        foreach (var line in File.ReadLines(record))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2 || Path.GetFileName(parts[0]) != parts[0] || string.IsNullOrWhiteSpace(parts[0])) return false;
            var file = Path.Combine(managed, parts[0]);
            if (!File.Exists(file) || !string.Equals(ClientInstaller.Sha256(file), parts[1], StringComparison.OrdinalIgnoreCase)) return false;
            checkedFiles++;
        }
        if (checkedFiles == 0) return false;
        // The patcher's record covers modified game DLLs. Our two DLLs must also still match this launcher.
        foreach (var name in new[] { "Ro3Emu.ClientHooks.dll", "Ro3Emu.Loc.dll" })
        {
            using var embedded = Self.GetManifestResourceStream(name);
            if (embedded is null) return false;
            if (!string.Equals(ClientInstaller.Sha256(Path.Combine(managed, name)), Convert.ToHexStringLower(SHA256.HashData(embedded)), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>The server's gate key (the client checks the server with it), as RO3Emu.exe join fetches it.</summary>
    public async Task<string> GateKeyAsync(string serverUrl, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _http.GetStringAsync(serverUrl.TrimEnd('/') + "/ro3emu/gate-key.pem", timeout.Token);
    }

    /// <summary>
    /// Patches the client for the server (originals stay in ro3emu-backup) and writes the English text map from the
    /// player's own client, both exactly as RO3Emu.exe join does.
    /// </summary>
    public static void Patch(string dir, string serverUrl, string pem)
    {
        var original = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            ClientPatcher.Patch(dir, serverUrl.TrimEnd('/'), pem);
        }
        finally
        {
            Console.SetOut(original);
        }

        // English text is a comfort, not a requirement: the game still works with its own texts.
        var target = Path.Combine(dir, "ro3emu", "loc_en.tsv");
        bool ours = !File.Exists(target) || File.ReadLines(target).FirstOrDefault()?.StartsWith(ClientPatcher.GeneratedLocHeader, StringComparison.Ordinal) == true;
        if (!ours) return;
        var temp = Path.Combine(Path.GetTempPath(), "ro3emu-loc_en-" + Environment.ProcessId + ".tsv");
        try
        {
            GameDataExport.WriteLocMap(dir, temp, new GameDataExport.Options { Translations = Translations() });
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(temp, target, overwrite: true);
        }
        catch (Exception e) when (e is ExportException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    public void Dispose() => _http.Dispose();
}
