namespace Ro3Emu.AiBots;

/// <summary>
/// An AI player's manners in a real player's party (owner decision, issue #58): it joins players near its own level,
/// leaves the kills near them to them, starts no fights of its own next to a much weaker player, and leaves a party
/// once no player is left in it, so it can be invited again.
/// </summary>
public static class PartyRules
{
    /// <summary>Invites are accepted from players within this many base levels of the bot (owner decision: 10 in #58,
    /// 15 since 24 Oct 2026, so a small realm's players find enough AI party members).</summary>
    public const int LevelRange = 15;

    /// <summary>A player's monster at or below this share of its HP (%) is left for their last hit.</summary>
    public const int SafeHpPercent = 30;

    /// <summary>How close to a player of the party a monster counts as theirs: the reach of the bot's party fight.</summary>
    public const float NearPlayer = 20f;

    /// <summary>A bot more than this many base levels above the player it follows is far stronger (owner direction, #40).</summary>
    public const int StrongerBy = 5;

    /// <summary>
    /// A monster more than this many levels above the bot is too strong for it, even in the leader's fight (owner, 27 Oct:
    /// in Orc Dungeon the bots joined fights with Orc Zombies and Orc Archers far above them, died three times and left
    /// the party). The bot already fought back only up to this margin.
    /// </summary>
    public const int FightsUpTo = 3;

    /// <summary>Whether the bot takes on a monster of this level; an unknown level (0) counts as fair.</summary>
    public static bool CanFight(long botLevel, long monsterLevel) => monsterLevel <= 0 || monsterLevel <= botLevel + FightsUpTo;

    /// <summary>
    /// Whether the bot may start on a fresh monster near the player, one they are not fighting: not when it is far
    /// stronger than them (it only joins their fights then), nor when their level is unknown.
    /// </summary>
    public static bool StartsOwnFights(long botLevel, long playerLevel) =>
        playerLevel > 0 && botLevel - playerLevel <= StrongerBy;

    /// <summary>Whether the inviter is within <see cref="LevelRange"/> base levels; an unknown level is declined.</summary>
    public static bool InLevelRange(long botLevel, long? inviterLevel) =>
        inviterLevel is > 0 and var level && Math.Abs(level - botLevel) <= LevelRange;

    /// <summary>
    /// Whether the bot stops hitting a player's monster (one near a player of the party or one they hit) and leaves the
    /// kill to them: it is at or below the safe HP share, or low enough that two of the bot's hits (a margin for a
    /// critical hit and for HP the bot has not seen drop yet) could finish it.
    /// </summary>
    public static bool LeaveToPlayer(long hp, long maxHp, long botHit) =>
        hp * 100 <= SafeHpPercent * Math.Max(1, maxHp) || hp <= 2 * botHit;

    /// <summary>
    /// Deaths in a player's party within <see cref="DeathWindow"/> after which the bot leaves it (owner decision, 26 Oct
    /// 2026): following a leader into a field far above it, a bot died there over and over (280 times in a day) and
    /// helped no one. Dungeons are exempt: the party clears those together and has revives.
    /// </summary>
    public const int DeathLimit = 3;

    public static readonly TimeSpan DeathWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long the bot declines new invites from the leader of a party it left for dying too often.</summary>
    public static readonly TimeSpan RejoinAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Counts a death in the party, forgetting those older than <see cref="DeathWindow"/>, and says whether that makes
    /// <see cref="DeathLimit"/>: the bot leaves the party.
    /// </summary>
    public static bool TooManyDeaths(Queue<DateTime> deaths, DateTime now)
    {
        deaths.Enqueue(now);
        while (deaths.Count > 0 && now - deaths.Peek() > DeathWindow) deaths.Dequeue();
        return deaths.Count >= DeathLimit;
    }

    /// <summary>The bot is in a party (the roster holds it) in which every other member is an AI player.</summary>
    public static bool NoPlayerLeft(IReadOnlyCollection<long> members, long self, Func<long, bool> isBot) =>
        members.Contains(self) && members.All(member => member == self || isBot(member));
}
