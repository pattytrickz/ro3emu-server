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
4. **Cut over at 10:00 Central on Saturday 24 Oct 2026** (owner's decision on Friday morning: moved from Fri 10:00). If staging, the
   rollback proof and the human pass are done early, the owner is happy to go **earlier the same night** (Fri 10 pm or Sat 2 am
   release slot), but only if the go/no-go below is met and the announcement has been up for a few hours. Ask them before
   moving it.

## State of the code

| | |
|---|---|
| Live line | `origin/v1.0.0-fixes` at `aa9075c` (includes PRs #60, #61, #62; all shipped, #62 as 1.0.0-casefix.33 at 10 pm Thursday) |
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

- The merge plan wants a **24 h soak** and an announcement 24 h ahead, in an off-peak slot (2 am Central). The cutover is
  **Saturday 10:00 Central** (one of the automation's release slots: 2/6/10 am and pm), which leaves about a day for staging
  on Friday. An earlier cutover the same night is allowed only if everything below is green. **Do not skip the checks to hit
  a time.** Cutover is go only if: the first-start migration ran clean, the rollback was proven, the bots and a human pass found
  no blocker, and a fresh backup was taken. If not, tell the owner and move to the next slot; they decide.
- **The automation stays on until the cutover** (the owner still has bug fixes to ship through it, every 4 hours). That means
  `v1.0.0-fixes` keeps moving while you test, so: (1) every fix the automation or Codex ships must also reach the staging branch
  before cutover: `git fetch origin && git merge origin/v1.0.0-fixes` into `sync/upstream-2026-10-22`, resolve (rerere is
  on locally only), rebuild, rerun the affected tests, and repeat once more right before cutover; (2) the staging database
  copy ages, which is fine for the migration test but retake the production backup just before cutover; (3) **freeze the
  automation only for the cutover window**: turn it off (`/admin` or `!off`) about an hour before the cutover slot so it
  cannot stage another release in between, tell GPT Astra/Codex the same, and turn it back on after. Nothing from the
  staging branch is merged into `v1.0.0-fixes` before the cutover: a merge there ships to production at the next slot.
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

1. Saturday 10:00 is the plan; do they want to go earlier (Fri night) if staging finishes early? (Their answer so far: yes, if it is done.)
2. Remove the developer test weapon "Basic Dagger" (item 90000102) from characters that are not GMs?
3. Add a setting to turn AI chat off in world and private chat (suggestion thread 1562821188101869578)?

## Record (fill in as you go)

- **Client chosen / evidence (23 Oct, local session):** keep the releases players already have; do **not** move to
  140076. Correction to an earlier note: the deployed Sol launcher's `maxGameRelease` 140072 does not put everyone on
  140072. The launcher can only install the publisher's newest release, so while that is above the ceiling (140078 on
  23 Oct) it leaves the game where it is ("Release 140078 isn't supported by Sol RO3 yet, so the game stays on release
  140065"). Players run 140065 to 140072. The merged patcher and hooks, built against the owner's 140065 client
  (`RO3Emu.exe check`: "patch targets and every hook reference found"), fit it, and the patched client reached staging. The staging join tool
  is `G:\RO3Emu-staging\RO3Emu.exe` (hooks/loc built from this branch against that client's original DLLs).
  `prebuilt/client` is **not** rebuilt yet: `pack.ps1` needs PowerShell 7, which this PC lacks; the Sol launcher build
  compiles the hooks from source anyway. Not yet proven: a real login (human pass below).
- **Finland staging address, ports, who has access:** `95.217.239.53`, HTTP **5480**, micro **20100**, game **33200**;
  ufw allows only the owner's IP (24.78.173.215). systemd `ro3emu-staging` (user `ro3staging`, MemoryMax 3G,
  CPUQuota 200%, Restart=no), binary `/srv/solro3-staging/current` -> `bin-new` (this branch) or `bin-old` (live
  `aa9075c`). DataDir `/srv/solro3-staging/data`: a `VACUUM INTO` copy of production backup `ro3emu-20261023-091006`
  (4.5 GB, sha checked). GameDataDir is production's export (fingerprint 2d94b90d…, same as Ashburn). House-rule options
  copied from production except `MinLauncherVersion` (so the join tool can log in). New option
  `Ro3Emu:DatabaseBackups=false` (commit "Add a switch to turn off database backups for a test realm"): the disk
  (94% used) cannot hold a second copy. The untouched pre-migration copy stays on Ashburn at `/root/staging/staging.db`.
- **Rollback proven (23 Oct 10:51-10:54 UTC):** the new build started clean on the copy (no warnings besides the backup
  switch), 20 AI bots logged in and fought; then the **old** build `aa9075c` started on the same migrated copy
  (backups dir made read-only so its startup backup fails harmlessly): 20 logins, 73 kills in 3 minutes, no errors
  besides that backup. Back on the new build afterwards. So a failed cutover can roll back to the old binary without a
  database restore. The Growth Classroom migration logs only for roles with old claims; the bots have none, so it is
  checked at the human pass.
- **Tests (local Windows, this branch + the backup switch, data exported 20 Oct):** 2,098 tests, 2,080 passed, 13 failed.
  Rerun alone, 10 pass (timing), `CardCrashRecoveryTests.ProcessCrash…` fails only on Windows (the crash child keeps
  `ro3emu.db-shm` open), `SkillCombatTests.NotInterruptSkill…` is the known baseline, and
  **`FirstHeartTests.SyncStartsAtItsOpenLevel…` fails: a First Heart claim below the open level is accepted (0, expected
  ActivityClosed 3902)**. It passed in the cloud on 22 Oct. **Explained (data, not code):** the test assumes First Heart
  opens at Base 30 (LC_ConditionList 21000012 in the 21 Oct client the cloud exported), but the export players' clients
  match (140065 to 140072) has 21000012 = Base **10**, so its "Base 29 is still closed" step fails. The server reads the
  condition from the data, so live players see First Heart from Base 10, as their client does. `QuestWalkTests.ChapterOneCanBeWalkedToTheEnd` failed after 7.5 minutes in
  the full run and passes alone (7 min 5 s): timing under the full suite's load.
- **Bots / load-test results:** a second AI runner (`/home/solro3/aibots-staging`, tmux `aibots-staging`, 20 bots, LLM
  chat off) plays on staging. Load test not run yet.
- **Live fixes on the merged line (23 Oct, owner asked for a bot pass instead of a human one):**
  - Every test the live PRs #6 to #62 added (108) passes on this branch, and so do upstream's own tests for the areas
    where upstream's code replaced live's (Double Attack, choice boxes, boss-clear quest targets). Three live tests were
    dropped in the merge. Each was restored locally and run (not committed):
    - the selection chest test passes for an eligible role. Upstream now enforces ItemConfig `_iLevelNeed` and
      `_kJobNeed` (exact career, like the client's `LC_ItemHelper.CanUseByInfo`). The 10 job-limited boxes are held by no
      real player on the copy;
    - the side-guide "boss win" now counts a dungeon clear, not a field kill of the boss (upstream's rule, its test passes);
    - the Double Attack test relied on live-only hooks; upstream's test covers the Dagger hit and the Katar CRIT.
  - A wire-level runner (`.loadtest-temp/StagingCheck`, not committed; on Finland `/srv/solro3-staging/check`, run with
    the staging bots stopped because it uses their roster) passed 26/26 on staging:
    - all 20 migrated bot characters log in and enter the world;
    - Zeny Shop potions and fire arrows, and the arrow switch;
    - selection chest (an invalid pick is refused, a valid pick pays) and the Old Card Album gift box;
    - Dodo's wheel (one turn spends a gem and names its slot);
    - Growth Classroom: a role seeded with live-style claims [1,2] logged "keeps its claimed ranks 1,2", and both
      claims answered 2062 (already claimed).
  - EconomyProbe compare (opening sequence 966141 to 966854): verdict Fail, all from history the old production build
    wrote:
    - 598,772 monster-kill receipts before seq 732352 have no outcome payload;
    - 247 cd13e daily-stage and quest-award receipts are older than the opening;
    - 489 Coverage items are on families the probe lists as uncovered (newactivity, monster-kill progress).
    No Corruption issue comes from a receipt written on staging. Summary at `/srv/solro3-staging/compare-summary.md`.
- **Human pass result:** the owner's 140065 client was patched with the join tool and fetched the staging gate key; the
  owner asked for the checks to be done with bots instead (above).
- **Cutover go/no-go and time:** the owner chose **Sat 24 Oct 10:00 Central (15:00 UTC)**. Announced in `#announcements`
  on 23 Oct (message 1563158477654859851).
