namespace Ro3Emu.Server;

public sealed class ServerOptions
{
    /// <summary>Address the client is told to connect to (server list gate addresses). Must be an IP literal.</summary>
    public string PublicHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// Interface the HTTP and gate ports listen on. Empty: loopback when <see cref="PublicHost"/> is a loopback
    /// address (local play and the tests: no Windows Firewall prompt per test host), otherwise every interface.
    /// </summary>
    public string? BindAddress { get; set; }

    public System.Net.IPAddress ListenAddress() =>
        BindAddress is { Length: > 0 } bind ? System.Net.IPAddress.Parse(bind)
        : System.Net.IPAddress.TryParse(PublicHost, out var host) && System.Net.IPAddress.IsLoopback(host) ? System.Net.IPAddress.Loopback
        : System.Net.IPAddress.Any;

    /// <summary>The voice relay serves whenever a port is set (the stream is sealed, so off loopback too).</summary>
    public bool VoiceServed() => Voice.Port > 0;

    public int HttpPort { get; set; } = 5380;

    public int MicroGatePort { get; set; } = 20000;

    public int GameGatePort { get; set; } = 33100;

    public string DataDir { get; set; } = "data";

    /// <summary>Story dungeons whose encounter scripts are not in the client use the reconstructed waves in
    /// Reconstructed/dungeon-monsters.json (inferred, marked "odtworzone"); off refuses them as before.</summary>
    public bool ReconstructedDungeons { get; set; } = true;

    /// <summary>Game-message JSON log (data/logs/traffic-*.jsonl): "off", "on" or a comma-separated account list.
    /// Formatting and writes run off the game loop; keep it off for load tests and production.</summary>
    public string TrafficLog { get; set; } = "off";

    /// <summary>Parse all client config tables before the gates open (tools/run-server.ps1 turns it on). Off by
    /// default so the hundreds of short-lived test hosts load only the tables they touch.</summary>
    public bool PreloadGameData { get; set; }

    /// <summary>
    /// Folder with the data exported from the client: config/ (tools/ConfigExport), sceneeditor/ and nav/
    /// (tools/WorldData); default DataDir.
    /// </summary>
    public string? GameDataDir { get; set; }

    /// <summary>
    /// Copy the database to DataDir/backups at startup and every 6 hours (DatabaseBackup). Off only for a test realm
    /// running on a database copy where the disk cannot hold another one.
    /// </summary>
    public bool DatabaseBackups { get; set; } = true;

    /// <summary>Walk check against the client's nav grid: "off", "log" (report only) or "enforce" (snap back).</summary>
    public string WalkCheck { get; set; } = "log";

    public int ClusterId { get; set; } = 1;

    public string ClusterName { get; set; } = "RO3Emu";

    /// <summary>The server (area) inside the cluster: the login screen shows the cluster name above it.</summary>
    public string AreaName { get; set; } = "Prontera";

    /// <summary>
    /// Stall title limit in characters. The original's PublicParamConfig.StallsNameCharacterLimit (10) was sized for
    /// Chinese; English titles need more. ClientHooks.LuaPatches sets the client's input limit to the same value.
    /// </summary>
    public int StallNameMaxLength { get; set; } = 24;

    /// <summary>
    /// House rule: a base level-up refills HP and SP, as in Ragnarok Online 1. Off by default: nothing in the RO3
    /// client (config, Lua, texts) says the original server did it, and its level-up only raises the maxima.
    /// </summary>
    public bool LevelUpRefill { get; set; }

    /// <summary>
    /// House rule: while a player sits (the Meditate or Sit emote, playing) out of combat, each 5 s HP/SP regeneration
    /// cycle is multiplied by this. 1 by default = the original: nothing in the RO3 client data gives sitting its own
    /// recovery, only Meditate's text "Meditate on the spot to recover".
    /// </summary>
    public double SitRegenMultiplier { get; set; } = 1;

    /// <summary>
    /// House rule: every ordinary field kill pays its Bonus Time table (NPCDropConfig environment 1, about ten times
    /// the reduced environment 0), as if Bonus Time were always running. Off by default: the original pays it only
    /// while auto-battling with battle time left, and battle time unlocks at Base 37 (FunctionConfig 18), so a player
    /// between the end of the Base 30 story and the Base 40 job change otherwise levels on the reduced table.
    /// Dungeon, MVP and boss kills are unchanged; battle time keeps its own accounting.
    /// </summary>
    public bool BonusExpAlways { get; set; }

    /// <summary>
    /// Hours a settled monster-kill receipt keeps its full payload before <see cref="Accounts.ReceiptCompaction"/>
    /// empties it (0 keeps everything). Only a retry of the same operation, seconds after the kill, reads it.
    /// </summary>
    public int ReceiptRetentionHours { get; set; } = 24;

    /// <summary>
    /// Oldest Sol RO3 Launcher whose clients may play (Http.LauncherVersions), e.g. "1.2.2"; empty = any client. A
    /// launcher from 1.2.2 on prefixes every client URL with /launcher/{version}; an older or no launcher sends none.
    /// Such a client sees "Update your launcher" in the server list and is shown the reason and disconnected when its
    /// role enters the world. Loopback logins (the AI bots) are exempt.
    /// </summary>
    public string MinLauncherVersion { get; set; } = "";

    /// <summary>Where an outdated client is sent: the line its player reads in game.</summary>
    public string LauncherUpdateMessage { get; set; } =
        "Your Sol RO3 Launcher is out of date. Close the game and open the launcher to update it, or download it again from patch.solro3.com";

    /// <summary>
    /// Normal-attack pacing at startup (CombatService.NormalAttackMinGap): an impact sooner than this fraction of the
    /// swing time after the previous one deals nothing. 0 only measures; the loopback POST /ro3emu/pacing reads the
    /// measured gaps and changes it live.
    /// </summary>
    public double NormalAttackMinGap { get; set; }

    /// <summary>
    /// The live season playbook (SeasonService). 101 is the only one this client has season content for; a value
    /// set in game with "!season" overrides it.
    /// </summary>
    public int PlayBook { get; set; } = 101;

    /// <summary>The season's first day, yyyy-MM-dd; empty means the server's first start (kept in the database).</summary>
    public string? SeasonStart { get; set; }

    /// <summary>
    /// Owner policy (2026-10-11): every piece of content the client can show is playable. Off by default so the tests
    /// keep pinning the original's rules; appsettings.json turns it on for the real server.
    /// </summary>
    public OpenContentOptions OpenContent { get; set; } = new();

    /// <summary>Battle pass policy: the premium track is free for now.</summary>
    public PassOptions Pass { get; set; } = new();

    /// <summary>House rules for a free-to-play realm (Ro3Emu.Shared.F2PRules); off by default.</summary>
    public F2POptions F2P { get; set; } = new();

    /// <summary>Who may register and log in (Accounts.AccessList, data/access.json).</summary>
    public AccessOptions Access { get; set; } = new();

    /// <summary>Every GM command (chat, GM panel, loopback tool) is appended to data/logs/gm-audit.log.</summary>
    public bool GmAudit { get; set; }

    /// <summary>Every GM command is also announced on every online player's marquee.</summary>
    public bool GmAnnounce { get; set; }

    /// <summary>
    /// Accounts allowed to use GM commands in game ("!" lines in chat, the client's GM panel, 1031/1034). Empty:
    /// <see cref="GmOpenWhenEmpty"/> decides. docs/guide/gm-access.md.
    /// </summary>
    public string[] GmAccounts { get; set; } = [];

    /// <summary>
    /// What an empty <see cref="GmAccounts"/> means in game: true = every account has GM (research server, tests,
    /// local play), false = nobody has GM. Left unset, ServerHost decides from the host environment: false when it was
    /// set to Production explicitly (ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT or --environment), true otherwise. The
    /// loopback HTTP POST /ro3emu/gm (the operator's tools) keeps the old rule either way: empty = every account.
    /// </summary>
    public bool? GmOpenWhenEmpty { get; set; }

    /// <summary>
    /// Accounts the loopback POST /ro3emu/gm may run commands for even when <see cref="GmAccounts"/> lists others
    /// (operator tooling such as the AI players' setup, docs/guide/ai-bots.md). It grants nothing in game. An entry
    /// ending in * matches a prefix: "aibot*".
    /// </summary>
    public string[] GmToolAccounts { get; set; } = [];

    /// <summary>In-game bug reports (FeedbackService, docs/guide/feedback.md).</summary>
    public FeedbackOptions Feedback { get; set; } = new();

    /// <summary>
    /// Credential checks (SDK login, character deletion) one remote address may make per minute; past it the server
    /// answers 429 without hashing. Each check costs a 100 000-round PBKDF2 hash, and logins register accounts.
    /// Loopback is exempt (local play, the load tests, RO3Emu.exe); behind a reverse proxy every player shares the
    /// proxy's address, so raise this there.
    /// </summary>
    public int LoginAttemptsPerMinute { get; set; } = 20;

    /// <summary>Tests: the credential limit applies to loopback too.</summary>
    public bool LoginLimitIncludesLoopback { get; set; }

    /// <summary>Team voice relay proof of concept (Voice/, docs TBD). Off until a port is set.</summary>
    public VoiceOptions Voice { get; set; } = new();

    /// <summary>(inferred) Locally configured text blocklist; the original service's sensitive-word dictionary is unavailable.</summary>
    public string[] GuildBlockedWords { get; set; } = [];

    /// <summary>
    /// "instant": a guild is Normal at once unless the founder ticks the create widget's responder toggle (this server's
    /// shortcut). "responders": every guild is founded through GuildResponsePeopleLimit responders, as the original;
    /// the toggle then only waives the founder's approval of each response.
    /// </summary>
    public string GuildFounding { get; set; } = "instant";

    /// <summary>
    /// Guild League registration's membership threshold. Unset: GuildSettingConfig GuildLeaguePeopleNum (40, read as the
    /// "Guild membership reaches ${1}" requirement, inferred). A small realm or a test can lower it.
    /// </summary>
    public int? GuildLeagueMinMembers { get; set; }

    /// <summary>
    /// House rule (Sol RO3, not original behaviour): party members whose account matches (exact, or "prefix*") take no
    /// share of a field kill's EXP, so the AI players ("aibot*") no longer halve a real player's kills. Their own kills
    /// still share with the real players on the map. Empty: every member shares (the original rule).
    /// </summary>
    public string[] PartyShareExcludedAccounts { get; set; } = [];

    /// <summary>
    /// House rule (Sol RO3, not original behaviour): a monster kill's Base and Job EXP in percent, after the level gap
    /// correction and the party bonus (RewardService.ResolveKill), so a small realm levels faster. 100 keeps the data's.
    /// </summary>
    public int KillExpPercent { get; set; } = 100;

    /// <summary>
    /// House rule (Sol RO3, not original behaviour): a kill's drop tables roll this many times for cards (1-10), keeping
    /// only the cards of the extra rolls, so a card is about that many times as likely and nothing else changes. 1 keeps
    /// the data's odds.
    /// </summary>
    public int CardDropRolls { get; set; } = 1;

    /// <summary>
    /// Accounts (exact, or "prefix*") whose roles the public player count (/ro3emu/status, read by the website and the
    /// launcher) leaves out, so the AI players ("aibot*") do not show as people online. The admin's /ro3emu/online and
    /// /ro3emu/metrics still count every session. Empty: every role counts.
    /// </summary>
    public string[] StatusExcludedAccounts { get; set; } = [];

    /// <summary>Whether <paramref name="account"/> is on an account list of exact names or "prefix*" entries.</summary>
    public static bool AccountListed(IEnumerable<string> entries, string? account) =>
        account != null && entries.Any(e => e.EndsWith('*')
            ? e.Length > 1 && account.StartsWith(e[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(e, account, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// House rule (Sol RO3, not original behaviour): field MVPs (MvpService) spawn with this percentage of their
    /// configured HP, so a small realm's party can finish one. 100 keeps the config. Dungeon and story bosses are unchanged.
    /// </summary>
    public int FieldMvpHpPercent { get; set; } = 100;

    /// <summary>
    /// House rule (Sol RO3, not original behaviour): once the oldest party in a Phantom Realm queue (DungeonMatchService)
    /// has waited this many seconds, the match starts with the parties queued for that boss, down to a single player.
    /// The boss is not scaled down. 0 keeps the original rule: the match waits for a full party.
    /// </summary>
    public int DungeonMatchFillSeconds { get; set; }

    /// <summary>
    /// Seconds an in-game role stays in the world after its game socket drops, waiting for the client's silent
    /// reconnect (Phonest ConnectServerReq ReConnection). 60 is the reference
    /// AVATAR_LOSE_CONNECTION_TIMEOUT_SECONDS; it must exceed the client's 20 s + 10 s budgets. 0 turns grace off.
    /// </summary>
    public int ReconnectGraceSeconds { get; set; } = 60;

    /// <summary>
    /// TEST AID, not original behaviour: Survivor Battle (match entry 2) starts with this many entrants in teams of
    /// <see cref="ChickenTestTeamSize"/> instead of EatChickenMatchConfig's 100 in teams of 5 (the client gives no sign of
    /// server-side filler bots). 0 keeps the config. GM "!chicken shape" sets the same in memory.
    /// </summary>
    public int ChickenTestPlayers { get; set; }
    public int ChickenTestTeamSize { get; set; }

    /// <summary>
    /// House rule (Sol RO3, not original behaviour): lobby sizes for a small population. Survivor Battle (entry 2)
    /// starts with <see cref="SurvivorPlayers"/> entrants in teams of <see cref="SurvivorTeamSize"/> instead of
    /// EatChickenMatchConfig's 100 in teams of 5; Battle of Dragon Coffin Island (entry 6) with
    /// <see cref="DragonIslandPlayers"/> in squads of <see cref="DragonIslandTeamSize"/> instead of
    /// DragonIslandMatchConfig's 30 in squads of 5 (squads alternate between the two camps, so the lobby must hold an
    /// even number of squads). 0 keeps the config; the Survivor test aid above and GM "!chicken shape" win over it.
    /// </summary>
    public int SurvivorPlayers { get; set; }
    public int SurvivorTeamSize { get; set; }
    public int DragonIslandPlayers { get; set; }
    public int DragonIslandTeamSize { get; set; }

    /// <summary>
    /// (inferred) Seconds of a Realm of the Gods raid's preparation phase: the boss waits until the team leader presses the
    /// scene's "Start Battle" button (DungeonLua_30201xx DungeonBase.FunctionButtons, sent as 30125) or this time runs out.
    /// The client has the button, not the phase length.
    /// </summary>
    public int RaidPreparationSeconds { get; set; } = 120;

    /// <summary>
    /// (inferred) Seconds from a raid boss fight's start to its enrage, against which the Final Trial "more than N minutes
    /// remaining until enrage" honors (TenGameplayFinalTrialConfig, condition 7004) are judged. The enrage timer lives in
    /// the boss's server script, which the client does not carry.
    /// </summary>
    public int RaidEnrageSeconds { get; set; } = 600;

    /// <summary>Boss special skills (BossSkillSets, MonsterService.BossSkills): the scheduler's inferred numbers.</summary>
    public BossSkillOptions BossSkills { get; set; } = new();
}

/// <summary>
/// The boss skill scheduler. The skill rows, their cooldowns, cast and action times, ranges, damage and buffs come from
/// SkillConfig; the original server's AI scripts that decided when to cast are not in the client, so every number here
/// is (inferred) and can be tuned without a code change.
/// </summary>
public sealed class BossSkillOptions
{
    /// <summary>Off: bosses only swing their normal attack, as before the scheduler.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after a boss engages before its first special skill.</summary>
    public int FirstCastDelayMs { get; set; } = 3000;

    /// <summary>Pause after a special's action ends before the next one (the boss swings meanwhile); a row's longer
    /// _iPubCD wins.</summary>
    public int GlobalCooldownMs { get; set; } = 2000;

    /// <summary>Cooldown of a skill whose family carries only placeholder cooldowns (every row 0-2 s: Realm of the
    /// Gods, story Baphomet and Pharaoh), where the encounter script set the pace.</summary>
    public int PlaceholderCooldownMs { get; set; } = 15000;

    /// <summary>HP percent at which a boss with phase variants enters each next phase (phase 1, phase 2...).</summary>
    public int[] PhaseHpPercents { get; set; } = [50, 25];

    /// <summary>Area radius (m) when neither the row nor its show template gives one.</summary>
    public float DefaultRadius { get; set; } = 5f;

    /// <summary>Longest distance (m) to its target at which a boss starts a targeted skill; rows with "anywhere"
    /// ranges (_iDistanceMax 9999) are capped to it.</summary>
    public float MaxReach { get; set; } = 15f;

    /// <summary>Most hits one cast lands (a show's attack points; Ring of Light has three).</summary>
    public int MaxHitsPerCast { get; set; } = 6;

    /// <summary>Boss skill damage as a percent of the row's formula result.</summary>
    public int DamagePercent { get; set; } = 100;

    /// <summary>Minions of a summon skill whose description gives no count.</summary>
    public int SummonCount { get; set; } = 3;

    /// <summary>Life of a summoned minion without a self-destruct born buff.</summary>
    public int SummonLifeMs { get; set; } = 30000;
}

/// <summary>In-game bug reports: the menu's Feedback button and "/bug" in chat (FeedbackService).</summary>
public sealed class FeedbackOptions
{
    /// <summary>Longest report text kept (characters); longer text is cut and marked truncated.</summary>
    public int MaxLength { get; set; } = 1000;

    /// <summary>Server log lines about the player stored with a report (RecentLogBuffer); 0 keeps none and leaves the
    /// buffer out of the logging pipeline.</summary>
    public int ContextLines { get; set; } = 40;

    /// <summary>How far back those log lines may reach.</summary>
    public int ContextMinutes { get; set; } = 15;

    /// <summary>Minimum seconds between two reports of one account.</summary>
    public int CooldownSeconds { get; set; } = 5;
}

/// <summary>
/// Team voice relay (Talk/Listen/Mute in the party panel). The listener only opens when <see cref="Port"/> is set;
/// an off relay still answers the 77015 ticket request with port 0 so the client's menu can disable itself cleanly.
/// </summary>
public sealed class VoiceOptions
{
    /// <summary>TCP port the voice gate listens on; 0 keeps the relay off.</summary>
    public int Port { get; set; }

    /// <summary>Members a single voice room (one party/team) may hold.</summary>
    public int MaxRoomSize { get; set; } = 40;

    /// <summary>Audio frames a connection may send per second before the token bucket drops the extra ones.</summary>
    public int FramesPerSecond { get; set; } = 50;

    /// <summary>Extra frames the token bucket allows in a short burst on top of <see cref="FramesPerSecond"/>.</summary>
    public int Burst { get; set; } = 10;

    /// <summary>Control frames (Join, Leave, Mic, Speaker, Ping, anything else after Auth) a connection may send per
    /// second; the extra ones are dropped, and a sustained flood (3x this rate over 5 s) disconnects it. A real
    /// client sends a few per minute: a ping every 10 s plus the player's own clicks.</summary>
    public int ControlPerSecond { get; set; } = 10;

    /// <summary>Extra control frames allowed in a short burst on top of <see cref="ControlPerSecond"/>.</summary>
    public int ControlBurst { get; set; } = 20;

    /// <summary>Longest opus payload accepted in one Audio frame, in bytes.</summary>
    public int MaxPayload { get; set; } = 512;

    /// <summary>How long a 77015 ticket stays valid before a connection must present it.</summary>
    public int TicketSeconds { get; set; } = 60;

    /// <summary>Voice connections the listener accepts at once; past this new connections are refused.</summary>
    public int MaxConnections { get; set; } = 1024;

    /// <summary>Voice connections one IP address may hold at once (a LAN party behind one router needs a few).</summary>
    public int MaxConnectionsPerIp { get; set; } = 8;

    /// <summary>Connections from one IP that end without a valid Auth before that IP is refused for a minute.</summary>
    public int MaxFailedAuthPerIp { get; set; } = 10;

    /// <summary>Seconds a fresh connection has to send its Auth frame before it is closed. A test host lowers this.</summary>
    public int FirstFrameTimeoutSeconds { get; set; } = 5;

    /// <summary>Seconds without any frame before an authed connection is closed as idle. A test host lowers this.</summary>
    public int IdleTimeoutSeconds { get; set; } = 30;
}

/// <summary>Both off by default so the tests keep the original's pass; a realm turns them on (docs/systems/activities-battlepass-monthcard.md).</summary>
public sealed class PassOptions
{
    /// <summary>Every role gets the premium track and its privileges without the premium item 703.</summary>
    public bool FreePremium { get; set; }

    /// <summary>Pass EXP from play: challenges, daily activity, monsters, dungeons, MVPs and story (NewActivityService.PassPlay).</summary>
    public bool Play { get; set; }
}

/// <summary>House rule: who may play. Existing accounts keep logging in unless blocked.</summary>
public sealed class AccessOptions
{
    /// <summary>"open": a new name registers on its first login (default). "approved": only names an admin approved.</summary>
    public string Mode { get; set; } = "open";
}

/// <summary>
/// House rules for a free-to-play realm, not evidenced in RO3 (whose Crystals were bought with money). Prices, caps and
/// Crystal amounts are in Ro3Emu.Shared.F2PRules, which the client hook shares; the chances below are server-only.
/// The play rewards ride on the pass challenges, so they need <see cref="PassOptions.Play"/> as well.
/// </summary>
public sealed class F2POptions
{
    /// <summary>The shop overlay (prices, weekly caps), Crystals from challenges and MVP fights, and the drops below.</summary>
    public bool Enabled { get; set; }

    /// <summary>Each settled MVP fight: chance of one Blessed Weapon or Armor Ore (half each).</summary>
    public double MvpBlessedOreChance { get; set; } = 0.2;

    /// <summary>Each settled MVP fight: chance of 1-2 High-Density Enriched Oridecon or Elunium (half each).</summary>
    public double MvpRefineOreChance { get; set; } = 0.3;

    /// <summary>Each dungeon clear among the first five of a day: chance of one Blessed Weapon or Armor Ore.</summary>
    public double DungeonBlessedOreChance { get; set; } = 0.05;
}

/// <summary>
/// "Everything open" ("Policy: all content open"). The playbook stays 101: it is the only one
/// with its own content in this client, so seasons need no switch.
/// </summary>
public sealed class OpenContentOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Functions the original never opened (base level 999/9999) whose client UI and data work: 7201 Realm War map,
    /// 70122 Totem Shop, 41 Feedback, 1000000 Adv. Guild (AdventureGroupService), 96 Valhalla Trials (ValhallaService),
    /// 69 Season page (SeasonPageService; level 50 until the 20 Oct 2026 client closed it with condition 2000062, [1002,9999])
    /// (Feedback's panel comes from Ro3Emu.ClientHooks.FeedbackButton, its backend is FeedbackService). Shells without a working panel (Title 14, Academy 34...) stay closed so the menu shows no dead
    /// buttons; a function joins this list together with its backend. Novice Challenge (90, activity 112) is left out
    /// (owner, 2026-10-11): its 15 random event texts are "Event Text..."/"Option A" filler and the first node's
    /// dialogues 9901-9904 do not exist, so the original never finished it.
    /// </summary>
    public long[] Functions { get; set; } = [7201, 70122, 41, 1000000, 96, 69];

    /// <summary>
    /// NewActivityConfig rows whose own open condition is a level the cap never reaches (e.g. 112 Novice Challenge).
    /// 200 Pass I asks for base level 99 (condition 21000019). Pass II (201, the same condition and EXP currency) opens
    /// after Pass I, when the owner adds it here.
    /// </summary>
    public long[] Activities { get; set; } = [200];

    /// <summary>
    /// The season page's stage activities (MultiverseStage._kStageActivity of the playbook: for 101 that is 105 Fragment
    /// Gather, 106 Treasure, 107 Middle Season, 109/110 Racing, 111 World Exploration, 115 Emblem) are open whatever
    /// their own condition: every one is [1002,99], the final level cap (BaseLvLimitConfig day 198), so on a fresh realm
    /// the season page would list them all as locked for months. Their time windows still apply (107 keeps its
    /// settlement bounds, KeepTimeLimits), and World Exploration's chapters open with them (NewActivityService.WorldPhaseOpen).
    /// </summary>
    public bool SeasonStageActivities { get; set; } = true;

    /// <summary>Server-day conditions (3031 open days, 3030 playbook seconds) count as met from the first day.</summary>
    public bool IgnoreServerDay { get; set; } = true;

    /// <summary>
    /// TimeLimitConfig windows bounded by the server opening or by season stages open at the server opening and never
    /// close (every such id has a single state row: card pools, passes, Novice Challenge, Battle for Prontera, tower
    /// races...). Windows of activities given by absolute dates that already passed repeat with their own length.
    /// </summary>
    public bool PerpetualWindows { get; set; } = true;

    /// <summary>Windows that keep their original bounds: 1021 Midterm Settlement, 1023 Top 10 Guilds, 1036 Feedback (settlements, not content).</summary>
    public long[] KeepTimeLimits { get; set; } = [1021, 1023, 1036];

    /// <summary>NewActivityConfig rows that stay closed: 1011-1014, the original's "Test ... Pool" rows.</summary>
    public long[] ClosedActivities { get; set; } = [1011, 1012, 1013, 1014];

    /// <summary>Absolute windows that also become perpetual: 1060, the Territory War activity around the May block.</summary>
    public long[] PerpetualAbsolute { get; set; } = [1060];

    /// <summary>
    /// Territory Wars: the exported block of battles (2026-05-19..27, absolute dates) repeats every N days; 0 keeps the
    /// absolute dates. Each repeat is its own territory season (TerritoryWarsClock.Epoch), so rewards settle every cycle.
    /// </summary>
    public int TerritoryWarCycleDays { get; set; } = 14;

    public bool Perpetual(long timeLimitId) => Enabled && PerpetualWindows && !KeepTimeLimits.Contains(timeLimitId);
    public bool PerpetualAbsoluteWindow(long timeLimitId) => Enabled && PerpetualWindows && PerpetualAbsolute.Contains(timeLimitId);
    public long TerritoryWarPeriod => Enabled && TerritoryWarCycleDays > 0 ? TerritoryWarCycleDays * 86400L : 0;
    public bool Closes(long activityId) => Enabled && ClosedActivities.Contains(activityId);
    public bool Forces(long functionId) => Enabled && Functions.Contains(functionId);
    public bool ForcesActivity(long activityId) => Enabled && Activities.Contains(activityId);
    public bool OpensStageActivities => Enabled && SeasonStageActivities;
    public bool ServerDayFree => Enabled && IgnoreServerDay;
}
