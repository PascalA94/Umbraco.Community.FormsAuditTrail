# Commits to make

Pascal commits once per milestone (the "Milestone commit" block at the end). The checkpoints below are a record of the plan's "Checkpoint (Pascal commits)" lines. Branch for M0: `fc/m0-setup`.

## M0 checkpoints

- [x] Task 1: `chore: raise CMS floor to 18.3.0 and Forms to 18.2.2, add design, plan and a local test site` (the existing demo site is the plan's "TestSite", ruling R1; no new site)
- [x] Tasks 2 to 5: `docs: record M0 spike results` (S1 to S12 in `docs/spikes.md`, short table at the end of `docs/plan.md`)
- [x] M0 stop: full suite 57/57, 0 warnings, check-vulnerable clean, gitleaks clean on the files to be committed. Each spike's results had a qa-reviewer read; every Important finding fixed and re-checked.

## Adapted from UserActivity

None in M0 (no M0 task carries a "Reuse (2026-11-16)" line).

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
