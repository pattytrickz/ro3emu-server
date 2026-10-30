using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Who may play (ServerOptions.Access, house rule). "open": any new name registers on its first login, as the research
/// server always did. "approved": a new name registers only after an admin approved it (GM "!access approve name", or
/// POST /ro3emu/access from the host); the approval is used up by that registration. In either mode a blocked name is
/// refused, existing accounts included, and "reset" lets the next login of an existing name set a new password (the
/// admin never sees one). "supporter" marks an account that earns Supporter Tokens alongside its Crystals
/// (NewActivityService.PassPlay). Kept in data/access.json; names compare case-insensitively. With
/// ServerOptions.Supporter.Entitlements on, supporter status is a paid-through date instead (SupporterEntitlements) and
/// the list only says who gets the legacy end date.
/// </summary>
public sealed class AccessList(IOptions<ServerOptions> options, ILogger<AccessList> log, SupporterEntitlements entitlements)
{
    public sealed class State
    {
        public List<string> Approved { get; set; } = new();
        public List<string> Blocked { get; set; } = new();
        public List<string> Reset { get; set; } = new();
        /// <summary>Accounts that earn Supporter Tokens alongside their Crystals (F2PRules.SupporterToken).</summary>
        public List<string> Supporters { get; set; } = new();
    }

    /// <summary>The account is a supporter now: paid through a later time, or (entitlements off) on the permanent list.</summary>
    public bool IsSupporter(string? account)
    {
        if (account == null) return false;
        if (entitlements.Enabled) return entitlements.IsActive(account);
        lock (_sync) return Has(Load().Supporters, account);
    }

    /// <summary>The permanent supporters of data/access.json, who get an end date with "!supporter legacy".</summary>
    public IReadOnlyList<string> LegacySupporters()
    {
        lock (_sync) return Load().Supporters.ToList();
    }

    private readonly object _sync = new();
    private State? _state;

    private string PathOf() => Path.Combine(options.Value.DataDir, "access.json");

    public bool ApprovalRequired => string.Equals(options.Value.Access.Mode, "approved", StringComparison.OrdinalIgnoreCase);

    private State Load()
    {
        if (_state != null) return _state;
        var path = PathOf();
        _state = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(path)) ?? new State() : new State();
        return _state;
    }

    private void Save(State state)
    {
        var path = PathOf();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    private static bool Has(List<string> list, string name) => list.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a login of <paramref name="name"/> may do: refuse (with the reason for the log), register, or log in.</summary>
    public enum Decision { Login, Register, ResetPassword, Refuse }

    public (Decision Decision, string Why) Decide(string name, bool exists)
    {
        lock (_sync)
        {
            var state = Load();
            if (Has(state.Blocked, name)) return (Decision.Refuse, "blocked");
            if (exists) return Has(state.Reset, name) ? (Decision.ResetPassword, "password reset") : (Decision.Login, "existing account");
            if (!ApprovalRequired) return (Decision.Register, "open registration");
            return Has(state.Approved, name) ? (Decision.Register, "approved") : (Decision.Refuse, "not approved");
        }
    }

    /// <summary>After the registration or reset a <see cref="Decide"/> allowed: the approval or reset is used up.</summary>
    public void Used(string name)
    {
        lock (_sync)
        {
            var state = Load();
            int removed = state.Approved.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                + state.Reset.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Save(state);
        }
    }

    public const string Usage = "approve|unapprove|block|unblock|reset|supporter|unsupporter <name> | list";

    /// <summary>"approve name", "block name", "list"...: the admin side, shared by the GM command and the host's HTTP tool.</summary>
    public string Run(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        lock (_sync)
        {
            var state = Load();
            if (verb == "list" || parts.Length < 2)
                return $"Registration {(ApprovalRequired ? "needs approval" : "is open")}. Approved (not yet registered): "
                    + Names(state.Approved) + ". Blocked: " + Names(state.Blocked) + ". Password reset pending: " + Names(state.Reset)
                    + ". Supporters: " + Names(state.Supporters)
                    + ". Usage: " + Usage;
            string name = parts[1];
            if (name.Length > 64 || name.Any(char.IsWhiteSpace)) return "Names have no spaces and at most 64 characters.";
            string reply;
            switch (verb)
            {
                case "approve":
                    if (!Has(state.Approved, name)) state.Approved.Add(name);
                    reply = $"{name} may now register: their first login with that name sets the password.";
                    break;
                case "unapprove":
                    state.Approved.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    reply = $"{name} is no longer approved (an account already made stays).";
                    break;
                case "block":
                    if (!Has(state.Blocked, name)) state.Blocked.Add(name);
                    reply = $"{name} is blocked: logins are refused from now on.";
                    break;
                case "unblock":
                    state.Blocked.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    reply = $"{name} is unblocked.";
                    break;
                case "reset":
                    if (!Has(state.Reset, name)) state.Reset.Add(name);
                    reply = $"{name}'s next login sets a new password (whatever they type then).";
                    break;
                case "supporter":
                    if (!Has(state.Supporters, name)) state.Supporters.Add(name);
                    reply = entitlements.Enabled
                        ? $"{name} is on the legacy list, but status now comes from paid-through dates: use !supporter grant."
                        : $"{name} is a supporter: they now earn Supporter Tokens with every Crystal they earn.";
                    break;
                case "unsupporter":
                    state.Supporters.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    reply = $"{name} is no longer a supporter: tokens already earned stay, new ones stop.";
                    break;
                default:
                    return "Usage: " + Usage;
            }
            Save(state);
            log.LogInformation("Access: {Line} -> {Reply}", line, reply);
            return reply;
        }
    }

    private static string Names(List<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    /// <summary>
    /// The host tool's commands, the <see cref="Run"/> ones plus two for the website's sign-up (through the admin
    /// bridge), which make the account at approval so no first login can claim an approved name:
    /// <list type="bullet">
    /// <item>"taken name": "taken Name" when an account has the name in any case, "approved Name" when a first-login
    /// approval is waiting for it, "blocked Name", else "free".</item>
    /// <item>"create name hash salt" (base64; AccountHash format): "created name", or "taken Name" / "blocked name".
    /// A waiting approval for the name is used up.</item>
    /// </list>
    /// </summary>
    public async Task<string> RunAsync(string line, AccountStore accounts)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        if (verb is not ("taken" or "create")) return Run(line);
        if (parts.Length < 2 || parts[1].Length > 64) return "Usage: taken <name> | create <name> <hash> <salt>";
        string name = parts[1];
        if (await accounts.FindNameAsync(name) is { } existing) return "taken " + existing;
        lock (_sync)
        {
            var state = Load();
            if (Has(state.Blocked, name)) return "blocked " + name;
            if (verb == "taken")
                return state.Approved.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is { } waiting
                    ? "approved " + waiting : "free";
        }
        byte[] hash, salt;
        try { hash = Convert.FromBase64String(parts.Length > 2 ? parts[2] : ""); salt = Convert.FromBase64String(parts.Length > 3 ? parts[3] : ""); }
        catch (FormatException) { return "Usage: create <name> <hash base64> <salt base64>"; }
        if (hash.Length != 32 || salt.Length != 16) return "Usage: create <name> <hash base64> <salt base64> (32-byte hash, 16-byte salt)";
        if (!await accounts.CreateAsync(name, hash, salt)) return "taken " + (await accounts.FindNameAsync(name) ?? name);
        Used(name);
        log.LogInformation("Access: create {Name} -> created", name);
        return "created " + name;
    }
}
