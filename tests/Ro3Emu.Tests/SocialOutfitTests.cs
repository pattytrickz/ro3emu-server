using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed class SocialOutfitTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3-outfit-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        app = ServerHost.Create([], o => { o.DataDir = directory; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g; });
        await app.StartAsync(); http = $"http://127.0.0.1:{h}";
    }
    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
    private async Task<BotClient> Login(bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id); return bot;
    }
    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
        => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), TimeSpan.FromSeconds(8))).Body);

    [ClientDataFact]
    public async Task DefaultFrameEquipIsDurableAndPublishedOnLoginAndAssetRefresh()
    {
        long role;
        await using (var bot = await Login())
        {
            role = (long)bot.Self!.Uuid;
            var initial = await Next(bot, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser,
                x => x.SocialOutfitEquip.ContainsKey(2));
            Assert.Equal(200000, initial.SocialOutfitEquip[2].All.CfgId);
            await bot.SendGameAsync(SocialOutfitService.Equip, new EquipSocialOutfitReq { Type = 2, CfgId = 200000 });
            Assert.Equal(0, (await Next(bot, SocialOutfitService.Equip, EquipSocialOutfitRes.Parser)).Err);
            var stored = DBSocialOutfit.Parser.ParseFrom(app.Services.GetRequiredService<RoleStore>().LoadSocialOutfit(role)!);
            Assert.Equal(200000, stored.SocialOutfitEquip[2].CfgId);
            Assert.Equal((int)SocialOutfitStatus.Permanent, stored.SocialOutfitContainer[2].SocialOutfitInfos[200000].Status);
        }
        await using var again = await Login(create: false);
        var restored = await Next(again, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser,
            x => x.SocialOutfitEquip.ContainsKey(2));
        Assert.Equal(200000, restored.SocialOutfitEquip[2].All.CfgId);
        await again.SendGameAsync(1023, new PlayerAssetReq { AssetType = { 68 } });
        var replayed = await Next(again, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser,
            x => x.SocialOutfitEquip.ContainsKey(2));
        Assert.Equal(200000, replayed.SocialOutfitEquip[2].All.CfgId);
    }

    [ClientDataFact]
    public async Task FailedUnlockTransactionKeepsVoucherAndOwnershipUnchanged()
    {
        await using var bot = await Login();
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account=alice", new StringContent("!item 1920003 1"))).EnsureSuccessStatusCode();
        var accounts = app.Services.GetRequiredService<AccountStore>();
        var store = app.Services.GetRequiredService<RoleStore>();
        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        var character = app.Services.GetRequiredService<CharacterService>();
        long guid = session.Items.Single(i => i.Entry == 1920003).Guid;
        byte[]? before = store.LoadSocialOutfit(session.Role!.Id);
        FixtureSql.Execute(accounts, """
            CREATE TRIGGER reject_frame_receipt BEFORE INSERT ON operation_receipt
            WHEN NEW.command_kind='cd13a/character-social-outfit'
            BEGIN SELECT RAISE(ABORT,'frame test rollback'); END
            """);
        await app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => app.Services.GetRequiredService<OwnerRegistry>()
            .RunSessionAsync("dispatcher", [ReservationKey.Role(session.Role.Id)], session, async () =>
            {
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => character.UseItemAsync(session,
                    new ItemUseReq { ItemGuid = guid, ItemCount = 1 }));
            }));
        Assert.Equal(before, store.LoadSocialOutfit(session.Role.Id));
        Assert.Single(session.Items.Where(i => i.Guid == guid));
        Assert.Single(app.Services.GetRequiredService<ItemStore>().Load(session.Role.Id).Where(i => i.Guid == guid));
        FixtureSql.Execute(accounts, "DROP TRIGGER reject_frame_receipt");
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
    }

    [ClientDataTheory]
    [InlineData(1920003, 200003, false)] // Genesis Canticle: the reported frame
    [InlineData(1920003, 200003, true)] // committed write with lost acknowledgement
    [InlineData(1920007, 200007, false)]
    public async Task FrameItemUnlockAndConsumptionAreDurableAndDuplicateUseDoesNotSpend(int entry, long frame, bool loseAck)
    {
        long role;
        await using (var bot = await Login())
        {
            role = (long)bot.Self!.Uuid;
            await Next(bot, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser);
            using var client = new HttpClient();
            (await client.PostAsync($"{http}/ro3emu/gm?account=alice", new StringContent($"!item {entry} 2"))).EnsureSuccessStatusCode();
            var bag = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.Values.Any(p => p.Inc?.Items.Values.Any(i => i.All?.ItemEntry == entry) == true));
            var vouchers = bag.PackInfo.Values.SelectMany(p => p.Inc?.Items.Values ?? [])
                .Where(i => i.All?.ItemEntry == entry).Select(i => i.All).ToArray();
            Assert.Equal(2, vouchers.Length); // Frame vouchers are unstackable.
            var voucher = vouchers[0];
            if (loseAck) app.Services.GetRequiredService<AccountStore>().FailNextEconomyAcknowledgementForTest();
            await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = voucher.ItemGuid, ItemCount = 1 });
            Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
            var store = app.Services.GetRequiredService<RoleStore>();
            var saved = DBSocialOutfit.Parser.ParseFrom(store.LoadSocialOutfit(role)!);
            Assert.Equal((int)SocialOutfitStatus.Permanent, saved.SocialOutfitContainer[2].SocialOutfitInfos[frame].Status);
            await bot.SendGameAsync(SocialOutfitService.Equip, new EquipSocialOutfitReq { Type = 2, CfgId = frame });
            Assert.Equal(0, (await Next(bot, SocialOutfitService.Equip, EquipSocialOutfitRes.Parser)).Err);
            await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = vouchers[1].ItemGuid, ItemCount = 1 });
            Assert.NotEqual(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
            saved = DBSocialOutfit.Parser.ParseFrom(store.LoadSocialOutfit(role)!);
            Assert.Equal(frame, saved.SocialOutfitEquip[2].CfgId);
        }
        await using var again = await Login(create: false);
        var restored = await Next(again, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser,
            x => x.SocialOutfitEquip.ContainsKey(2));
        Assert.Equal(frame, restored.SocialOutfitEquip[2].All.CfgId);
        var inventory = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            x => x.PackInfo.Values.Any(p => p.All?.Items.Values.Any(i => i.ItemEntry == entry) == true));
        Assert.Equal(1, inventory.PackInfo.Values.SelectMany(p => p.All?.Items.Values ?? [])
            .Where(i => i.ItemEntry == entry).Sum(i => i.Stack));
    }

    [ClientDataTheory]
    [InlineData(2, 200003)] // earned frame, not a default entitlement
    [InlineData(1, 200000)] // correct ID, forged slot
    [InlineData(2, 99999999)]
    [InlineData(2147483648L, 200000)] // do not truncate an int64 slot before validating
    public async Task InvalidOrLockedFrameCannotReplaceTheEquippedFrame(long type, long id)
    {
        await using var bot = await Login();
        await bot.SendGameAsync(SocialOutfitService.Equip, new EquipSocialOutfitReq { Type = 2, CfgId = 200000 });
        Assert.Equal(0, (await Next(bot, SocialOutfitService.Equip, EquipSocialOutfitRes.Parser)).Err);
        var store = app.Services.GetRequiredService<RoleStore>();
        byte[] before = store.LoadSocialOutfit((long)bot.Self!.Uuid)!;
        await bot.SendGameAsync(SocialOutfitService.Equip, new EquipSocialOutfitReq { Type = type, CfgId = id });
        Assert.NotEqual(0, (await Next(bot, SocialOutfitService.Equip, EquipSocialOutfitRes.Parser)).Err);
        Assert.Equal(before, store.LoadSocialOutfit((long)bot.Self.Uuid));
    }
}
