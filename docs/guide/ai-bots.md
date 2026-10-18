# AI players (`src/Ro3Emu.AiBots`)

A separate program that keeps the world populated. Each bot logs in like a real client (`Ro3Emu.Bot.BotClient`), so
the server needs no changes and no restart to use them:

- **Hunters** level up on fields. They pick a field whose monsters suit their level (`MapConfig` scene type 1) and
  travel to a better one as they outgrow it. They walk on the client's nav grid, rest when low on HP and revive
  after dying.
- **Town bots** (`townShare`, default 30%) hang out in Prontera. They wander between NPCs, stop for a while and
  sometimes go AFK for a few minutes.
- **Parties.** A bot accepts a party invite from a real player, then follows that player and fights next to them. It
  leaves when the leader leaves, kicks it, or is out of sight for two minutes.
- **Manners.** Bots never attack a monster someone else has damaged, or one next to a real player, so they don't
  steal kills. They don't trade, sell, use the auction or post in world chat.
- **Chat.** Bots answer whispers, and map-chat lines that name them. Now and then they answer an open "hi" or
  "anyone?" from someone nearby, and drop the odd line when a real player is around. With an Anthropic API key, the
  replies come from a language model playing the bot's persona. Without one, they use canned lines. Bots never
  talk to each other.

Names carry the `[AI]` prefix, and the model is told to say it is a bot if asked.

## Running

```sh
dotnet publish src/Ro3Emu.AiBots -c Release -o ~/aibots/bin
~/aibots/bin/Ro3Emu.AiBots --config ~/aibots/aibots.json
```

The first run writes `aibots.json` with defaults if it is missing, and a `roster.json` next to it. The roster holds
the generated accounts (`aibot01`, ...), passwords, names and personas. It is chmod 600; keep it, or the bots lose
their characters. With approval-only registration (`Ro3Emu__Access__Mode=approved`), approve each account once:
`ro3-access approve aibot01`. Until then, that bot's login is refused and it retries every five minutes.

Logs go to the console and to `logs/aibots-yyyyMMdd.log` next to the config, kept for a week. A status line per bot
follows every `statusMinutes`. When the server restarts, the bots drop out and log back in by themselves within a
minute or two.

## `aibots.json`

| Key | Default | |
|---|---|---|
| `server` | `http://127.0.0.1:5380` | the server's HTTP base |
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
| `llm.model` | `claude-opus-5-5` | `claude-haiku-5-5` costs far less per reply |
| `llm.apiKeyFile` | `anthropic.key` | relative to the config; `ANTHROPIC_API_KEY` wins when set |
| `llm.maxCallsPerDay` | 400 | across all bots; past it the bots use canned lines |
| `llm.maxReplyChars` | 140 | |

## Load

These numbers were measured on the 2-core box with 16 bots on one field. The runner used about 14% of one core
and 350 MB, most of which is game data loaded once. The server spent about 25% of a core on them. Start with 20 and
watch `top` before going higher.
