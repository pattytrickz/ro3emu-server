using System.Collections.Concurrent;

namespace Ro3Emu.AiBots;

/// <summary>What the hunters share so they spread out: which bot fights which monster, and how many hunt each map.</summary>
public sealed class Crowd
{
    private static readonly TimeSpan ClaimLife = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<ulong, (long Bot, DateTime At)> _claims = new();
    private readonly ConcurrentDictionary<long, long> _where = new();

    /// <summary>Marks the monster as this bot's; false when another bot claimed it recently.</summary>
    public bool Claim(ulong monster, long bot)
    {
        var now = DateTime.UtcNow;
        if (_claims.Count > 2000)
            foreach (var (k, v) in _claims) if (now - v.At > ClaimLife) _claims.TryRemove(k, out _);
        var claim = _claims.AddOrUpdate(monster, (bot, now), (_, old) => old.Bot == bot || now - old.At > ClaimLife ? (bot, now) : old);
        return claim.Bot == bot;
    }

    public bool ClaimedByOther(ulong monster, long bot) =>
        _claims.TryGetValue(monster, out var c) && c.Bot != bot && DateTime.UtcNow - c.At <= ClaimLife;

    /// <summary>Records which map a hunter is on (0: none, e.g. offline or a town bot).</summary>
    public void Locate(long bot, long map)
    {
        if (map == 0) _where.TryRemove(bot, out _);
        else _where[bot] = map;
    }

    public int Hunters(long map) => _where.Values.Count(m => m == map);

    public int AllHunters => _where.Count;
}
