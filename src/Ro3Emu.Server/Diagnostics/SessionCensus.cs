using Microsoft.Extensions.Options;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>
/// Who is connected, as the game loop last saw it. Session state changes only on the loop, so the HTTP endpoints read
/// this snapshot, taken on the loop once a second, instead of the live sessions: a session's map is several fields
/// (scene, line, instance) that a reader on another thread could see half changed.
/// </summary>
public sealed class SessionCensus(SessionRegistry sessions, IOptions<ServerOptions>? options = null) : IGameTicker
{
    private readonly string[] _notPlayers = options?.Value.StatusExcludedAccounts ?? [];

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private TimeSpan _due;
    private volatile Snapshot _current = Snapshot.Empty;

    /// <summary>The latest snapshot; empty until the loop's first tick.</summary>
    public Snapshot Current => _current;

    public void Tick(GameTime time)
    {
        if (time.Now < _due) return;
        _due = time.Now + Interval;
        _current = Take(sessions, _notPlayers);
    }

    /// <summary>Counts the registered sessions; call it on the loop (or where nothing else changes them).</summary>
    /// <param name="notPlayers">ServerOptions.StatusExcludedAccounts: roles of these accounts are not counted as players.</param>
    public static Snapshot Take(SessionRegistry sessions, string[]? notPlayers = null)
    {
        int login = 0;
        List<Placement> roles = [];
        foreach (var session in sessions.All())
        {
            if (session.Role == null) login++;
            else roles.Add(new Placement(session.RuntimeMap, session.SceneLoading,
                notPlayers is { Length: > 0 } && ServerOptions.AccountListed(notPlayers, session.Account)));
        }
        return new Snapshot(DateTimeOffset.UtcNow, login, roles);
    }

    /// <param name="Login">Sessions without a selected role (role screen).</param>
    /// <param name="Roles">One entry per session with a selected role, loading or in the world.</param>
    public sealed record Snapshot(DateTimeOffset? CapturedUtc, int Login, IReadOnlyList<Placement> Roles)
    {
        public static readonly Snapshot Empty = new(null, 0, []);

        /// <summary>The public player count (/ro3emu/status): roles in the world or loading, without the excluded accounts.</summary>
        public int Players => Roles.Count(r => !r.NotPlayer);
    }

    /// <param name="NotPlayer">The role's account is in ServerOptions.StatusExcludedAccounts (the AI players).</param>
    public readonly record struct Placement(RuntimeMapKey Map, bool Loading, bool NotPlayer = false);
}
