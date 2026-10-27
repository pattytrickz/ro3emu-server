using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Tencent.IM;

namespace Ro3Emu.Tests;

/// <summary>The recent lines ChatService replays when a role joins a channel (ChatService.JoinAsync).</summary>
public sealed class ChatHistoryTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static bool Line(BotEvent e, string text) => e.IsGame(ChatService.TunnelDown) && e.Body.Length >= 8
        && BitConverter.ToUInt32(e.Body, 0) == 4 && NotifyNewTextChatMessage.Parser.ParseFrom(e.Body.AsSpan(8)).Msg.Content == text;

    [Fact]
    public async Task JoiningReplaysRecentLinesExceptInBarrageChannels()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        // Fixture ChatChannelConfig: channel 3 scrolls its lines across the screen (IsBarrage), channel 1 does not.
        await alice.JoinChatAsync("3_7"); await alice.JoinChatAsync("@TGS#_1_0");
        await alice.SendChatAsync("3_7", 2, "party-before");
        await alice.SendChatAsync("@TGS#_1_0", 2, "world-before");
        await alice.Inbox.WaitAsync(e => Line(e, "world-before"), Wait);

        await bob.JoinChatAsync("3_7"); await bob.JoinChatAsync("@TGS#_1_0");
        await bob.Inbox.WaitAsync(e => Line(e, "world-before"), Wait);
        await alice.SendChatAsync("3_7", 2, "party-live");
        await bob.Inbox.WaitAsync(e => Line(e, "party-live"), Wait);
        Assert.DoesNotContain(bob.Inbox.Snapshot(), e => Line(e, "party-before"));
    }
}
