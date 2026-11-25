# Commits to make

Pascal commits once per milestone (the "Milestone commit" block at the end). The checkpoints below are a record of the plan's "Checkpoint (Pascal commits)" lines. Branch for M0: `fc/m0-setup`.

## M0 checkpoints

- [x] Task 1: `chore: raise CMS floor to 18.3.0 and Forms to 18.2.2, add design, plan and a local test site` (the existing demo site is the plan's "TestSite", ruling R1; no new site)
- [x] Tasks 2 to 5: `docs: record M0 spike results` (S1 to S12 in `docs/spikes.md`, short table at the end of `docs/plan.md`)
- [x] M0 stop: full suite 57/57, 0 warnings, check-vulnerable clean, gitleaks clean on the files to be committed. Each spike's results had a qa-reviewer read; every Important finding fixed and re-checked.

## M1 checkpoints

Branch `fc/m1-schema`. Pascal asked (2026-11-24) to keep building past M1 without stopping, so later milestones sit in the same working tree. Each milestone's end state is saved as a git tree object (see "Milestone commits" below).

- [x] Task 6: `chore: pin vulnerable transitive packages, raise the client floor and add Forms contract tests`
- [x] Task 6a: `ci: add a vulnerable-package gate, npm audit and supply-chain hardening`
- [x] Task 7: `feat: add EntryAudit options with validation`
- [x] Task 8: `feat: add entry access tables and migrations for SQLite and SQL Server` (plus `release-checks.yml`, a manual workflow for the SQL Server tests)
- [x] Task 9: `feat: add access retention, edit value expiry and erasure reconciliation to the daily job`
- M1 end tree: `93900d7` before the milestone fix wave; `9abb3cc` after it (this one also contains Task 10, the first M2 task, because work was pipelined).

## Adapted from UserActivity

- M0: none.
- Task 7: `Configuration/UserActivityOptions.cs`, `Configuration/UserActivityOptionsValidator.cs` (options shape, post-configure and validation).
- Task 8: none copied (read for the shared column-width constant idea only).
- Task 9: `Recorder/RetentionJob.cs` (batch loop, exception-type-only outcomes, zero days means skip), `Recorder/RecorderSql.cs` (parameterised SQL, quoted names via the syntax provider). Patterns adapted, no verbatim copy.
- Task 10: `Security/ViewThrottle.cs` into `EntryAudit/EntryAuditDeduplicator.cs` (bounded cache, fail towards writing).
- Task 11: `Recorder/RecorderWriter.cs` (never-blocks write path). `Identity/ActorRef.cs` and `UserIdentityResolver.cs` not adapted here: the caller resolves the user (Task 12).
- Task 12: `Identity/UserIdentityResolver.cs` into `NotificationHandlers/AuditHandlerHelpers.cs` `TryGetCurrentUser` (a found user keeps its own name, a missing one never borrows another identity). `Identity/ActorRef.cs` read, not needed: only the current user is resolved here.

## Paused here (2026-11-16)

M0 is done. Next: **Task 6 (Dependency pins, client floor and contract tests)**, the first task of M1, on a new branch `fc/m1-schema` cut from `main` after the M0 PR merges. Read `HANDOVER.md` first. State lives in `docs/plan.md` (ticked boxes and M0 results), `docs/spikes.md` and this file.

## Milestone commit

Check first: `git status` must show only the files below (no `appsettings.Local.json`, no demo `umbraco/Data` or `Logs`, no `.superpowers/`, no review docs).

```bash
git status --short
# expect: .gitignore CLAUDE.md COMMITS-TODO.md Directory.Build.props HANDOVER.md README.md docs/ and four packages.lock.json / csproj changes
git add -A
git commit -m "chore: raise floors to CMS 18.3.0 and Forms 18.2.2, add docs and M0 spike results" -m "Raises the Umbraco CMS floor to 18.3.0 and Forms to 18.2.2 (portfolio floor and Forms security release), keeps the MessagePack and Pkcs pins, and regenerates the lock files at the floor. Adds docs/design.md, docs/plan.md and docs/spikes.md with the results of spikes S1 to S12."
git log -1 --stat --oneline
```
