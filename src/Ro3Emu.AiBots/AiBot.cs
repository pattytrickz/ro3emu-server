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
    private const int BasicAttack = 1000001;
    private const float AttackRange = 2f, PlayerBuffer = 6f, SightRange = 35f;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    private readonly RosterEntry _entry;
    private readonly AiBotsOptions _options;
    private readonly Atlas _atlas;
    private readonly ChatHub _hub;
    private readonly Crowd _crowd;
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
    private int _refusals, _badSteps;
    private bool _resting;
    private readonly Dictionary<ulong, DateTime> _avoid = new();
    private long _leader;
    private DateTime _leaderSeen;
    private string _activity = "just arrived";

    public AiBot(RosterEntry entry, AiBotsOptions options, Atlas atlas, ChatHub hub, Crowd crowd, string pem, Action<string> log, Action saveRoster)
    {
        (_entry, _options, _atlas, _hub, _crowd, _pem, _saveRoster) = (entry, options, atlas, hub, crowd, pem, saveRoster);
        _log = line => log($"{entry.Name}: {line}");
        _talker = NewTalker();
    }

    public string Name => _entry.Name;
    public bool Online { get; private set; }

    public string Status => Online
        ? $"{_entry.Name} lv{_view.Level} {_atlas.MapName(_view.SceneEntry)} ({_view.X:F0},{_view.Z:F0}) hp {_view.Hp}/{_view.MaxHp}: {_activity}"
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
            if (_view.Dead) { await DeadAsync(); continue; }
            _deadSince = null;
            if (Volatile.Read(ref _leader) != 0) await PartyAsync();
            else if (_entry.Town) await TownAsync();
            else await HuntAsync();
            await StepAsync();
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
                long leader = Volatile.Read(ref _leader);
                if (leader == 0) break;
                if (change.Left == _roleId) { Volatile.Write(ref _leader, 0); _log("was removed from the party"); }
                else if (change.NewLeader == _roleId || change.Left == leader) await LeavePartyAsync("my party leader left");
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

    private async Task TeleportAsync(long map, string why)
    {
        _log($"travelling to {_atlas.MapName(map)} ({why})");
        _path = null;
        _target = 0;
        using var reply = _bot.Inbox.Expect(x => x.IsGame(SceneService.SceneTeleport));
        await SendAsync(() => _bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = map }));
        var response = SceneTeleportRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(15))).Body);
        if (response.Result != 0) _log($"travel refused: {response.Result}");
        // The scene switch (70010) that follows is loaded by HandleAsync.
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
            _log("died");
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
        long level = _view.Level;
        if (now >= _nextMapCheck)
        {
            _nextMapCheck = now.AddMinutes(3 + _random.NextDouble() * 4);
            var here = _atlas.Hunt(_view.SceneEntry);
            bool wrong = here == null || here.MinLevel > level + 3 || level > here.MedianLevel + 8;
            // Spread out: a field holding more than its share of the hunters sends some elsewhere.
            int crowd = _crowd.Hunters(_view.SceneEntry);
            bool crowded = crowd > Math.Max(4, _crowd.AllHunters / 3 + 1) && _random.NextDouble() < 0.5;
            if ((wrong || crowded) && _atlas.Choose(level, _random, _crowd.Hunters) is { } pick && pick.Id != _view.SceneEntry
                && (wrong || _crowd.Hunters(pick.Id) + 1 < crowd))
            {
                await TeleportAsync(pick.Id, here == null ? "time to hunt" : wrong ? $"level {level}" : "too crowded here");
                return;
            }
        }
        if (Attacker() is { } attacker) { await EngageAsync(attacker); return; }
        if (await RestAsync()) return;
        var target = CurrentTarget() ?? PickTarget(_view.X, _view.Z, SightRange, level + 3, partyLeader: 0);
        if (target != null) { await EngageAsync(target); return; }
        _target = 0;
        if (_path != null || now < _idleUntil) return;
        // Nothing in sight: head for a spawn point that suits the bot's level.
        if (_atlas.Hunt(_view.SceneEntry) is { } map)
        {
            var points = map.Spawns.Where(s => s.Level <= level + 3).Select(s => s.Point)
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

    private async Task PartyAsync()
    {
        long leaderId = Volatile.Read(ref _leader);
        var leader = _view.Find((ulong)leaderId);
        var now = DateTime.UtcNow;
        if (leader == null)
        {
            if (now - _leaderSeen > TimeSpan.FromMinutes(2)) await LeavePartyAsync("lost my party leader");
            return;
        }
        _leaderSeen = now;
        _activity = $"in a party with {leader.Name}, following them";
        if (Attacker() is { } attacker) { await EngageAsync(attacker); return; }
        if (await RestAsync()) return;
        float toLeader = Dist(leader.X, leader.Z);
        var target = CurrentTarget();
        if (target == null && toLeader < 12) target = PickTarget(leader.X, leader.Z, 10, long.MaxValue, leaderId);
        if (target != null && toLeader < 15) { await EngageAsync(target); return; }
        _target = 0;
        if (toLeader > 4 && (_path == null || now - _pathAt > TimeSpan.FromSeconds(1.5)))
        {
            float a = (float)(_random.NextDouble() * MathF.Tau);
            PathTo(leader.X + 2 * MathF.Cos(a), leader.Z + 2 * MathF.Sin(a), TimeSpan.Zero);
        }
    }

    /// <summary>Low on HP: back off from the fight and wait for natural regeneration.</summary>
    private async Task<bool> RestAsync()
    {
        var now = DateTime.UtcNow;
        long hp = _view.Hp, max = Math.Max(1, _view.MaxHp);
        if (!_resting && hp * 100 / max < 35)
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
    /// next to a real player (other than the party leader), so the bots never steal kills.
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
            .Where(m => !players.Any(p => Flat(p.X, p.Z, m.X, m.Z) < PlayerBuffer))
            .OrderBy(m => Dist(m.X, m.Z))
            .FirstOrDefault();
    }

    private async Task EngageAsync(Unit target)
    {
        var now = DateTime.UtcNow;
        if (_target != target.Uid) { _target = target.Uid; _refusals = 0; }
        _crowd.Claim(target.Uid, _roleId);
        string monster = _atlas.MonsterName(target.TemplateId);
        _activity = string.IsNullOrEmpty(monster) ? "fighting monsters" : $"fighting a {monster}";
        var nav = _atlas.Map(_view.SceneEntry)?.Nav;
        var (sx, sy, sz) = _view.Position;
        bool reach = Dist(target.X, target.Z) <= AttackRange && (nav == null || nav.CanWalk(sx, sy, sz, target.X, target.Z));
        if (!reach)
        {
            bool stale = _path == null || now - _pathAt > TimeSpan.FromSeconds(1.5)
                || Flat(_path[^1].X, _path[^1].Z, target.X, target.Z) > 1.5f;
            // Close but with something in the way, again and again: give that one up.
            if (stale && Dist(target.X, target.Z) <= AttackRange) _refusals++;
            if (stale && (_refusals > 6 || !PathTo(target.X, target.Z, TimeSpan.Zero, slack: AttackRange)))
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
        await SendAsync(() => _bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = BasicAttack, TargetId = (long)target.Uid }));
        int result;
        try { result = CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(4))).Body).Result; }
        catch (TimeoutException) { result = -1; }
        if (result == 0) { _refusals = 0; return; }
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

    // ---------------------------------------------------------------- chat and parties

    private string Situation() =>
        $"you are a level {_view.Level} novice on {_atlas.MapName(_view.SceneEntry)}, {_activity}";

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
            bool accept = _hub.Options.AcceptPartyInvites && Volatile.Read(ref _leader) == 0 && !_hub.IsBot(invite.InviterId);
            await Task.Delay(TimeSpan.FromSeconds(_random.Next(2, 5)), ct);
            using var reply = _bot.Inbox.Expect(x => x.IsGame(TeamService.TeamInviteeResp));
            await SendAsync(() => _bot.SendGameAsync(TeamService.TeamInviteeResp,
                new InviteeResp { TeamId = invite.TeamId, IsAgree = accept, InviteGuid = invite.InviterId }));
            var result = InviteeRespResp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(10), ct)).Body).Result;
            if (!accept) return;
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

    private async Task LeavePartyAsync(string why)
    {
        long leader = Interlocked.Exchange(ref _leader, 0);
        if (leader == 0) return;
        _log($"leaving the party ({why})");
        await SendAsync(() => _bot.SendGameAsync(TeamService.TeamLeave, new LeaveTeamReq { Guid = _roleId }));
    }
}
