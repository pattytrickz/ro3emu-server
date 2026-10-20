using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Http;

/// <summary>
/// Which launcher patched each client. From 1.2.2 the Sol RO3 Launcher patches the client's URLs under
/// /launcher/{version} (LauncherController.PrepareForServerAsync); the prefix is stripped before routing and the
/// version recorded at SDK login per account. <see cref="ServerOptions.MinLauncherVersion"/> sets the oldest allowed.
/// </summary>
public sealed class LauncherVersions(IOptions<ServerOptions> options)
{
    public const string Prefix = "/launcher/";
    public const string ItemKey = "ro3emu.launcher";

    /// <summary>Loopback logins (the AI bots, local tools) are never turned away.</summary>
    public static readonly Version Exempt = new(int.MaxValue, 0);

    private readonly ConcurrentDictionary<string, Version?> _byAccount = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loopback requests count as <see cref="Exempt"/> (tests switch this off to play an outdated client).</summary>
    public bool ExemptLoopback { get; set; } = true;

    /// <summary>The launcher that patched the client of this request: Exempt for loopback, null for none.</summary>
    public Version? Of(HttpContext context) =>
        ExemptLoopback && context.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip) ? Exempt
            : context.Items.TryGetValue(ItemKey, out var v) ? v as Version : null;

    /// <summary>"/launcher/1.2.2/sdk/login" -> 1.2.2 and "/sdk/login". False when the path has no launcher prefix.</summary>
    public static bool TryStrip(PathString path, out Version? version, out PathString rest)
    {
        version = null;
        rest = path;
        string value = path.Value ?? "";
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        int slash = value.IndexOf('/', Prefix.Length);
        string text = slash < 0 ? value[Prefix.Length..] : value[Prefix.Length..slash];
        version = Version.TryParse(text, out var parsed) ? parsed : null;
        rest = slash < 0 ? new PathString("/") : new PathString(value[slash..]);
        return true;
    }

    public void Record(string account, Version? version) => _byAccount[account] = version;

    public bool Outdated(Version? version) =>
        options.Value.MinLauncherVersion is { Length: > 0 } text && Version.TryParse(text, out var min)
        && version != Exempt && (version == null || version < min);

    /// <summary>An account whose last SDK login came from an outdated client. Unknown accounts are not judged.</summary>
    public bool Outdated(string? account) =>
        account != null && _byAccount.TryGetValue(account, out var version) && Outdated(version);
}
