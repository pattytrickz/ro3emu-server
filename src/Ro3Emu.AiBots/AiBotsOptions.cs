using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ro3Emu.AiBots;

/// <summary>
/// aibots.json: what the runner connects to and how chatty the bots are. Everything else (accounts, passwords,
/// names, personas) is generated once into the roster file next to it.
/// </summary>
public sealed class AiBotsOptions
{
    /// <summary>The server's HTTP base (SDK login, server list, gate public key).</summary>
    public string Server { get; set; } = "http://127.0.0.1:5380";

    /// <summary>Game tables and nav grids, read only (the server's GameDataDir or DataDir).</summary>
    public string GameDataDir { get; set; } = "/opt/ro3emu/RO3Emu-server/data";

    /// <summary>How many bots to run. The roster grows to this size; extra roster entries just stay offline.</summary>
    public int Count { get; set; } = 20;

    /// <summary>Account names are this prefix plus a two-digit number (aibot01, aibot02, ...).</summary>
    public string AccountPrefix { get; set; } = "aibot";

    /// <summary>Character names are this prefix plus a generated name; the server allows 16 characters in total.</summary>
    public string NamePrefix { get; set; } = "[AI]";

    /// <summary>Share of bots that live in town (wander, chat) instead of hunting.</summary>
    public double TownShare { get; set; } = 0.3;

    /// <summary>Seconds between bot logins at startup, so a restart does not hit the server all at once.</summary>
    public double LoginSpacingSeconds { get; set; } = 4;

    /// <summary>Hunting maps (MapConfig ids). Empty: every field map with monsters (scene type 1).</summary>
    public List<long> HuntMaps { get; set; } = new();

    /// <summary>The town for town bots. 10100 is Prontera.</summary>
    public long TownMap { get; set; } = 10100;

    /// <summary>Minutes between status lines (one per bot) in the log.</summary>
    public double StatusMinutes { get; set; } = 30;

    public ChatOptions Chat { get; set; } = new();

    public LlmOptions Llm { get; set; } = new();

    [JsonIgnore] public string Directory { get; private set; } = "";

    public static AiBotsOptions Load(string path)
    {
        var options = File.Exists(path)
            ? JsonSerializer.Deserialize<AiBotsOptions>(File.ReadAllText(path), Json.Options) ?? new()
            : new AiBotsOptions();
        options.Directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!File.Exists(path)) Json.WritePrivate(path, options);
        return options;
    }
}

public sealed class ChatOptions
{
    /// <summary>Average minutes between one bot's unprompted map chat lines (only said with a real player near).</summary>
    public double AmbientMinutes { get; set; } = 25;

    /// <summary>At most one unprompted line per map in this many seconds, across all bots.</summary>
    public double AmbientMapGapSeconds { get; set; } = 180;

    /// <summary>Chance a nearby bot answers a map chat line that does not name it (greetings, questions).</summary>
    public double OverhearChance { get; set; } = 0.25;

    /// <summary>Replies one player can get from all bots together per hour.</summary>
    public int RepliesPerPlayerPerHour { get; set; } = 30;

    /// <summary>Accept party invites from real players and follow them.</summary>
    public bool AcceptPartyInvites { get; set; } = true;
}

public sealed class LlmOptions
{
    /// <summary>Off: canned lines only.</summary>
    public bool Enabled { get; set; } = true;

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>A file holding only the Anthropic API key (chmod 600). ANTHROPIC_API_KEY wins when set.</summary>
    public string ApiKeyFile { get; set; } = "anthropic.key";

    /// <summary>Model calls per UTC day across all bots; past it the bots fall back to canned lines.</summary>
    public int MaxCallsPerDay { get; set; } = 400;

    public int MaxReplyChars { get; set; } = 140;
}

/// <summary>One bot's identity, generated once and kept (roster.json, chmod 600: it holds the passwords).</summary>
public sealed class RosterEntry
{
    public string Account { get; set; } = "";
    public string Password { get; set; } = "";
    public string Name { get; set; } = "";
    public int Sex { get; set; } = 1;
    public int Persona { get; set; }
    public bool Town { get; set; }
}

public static class Roster
{
    public static List<RosterEntry> LoadOrGrow(AiBotsOptions options, string path)
    {
        var roster = File.Exists(path)
            ? JsonSerializer.Deserialize<List<RosterEntry>>(File.ReadAllText(path), Json.Options) ?? new()
            : new List<RosterEntry>();
        int before = roster.Count;
        var random = new Random();
        var taken = roster.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int maxName = 16 - options.NamePrefix.Length;
        while (roster.Count < options.Count)
        {
            int n = roster.Count + 1;
            string name;
            do name = options.NamePrefix + Personas.RandomName(random, maxName);
            while (!taken.Add(name));
            roster.Add(new RosterEntry
            {
                Account = $"{options.AccountPrefix}{n:00}",
                Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                Name = name,
                Sex = random.Next(1, 3),
                Persona = random.Next(Personas.All.Count),
                // Spread town bots evenly through the roster so a small Count still gets some.
                Town = Math.Floor(n * options.TownShare) > Math.Floor((n - 1) * options.TownShare),
            });
        }
        if (roster.Count != before) Json.WritePrivate(path, roster);
        return roster;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Writes the file readable by its owner only (it may hold passwords).</summary>
    public static void WritePrivate<T>(string path, T value)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}
