# Sol RO3: balance and retention strategy

*Written 22 Oct 2026, about a week after the realm opened. It is based on the live branch `v1.0.0-fixes` and on
outside research (sources at the end). Companion plans: [events](events-plan.md) · [roadmap](roadmap.md).*

## In one paragraph

RO3 was built to spread its content over about 200 days: a level cap that rises with the server's open day (99 on day
198), season stages, Exploration chapters on days 8 to 14, and guild challenge tiers on days 3 and 5. This realm has
removed nearly all of that pacing. Everything is open from day 1, there is no level cap, and every field and cave kill
pays the ×10 Bonus Time table with no daily budget. Fast early levelling is good for a new private server, but it
leaves players "done" in weeks instead of months. The pattern that kills RO private servers repeats on every forum:
rush to cap, nothing left to do, low population, inactive staff, players leave. The strategy below keeps the fun fast
start and adds back **a pacing spine** (phased caps and a budgeted Bonus Time), **a reason to log in each week**
(scheduled guild and PvP nights sized for our population), **a cosmetic and social endgame** that never runs out,
and **visible, regular staff activity**.

---

## 1. Where the realm stands (repo audit)

The live branch is `v1.0.0-fixes` (merged PRs [#45](https://github.com/pattytrickz/ro3emu-server/pull/45),
[#46](https://github.com/pattytrickz/ro3emu-server/pull/46),
[#47](https://github.com/pattytrickz/ro3emu-server/pull/47),
[#49](https://github.com/pattytrickz/ro3emu-server/pull/49) …). These are its pacing-relevant settings and systems:

| Lever | Current state | Effect on pacing / balance |
|---|---|---|
| `OpenContent` (`docs/guide/open-content.md`) | On. `IgnoreServerDay`, `PerpetualWindows`, `SeasonStageActivities` | Every function, season stage, Exploration chapter, card pool and pass opens on day 1 and never closes. The original drip-feed is gone. |
| Level cap (`BaseLvLimitConfig`) | **Display only.** The client still shows the cap for the open day, but "the server applies no cap" | Nothing stops a player from reaching 99 in the first weeks. The client's cap tip disagrees with reality. |
| `BonusExpAlways` (`WildAfkService.BeforeKillAsync`) | On | Every ordinary field **and cave** kill uses the Bonus Time drop table (NPCDropConfig environment 1, about ×10 the normal table). It ignores the daily Bonus Time budget (`HighMultiplierTimeSeconds`), so ×10 has no daily limit. It also pays out whatever loot that table holds, so it adds Zeny and materials, not only EXP. |
| `Pass.FreePremium` + `Pass.Play` | On | Everyone has the premium battle-pass track and earns Pass EXP from ordinary play. |
| `F2P` rules (`F2PRules.cs`) | On | Crystals come from play: 20 per daily challenge, 80 + 1 Jerky weekly, 20 per MVP fight (first few a day). The Crystal Shop has weekly caps (2 Blessed Ores a week, 20 Oridecon/Elunium…). Cosmetics cost about ¼ of the original price. Supporter Tokens buy only cosmetics. **This model is healthy; keep it.** |
| Zeny Shop additions | 100 z instant potions (15–20 % HP, SP) and elemental arrows that are never used up | Cheap burst healing can trivialise MVPs and PvP unless cooldowns limit it. **Check it.** |
| QoL house rules | `LevelUpRefill`, `SitRegenMultiplier`, party EXP share on the same map | Fine; they cut downtime. |
| AI players (`docs/guide/ai-bots.md`) | Levels 1–95, 30 % in town, `[AI]` name tag, join parties within 10 levels, never steal kills | Good filler for levelling and towns. They do **not** fill PvP matches. Honest labelling protects trust (see §3.9). |
| Population-gated content | Survivor Battle needs **100** entrants (teams of 5), Dragon Coffin Island **30** (teams of 5), Guild League registration **40** members (`GuildLeagueMinMembers` can lower it), Territory Wars every **14** days | With a small population these never fire, or they fire empty. That wastes some of the best content the realm has. |
| Measurement | `ChickenTestPlayers`/`ChickenTestTeamSize` exist only as a *test aid*. Training Ground (activity 119) stores every player's best DPS. Leaderboards exist. | We already store useful balance data (DPS per class, rank, scores) but nothing reports it. |
| Upstream content not yet live | `main` has 16 commits (17 Oct) that `v1.0.0-fixes` lacks: Rift Crisis rounds, Seven-Day Goal, Codex monster unlocks, profile likes and popularity, pet hatchery fixes, MVP summons, Guild League elites, award boxes and item actions | **Ready-made content drops.** Release them as phase-launch content instead of all at once. |
| GM tooling | `!announce`, `!mvp spawn/where/goto`, `!guildchallenge open`, `!guildhunt open`, `!worldauction open`, `!actauction`; `!mail` mails only the GM | Enough for GM-run events. Missing: a server-wide mail or login gift, timed EXP/drop multipliers, and an event scheduler. |

### The diagnosis

1. **The runway is too short.** With no cap, ×10 kills without a budget, and everything open, the "journey" part of
   the game shrinks from months to weeks. Once players reach the top, they have only the endgame loops to play.
2. **The endgame loops depend on population.** The big PvP and GvG modes need 30 to 100 people at once. A young realm
   doesn't have that many players online at the same time.
3. **The economy has an uncapped tap.** ×10 drops with no daily budget means Zeny and materials enter the game at a
   rate the original never planned for. Sinks (refine, cosmetics, guild) must keep up, or prices inflate and new
   players fall behind.
4. **The good parts are already there.** F2P Crystals, cosmetic-only supporter rewards, honest AI labelling, a
   working pass, and a large amount of reconstructed content. Most of the work is **tuning and scheduling**, not
   building new systems.

---

## 2. What the research says

### Ragnarok-specific

- **Why players leave RO private servers.** rAthena threads name unbalanced stats, inactive GMs, low population,
  overpriced donation items, poor new-player guidance and a server "not well maintained… lack of update"
  ([rAthena: What make you leave a private server?](https://rathena.org/board/topic/54418-what-make-you-leave-a-private-server/),
  [rAthena: How to attract players](https://rathena.org/board/topic/82159-how-to-attract-players-to-play-your-new-server/)).
  Staff activity is the most common theme: "If you don't… update server, help people… be active… People WILL leave"
  ([rAthena: How to increase your server's population](https://rathena.org/board/topic/86720-how-to-increase-your-servers-population/)).
- **Word of mouth beats advertising.** The same threads say ads did little and existing players bringing friends
  mattered more. The "golden age" is the 10–30 player stage when everyone knows everyone, so **use that stage
  instead of rushing past it**.
- **Stay close to the original; customise lightly.** "Keep the GAP between YOUR SERVER and Official Server Close as
  Possible" (rAthena). For us that means using RO3's own systems and data (level-cap table, season stages, Bonus
  Time) as the base, and adding house rules on top as dials.
- **The players' own reviews punish decline.** One RateMyServer review of a 50× server describes coming back months
  later to "very few players left and new gear hard to get"
  ([RMS: Genesis Ragnarok](https://ratemyserver.net/index.php?page=detailedlistserver&serid=23231&itv=6&url_sname=Genesis+Ragnarok)).
  Another listing scores weakest on **community eventfulness and guild competition**
  ([RMS: Nightmare of Ragnarok](https://ratemyserver.net/index.php?itv=999&page=serverstat&serid=23487&url_sname=Nightmare+of+Ragnarok)).
- **Rates.** Low rates keep the original's pace and let the economy grow naturally. High rates suit players who want
  WoE/PvP fast; neither is automatically better
  ([gametop.gg](https://gametop.gg/blog/ragnarok-online-private-servers-explained),
  [nostalgic.gg](https://nostalgic.gg/en/blog/best-ragnarok-online-low-rate-private-servers-en)). Community posts
  warn that after a few months on many servers "zeny loses value, items pile up endlessly"
  ([r/RagnarokOnline FAQ, mirror](https://r.datuan.dev/r/RagnarokOnline/wiki/general_faq)).
- **Episodes as pacing.** RO servers unlock content by *episode*. Gravity's RE:START progression server added
  content monthly ([mmos.com](https://mmos.com/news/ragnarok-online-classic-progression-server-works)). Players
  accept a staged unlock schedule if they can see it.
- **Admin control is the private server's advantage.** Admins can tune rates, drops and items to target a particular
  audience ([Debeauvais & Nardi, study of RO private servers](https://www.artifex.org/~bonnie/Debeauvais-Nardi-RO-private-servers.pdf)).
- **RO3's reputation risk is "mobile-style".** Pre-launch forum threads on RO3 were sceptical about monetisation and
  auto-battle/idle design ([ResetEra](https://www.resetera.com/threads/ragnarok-online-fans-are-you-interested-in-playing-ro3.1278204/)).
  A free, fair, hands-on realm is our pitch against that.
- **Timing.** Official RO3 is now expected in **2027** (China, then global)
  ([release tracker](https://www.mmochain.com/ragnarok-3/ro3-release-date/)). Official RO services are closing, for
  example **RO Brazil (29 Jul 2026)** and **RO Prime EU (23 Sep 2026)**
  ([TecMundo](https://www.tecmundo.com.br/voxel/504158-servidor-de-ragnarok-online-brasil-sera-desativado-apos-mais-de-20-anos.htm),
  [ruRO wiki](https://www.rurowiki.ru/news/show/2545/)). So there are displaced RO players looking for a home *now*,
  and we have until the official launch to build a community that won't leave.

### Other MMOs

| Game | What they do | Lesson for us |
|---|---|---|
| WoW Classic: Season of Discovery | **Phased level caps** (25 → 40 → 50 → 60), each phase with content tuned to its cap, roughly 1–2 months per phase ([wiki](https://warcraft.wiki.gg/wiki/Season_of_Discovery), [Warcraft Tavern](https://www.warcrafttavern.com/wow-classic-sod/guides/phases/)) | A cap that rises on a published schedule turns "I'm done" into "I'm ready for next phase". Each phase gets a moment of hype. |
| Path of Exile | 3–4 month leagues with a fresh economy; old characters move to Standard ([game-wisdom](https://game-wisdom.com/general/design-philosophy-behind-poe-2s-seasonal-model-resetting-everything-works)) | Fresh starts level the field and reset inflation. The catch: a big launch spike, then a steep drop ([PoE forum](https://www.pathofexile.com/forum/view-thread/3733882)). Use it for an *optional* side realm later, not the main world. |
| Old School RuneScape | Leagues (about 8 weeks, yearly) and seasonal Deadman, alongside the permanent game, with rewards that carry over ([OSRS wiki: Leagues](https://oldschool.runescape.wiki/w/League), [Seasonal Deadman](https://oldschool.runescape.wiki/w/Update:Dev_Blog:_Seasonal_Deadman_Changes)) | Seasonal modes **beside** the main world, with cosmetic rewards that carry back, give veterans a fresh race without wiping anyone. |
| Guild Wars 2 | Horizontal progression with no gear treadmill; masteries, cosmetics and festivals ([GW2 wiki: Endgame](https://wiki.guildwars2.com/wiki/Endgame), [Massively OP](https://massivelyop.com/2015/05/20/flameseeker-chronicles-expanding-into-heart-of-thorns/)) | Progress that never goes obsolete (wardrobe, Codex, titles, mounts) keeps casuals and returners. |
| Lost Ark | Daily and weekly chores drive burnout; Rest Bonus banks missed days ([Sportskeeda](https://sportskeeda.com/esports/what-rest-bonus-lost-ark), [Steam thread](https://steamcommunity.com/app/1599340/discussions/0/5440953210433978809)) | Don't punish a skipped day. Budgets that bank up (like Bonus Time) beat "log in or lose it". |
| Ragnarok M | A daily 300-minute combat budget, after which rewards drop ([Nextrift](https://nextrift.com/?p=745)) | RO3's own Bonus Time is the same idea. Use the budget as the dial instead of removing it. |
| Black Desert | Returning-player kits after 30 days away ([AppSpy](https://www.appspy.com/news/13187/black-desert-mobile-aims-to-welcome-back-lapsed-players-by-aggressively-launching-rewards-and-benefits-at-them)) | A comeback package that catches a returner up to their friends recovers lapsed players cheaply. |
| WoW (counter-example) | Catch-up gear makes earlier progress feel pointless, the "treadmill effect" ([Blizzard forums](https://us.forums.blizzard.com/en/wow/t/the-treadmill-effect-whats-the-solution/1813111)) | Catch-up should speed up *levelling*, never hand out top gear. |
| Live-service in general | Double-XP weekends and stacked bonuses late in a season stop mid-season drop-off ([outof.games](https://outof.games/news/9618-how-live-service-games-keep-communities-engaged-between-major-updates/)). Daily quests give players at the cap a clear to-do list ([Engadget](https://www.engadget.com/2014-01-29-mmo-mechanics-encouraging-the-daily-grind.html)) | Plan the *second half* of each phase. That's when players drift away. |

Retention benchmarks for reference: median games keep about 22 % of players on day 1, 4 % on day 7 and 0.7 % on day 30.
The top 10 % reach about 40 / 12 / 4 % ([2026 mobile benchmarks](https://investgame.net/wp-content/uploads/2026/01/2026-01-20-Mobile_retention_benchmarks_2026.pdf)).
These are mobile numbers. An MMO community should be judged on day 30 and beyond
([Stash](https://www.stash.gg/glossary/game-retention)).

Reasons people gave for quitting their last MMO (a reader poll): poor design 10 %, no fulfilling endgame 10 %,
pay-to-win 13 %, toxic community 8 % ([Massively OP](https://massivelyop.com/2016/11/13/leaderboard-what-made-you-quit-your-last-mmorpg/)).

> **Research gap:** Reddit (r/RagnarokOnline, r/RagnarokMobile, r/PrivateServers) blocks access from the
> environment this plan was written in, and search engines index it poorly. The forum evidence above comes from
> rAthena, RateMyServer, Steam and Massively OP. Before Phase 2, ask the Discord community directly
> ([events plan §6](events-plan.md#6-community-and-discord)). It is the most relevant "forum" we have.

---

## 3. Strategy: nine principles

### 3.1 Pace the runway with a published schedule
Add back a **phased level cap** that rises on dates everyone can see, like WoW SoD or RO episodes. Each phase opens
with a headline event and a small content push. The cap line in the client already exists (`BaseLvLimitConfig`), so
the UI is ready.

### 3.2 Budget the ×10, don't remove it
Keep the fast start. Below the first job change, `BonusExpAlways` fixes a real gap: the reduced table applies between
the Base 30 story and the Base 40 job change. Above that level, bring back a **daily Bonus Time budget that banks up**
(Lost Ark Rest Bonus / Ragnarok M combat time), so a player who misses a day loses nothing and a no-lifer can't run
×10 forever.

### 3.3 Make the endgame horizontal
Wardrobe, Codex, mounts, titles, pets, card collections, Supporter cosmetics and leaderboards. Progress there never
becomes obsolete (GW2), and it suits RO, where headgear and fashion have always been part of the culture. Season
rewards and event rewards should be **mostly cosmetic or convenience**.

### 3.4 Size competition to the population
A 100-player Survivor Battle that never starts is worse than a 20-player one that runs every night. Promote the
`ChickenTestPlayers`/`ChickenTestTeamSize` test aid to a real house rule. Do the same for Dragon Island. Lower
`GuildLeagueMinMembers` to about 10–15. Run fewer, **fixed-time** PvP/GvG nights in the realm's prime time, so the
people who want to fight are all online together.

### 3.5 Guilds are the retention engine
Players stay for people. Funnel newcomers into guilds (guild recruitment channel, AI bots that suggest joining one),
make guild content the weekly anchor (Guild Challenge, Hunting Challenge, campfire, Guild League, Territory Wars),
and reward the **guild**, not only the individual.

### 3.6 Keep the economy fair and visible
- Keep the F2P Crystal model and the cosmetic-only Supporter Tokens. Pay-to-win and overpriced donation items are
  among the most-cited reasons players quit RO servers.
- Measure the Zeny, Crystal and material minted per day against what's sunk. Add sinks before inflation shows
  (cosmetic Zeny shop, guild donations, refine fees, event entry tokens).
- No event or catch-up hands out top gear or refine ore at scale.

### 3.7 Be visibly active
Inactive staff are the most common reason RO players give for leaving. Commit to a cadence: **weekly** patch notes
(the bug automation already drafts them), a **monthly** balance and roadmap post, a GM-run event **every week**,
and replies to the `#suggestions` channel. Players should be able to see the bug-automation lane and the human
feature lane both moving.

### 3.8 Welcome newcomers and returners
- New players: a clear first-hour path (story to Base 30, then job change), a mentor bonus, and a newbie chat or
  Discord role.
- Returners (14 or 30 days away): a comeback kit (Bonus Time credit, consumables, a cosmetic) and a "what changed"
  mail.
- Late joiners: catch-up through faster **levelling** in the phases already passed, never through free top gear.

### 3.9 Be honest about population
The `[AI]` tag and "say you're a bot if asked" are the right calls. Players punish servers they think inflate
headcounts ([builtbybit thread](https://builtbybit.com/threads/fake-players.685425/post-4905866)). Rules:
- Never count bots in any "players online" figure we publish (Discord, website, server list).
- Shrink the bot count as real population grows. Keep bots where they help (empty levelling fields, party fill for
  dungeons), not in PvP, guild ranks or the economy.

---

## 4. Balance plan by area

### 4.1 Levelling pace

**Decision for the owner: how fast should 99 be?** Suggested targets for a player who plays about 2 hours a day:

| Milestone | Target | Today (estimate; measure first) |
|---|---|---|
| Base 40, first job change | Day 2–3 | Day 1–2 |
| Base 70 | Week 3–4 | Probably week 1–2 |
| Base 99 | Week 10–12 (end of Phase 4) | Probably weeks 3–5 |

**Mechanism (recommended):**
1. **`LevelCapPhases` house rule.** A list of `{date, cap}`. EXP past the cap is banked as Bonus Time credit or
   simply lost. Set the first cap **at or above the current highest real player**, so nobody is pushed back. Example:
   Phase 1 = 70 (now), Phase 2 = 80 (end of Nov), Phase 3 = 90 (early Jan), Phase 4 = 99 (Feb). Two options:
   - Option A: follow `BaseLvLimitConfig` by real open day. The client UI already matches, but reaching 99 takes 198
     days, which is too slow for a ×10 realm.
   - Option B: our own schedule (recommended). The client's cap tip will show the original numbers unless
     `Ro3Emu.ClientHooks` patches it, the same way `ServerDayGates` patches other day gates.
2. **Level-banded `BonusExpAlways`.** Always on below Base 40–50. Above that, the daily Bonus Time budget applies,
   multiplied (for example ×2) and banking up to 3 days.
3. **Catch-up.** Once a phase is past, kills below the current cap minus 10 get +50 % EXP. Late joiners and alts
   catch up, while the race at the top stays fair.

### 4.2 Economy
- **Measure** (weekly SQL report): Zeny minted (kills, quests, NPC sales) and sunk (shops, refine, repairs, taxes);
  Crystals minted and spent; the stall/trade price index of 10 staple items (Oridecon, Elunium, potions, popular
  cards).
- **Sinks to add as needed:** a cosmetic Zeny shop rotation, guild donation goals that unlock guild-wide buffs or
  cosmetics, a Zeny fee on refine safety, and event entry tokens bought with Zeny.
- **Crystal model.** Watch how many weeks a typical player needs for one cosmetic set (≈450 Crystals). Aim for
  2–3 weeks of normal play. If it's faster, Crystals have lost their value; if it's slower, the reward feels out of
  reach.
- **100 z instant potions.** Check whether their cooldown makes MVPs or PvP trivial. If so, raise the price, limit
  them in MVP/PvP maps, or make the 20 % potion a craft or drop.

### 4.3 Class balance
- **Data we already store:** the Training Ground best DPS per player (activity 119, a configured dummy and timer),
  the job distribution, MVP last-hit/contribution and PvP results.
- **Process:** a monthly balance patch. Change a few things at a time, publish the numbers and the reasons, and never
  nerf and buff the same class in the same patch. Collect feedback in a pinned `#balance-feedback` thread.
- **Inputs right now:** the player reports already in the pipeline (Aura Blade/Shield Reflect
  [#48](https://github.com/pattytrickz/ro3emu-server/pull/48), DEX cast time and Jupitel knock-back
  [#47](https://github.com/pattytrickz/ro3emu-server/pull/47), bow Archer normal attack
  [#52](https://github.com/pattytrickz/ro3emu-server/pull/52), season talents shared across builds
  [#51](https://github.com/pattytrickz/ro3emu-server/pull/51)). Fix parity bugs first, then tune. Many "balance"
  complaints in an emulator are really missing mechanics.

### 4.4 PvP and GvG
- Make the match sizes house rules: Survivor Battle 20–40 entrants in teams of 4–5; Dragon Island 10–20 entrants.
  Raise them when the queues fill.
- `GuildLeagueMinMembers` at about 10. Keep Territory Wars at 14 days until 4 or more guilds take part, then consider
  7 days.
- A fixed weekly "war night" in the realm's prime time (see the [events plan](events-plan.md)).
- Rewards for showing up (a participation award, Competition Medals), plus bigger rewards for the winners, so small
  guilds keep coming.

### 4.5 MVPs and bosses
- Keep the Crystals for the first MVPs of the day. They reward showing up without encouraging an all-day farm.
- Publish MVP respawn windows (or a world-boss schedule) so players can meet up. MVPs work as social events.
- A monthly MVP ladder (kills/contribution) with title and cosmetic rewards.

### 4.6 AI players
- Bot count = max(0, target − real online). For example, keep about 40 "bodies" in the world at night and taper the
  bots to 0 when 40 real players are on.
- Keep them out of PvP queues, guild rankings, the trade center and leaderboards (already mostly true).

---

## 5. Metrics to track every week

| Metric | How | Healthy sign |
|---|---|---|
| New accounts, D1/D7/D30 return | SQLite `accounts`/role login times | D7 ≥ 25 %, D30 ≥ 10 % for a community MMO |
| Daily and weekly active (real, not bots) | Logins excluding `aibot*` | Weekly active / monthly active ≥ 50 % |
| Level distribution by week | Role base level histogram | No pile-up at the cap more than 2 weeks before the next phase |
| Time to 40 / 70 / 99 | First reach timestamps | Matches the targets in §4.1 |
| Zeny / Crystal minted vs. sunk | Economy log | Sunk ≥ 70 % of minted after the first month |
| Activity participation | Entries per Survivor/Dragon/Guild League/TW/MVP | Every scheduled event runs with ≥ 70 % of its slots filled |
| Class spread and Training Ground DPS | Job id, activity 119 best DPS | No first job above 30 % of players; DPS within ±20 % of the median for damage roles |
| Bug and suggestion flow | Feedback, `#bug-reports`, `#suggestions` | Median bug fix under 3 days; each suggestion gets a reply within 7 days |

The roadmap adds a small admin report (`/ro3emu/stats` or a weekly Discord post) so nobody runs SQL by hand.

---

## 6. Decisions the owner needs to make

1. **Time to 99.** About 10–12 weeks (recommended), or faster?
2. **Phased cap schedule.** Option B (our own dates, recommended) or the original `BaseLvLimitConfig` table?
3. **Bonus Time above Base 40–50.** A banked daily budget ×2 (recommended), keep unlimited, or the original budget?
4. **Small-population match sizes.** Approve Survivor 20–40, Dragon 10–20, Guild League minimum 10?
5. **Seasons.** Call Phases 1–4 "Season 1" with an end-of-season cosmetic reward (recommended)? Plan an optional
   fresh-start realm for Q2 2027?
6. **Bot policy.** Approve the taper formula and "never count bots" rule.

---

## Sources

RO community and private servers: [rAthena: What make you leave a private server?](https://rathena.org/board/topic/54418-what-make-you-leave-a-private-server/) ·
[rAthena: How to increase your server's population?](https://rathena.org/board/topic/86720-how-to-increase-your-servers-population/) ·
[rAthena: How to attract players](https://rathena.org/board/topic/82159-how-to-attract-players-to-play-your-new-server/) ·
[Debeauvais & Nardi, RO private servers](https://www.artifex.org/~bonnie/Debeauvais-Nardi-RO-private-servers.pdf) ·
[RMS: Genesis Ragnarok](https://ratemyserver.net/index.php?page=detailedlistserver&serid=23231&itv=6&url_sname=Genesis+Ragnarok) ·
[RMS: Nightmare of Ragnarok](https://ratemyserver.net/index.php?itv=999&page=serverstat&serid=23487&url_sname=Nightmare+of+Ragnarok) ·
[gametop.gg](https://gametop.gg/blog/ragnarok-online-private-servers-explained) ·
[nostalgic.gg](https://nostalgic.gg/en/blog/best-ragnarok-online-low-rate-private-servers-en) ·
[r/RagnarokOnline FAQ (mirror)](https://r.datuan.dev/r/RagnarokOnline/wiki/general_faq) ·
[mmos.com: RO classic progression](https://mmos.com/news/ragnarok-online-classic-progression-server-works) ·
[ResetEra: RO3 thread](https://www.resetera.com/threads/ragnarok-online-fans-are-you-interested-in-playing-ro3.1278204/) ·
[RO3 release tracker](https://www.mmochain.com/ragnarok-3/ro3-release-date/) ·
[TecMundo: RO Brazil closing](https://www.tecmundo.com.br/voxel/504158-servidor-de-ragnarok-online-brasil-sera-desativado-apos-mais-de-20-anos.htm) ·
[ruRO wiki: Prime EU closing](https://www.rurowiki.ru/news/show/2545/) ·
[Massively OP: Gravity and private servers](https://massivelyop.com/?p=386938)

Other MMOs and retention: [WoW SoD wiki](https://warcraft.wiki.gg/wiki/Season_of_Discovery) ·
[Warcraft Tavern: SoD phases](https://www.warcrafttavern.com/wow-classic-sod/guides/phases/) ·
[game-wisdom: PoE seasons](https://game-wisdom.com/general/design-philosophy-behind-poe-2s-seasonal-model-resetting-everything-works) ·
[PoE forum](https://www.pathofexile.com/forum/view-thread/3733882) ·
[OSRS Leagues](https://oldschool.runescape.wiki/w/League) ·
[OSRS Seasonal Deadman](https://oldschool.runescape.wiki/w/Update:Dev_Blog:_Seasonal_Deadman_Changes) ·
[GW2 Endgame](https://wiki.guildwars2.com/wiki/Endgame) ·
[Massively OP: GW2 progression](https://massivelyop.com/2015/05/20/flameseeker-chronicles-expanding-into-heart-of-thorns/) ·
[Sportskeeda: Lost Ark Rest Bonus](https://sportskeeda.com/esports/what-rest-bonus-lost-ark) ·
[Steam: Lost Ark burnout](https://steamcommunity.com/app/1599340/discussions/0/5440953210433978809) ·
[Nextrift: Ragnarok M](https://nextrift.com/?p=745) ·
[AppSpy: Black Desert returners](https://www.appspy.com/news/13187/black-desert-mobile-aims-to-welcome-back-lapsed-players-by-aggressively-launching-rewards-and-benefits-at-them) ·
[Blizzard forums: treadmill effect](https://us.forums.blizzard.com/en/wow/t/the-treadmill-effect-whats-the-solution/1813111) ·
[outof.games: between updates](https://outof.games/news/9618-how-live-service-games-keep-communities-engaged-between-major-updates/) ·
[Engadget: dailies](https://www.engadget.com/2014-01-29-mmo-mechanics-encouraging-the-daily-grind.html) ·
[2026 retention benchmarks](https://investgame.net/wp-content/uploads/2026/01/2026-01-20-Mobile_retention_benchmarks_2026.pdf) ·
[Stash: game retention](https://www.stash.gg/glossary/game-retention) ·
[Massively OP: why players quit](https://massivelyop.com/2016/11/13/leaderboard-what-made-you-quit-your-last-mmorpg/) ·
[builtbybit: fake players](https://builtbybit.com/threads/fake-players.685425/post-4905866)
