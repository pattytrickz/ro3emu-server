# Handoff: client choice, Finland staging realm, cutover

*Written 23 Oct 2026, 05:10 Central, for the next Claude session on the owner's local machine (the cloud session ran out of
credits). Read this first, then [upstream-merge-plan.md](upstream-merge-plan.md). Everything here is the state of the repo
and of Discord as the cloud session left it; nothing below was done on the Finland server or on a real client: the cloud
session had neither.*

## What you are finishing

The Sol RO3 line (`pattytrickz/ro3emu-server`, branch `v1.0.0-fixes`, deployed every 4 hours by the bug automation) has been
merged with the upstream emulator (`aethai/ro3emu-server` `main`) on a **staging branch**. The owner wants:

1. **Select the client** that works for everyone on the server.
2. **Stand up a staging realm on the Finland server**; that is where the merge is tested.
3. **Run the test plan there.**
4. **Cut over at 10:00 Central "tomorrow".** The owner wrote this overnight on 22/23 Oct. *Ask which day they mean before doing
   anything time-dependent:* Friday 23 Oct 10:00 is five hours after this was written, Saturday 24 Oct 10:00 is the other
   reading. See "Timeline and go/no-go" for what that does to the plan.

## State of the code

| | |
|---|---|
| Live line | `origin/v1.0.0-fixes` at `aa9075c` (includes PRs #60, #61, #62; #62 was staged for the 10 pm release, check that it shipped) |
| Staging branch | **`origin/sync/upstream-2026-10-22`** at `987c576` (merge `067f8da` + fixes). Includes everything in live up to `aa9075c` |
| Rollback build | live `aa9075c`. A local tag `pre-upstream-sync` points at it but was **not pushed**: `git tag pre-upstream-sync aa9075c && git push origin pre-upstream-sync` |
| Upstream | `aethai/ro3emu-server` `main` at `9f3c04d` (22 Oct). Fetch it with `git remote add upstream https://github.com/aethai/ro3emu-server && git fetch upstream` |
| PR | None opened for the staging branch. Open it as a **draft** after staging passes; merge only at cutover |

Tests on the staging branch (data-backed, `dotnet test tests/Ro3Emu.Tests -c Release`, about 20 minutes): the last full run
had 2,098 tests, 2,087 passed and 6 failed. Four of those six were then fixed and verified one by one (the full suite was
**not rerun** after the last fixes, so rerun it first). What still fails:

- `ShopRulesTests.DailyLimitResetsAtFiveInTheRealmZoneAndResyncsOnlineRoles` fails on pure upstream too.
- `SkillCombatTests.NotInterruptSkillLeavesTheRunningAutoAttackGoingOn` failed on live before the merge too (documented baseline).
- Timing flakes that pass on rerun: `PowerTests.ChangesPublishAndTheHistoryIsKeptAcrossRelog`,
  `SkillCombatTests.ARepeatedNormalAttackIsAcceptedButHitsOncePerSwingTime`, `TowerTests.CommittedClearWithApplyFault...`,
  `WildAfkTests.DailyTick...`.

How the live-vs-upstream conflicts were decided is in the merge plan ("Status" and the decisions table). The one data
migration (Growth Classroom claims) is `GrowthClassService.MigrateLiveClaims`, tested.

**What the cloud session could not verify** (it is a Linux box without the game or Windows): the Windows launcher, the
client hooks DLL, the client patcher against a real client, and anything on a real server. These are the biggest risks and
steps 1 and 3 below exist to close them.

## 1. Client selection

**Recommendation: the Genesis client release of 21 Oct 2026, "2026.10.21.1" = publisher release 140076.**

Why: it is the release the live line's launcher runs today and the one the cloud session exported the game data from
(`data/` is gitignored; regenerate with `dotnet run --project src/Ro3Emu.Setup -c Release -- export <client folder> data`).
Upstream's README lists it as the current release it was tested with (7, 16 and 21 Oct), so both lines support it. The 20 Oct
release (140069) replaced `Assembly-CSharp.dll` and `Happyengine.Network.dll`; live already patches it (commit `786f273`,
sealed login callback) and the merged `ClientPatcher` keeps that **and** upstream's voice patches, but this combined patcher
has never run on a real client.

Do this on the owner's Windows machine (Claude there has the client and the memories of how releases work):

1. Make sure the game folder is 2026.10.21.1 (`RO3Emu.exe check` reports it; or the official launcher).
2. Check out `sync/upstream-2026-10-22`. **Rebuild `prebuilt/client`**: the hook sources changed on both sides, so the
   checked-in DLLs and `source.sha256` are stale and CI's `client-stamp.ps1` will refuse them:
   `pwsh tools/release/pack.ps1 -ClientDir "<game folder>" -UpdatePrebuiltClientDlls`, then commit `prebuilt/client`.
3. Patch a **copy** of the game folder for the staging address and log in (see step 2 for the address). Confirm: login,
   character select, enter the world, move, fight, open the skill, talent and wardrobe panels. If the 21 Oct client does not
   reach the server, stop and fall back to the client live runs today; do not ship a patcher you could not log in with.
4. In `src/Ro3Emu.Launcher/launcher.json` set `"maxGameRelease": 140076` so a later publisher release is not installed over
   players' folders until it has been tested (it is `0` = no limit in the checked-in file; the deployed launcher config may
   differ, so check what players actually use). Rebuild and publish the launcher through the owner's existing release process.
5. Players already on 2026.10.21.1 need no client change if the hooks DLL is delivered by the launcher's patch channel; check
   how the launcher delivers `prebuilt/client` before cutover (read `docs/guide/launcher.md`).

Record the decision and the evidence (which build logged in) at the end of this file.

## 2. Staging realm on the Finland server

What the cloud session knows: the game moved to a US East (Virginia) server on 21 Oct and the old address forwards to it;
Finland is the previous host. It does **not** know what is still running there, whether the forwarder or the release box and
automation live there, or its ports and firewall. **Look before you start anything**: `ss -ltnp`, the running services, the
firewall, and the owner's memories about the release box.

Plan, from the merge plan (step 3), with the safety points that matter:

- **Own ports, DataDir and name.** Do not reuse production's ports (5380 HTTP, 20000 micro gate, 33100 game gate); the
  forwarder may hold them. For example HTTP 5480, micro 20100, game 33200, `DataDir` `/srv/solro3-staging/data`,
  `ClusterName` "Sol RO3 Staging", `PublicHost` the Finland IP. Options are `Ro3Emu:<Name>` in config or `--Ro3Emu:<Name>=...`
  (`src/Ro3Emu.Server/ServerOptions.cs`). Copy the production house-rule options (F2P, access list, GM audit) so the test
  matches what players will get.
- **A copy of the production database, never production itself.** Take it from `data/backups/` on the US East server (or a
  fresh SQLite backup of `ro3emu.db`), copy it to Finland, and start staging from the copy. It holds players' accounts and
  password hashes: limit the staging ports to the testers' IPs, do not leave it open.
- **Same exported game data** as production (the `GameDataDir`).
- **Keep it away from production automation.** The AI bot runner, the Discord bot/automation (`Kafra`), announcements and the
  bug automation must keep pointing at production only. Staging posts nothing to Discord.
- **Build the staging binary from `sync/upstream-2026-10-22`**, with `dotnet publish -c Release` (.NET 10, see `global.json`).

Before going further, **prove the rollback** (merge plan step 9): after the new build has started cleanly on the copy and the
migrations ran (read the first-start log), stop it and start the **old** build (`aa9075c`) on the same migrated copy. It must
run. Upstream only adds columns and live's only new data is the Growth Classroom blob, so it should; if it does not, that
decides the rollback plan (restore the pre-release backup instead) and you tell the owner.

## 3. Test plan on staging (merge plan phase 3)

1. First-start log has no errors; `Growth Classroom` log lines show claims carried across for roles that had them.
2. **Economy check**: compare `EconomySnapshotReader` snapshots (`src/Ro3Emu.Server/Economy`; `tools/EconomyProbe` may be the
   command-line way, check its README) of the copy before the new build and after a bot session. No currency or item may
   appear from a migration.
3. **Bots**: `Ro3Emu.BotCli` and the AI runner on 20–40 characters across levels for a few hours; `Ro3Emu.LoadTest` at the
   realm's peak concurrency ×2. Compare CPU, memory and game-loop wait times with production (lag is the top item in
   [fix-priorities.md](fix-priorities.md)).
4. **Human pass** (the owner and 2–3 trusted players, one class line each) with this checklist:
   login and character select; each class's skills, normal attack on the bar, talents (job talents now do something:
   test Paladin, Sniper, High Priest, Assassin), cast speed; pets (hatch, S-class counter, room level); Growth Classroom
   (**no second claim of a rank**); stage rewards and Claim All; selection chests; Zeny and Crystal shops at F2P prices;
   Supporter Tokens; invite-only registration; Kafra and Butterfly Wing travel; MVP, dungeon (do bosses use skills now?),
   Spirit Tower, Phantom Realm, Rift; party EXP share; guild, trade, mail; Survivor Battle; the Dodo wheel; FLEE against
   Roda Frog; Mining level 3 unlocking Lv. 2 ore.
5. Season talents: a role with levels bought in several builds must show the shared levels and have received the one-time
   refund mail exactly once.

Fix what breaks on the staging branch (commit with a short imperative subject; **no tool attribution trailers**, per
`AGENTS.md`), rerun the affected tests, and push.

## 4. Cutover and the rules around it

Timeline and go/no-go:

- The merge plan wants a **24 h soak** and an announcement 24 h ahead, in an off-peak slot (2 am Central). The owner chose
  **10:00 Central**, which is also one of the automation's release slots (2/6/10 am and pm). If the cutover is Friday 10:00,
  the soak cannot happen; if Saturday, there is about a day. **Do not skip the checks to hit the time.** Cutover is
  go only if: the first-start migration ran clean, the rollback was proven, the bots and a human pass found no blocker, and a
  fresh backup was taken. If not, tell the owner and move to the next slot (the merge plan's off-peak 2 am slot, or whatever
  they choose). Say so plainly; they decide.
- **Hold the automation** from now until the cutover is done (`/admin` or `!off` in Discord) so it does not stage a release in
  between; tell GPT Astra/Codex the same. Nothing is merged into `v1.0.0-fixes` before the cutover.
- **Announce** in `#announcements` as soon as the owner agrees to a time (channel `1561168146201583656`, `@everyone`, same style as
  the earlier posts): a longer restart, what is new (job talents, boss skills, pet skills, Rift later), and the time. A draft:
  "Sol RO3 is getting a big update at 10:00 Central: job talents and boss skills now work, plus many fixes. Expect a restart of
  about 15 minutes. Your progress is kept." Adjust to what the staging pass actually showed works.
- **Cutover**: fresh database backup, then merge `sync/upstream-2026-10-22` into `v1.0.0-fixes` (a merge, not a rebase, so the
  automation's and Codex's branches keep a valid base), then release through the owner's normal process with a **15-minute**
  countdown and the old binaries kept on the box. Watch the first hour: log errors, reconnects, game-loop wait times,
  kills per minute (the automation's "Kills 822 → 453" line), and `#bug-reports`.
- **Rollback rules**, decided in advance:
  - Startup migration fails, or players cannot log in, enter the world or fight: roll back at once to the old binaries (no
    database restore needed, proven in step 2).
  - Data loss or duplication (items, currencies, claims): stop the server, restore the pre-release backup, start the old
    build, and post what happened.
  - Anything else: fix forward in the next release.
- **After**: turn the automation back on with the new base; recreate or rebase open fix branches (`fix/new-report-followup` is
  Codex's); post patch notes in the usual format. Then start the weekly upstream syncs ([upstream-merge-plan.md](upstream-merge-plan.md), "Staying close").

## Things to know

- **How to work with the release automation**: it ships `v1.0.0-fixes` every 4 hours from 2 am Central, posts the countdown,
  patch notes and a `news.ts` snippet for the website. When writing a PR for it, put the **News draft above the Patch notes
  draft and end the body with nothing after it**: it copies a PR section to the end of the description, and the last ship
  (casefix.32) put a Claude Code footer into the website news text. The owner has to cut those two endings from the
  `news.ts` snippet in `#dev-log` before pushing it.
- **Discord**: the bot is "Kafra"; the token is in the environment, never print or commit it. Bug-report forum is
  `1561168195371540560`. Threads waiting on players or on this merge (replies already posted): Friend Chat blank for the
  sender (1562116616706326588), friends delete option (1562822508116185130: it is on the player card), Moon Maiden
  (1562877918970126488), Strange Occurrences (1561894617169928332), the Basic Dagger (1562649170454773820: a GM test weapon,
  ask the owner whether to strip it at login), Louie's game bells (1562506218398949396), auto-battle pathing for ranged
  (1562536643934556242). After the cutover, reply in the threads that the merge fixes: job talents (1562492910761549897),
  dungeon boss mechanics (1562828245211021332, 1562828761823449220, 1562829622201032825, 1562495249249476658) and Rift
  (1562859632735817738), **after checking on staging that they really work.**
- **Building here**: `.NET 10` at `~/.dotnet`; `data/` must be exported for `[ClientDataFact]` tests. The Windows launcher
  and `Ro3Emu.ClientHooks` only build on Windows with the client's DLLs.
- **Don't** commit `data/`, tokens, logs with personal data or working notes (`AGENTS.md`).

## Decisions for the owner (open)

1. Which day is "tomorrow" for the cutover, and is 10:00 acceptable without the 24 h soak (see above)?
2. Remove the developer test weapon "Basic Dagger" (item 90000102) from characters that are not GMs?
3. Add a setting to turn AI chat off in world and private chat (suggestion thread 1562821188101869578)?

## Record (fill in as you go)

- Client chosen / evidence:
- Finland staging address, ports, who has access:
- Rollback proven (date, build):
- Bots / load-test results:
- Human pass result:
- Cutover go/no-go and time:
