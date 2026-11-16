# Handover

## State (2026-11-16, end of M0)

- Branch `fc/m0-setup`, nothing committed or pushed. Tasks 1 to 5 done; results in `docs/spikes.md` and the "M0 results" table at the end of `docs/plan.md`.
- CI has not run on this branch. "Done" here means green locally only.
- Spike worktree `../FormsAuditTrail-spike` (branch `spike/entry-audit-m0`) is kept locally until 18.1.0 ships, as the plan says. Never push it. The v17 spike worktree was removed.

## For Pascal to do (in order)

1. Run the "Milestone commit" block at the end of `COMMITS-TODO.md`.
2. Push `fc/m0-setup` and open the M0 PR against `main`. Paste the "M0 results" table from the end of `docs/plan.md` into the description, then review the spikes.
3. Merge it. The plan says rebase-merge, while the repo convention is squash. With one commit per milestone the two are the same, so either is fine.
4. Start a fresh session for M1 (Task 6) from the merged `main`.

## Decisions taken in this session

- **S3 (yours, 2026-11-16, confirmed again after the like-for-like numbers):** keep the synchronous write after `next()`. Task 19's README says that on SQLite the package's writes can trigger Forms' delete hang, and recommends SQL Server for multi-user sites.
- **Rulings made by the build session** (full list in the plan workspace ledger). These are cheap to undo:
  - R1: the existing `demo/...DemoSite` is the plan's "TestSite". Every later `src/...TestSite` path means the demo site.
  - R2: floors are raised through the existing `Directory.Build.props` mechanism. No new `UmbracoCmsVersion` properties; Task 6 reconciles.
  - R3: `docs/` has design, plan and spikes.
  - R16: the plan and design text that S11 and S12 contradicted was corrected: Task 12a's upload root and identity, design 7.3, and the open inputs in Tasks 10a, 11 and 12. The corrections are synced to the workspace originals, with backups at `plan.md.bak-20261116` and `design.md.bak-20261116`.
  - R17: the no-DDL 500 is covered by SEC-09 (Task 10a health signal, Task 19 README).

## Open items and options for you (none block M1)

- **Permanent form delete (S5):** Forms sends the record ids in the request, so these could be scrubbed in the request instead of waiting up to 24 h for reconciliation. Not adopted, because the design says form deletes may lag. Say if you want it in Task 14.
- **Local paths in a public file:** `docs/plan.md` has 4 `/Users/...` paths copied from the workspace (the Repo and Spec lines, Task 1 Steps 1 and 2). CI rule 1 says no local paths. Strip them in the workspace and re-copy, or accept them.
- Task 20 Step 4 is wrong for the "uploaded files by entry" exporter: it does capture ids. Only "in disk structure" shows "ids not captured". Fix it when Task 20 runs.
- Things parked for later tasks:
  - README line 15 ("on Forms 18.1 this package is the only record") is stale under the new floor. Task 19.
  - The demo's ImageSharp pin is now redundant and its comment is stale. Task 6.
  - npm `^18.3.0`. Task 6.
- Not verified in M0:
  - A real browser UI pass. The spikes drove the same Management API calls.
  - S5, S6 and S8 on SQL Server.
  - A SQLite two-process migration race. Task 10a tests it.
  - Upload 403/206/304 on Forms 18.3.0. Task 12a re-checks it.
- Docker: your `umbraco-sql` container (created 8 Oct) started when Docker Desktop was opened for the spikes. It was left running.
