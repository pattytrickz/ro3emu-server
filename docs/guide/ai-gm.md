# The AI GM

A house rule, not RO3's own behaviour: an AI that runs events on the realm by itself, inside limits only the realm's
settings set. The server side is `src/Ro3Emu.Server/Game/AiGmService.cs`; the AI itself runs elsewhere (Sol RO3's
automation) and talks to the loopback endpoint below. Everything is off until `AiGm:Enabled` is true.

## What it may do

It uses the same code as the GMs, so its events look and behave like theirs ([events](events.md)):

| Action | What | Limits |
|---|---|---|
| `rates` | A rate window, now or planned | `MaxExpPercent`, `MaxDropRolls`, `MaxCardRolls`, `MaxRateHours`, `MaxRateWindowsPerWeek` |
| `logingift` | A login gift, now or planned (its id becomes `ai-<id>`) | `ItemPool`, `MaxLoginGiftHours`, `MaxGiftsPerWeek` |
| `gift` | One mail now to `all`, `online`, `level <min>-<max>` or `active <days>` | `ItemPool`, `MaxGiftsPerWeek` |
| `mvp` | Respawn a field MVP now (`!mvp spawn`) | `MvpPool`, `MaxMvpSpawnsPerDay` |
| `announce` | One marquee line, at most 200 characters, no links, commands or mentions | `MaxAnnouncementsPerDay` |
| `cancel` | End its own rate window (by `name`) or login gift (by `id`) | |
| `limits`, `list` | Its limits and what it used; every running and planned window and login gift, the owner's too | |

- **The owner's events stay the owner's.** Its rate windows never overlap another window, from the settings or a GM.
- **Gifts come only from `ItemPool`**, each item at most its `MaxCount` per role. An empty pool means no gifts.
- **Limits are rolling.** "2 a week" means no 7-day stretch holds more than 2; a planned window counts at its start,
  and a cancelled planned one stops counting.
- **Planned events** start at most `MaxDaysAhead` days ahead and are kept in the database like the GMs' (a restart
  keeps them).

## Settings

```
Ro3Emu__AiGm__Enabled=true
Ro3Emu__AiGm__MaxExpPercent=150
Ro3Emu__AiGm__MaxCardRolls=2
Ro3Emu__AiGm__MaxRateWindowsPerWeek=2
Ro3Emu__AiGm__ItemPool__0__Item=1250709
Ro3Emu__AiGm__ItemPool__0__MaxCount=3
Ro3Emu__AiGm__MaxGiftsPerWeek=2
Ro3Emu__AiGm__MvpPool__0=1
Ro3Emu__AiGm__MaxMvpSpawnsPerDay=3
Ro3Emu__AiGm__MaxAnnouncementsPerDay=12
```

The defaults are in `AiGmOptions` (`ServerOptions.cs`). The endpoint cannot change any of them: only these settings
do, so the AI can never raise its own limits.

## The endpoint

`POST /ro3emu/aigm` answers only on loopback, like the other host tools. The body is one JSON request; the answer is
`{"ok": true|false, "message": "...", "data": {...}}`, with the reason in `message` when a request is refused.

```json
{"action": "rates", "start": "2026-11-06T19:00:00Z", "hours": 2, "expPercent": 150, "name": "Friday Night Hunt"}
{"action": "gift", "item": 1250709, "count": 2, "filter": "active 7", "title": "Thanks for playing!"}
{"action": "mvp", "cfg": 1}
{"action": "announce", "text": "Drake is stirring in the Sunken Ship..."}
```

## Records

- Its actions are kept in `server_state` (`aigm_log`, two weeks) for the limits.
- With `GmAudit`, each action is a `gm-audit.log` line with the account `ai-gm` and the source `aigm`.
- Its rate windows and login gifts carry `By = ai-gm`; its mails go through the mail service like the GMs' gifts.
