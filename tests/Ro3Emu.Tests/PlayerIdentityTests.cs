using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Shared;

namespace Ro3Emu.Tests;

public sealed class PlayerIdentityTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnlyExplicitGmAccountsGetGmBadgesAndGmWinsOverSupporter()
    {
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GmAccounts = ["alice"];
            o.GmToolAccounts = ["bob"];
        });
        var access = host.Service<AccessList>();
        access.Run("supporter alice");
        var identity = host.Service<PlayerIdentityService>();
        Assert.Equal(SupporterIdentityRules.GameMaster, identity.Badge("alice"));
        Assert.Equal(SupporterIdentityRules.Ordinary, identity.Badge("bob"));
        Assert.Equal(SupporterIdentityRules.Ordinary, identity.Badge(null));
        access.Run("supporter BOB");
        Assert.Equal(SupporterIdentityRules.Supporter, identity.Badge("bob"));
        await using var gm = await host.InWorldAsync("Alice");
        Assert.Equal(SupporterIdentityRules.GameMaster, Marker(gm.Self!));
    }

    [Fact]
    public async Task OpenResearchGmCommandsDoNotLabelEveryPlayerGm()
    {
        await using var host = await SyntheticHost.StartAsync(o => { o.GmAccounts = []; o.GmOpenWhenEmpty = true; });
        await using var alice = await host.InWorldAsync("Alice");
        Assert.Equal(SupporterIdentityRules.Ordinary, Marker(alice.Self!));
    }

    [Fact]
    public async Task SupporterFrameAndIdentitySurviveRestartAndRevocationUpdatesSelfAndViewers()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.GmOpenWhenEmpty = false);
        host.Service<AccessList>().Run("supporter Alice");
        await using var bob = await host.InWorldAsync("Bob");
        long role;
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            Assert.Equal(SupporterIdentityRules.Supporter, Marker(alice.Self));
            var legacy = await Next(alice, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser, p => p.SyncAllMark);
            Assert.DoesNotContain(SupporterIdentityRules.Frame, legacy.SocialOutfitEquip.Values.Select(v => v.All.CfgId));
            Assert.NotEqual(0, await Equip(alice)); // no new cosmetic ids before the client registers their config
            Assert.DoesNotContain(SupporterIdentityRules.Frame, (await PublicBase(bob, role)).SocialOutfitData.SocialOutfit.Values);
            await Enable(bob);
            var initial = await Enable(alice, consumeInitial: false);
            Assert.Equal(SupporterIdentityRules.Frame, initial.SocialOutfitEquip[SupporterIdentityRules.FrameSlot].All.CfgId);
            Assert.Equal(3, initial.SocialOutfitContainer[SupporterIdentityRules.FrameSlot].All.SocialOutfitInfos[SupporterIdentityRules.Frame].Status);
            Assert.Equal(SupporterIdentityRules.Frame, (await PublicBase(bob, role)).SocialOutfitData.SocialOutfit[SupporterIdentityRules.FrameSlot]);
            Assert.NotEqual(0, await Equip(bob));
            Assert.Equal(0, await Equip(alice));
            // A writable generic custom-data producer must not replace the reserved identity marker.
            await Assert.ThrowsAsync<ArgumentException>(() => host.Service<GameLoop>().InvokeAsync(async () =>
                await host.Service<WorldCoreService>().SetCustomAsync(Session(host, "alice"), new CustomData
                {
                    Id = SupporterIdentityRules.BadgeCustomData,
                    CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = SupporterIdentityRules.GameMaster }
                })));
        }
        await host.RestartAsync();
        await using var viewer = await host.InWorldAsync("Bob", create: false);
        await Enable(viewer);
        await using var again = await host.InWorldAsync("Alice", create: false);
        Assert.Equal(SupporterIdentityRules.Supporter, Marker(again.Self!));
        var restored = await Enable(again);
        Assert.Equal(SupporterIdentityRules.Frame, restored.SocialOutfitEquip[SupporterIdentityRules.FrameSlot].All.CfgId);
        host.Service<AccessList>().Run("unsupporter alice");
        var changed = await Next(viewer, WorldCoreService.CustomChange, CustomDataChange.Parser, p => p.EntityId == role
            && p.CustomDatas.Any(d => d.Id == SupporterIdentityRules.BadgeCustomData && d.CustomParam.LongParam == 0));
        Assert.Equal(0, changed.CustomDatas.Single(d => d.Id == SupporterIdentityRules.BadgeCustomData).CustomParam.LongParam);
        await Next(again, WorldCoreService.CustomChange, CustomDataChange.Parser, p => p.EntityId == role);
        Assert.NotEqual(0, await Equip(again));
        var after = await PublicBase(viewer, role);
        Assert.DoesNotContain(SupporterIdentityRules.Frame, after.SocialOutfitData.SocialOutfit.Values);
    }

    [Fact]
    public async Task EachCharacterOnASupporterAccountReceivesTheFrame()
    {
        await using var host = await SyntheticHost.StartAsync();
        host.Service<AccessList>().Run("supporter alice");
        await using (var first = await host.InWorldAsync("Alice"))
            await Enable(first);
        await using var second = host.NewBot();
        await second.LoginAsync("alice", "pw");
        await second.EnterGateAsync();
        var role = await second.CreateRoleAsync("SecondCharacter");
        await second.EnterWorldAsync(role.RoleBaseInfo.Id);
        var state = await Enable(second);
        Assert.Equal(SupporterIdentityRules.Supporter, Marker(second.Self!));
        Assert.Equal(SupporterIdentityRules.Frame, state.SocialOutfitEquip[2].All.CfgId);
    }

    [Fact]
    public async Task StoredUnlockCannotGrantSupporterFrameAndOtherSavedFrameChoicesStayIntact()
    {
        await using var host = await SyntheticHost.StartAsync();
        var stored = new DBSocialOutfit();
        stored.SocialOutfitContainer[2] = new DBSocialOutfitContainer();
        stored.SocialOutfitContainer[2].SocialOutfitInfos[SupporterIdentityRules.Frame] = new DBSocialOutfitInfo
        { CfgId = SupporterIdentityRules.Frame, Status = 3 };
        stored.SocialOutfitContainer[2].SocialOutfitInfos[123] = new DBSocialOutfitInfo { CfgId = 123, Status = 3 };
        stored.SocialOutfitEquip[2] = new DBSocialOutfitEquip { CfgId = 123 };
        var data = host.Service<GameData>();
        var ordinary = SocialOutfitService.View(data, 1, 1000, stored, 0);
        Assert.False(ordinary.SocialOutfitContainer[2].SocialOutfitInfos.ContainsKey(SupporterIdentityRules.Frame));
        var supporter = SocialOutfitService.View(data, 1, 1000, stored, 0, supporter: true);
        Assert.Equal(123, supporter.SocialOutfitEquip[2].CfgId);
        Assert.True(supporter.SocialOutfitContainer[2].SocialOutfitInfos.ContainsKey(SupporterIdentityRules.Frame));
    }

    [Theory]
    [InlineData(0, "Player", "Player")]
    [InlineData(1, "Player", "[Supporter] Player")]
    [InlineData(2, "Player", "[GM] Player")]
    [InlineData(99, "Player", "Player")]
    [InlineData(0, "<color=cyan>Fake</color>", "\u2039color=cyan\u203aFake\u2039/color\u203a")]
    public void NameLabelsUseOnlyTheServerBadgeAndEscapeRichText(int badge, string name, string expected) =>
        Assert.Equal(expected, SupporterIdentityRules.DisplayName(badge, name));

    private static long Marker(EntityBase entity) => entity.CustomDatas.Single(d => d.Id == SupporterIdentityRules.BadgeCustomData).CustomParam.LongParam;

    private static GameSession Session(SyntheticHost host, string account) =>
        host.Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    private static async Task<int> Equip(BotClient bot)
    {
        await bot.SendGameAsync(SocialOutfitService.Equip, new EquipSocialOutfitReq { CfgId = SupporterIdentityRules.Frame, Type = 2 });
        return (await Next(bot, SocialOutfitService.Equip, EquipSocialOutfitRes.Parser)).Err;
    }

    private static async Task<DBSocialOutfitIncrement> Enable(BotClient bot, bool consumeInitial = true)
    {
        if (consumeInitial) await Next(bot, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser, p => p.SyncAllMark);
        await bot.SendGameBytesAsync(SupporterIdentityRules.ClientReadyMessage, [1]);
        return await Next(bot, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser, p => p.SyncAllMark);
    }

    private static async Task<RoleAdditionBaseInfo> PublicBase(BotClient bot, long role)
    {
        var card = await bot.GetRoleFieldsAsync([role], ["Base"]);
        var fields = (Dictionary<object, object?>)((Dictionary<object, object?>)((object?[])card["Results"]!).Single()!)["Data"]!;
        return RoleAdditionBaseInfo.Parser.ParseFrom((byte[])fields["Base"]!);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(p => p.IsGame(id) && (match == null || match(parser.ParseFrom(p.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }
}
