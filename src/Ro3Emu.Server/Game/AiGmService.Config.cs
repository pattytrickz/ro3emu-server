using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

public sealed partial class AiGmService
{
    internal const string ConfigStateKey = "aigm_config";
    /// <summary>The events the staff panel can allow, by the action that runs each.</summary>
    internal static readonly string[] Actions = ["rates", "mvp", "gift", "logingift", "announce"];
    private const int MaxRulesLength = 4000, MaxPoolItems = 50;
    private Config? _config;
    private bool _configLoaded;

    /// <summary>
    /// What the staff panel saved (action "setconfig", with <see cref="AiGmOptions.ConfigKey"/>): its on/off switch, the
    /// owner's rules for the AI GM to follow (the server enforces the limits, not the rules), the item pool that takes
    /// the place of <see cref="AiGmOptions.ItemPool"/>, and the events it may run.
    /// </summary>
    public sealed class Config
    {
        public bool Enabled { get; set; }
        public string Rules { get; set; } = "";
        public AiGmItem[] ItemPool { get; set; } = [];
        public string[] Events { get; set; } = [];
        public string? By { get; set; }
        public DateTimeOffset? At { get; set; }
    }

    /// <summary>The staff panel's saved config, or null before the first save (the settings alone apply).</summary>
    private Config? Saved()
    {
        if (_configLoaded) return _config;
        _configLoaded = true;
        try
        {
            _config = roles.ServerState(ConfigStateKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<Config>(json) : null;
        }
        catch (JsonException e)
        {
            LogBadState(log, e);
        }
        return _config;
    }

    private AiGmItem[] Pool() => Saved()?.ItemPool ?? Limits.ItemPool;

    /// <summary>An event action runs only when the panel allows it (before any save, every one does).</summary>
    private bool Allows(string action) => !Actions.Contains(action) || Saved() is not { } saved || saved.Events.Contains(action);

    private async Task<Reply> SetConfigAsync(Request r, string? key)
    {
        if (!KeyMatches(key)) return Refused("setconfig needs the staff panel's key");
        if (r.Config is not { } c) return Refused("config: the switch, rules, item pool and events");
        // JSON may send null for any of them.
        var saved = new Config
        {
            Enabled = c.Enabled, Rules = (c.Rules ?? "").Trim(), ItemPool = c.ItemPool ?? [], Events = [.. (c.Events ?? []).Distinct()],
            By = Clip(c.By, 60), At = clock.GetUtcNow(),
        };
        if (ConfigRefusal(saved) is { } why) return Refused(why);
        await roles.SetServerStateAsync(ConfigStateKey, JsonSerializer.Serialize(saved));
        _config = saved;
        _configLoaded = true;
        string summary = $"{(saved.Enabled ? "on" : "off")}, {saved.ItemPool.Length} pool item(s), events {string.Join(',', saved.Events)}";
        Audit($"setconfig by {saved.By}: {summary}");
        LogAction(log, "setconfig", summary);
        return new(true, "saved", saved);
    }

    private string? ConfigRefusal(Config c)
    {
        if (c.Rules.Length > MaxRulesLength) return $"rules: at most {MaxRulesLength} characters";
        if (c.ItemPool.Length > MaxPoolItems) return $"item pool: at most {MaxPoolItems} items";
        if (c.ItemPool.GroupBy(p => p.Item).FirstOrDefault(g => g.Count() > 1) is { } twice) return $"item {twice.Key} is in the pool twice";
        if (c.ItemPool.FirstOrDefault(p => p.MaxCount < 1 || data.Row("ItemConfig", p.Item) is null) is { } bad)
            return $"item {bad.Item}: not in the game data, or a max count below 1";
        return c.Events.Except(Actions).FirstOrDefault() is { } unknown ? $"unknown event {unknown}" : null;
    }

    private bool KeyMatches(string? key) => Limits.ConfigKey.Length > 0 && key is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Limits.ConfigKey));
}
