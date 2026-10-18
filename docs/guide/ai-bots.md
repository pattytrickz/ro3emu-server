# AI players (`src/Ro3Emu.AiBots`)

A separate program that keeps the world populated. Each bot logs in like a real client (`Ro3Emu.Bot.BotClient`):

- **Every stage of the game.** Each bot gets a planned starting level, spread evenly from 1 to 95, and a job line.
  First jobs are dealt round-robin, then a random branch at each rank: 2nd job, transcendent, 3rd job. The bot gets
  there the way a player does. It sends the change-job request (31176) once it has the base and job level the rank
  needs, and keeps advancing as it levels. Stat points follow its first job. With the GM tool (below) a new bot
  jumps straight to its planned level and gets gear. Without it, every bot starts at level 1 and works its way up.
- **Hunters** pick a field a few levels below themselves (`MapConfig` scene type 1, fewest bots first) and spread
  out when a field gets crowded. They walk on the client's nav grid, rest below half HP, fight back against
  monsters near their level and run from stronger ones. A bot that dies twice in five minutes moves 10 levels
  easier; this fades after 15 minutes without dying.
- **Town bots** (`townShare`, default 30%) hang out in Prontera. They wander between NPCs, stop for a while and
  sometimes go AFK for a few minutes.
- **Parties.** A bot accepts a party invite from a real player, then follows that player and fights next to them. It
  leaves when the leader leaves, kicks it, or is out of sight for two minutes.
- **Manners.** Bots never attack a monster someone else has damaged, or one next to a real player, so they don't
  steal kills. They don't trade, sell, use the auction or post in world chat.
- **Chat.** Bots answer whispers, and map-chat lines that name them. Now and then they answer an open "hi" or
  "anyone?" from someone nearby, and drop the odd line when a real player is around. With an API key, the replies
  come from a language model playing the bot's persona. Without one, they use canned lines. Bots never talk to each
  other.

Names carry the `[AI]` prefix, and the model is told to say it is a bot if asked.

## The GM tool (`Ro3Emu:GmToolAccounts`)

Setting a level (`!lvl`) and handing out gear (`!item`) goes through the server's loopback tool. With `GmAccounts`
set, that tool only acts on listed accounts, so the server needs `Ro3Emu__GmToolAccounts__0=aibot*`. This lets the
tool act on the bot accounts without giving them GM in game (`docs/guide/gm-access.md`). Without it, the bots log
once that the tool refused, and they level from 1.

Gear is picked from `ItemConfig` for the job and level: per slot, the highest-level regular piece of common to rare
quality that needs no appraisal. A shield comes only with a one-handed weapon, plus two accessories. Better gear
follows every 15 levels. The bot's normal attack comes from `JobAttackConfig` for its job and weapon type. When the
server refuses that attack (some jobs' normal attacks are not counted as learned), the bot tries the job's
`_kSkillNormalID` and then the Novice attack, and remembers which one worked.

## Running

```sh
dotnet publish src/Ro3Emu.AiBots -c Release -o ~/aibots/bin
~/aibots/bin/Ro3Emu.AiBots --config ~/aibots/aibots.json
```

The first run writes `aibots.json` with defaults if it is missing, and a `roster.json` next to it. The roster holds
the generated accounts (`aibot01`, ...), passwords, random names, personas and level plans. It is chmod 600; keep
it, or the bots lose their characters. With approval-only registration (`Ro3Emu__Access__Mode=approved`), approve
each account once: `ro3-access approve aibot01`. Until then, that bot's login is refused and it retries every five
minutes.

Logs go to the console and to `logs/aibots-yyyyMMdd.log` next to the config, kept for a week. A status line per bot
follows every `statusMinutes`. When the server restarts, the bots drop out and log back in by themselves within a
minute or two.

## `aibots.json`

| Key | Default | |
|---|---|---|
| `server` | `http://127.0.0.1:5380` | the server's HTTP base; the GM tool needs it on loopback |
| `gameDataDir` | `/opt/ro3emu/RO3Emu-server/data` | game tables and nav grids, read only |
| `count` | 20 | bots online; the roster grows to fit |
| `accountPrefix` / `namePrefix` | `aibot` / `[AI]` | names are limited to 16 characters, prefix included |
| `townShare` | 0.3 | share of town bots |
| `huntMaps` | all fields | limit hunters to these `MapConfig` ids |
| `townMap` | 10100 | Prontera |
| `chat.ambientMinutes` | 25 | average gap between one bot's unprompted lines |
| `chat.ambientMapGapSeconds` | 180 | at most one unprompted line per map in this time |
| `chat.overhearChance` | 0.25 | chance a nearby bot answers an open greeting or question |
| `chat.repliesPerPlayerPerHour` | 30 | replies one player gets from all bots together |
| `chat.acceptPartyInvites` | true | |
| `llm.enabled` | true | false: canned lines only |
| `llm.provider` | `openrouter` | or `anthropic` (the Claude API) |
| `llm.model` | provider default | `anthropic/claude-opus-5.5` (OpenRouter) / `claude-opus-5-5`; Haiku 5.5 costs far less |
| `llm.apiKeyFile` | `openrouter.key` / `anthropic.key` | relative to the config; `OPENROUTER_API_KEY` / `ANTHROPIC_API_KEY` win |
| `llm.maxCallsPerDay` | 400 | across all bots; past it the bots use canned lines |
| `llm.maxReplyChars` | 140 | |

## Load

These numbers were measured on the 2-core box. With 16 bots on one field, the runner used about 14% of one core and
350 MB, and the server spent about 25% of a core on them. The game data the runner loads (maps, items) is shared and
costs most of that memory: about 550 MB with gear tables, for any number of bots. Start with 20 and watch `top`
before going higher.
