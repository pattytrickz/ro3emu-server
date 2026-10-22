# Sol RO3: roadmap (Oct 2026 – Q2 2027)

*Written 22 Oct 2026. This puts the [strategy](balance-retention-strategy.md) and the [events plan](events-plan.md) on
a calendar. Effort: **S** about a day, **M** 2–5 days, **L** 1–2 weeks of focused work.*

## How the work is split

| Lane | Branches | Owns |
|---|---|---|
| **Bug automation** (`solro3-auto`) | `auto/issue-*` → PR into `v1.0.0-fixes` | Everything in `#bug-reports` and `#general` bug talk; patch-notes and news drafts |
| **Features and balance** (owner + Claude sessions) | `feat/*`, `balance/*` from `v1.0.0-fixes` | This roadmap: house rules, tools, events, balance patches; `#suggestions` |

Rules so the lanes don't collide:
- A file the automation has an open PR on is off-limits to the feature lane until it merges. Check the open
  `auto/issue-*` PRs before starting.
- Every balance change is a **house-rule option that defaults to the original behaviour**, the repo's existing
  pattern (`SitRegenMultiplier = 1`, `BonusExpAlways = false`). Tests keep pinning RO3's rules, and the live realm
  turns the change on in its own settings.
- Every option gets a test, a line in its `docs/guide` page, and a patch-notes and news draft in its PR, the same
  format the automation's PRs use.

---

## Phase 0: measure and stabilise (now – Sun 1 Nov 2026)

| # | Item | Effort | Done when |
|---|---|---|---|
| 0.1 | **Baseline numbers.** SQL against the realm DB: the level histogram (real players vs `aibot*`), D1/D7 return for every account since launch, job spread, peak concurrent real players, total Zeny and Crystals | S | Numbers posted in `#staff-chat`. They set the first cap phase (it must be ≥ the top real player) |
| 0.2 | **Owner decisions** from [strategy §6](balance-retention-strategy.md#6-decisions-the-owner-needs-to-make): time to 99, cap schedule, Bonus Time budget, match sizes, seasons, bot policy | S | Decisions written into this file |
| 0.3 | Review and merge the open player-report PRs: ~~[#48](https://github.com/pattytrickz/ro3emu-server/pull/48), [#50](https://github.com/pattytrickz/ro3emu-server/pull/50), [#51](https://github.com/pattytrickz/ro3emu-server/pull/51), [#52](https://github.com/pattytrickz/ro3emu-server/pull/52), [#53](https://github.com/pattytrickz/ro3emu-server/pull/53), [#54](https://github.com/pattytrickz/ro3emu-server/pull/54)~~ all merged 22 Oct | S | Done |
| 0.4 | Check that the **100 z instant potions** don't trivialise MVPs and PvP (their cooldown vs. MVP damage) | S | Kept, repriced or limited |
| 0.5 | **🎃 Night of the Pumpkin** (30 Oct – 8 Nov) with today's GM tools ([events §4](events-plan.md#4-seasonal-calendar-season-1-nov-2026--mar-2027)) | S | Announced by Mon 26 Oct |
| 0.6 | Post the **public roadmap** (a short version of this file) in `#announcements` and open the **30-day player survey** | S | Posted |
| 0.7 | Work the [fix priorities](fix-priorities.md) list, P0 first: lag spikes and the upstream security-audit fixes | M | P0 items closed |

**Exit:** we know the real population and pace, the owner has signed off on the pacing plan, and the first event is
live.

---

## Phase 1: pacing and tools (Mon 2 Nov – Sun 29 Nov 2026)

| # | Item | Where in the code | Effort |
|---|---|---|---|
| 1.1 | **`LevelCapPhases`** house rule: `[{date, cap}]`. Stops base EXP at the cap (excess discarded, or banked as Bonus Time credit). Off by default. | `ServerOptions`, `CharacterService.Progression` (the EXP grant paths: kills, awards, exchange) | M |
| 1.2 | Client cap tip shows our phase, not `BaseLvLimitConfig`'s open day (like `ServerDayGates`) | `Ro3Emu.ClientHooks`, then refresh `prebuilt/client` | M |
| 1.3 | **Level-banded `BonusExpAlways`**: `BonusExpAlwaysBelowLevel` (e.g. 50). Above it, the normal Bonus Time budget × `BonusTimeBudgetMultiplier` (e.g. 2), banking up to N days | `WildAfkService.BeforeKillAsync` and its daily budget | M |
| 1.4 | **Event rates**: `EventRates: [{start, end, exp, bonusTime, drop}]` for Bonus Sundays and launch weekends; the client shows a marquee at start and end | `ServerOptions`, the reward roll, `GmCommands` announce | M |
| 1.5 | **Gift tool**: mail an item, or Zeny, to all / online / a level range / returners, as a GM command and a loopback POST for the admin panel; plus a "login gift" that fires once per role inside a window | `MailService.SendRawAsync` (exists), `GmCommands`, `Http` loopback | M |
| 1.6 | **Small-population match sizes**: promote `ChickenTestPlayers`/`ChickenTestTeamSize` to a real `Arena` option, and add the same for Dragon Coffin Island; set `GuildLeagueMinMembers` to about 10 on live | `ServerOptions`, `TimedBattleService` | S–M |
| 1.7 | **Stats report**: loopback `GET /ro3emu/stats` (real online/DAU/WAU, level histogram, job spread, Zeny/Crystal minted vs sunk, event participation), and a weekly post to `#staff-chat` from `solro3-auto` | `Http`, a SQLite read model | M |
| 1.8 | **Bot taper**: the bot runner keeps `max(0, target − real online)` bots in the world | `Ro3Emu.AiBots` | S |
| 1.9 | Phase 1 cap set on live (≥ the top real player from 0.1); the cap schedule is published | config | S |
| 1.10 | **Upstream sync**: upstream `aethai/ro3emu-server` is 186 commits ahead of the live branch (talent and passive execution, boss special skills, pet guardian skills, Rift Crisis, Seven-Day Goal, Codex unlocks, voice, external security-audit fixes, 20–21 Oct client support) and renamed fields codebase-wide. Branch from upstream `main`, re-apply the live fixes on top (dropping the ones upstream fixed its own way), run the full data-backed suite, replay a copy of the realm, then switch. **Port the security-audit fixes first, on their own.** Release the new content as the Phase 2 and 3 content drops. See [fix priorities](fix-priorities.md#the-one-strategic-item-catch-up-with-upstream) | upstream `main` → new live branch | L |
| 1.11 | **Party access for a small realm**: a house rule for the minimum party size of dungeons (e.g. 3 instead of 5; the `!dungeonsolo` test aid shows the path), monster HP scaled to the party if needed; AI players may fill a short dungeon party | `ServerOptions`, `DungeonService`, `Ro3Emu.AiBots` | M |
| 1.12 | **Stall and trade-center sales tax** (a few %, removed from the economy) as the first Zeny sink | `StallsService`, trade center | S–M |
| 1.13 | **🚀 Phase 2 launch: cap 80** (≈ Fri 27 Nov), with a Bonus Weekend via 1.4 and the first content drop from 1.10 | config + event | S |

**Exit KPIs:** every Arena Night queue fires; D7 return ≥ 25 %; no more than 15 % of real players sit at the cap for
more than 2 weeks before the next phase.

---

## Phase 2: the heart of Season 1 (Mon 30 Nov 2026 – Sun 17 Jan 2027)

| # | Item | Effort |
|---|---|---|
| 2.1 | **Event scheduler**: a config list (weekday, time, type) that announces 15 minutes ahead, opens and closes windows, and starts the event rates. GM events stay manual | M–L |
| 2.2 | **Event token currency + shop**: an unused `ItemConfig` currency renamed by the client hook (the Supporter Token pattern in `F2PRules`); seasonal cosmetics stay buyable later | M |
| 2.3 | **Returner kit** on the first login after 14 or 30 days away (via the gift tool), plus a "what changed" mail | S–M |
| 2.4 | **Mentor and bring-a-friend tracking**: a party-time link between a Base 70+ player and a new role up to the job change; rewards for both at Base 40 | M |
| 2.5 | **Balance patch #1** from the Training Ground DPS per class, the PvP results and player council feedback. A few changes, with the numbers published | M |
| 2.6 | **Economy sinks round 1**: a cosmetic Zeny shop rotation, guild donation goals (guild-wide cosmetic or buff), and World Auction Nights | M |
| 2.7 | Catch-up EXP: +50 % for roles below the current cap minus 10 | S |
| 2.8 | **🎄 Lutie Lights** (18 Dec – 3 Jan), with an advent login gift via 1.5; **🎆 New Year** countdown | S |
| 2.9 | **🚀 Phase 3 launch: cap 90** (≈ Fri 8 Jan) together with balance patch #1; **🌱 Fresh Start Week** (11–17 Jan) | S |
| 2.10 | **Party over auto**: bonus EXP for 3+ real players together on a map; auto-battle (`session.AutoBattle`) stays on the budgeted Bonus Time | S–M |
| 2.11 | **Event spawn command**: many weak monsters on a field, or a chosen monster at the GM's position (Poring Pop, Novice Survival) | M |
| 2.12 | **Pity counter for rare drops**: after N kills without the rare drop, its chance rises step by step | M |

**Exit KPIs:** D30 return ≥ 10 %; weekly active / monthly active ≥ 50 %; Zeny sunk ≥ 70 % of minted; at least 4
guilds active in guild content each week.

---

## Phase 3: endgame and the Season 1 finale (Mon 18 Jan – Wed 31 Mar 2027)

| # | Item | Effort |
|---|---|---|
| 3.1 | **Season rewards**: titles, frames and cosmetics from the leaderboards (MVP, Arena, Training DPS, Guild League, Territory Wars), granted at the season end | M |
| 3.2 | **Territory Wars cadence**: 7 days instead of 14 once 4 or more guilds take part (`TerritoryWarCycleDays`); Guild League season | S |
| 3.3 | **Balance patch #2** | M |
| 3.4 | **🧧 Lunar New Year** (6–21 Feb): red envelope Zeny gifts with a fee (a sink), lucky draw from dailies | M |
| 3.5 | **💘 Valentine's** (12–16 Feb) with the built-in Wedding Crashers | S |
| 3.6 | **🚀 Phase 4 launch: cap 99** (≈ Fri 19 Feb) and the "first 99 per class" hall of fame | S |
| 3.7 | **🏆 Season 1 finale** (March): final ladders, rewards granted, Season 2 preview and survey #2 | S |
| 3.8 | **GvG balance**: tiered limits on game-deciding items in Territory Wars (NovaRO-style, set with the player council); daily castle rewards for the holder, with diminishing returns for long holds | M |
| 3.9 | **Duel ladder**: 1v1 and 2v2 ratings and a monthly tournament | M |

---

## Phase 4: Season 2 and the long term (Q2 2027 on)

| # | Item | Effort |
|---|---|---|
| 4.1 | **Optional fresh-start realm**, OSRS Leagues / PoE league style: a second `DataDir`/cluster for 8–10 weeks with its own rules (for example faster rates or a race to 99). Cosmetic rewards carry back to the main realm; the main realm is **never** wiped | L |
| 4.2 | **Bar (tavern)**: still unimplemented (README "Not yet"), and a social hub that helps retention | L |
| 4.3 | Season 2 theme and phases (if the main realm is capped at 99: new cosmetic tracks, Codex goals, guild progression and harder MVP variants instead of a higher cap) | M–L |
| 4.4 | **Recruitment push** before the official RO3 launch (expected 2027): RateMyServer and top-list listings, a "why play here" page (free, fair, community-run, no pay-to-win), and outreach to players from the closed official services (RO Brazil, Prime EU). Target **EU players**: the official RO3 excludes them, and Reddit commenters say they'll go private because of it. On r/RagnarokOnline, post as a person, not an ad: threads that read as ads or AI text get dismissed. Server lists that track non-predatory servers are worth submitting to | S–M |

---

## Features still missing

The emulator's own "Not yet" list (upstream README, 22 Oct 2026), placed on this roadmap. Some may already be closer after
the upstream sync (1.10); re-check the README then.

| Feature | What's missing | Why it matters here | When | Effort |
|---|---|---|---|---|
| **Boss script mechanics** | Special skills are in, but mechanics that lived only in the original server's scripts (bubbles, bells, rhythm stages) are plain area attacks, and knockback does not move players | The top dungeon complaint ("bosses have 0 mechanics"). MVPs are the social events of the strategy (§4.5) | Phase 2 (after the sync), boss by boss, starting with the most-run dungeons | L |
| **Guild extras: merging, impeachment, group mail** | Not implemented | Critical for a small realm: merging lets two half-empty guilds become one that can play Guild League; impeachment rescues a guild whose leader quit; group mail is how officers organise war nights | **Phase 1–2**: impeachment and group mail first, then merging | M |
| **Titles** | Not implemented | The season rewards, hall of fame and monthly ladders (events plan) need a title system to give out | **Phase 2**, before the Season 1 finale (3.1) | M |
| **Paragon (Astral Level)** | Not implemented | Post-99 progression: what capped players do after Phase 4 without raising the cap (4.3) | Phase 3–4 | L |
| **Survivor Battle extras: follow, spectate, shop, Kafra adventure** | Not implemented | Spectating keeps eliminated players watching (good for small matches); the shop gives Survivor points a use | Phase 2, with Arena Night | M |
| **Mock Battle extras** | The arena's monsters and the 40-player team frame | Mock Battle needs 40 players; on a small realm, pair it with the small-population sizes (1.6) | Phase 3 | M |
| **War of Emperium extras: commander tools, siege weapons, vehicles** | Not implemented | WoE is the bright spot players praise in RO3; the Territory Wars cadence (3.2) benefits | Phase 3 | L |
| **Shop gifting** | Not implemented | Lets players gift cosmetics (Lunar New Year red envelopes, gift-exchange week) | Phase 2, before Lutie Lights | S–M |
| **Story dungeon stages still simplified** | Some stages are reconstructions | Story quality for new players and Fresh Start Week | Ongoing, as reported | M |
| **Tavern (bar)** | Not implemented | A social hub (4.2) | Phase 4 | L |
| **Guild voice** | Only party voice is implemented upstream (relay in the server, off by default, `Voice:Port` turns it on) | Voice in-game, without Discord; not in the live branch until the sync | After the sync: turn party voice on, then guild voice | M |

## Risks and how we handle them

| Risk | Mitigation |
|---|---|
| Players at the top feel the cap as a nerf | The first cap is ≥ the top real player; nobody loses levels; EXP above the cap is banked; announce 7 days ahead and explain why |
| Staff burnout from events | Events cost 30–60 minutes; the scheduler (2.1) automates the weekly part; skip a week rather than run a bad event |
| The population is too small for PvP and GvG | Sizes as house rules (1.6), fixed nights, participation rewards, recruitment (4.4) |
| Backlash over bots | `[AI]` tag, the taper (1.8), bots never counted in published numbers |
| Economy inflation | The ×10 budget (1.3), weekly mint/sink numbers (1.7), sinks (2.6), auctions |
| Legal pressure on RO private servers ([Massively OP](https://massivelyop.com/?p=386938)) | Keep the realm non-commercial in spirit: no money for power, Supporter rewards cosmetic only, and no game files hosted (the launcher already downloads them from the publisher) |
| The official RO3 launch pulls players away (2027) | Build the community and guild bonds first; be the free, fair alternative to a monetised official launch |
| Other RO3 private servers open (at least one is monetising heavily, per Reddit) | Compete on fairness, stability and community, not on rates or freebies |
| Players reject autoplay-heavy play | Party bonuses (2.10), budgeted Bonus Time (1.3), and rewards concentrated in social content |

## The weekly operating loop

- **Monday:** read the stats report (1.7) and the bug automation's PR list; pick the week's feature and balance work.
- **Thursday:** Community Night GM event.
- **Sunday:** weekly patch notes (automation draft + feature-lane items), the event recap, and a reply round in
  `#suggestions`.
- **Monthly:** roadmap post, Suggestion of the Month, player council, balance patch.
