using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.Launcher;

/// <summary>launcher.json: built into the exe (LauncherConfigFile in the csproj); a file beside the exe wins.</summary>
public sealed class LauncherConfig
{
    [JsonPropertyName("repository")] public string Repository { get; set; } = "http://localhost:8790/";
    [JsonPropertyName("channel")] public string Channel { get; set; } = "test";
    [JsonPropertyName("server")] public string? Server { get; set; }
    [JsonPropertyName("serverName")] public string ServerName { get; set; } = "RO3Emu";
    /// <summary>
    /// "repository": the patched client comes from the repository. "publisher": the official client comes from the
    /// publisher's update service and is patched on this PC for <see cref="Server"/>; the repository then only carries
    /// launcher updates and news.
    /// </summary>
    [JsonPropertyName("game")] public string Game { get; set; } = "repository";
    /// <summary>
    /// "publisher" mode: the newest publisher release this launcher's patch is known to work with (0 = no limit). A newer
    /// release is not installed over a game folder: its client may not reach <see cref="Server"/>. Release 140069
    /// (20 Oct 2026) replaced the two DLLs the patch rewrites, and its login never reaches the server.
    /// </summary>
    [JsonPropertyName("maxGameRelease")] public long MaxGameRelease { get; set; }
    /// <summary>
    /// "publisher" mode: the oldest publisher release known to log in on <see cref="Server"/> (0 = no limit). When the main
    /// download mirror cannot be reached, a fallback mirror may only have an older release; a new install takes it only
    /// if it is at least this one. 30 Oct 2026: CloudFront serves 140035, from before the 140069 login change.
    /// </summary>
    [JsonPropertyName("minGameRelease")] public long MinGameRelease { get; set; }
    /// <summary>"publisher" mode: a page explaining what to do when the game download fails (shown as a link under the
    /// message). Empty: no link.</summary>
    [JsonPropertyName("downloadHelp")] public string? DownloadHelp { get; set; }
    /// <summary>Buttons on the page (website, Discord, ...): label, url and an optional icon name the page knows.</summary>
    [JsonPropertyName("links")] public List<LauncherLink> Links { get; set; } = new();

    public bool PublisherGame => string.Equals(Game, "publisher", StringComparison.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Options = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static LauncherConfig Load(string besideExe)
    {
        try
        {
            if (File.Exists(besideExe)) return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllBytes(besideExe), Options) ?? new();
            using var stream = typeof(LauncherConfig).Assembly.GetManifestResourceStream("launcher.json");
            return stream is null ? new() : JsonSerializer.Deserialize<LauncherConfig>(stream, Options) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

public sealed class LauncherLink
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("icon")] public string? Icon { get; set; }
}

/// <summary>Per-user choices and records (%APPDATA%\RO3Emu\launcher-settings.json).</summary>
public sealed class UserSettings
{
    [JsonPropertyName("clientDirectory")] public string? ClientDirectory { get; set; }
    /// <summary>"publisher" mode: the publisher's release sequence each game folder was last installed or checked at.</summary>
    [JsonPropertyName("installedSequence")] public Dictionary<string, long> InstalledSequence { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"publisher" mode: what each game folder was last patched with (see PublisherGame.PatchStamp).</summary>
    [JsonPropertyName("patchStamp")] public Dictionary<string, string> PatchStamp { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RO3Emu", "launcher-settings.json");

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
