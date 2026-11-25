# Commits to make

Pascal commits; the agents never do (the hook blocks it). The checkpoints below record the plan's "Checkpoint (Pascal commits)" lines. The recipe for building the commits is under "Milestone commits" at the end.

## M0 checkpoints

Branch `fc/m0-setup`, merged as PR #36 (`1a69d9c`).

- [x] Task 1: `chore: raise CMS floor to 18.3.0 and Forms to 18.2.2, add design, plan and a local test site` (the existing demo site is the plan's "TestSite", ruling R1; no new site)
- [x] Tasks 2 to 5: `docs: record M0 spike results` (S1 to S12 in `docs/spikes.md`, short table at the end of `docs/plan.md`)
- [x] M0 stop: full suite 57/57, 0 warnings, check-vulnerable clean, gitleaks clean on the files to be committed. Each spike's results had a qa-reviewer read; every Important finding fixed and re-checked.

## M1 checkpoints

Branch `fc/m1-schema`. Pascal asked (2026-11-24) to keep building past M1 without stopping, so later milestones sit in the same working tree. Each milestone's end state is saved as a git tree object.

- [x] Task 6: `chore: pin vulnerable transitive packages, raise the client floor and add Forms contract tests`
- [x] Task 6a: `ci: add a vulnerable-package gate, npm audit and supply-chain hardening`
- [x] Task 7: `feat: add EntryAudit options with validation`
- [x] Task 8: `feat: add entry access tables and migrations for SQLite and SQL Server` (plus `release-checks.yml`, a manual workflow for the SQL Server tests)
- [x] Task 9: `feat: add access retention, edit value expiry and erasure reconciliation to the daily job`
- M1 end tree: `93900d7` before the milestone fix wave; `9abb3cc` after it (this one also contains Task 10, the first M2 task, because work was pipelined).

## M2 to M5 checkpoints

- [x] M2 (Tasks 10 to 12b plus the M2 fix wave): tree `615e36d`. This tree also contains Tasks 13 and 13b (built on `5477905`).
- [x] M3, M4 and M5 (Tasks 13 to 19 docs, merged together from parallel worktrees, plus all fix waves): tree `a96f87c` is "all milestones merged". M3, M4 and M5 are one commit because they were merged together and have no separate trees.
- [x] After the final fix wave, E2E pass 2, R77 and full verification: tree `65d97f3`.
- [ ] Final: `65d97f3` plus the edit-overhead fix (`RequestFormCache`), the SQLite create-hang fix, the Entry access link fix, the Task 18b specs with their demo support, the README SQLite and SQL Server notes, the final `HANDOVER.md` and this file. This file is part of that tree, so its hash cannot be written here: compute it with `FINAL_TREE=$(git add -A && git write-tree)` (see below).
- Not done on purpose: Task 19 publish steps, Task 20, M6.

## Adapted from UserActivity

- M0: none.
- Task 7: `Configuration/UserActivityOptions.cs`, `Configuration/UserActivityOptionsValidator.cs` (options shape, post-configure and validation).
- Task 8: none copied (read for the shared column-width constant idea only).
- Task 9: `Recorder/RetentionJob.cs` (batch loop, exception-type-only outcomes, zero days means skip), `Recorder/RecorderSql.cs` (parameterised SQL, quoted names via the syntax provider). Patterns adapted, no verbatim copy.
- Task 10: `Security/ViewThrottle.cs` into `EntryAudit/EntryAuditDeduplicator.cs` (bounded cache, fail towards writing).
- Task 11: `Recorder/RecorderWriter.cs` (never-blocks write path). `Identity/ActorRef.cs` and `UserIdentityResolver.cs` not adapted here: the caller resolves the user (Task 12).
- Task 12: `Identity/UserIdentityResolver.cs` into `NotificationHandlers/AuditHandlerHelpers.cs` `TryGetCurrentUser` (a found user keeps its own name, a missing one never borrows another identity). `Identity/ActorRef.cs` read, not needed: only the current user is resolved here.
- Tasks 13, 13b, 14 and 16a: none.
- Task 15: `Security/UserActivityAuthorizationHandler.cs` and `Security/UserActivityPolicy.cs` into `Authorization/AccessLogAuthorizationHandler.cs` and `Authorization/AccessLogRequirement.cs` (group-list requirement, handler never Scoped per spike S6). Patterns, rewritten for this package's requirement and options. The policy itself now lives in `Authorization/AccessLogPolicy.cs`.
- Task 16: `Security/ViewLogger.cs` into `Controllers/FormsAuditAccessApiController.cs` (`AuditSearched` and `AuditViewed` writes: log the read, skip repeats in the window, never fail the read). `Security/ViewThrottle.cs` was already adapted in Task 10; nothing new copied from it.
- Tasks 17 and 17b: none (no UserActivity file copied; Task 17 took only the GUID regex idea, written fresh).
- Task 16b: `Export/CsvWriter.cs` (formula guard and UTF-8 BOM) and `Export/ExportGate.cs` (row cap) into `Services/AccessCsvWriter.cs`, with `Services/CsvFormatter.cs` extended for SEC-L1. The streaming is new: UserActivity builds its file in memory. Patterns adapted, no verbatim copy.
- Tasks 18, 18b and 19: none (no UserActivity file copied).

## Milestone commits

The hook blocks `git commit`, so each milestone end state was saved as a tree object. A tree is a snapshot with no history. You wrap each one in a commit, chained on the one before, with `git commit-tree`, then move the branch with `git update-ref`. Nothing is rewritten. Author and committer come from your git config.

No AI attribution anywhere: no `Co-Authored-By` trailer, no generated-by line. The messages below have none.

Known trees:

| Milestone | Tree |
|---|---|
| M1 end (includes Task 10) | `9abb3cc` |
| M2 end (includes Tasks 13 and 13b) | `615e36d` |
| All milestones merged (M3, M4, M5) | `a96f87c` |
| After final fixes and verification | `65d97f3` |
| Last commit, with the final docs | computed: `FINAL_TREE=$(git add -A && git write-tree)` |

### Before you start

```bash
cd /Users/tpxi676/Projects/Umbraco.Community.FormsAuditTrail
git status --short | head            # the index is all staged; nothing should be unstaged or untracked-but-wanted
git rev-parse --short HEAD           # expect 1a69d9c (M0, merged as #36)

FINAL_TREE=$(git add -A && git write-tree)   # the working tree as handed over

# Each tree must exist and must not hold stray files.
for t in 9abb3cc 615e36d a96f87c 65d97f3 "$FINAL_TREE"; do
  git cat-file -t "$t"               # expect: tree
  git ls-tree -r --name-only "$t" | grep -Ei 'appsettings\.Local|\.sqlite|umbraco/Data|/Logs/|\.superpowers|(^|/)\.env$|\.bak' && echo "STRAY FILES IN $t"
done
```

If a stray file shows up, stop and ask. The early trees hold the older `COMMITS-TODO.md` and `HANDOVER.md` that existed at each snapshot, which is harmless: the last commit carries the final ones.

### Rebuild the commits

Run in order. Each command prints a commit hash that the next one uses as its parent. `HEAD` is `1a69d9c` now and does not move until `update-ref`.

```bash
BASE=$(git rev-parse HEAD)

# 1. M1: schema, options, retention and erasure scrub (also holds Task 10)
C1=$(git commit-tree 9abb3cc -p "$BASE" \
  -m "feat: add entry access audit schema, options, retention and erasure scrub" \
  -m "Adds the EntryAudit options with validation, the entry access tables and migrations for SQLite and SQL Server, and a manual release-checks workflow for the SQL Server tests. The daily job gains access retention, edit value expiry and erasure reconciliation, which probes the Forms reader before it scrubs anything. Pins the vulnerable transitive packages, raises the client floor, adds Forms contract tests and a vulnerable-package gate with npm audit and supply-chain hardening. Publishing now requires a successful release-checks run on the commit. Includes the first M2 task: the collector, controller map, detail shaping and dedupe.")
echo "$C1"

# 2. M2: capture pipeline (also holds Tasks 13 and 13b)
C2=$(git commit-tree 615e36d -p "$C1" \
  -m "feat: capture entry access for lists, details, exports, downloads and uploaded files" \
  -m "Adds the health signal, the writer (row shape, truncation, failure and gap rows, child record ids, export correlation), the capture core with its action filter and fetch handlers, and the uploaded-file middleware. The fix wave routes pre-write failures to health and hardens the capture pipeline. Includes the edit diff units and edit and create capture through the Forms read path.")
echo "$C2"

# 3. M3, M4 and M5 together (merged from parallel worktrees, so one tree)
C3=$(git commit-tree a96f87c -p "$C2" \
  -m "feat: audit edits and deletes, add the access-log API, CSV export, dashboard tabs and docs" \
  -m "Records edits with read-time masking and record and form deletes with an immediate erasure scrub, backed by a sentinel scan that proves no entry values reach the new tables. Adds the access-log policy and config fields, the list, detail and history API, the config change trail, a streaming CSV export with a per-user concurrency limit, and the Entry access and Entry history dashboard tabs. Adds the Playwright E2E project with a Development-only seed and diagnostics controller, and rewrites the README and CLAUDE.md for entry access audit. Includes the fix waves from the milestone and security reviews, the cleared-value sweep and the 403 for non-readers of the access log.")
echo "$C3"

# 4. Final fixes and verification
C4=$(git commit-tree 65d97f3 -p "$C3" \
  -m "chore: final fixes before 18.1.0-beta.1" \
  -m "Sets the version to 18.1.0-beta.1 in the four version files and updates the package description and tags. Narrows the public API surface to internal where the language allows, removes plan references from production comments, keeps more flags in the detail fallback and makes the cleared-value sweep also clear the old value on the clearing row. Completes the E2E suite (27 of 27 on SQLite), fixes the tab ARIA wiring found by axe, and adds a test for the tab and panel association. Nothing is published.")
echo "$C4"

# 5. Last commit: late fixes, Task 18b specs, final docs
C5=$(git commit-tree "$FINAL_TREE" -p "$C4" \
  -m "fix: late entry audit fixes, Task 18b specs and the final handover" \
  -m "Replaces the M0 handover with the end-of-run handover: what was built, gate results, review findings, rulings, escalations and the to-do list. Replaces the commit recipe. Adds a per-request form cache so an edit reads the form definition once, which brings the SQL Server edit overhead under 10 ms. Reads the form before the Forms action on a backoffice create, so no audit read runs inside Forms' save scope; on SQLite that read made Forms' INSERT occasionally hang with 'database is locked'. Makes the Entry access entry id a button, because Umbraco's router followed the old link out of the dashboard. Adds the Task 18b load, reconciliation and health specs with their gated demo endpoints. Documents the SQLite and SQL Server bulk delete limits in the README.")
echo "$C5"
```

Then move the branch and verify:

```bash
git update-ref refs/heads/fc/m1-schema "$C5"
git diff "$C5" --stat                # expect no output: the working tree matches the last commit
git status --short                   # expect no output (also catches untracked files, which diff does not)
git log --oneline -6
```

If `git diff` shows anything, the working tree moved after the last snapshot. Run `git add -A && git write-tree`, compare with `$FINAL_TREE`, and ask before going further. If you build only part of the chain, stop at the last commit you want and skip `update-ref` until the working tree and that tree agree.

If you would rather have one commit for the whole branch (the PR is rebase-merged for the milestone, but a single commit is also fine), skip the chain: `git commit-tree "$FINAL_TREE" -p 1a69d9c -m "feat: add entry access audit (18.1.0-beta.1)"`, then the same `update-ref` and verify.

### Optional per-task trees

Only if you want finer history inside M1 and M2. Parents follow the order shown, each on the previous.

| Task | Tree |
|---|---|
| 6 | `1cb33ec` |
| 6a | `a785180` |
| 7 | `7d23813` |
| 8 | `4a3a3c9` |
| 9 | `93900d7` |
| 9 fixes, M1 fix wave, 10 | `9abb3cc` (M1 end) |
| 10 to 12a | `2dc11a0` (M2 before its fix wave) |
| M2 fix wave plus 13, 13b | `615e36d` (M2 end) |

### After the commits

1. Run `bash build/gitleaks.sh "origin/main..HEAD"`.
2. Push the branch and open the PR (you, not the agents).
3. Run `release-checks.yml` on the branch, then on `main` after the merge, on the merged commit.
4. Do not merge `main` into `v17`.

## Milestone commit: retake screenshots

Branch `docs/screenshots-refresh`. Run these yourself:

1. `git status`: only `assets/screenshots`, `README.md`, `umbraco-marketplace.json` and this file changed; no secrets, no `.env`, no local settings.
2. `git add -A`
3. `git commit -m "docs: retake screenshots"`
4. `git log -1`

Then push, open the PR and merge. NuGet picks up the README and marketplace change on the next release.
