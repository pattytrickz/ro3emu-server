# Boost items

A house rule, not RO3's own behaviour: items that start a personal EXP or item-drop boost when used. RO3 has no such
items, so Sol RO3 lends the effect to existing usable items (ones nothing else in the game gives) and hands them out
only through events and GM gifts (`!gift`, login gifts, the AI GM's item pool). Code: `src/Ro3Emu.Server/Game/BoostService.cs`.
Nothing changes until `BoostItems` lists an item.

## Settings

```
Ro3Emu__BoostItems__0__Item=1294701
Ro3Emu__BoostItems__0__Kind=exp
Ro3Emu__BoostItems__0__Percent=50
Ro3Emu__BoostItems__0__Minutes=30
Ro3Emu__BoostItems__0__Buff=0
```

| Field | Meaning |
|---|---|
| `Item` | The ItemConfig id. It must be usable (`_iIsUse` 1); its own use effect no longer runs. |
| `Kind` | `exp`: Base and Job EXP from kills +`Percent` %. `drop`: one more loot roll at `Percent` % chance (cards included, never EXP). |
| `Percent` | 1-100. |
| `Minutes` | 1-240. |
| `Buff` | A BuffConfig row shown while the boost runs, for its icon and timer; 0 for none. Only a row that changes no attributes is shown. |

## Sol RO3's items

| Item | Id | Boost |
|---|---|---|
| Glyph | 1294701 | EXP +25%, 30 minutes |
| Spirit Glyph | 1294702 | EXP +50%, 30 minutes |
| Sacred Glyph | 1294703 | EXP +50%, 1 hour |
| Blessed Sacred Glyph | 1294704 | EXP +100%, 15 minutes |
| Armor Plate | 1296701 | Item drop +25%, 30 minutes |
| Reinforced Armor Plate | 1296702 | Item drop +50%, 30 minutes |
| Sacred Armor Plate | 1296703 | Item drop +50%, 1 hour |
| High-Tenacity Sacred Armor Plate | 1296704 | Item drop +100%, 15 minutes |

The Glyphs are given by nothing else in the game. The Armor Plates were in the battle pass's level 7 premium reward
(AwardConfig 100120001): awards leave boost items out, so from now on only events and GMs give them.

## Rules

- **One of each kind at a time.** Using a second EXP boost while one runs is refused (the item is kept), and the
  marquee says how long the first still runs.
- **Events don't stack with boosts.** A kill takes the higher of the running event rate and 100 + the boost: a +50%
  boost during an x2 EXP event gives x2. A drop boost adds its roll unless an event already rolls more.
- **Each player's own boost.** In a party, every member's EXP share uses their own boost, never the killer's; the
  loot (and the drop boost) is the killer's.
- **Never from game rewards.** Awards (battle pass, quests, chests, sign-in) leave boost items out (AwardService).
- **A relog keeps it.** The end times are kept in `server_state` (`boost/<role id>`); the buff comes back at login.
  The item is spent first and the boost saved after: a crash in between loses that one item (the log says so).

## Names in the client

The items keep the client's own name and description unless the client patch's text overrides change them
(`src/Ro3Emu.Loc`), for example "Field Manual: Base and Job EXP +50% for 30 minutes".
