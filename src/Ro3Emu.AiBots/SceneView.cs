using System.Buffers.Binary;
using System.Threading.Channels;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.AiBots;

/// <summary>One unit the bot can see.</summary>
public sealed class Unit
{
    public ulong Uid { get; init; }
    public int Type { get; init; } // 1 monster, 3 player
    public ulong TemplateId { get; set; }
    public string Name { get; set; } = "";
    public float X, Y, Z;
    public long Hp = 1, MaxHp = 1, Level;
    public bool Dead;
}

/// <summary>
/// A party member as the party sync reports it (42048 roster, 42050 member cards every 2 s, 42051 position and HP):
/// where the member is even when the bot cannot see them.
/// </summary>
public sealed class PartyMember
{
    public long Guid { get; init; }
    public long MapId;
    public int Line;
    public float X, Y, Z;
    public long Hp = 1, MaxHp = 1;
    public bool Dead, Offline;
    public DateTime Updated;
}

/// <summary>Something for the bot's brain to react to, raised from the network thread.</summary>
public abstract record ViewEvent;
public sealed record ChatHeard(long SenderId, string ReceiveId, string Content) : ViewEvent;
public sealed record PartyInvite(long InviterId, long TeamId) : ViewEvent;
public sealed record SceneSwitched(long SceneEntry, long SceneId, int Generation) : ViewEvent;
/// <summary>Someone left the bot's party (the bot itself when kicked), or the party got a new leader.</summary>
public sealed record PartyChanged(long Left, long NewLeader) : ViewEvent;

/// <summary>
/// The bot's picture of its scene, built from what the server pushes (AOI view, movement, attributes, hits), and the
/// inbound events its brain handles. Updated on the network thread; read by the brain under the lock.
/// </summary>
public sealed class SceneView
{
    private const int HpId = 2000, MaxHpId = 101, LevelId = 2002, SpeedId = 113;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, Unit> _units = new();
    private readonly Dictionary<long, int> _bag = new();
    private readonly Dictionary<long, PartyMember> _party = new();
    /// <summary>Recent hits between units: who each unit last hit, and who hit each unit (source, when).</summary>
    private readonly Dictionary<ulong, (ulong Target, DateTime At)> _lastHit = new();
    private readonly Dictionary<ulong, Dictionary<ulong, DateTime>> _hitBy = new();
    private readonly Channel<ViewEvent> _events = Channel.CreateBounded<ViewEvent>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<ViewEvent> Events => _events.Reader;
    public ulong SelfUid { get; set; }
    public long SceneEntry { get; private set; }
    public long SceneId { get; private set; }
    /// <summary>The map line (channel) the bot is on: two players on different lines of a map do not see each other.</summary>
    public int Line { get; private set; }
    /// <summary>Counts scene switches (70010), so the brain loads each one exactly once.</summary>
    public int SceneGeneration { get; private set; }
    public bool Disconnected { get; private set; }

    // The bot's own state. Position is also moved by the bot's walks (BotBrain), the server only corrects it.
    public float X, Y, Z;
    public long Hp = 1, MaxHp = 1, Level = 1, Speed = 550;
    public bool Dead;
    /// <summary>The bot's job (its entity template), and the last GM tool reply it was shown (the marquee).</summary>
    public int Career;
    public string LastNotice = "";
    /// <summary>The monster that last hit the bot, and when.</summary>
    public ulong LastAttacker;
    public DateTime LastHitUtc;

    public object Sync => _lock;

    public void Observe(BotEvent e)
    {
        if (e.Kind == BotEventKind.Disconnect) { Disconnected = true; return; }
        if (e.Kind != BotEventKind.Game) return;
        try { ObserveGame(e.MsgId, e.Body); }
        catch (Google.Protobuf.InvalidProtocolBufferException) { }
    }

    private void ObserveGame(uint msgId, byte[] body)
    {
        switch (msgId)
        {
            case MsgId.SwitchSceneLine:
                var sw = SwitchSceneLineResp.Parser.ParseFrom(body);
                if (sw.Result != 0) return;
                int generation;
                lock (_lock)
                {
                    _units.Clear(); _lastHit.Clear(); _hitBy.Clear();
                    SceneEntry = sw.SceneEntry; SceneId = sw.SceneID; Line = sw.Linenum; generation = ++SceneGeneration;
                }
                _events.Writer.TryWrite(new SceneSwitched(sw.SceneEntry, sw.SceneID, generation));
                break;
            case MsgId.EntitySelf:
                var self = EntitySelf.Parser.ParseFrom(body).Base;
                lock (_lock)
                {
                    SelfUid = self.Uuid;
                    if (self.MovementInfo?.Pos is { } p) (X, Y, Z) = (p.X, p.Y, p.Z);
                    Dead = self.State == EntityState.Dead;
                    if (self.EntityTemplateID != 0) Career = (int)self.EntityTemplateID;
                    foreach (var prop in self.Property) ApplySelf(prop.Idx, prop.Data);
                }
                break;
            case GameAoiSink.SyncAoiView:
                var view = AoIViewChangeData.Parser.ParseFrom(body);
                lock (_lock)
                {
                    if (view.BReset) _units.Clear();
                    foreach (var uid in view.LeaveAoi) _units.Remove(uid);
                    foreach (var b in view.Bases)
                    {
                        if (b.Uuid == SelfUid || b.EntityType is not (1 or 3)) continue;
                        var unit = new Unit { Uid = b.Uuid, Type = b.EntityType, TemplateId = b.EntityTemplateID, Name = b.Name,
                            Dead = b.State == EntityState.Dead };
                        if (b.MovementInfo?.Pos is { } p) (unit.X, unit.Y, unit.Z) = (p.X, p.Y, p.Z);
                        foreach (var prop in b.Property) Apply(unit, prop.Idx, prop.Data);
                        _units[b.Uuid] = unit;
                    }
                }
                break;
            case GameAoiSink.MovementChange:
                var move = MovementChangeNtf.Parser.ParseFrom(body);
                if (move.MovementInfo?.Pos is not { } pos) return;
                lock (_lock)
                {
                    if ((ulong)move.Uid == SelfUid) (X, Y, Z) = (pos.X, pos.Y, pos.Z); // a server correction or teleport
                    else if (_units.TryGetValue((ulong)move.Uid, out var unit)) (unit.X, unit.Y, unit.Z) = (pos.X, pos.Y, pos.Z);
                }
                break;
            case CharacterService.AoiAttrChange:
                var attr = AOIAttrChange.Parser.ParseFrom(body);
                lock (_lock)
                {
                    if (attr.EntityUuid == SelfUid) foreach (var prop in attr.Property) ApplySelf(prop.Idx, prop.Data);
                    else if (_units.TryGetValue(attr.EntityUuid, out var unit)) foreach (var prop in attr.Property) Apply(unit, prop.Idx, prop.Data);
                }
                break;
            case CharacterService.EntityBaseChange:
                var change = EntityChange.Parser.ParseFrom(body);
                lock (_lock)
                    foreach (var b in change.Bases)
                    {
                        if (b.Uuid == SelfUid)
                        {
                            Dead = b.State == EntityState.Dead;
                            if (b.EntityTemplateID != 0) Career = (int)b.EntityTemplateID; // 31049 after a job change
                        }
                        else if (_units.TryGetValue(b.Uuid, out var unit)) unit.Dead = b.State == EntityState.Dead;
                    }
                break;
            case CombatService.SkillHit:
                var hit = SkillHitInfo.Parser.ParseFrom(body);
                lock (_lock)
                    foreach (var d in hit.Dsts)
                    {
                        if (d.DamageType != DamageType.Heal && hit.SrcID != d.DstID)
                        {
                            var now = DateTime.UtcNow;
                            _lastHit[(ulong)hit.SrcID] = ((ulong)d.DstID, now);
                            if (!_hitBy.TryGetValue((ulong)d.DstID, out var sources)) _hitBy[(ulong)d.DstID] = sources = new();
                            sources[(ulong)hit.SrcID] = now;
                            if (_hitBy.Count > 512) PruneHits(now - TimeSpan.FromSeconds(30));
                        }
                        if ((ulong)d.DstID == SelfUid)
                        {
                            if (d.IsDead) Dead = true;
                            if (_units.TryGetValue((ulong)hit.SrcID, out var source) && source.Type == 1)
                                (LastAttacker, LastHitUtc) = ((ulong)hit.SrcID, DateTime.UtcNow);
                        }
                        else if (_units.TryGetValue((ulong)d.DstID, out var unit) && d.IsDead) unit.Dead = true;
                    }
                break;
            case ChatService.TunnelDown:
                // uint32 LE cmd ‖ int32 LE error ‖ body; cmd 4 is NOTIFY_NEW_TEXT_CHAT_MESSAGE.
                if (body.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(body) != 4) return;
                var msg = Tencent.IM.NotifyNewTextChatMessage.Parser.ParseFrom(body.AsSpan(8)).Msg;
                if (msg != null && long.TryParse(msg.SenderId, out long sender))
                    _events.Writer.TryWrite(new ChatHeard(sender, msg.ReceiveId, msg.Content));
                break;
            case CharacterService.ItemsSync:
                // 5009 is a dataset increment: every item it adds or rewrites appears whole as an ItemBaseInfo.
                var items = new List<ItemBaseInfo>();
                CollectItems(DBItemsInfoIncrement.Parser.ParseFrom(body), items);
                lock (_lock) foreach (var item in items) if (item.ItemGuid != 0) _bag[item.ItemGuid] = item.ItemEntry;
                break;
            case GmCommands.BroadcastNotify:
                string notice = BroadCastNotify.Parser.ParseFrom(body).Content;
                lock (_lock) LastNotice = notice;
                break;
            case TeamService.TeamNotifyInvitee:
                var invite = NotifyInvitee.Parser.ParseFrom(body);
                _events.Writer.TryWrite(new PartyInvite(invite.InviteGuid, invite.TeamTotalSimpleInfo?.Info?.TeamId ?? 0));
                break;
            case TeamService.TeamOperateBroadcast:
                var oper = TeamOperateNotify.Parser.ParseFrom(body);
                if (oper.Oper == TeamOperationType.TeamOperTypeKick && (ulong)oper.TargetGuid == SelfUid)
                    _events.Writer.TryWrite(new PartyChanged(Left: oper.TargetGuid, NewLeader: 0));
                else if (oper.Oper == TeamOperationType.TeamOperTypeChangeLeader)
                    _events.Writer.TryWrite(new PartyChanged(Left: 0, NewLeader: oper.TargetGuid));
                break;
            case TeamService.TeamBroadcastLeave:
                _events.Writer.TryWrite(new PartyChanged(Left: BroadCastMemberLeave.Parser.ParseFrom(body).Guid, NewLeader: 0));
                break;
            case TeamService.TeamAll:
                var all = TeamTotalDetailInfo.Parser.ParseFrom(body);
                lock (_lock) { _party.Clear(); foreach (var card in all.MemberList.Values) Card(card); }
                break;
            case TeamService.TeamMemberUpdate:
                var cards = TeamMemberChangeResp.Parser.ParseFrom(body);
                lock (_lock) foreach (var card in cards.MemberList.Values) Card(card);
                break;
            case TeamService.TeamMemberBattleUpdate:
                var battle = TeamMemberBattleInfoChangeRes.Parser.ParseFrom(body);
                lock (_lock) if (_party.TryGetValue(battle.Guid, out var member) && battle.BattleInfo is { } info) Battle(member, info);
                break;
        }
    }

    /// <summary>A member card (42048/42050): map, line, online state, and the battle info when it carries one.</summary>
    private void Card(TeamMemberDetailInfo card)
    {
        if (!_party.TryGetValue(card.Guid, out var member)) _party[card.Guid] = member = new PartyMember { Guid = card.Guid };
        member.MapId = card.MapId;
        member.Line = card.LineId;
        member.Offline = card.OnlineState == TeamMemberOnlineState.Offline;
        member.Updated = DateTime.UtcNow;
        if (card.BattleInfo is { } info) Battle(member, info);
    }

    /// <summary>42051 battle info: hpPercent / maxHpValue carry the absolute HP (TeamService.Detail), pos the position.</summary>
    private static void Battle(PartyMember member, TeamMemberBattleInfo info)
    {
        if (info.Pos is { } p && (p.X != 0 || p.Z != 0)) (member.X, member.Y, member.Z) = (p.X, p.Y, p.Z);
        member.MaxHp = Math.Max(1, info.MaxHpValue);
        member.Hp = info.HpPercent;
        member.Dead = info.PlayerLifeStatus == EPlayerLifeStatus.Dead;
        member.Updated = DateTime.UtcNow;
    }

    /// <summary>A copy of a party member's last reported state, or null.</summary>
    public PartyMember? Member(long guid)
    {
        lock (_lock)
            return _party.TryGetValue(guid, out var m)
                ? new PartyMember { Guid = m.Guid, MapId = m.MapId, Line = m.Line, X = m.X, Y = m.Y, Z = m.Z, Hp = m.Hp,
                    MaxHp = m.MaxHp, Dead = m.Dead, Offline = m.Offline, Updated = m.Updated }
                : null;
    }

    public void ForgetParty() { lock (_lock) _party.Clear(); }

    private void PruneHits(DateTime before)
    {
        foreach (var (uid, hit) in _lastHit.ToArray()) if (hit.At < before) _lastHit.Remove(uid);
        foreach (var (uid, sources) in _hitBy.ToArray())
        {
            foreach (var (source, at) in sources.ToArray()) if (at < before) sources.Remove(source);
            if (sources.Count == 0) _hitBy.Remove(uid);
        }
    }

    /// <summary>What <paramref name="uid"/> hit last, if within <paramref name="within"/>.</summary>
    public ulong LastTargetOf(ulong uid, TimeSpan within)
    {
        lock (_lock)
            return _lastHit.TryGetValue(uid, out var hit) && DateTime.UtcNow - hit.At <= within ? hit.Target : 0;
    }

    /// <summary>Units that hit <paramref name="uid"/> within <paramref name="within"/>.</summary>
    public ulong[] AttackersOf(ulong uid, TimeSpan within)
    {
        var since = DateTime.UtcNow - within;
        lock (_lock)
            return _hitBy.TryGetValue(uid, out var sources) ? sources.Where(p => p.Value >= since).Select(p => p.Key).ToArray() : [];
    }

    /// <summary>Whether <paramref name="source"/> has hit <paramref name="uid"/> within <paramref name="within"/>.</summary>
    public bool HasHit(ulong source, ulong uid, TimeSpan within)
    {
        lock (_lock)
            return _hitBy.TryGetValue(uid, out var sources) && sources.TryGetValue(source, out var at) && DateTime.UtcNow - at <= within;
    }

    private void ApplySelf(long idx, long value)
    {
        switch (idx)
        {
            case HpId: Hp = value; if (value > 0) Dead = false; break;
            case MaxHpId: MaxHp = Math.Max(1, value); break;
            case LevelId: Level = Math.Max(1, value); break;
            case SpeedId: if (value > 0) Speed = value; break;
        }
    }

    private static void Apply(Unit unit, long idx, long value)
    {
        switch (idx)
        {
            case HpId: unit.Hp = value; if (value <= 0 && unit.Type == 1) unit.Dead = true; break;
            case MaxHpId: unit.MaxHp = Math.Max(1, value); break;
            case LevelId: unit.Level = value; break;
        }
    }

    private static void CollectItems(Google.Protobuf.IMessage message, List<ItemBaseInfo> into)
    {
        if (message is ItemBaseInfo item) { into.Add(item); return; }
        foreach (var field in message.Descriptor.Fields.InFieldNumberOrder())
        {
            if (field.FieldType != Google.Protobuf.Reflection.FieldType.Message) continue;
            var value = field.Accessor.GetValue(message);
            if (field.IsMap)
            {
                foreach (var entry in (System.Collections.IDictionary)value)
                    if (((System.Collections.DictionaryEntry)entry).Value is Google.Protobuf.IMessage m) CollectItems(m, into);
            }
            else if (field.IsRepeated) { foreach (var m in (System.Collections.IEnumerable)value) if (m is Google.Protobuf.IMessage child) CollectItems(child, into); }
            else if (value is Google.Protobuf.IMessage child) CollectItems(child, into);
        }
    }

    /// <summary>Bag item guids holding <paramref name="entry"/>.</summary>
    public long[] Guids(int entry)
    {
        lock (_lock) return _bag.Where(p => p.Value == entry).Select(p => p.Key).ToArray();
    }

    public Unit[] Units(int type)
    {
        lock (_lock) return _units.Values.Where(u => u.Type == type).Select(Copy).ToArray();
    }

    public Unit? Find(ulong uid)
    {
        lock (_lock) return _units.TryGetValue(uid, out var u) ? Copy(u) : null;
    }

    public (float X, float Y, float Z) Position { get { lock (_lock) return (X, Y, Z); } }

    public void MovedTo(float x, float y, float z) { lock (_lock) (X, Y, Z) = (x, y, z); }

    private static Unit Copy(Unit u) => new()
    {
        Uid = u.Uid, Type = u.Type, TemplateId = u.TemplateId, Name = u.Name, X = u.X, Y = u.Y, Z = u.Z,
        Hp = u.Hp, MaxHp = u.MaxHp, Level = u.Level, Dead = u.Dead,
    };
}
