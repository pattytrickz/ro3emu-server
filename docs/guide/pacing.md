# Pacing: level caps and Bonus Time

House rules that set how fast a realm levels. None of them is RO3's own behaviour, and each one keeps the original
rule until a realm sets it. Owner decision for Sol RO3, 24 Oct 2026: see `docs/plans/roadmap.md`, "Decisions".

## Level-cap schedule (`LevelCaps`)

RO3 caps the base level by server day (`BaseLvLimitConfig`: 39 on day 1, up to 99 from day 36). This realm doesn't
apply those caps (`CharacterService.LevelRules`). `LevelCaps` replaces them with a schedule the realm publishes:

```
Ro3Emu__LevelCaps__0__From=2026-10-25T00:00:00Z
Ro3Emu__LevelCaps__0__Cap=70
Ro3Emu__LevelCaps__1__From=2026-11-27T00:00:00Z
Ro3Emu__LevelCaps__1__Cap=80
```

- The cap in force is the `Cap` of the latest phase whose `From` has passed. Before the first phase there is no
  house cap, and a cap is never above the data's maximum (99).
- **At the cap.** Base EXP stops one point short of the next level (a full bar), and anything past that is lost.
  When the cap rises, the role levels on its next EXP.
- **Above the cap.** A role already above the cap keeps its level, because EXP never lowers a level. A GM's `!lvl`
  is not capped.
- **Client display.** The client still shows RO3's day-based cap tip. Patching it to show the house cap is roadmap
  item 1.2, which needs a client hook and a launcher release.

## Bonus Time for hands-on play (`BonusExpAlwaysBelowLevel`)

`BonusExpAlways` pays the Bonus Time table (about ten times the reduced table) for every ordinary field and cave
kill. With `BonusExpAlwaysBelowLevel` set (Sol RO3: 50), the rule changes from that base level up:

- **Hands-on kills** (auto-battle off) still pay the Bonus Time table.
- **Auto-battle kills** follow RO3's own rule. They pay the table while battle time lasts, then the reduced table.
  Battle time can't be turned on in caves, so auto-battle kills there pay the reduced table.

0 (the default) keeps `BonusExpAlways` at every level.

## A larger battle-time budget (`BonusTimeMultiplier`)

RO3 grants 60 minutes of battle time a day and banks up to 180 (`PublicParamConfig` `OnHookDailyFightTime`,
`OnHookFightTimeLimit`). `BonusTimeMultiplier` multiplies both. Sol RO3 uses 2: 120 minutes a day, banking up to 360,
so a player who skips two days loses nothing.

## Playing together (`RealPartyExpPercent`)

RO3's party bonus raises a kill's EXP by 25 % per extra party member on the map, and this realm splits field-kill EXP
among them. `RealPartyExpPercent` adds a further bonus (0–100 %) when the killer's party has at least
`RealPartyMinPlayers` (default 3) **real** players on the killer's map. AI players (`PartyShareExcludedAccounts`) don't
count. Sol RO3 uses 25 with 3 players, so a party of three people out-earns three soloists.

## Hands-on play (`HandsOnExpPercent`, `HandsOnLootPercent`)

Sol RO3's selling point is that playing at the keyboard pays more than letting the client play. A kill counts as
hands-on when auto-battle has been off for at least `HandsOnSeconds` (default 10), so turning it off just before a
kill doesn't count. A hands-on kill gives `HandsOnExpPercent` more Base and Job EXP, and has a `HandsOnLootPercent`
chance of a second loot roll (items, Zeny, materials and cards, never EXP). Auto-battle keeps the normal rates, so
players short on time aren't penalised. AI players never get it. Sol RO3 uses 25 and 25.

## Sol RO3 settings

```
Ro3Emu__BonusExpAlways=true
Ro3Emu__BonusExpAlwaysBelowLevel=50
Ro3Emu__BonusTimeMultiplier=2
Ro3Emu__RealPartyExpPercent=25
Ro3Emu__HandsOnExpPercent=25
Ro3Emu__HandsOnLootPercent=25
Ro3Emu__LevelCaps__0__From=<announce date>
Ro3Emu__LevelCaps__0__Cap=70
```
