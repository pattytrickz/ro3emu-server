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
