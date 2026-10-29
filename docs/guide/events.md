# Events: rate windows and gifts

House rules for running events on a realm. None of this is RO3's own behaviour, and all of it is off until a
realm sets it. Code: `src/Ro3Emu.Server/Game/EventService.cs`.

## Rate windows

A rate window raises kill rewards between two UTC times, on top of the realm's house rates (`KillExpPercent`,
`CardDropRolls`):

| Field | Meaning |
|---|---|
| `ExpPercent` | Kill EXP in percent of the house rate. `200` doubles it (100–1000). |
| `DropRolls` | The kill's loot tables roll this many times (1–5). Extra rolls keep their items, never their EXP. |
| `CardRolls` | Card chance times this (1–5), on top of `CardDropRolls` (at most 10 rolls in all). |
| `Name` | Shown on the marquee when the window opens, when it ends, and to players who log in during it. |

When windows overlap, each rate takes the highest value; they don't stack.

Planned windows go in the settings, for example a Bonus Sunday:

```
Ro3Emu__EventRates__0__Name=Bonus Sunday
Ro3Emu__EventRates__0__Start=2026-11-01T14:00:00Z
Ro3Emu__EventRates__0__End=2026-11-01T22:00:00Z
Ro3Emu__EventRates__0__ExpPercent=200
```

A GM starts one at once with `!rates <hours> <exp%> [drops] [cards] [name]`, for example
`!rates 48 200 1 2 Bonus Weekend`. `!rates` lists the running and planned windows, and `!rates stop` ends the GM
windows (the planned ones stay). GM windows are kept in the database (`server_state` key `event_rates`), so a
restart keeps them.

## Login gifts

A login gift is a mail every role gets once, at its first login inside the window (an advent gift, a holiday
present). In the settings:

```
Ro3Emu__LoginGifts__0__Id=pumpkin-2026
Ro3Emu__LoginGifts__0__Start=2026-10-30T00:00:00Z
Ro3Emu__LoginGifts__0__End=2026-11-09T00:00:00Z
Ro3Emu__LoginGifts__0__Item=1250709
Ro3Emu__LoginGifts__0__Count=3
Ro3Emu__LoginGifts__0__Title=Happy Halloween from Sol RO3
Ro3Emu__LoginGifts__0__MinLevel=10
```

A GM makes one with `!logingift <id> <item> <count> <hours> [title]`. The players online at that moment get theirs
right away. `!logingift` lists the gifts, and `!logingift stop <id>` ends a GM gift.

- **Ids are never reused.** A role that claimed an id never gets that id again, so give every gift a new one.
- **The claim is saved before the mail.** A failed mail loses that role's gift (the log says so) but never sends it
  twice.
- **Roles below `MinLevel`** get the gift when they reach it inside the window.

## The gift tool

`!gift <item> <count> <filter> [title]` mails every matching role at once, online or not:

| Filter | Who |
|---|---|
| `all` | Every role |
| `online` | Roles in game now |
| `level <min>-<max>` | Base level in the range |
| `active <days>` | Online now, or logged in within that many days |

For example, `!gift 1000001 5 active 14 Thanks for playing`. Use it for compensation and prizes.

## The AI GM

The AI GM runs rate windows, gifts, MVP respawns and announcements through this same code, inside limits only the
settings set: [the AI GM](ai-gm.md).

## AI players

The accounts in `StatusExcludedAccounts` (the AI players, `aibot*`) get no login gifts and no tool gifts.
