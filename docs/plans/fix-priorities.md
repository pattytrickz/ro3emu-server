# Sol RO3: what to fix next

*Scan of 22 Oct 2026, 2 pm Central: 85 `#bug-reports` threads, 921 `#general` messages (19–22 Oct), `#suggestions` and
`#help`. Companion plans: [strategy](balance-retention-strategy.md) · [events](events-plan.md) · [roadmap](roadmap.md).*

## How this is ranked

- **P0:** hurts everyone online or the server's safety.
- **P1:** blocks progression, or is a core combat or balance bug that several players report.
- **P2:** a broken system with a workaround, or one player's report.
- **P3:** polish and suggestions.

Within a tier, items with more reporters and less effort come first. "Who" says who is already on it, so nobody
duplicates work. **Codex** is GPT Astra (dev-log: "Local Codex is handling…"); **automation** is the bug-fix AI on
the release box.

## The one strategic item: catch up with upstream

Upstream `aethai/ro3emu-server` is **186 commits ahead of `v1.0.0-fixes`**, and it already implements several things
players report as broken here:

| Upstream (17–22 Oct) | Open reports it addresses |
|---|---|
| Talents executed: skill-tree passives, job and profession talent procs (Thief, Merchant, Priest, Mage, Archer lines), talent-gated buffs | "Job Talents don't work", Double Attack feels useless, "Envenom/Deadly Poison triggers?", Sonic Blow low-HP bonus |
| Boss special skills, bosses resist controls, born buffs for every monster | "Dungeon GTB / Orc Hero / Drake have 0 mechanics", "MVP in the dungeons doesn't have mechanics" |
| Pet guardian skills and reactive effects | "Pet skill (T key) does not work in the field" |
| Mount passives and bind buffs | (not yet reported) |
| Security and data-safety fixes from an external audit; refusing messages sent before their stage or account | Not reported by players, but **a public server needs these** |
| 20 and 21 Oct client support | Players on the newest Genesis client |
| Party voice and guild voice | "Guild voice" in the README's "Not yet" list |

**The catch:** upstream also renamed private fields across the codebase (`_camelCase`) and changed many core files,
and the live branch has about 110 commits of its own. A plain merge will conflict heavily; the 16-commit subset in
`main` alone conflicted in 18 files. **Recommendation:** treat it as a project on its own branch, done by someone
with the exported game data so the ~950 data tests can run:
1. Branch from upstream `main`.
2. Re-apply the live branch's fixes (most are small and self-contained) on top, dropping the ones upstream already
   fixed its own way (Normal Attack on the bar, multi-hit skills, cast speed and others).
3. Run the full data-backed suite, then replay a test realm copy before switching live.

Until that lands, every combat fix made on `v1.0.0-fixes` risks being redone. So for combat and talents, prefer porting
the upstream change over writing a new one.

---

## P0: everyone or safety

| # | Issue | Evidence | Who | Suggested approach | Effort |
|---|---|---|---|---|---|
| 1 | **Lag spikes, "reconnecting", Internal Server Error** (Culvert 2, Sograt, Ant Hell; 1–2 s spikes) | [thread](https://discord.com/channels/1528778612570980403/1562189303432351825); `#general` almost daily from 5+ players (10-20 → 10-22); dev-log: "logs show character-lock timeouts" | automation (observing) | Profile the game loop on the live box during a spike: which reservations wait (character lock timeouts). Rule out the AI bots' load (10–20 bots log in as real clients). Compare with upstream's "Read sessions for HTTP endpoints where the loop changes them" and audit fixes | M–L |
| 2 | **Upstream security and audit fixes missing** | Upstream commits of 18 and 21 Oct ("Security fixes from an external audit", "Refuse game messages sent before their stage", "Refuse micro calls before an account is bound") | nobody | Port these first, ahead of the full sync; they are small and self-contained | M |
| 3 | Launcher stuck on "checking update" | `#general` 10-22 (1 player) | nobody | Ask for their launcher version and log; compare with the 1.2.5 self-update fix | S |

## P1: progression blockers

| # | Issue | Evidence | Who | Suggested approach | Effort |
|---|---|---|---|---|---|
| 4 | **Cannot advance to 3rd job** ("press it and nothing happens") | `#general` 10-22 (bone_jawey, twice); dev-log: Codex "verifying the Assassin Cross → Guillotine Cross path" | Codex (checking) | Confirm Codex owns it; then trace the job-change quest/NPC for 3rd jobs | M |
| 5 | **Dodo Pink Gem prize wheel hard-locks the game** (Alt+F4 needed, gem consumed) | [thread](https://discord.com/channels/1528778612570980403/1562269417390342215) (3 players) + [duplicate](https://discord.com/channels/1528778612570980403/1562745099341922384); repro given: "Skip Animation" + "Start 1 Times" | nobody | The reply to the spin is missing or malformed; find the Dodo wheel handler (lottery) and answer the 1-spin path. Fully reproducible, so a good next fix | S–M |
| 6 | **Life skills: level-2 resources never unlock** (Mining, Gardener) | [mining](https://discord.com/channels/1528778612570980403/1562742903820787722) (3 players), [gardener](https://discord.com/channels/1528778612570980403/1562516403368501249) | automation (answered "Learn/Unlock") | The player says Unlock only shows the gathering area. Check what the panel sends on Unlock, and whether the level-2 recipe/area unlock is granted | M |
| 7 | Main quest "Strange Occurrences" cutscene freezes | [thread](https://discord.com/channels/1528778612570980403/1561894617169928332) (video attached) | automation (asked for info) | Watch the video; compare with the scripted-scene path | M |
| 8 | Spirit Tower daily To-Do never completes | Known-issues list; `#general` 10-20 | nobody | Daily objective credit for a tower clear | S |
| 9 | Commendation Vault: Challenge does not warp the player in | Known-issues list | nobody | Trace 30400 from the vault NPC (PR #60 fixed only its time unit) | S–M |
| 10 | Rift: cannot enter (0/10) | [thread](https://discord.com/channels/1528778612570980403/1562859632735817738); `#general` 10-22 | Claude (asked) | Rift Crisis exists only upstream: part of the upstream sync | (sync) |

## P1: combat and balance (many reports, core feel)

| # | Issue | Evidence | Who | Suggested approach | Effort |
|---|---|---|---|---|---|
| 11 | **SP: skills consume no SP and show no SP cost** ("mana is infinite") | Known-issues list; `#general` 10-19 (2 players), 10-21 | nobody | The server pays `_kCost` (CombatService.Casting); find why skill rows carry no cost: wrong skill-level row, or a cost table the client reads. **Big balance impact**: free SP makes every class spam its best skill | M |
| 12 | **CRIT, FLEE and Katar crit feel wrong** ("163 FLEE, every attack hits"; "34 CRIT feels like 1/4 or 1/5"; Katar adds no CRIT) | [FLEE](https://discord.com/channels/1528778612570980403/1562901801915916329), [Katar](https://discord.com/channels/1528778612570980403/1562747879662288960); `#general` 10-22 (3 players) | nobody | The hit and crit formulas in `CombatService.Damage` are *(inferred)*: dodge = FLEE/(FLEE+600), crit = CRI/10000 × skill crit. Check them against the client's formula params (`LC_DamageHelper`, PublicParamConfig) and whether monster normal attacks carry `_iSkillHit` 10000 (never miss). One audit covers all three reports | M |
| 13 | Talents and skill effects do nothing (Job Talents, Envenom, Deadly Poison, Magnus Exorcismus, Double Attack 1 %/level) | [Job Talents](https://discord.com/channels/1528778612570980403/1562492910761549897); `#general` 10-20, 10-22; `#suggestions` (Double Attack) | nobody | **Port from upstream** (talent and passive execution, 20–22 Oct) rather than rewrite | L (port) |
| 14 | Skills hit for less than normal attacks (Bash ≈ auto attack) | `#general` 10-20, 10-21 (3 players) | nobody | Part of the #12 audit, with the skill ratio rows | (with 12) |
| 15 | **Mage bolts have no animation again**, and a small freeze after each cast in auto combat | [no animation](https://discord.com/channels/1528778612570980403/1562866663521525851), [freeze](https://discord.com/channels/1528778612570980403/1562867774399709304) (today, same player) | nobody | Likely a regression from today's combat merges (skill bullets 31120 or heal show-point change #59). Bisect between 10 pm and 2 pm releases | S–M |
| 16 | Basic Dagger has huge stats | [thread](https://discord.com/channels/1528778612570980403/1562649170454773820) (screenshot) | automation (investigating) | Look at the screenshot's numbers against ItemConfig; likely a refine or affix applied to the starter item | S |
| 17 | Monster EXP: higher-level monsters give little EXP; Ant Hell dropped from 300–600 to ~160 | `#general` 10-20, 10-22 (4 players) | owner decision | Ties to the pacing plan (strategy §4.1): check `BonusExpAlways` still applies in caves after today's merges, then decide rates deliberately | S |

## P2: broken systems with a workaround

| # | Issue | Evidence | Who | Suggested approach | Effort |
|---|---|---|---|---|---|
| 18 | Auto-battle: ranged characters stand still behind obstacles | [thread](https://discord.com/channels/1528778612570980403/1562536643934556242) | nobody | Client-side auto-battle pathing; check what the server answers when the target is unreachable | M |
| 19 | Friend list: no remove option; header shows 3/10 with 5 listed | [thread](https://discord.com/channels/1528778612570980403/1562822508116185130) | Claude (asked) | Server supports remove; likely the relation type the friends are stored with | S–M |
| 20 | Friend Chat blank for the sender | [thread](https://discord.com/channels/1528778612570980403/1562116616706326588) | automation | Echo of the sender's own message | S |
| 21 | Guild Caravan only gives Oridecon after the first submit | [thread](https://discord.com/channels/1528778612570980403/1562481298671407215) | Claude (asked) | Check `_kAward[round]` groups for rounds after the first | S |
| 22 | Crafted T4 gear shows Stunts "N/A" (boots, gloves, katars) | [thread](https://discord.com/channels/1528778612570980403/1562476589973901352) | automation | Stunt (bonus effect) roll on craft for those parts | M |
| 23 | Menu clicks restart the music; cutscenes have no sound | [music](https://discord.com/channels/1528778612570980403/1562842002343862386), [cutscenes](https://discord.com/channels/1528778612570980403/1562788691263619212) | automation | Likely a server message that resets the scene BGM; needs a repeatable sequence | M |
| 24 | Louie's game: bells not shuffled, Fervor doesn't sniff | [thread](https://discord.com/channels/1528778612570980403/1562506218398949396) | automation | Scripted minigame presentation | M |
| 25 | Bonus Time can't be enabled: "you are in a dungeon" | Known-issues list; `#general` 10-22 | nobody | Intended in caves (they already pay Bonus Time with `BonusExpAlways`); explain in-game, or allow it | S |
| 26 | AI players: right-click gives no party option; most already in a party; count cut to 10 | Known-issues list; `#general` 10-21, 10-22 | owner | Restore the bot count (owner said "later today"); keep some bots unpartied for players | S |
| 27 | Skill dragged onto the hotbar doesn't show until you change screens | Known-issues list; `#general` | nobody | Missing bar sync after 31xxx bar set | S |
| 28 | Region error "Failed to connect to the region" | [thread](https://discord.com/channels/1528778612570980403/1562248476568330402) (resolved after a restart) | automation | Close if not seen since the US move | S |

## P3: suggestions worth doing

| # | Suggestion | Source | Recommendation |
|---|---|---|---|
| 29 | Optional auto-skip for dialogue and cutscenes (alts replay the same story) | `#suggestions` 10-22 | **Do it** as a client-hook setting; pairs with Fresh Start Week (events plan) |
| 30 | Remove the "Never share your credentials… UID" overlay | [thread](https://discord.com/channels/1528778612570980403/1562823436290621491) | Do it: a client-hook text change |
| 31 | Remove AI chat in PMs and World | [thread](https://discord.com/channels/1528778612570980403/1562821188101869578) | Partly: bots shouldn't talk in World; keep whisper replies only when the player wrote first (strategy §3.9) |
| 32 | Thief Double Attack: 1 %/level feels useless (RO1 was 5 %/level) | `#suggestions` 10-20 | Port upstream's talent procs first (#13), then tune |
| 33 | Better ping for Asia and EU | `#suggestions` 10-19; `#general` | Later: one server keeps the community together; revisit when an EU/Asia group is big enough |
| 34 | Pets: an active ability that chains passives | `#general` 10-22 | Note for later; upstream's guardian skills come first |
| 35 | Faster sitting regen | `#suggestions` 10-19 | Done (`SitRegenMultiplier`); confirm the live value is set |

## Already fixed, shipping

- 2 pm release (casefix.31): Tension Relax, duplicate chat, Heal hit sound.
- PR #60 (next release): selection chests, Fashion Sense Claim All, pet S-class guarantee and hatchery level, duplicate pet pop-up, Southern Payon Kafra arrival.
- In progress by Codex: portrait frames, party reconnect chat replay.

## Suggested order for the next three releases

1. **6 pm:** PR #60; the Dodo wheel hard lock (#5); the mage bolt regression (#15, if bisectable).
2. **10 pm:** the security and audit ports (#2); the Spirit Tower daily (#8); the dialogue skip and overlay text (#29, #30).
3. **Tomorrow:** the SP cost investigation (#11) and the hit/crit formula audit (#12); start the upstream-sync branch.
