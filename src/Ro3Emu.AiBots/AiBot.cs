using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.AiBots;

/// <summary>
/// One AI player: logs in like a real client (BotClient), then hunts monsters suited to its level, hangs out in town
/// or follows the real player whose party it joined, and chats (ChatHub/Talker). Reconnects with back-off whenever
/// the server drops it (restart, kick).
/// </summary>
public sealed class AiBot
{
    private const float PlayerBuffer = 6f, SightRange = 35f;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    private readonly RosterEntry _entry;
    private readonly AiBotsOptions _options;
    private readonly Atlas _atlas;
    private readonly ChatHub _hub;
    private readonly Crowd _crowd;
    private readonly Progression _progression;
    private readonly GmTool _gm;
    private readonly string _pem;
    private readonly Action<string> _log;
    private readonly Action _saveRoster;
    private readonly Random _random = new();
    private Talker _talker;

    // One connection's state (SessionAsync resets it).
    private BotClient _bot = null!;
    private SceneView _view = new();
    private SemaphoreSlim _send = new(1);
    private SemaphoreSlim _talk = new(1);
    private long _roleId;
    private int _handledScene;
    private List<Point3>? _path;
    private int _pathIndex;
    private TimeSpan _idleAfterPath;
    private DateTime _idleUntil, _nextMapCheck, _nextSwing, _restUntil, _pathAt;
    private DateTime? _deadSince;
    private ulong _target, _locked;
    private int _refusals, _badSteps, _attackRefusals, _statsLevel;
    private DateTime _nextGrow, _lastDeath;
    private readonly Queue<DateTime> _deaths = new();
    private int _handicap;
    private bool _tooHard;
    private bool _resting;
    private readonly Dictionary<ulong, DateTime> _avoid = new();
    private long _leader;
    private DateTime _leaderSeen, _followTravelAt, _nextHeal, _nextHealLearn;
    /// <summary>A map the leader is on that the bot cannot travel to (an instance, a dungeon): it waits instead.</summary>
    private long _cantFollowMap;
    /// <summary>The learned heal's SkillConfig row id (group * 100 + level), or 0.</summary>
    private int _healSkill;
    /// <summary>Heal groups by job tree: Acolyte line (SkillTreeConfig job 4100) and job 1210.</summary>
    private static readonly int[] HealGroups = [14007, 11405];
    private string _activity = "just arrived";

    public AiBot(RosterEntry entry, AiBotsOptions options, Atlas atlas, Progression progression, GmTool gm, ChatHub hub, Crowd crowd,
        string pem, Action<string> log, Action saveRoster)
    {
        (_entry, _options, _atlas, _progression, _gm, _hub, _crowd, _pem, _saveRoster) =
            (entry, options, atlas, progression, gm, hub, crowd, pem, saveRoster);
        _log = line => log($"{entry.Name}: {line}");
        _talker = NewTalker();
    }

    public string Name => _entry.Name;
    public bool Online { get; private set; }

    public string Status => Online
        ? $"{_entry.Name} lv{_view.Level} {_progression.JobName(_view.Career)} {_atlas.MapName(_view.SceneEntry)} ({_view.X:F0},{_view.Z:F0}) hp {_view.Hp}/{_view.MaxHp}: {_activity}"
        : $"{_entry.Name} offline";

    private Talker NewTalker() => new(_hub, Personas.Of(_entry.Persona), _entry.Name,
        _entry.Name.StartsWith(_options.NamePrefix) ? _entry.Name[_options.NamePrefix.Length..] : _entry.Name, _random, _log);

    // ---------------------------------------------------------------- connection

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(15);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try { await SessionAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (BotRefusedException e) when (e.Operation == "sdk-login")
            {
                _log($"login refused ({e.Result}); is the account approved? ro3-access approve {_entry.Account}");
                backoff = TimeSpan.FromMinutes(5);
            }
            catch (Exception e) { _log($"offline: {e.GetType().Name}: {e.Message}"); }
            finally { Online = false; _crowd.Locate(_roleId, 0); }
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(10)) backoff = TimeSpan.FromSeconds(15);
            try { await Task.Delay(backoff + TimeSpan.FromSeconds(_random.Next(15)), ct); }
            catch (OperationCanceledException) { return; }
            if (backoff < TimeSpan.FromMinutes(5)) backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
        }
    }

    private async Task SessionAsync(CancellationToken ct)
    {
        _view = new SceneView();
        _send = new SemaphoreSlim(1);
        _talk = new SemaphoreSlim(1);
        await using var bot = new BotClient(_options.Server, _pem) { Timeout = TimeSpan.FromSeconds(30) };
        _bot = bot;
        bot.EnableLoadMode(32, _view.Observe);
        await bot.LoginAsync(_entry.Account, _entry.Password);
        await bot.EnterGateAsync();
        _roleId = bot.Roles.FirstOrDefault(r => r.Name == _entry.Name)?.Id ?? await CreateRoleAsync(bot);
        _hub.AddBot(_roleId);
        _view.SelfUid = (ulong)_roleId;
        await bot.EnterWorldAsync(_roleId);
        _handledScene = _view.SceneGeneration;
        Reset();
        Online = true;
        Located();
        await JoinMapChatAsync();
        _log($"online on {_atlas.MapName(_view.SceneEntry)}, level {_view.Level}{(_entry.Town ? ", town bot" : "")}");

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(session.Token);
        try { await SetupAsync(); }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException) { _log($"setup incomplete: {e.Message}"); }
        try { await BrainAsync(session.Token); }
        finally
        {
            session.Cancel();
            try { await heartbeat; } catch (Exception) { }
        }
    }

    private async Task<long> CreateRoleAsync(BotClient bot)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                long id = (await bot.CreateRoleAsync(_entry.Name, _entry.Sex)).RoleBaseInfo.Id;
                _log("created the character");
                return id;
            }
            catch (BotRefusedException) when (attempt < 3)
            {
                // Most likely the name is taken by a real player: pick another one and keep it.
                string old = _entry.Name;
                _entry.Name = _options.NamePrefix + Personas.RandomName(_random, 16 - _options.NamePrefix.Length);
                _talker = NewTalker();
                _saveRoster();
                _log($"name {old} refused, trying {_entry.Name}");
            }
        }
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            await SendAsync(() => _bot.SendGameAsync(MsgId.Heartbeat, new Ping()));
        }
    }

    /// <summary>Sends go one at a time per bot (the brain, chat replies and the heartbeat share the connection).</summary>
    private async Task SendAsync(Func<Task> send)
    {
        await _send.WaitAsync();
        try { await send(); }
        finally { _send.Release(); }
    }

    private void Reset()
    {
        _path = null;
        _target = _locked = 0;
        _resting = false;
        _deadSince = null;
        _leader = 0;
        _cantFollowMap = 0;
        _healSkill = 0;
        _nextHealLearn = DateTime.UtcNow;
        _avoid.Clear();
        _idleUntil = DateTime.UtcNow.AddSeconds(_random.Next(2, 8));
        _nextMapCheck = DateTime.UtcNow.AddSeconds(_random.Next(5, 20));
    }

    // ---------------------------------------------------------------- brain

    private async Task BrainAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (_view.Disconnected) throw new IOException("the server closed the connection");
            while (_view.Events.TryRead(out var e)) await HandleAsync(e, ct);
            if (_view.SceneEntry == 0 || _view.SceneGeneration != _handledScene) continue;
            // Free to be invited again: a party whose players have all left (one from before a reconnect too).
            if (PartyRules.NoPlayerLeft(_view.PartyGuids(), _roleId, _hub.IsBot)) await LeavePartyAsync("no player left in it");
            if (_view.Dead) { await DeadAsync(); continue; }
            _deadSince = null;
            if (Volatile.Read(ref _leader) != 0) await PartyAsync();
            else if (_entry.Town) await TownAsync();
            else await HuntAsync();
            await StepAsync();
            await GrowAsync();
            await AmbientAsync();
        }
    }

    private async Task HandleAsync(ViewEvent e, CancellationToken ct)
    {
        switch (e)
        {
            case SceneSwitched s when s.Generation > _handledScene:
                _path = null;
                _target = _locked = 0;
                await LoadSceneAsync(s.SceneEntry);
                _handledScene = s.Generation;
                Located();
                _nextMapCheck = DateTime.UtcNow.AddSeconds(_random.Next(5, 15));
                await JoinMapChatAsync();
                _log($"now on {_atlas.MapName(s.SceneEntry)}");
                break;
            case ChatHeard heard:
                _ = Task.Run(() => AnswerAsync(heard, ct), ct);
                break;
            case PartyInvite invite:
                _ = Task.Run(() => AnswerInviteAsync(invite, ct), ct);
                break;
            case PartyChanged change:
                if (change.Left == _roleId) { Volatile.Write(ref _leader, 0); _view.ForgetParty(); _log("was removed from the party"); break; }
                long leader = Volatile.Read(ref _leader);
                if (leader == 0) break;
                if (change.NewLeader == _roleId || change.Left == leader) await LeavePartyAsync("my party leader left");
                else if (change.NewLeader != 0) Volatile.Write(ref _leader, change.NewLeader);
                break;
        }
    }

    /// <summary>The client side of a scene switch (70010 -> 30005 -> 31005, 31053), then map chat.</summary>
    private async Task LoadSceneAsync(long sceneEntry)
    {
        using var ack = _bot.Inbox.Expect(x => x.IsGame(MsgId.SceneLoaded));
        using var self = _bot.Inbox.Expect(x => x.IsGame(MsgId.EntitySelf));
        using var end = _bot.Inbox.Expect(x => x.IsGame(MsgId.SelfInfoEndFrameNotify));
        await SendAsync(() => _bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sceneEntry, ViewMaxCount = 149 }));
        var loaded = SceneLoadedResp.Parser.ParseFrom((await ack.WaitAsync(TimeSpan.FromSeconds(30))).Body);
        if (loaded.Result != 0) throw new InvalidOperationException($"scene {sceneEntry} load refused: {loaded.Result}");
        await self.WaitAsync(TimeSpan.FromSeconds(30));
        await end.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private void Located() => _crowd.Locate(_roleId, _entry.Town ? 0 : _view.SceneEntry);

    private async Task JoinMapChatAsync()
    {
        try { await _bot.JoinChatAsync($"4_{_view.SceneId}"); }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException) { _log($"map chat join failed: {e.Message}"); }
    }

    private async Task<bool> TeleportAsync(long map, string why)
    {
        _log($"travelling to {_atlas.MapName(map)} ({why})");
        _path = null;
        _target = 0;
        using var reply = _bot.Inbox.Expect(x => x.IsGame(SceneService.SceneTeleport));
        await SendAsync(() => _bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = map }));
        var response = SceneTeleportRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(15))).Body);
        if (response.Result != 0) _log($"travel refused: {response.Result}");
        // The scene switch (70010) that follows is loaded by HandleAsync.
        return response.Result == 0;
    }

    private async Task DeadAsync()
    {
        var now = DateTime.UtcNow;
        if (_deadSince == null)
        {
            _deadSince = now;
            _path = null;
            _target = _locked = 0;
            _resting = false;
            _activity = "just got knocked out";
            _deaths.Enqueue(DateTime.UtcNow);
            while (_deaths.Count > 0 && DateTime.UtcNow - _deaths.Peek() > TimeSpan.FromMinutes(5)) _deaths.Dequeue();
            if (_deaths.Count >= 2 && !_entry.Town && Volatile.Read(ref _leader) == 0)
            {
                _handicap = Math.Min(40, _handicap + 10);
                _tooHard = true;
                _deaths.Clear();
            }
            _lastDeath = DateTime.UtcNow;
            _log(_tooHard ? $"died again; looking for an easier field (hunting {_handicap} levels lower)" : "died");
            return;
        }
        if (now - _deadSince < TimeSpan.FromSeconds(6)) return;
        using var ack = _bot.Inbox.Expect(x => x.IsGame(CombatService.Relive));
        await SendAsync(() => _bot.SendGameAsync(CombatService.Relive, new EntityReliveReq()));
        var relive = EntityReliveAck.Parser.ParseFrom((await ack.WaitAsync(TimeSpan.FromSeconds(10))).Body);
        if (relive.Result == 0) { lock (_view.Sync) _view.Dead = false; _deadSince = null; _activity = "back on my feet"; }
        else _deadSince = now; // try again in a bit
    }

    // ---------------------------------------------------------------- modes

    private async Task HuntAsync()
    {
        var now = DateTime.UtcNow;
        // Dying keeps sending the bot to easier fields (the handicap), so it never stays where it cannot win.
        long level = Math.Max(1, _view.Level - _handicap);
        if (now >= _nextMapCheck || _tooHard)
        {
            _nextMapCheck = now.AddMinutes(3 + _random.NextDouble() * 4);
            var here = _atlas.Hunt(_view.SceneEntry);
            // Bots fight with normal attacks only, so they hunt a few levels below themselves (Atlas.Fitting); a field
            // is wrong when it is not among the few that fit best (at the top end nothing fits well, and that is fine).
            bool wrong = here == null || _tooHard || !_atlas.Fitting(level).Take(4).Any(m => m.Id == here.Id);
            _tooHard = false;
            // Spread out: a field holding more than its share of the hunters sends some elsewhere.
            int crowd = _crowd.Hunters(_view.SceneEntry);
            bool crowded = crowd > Math.Max(4, _crowd.AllHunters / 3 + 1) && _random.NextDouble() < 0.5;
            if ((wrong || crowded) && _atlas.Choose(level, _random, _crowd.Hunters) is { } pick && pick.Id != _view.SceneEntry
                && (wrong || _crowd.Hunters(pick.Id) + 1 < crowd))
            {
                await TeleportAsync(pick.Id, here == null ? "time to hunt" : wrong ? $"level {_view.Level}, hunting like {level}" : "too crowded here");
                return;
            }
        }
        if (Attacker() is { } attacker) { await DefendAsync(attacker); return; }
        if (await RestAsync()) return;
        var target = CurrentTarget() ?? PickTarget(_view.X, _view.Z, SightRange, level + 1, partyLeader: 0);
        if (target != null) { await EngageAsync(target); return; }
        _target = 0;
        if (_path != null || now < _idleUntil) return;
        // Nothing in sight: head for a spawn point that suits the bot's level.
        if (_atlas.Hunt(_view.SceneEntry) is { } map)
        {
            var points = map.Spawns.Where(s => s.Level <= level + 1).Select(s => s.Point)
                .OrderBy(p => Dist(p.X, p.Z)).Take(12).ToArray();
            if (points.Length > 0)
            {
                var p = points[_random.Next(points.Length)];
                PathTo(p.X + _random.Next(-3, 4), p.Z + _random.Next(-3, 4), TimeSpan.FromSeconds(_random.Next(1, 5)));
                _activity = $"looking for monsters on {map.Name}";
                return;
            }
        }
        Wander(25, TimeSpan.FromSeconds(_random.Next(3, 10)));
    }

    private async Task TownAsync()
    {
        var now = DateTime.UtcNow;
        if (_view.SceneEntry != _options.TownMap)
        {
            if (now >= _nextMapCheck)
            {
                _nextMapCheck = now.AddMinutes(5);
                await TeleportAsync(_options.TownMap, "heading to town");
            }
            return;
        }
        _activity = $"hanging out in {_atlas.MapName(_view.SceneEntry)}";
        if (_path != null || now < _idleUntil) return;
        var map = _atlas.Map(_view.SceneEntry);
        // Mostly short stops; now and then a long one, like a player gone to make tea.
        var idle = _random.NextDouble() < 0.1 ? TimeSpan.FromMinutes(_random.Next(3, 7)) : TimeSpan.FromSeconds(_random.Next(10, 90));
        var born = map?.Born;
        var npcs = map?.Npcs.Where(n => n.Kind is WorldNpcKind.Npc or WorldNpcKind.Kafra && born is { } b
            && MathF.Sqrt((n.X - b.X) * (n.X - b.X) + (n.Z - b.Z) * (n.Z - b.Z)) < 90).ToArray() ?? [];
        if (npcs.Length > 0 && _random.NextDouble() < 0.6)
        {
            var npc = npcs[_random.Next(npcs.Length)];
            PathTo(npc.X + _random.Next(-3, 4), npc.Z + _random.Next(-3, 4), idle);
        }
        else if (born is { } b) PathTo(b.X + _random.Next(-35, 36), b.Z + _random.Next(-35, 36), idle);
        else Wander(30, idle);
    }

    /// <summary>
    /// In a real player's party: be where they are and help. The party sync (SceneView.Member) says where the leader
    /// is even out of sight, so the bot travels to their map, switches to their line and walks to them. Next to them it
    /// heals (a healer), fights what attacks the leader and what the leader is fighting, and takes fresh monsters nearby.
    /// It never takes the last hit on a monster near a player of the party or one they hit (LeaveToPlayers). The bot
    /// stays until the leader leaves, kicks it, or is offline for five minutes.
    /// </summary>
    private async Task PartyAsync()
    {
        long leaderId = Volatile.Read(ref _leader);
        var member = _view.Member(leaderId);
        var leader = _view.Find((ulong)leaderId);
        var now = DateTime.UtcNow;
        if (leader == null)
        {
            await FindLeaderAsync(member, now);
            return;
        }
        _leaderSeen = now;
        _cantFollowMap = 0;
        _activity = $"in a party with {leader.Name}, following them";
        if (await HealAsync(leader, member, now)) return;
        float toLeader = Dist(leader.X, leader.Z);
        if (PartyTarget(leader) is { } fight && toLeader < 20) { await EngageAsync(fight); return; }
        if (Attacker() is { } attacker)
        {
            // Too strong to take alone, or the players' to finish: back to the leader rather than to the revive point.
            if (attacker.Level <= _view.Level + 3 && !LeaveToPlayers(attacker)) { await EngageAsync(attacker); return; }
            if (toLeader > 3) PathTo(leader.X, leader.Z, TimeSpan.Zero, slack: 2);
            return;
        }
        if (_healSkill == 0 && await RestAsync()) return;
        var target = toLeader < 12 ? PickTarget(leader.X, leader.Z, 10, long.MaxValue, leaderId) : null;
        if (target != null) { await EngageAsync(target); return; }
        _target = 0;
        if (toLeader > 4 && (_path == null || now - _pathAt > TimeSpan.FromSeconds(1.5)))
        {
            float a = (float)(_random.NextDouble() * MathF.Tau);
            PathTo(leader.X + 2 * MathF.Cos(a), leader.Z + 2 * MathF.Sin(a), TimeSpan.Zero);
        }
    }

    /// <summary>
    /// The leader's fight: monsters hitting the leader first, then what the leader is hitting, then the bot's own target,
    /// leaving out those low enough for the players' last hit.
    /// </summary>
    private Unit? PartyTarget(Unit leader)
    {
        var window = TimeSpan.FromSeconds(6);
        var candidates = _view.AttackersOf(leader.Uid, window).Select(_view.Find)
            .Append(_view.Find(_view.LastTargetOf(leader.Uid, window)))
            .Append(CurrentTarget());
        foreach (var unit in candidates)
        {
            if (unit is not { Type: 1, Dead: false } || Flat(unit.X, unit.Z, leader.X, leader.Z) > 20
                || _avoid.TryGetValue(unit.Uid, out var until) && until > DateTime.UtcNow || LeaveToPlayers(unit)) continue;
            return unit;
        }
        return null;
    }

    /// <summary>
    /// A monster whose last hit belongs to the players of the bot's party (owner decision, #58): near one of them or hit
    /// by one, and low enough that the bot could finish it (PartyRules.LeaveToPlayer). The bot stops hitting it.
    /// </summary>
    private bool LeaveToPlayers(Unit monster)
    {
        var party = _view.PartyGuids();
        if (party.Length == 0 || !PartyRules.LeaveToPlayer(monster.Hp, monster.MaxHp, _view.BiggestHit)) return false;
        foreach (long guid in party.Append(Volatile.Read(ref _leader)).Distinct())
        {
            if (guid == 0 || guid == _roleId || _hub.IsBot(guid)) continue;
            if (_view.HasHit((ulong)guid, monster.Uid, TimeSpan.FromSeconds(20))) return true;
            if (_view.Find((ulong)guid) is { } player && Flat(player.X, player.Z, monster.X, monster.Z) <= PartyRules.NearPlayer) return true;
        }
        return false;
    }

    /// <summary>The leader is out of sight: wait while they are offline, otherwise go to their map, line and position.</summary>
    private async Task FindLeaderAsync(PartyMember? member, DateTime now)
    {
        if (member == null || member.Offline)
        {
            _activity = "waiting for my party leader to come back";
            if (now - _leaderSeen > TimeSpan.FromMinutes(5))
                await LeavePartyAsync(member == null ? "lost my party" : "my party leader has been offline for five minutes");
            return;
        }
        _leaderSeen = now;
        if (Attacker() is { } attacker) { await DefendAsync(attacker); return; }
        if (member.MapId == 0) return; // no card yet
        if (member.MapId != _view.SceneEntry)
        {
            if (member.MapId == _cantFollowMap)
            {
                _activity = $"waiting for my party leader to come back from {_atlas.MapName(member.MapId)}";
                return;
            }
            if (now < _followTravelAt) return;
            _followTravelAt = now.AddSeconds(20);
            if (_atlas.Map(member.MapId)?.Nav == null || !await TeleportAsync(member.MapId, "following my party leader"))
                await CantFollowAsync(member.MapId);
            return;
        }
        _cantFollowMap = 0;
        if (member.Line > 0 && _view.Line > 0 && member.Line != _view.Line)
        {
            if (now < _followTravelAt) return;
            _followTravelAt = now.AddSeconds(15);
            _log($"switching to line {member.Line} to join my party leader");
            await SendAsync(() => _bot.SendGameAsync(MsgId.SwitchSceneLine,
                new SwitchSceneLineReq { Linenum = member.Line, ESource = SwitchSceneSourceType.SwitchSceneSourceManual }));
            return;
        }
        // Same map and line, out of sight: walk to where the party sync last placed them.
        _activity = "on my way to my party leader";
        if ((member.X != 0 || member.Z != 0) && (_path == null || now - _pathAt > TimeSpan.FromSeconds(3)))
            PathTo(member.X, member.Z, TimeSpan.Zero, slack: 3);
    }

    /// <summary>The leader is somewhere the bot cannot go (an instance or dungeon): wait, and say so once.</summary>
    private async Task CantFollowAsync(long map)
    {
        _cantFollowMap = map;
        long leader = Volatile.Read(ref _leader);
        _log($"cannot follow my party leader to {_atlas.MapName(map)}; waiting");
        string line = $"I can't follow you into {_atlas.MapName(map)}, so I'll wait here and catch up when you're back out.";
        try { await SendAsync(() => _bot.SendChatAsync(leader.ToString(), 1, line, channelIndex: 5)); }
        catch (Exception e) when (e is InvalidOperationException or IOException) { }
    }

    /// <summary>A healer heals the leader below 70% HP and itself below 60%, walking into range first.</summary>
    private async Task<bool> HealAsync(Unit leader, PartyMember? member, DateTime now)
    {
        if (_healSkill <= 0 || now < _nextHeal) return false;
        long hp = member?.Hp ?? leader.Hp, max = Math.Max(1, member?.MaxHp ?? leader.MaxHp);
        bool leaderNeeds = member is not { Dead: true } && hp * 100 / max < 70;
        bool selfNeeds = _view.Hp * 100 / Math.Max(1, _view.MaxHp) < 60;
        if (!leaderNeeds && !selfNeeds) return false;
        long target = leaderNeeds ? (long)leader.Uid : _roleId;
        if (leaderNeeds && Dist(leader.X, leader.Z) > 15)
        {
            if (_path == null || now - _pathAt > TimeSpan.FromSeconds(1)) PathTo(leader.X, leader.Z, TimeSpan.Zero, slack: 6);
            return true;
        }
        if (_path != null) await StopAsync();
        _nextHeal = now.AddSeconds(2.5);
        using var reply = _bot.Inbox.Expect(x => x.IsGame(CombatService.UseSkill));
        int skill = _healSkill;
        await SendAsync(() => _bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target }));
        int result;
        try { result = CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(4))).Body).Result; }
        catch (TimeoutException) { result = -1; }
        if (result == 0) { _activity = leaderNeeds ? "healing my party leader" : "healing myself"; return true; }
        if (result is ErrorCodes.SkillUnknown or ErrorCodes.SkillNotLearned) { _healSkill = 0; _nextHealLearn = now; }
        else _nextHeal = now.AddSeconds(8); // out of SP or blocked: try again later
        _log($"heal refused ({result})");
        return false;
    }

    /// <summary>
    /// Learns a heal when the bot's job tree has one (31151 on plan 0, the highest level its skill points allow).
    /// Jobs without one get "not in the skill tree" and try again after a job change, ten minutes later.
    /// </summary>
    private async Task LearnHealAsync()
    {
        var now = DateTime.UtcNow;
        if (_healSkill != 0 || now < _nextHealLearn) return;
        _nextHealLearn = now.AddMinutes(10);
        foreach (int group in HealGroups)
            foreach (int level in new[] { 10, 5, 3, 1 })
            {
                var req = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
                req.LevelUpList[group] = new SkillData { SkillId = group, SkillLevel = level };
                using var reply = _bot.Inbox.Expect(x => x.IsGame(SkillService.PlanUpdate));
                await SendAsync(() => _bot.SendGameAsync(SkillService.PlanUpdate, req));
                int result = SkillGrowthSkillPlanUpdateResp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(10))).Body).Result;
                if (result == 0)
                {
                    _healSkill = group * 100 + level;
                    _log($"learned heal {group} level {level}");
                    return;
                }
                if (result == ErrorCodes.SkillNotInTree) break;
            }
    }

    /// <summary>Low on HP: back off from the fight and wait for natural regeneration.</summary>
    private async Task<bool> RestAsync()
    {
        var now = DateTime.UtcNow;
        long hp = _view.Hp, max = Math.Max(1, _view.MaxHp);
        if (!_resting && hp * 100 / max < 50)
        {
            _resting = true;
            _restUntil = now.AddMinutes(2);
            _target = 0;
            _activity = "resting to get my HP back";
            var threat = _view.Units(1).Where(m => !m.Dead).OrderBy(m => Dist(m.X, m.Z)).FirstOrDefault();
            if (threat != null)
            {
                float dx = _view.X - threat.X, dz = _view.Z - threat.Z, d = MathF.Max(0.1f, MathF.Sqrt(dx * dx + dz * dz));
                PathTo(_view.X + dx / d * 12, _view.Z + dz / d * 12, TimeSpan.Zero);
            }
            else await StopAsync();
        }
        if (_resting && (hp * 100 / max >= 85 || now >= _restUntil)) _resting = false;
        return _resting;
    }

    // ---------------------------------------------------------------- combat

    /// <summary>A monster that hit the bot in the last few seconds and is still close: fight back rather than stand there.</summary>
    /// <summary>Fight back, unless the attacker is well above the bot's level: then run for the revive point.</summary>
    private async Task DefendAsync(Unit attacker)
    {
        if (attacker.Level <= _view.Level + 3) { await EngageAsync(attacker); return; }
        if (_path != null && DateTime.UtcNow - _pathAt < TimeSpan.FromSeconds(3)) return;
        _target = 0;
        _activity = "running from something way too strong";
        var map = _atlas.Map(_view.SceneEntry);
        if ((map?.Revive ?? map?.Born) is { } safe) PathTo(safe.X, safe.Z, TimeSpan.FromSeconds(20));
        else
        {
            float dx = _view.X - attacker.X, dz = _view.Z - attacker.Z, d = MathF.Max(0.1f, MathF.Sqrt(dx * dx + dz * dz));
            PathTo(_view.X + dx / d * 25, _view.Z + dz / d * 25, TimeSpan.FromSeconds(10));
        }
    }

    private Unit? Attacker()
    {
        if (DateTime.UtcNow - _view.LastHitUtc > TimeSpan.FromSeconds(4)) return null;
        var a = _view.Find(_view.LastAttacker);
        return a is { Dead: false } && Dist(a.X, a.Z) < 12 ? a : null;
    }

    private Unit? CurrentTarget()
    {
        if (_target == 0) return null;
        var t = _view.Find(_target);
        if (t == null || t.Dead || Dist(t.X, t.Z) > SightRange) { _target = 0; return null; }
        return t;
    }

    /// <summary>
    /// The nearest monster near (x, z) worth attacking. Manners: leave alone monsters someone else has hurt and those
    /// next to a real player (other than the party leader), so the bots never steal kills, and those left to the party.
    /// </summary>
    private Unit? PickTarget(float x, float z, float range, long maxLevel, long partyLeader)
    {
        var now = DateTime.UtcNow;
        foreach (var (uid, until) in _avoid.ToArray()) if (until < now) _avoid.Remove(uid);
        var players = _view.Units(3).Where(p => (long)p.Uid != partyLeader && !_hub.IsBot((long)p.Uid)).ToArray();
        var nav = _atlas.Map(_view.SceneEntry)?.Nav;
        return _view.Units(1)
            .Where(m => !m.Dead && m.Hp >= m.MaxHp && (m.Level == 0 || m.Level <= maxLevel) && !_avoid.ContainsKey(m.Uid)
                && !_crowd.ClaimedByOther(m.Uid, _roleId))
            .Where(m => Flat(m.X, m.Z, x, z) <= range)
            // Monsters have their own nav data: one standing where players cannot walk cannot be hit (SkillBlocked).
            .Where(m => nav == null || nav.WalkHeight(m.X, m.Z, m.Y) is { } h && MathF.Abs(h - m.Y) <= NavGrid.WalkHeightTolerance)
            .Where(m => !players.Any(p => Flat(p.X, p.Z, m.X, m.Z) < PlayerBuffer) && !LeaveToPlayers(m))
            .OrderBy(m => Dist(m.X, m.Z))
            .FirstOrDefault();
    }

    private async Task EngageAsync(Unit target)
    {
        var now = DateTime.UtcNow;
        if (LeaveToPlayers(target))
        {
            _target = 0;
            _activity = "leaving the last hit to my party";
            return;
        }
        if (_target != target.Uid) { _target = target.Uid; _refusals = 0; }
        _crowd.Claim(target.Uid, _roleId);
        string monster = _atlas.MonsterName(target.TemplateId);
        _activity = string.IsNullOrEmpty(monster) ? "fighting monsters" : $"fighting a {monster}";
        var nav = _atlas.Map(_view.SceneEntry)?.Nav;
        var (sx, sy, sz) = _view.Position;
        bool reach = Dist(target.X, target.Z) <= Attack.Range && (nav == null || nav.CanWalk(sx, sy, sz, target.X, target.Z));
        if (!reach)
        {
            bool stale = _path == null || now - _pathAt > TimeSpan.FromSeconds(1.5)
                || Flat(_path[^1].X, _path[^1].Z, target.X, target.Z) > 1.5f;
            // Close but with something in the way, again and again: give that one up.
            if (stale && Dist(target.X, target.Z) <= Attack.Range) _refusals++;
            if (stale && (_refusals > 6 || !PathTo(target.X, target.Z, TimeSpan.Zero, slack: Attack.Range)))
            {
                _avoid[target.Uid] = now.AddMinutes(1);
                _target = 0;
            }
            return;
        }
        if (_path != null) await StopAsync();
        if (now < _nextSwing) return;
        _nextSwing = now.AddMilliseconds(_random.Next(1200, 1700));
        if (_locked != target.Uid)
        {
            _locked = target.Uid;
            await SendAsync(() => _bot.SendGameAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = (long)target.Uid }));
        }
        using var reply = _bot.Inbox.Expect(x => x.IsGame(CombatService.UseSkill));
        int skill = Attack.Skill;
        await SendAsync(() => _bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = (long)target.Uid }));
        int result;
        try { result = CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(4))).Body).Result; }
        catch (TimeoutException) { result = -1; }
        if (result == 0) { _refusals = 0; return; }
        // Refused outright (unknown, not learned, wrong weapon): move on to the job's next candidate attack.
        if (result is ErrorCodes.SkillUnknown or ErrorCodes.SkillNotLearned or ErrorCodes.SkillRequirements && ++_attackRefusals >= 2)
        {
            var key = (_view.Career, _entry.WeaponType);
            int index = WorkingAttack.GetValueOrDefault(key);
            if (index + 1 < _progression.Attacks(key.Career, key.WeaponType).Count)
            {
                WorkingAttack[key] = index + 1;
                _log($"attack {skill} refused ({result}); trying {Attack.Skill}");
            }
            _attackRefusals = 0;
            _refusals = 0;
            return;
        }
        if (result == ErrorCodes.SkillBlocked || ++_refusals >= 4)
        {
            _log($"giving up on monster {target.Uid} (attack refused: {result})");
            _avoid[target.Uid] = now.AddMinutes(1);
            _target = _locked = 0;
        }
    }

    // ---------------------------------------------------------------- movement

    private float Dist(float x, float z) { var (sx, _, sz) = _view.Position; return Flat(sx, sz, x, z); }

    private static float Flat(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    /// <summary>Plans a walk to (x, z); false when there is no way there. Idles for <paramref name="idleAfter"/> on arrival.</summary>
    private bool PathTo(float x, float z, TimeSpan idleAfter, float slack = 1.5f)
    {
        _pathAt = DateTime.UtcNow;
        if (_atlas.Map(_view.SceneEntry)?.Nav is not { } nav) return false;
        var (sx, sy, sz) = _view.Position;
        var path = Navigator.FindPath(nav, new Point3(sx, sy, sz), x, z, slack);
        if (path == null || path.Count < 2) { _path = null; return false; }
        (_path, _pathIndex, _idleAfterPath) = (path, 1, idleAfter);
        return true;
    }

    private void Wander(float radius, TimeSpan idleAfter)
    {
        if (_atlas.Map(_view.SceneEntry)?.Nav is not { } nav) return;
        var (x, y, z) = _view.Position;
        if (Navigator.RandomPointNear(nav, _random, x, y, z, radius) is { } p) PathTo(p.X, p.Z, idleAfter);
        else _idleUntil = DateTime.UtcNow.AddSeconds(10);
    }

    /// <summary>One walk step (WALK every 0.2 s at the bot's speed, like a client holding a direction).</summary>
    private async Task StepAsync()
    {
        if (_path == null) return;
        var nav = _atlas.Map(_view.SceneEntry)?.Nav;
        var (x, y, z) = _view.Position;
        float fx = x, fz = z;
        float budget = _view.Speed / 100f * (float)Tick.TotalSeconds * 0.95f;
        while (budget > 0 && _pathIndex < _path.Count)
        {
            var wp = _path[_pathIndex];
            float d = Flat(x, z, wp.X, wp.Z);
            if (d <= budget) { (x, y, z) = (wp.X, wp.Y, wp.Z); budget -= d; _pathIndex++; }
            else { x += (wp.X - x) / d * budget; z += (wp.Z - z) / d * budget; budget = 0; }
        }
        if (nav?.WalkHeight(x, z, y) is { } h) y = h;
        // The server's own check (SceneService.AcceptWalkAsync); a step that would fail it is re-planned instead.
        if (nav != null && (!nav.IsWalkable(x, y, z) || !nav.CanWalk(fx, _view.Y, fz, x, z)))
        {
            var end = _path[^1];
            if (++_badSteps > 3 || !PathTo(end.X, end.Z, _idleAfterPath)) { _path = null; _idleUntil = DateTime.UtcNow.AddSeconds(1); }
            return;
        }
        _badSteps = 0;
        bool done = _pathIndex >= _path.Count;
        float dir = (MathF.Atan2(x - fx, z - fz) * 180f / MathF.PI + 360f) % 360f;
        await SendAsync(() => _bot.SendGameAsync(MsgId.Walk, new WalkReq
            { Pos = new Vec3 { X = x, Y = y, Z = z }, Dir = dir, Face = dir, BStop = done, SpeedRate = 1 }));
        _view.MovedTo(x, y, z);
        if (done)
        {
            _path = null;
            _idleUntil = DateTime.UtcNow + _idleAfterPath;
        }
    }

    private async Task StopAsync()
    {
        _path = null;
        var (x, y, z) = _view.Position;
        await SendAsync(() => _bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = x, Y = y, Z = z }, BStop = true, SpeedRate = 1 }));
    }

    // ---------------------------------------------------------------- growing up

    /// <summary>Which of a job's candidate attacks the server accepted, shared by all bots ((job, weapon type) -> index).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), int> WorkingAttack = new();

    /// <summary>The normal attack for the job and weapon and its reach: the first candidate the server accepts.</summary>
    private (int Skill, float Range) Attack
    {
        get
        {
            var options = _progression.Attacks(_view.Career, _entry.WeaponType);
            return options[Math.Min(WorkingAttack.GetValueOrDefault((_view.Career, _entry.WeaponType)), options.Count - 1)];
        }
    }

    /// <summary>
    /// First login (with the server's GM tool): jump to the planned level, advancing through the planned job line the
    /// way a player does (the GM tool sets the base and job level each advance needs), then stats and gear.
    /// </summary>
    private async Task SetupAsync()
    {
        if (_view.Level < _entry.Level && _gm.Available)
        {
            int level = _entry.Level;
            bool ok = await GmLevelAsync(level, 50);
            for (int next; ok && (next = _progression.NextJob(_view.Career, _entry.Job)) != 0 && Progression.Needs(_progression.JobOf(next)!.Rank).Base <= level;)
            {
                if (!await TransformAsync(next)) break;
                ok = await GmLevelAsync(level, 50);
            }
            if (ok)
            {
                await GmLevelAsync(level, _random.Next(20, 51));
                _log($"set up as a level {_view.Level} {_progression.JobName(_view.Career)}");
            }
        }
        await GrowAsync(force: true);
    }

    /// <summary>Job advances as the levels come, stat points as they are earned, better gear every 15 levels.</summary>
    private async Task GrowAsync(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < _nextGrow) return;
        _nextGrow = now.AddSeconds(45);
        if (_handicap > 0 && now - _lastDeath > TimeSpan.FromMinutes(15)) { _handicap -= 5; _lastDeath = now; }
        int level = (int)_view.Level;
        // A bot that has reached its planned job keeps going down a random branch when its level allows more.
        if (_entry.FirstJob == 0) { _entry.FirstJob = _progression.FirstJobOf(_entry.Job); _saveRoster(); }
        if (_view.Career == _entry.Job && _progression.JobOf(_entry.Job) is { } planned && planned.Rank < Progression.RankFor(level))
        {
            int from = _entry.Job == Progression.Novice && _entry.FirstJob != Progression.Novice ? _entry.FirstJob : _entry.Job;
            _entry.Job = _progression.PlanJob(from, Progression.RankFor(level), _entry.Sex, _random);
            _saveRoster();
        }
        int next = _progression.NextJob(_view.Career, _entry.Job);
        if (next != 0 && Progression.Needs(_progression.JobOf(next)!.Rank).Base <= level && await TransformAsync(next))
            _log($"advanced to {_progression.JobName(next)}");
        if (level != _statsLevel) await AllocateStatsAsync(level);
        if (_gm.Available && (_entry.GearLevel == 0 || level >= _entry.GearLevel + 15)) await GearUpAsync(level);
        try { await LearnHealAsync(); }
        catch (TimeoutException) { }
    }

    private async Task<bool> GmLevelAsync(int level, int jobLevel)
    {
        if (!await _gm.RunAsync(_entry.Account, $"!lvl {level} {jobLevel}")) { _gm.Refused(); return false; }
        var until = DateTime.UtcNow.AddSeconds(6);
        while (_view.Level != level && DateTime.UtcNow < until) await Task.Delay(200);
        if (_view.Level == level) return true;
        if (_view.LastNotice.Contains("not enabled", StringComparison.OrdinalIgnoreCase)) _gm.Refused();
        return false;
    }

    /// <summary>31176, the change-job panel's request: the server checks the rank's base and job level.</summary>
    private async Task<bool> TransformAsync(int job)
    {
        using var reply = _bot.Inbox.Expect(x => x.IsGame(CharacterService.PlayerTransformJob));
        await SendAsync(() => _bot.SendGameAsync(CharacterService.PlayerTransformJob, new PlayerTransformJobReq { JobId = job }));
        var result = PlayerTransformJobResp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(10))).Body).Result;
        if (result != 0) return false;
        var until = DateTime.UtcNow.AddSeconds(5);
        while (_view.Career != job && DateTime.UtcNow < until) await Task.Delay(100);
        _view.Career = job;
        _attackRefusals = 0;
        return true;
    }

    /// <summary>31500 with the stat values for this level (Progression.Stats only ever grows, as the server requires).</summary>
    private async Task AllocateStatsAsync(int level)
    {
        _statsLevel = level;
        var stats = _progression.Stats(_entry.FirstJob != 0 ? _entry.FirstJob : _progression.FirstJobOf(_entry.Job), level);
        var req = new AllocateAttrPointReq { PlanIndex = 0 };
        for (int i = 0; i < 6; i++) req.Points[i + 1] = stats[i];
        using var reply = _bot.Inbox.Expect(x => x.IsGame(CharacterService.AllocateAttrPoints));
        await SendAsync(() => _bot.SendGameAsync(CharacterService.AllocateAttrPoints, req));
        var result = AllocateAttrPointRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(10))).Body).Result;
        if (result != 0) _log($"stat points refused ({result})");
    }

    /// <summary>Gear for the job and level through the GM tool, worn straight from the bag (31400).</summary>
    private async Task GearUpAsync(int level)
    {
        var pieces = _progression.Gear(_view.Career, level, _entry.Sex, _random);
        int worn = 0;
        int accessoryGrid = 6;
        foreach (var (item, part) in pieces)
        {
            var before = _view.Guids(item).ToHashSet();
            if (!await _gm.RunAsync(_entry.Account, $"!item {item} 1")) { _gm.Refused(); return; }
            long guid = 0;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (guid == 0 && DateTime.UtcNow < until)
            {
                guid = _view.Guids(item).FirstOrDefault(g => !before.Contains(g));
                if (guid == 0) await Task.Delay(200);
            }
            if (guid == 0)
            {
                if (_view.LastNotice.Contains("not enabled", StringComparison.OrdinalIgnoreCase)) { _gm.Refused(); return; }
                continue;
            }
            int grid = part switch { 1 or 2 => 1, 3 => 2, 4 => 3, 5 => 4, 6 => 5, 7 => accessoryGrid++, _ => 0 };
            using var reply = _bot.Inbox.Expect(x => x.IsGame(CharacterService.EquipItemOpr));
            await SendAsync(() => _bot.SendGameAsync(CharacterService.EquipItemOpr,
                new EquipItemOprReq { Opr = (EquipItemOprType)0, Pos = (EquipGridType)grid, ItemGUID = guid }));
            if (EquipItemOprResp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(10))).Body).Result != 0) continue;
            worn++;
            if (part is 1 or 2) { _entry.WeaponType = _progression.WeaponType(item); _attackRefusals = 0; }
        }
        _entry.GearLevel = level;
        _saveRoster();
        if (worn > 0) _log($"geared up for level {level} ({worn} pieces)");
    }

    // ---------------------------------------------------------------- chat and parties

    private string Situation() =>
        $"you are a level {_view.Level} {_progression.JobName(_view.Career)} on {_atlas.MapName(_view.SceneEntry)}, {_activity}";

    private async Task AmbientAsync()
    {
        if (!_talker.AmbientDue(_view.SceneId)) return;
        bool audience = _view.Units(3).Any(p => !_hub.IsBot((long)p.Uid) && Dist(p.X, p.Z) < 25);
        if (!audience) return;
        string line = _talker.AmbientLine();
        await SendAsync(() => _bot.SendChatAsync($"4_{_view.SceneId}", 2, line, channelIndex: 4));
        _log($"said: {line}");
    }

    private async Task AnswerAsync(ChatHeard heard, CancellationToken ct)
    {
        try
        {
            if (heard.SenderId == _roleId || _hub.IsBot(heard.SenderId) || heard.Content.Length == 0) return;
            bool whisper = heard.ReceiveId == _roleId.ToString();
            if (!whisper && !heard.ReceiveId.StartsWith("4_", StringComparison.Ordinal)) return;
            var sender = _view.Find((ulong)heard.SenderId);
            if (!whisper && !_talker.WantsToAnswer(heard.SenderId, heard.Content, sender != null && Dist(sender.X, sender.Z) < 20)) return;
            string name = sender?.Name is { Length: > 0 } n ? n : "a player";
            await _talk.WaitAsync(ct);
            try
            {
                var started = DateTime.UtcNow;
                string? line = await _talker.ReplyAsync(heard.SenderId, name, heard.Content, whisper, Situation(), ct);
                if (line == null) return;
                // Look like someone typing: about 15 characters a second, at least a moment to read the line.
                var typing = TimeSpan.FromSeconds(Math.Min(8, 1.5 + line.Length / 15.0)) - (DateTime.UtcNow - started);
                if (typing > TimeSpan.Zero) await Task.Delay(typing, ct);
                if (whisper) await SendAsync(() => _bot.SendChatAsync(heard.SenderId.ToString(), 1, line, channelIndex: 5));
                else await SendAsync(() => _bot.SendChatAsync($"4_{_view.SceneId}", 2, line, channelIndex: 4));
                _log($"{(whisper ? "whispered to" : "answered")} {name}: {line}");
            }
            finally { _talk.Release(); }
        }
        catch (Exception e) when (e is not OperationCanceledException) { _log($"chat reply failed: {e.Message}"); }
    }

    private async Task AnswerInviteAsync(PartyInvite invite, CancellationToken ct)
    {
        try
        {
            bool player = !_hub.IsBot(invite.InviterId), free = Volatile.Read(ref _leader) == 0;
            await Task.Delay(TimeSpan.FromSeconds(_random.Next(2, 5)), ct);
            // Only players within ten base levels (owner decision, #58), so the bot helps rather than carries.
            long? level = _hub.Options.AcceptPartyInvites && free && player ? await PlayerLevelAsync(invite.InviterId, ct) : null;
            bool inRange = PartyRules.InLevelRange(_view.Level, level);
            bool accept = _hub.Options.AcceptPartyInvites && free && player && inRange;
            using var reply = _bot.Inbox.Expect(x => x.IsGame(TeamService.TeamInviteeResp));
            await SendAsync(() => _bot.SendGameAsync(TeamService.TeamInviteeResp,
                new InviteeResp { TeamId = invite.TeamId, IsAgree = accept, InviteGuid = invite.InviterId }));
            var result = InviteeRespResp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(10), ct)).Body).Result;
            if (!accept)
            {
                // Say why, so the inviter is not left wondering whether the bot is away.
                string? why = !player || !_hub.Options.AcceptPartyInvites ? null
                    : !free ? "Sorry, I'm already in someone else's party right now."
                    : PartyRules.LevelDecline(_view.Level, level);
                if (!free || !inRange) _log($"declined a party invite from {invite.InviterId} (level {level?.ToString() ?? "unknown"})");
                if (why != null) await SendAsync(() => _bot.SendChatAsync(invite.InviterId.ToString(), 1, why, channelIndex: 5));
                return;
            }
            if (result == ErrorCodes.AlreadyInParty)
            {
                // Still in a party from before a reconnect: leave it, and ask to be invited again.
                _log("still in an old party; leaving it");
                await SendAsync(() => _bot.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq { Guid = _roleId }));
                _view.ForgetParty();
                await SendAsync(() => _bot.SendChatAsync(invite.InviterId.ToString(), 1,
                    "Oops, I was stuck in an old party. Invite me again?", channelIndex: 5));
                return;
            }
            if (result != 0) { _log($"party join refused: {result}"); return; }
            _leaderSeen = DateTime.UtcNow;
            Volatile.Write(ref _leader, invite.InviterId);
            string name = _view.Find((ulong)invite.InviterId)?.Name ?? invite.InviterId.ToString();
            _log($"joined {name}'s party");
            string thanks = _talker.Thanks();
            await SendAsync(() => _bot.SendChatAsync(invite.InviterId.ToString(), 1, thanks, channelIndex: 5));
        }
        catch (Exception e) when (e is not OperationCanceledException) { _log($"party invite failed: {e.Message}"); }
    }

    /// <summary>
    /// A player's base level: the level the bot sees on them, else the client's head-name lookup
    /// (UserDataService.getAttributeBatchByVersion); null when neither answers.
    /// </summary>
    private async Task<long?> PlayerLevelAsync(long roleId, CancellationToken ct)
    {
        if (_view.Find((ulong)roleId) is { Level: > 0 } seen) return seen.Level;
        try
        {
            var reply = await _bot.GetRoleAttributesAsync(roleId).WaitAsync(TimeSpan.FromSeconds(8), ct);
            if (reply.GetValueOrDefault("Results") is object?[] results)
                foreach (var result in results.OfType<Dictionary<object, object?>>())
                    if (Convert.ToInt64(result.GetValueOrDefault("Uid")) == roleId && result.GetValueOrDefault("Data") is Dictionary<object, object?> data
                        && data.GetValueOrDefault("Level") is { } level)
                        return Convert.ToInt64(level);
        }
        catch (Exception e) when (e is not OperationCanceledException) { _log($"level lookup for {roleId} failed: {e.Message}"); }
        return null;
    }

    private async Task LeavePartyAsync(string why)
    {
        long leader = Interlocked.Exchange(ref _leader, 0);
        if (leader == 0 && _view.PartyGuids().Length == 0) return;
        _view.ForgetParty();
        _cantFollowMap = 0;
        _log($"leaving the party ({why})");
        await SendAsync(() => _bot.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq { Guid = _roleId }));
    }
}
