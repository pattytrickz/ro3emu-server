using Ro3Emu.AiBots;

namespace Ro3Emu.Tests;

/// <summary>
/// How an AI player behaves in a real player's party (Ro3Emu.AiBots.PartyRules, linked here from the bot runner): whom
/// it joins, which kills it leaves to the players, and when its party has no player left in it.
/// </summary>
public sealed class AiBotPartyRulesTests
{
    [Theory]
    [InlineData(40, 30, true)]
    [InlineData(40, 50, true)]
    [InlineData(40, 29, false)]
    [InlineData(40, 51, false)]
    public void InvitesAreAcceptedFromPlayersWithinTenBaseLevels(long bot, long inviter, bool accepted) =>
        Assert.Equal(accepted, PartyRules.InLevelRange(bot, inviter));

    [Fact]
    public void AnInviterOfUnknownLevelIsDeclined() => Assert.False(PartyRules.InLevelRange(40, null));

    [Fact]
    public void TheDeclineSaysWhichSideIsTooHigh()
    {
        Assert.Equal("Sorry, I'm too high level to help you here.", PartyRules.LevelDecline(60, 20));
        Assert.Equal("Sorry, you're too high level for me to keep up with you.", PartyRules.LevelDecline(20, 60));
    }

    [Theory]
    [InlineData(1000, 1000, 50, false)] // fresh
    [InlineData(310, 1000, 50, false)]  // above the safe share
    [InlineData(300, 1000, 50, true)]   // at the safe share
    [InlineData(100, 1000, 50, true)]
    [InlineData(800, 1000, 400, true)]  // two of the bot's hits could finish it
    [InlineData(1000, 1000, 600, true)] // too weak to touch at all
    public void APlayersMonsterIsLeftForTheirLastHit(long hp, long maxHp, long botHit, bool leave) =>
        Assert.Equal(leave, PartyRules.LeaveToPlayer(hp, maxHp, botHit));

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
