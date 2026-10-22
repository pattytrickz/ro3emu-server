# Sol RO3: balance and retention strategy

*Written 22 Oct 2026, about a week after the realm opened. It is based on the live branch `v1.0.0-fixes`, on outside
research, and on about 45 Reddit threads from r/RagnarokOnline, r/RagnarokOnline3 and r/MMORPG (2022–Oct 2026),
including the thread about this emulator itself (sources at the end). Companion plans: [events](events-plan.md) ·
[roadmap](roadmap.md).*

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

Reddit adds one big point: **RO players want "RO3 without the cash grab"**. When this emulator went public, the
thread got 220 upvotes, and the top reply was "So the community can fix this cashgrab and make a real game?"
Players don't object to RO3's graphics or world. They object to pay-to-win, combat power, card-pack gacha, and
autoplay that plays the game for you. Our pitch is the fair, social version of RO3. The balance work should make
**playing together worth more than AFK auto-hunting**.

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

### What Reddit says

Read through the Arctic Shift Reddit archive (Reddit blocks cloud IPs directly). Upvote counts are shown where they
indicate how widely a view is shared. Links are in the sources.

**About RO3 itself** (most relevant to us)

| Finding | Evidence |
|---|---|
| There's real demand for a fair RO3 private server | The thread on this emulator: 220 upvotes. "So the community can fix this Cashgrab, and make a real game?" (87). "If official servers don't allow EU players, I'm playing on a private server" (13). Others: "i hope they release it soon, that way ill be able to pick a private server that actually makes their game good." |
| Other RO3 private servers are monetising heavily | "There's a pServer releasing this Friday of RO3 and they not only didn't remove the monetization, they doubled up on it" (53) |
| Autoplay and auto-walk are the main gameplay complaint | "You have to do 10 daily quests, but the game does them for you… then you have stamina to make your character autohunt… basically cookie clicker" ([Thoughts on RO3](https://www.reddit.com/r/RagnarokOnline/comments/1vqt82e/), 159). "Imagine… not knowing the names of the cities… That's what autoplay does" ([Feelings about Ragnarok 3](https://www.reddit.com/r/RagnarokOnline/comments/1w4clc3/)). A minority view: older players with little time like auto for low-level grinding |
| Small groups get locked out of dungeons | "All the dungeons require 5 to 10 player party, so if you don't make friends you're shit out of luck" (12). "There's a 'match making' button in RO3 but it literally never works… you have to 'apply' to [parties]… they ignore" |
| PvP and stats feel off | "Pvp modes are an unbalanced mess… a bad version of [MOBAs]" (9). "The stats are a lie… no actual breakpoints" |
| Pay-to-win is the expectation to beat | "RO3 is P2W and we can't do anything about it" (117 / 94 comments). "Ragnarok 3 is complete garbage" (144). Combat power and card packs are read as P2W signs ([reel thread](https://www.reddit.com/r/RagnarokOnline/comments/1iusmr1/)) |
| WoE is the bright spot | "The WoE footage out of RO3 is genuinely great, and honestly that makes the rest of it sadder." |
| Official Ragnarok Zero Global (2026) shows what drives people away | Cash ores within a month ([44](https://www.reddit.com/r/RagnarokOnline/comments/1w67wcv/)); "Uninstalled… save your money" (117 / 85 comments); paid EXP buffs and death-penalty protection; a level-60 wall; "no party play at all… Auto Hunt does suck and ruin everything" |

**About RO private servers in general**

| Finding | Evidence |
|---|---|
| Too-fast rates kill parties and then the server | "50/50/50 is pretty much instant level which obliterates parties"; "if the server doesn't have the population it usually dies off fast due to boredom. 10x or 15x… the better middle ground" ([rates thread](https://www.reddit.com/r/RagnarokOnline/comments/1u78smd/)). "5x… everyone levels in comodo and quits after a week" ([what makes a successful server](https://www.reddit.com/r/RagnarokOnline/comments/1l9hpqb/)) |
| The journey is the point | "Because the journey gives meaning to the goal. Killing an MvP with your guild after months of effort? You'll never forget." (25, [low rates thread](https://www.reddit.com/r/RagnarokOnline/comments/1jz5djv/)). "Progression is too fast, it's not satisfying" (about uaRO) |
| What makes a server successful | "The server owner not needing your money" (17); "all equipment can be achieved in game via farming… instead of gacha" (16); "A community…" (15). Payon Stories is the most-recommended server: "best community and helpful GMs", donation-free |
| How players spot a dead server | "When you go to the main city and there aren't any people selling/buying stuff" (7); level parties scarce, MVP hunting stale, no WoE; "when new players are significantly disadvantaged relative to early adopters" ([dead server thread](https://www.reddit.com/r/RagnarokOnline/comments/1ekol34/)) |
| Inflation arrives fast without sinks | "Crazy inflation… in a span of two months… huge incentives to farm zennies and the lack of sinking mechanism." Suggested fixes: tax vending/autotrade; *affordable* stat/skill resets (expensive ones go unused); costly, non-guaranteed card removal; Zeny-cost random enchant rerolls ([inflation thread](https://www.reddit.com/r/RagnarokOnline/comments/1tfx1jd/)) |
| Seasonal wipes are divisive | "It burns out players… People won't be motivated to start 2 months late." A success case: ccRO, where card collection unlocks **permanent account rewards that survive the wipe**. "PoE's reset isn't the mechanic, the league is… a plain wipe just asks you to redo work" ([reset thread](https://www.reddit.com/r/RagnarokOnline/comments/1vlhwn9/)) |
| Players take breaks rather than quit | "Nobody truly quits. You just take extended breaks." (57). "Xmas season is the best to play the game" ([quit thread](https://www.reddit.com/r/RagnarokOnline/comments/1het4z9/)) |
| What makes WoE fun, and what kills it | "The feeling of being an important part of a complex machine… the pre and post woe administration… visiting 'your' treasure room once per day" (26, [WoE thread](https://www.reddit.com/r/RagnarokOnline/comments/1sblgyk/)). It dies when one guild dominates, rewards are useless, or rare power items decide fights. NovaRO's fix: tiered limits on MVP cards in WoE ([MVP cards thread](https://www.reddit.com/r/RagnarokOnline/comments/1rxu2ou/)) |
| What open-world PvP still works | 2v2 and 3v3 duels; PK on MVP maps "was actually pretty fun for a while" ([PvP thread](https://www.reddit.com/r/RagnarokOnline/comments/ruusk2/)) |
| AI and fake players: wanted for atmosphere, distrusted in counts | An AI "population engine" pack got 73 upvotes: "one of [the game's] major flaws nowadays is the lack of players… that nostalgia hit of going to culvert at level 12 and seeing 100 other players." But on headcounts: "Uaro rewards you for being afk, so the numbers are inflated" |
| The cozy social town is the memory | "Just chilling somewhere in prontera… Then someone accidentally summoning an MVP, and all hell breaks loose" (66, [cozy thread](https://www.reddit.com/r/RagnarokOnline/comments/1c1ecvj/)) |
| Readers spot AI-written posts and punish them | A server owner's post was dismissed with "The AI text though" (19) and "Can't be bothered to write his own post means I can't be bothered to try his… server" ([1v4vwze](https://www.reddit.com/r/RagnarokOnline/comments/1v4vwze/)) |
| Monetising a private server attracts Gravity | "The issue of a monthly subscription private server is that it will easily be a target to Gravity to shut down" ([subscription thread](https://www.reddit.com/r/RagnarokOnline/comments/1x6d72f/)) |

**r/MMORPG on catch-up and chores:** catch-up gear drives many players away ("Makes me quit faster", 16, [catch-up
thread](https://www.reddit.com/r/MMORPG/comments/1nrgvvh/)). "The easier things are to do, the more of a chore they
feel like" ([theme park thread](https://www.reddit.com/r/MMORPG/comments/1u3we09/)). And "the worst way to experience
a MMO as a beginner is to rush so you can catch up with your veteran friends" (340,
[1awrfrz](https://www.reddit.com/r/MMORPG/comments/1awrfrz/)). That last one argues for ways veterans can play
*with* newcomers, not just faster levelling for the newcomers.

The 30-day Discord survey ([events plan §6](events-plan.md#6-community-and-discord)) is still worth running. Reddit
tells us what RO players in general want; the survey tells us what *our* players want.

---

## 3. Strategy: twelve principles

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

Reddit matches this: players like AI players for atmosphere ("seeing 100 other players" at a levelling spot), but
they call out servers whose AFK or bot counts inflate their numbers.

### 3.10 Playing together must beat AFK auto-hunting
RO3's autoplay, auto-walk and stamina-based auto-hunt are the biggest complaint about the official game. We can't
remove them (the client is the client), and some players with little time want them. But the rewards can lean
toward active, social play:
- Auto-battle (`session.AutoBattle`, which the server already tracks) stays on the budgeted Bonus Time.
- **Party play earns more than solo auto:** for example +25 % EXP for 3+ real players on the same map, on top of the
  existing party share.
- MVPs, guild content, WoE and events (the things people remember) give the best rewards, not AFK time.

### 3.11 A small population must still be able to party
"All the dungeons require 5 to 10 player party" and "match making… literally never works" are two of RO3's most
upvoted complaints. On a young realm they are worse. So:
- Add a house rule for dungeon minimum party sizes (the `!dungeonsolo` test aid shows the path), for example 3 instead of
  5. Scale monster HP to the party size if needed.
- Let the AI players fill a dungeon party when a real party is short (they already join real players' parties).
- Use a Discord `#party-finder` with a "LFG" ping role, and a fixed weekly Party Night.

### 3.12 Our identity: the fair RO3
Gravity's official RO3 and at least one other RO3 private server sell power. We don't. Say so everywhere, and keep
it true:
- No Crystals, ores, EXP or death protection for money. Supporter rewards stay cosmetic.
- Write public posts (Reddit, Discord, patch notes) in a human voice. The bug automation's drafts are a starting
  point, but an owner should edit them before they go out. Reddit readers dismiss posts that read as AI-written.
- Stay non-commercial in spirit. A subscription or power shop is exactly what draws Gravity's lawyers.

---

## 4. Balance plan by area

### 4.1 Levelling pace

**Decision for the owner: how fast should 99 be?** Suggested targets for a player who plays about 2 hours a day:

| Milestone | Target | Today (estimate; measure first) |
|---|---|---|
| Base 40, first job change | Day 2–3 | Day 1–2 |
| Base 70 | Week 3–4 | Probably week 1–2 |
| Base 99 | Week 10–12 (end of Phase 4) | Probably weeks 3–5 |

**Cap or slower EXP? Use a cap.**
- Cutting the EXP rate after launch feels like a bait and switch on a server whose rates were the selling point. It
  hurts casual players and alts most, and the most dedicated players still reach the top in weeks.
- A rising cap keeps everyone **near the same level** (good for parties, guilds and WoE, which a small population
  can't afford to split) and turns each raise into an event.
- It's also RO3's own design (the client still shows the cap line), and it fits the Reddit view that 50× "obliterates
  parties" while the journey "gives meaning to the goal".
- But a cap doesn't stop the ×10 **loot**, so budget the ×10 table above level ~50 as well (step 2 below).
- Check the level histogram first (roadmap 0.1). If most real players are already near 99, a cap changes nothing,
  and the effort goes into endgame content and events instead.

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
  cosmetics, a Zeny fee on refine safety, and event entry tokens bought with Zeny. Reddit's suggestions on top of
  those: a small **tax on stall and trade-center sales** (taken out of the economy, not given to anyone), stat and
  skill resets **priced low enough that people use them**, card removal that costs Zeny and can fail, and Zeny-cost
  random enchant rerolls.
- **Keep the town market alive.** A Prontera full of stalls is what players read as "this server is alive". Make
  stalls easy, put a stall area near the Kafra, and post a weekly "market report" on Discord.
- **Rare drops: consider a pity counter.** After N kills of the same monster without its rare drop, the chance
  rises step by step. Grinding stays meaningful without days of bad luck ([rates thread](https://www.reddit.com/r/RagnarokOnline/comments/1u78smd/)).
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
- **Castle rewards that matter**, paid daily to the holder (WoE players remember "visiting your treasure room once
  per day"). Use **diminishing returns for long holds**, so one guild can't snowball forever.
- **Limit game-deciding items in GvG.** Copy NovaRO's tiered limits on MVP cards: at most N tier-1 power items per
  guild in Territory Wars, decided with the player council.
- **Brackets when there are enough guilds:** for example a "below 80" war bracket, so new guilds have a war they can
  win.
- **Small-format PvP that needs no population:** a 2v2 and 3v3 duel ladder and a monthly duel tournament. Reddit
  says these are the PvP formats in RO that still work.

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
7. **Party over auto.** A bonus for 3+ real players together (§3.10)? A smaller dungeon minimum party size (§3.11)?
8. **GvG limits.** Tiered limits on power items in Territory Wars, and a stall/trade tax as a Zeny sink?

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

Reddit (read through the [Arctic Shift](https://arctic-shift.photon-reddit.com/) archive):
[open-source RO3 emulator](https://www.reddit.com/r/RagnarokOnline/comments/1x4yowq/) ·
[Thoughts on RO3](https://www.reddit.com/r/RagnarokOnline/comments/1vqt82e/) ·
[Feelings about Ragnarok 3](https://www.reddit.com/r/RagnarokOnline/comments/1w4clc3/) ·
[RO3 PC suggestions thread](https://www.reddit.com/r/RagnarokOnline/comments/1iq3d7a/) ·
[RO3 is P2W](https://www.reddit.com/r/RagnarokOnline/comments/1mj4r6q/) ·
[RO3 producer on P2W](https://www.reddit.com/r/RagnarokOnline/comments/1n84p3l/) ·
[RO3 combat power reel](https://www.reddit.com/r/RagnarokOnline/comments/1iusmr1/) ·
[Point of autoplay in RO3](https://www.reddit.com/r/RagnarokOnline/comments/1mobq8r/) ·
[RO Zero Global P2W ores](https://www.reddit.com/r/RagnarokOnline/comments/1w67wcv/) ·
[Uninstalled RO Zero Global](https://www.reddit.com/r/RagnarokOnline/comments/1wt9t2r/) ·
[What makes a successful server](https://www.reddit.com/r/RagnarokOnline/comments/1l9hpqb/) ·
[What makes you play a server](https://www.reddit.com/r/RagnarokOnline/comments/1seyrij/) ·
[Rates preference](https://www.reddit.com/r/RagnarokOnline/comments/1u78smd/) ·
[Why play low rates](https://www.reddit.com/r/RagnarokOnline/comments/1jz5djv/) ·
[When is a server dead](https://www.reddit.com/r/RagnarokOnline/comments/1ekol34/) ·
[Inflation in private servers](https://www.reddit.com/r/RagnarokOnline/comments/1tfx1jd/) ·
[Seasonal server resets](https://www.reddit.com/r/RagnarokOnline/comments/1vlhwn9/) ·
[Why did you quit RO](https://www.reddit.com/r/RagnarokOnline/comments/1het4z9/) ·
[What made WoE fun](https://www.reddit.com/r/RagnarokOnline/comments/1sblgyk/) ·
[MVP cards ruining WoE](https://www.reddit.com/r/RagnarokOnline/comments/1rxu2ou/) ·
[Why PvP is dead](https://www.reddit.com/r/RagnarokOnline/comments/ruusk2/) ·
[GM event ideas](https://www.reddit.com/r/RagnarokOnline/comments/1sn1qfg/) ·
[AI population engine](https://www.reddit.com/r/RagnarokOnline/comments/1x7zncp/) ·
[Subscription private server](https://www.reddit.com/r/RagnarokOnline/comments/1x6d72f/) ·
[Cozy MMOs](https://www.reddit.com/r/RagnarokOnline/comments/1c1ecvj/) ·
[Building a private server](https://www.reddit.com/r/RagnarokOnline/comments/1v4vwze/) ·
[Low-rate PvE/party servers](https://www.reddit.com/r/RagnarokOnline/comments/1slogf4/) ·
[Farmable premium currency](https://www.reddit.com/r/RagnarokOnline/comments/1tmrf9q/) ·
[r/MMORPG: catch-up mechanics](https://www.reddit.com/r/MMORPG/comments/1nrgvvh/) ·
[r/MMORPG: theme park retention](https://www.reddit.com/r/MMORPG/comments/1u3we09/) ·
[r/MMORPG: rushing to catch up](https://www.reddit.com/r/MMORPG/comments/1awrfrz/)

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
