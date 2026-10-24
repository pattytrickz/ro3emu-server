using Ro3Emu.AiBots;

namespace Ro3Emu.Tests;

/// <summary>
/// How an AI player behaves in a real player's party (Ro3Emu.AiBots.PartyRules, linked here from the bot runner): whom
/// it joins, which kills it leaves to the players, and when its party has no player left in it.
/// </summary>
public sealed class AiBotPartyRulesTests
{
    [Theory]
    [InlineData(40, 25, true)]
    [InlineData(40, 55, true)]
    [InlineData(40, 24, false)]
    [InlineData(40, 56, false)]
    public void InvitesAreAcceptedFromPlayersWithinFifteenBaseLevels(long bot, long inviter, bool accepted) =>
        Assert.Equal(accepted, PartyRules.InLevelRange(bot, inviter));

    [Fact]
    public void AnInviterOfUnknownLevelIsDeclined() => Assert.False(PartyRules.InLevelRange(40, null));

    [Theory]
    [InlineData(1000, 1000, 50, false)] // fresh
    [InlineData(310, 1000, 50, false)]  // above the safe share
    [InlineData(300, 1000, 50, true)]   // at the safe share
    [InlineData(100, 1000, 50, true)]
    [InlineData(800, 1000, 400, true)]  // two of the bot's hits could finish it
    [InlineData(1000, 1000, 600, true)] // too weak to touch at all
    public void APlayersMonsterIsLeftForTheirLastHit(long hp, long maxHp, long botHit, bool leave) =>
        Assert.Equal(leave, PartyRules.LeaveToPlayer(hp, maxHp, botHit));

    [Theory]
    [InlineData(40, 40, true)]
    [InlineData(45, 40, true)]  // up to five levels above the player
    [InlineData(46, 40, false)] // far stronger: only the player's own fights
    [InlineData(50, 40, false)]
    [InlineData(30, 40, true)]  // below the player
    public void AFarStrongerBotStartsNoFightsOfItsOwn(long bot, long player, bool starts) =>
        Assert.Equal(starts, PartyRules.StartsOwnFights(bot, player));

    [Fact]
    public void APlayerOfUnknownLevelGetsNoFightsStartedForThem() => Assert.False(PartyRules.StartsOwnFights(40, 0));

    [Fact]
    public void APartyWithNoPlayerLeftIsLeft()
    {
        Func<long, bool> isBot = id => id >= 900;
        Assert.True(PartyRules.NoPlayerLeft([900], 900, isBot));
        Assert.True(PartyRules.NoPlayerLeft([900, 901, 902], 900, isBot));
        Assert.False(PartyRules.NoPlayerLeft([100, 900, 901], 900, isBot));
        // Not in a party (no roster, or one without the bot).
        Assert.False(PartyRules.NoPlayerLeft([], 900, isBot));
        Assert.False(PartyRules.NoPlayerLeft([901, 902], 900, isBot));
    }
}
