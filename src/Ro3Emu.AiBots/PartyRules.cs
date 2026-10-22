namespace Ro3Emu.AiBots;

/// <summary>
/// An AI player's manners in a real player's party (owner decision, issue #58): it joins players near its own level,
/// leaves the kills near them to them, starts no fights of its own next to a much weaker player, and leaves a party
/// once no player is left in it, so it can be invited again.
/// </summary>
public static class PartyRules
{
    /// <summary>Invites are accepted from players within this many base levels of the bot.</summary>
    public const int LevelRange = 10;

    /// <summary>A player's monster at or below this share of its HP (%) is left for their last hit.</summary>
    public const int SafeHpPercent = 30;

    /// <summary>How close to a player of the party a monster counts as theirs: the reach of the bot's party fight.</summary>
    public const float NearPlayer = 20f;

    /// <summary>A bot more than this many base levels above the player it follows is far stronger (owner direction, #40).</summary>
    public const int StrongerBy = 5;

    /// <summary>
    /// Whether the bot may start on a fresh monster near the player, one they are not fighting: not when it is far
    /// stronger than them (it only joins their fights then), nor when their level is unknown.
    /// </summary>
    public static bool StartsOwnFights(long botLevel, long playerLevel) =>
        playerLevel > 0 && botLevel - playerLevel <= StrongerBy;

    /// <summary>Whether the inviter is within <see cref="LevelRange"/> base levels; an unknown level is declined.</summary>
    public static bool InLevelRange(long botLevel, long? inviterLevel) =>
        inviterLevel is > 0 and var level && Math.Abs(level - botLevel) <= LevelRange;

    /// <summary>The whisper that explains a declined invite.</summary>
    public static string LevelDecline(long botLevel, long? inviterLevel) => inviterLevel switch
    {
        null or <= 0 => "Sorry, I can't join a party right now.",
        var level when level < botLevel => "Sorry, I'm too high level to help you here.",
        _ => "Sorry, you're too high level for me to keep up with you.",
    };

    /// <summary>
    /// Whether the bot stops hitting a player's monster (one near a player of the party or one they hit) and leaves the
    /// kill to them: it is at or below the safe HP share, or low enough that two of the bot's hits (a margin for a
    /// critical hit and for HP the bot has not seen drop yet) could finish it.
    /// </summary>
    public static bool LeaveToPlayer(long hp, long maxHp, long botHit) =>
        hp * 100 <= SafeHpPercent * Math.Max(1, maxHp) || hp <= 2 * botHit;

    /// <summary>The bot is in a party (the roster holds it) in which every other member is an AI player.</summary>
    public static bool NoPlayerLeft(IReadOnlyCollection<long> members, long self, Func<long, bool> isBot) =>
        members.Contains(self) && members.All(member => member == self || isBot(member));
}
