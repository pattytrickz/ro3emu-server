# Sol RO3: merging upstream (aethai) safely

*22 Oct 2026. How to bring `aethai/ro3emu-server` `main` into the live line (`v1.0.0-fixes`) without breaking the
realm, its database or its players' clients. Companion: [fix priorities](fix-priorities.md) · [roadmap](roadmap.md) 1.10.*

## Where the two lines stand

| | |
|---|---|
| Common ancestor | `3eced0c` (17 Oct, "count craft quests by the crafted item's equip part") |
| Upstream only | 128 commits (198 with merges); newest `9f3c04d`, 22 Oct |
| Live only | 84 commits (bug automation, owner, Codex, Claude) |
| Trial merge of upstream into live | **53 files, 134 conflict hunks**: 34 files in `Game/` (most in `CombatService*`, `SkillService`, `SeasonTalentService`, `PetService.Incubation`, `DailyActivityService`), 8 test files, plus `SdkEndpoints`, `GateListener`, `ServerHost`, `ClientHooks/LuaPatches`, `ClientTools/ClientPatcher`, the launcher project |
| Database | Both lines only **add** to the schema (new role BLOB columns upstream, `receipt_compaction` live). Forward migration is safe. One **data** migration is needed (Growth Classroom, below) |

### What upstream brings

Talent and skill-tree passive execution (job and profession talent procs for every line), boss special skills and
control resistance, born buffs for all monsters, pet guardian skills, mount passives, Rift Crisis, Seven-Day Goal,
Codex unlocks, profile likes, item actions and award boxes (including SelfChooseGift 303), party and guild voice,
fixes from an external **security and data-safety audit**, and support for the 20 and 21 Oct Genesis clients.

### Live commits, sorted

| Group | Commits | Rule when resolving |
|---|---|---|
| **A. Already upstream** (identical patches; your upstream PRs #6–#8) | `0903080` Linux data-file case, `cd3e3ae` AutoNormalAttack, `58e2c56` Join English text, `22b5e53` hatch null check | Nothing to do: git drops them |
| **B. Ported from upstream**, upstream has moved on | `1816a4f` `92587fb` pet hatchery, `3e0585c` stage rewards, `5f42fce` vault time | **Take upstream's version** of the code; keep live-only tests only if they still pass |
| **C. Fixed on both sides, differently** (verify each) | Normal attack on the bar (`32d543e` `2796cfd` `7672251` `48a00ff`), multi-hit display (`7f3b935` vs upstream "Split a skill hit"), job and season talents (`9b84716` `9fe9fd3` `49196bd` `6bba547`), Sonic Blow (`32d543e`), auto-attack loop (`17c047d` `5e008fc` `cea805c` `d90e6ae` `ebe79d0`), cast speed / Aura Blade / Shield Reflect / Tension Relax (`5bacd8c` `7e5b804` `be4376b`), gift and selection boxes (`e821e26` `8a7d82a` vs upstream `ItemBoxService`), Growth Classroom (`2ab71b1` `1cc541b` `aae21ee` `cb27091` vs upstream `GrowthClassService`) | **Prefer upstream's implementation** (it carries its own data-backed tests). **Keep the live regression test** for each player report and make it pass on the merged code. Only re-apply live code when its test fails on upstream's |
| **D. Live-only features**: keep | House rules (`e4c714c` level-up refill, `68fa522` F2P + GM audit, `b975e09` `d6fedc7` access list + Supporter Tokens, `cd32673` `0847169` Bonus EXP, `20c8f17` sitting, `e445a74` Zeny Shop rows), AI players (`49e0a82` `63596fc` `0f62901` `fb3be1e` `7ce7425`), Sol launcher (`52680a4` `b968fd5` `228fe91` `c3b32a9` `9c65d92`), announcements (`09f6906`), receipt compaction (`ae3dd68`), gate wait timeouts (`6553a11`), party EXP share (`6fbeb2e`), Kafra arrivals (`b32fdbc`), registration and ping (`759b8ad`) | **Keep live**, adapting to upstream's renames and APIs. These are the realm's identity; none may silently disappear |
| **E. Live bug fixes upstream lacks** | Story waves, Louie's game, reward Vigor, award base EXP and pool picks, card skills, Payon NPC, Fire Wall, headgear cards, GM login spelling, Sight reset, free skill level, treasure maps, Spirit Tower retry, Phantom Realm, Rescue Bobo, chat history, Heal sound, quest auto-battle, side quests, scene-teleport checks | **Keep**, and offer each upstream as a PR afterwards (see "Staying close") |

## The plan

### Phase 1: prepare (no effect on players)

1. **Security first, separately.** Port upstream's audit commits (18 and 21 Oct: "Security fixes from an external
   audit", "Data-safety fixes", "Refuse game messages sent before their stage", "Refuse micro calls before an account
   is bound") as their own small PR and release. They should not wait for the big merge.
2. **Freeze point.** Tag the live head `pre-upstream-sync`. Note the deployed build (`1.0.0-casefix.N`) and keep its
   binaries on the box.
3. **A staging realm on the release box**, beside production: its own `DataDir`, ports and `ClusterName` ("Sol RO3
   Staging"), the same exported game data, **a copy of the production database** taken from `data/backups/`. Players
   never see it; testers join with `RO3Emu.exe join <box>:<staging port>`. Keep the AI bot runner pointed at production.
4. **Pick the client.** Upstream's `prebuilt/client` targets the 20/21 Oct Genesis client; the Sol launcher pins the
   publisher release the patch supports (`b968fd5`). Decide which client version the merged build supports, and
   rebuild `prebuilt/client` with `pack.ps1 -UpdatePrebuiltClientDlls` on a machine with that client. Testers and
   players must be on the same client release as the server's hooks.

### Phase 2: build the merge (a branch, never production)

5. Branch **`sync/upstream-<date>`** from `v1.0.0-fixes` and **merge** `upstream/main` into it (no rebase: the
   automation's and Codex's branches keep a valid base). Turn on `git rerere` so repeated resolutions stay identical.
6. Resolve the 134 hunks with the rules in the table above, **one area per commit** (pets, stage rewards, talents,
   combat, Growth Classroom, gate/HTTP, launcher, client hooks), so a bad resolution can be found and redone alone.
   Hardest first: `CombatService*`, `SkillService`, `SeasonTalentService`, `BuffService`.
7. **Data migrations**, each idempotent, run at role login, with a test:
   - **Growth Classroom**: live keeps claimed ranks in the task runtime state (`TaskRuntime.GrowthRanksClaimed`);
     upstream reads the role column `growth_class`. Copy the claims across at first login, or every player can claim
     their rank rewards a second time.
   - **Season talents**: live `6bba547` changed how bought levels are stored and mails refunds. Check upstream's
     talent storage reads the same state, so nobody is refunded twice or loses levels.
   - Any other state both lines store differently, found while resolving (pets: one egg per round per slot came from
     upstream already).
8. **Build and test on the box with game data** (`dotnet test tests/Ro3Emu.Tests -c Release`, Slow included). Gate:
   - 0 failures, apart from a written list of known baseline failures from before the merge;
   - every live regression test (one per player report) kept and passing;
   - the snapshot tests (`dispatcher-admission`, economy pins) updated on purpose, never just regenerated.

### Phase 3: prove it on staging

9. **Start staging on the production copy.** The migrations must run cleanly; read the first-start log for errors.
   Then **check rollback works**: start the *old* build on the migrated copy and confirm it still runs.
10. **Bots:** let `Ro3Emu.BotCli`/the AI runner log in 20–40 characters across levels for a few hours; run
    `Ro3Emu.LoadTest` at the realm's peak concurrency ×2. Compare CPU, memory and the game-loop wait times with
    production (the lag spikes, [fix priorities](fix-priorities.md) P0).
11. **A human test pass** (owner plus 2–3 trusted players, one class line each), using a checklist built from the
    player reports: login and character select; each class's skills, normal attack on the bar, talents, cast speed;
    pets (hatch, the S-class counter, the room level); Growth Classroom (**no second claim**); stage rewards;
    selection chests; Zeny and Crystal shops at F2P prices; Supporter Tokens; invite-only registration; Kafra and
    Butterfly Wing travel; MVP, dungeon, Spirit Tower, Phantom Realm; party EXP share; guild, trade and mail;
    Survivor Battle with the small-population sizes; launcher update to the chosen client.
12. **Economy check:** compare the economy snapshot (`EconomySnapshotReader`) of the copy before and after a play
    session: no currency or item appears from a migration.
13. **Soak for 24 hours** on staging with the bots before scheduling the cutover.

### Phase 4: cut over

14. **Announce 24 hours ahead** (`#announcements`): a longer restart, what's new (talents, boss skills, pet skills…),
    and that the release time is off-peak.
15. **Hold the automation** (`!off` / `/admin`) from the freeze until the cutover is done, so it doesn't stage
    another release in between. Tell Codex the same.
16. Merge `sync/upstream-<date>` into `v1.0.0-fixes` and release in an **off-peak slot** (the 2 am Central release):
    a fresh database backup just before, a 15-minute countdown instead of 5, and the old binaries kept.
17. **Watch the first hour:** errors in the log, reconnects, the game-loop wait times, kills per minute (the
    automation's "Kills 822 → 453" line), and `#bug-reports`.
18. **Rollback rules**, decided in advance:
    - Startup migration fails, or players can't log in, enter the world or fight: roll back at once to the old
      binaries (proven to run on the migrated database in step 9). No database restore needed.
    - Data loss or duplication (items, currencies, claims): stop the server, restore the pre-release backup, start
      the old build, and post what happened. The time between the backup and the stop is lost, so keep it short.
    - Anything else: fix forward in the next release.

### Phase 5: after the cutover

19. Turn the automation back on with the new base, and rebase or recreate any open fix branches.
20. Post the patch notes in the usual format, grouped by what players will notice.

## Staying close afterwards

- **Small and often:** merge upstream into `v1.0.0-fixes` **every week** (each merge is small then), through the same
  staging pass but shorter.
- **Send live fixes upstream:** you already did this for upstream PRs #6–#8, and those commits now disappear from the
  merge. Offer group E as upstream PRs (house rules stay here: they're realm policy, not emulator behavior). Every
  fix upstream accepts is one fewer conflict next time.
- **New combat and talent work starts from upstream's code**, ported down, not written fresh on the live line.

## Effort

About **3–5 working days**: 1–2 resolving and migrations, 1 testing on the box, 1 staging and the human pass, plus the
24-hour soak.
