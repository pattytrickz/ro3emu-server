using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Sol RO3 house rates (ServerOptions.KillExpPercent, CardDropRolls): the data's numbers unless set.</summary>
public sealed class HouseRateTests
{
    [Fact]
    public void KillExpScalesByPercentAndKeepsAtLeastOne()
    {
        Assert.Equal(100, new ServerOptions().KillExpPercent);
        Assert.Equal(300, RewardService.HouseExp(200, 150));
        Assert.Equal(200, RewardService.HouseExp(200, 100));
        Assert.Equal(1, RewardService.HouseExp(1, 50));
        Assert.Equal(0, RewardService.HouseExp(0, 150)); // a kill without EXP stays without
    }

    [Fact]
    public void ExtraCardRollsKeepOnlyTheirCards()
    {
        Assert.Equal(1, new ServerOptions().CardDropRolls);
        const int Card = 1440100, Zeny = 2, Potion = 1000001;
        int rolls = 0;
        void Roll(List<(int Entry, int Count)> into)
        {
            rolls++;
            into.Add((Zeny, 10));
            into.Add((Potion, 1));
            if (rolls != 2) into.Add((Card, 1)); // the second roll finds no card
        }

        var drops = new List<(int Entry, int Count)>();
        RewardService.RollWithExtraCards(drops, Roll, e => e == Card, 3);
        Assert.Equal(3, rolls);
        Assert.Equal([(Zeny, 10), (Potion, 1), (Card, 1), (Card, 1)], drops);

        var once = new List<(int Entry, int Count)>();
        rolls = 0;
        RewardService.RollWithExtraCards(once, Roll, e => e == Card, 1);
        Assert.Equal(1, rolls);
        Assert.Equal([(Zeny, 10), (Potion, 1), (Card, 1)], once);
    }

    [Fact]
    public void AFractionalCardMultiplierRollsTheRestAtThatChance()
    {
        var random = new Random(7);
        Assert.Equal(1, RewardService.WholeRolls(1, random));
        Assert.Equal(3, RewardService.WholeRolls(3, random));
        const int Kills = 20_000;
        int total = 0;
        for (int i = 0; i < Kills; i++)
        {
            int rolls = RewardService.WholeRolls(1.5, random);
            Assert.InRange(rolls, 1, 2);
            total += rolls;
        }
        Assert.InRange(total / (double)Kills, 1.47, 1.53);
    }

    [Fact]
    public void ARealPartyOfThreeEarnsTheBonus()
    {
        Assert.Equal(0, new ServerOptions().RealPartyExpPercent); // off unless the realm sets it
        Assert.Equal(125, RewardService.RealPartyPercent(25, 3, 3));
        Assert.Equal(125, RewardService.RealPartyPercent(25, 3, 5));
        Assert.Equal(100, RewardService.RealPartyPercent(25, 3, 2)); // two real players and an AI player: no bonus
        Assert.Equal(100, RewardService.RealPartyPercent(0, 3, 5));
        Assert.Equal(100, RewardService.RealPartyPercent(25, 1, 1)); // a solo player is never a party
        Assert.Equal(187, RewardService.HouseExp(100, 150 * 100 / 100 * 125 / 100)); // x1.5 house rate, then +25%
    }

    [Fact]
    public void HandsOnMeansAutoBattleOffLongEnough()
    {
        var options = new ServerOptions();
        Assert.Equal((0, 0, 10), (options.HandsOnExpPercent, options.HandsOnLootPercent, options.HandsOnSeconds)); // off by default
        var session = new GameSession(null!, null!) { Account = "player" };
        Assert.True(session.HandsOn(10_000)); // auto-battle never turned on this login
        session.AutoBattle = true;
        Assert.False(session.HandsOn(0));
        session.AutoBattle = false; // just turned off: not yet hands-on
        Assert.False(session.HandsOn(10_000));
        Assert.True(session.HandsOn(0));
        long changed = session.AutoBattleChangedMs;
        session.AutoBattle = false; // the client repeating the same state doesn't restart the wait
        Assert.Equal(changed, session.AutoBattleChangedMs);
    }

    [Fact]
    public void AHandsOnLootRollKeepsItemsAndCardsButNoExp()
    {
        const int Card = 1440100, Zeny = 2, BaseExp = 100;
        void Roll(List<(int Entry, int Count)> into) { into.Add((BaseExp, 50)); into.Add((Zeny, 10)); into.Add((Card, 1)); }
        var drops = new List<(int Entry, int Count)>();
        RewardService.RollWithExtraCards(drops, Roll, e => e == Card, cardRolls: 1, dropRolls: 2); // hands-on: one more roll
        Assert.Equal([(BaseExp, 50), (Zeny, 10), (Card, 1), (Zeny, 10), (Card, 1)], drops);
        Assert.Equal(233, RewardService.HouseExp(100, 150 * 100 / 100 * 125 / 100 * 125 / 100)); // x1.5, party +25%, hands-on +25%
    }
}
