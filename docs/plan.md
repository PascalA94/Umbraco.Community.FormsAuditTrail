# Forms compliance (entry access audit) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn `Umbraco.Community.FormsAuditTrail` into "Forms Audit Trail: who touched your form data" by adding a backoffice entry access audit (list views, opens, edits with before and after, deletes, exports, downloads) beside the existing form definition audit, released as 18.1.0 on `main` and then 17.1.0 on `v17`.

**Architecture:** A global MVC action filter matches ten Forms Management API controller types by `typeof` and opens a capture window on a request-scoped collector; a completeness contract test fails CI for any Forms entry endpoint that is neither audited nor on a reasoned `NotAudited` list. The capture logic lives in one `EntryAuditCapture` class, so the S1 middleware fallback only swaps a thin adapter. A small middleware records uploaded-file opens, which MVC never sees. Forms notification handlers append record ids, edit diffs, created-entry captions and deletes to the window only while it is open; before values come through Forms' own `IRecordStorage` read, never raw SQL. After the action returns 2xx, one **singleton** writer persists an access row first and child rows in batches, through a fresh DI scope per write on the package's existing EF Core context; it never takes the request's cancellation token, and a failure is recorded in a health signal and later as an `AuditGap` row. A bulk delete is one `Deleted` row with child record ids. Four new tables arrive through one additive migration per provider. The daily retention job grows three batched GDPR steps (access retention, edit value expiry, reconciliation scrub). Config changes are recorded. A new group-gated API and two new dashboard sub-tabs read the log.

**Tech Stack:** .NET 10, Umbraco CMS 18.3 / Forms 18.2.2 (v17 line: CMS 17.8 / Forms 17.6.2), EF Core 10 (SQLite + SQL Server, dual migration sets), xUnit v2 + NSubstitute, Lit + TypeScript + Vite (`@umbraco-cms/backoffice` ^18.3.0), Vitest (new, client utils only), Playwright + `@umbraco/playwright-testhelpers` (new, E2E).

**Spec:** `design.md` in the private plan workspace (copied to `docs/design.md` in Task 1). Its "Resolved by Pascal" section is final: erasure scrubbing may lag up to 24 hours for bulk deletes, permanent form deletes and Forms' scheduled deletion; single-entry deletes are scrubbed immediately. Its "Security review changes (2026-10-30)" section maps each `security-review.md` finding (SEC-xx) to the spec; the tasks below cite the same IDs and the review's test numbers (T1 to T23). Two items there wait on Pascal (fail-open default, `KeepClearedValues` default); the plan builds the spec defaults, and either answer changes one default and its test, not the task structure. (Both were since resolved by Pascal: fail-open default kept, `KeepClearedValues` default false.) **S5 decision (Pascal, 2026-10-30, final):** if S5 finds no per-record delete event, the 24-hour reconciliation lag is accepted for **all non-request deletes** and the README says so; backoffice deletes are still scrubbed in the request (Task 14).

**Revision 2026-10-30 (security review):** new tasks 6a, 10a, 12a and 16a; changed tasks 1 to 3 and 5 to 21. See "Change log" at the end.

**Revision 2026-10-30 (plan review, P1 to P21):** tasks 11, 13, 16, 17 and 18 split (new 11b, 13b, 16b, 17b, 18b); new 12b (security checkpoint after M2) and 14a (sentinel scan); one PR per milestone, rebase-merged. 32 tasks. See "Plan review changes (2026-10-30)" near the end.

**Repo:** `Umbraco.Community.FormsAuditTrail` (this repository). Paths below are relative to its root. `PKG` = `src/Umbraco.Community.FormsAuditTrail`, `TESTS` = `src/Umbraco.Community.FormsAuditTrail.Tests`, `CLIENT` = `PKG/Client`. Expand these short names to the full paths when running a command.

**Spec gaps closed in this plan:** (1) `AuditExported` with no form filter has no single form: it is stored with `FormId = Guid.Empty`, `FormName = "(access log)"` and shown **only to access-log users who can access every form** (Task 16; changed by SEC-L3). (2) Record history for an unknown id returns 404, same as an inaccessible one, so existence does not leak (Task 16). (3) No E2E infrastructure or test site exists: Task 1 adds a local TestSite and Tasks 18 and 18b a Playwright project, run locally rather than in CI; their output for both providers is attached to the M5 PR as release evidence (P17). (4) The health signal is in MVP (Task 10a). The first draft left it out; SEC-09 reversed that. (5) SQL Server tests (`[SqlServerFact]`, which **fails** in CI when SQL Server is missing, P6) are the upgrade test, the over-length write test (SEC-02, Task 11), the 50k export (Task 11b), batched retention (Task 9) and the `DROP` script (Task 19); E2E runs on both providers. Other integration tests run on SQLite, which does not enforce `nvarchar` lengths. (6) Bulk deletes write one `Deleted` row with child record ids (spec 7.3 allowed either; P8 decides).

## Global Constraints

- **Versions (Pascal, final, D9):** `main` ships 18.1.0: CMS **`[18.3.0, 19.0.0)`** on all three CMS references, Forms `[18.2.2, 19.0.0)` on both `Umbraco.Forms.Web` and `Umbraco.Forms.Core.Providers`, backoffice npm `^18.3.0`. `v17` ships 17.1.0: CMS **`[17.8.0, 18.0.0)`**, Forms `[17.6.2, 18.0.0)`, npm `^17.8.0`. Compatible: Forms 18.2.2 needs CMS `[18.2.0, 19.0.0)` and Forms 17.6.2 needs `[17.6.0, 18.0.0)` (nuspec, verified 2026-10-30). .NET 10 on both. Node 24.13+. The floors are set in **Task 1** (P1), so the M0 spikes compile against the types they probe.
- **Dependency audit (SEC-05, D10):** `<NuGetAuditMode>all</NuGetAuditMode>` and `<NuGetAuditLevel>low</NuGetAuditLevel>`; direct pins `System.Security.Cryptography.Pkcs` `[10.0.13, 11.0.0)` and `MessagePack` `[3.1.11, 4.0.0)` with a why-comment naming GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7 and GHSA-qhrr-8q5h-9q3h. CI fails on any High or Critical, transitive included, on both matrix legs and both branches (Task 6a). Never remove a pin as a tidy-up.
- **Identity kept:** NuGet ID `Umbraco.Community.FormsAuditTrail`, assembly, namespace, `App_Plugins/FormsAuditTrail`, table prefix `formsAuditTrail`, dashboard alias `UmbracoCommunity.FormsAuditTrail.Dashboard`, pathname `forms-audit-trail`.
- **Existing installs (G5):** schema change is additive only; `formsAuditTrailEntries` and `formsAuditTrailChanges` are untouched. `RetentionDays` (0 = forever) and `EnableCsvExport` (true) keep meaning and defaults. Existing routes and DTOs keep their shape; new fields are additive. Migrations still run only through `RunAuditMigration` at `RuntimeLevel.Run`.
- **Fail-open (E12):** an audit failure never blocks, fails or changes a Forms save, delete, list or export response, except under the opt-in `EntryAudit:FailClosedOnExport` (default false), which returns 503 for an export or download whose row cannot be written. Log at Error with `userKey`, event type, form id and record count. Overhead target on the entries list: under 10 ms per request.
- **Fail-open never hides itself (SEC-02):** the writer truncates bounded strings to their columns, saves the parent row before child rows, uses `CancellationToken.None` (never `RequestAborted`), never skips a row for an unresolved form id (`Guid.Empty` + `formUnresolved`), and records every failure in `EntryAuditHealth`, with an `AuditGap` row on the next success.
- **Before values (SEC-04):** read through Forms' `IRecordStorage` in the filter before `next()`, then copied. Never parse `UFRecords.RecordData` with our own SQL. The only raw `UFRecords` read is `UniqueId` for reconciliation.
- **`Guid.Empty` rows (SEC-L3):** returned only to users who can access every form.
- **Pitfall P2 [Critical]:** never hold `IBackOfficeSecurityAccessor` results, `HttpContext`, the current user or the collector in a static or singleton field. Resolve the user per request via `AuditHandlerHelpers.TryGetCurrentUser` (Task 12). The collector is scoped and reached through `HttpContext.RequestServices`.
- **DI lifetimes (P2):** a singleton never takes a scoped dependency. `IEntryAuditWriter`, `IAuditStateStore`, `EntryAuditHealth`, `EntryAuditDeduplicator`, `ConfigChangeRecorder` and `IForeignValueHandlerDetector` are singletons that open their own scope per unit of work; `IFormsRecordReader`, `IFormFieldInfoProvider`, `ISensitiveDataPermission` and the access-log authorization handler are scoped and are resolved from a scope, never injected into a singleton. (2026-11-16 override: the access-log authorization handler is never Scoped, per UserActivity spikes S6: Singleton as UserActivity registers it, or Transient; it resolves scoped services per call. See Task 15.) `CompositionTests` (from Task 10) builds the container with `ValidateScopes` and `ValidateOnBuild` and resolves every handler, filter, middleware and job; every task that registers a type adds it there. The TestSite runs in `Development`, which validates scopes too.
- **Pitfall S1 [Critical]:** every new endpoint sits on a `ControllerBase` with `[Authorize(Policy = "SectionAccessForms")]` plus the access-log policy. No anonymous action, no `UmbracoApiController`. Enforced by a reflection test (Task 15).
- **Pitfall S3 [Critical]:** dashboard renders stored strings (form names, user names, captions, values, `Detail`) as Lit text bindings only. No `unsafeHTML`, no `innerHTML`. Enforced by a grep check in CI and an ESLint `no-restricted-imports` rule (Task 17, SEC-I2).
- **Supply chain (SEC-13, SEC-I3, SEC-I4):** every GitHub action pinned to a full commit SHA; publish only from a protected `nuget` environment for a tag on `main` or `v17`; `npm audit --audit-level=high` and CodeQL in CI (Task 6a).
- **Pitfall S5 / P5:** not applicable (no Surface controllers, no Razor views, all new endpoints are `GET`). Pitfalls D1 to D4 do not apply: the migration is additive with no backfill, proven by Task 8's operations test.
- **Anti-enumeration:** 403 when a `formId` filter names an inaccessible form; 404 for a single item or a record history on an inaccessible form. Every returned row passes `IFormsSecurity.HasAccessToForm`.
- **CSV:** every text cell through `CsvFormatter.Escape`, which also guards a leading `\r` and whitespace-then-trigger (SEC-L1). Edit values never appear in any CSV. The access-log CSV is streamed with a 100,000-row cap (SEC-10).
- **GDPR:** masking happens at write time; a masked value never reaches the database. Read-time masking fails closed (SEC-12). A blanked field keeps no old value unless `KeepClearedValues` (SEC-03). Free-text search terms, member emails and upload file names are never stored. No copy anywhere says "compliant". The README "GDPR" section is reviewed by a DPO or legal adviser before 18.1.0 stable (SEC-11, release checklist).
- **Repo rules:** a version bump touches four files (csproj `<Version>`, `CLIENT/package.json`, `CLIENT/package-lock.json` via `npm install --package-lock-only`, `CLIENT/public/umbraco-package.json`). Rebuild and commit `PKG/wwwroot/` with any `Client/` change. Any model change needs a migration for both providers and a clean `has-pending-model-changes` on both. Never merge `main` into `v17`; cherry-pick.
- **Writing:** UK English. Code comments explain why, not what. No en or em dashes in docs, comments or commit messages (repo convention since #11).
- **Commits:** Claude never runs `git commit`. Every checkpoint is "**Checkpoint (Pascal commits):**" with a suggested Conventional Commit message and no AI attribution.
- **Branches and PRs (Pascal, P12, P21):** one branch and one PR per milestone, each cut from up-to-date `main` after the previous milestone merged, and **rebase-merged** (never squashed) so each checkpoint commit lands on `main` on its own and the `v17` port cherry-picks one milestone at a time. Branches: `fc/m0-setup` (Task 1 plus the M0 results), `fc/m1-schema` (6 to 9), `fc/m2-capture` (10 to 12b), `fc/m3-edits-deletes` (13 to 14a), `fc/m4-api-dashboard` (15 to 17b), `fc/m5-beta` (18 to 19); on `v17`, `fc17/m1` to `fc17/m5` (Task 21). Until 18.1.0-beta.1 is tagged, `main` holds unreleased work; nothing is tagged from it in between.
- **PR checks (Pascal):** run in this repo's own CI (`build.yml`, `codeql.yml`, GitHub secret scanning and push protection). The repo is not enrolled in the mf-harness `pr-checks.yml` ruleset.
- **Public docs (Pascal, P21):** the repo is public. `docs/` holds `design.md` and `plan.md` only. `security-review.md`, `plan-review.md` and council transcripts never go into the repo; references to them by name are fine.
- **SQL Server tests never skip in CI (P6):** `[SqlServerFact]` skips only when the `CI` environment variable is unset; in CI a missing `FORMSAUDIT_SQLSERVER` is a test failure, and a CI step fails unless the trx shows every `Category=SqlServer` test executed and passed.
- **Subagent briefs:** execution is subagent-driven. Each task's brief includes this file's Global Constraints, the task's own section, the spec sections it cites, and `~/.claude/references/umbraco-pitfalls.md` (Critical and Security items are mandatory checks).
- **Security sign-off:** `security-analyst` reviews the capture pipeline (filter, writer, upload middleware) after M2 (Task 12b) and the whole surface before 18.1.0 stable (Task 19). Each High in `security-review.md` is closed or accepted by Pascal as residual risk.

## Review Focus

1. **Default `RetentionDays = 0` must not stop the new GDPR steps.** Today `AuditRetentionJob.RunJobAsync` returns early when `RetentionDays <= 0`, which is the default on every existing install. Expect access retention, value expiry and reconciliation to run anyway. Test: `RunJob_WithDefinitionRetentionOff_StillRunsEntryAuditSteps` (Task 9).
2. **Upgrade with no `EntryAudit` section in appsettings.** Every existing site. Expect all `EntryAudit` defaults (Enabled true, groups `["admin"]`, 365/90 days). Test: `Bind_WithNoEntryAuditSection_AppliesSpecDefaults` (Task 7).
3. **Record history for an unknown record id.** A 200 with an empty list for unknown ids next to a 404 for inaccessible ones would leak existence. Expect 404 for both, and the UI shows one neutral message. Test: `GetRecordHistory_UnknownAndInaccessible_BothReturn404` (Task 16).
4. **Editing an entry whose field was removed from the form since submission.** The before data holds a field id the current form no longer has. Expect a row with the field id as caption, never an exception. Test: `Diff_FieldMissingFromForm_UsesFieldIdAsCaption` (Task 13).
5. **Exporting a large form.** An export of 10,000 entries produces 10,000 child rows; SQLite has a bound-parameter limit. Expect the parent saved first, children in batches of 1,000, every id stored and no exception, on both providers. Tests: `Write_ExportWithTenThousandRecordIds_StoresAll`, `Write_Export50k_ChildFailure_KeepsParentWithRecordIdsCapturedFalse`, `[SqlServerFact] Write_Export50k_OnSqlServer_StoresAll` (Task 11b).
6. **Over-length strings on SQL Server (SEC-02).** A 600-character form name or caption fails `SaveChanges` on SQL Server but not on SQLite, so SQLite-only tests pass while production loses the row. Expect a truncated row. Test: `[SqlServerFact] Write_OverLengthStrings_TruncatesAndWrites` (Task 11).
7. **Before values on an encrypting site (SEC-04).** Expect the diff to use Forms' read path and no ciphertext or extra plaintext to reach our tables, through real Forms. Tests: `Filter_Edited_BeforeValuesViaRecordStorage_TransformingHandler_DiffCorrect` (Task 13b), E2E `encryption.spec.ts` on SQLite and SQL Server (Task 18, P3).
8. **Reconciliation when `UFRecords` cannot be read (SEC-I1).** Expect nothing scrubbed and the step marked failed, never "all missing". Tests: `ReconcileErasures_ReaderThrows_ScrubsNothingAndMarksStepFailed` (Task 9), E2E `reconciliation.spec.ts` "unreadable UFRecords" case (Task 18b).
9. **Upgrading an existing install on SQL Server.** The SQL Server `InitialCreate` id differs (`20260715114751`). Expect old rows intact and the new tables added, and the test must not skip in CI. Tests: `[SqlServerFact] Upgrade_From1802Schema_OnSqlServer` plus the trx check (Task 8, P6).
10. **Forms reorders its upload middleware.** Expect `FileViewed` on 200 and 206, nothing on 403 and 404, the recorded 304 decision, and a failing build if Forms moves or renames the anchor. Tests: `UploadMiddlewarePipelineTests` (Task 12a), `ProtectFormUploadRequestsMiddleware_NamespaceAndAnchor` (Task 6, P9).
11. **Reconciliation SQL on real `UFRecords`.** A wrong table, column or quoting would silently stop scrubbing bulk, form and scheduled deletes. Expect a deleted record's values nulled by the job on both providers, including more than 500 ids. Test: E2E `reconciliation.spec.ts` (Task 18b, P5).
12. **Bulk delete of thousands of entries.** Expect one parent row, batched children, set-based scrub and the Forms response inside the time bound. Test: `Filter_BulkDelete5000_OneParentRowWithChildren_WithinBounds` (Task 14, P8).
13. **No field values in the database when `StoreEditValues=None`.** Expect no sentinel value, search term, upload file name or member email in any text column of the four new tables, for each mode. Test: `SentinelScanTests` (Task 14a, P7).
14. **An existing negative `RetentionDays`.** The current job treats it as off. Expect start-up to succeed, the value coerced to 0 with a Warning. Test: `Bind_NegativeDefinitionRetentionDays_CoercedToZeroWithWarning` (Task 7, P11).

---

## File structure

New server files (all under `PKG`):

| File | Responsibility |
|---|---|
| `Configuration/EntryAuditOptions.cs` | `EntryAudit` sub-section, defaults from spec 7.7 |
| `Configuration/StoreEditValuesMode.cs` | `None`, `MaskSensitive`, `All` |
| `Configuration/FormsAuditTrailOptionsSetup.cs` | `PostConfigure` (group default) and `Validate` (no negatives) |
| `Models/AccessEventType.cs` | 10 to 24 |
| `Models/FormAccessEntry.cs`, `Models/FormAccessRecord.cs`, `Models/FormEntryChange.cs`, `Models/AuditStateEntry.cs` | EF entities for the four new tables |
| `Migrations/{Sqlite,SqlServer}/<ts>_AddEntryAccessAudit.cs` (+ `.Designer.cs`) | Generated additive migration per provider |
| `EntryAudit/EntryAuditDetail.cs` | `Detail` JSON model, `v = 1`, allowed keys only |
| `EntryAudit/CaptureWindow.cs`, `EntryAudit/EntryAuditCollector.cs` | Per-request capture state |
| `EntryAudit/EntryAuditCollectorAccessor.cs` | Resolves the request's collector, null outside HTTP |
| `EntryAudit/FormsControllerMap.cs` | `typeof` map of the ten audited Forms controllers to event kinds, plus the `NotAudited` allow-list with reasons |
| `EntryAudit/EntryAuditHealth.cs`, `EntryAudit/AuditStateStore.cs` | Health counters (singleton) and the `formsAuditTrailState` key-value store |
| `EntryAudit/UploadAuditMiddleware.cs`, `EntryAudit/UploadPathResolver.cs` | `FileViewed` capture on Forms' upload path |
| `EntryAudit/ConfigChangeRecorder.cs` | `ConfigChanged` rows at startup and on `IOptionsMonitor.OnChange` |
| `EntryAudit/ForeignValueHandlerDetector.cs` | Startup check for other `RecordFetching` / `EntrySearchResultFetching` handlers |
| `EntryAudit/EntryAuditRequestReader.cs` | Form id, record id and shaped `Detail` from an action context |
| `EntryAudit/EntryAuditCapture.cs` | Shared capture core: open the window, take the before snapshot, decide success, build the `AccessWrite`, call the writer, scrub request deletes, fail open or closed |
| `EntryAudit/EntryAuditActionFilter.cs` | Thin MVC adapter over `EntryAuditCapture` (the S1 NO-GO path swaps it for `EntryAuditEndpointMiddleware`) |
| `EntryAudit/AccessWrite.cs`, `EntryAudit/IEntryAuditWriter.cs`, `EntryAudit/EntryAuditWriter.cs` | Persist one access event in a fresh scope |
| `EntryAudit/EntryAuditDeduplicator.cs` | `IMemoryCache` dedupe for view events |
| `EntryAudit/RecordValueSnapshot.cs`, `EntryAudit/EntryEditDiffer.cs` | Before values copied from `IRecordStorage`, and field diff with masking and `Cleared` |
| `EntryAudit/IFormFieldInfoProvider.cs` (+ impl) | Field caption and sensitive flag per form |
| `EntryAudit/IFormsRecordReader.cs`, `EntryAudit/UmbracoFormsRecordReader.cs` | `UFRecords.UniqueId` existence check through Umbraco's scope (reconciliation only) |
| `EntryAudit/ErasureScrubber.cs` | Null edit values by record ids or by form id |
| `EntryAudit/ISensitiveDataPermission.cs` (+ impl) | Read-time "can view sensitive data" check (S4) |
| `NotificationHandlers/EntrySearchResultFetchingHandler.cs`, `RecordFetchingHandler.cs`, `RecordSavingHandler.cs`, `RecordDeletingHandler.cs`, `RecordDeletedHandler.cs` | Forms hooks feeding the collector or scrubbing |
| `Authorization/AccessLogRequirement.cs`, `Authorization/AccessLogAuthorizationHandler.cs` | Named policy `FormsAuditTrail.AccessLog` |
| `Controllers/FormsAuditAccessApiController.cs`, `Controllers/Dtos/AccessEntryDtos.cs` | Access-log API |

Modified server files: `Composers/FormsAuditComposer.cs`, `Configuration/FormsAuditTrailOptions.cs`, `Persistence/FormsAuditDbContext.cs`, both `*ModelSnapshot.cs`, `BackgroundJobs/AuditRetentionJob.cs`, `NotificationHandlers/FormDeletedHandler.cs`, `NotificationHandlers/RunAuditMigration.cs` (health only), `Services/CsvFormatter.cs` (SEC-L1), `Controllers/FormsAuditApiController.cs` (`GetConfig` only), `Controllers/Dtos/AuditEntryListDto.cs` (`AuditConfigDto` only), `Umbraco.Community.FormsAuditTrail.csproj`.

CI and repo files: `.github/workflows/build.yml`, `.github/workflows/publish.yml`, new `.github/workflows/codeql.yml`, new `.github/dependabot.yml` (`github-actions`, `nuget`, `npm`), new `build/check-vulnerable.py` (+ `build/tests/`).

Client (`CLIENT/src`): new `utils/access-format.ts` (+ `.test.ts`), `dashboards/audit-root.element.ts`, `dashboards/entry-access-view.element.ts`, `dashboards/entry-history-view.element.ts`, `components/audit-health-banner.element.ts`; modified `manifests.ts`, `services/audit-api.service.ts`, `components/audit-diff-viewer.element.ts`; new `CLIENT/eslint.config.js` (the repo has no ESLint today). The existing `forms-audit-dashboard` element is left as is and rendered inside the "Form changes" tab.

Outside the package: `src/Umbraco.Community.FormsAuditTrail.TestSite/` (local Umbraco + Forms site, not in the solution, not packed; holds the E2E seed, diagnostics and simulated-encryption composers), `src/Umbraco.Community.FormsAuditTrail.E2E/` (Playwright), `.config/dotnet-tools.json`, `.github/workflows/build.yml`, `build/check-trx.py` (SQL Server tests ran, P6).

New cross-cutting tests (`TESTS`): `CompositionTests.cs` (P2), `UploadMiddlewarePipelineTests.cs` (P9), `AccessApiAuthorizationTests.cs` (in-process 403, P10), `SentinelScanTests.cs` (P7), `DropScriptTests.cs` (P6). New test packages: `Microsoft.AspNetCore.TestHost`, `Microsoft.Extensions.TimeProvider.Testing`.

---

## M0: spikes (go/no-go)

Tasks 2 to 5 run on a throwaway branch `spike/entry-audit-m0` cut from `fc/m0-setup` after Task 1, checked out in its own worktree (`git worktree add ../FormsAuditTrail-spike -b spike/entry-audit-m0`). Spike code is never merged or pushed. Results are written to the **M0 results** table at the bottom of `docs/plan.md` in the main checkout on `fc/m0-setup` (P13f), never on the spike branch; that table becomes the M0 note in the `fc/m0-setup` PR description (spec section 8: no new doc). Run every spike on SQLite first, then SQL Server (Docker `mcr.microsoft.com/mssql/server:2022-latest`, connection string swapped in the TestSite's `appsettings.Development.json`). Pascal reviews the M0 results before M1 starts (README "Build approach").

### Task 1: Milestone branch, floors, docs and local test site

**Files:**
- Create: `docs/design.md`, `docs/plan.md` (copies; never `security-review.md` or `plan-review.md`)
- Create: `src/Umbraco.Community.FormsAuditTrail.TestSite/` (from the `umbraco` template)
- Modify: `.gitignore`, `PKG/Umbraco.Community.FormsAuditTrail.csproj` (floors only, P1)

**Interfaces:**
- Produces: MSBuild properties `UmbracoCmsVersion` (default `[18.3.0, 19.0.0)`) and `UmbracoFormsVersion` (default `[18.2.2, 19.0.0)`), overridable with `-p:UmbracoCmsVersion=18.* -p:UmbracoFormsVersion=18.*`. Task 6 adds pins, audit mode and the CI matrix on top.
- Produces: a runnable site at `https://localhost:44330` with CMS 18.3.0, Forms 18.2.2 and a project reference to `PKG`, unattended install, admin `admin@example.com` / password from `appsettings.Development.json` (git-ignored local override), `ASPNETCORE_ENVIRONMENT=Development` (scope validation on). Used by Tasks 2 to 5, 18, 18b, 20.

- [x] **Step 1:** `git switch main && git pull && git switch -c fc/m0-setup`
- [x] **Step 2:** Copy `design.md` and `plan.md` (only these two) from the private plan workspace into `docs/`. Verify: `ls docs` shows exactly `design.md` and `plan.md`. From here `docs/plan.md` holds the M0 results; any later plan revision is made in the workspace and re-copied.
- [x] **Step 2a (P1):** In the csproj add `<UmbracoCmsVersion Condition="'$(UmbracoCmsVersion)' == ''">[18.3.0, 19.0.0)</UmbracoCmsVersion>` and use it for `Umbraco.Cms.Web.Common`, `Umbraco.Cms.Persistence.EFCore.Sqlite` and `Umbraco.Cms.Persistence.EFCore.SqlServer`; add `<UmbracoFormsVersion Condition="'$(UmbracoFormsVersion)' == ''">[18.2.2, 19.0.0)</UmbracoFormsVersion>` and use it for both Forms references. Rewrite the floor comment: the CMS floor is Pascal's portfolio floor (D9); Forms 18.2.2 is a security release and has the controllers the audit matches. Then `dotnet restore src/Umbraco.Community.FormsAuditTrail.slnx && dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release`. Expected: no `NU1605`, `Passed!` with every existing test.
- [x] **Step 3:** `dotnet new install Umbraco.Templates::18.3.0`, then in `src/`: `dotnet new umbraco -n Umbraco.Community.FormsAuditTrail.TestSite --development-database-type SQLite`. Pin `Umbraco.Cms` to `18.3.0` (exact, the new CMS floor) and add `Umbraco.Forms` `18.2.2` (exact) and a `ProjectReference` to `../Umbraco.Community.FormsAuditTrail/Umbraco.Community.FormsAuditTrail.csproj`. Configure unattended install (`Umbraco:CMS:Unattended`) with credentials read from `appsettings.Development.json`. Do not add the site to `Umbraco.Community.FormsAuditTrail.slnx` (CI must not build it).
- [x] **Step 4:** Add to `.gitignore`: `src/Umbraco.Community.FormsAuditTrail.TestSite/umbraco/Data/`, `.../umbraco/Logs/`, `.../wwwroot/media/`, `.../appsettings.Development.json`, `src/Umbraco.Community.FormsAuditTrail.E2E/test-results/`, `.../playwright-report/`, `.../.env`.
- [x] **Step 5: Verify.** `dotnet run --project src/Umbraco.Community.FormsAuditTrail.TestSite` then sign in, create form "Spike Contact" (Name; Email marked "contains sensitive data"; CV file upload; one workflow that sends an email to a local catch-all such as smtp4dev), submit it three times from a content page with a file named `Jane_Smith_CV.pdf`. Expected: Forms section shows the "Forms Audit Trail" dashboard and three entries; log contains `Applying Forms Audit Trail database migrations` on first run only; no Error from Forms on CMS 18.3.0 (first runtime check of the Forms 18.2.2 + CMS 18.3.0 pair).
- [x] **Checkpoint (Pascal commits):** `git add docs .gitignore src/Umbraco.Community.FormsAuditTrail.TestSite PKG/Umbraco.Community.FormsAuditTrail.csproj` / suggested message `chore: raise CMS floor to 18.3.0 and Forms to 18.2.2, add design, plan and a local test site`

### Task 2: Spike S1, filter matching and parameter sources (GO/NO-GO)

**Files (spike branch only):** `PKG/EntryAudit/SpikeProbeFilter.cs`, `PKG/NotificationHandlers/SpikeProbeHandler.cs`, one line in `FormsAuditComposer.cs`.

- [x] **Step 1:** `git worktree add ../FormsAuditTrail-spike -b spike/entry-audit-m0` and work there. Add `SpikeProbeFilter : IAsyncActionFilter` registered with `services.Configure<MvcOptions>(o => o.Filters.Add<SpikeProbeFilter>())`. It logs at Warning: `ControllerTypeInfo.FullName`, route values, query keys, `ActionArguments` names and types, result type and status after `next()`, and `RuntimeHelpers.GetHashCode` of a scoped probe object from `HttpContext.RequestServices`.
- [x] **Step 2:** Add `SpikeProbeHandler` for `EntrySearchResultFetchingNotification`, `RecordFetchingNotification`, `RecordSavingNotification`, `RecordDeletingNotification`, `RecordDeletedNotification`, resolving the same scoped probe through `IHttpContextAccessor` and logging its hash code plus payload member names.
- [x] **Step 3:** In the backoffice, on SQLite then SQL Server: open the entries list, change page and filter, open an entry, edit and save it, delete one entry, bulk delete two, generate a CSV export, download it, re-run the entry's workflow, create an entry from the backoffice (if the 18.2.2 UI offers it; otherwise call `CreateRecordController` with the bearer token from the browser), and open a member's form summary.
- [x] **Step 4: Verify.** `grep -c "SpikeProbe" src/Umbraco.Community.FormsAuditTrail.TestSite/umbraco/Logs/*.json`. Expected: one filter line per action for each of `GetCollectionController`, `GetAuditTrailController`, `UpdateRecordController`, `DeleteRecordController`, `ExecuteActionController`, `PostGenerateExportController`, `ExportController`, `RetryWorkflowController`, `CreateRecordController`, `GetMemberFormSummariesController`; handler hash codes equal the filter's for the same request.
- [x] **Step 5: Record in M0 results:** for each controller its full type name, where `formId` and the record id come from (route, query, body argument), the list filter parameter names (date range, state, sort, search term, page, page size), the export type parameter, the result type of `PostGenerateExportController` and `ExportController`, `RetryWorkflowController`'s `workflowId` source, how `CreateRecordController` returns the new record id, and the type and meaning of `GetMemberFormSummaries`' string parameter (key or email). Also record (P13h, P19): the **claim type** that carries the backoffice user key on `HttpContext.User` for a Management API request (log every claim type, never values), whether `HttpContext.User` is authenticated before and after `next()`, and, from the TestSite's `IServiceCollection`, the registered lifetime of every Umbraco and Forms service the package consumes (`IFormService`, `IRecordStorage`, `IFormsSecurity`, `IUserService`, `IScopeProvider`, `IBackOfficeSecurityAccessor`, `IAuthorizationService`), for `CompositionTests` (P2).
- **GO:** all ten matched by `ControllerTypeInfo` and the collector identity holds. **NO-GO:** Task 12 uses the middleware adapter described there ("S1 NO-GO adapter"); `EntryAuditCapture`, the collector, writer and handlers are unchanged.

### Task 3: Spike S2, before values via `IRecordStorage` (GO/NO-GO)

Changed by SEC-04: the first draft read `UFRecords.RecordData` through the ambient scope. That read bypasses any at-rest encryption, so it is dropped. The former fallback is now the only path; this spike proves it.

- [x] **Step 1:** In `SpikeProbeFilter`, for `UpdateRecordController` only and **before** `await next()`: resolve the record id (S1), load the record with `IRecordStorage.GetRecordByUniqueId` (or the member S1 shows Forms uses), log `RuntimeHelpers.GetHashCode(record)` and copy its field values into a dictionary. In `SpikeProbeHandler` for `RecordSavingNotification`, log the notification record's hash code and its field values.
- [x] **Step 2:** Edit an entry ten times on SQLite (change Email, then Name, then a multi-value checkbox, then blank Name), then on SQL Server.
- [x] **Step 3: Verify.** Expected: every save returns 2xx in the browser network tab; no `database table is locked` or `SQLite Error 6` in the log; the copied dictionary holds the pre-edit values and the notification the post-edit values; record whether the two hash codes are equal (same instance) and, if so, that the copy taken before `next()` was not changed by Forms' mutation.
- [x] **Step 4: Record:** the field value shapes (single, multi-value, file upload, blank), whether the storage call returned the same instance Forms mutates, and its timing (ms).
- **GO:** pre-edit values captured with no lock. **NO-GO** (lock, or values already mutated): stop and ask Pascal before Task 13; do not fall back to a raw `RecordData` read (SEC-04).

### Task 4: Spike S3, SQLite write ordering (GO/NO-GO)

- [x] **Step 1:** In `SpikeProbeFilter`, after `await next()`, write one dummy row to `formsAuditTrailEntries` through a fresh `IServiceScopeFactory` scope and `FormsAuditDbContext` (then delete it), for update, delete and list requests.
- [x] **Step 2:** On SQLite, do 20 entry edits, 5 single deletes, 2 bulk deletes and 30 list loads with a second browser tab also paging.
- [x] **Step 3: Verify.** Expected: zero `table is locked`, zero `SQLITE_BUSY` in the log; every Forms action returned 2xx.
- **GO:** write synchronously after `await next()`. **NO-GO:** Task 12 moves the write to an `IAsyncResultFilter.OnResultExecutionAsync` after `await next()` (still synchronous). A background queue is out of MVP scope; if that also locks, stop and ask Pascal.

### Task 5: Spikes S4 to S12 (fact-finding with spec fallbacks)

Not gating; each answer selects a fallback already defined in the spec.

- [x] **S4:** the Forms field property for "contains sensitive data" (`Field.ContainsSensitiveData` on 17.6.2; confirm on 18.2.2) and the public API (if any) that checks a user's "view sensitive data" permission. Record the member names. No public check means Task 16 uses the spec 7.5 fallback.
- [x] **S5:** bulk delete via `ExecuteActionController` and permanent form delete (`DeleteFormRecords`): do `RecordDeleting/Deleted` fire per record, with which payload, inside or after the Forms scope? Add a dummy EF write in the `RecordDeleted` probe on SQLite: does it lock? Also record where `ExecuteActionController` takes its record ids and action type (body member names). Record the answers. **Outcome selects Task 14's branch (P13c, Pascal's decision, final):**
  - **GO** (per-record `RecordDeleted` for every delete, no lock): the handler scrubs non-request deletes immediately; request deletes are scrubbed by the capture core after 2xx.
  - **Fallback** (no per-record event for some path, or a lock): request deletes (single and bulk) are still scrubbed in the request from the ids S1/S5 found; **all non-request deletes are left to the daily reconciliation, up to 24 hours**, and the README says so. The T23 test `Deleted_AlwaysScrubs_EvenWithNoWindow` is replaced as Task 14 states.
- [x] **S6:** set a form's record retention to 0 days, wait for Forms' scheduled deletion: is `RecordDeleted` published?
- [x] **S7:** export with the CSV exporter and the "uploaded files" exporter: does `EntrySearchResultFetchingNotification` fire per row for each? How does the generated file name reach the client (result DTO member)?
- [x] **S8 (local, advisory, P13d, P14):** with Playwright (or the browser network panel), time 50 loads of a 100-row entries page with the probe on and off. Record median and p95 of the collection request. This only sizes the hook cost; the 10 ms target is checked on the real pipeline after Tasks 12 and 13b on both providers.
- [x] **S9:** `dotnet add package Umbraco.Forms --version 18.3.0-rc*` in the TestSite only and rebuild: do the ten controller and four notification types still resolve, and does the completeness list change? Revert afterwards.
- [x] **S10 (SEC-04):** add a spike `RecordFetchingNotification` handler that reverses every string value on read (a stand-in for encryption), with `RecordData` stored normally. Edit an entry. Record: do the before values from `IRecordStorage` come back transformed (so the hook ran); does the diff show only the changed field? Then find a reliable startup check for foreign handlers (for example enumerating `IServiceCollection` descriptors for `INotificationHandler<RecordFetchingNotification>` / `INotificationAsyncHandler<...>` at compose end, or resolving them at `UmbracoApplicationStartedNotification`); record the method and whether it sees the spike handler but not ours. **Fallback (P13b):** if no reliable check exists, `IForeignValueHandlerDetector` is not built; the README gets a static notice ("If another package encrypts or transforms entry values, set `StoreEditValues=None`") and the default stays `MaskSensitive`. Task 13 then drops the detector tests and Task 15 drops `ForeignValueHandlers`.
- [x] **S11 (SEC-01, escalated UNKNOWN; 18.2.2 first, then 17.6.2):** for every public type in `Umbraco.Forms.Web.Api.ManagementApi.{Record,Export,Member,Media}` (23 types on both versions, listed in spec 7.3), call its endpoint from the backoffice or with the browser's bearer token and record whether the response holds entry field values, record ids or files. Then: find Forms' configured upload path; open `Jane_Smith_CV.pdf` from an entry; add a spike middleware via `UmbracoPipelineFilter` in each of the candidate positions (before Forms' `ProtectFormUploadRequestsMiddleware`, after it, after static files) and log the path, the final status and whether it ran, as admin and as a user without access to the form (expect 403 from Forms). Repeat the open with a `Range: bytes=0-99` header (206) and with `If-None-Match` / `If-Modified-Since` from the first response (304), and record whether Forms authorises those before answering. Record how Forms resolves the form id from the path, Forms' middleware full type name and namespace, and the pipeline anchor our filter hooks (the `UmbracoPipelineFilter` stage, or Forms' own filter name) for the contract test (P9).
- [x] **S11 on 17.6.2 (P13e):** `git worktree add ../FormsAuditTrail-v17-spike origin/v17`; in it, `dotnet new install Umbraco.Templates::17.8.0` and create a throwaway site (CMS 17.8.0, Forms 17.6.2, SQLite) outside `src/`, with the S11 spike middleware and the 23-type calls copied in. Run the same S11 steps and fill the 17.6.2 half of the S11 row. Delete the worktree afterwards.
- [x] **S12 (SEC-09, SEC-I5):** on SQL Server, delete the package's tables and migration history, then start two TestSite instances on different ports against the same DB within one second. Record: migrations applied once, no duplicate-object error, and the EF Core version resolved. Then start once with a DB login that has `db_datareader`/`db_datawriter` but no DDL rights: record the logged exception and that the site still serves pages. **On a race (P13g):** Task 10a wraps `MigrateAsync` in an application lock (`sp_getapplock` on SQL Server, a `BEGIN IMMEDIATE` guard on SQLite) with a test, and the README notes it; if the lock itself fails, stop and ask Pascal.
- [x] **Step: Verify.** M0 results table has a filled row for S1 to S12 with a yes/no and the fallback chosen. S11 has a row per type with "audited" or "NotAudited: <reason>"; any type that returns entry values or files is audited. If S11 changes the audited set, update spec 7.3 and `FormsControllerMap` in Task 10 before starting it.
- [x] **Step: Close the spike.** Back in the main checkout on `fc/m0-setup` (where the results already are); keep the spike worktree and branch locally until 18.1.0 ships, never push it.
- [x] **Checkpoint (Pascal commits):** `git add docs/plan.md` / suggested message `docs: record M0 spike results`
- [ ] **PR (Pascal):** push `fc/m0-setup`, open the PR with the M0 results table in the description, review the spikes, rebase-merge. M1 starts from the merged `main`.

---

## M1: schema, options, retention and scrub

Branch `fc/m1-schema` from the merged `main`. One PR for Tasks 6 to 9, rebase-merged.

### Task 6: Dependency pins, client floor and contract tests

**Files:**
- Modify: `PKG/Umbraco.Community.FormsAuditTrail.csproj` (pins, audit mode; the version properties came in Task 1)
- Modify: `.github/workflows/build.yml`
- Modify: `CLIENT/package.json`, `CLIENT/package-lock.json` (`@umbraco-cms/backoffice` `^18.3.0`)
- Test: `TESTS/FormsContractTests.cs`, `TESTS/FormsManagementApiCompletenessTests.cs`

**Interfaces:**
- Consumes: Task 1 `UmbracoCmsVersion` / `UmbracoFormsVersion`.
- Produces: `FormsControllerMap.Audited` and `FormsControllerMap.NotAudited` are consumed here by name only; Task 10 creates them. Until Task 10, the completeness test holds its own copy of the two lists and Task 10 replaces it with the real map (one-line change).

- [ ] **Step 1: Write the failing tests** `FormsContractTests` with one `[Fact]` per type, using the full names recorded in S1, and `FormsManagementApiCompletenessTests` (SEC-01, T1):

```csharp
[Theory]
[MemberData(nameof(ControllerTypes))] // the 10 audited types via typeof(...)
public void FormsController_IsPublicAndAController(Type t) {
    Assert.True(t.IsPublic);
    Assert.True(typeof(ControllerBase).IsAssignableFrom(t));
}
[Fact] public void EntrySearchResultFetchingNotification_ExposesUniqueId() =>
    Assert.NotNull(typeof(EntrySearchResultFetchingNotification).GetProperty(/* record member from S1 */));
[Fact] public void RecordSavingNotification_ExposesRecordWithIdUniqueIdFormAndRecordFields() { /* reflection on members read in Task 13 */ }
[Fact] public void RecordDeletingAndDeleted_ExposeDeletedRecords() { /* members read in Task 14 */ }
[Fact] public void FormsFieldModel_HasSensitiveFlag() { /* member from S4 */ }
[Fact] public void ProtectFormUploadRequestsMiddleware_NamespaceAndAnchor() // P9: full name and namespace from S11,
  // and the pipeline anchor S11 recorded (stage or Forms' filter name) still exists, so a Forms move breaks CI

// FormsManagementApiCompletenessTests
[Fact] public void EveryFormsEntryApiType_IsAuditedOrAllowListed()
  // typeof(GetCollectionController).Assembly.GetExportedTypes()
  //   .Where(ns starts with "Umbraco.Forms.Web.Api.ManagementApi." + Record|Export|Member|Media)
  // each must be in Audited.Keys or NotAudited.Keys; failure message lists the unknown types
[Fact] public void NotAudited_EveryEntryHasAReason()   // non-empty, single line
[Fact] public void AuditedAndNotAudited_DoNotOverlap()
```

- [ ] **Step 2:** `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx --filter "FormsContractTests|FormsManagementApiCompletenessTests"`. Expected: FAIL only where the local lists or member names disagree with S1/S11 (the floors are already 18.3.0 / 18.2.2 from Task 1). Fix the list from the M0 results, never weaken the test.
- [ ] **Step 3:** In the csproj:
  - replace `<NuGetAuditMode>direct</NuGetAuditMode>` with `<NuGetAuditMode>all</NuGetAuditMode>` and add `<NuGetAuditLevel>low</NuGetAuditLevel>`; add to the comment above it: transitive audit is on because the old floors resolved a High Pkcs advisory that `direct` mode could not see (SEC-05);
  - add the pins with this why-comment: "Pinned so the package never resolves Pkcs <= 10.0.12 (GHSA-846f-f764-76mx High, GHSA-2fmv-rv2c-59x7) or MessagePack < 3.1.9 (GHSA-qhrr-8q5h-9q3h), whatever Umbraco's transitive graph does. MessagePack is 3.1.11 because the CMS floor already needs it (a lower direct floor raises NU1605). Remove a pin only when every supported CMS floor resolves the patched version itself and CI's transitive audit is green without it.":

```xml
<PackageReference Include="System.Security.Cryptography.Pkcs" Version="[10.0.13, 11.0.0)" />
<PackageReference Include="MessagePack" Version="[3.1.11, 4.0.0)" />
```

- [ ] **Step 4:** In `CLIENT`: set `@umbraco-cms/backoffice` to `^18.3.0`, `npm install --package-lock-only`, then `npm ci && npm run build` (wwwroot must be unchanged or rebuilt and committed).
- [ ] **Step 5:** In `build.yml` add `strategy.matrix.leg: [floor, latest]`; the `latest` leg passes `-p:UmbracoCmsVersion=18.* -p:UmbracoFormsVersion=18.*` to restore, build and test. (NuGet resolves the lowest version in range, so the floor leg tests CMS 18.3.0 + Forms 18.2.2.)
- [ ] **Step 6: Verify.** `dotnet restore src/Umbraco.Community.FormsAuditTrail.slnx` prints no `NU1605`, `NU1901`, `NU1902`, `NU1903` or `NU1904`; `dotnet list src/Umbraco.Community.FormsAuditTrail.slnx package --include-transitive | grep -E "Pkcs|MessagePack |Umbraco.Cms.Core |Umbraco.Forms.Core "` shows Pkcs `10.0.13`, MessagePack `3.1.11`, CMS `18.3.0`, Forms `18.2.2`; then `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release` and `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release -p:UmbracoCmsVersion=18.* -p:UmbracoFormsVersion=18.*`. Expected: both `Passed!`, with all existing tests still passing.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/Umbraco.Community.FormsAuditTrail.csproj .github/workflows/build.yml CLIENT/package.json CLIENT/package-lock.json PKG/wwwroot TESTS/FormsContractTests.cs TESTS/FormsManagementApiCompletenessTests.cs` / suggested message `feat: pin patched Pkcs and MessagePack, audit transitive packages, add Forms contract and completeness tests`

### Task 6a: Dependency audit and supply-chain CI (SEC-05, SEC-13, SEC-I3, SEC-I4)

**Files:**
- Create: `build/check-vulnerable.py`, `build/tests/test_check_vulnerable.py`, `build/tests/fixtures/{clean,high,moderate}.json`, `.github/workflows/codeql.yml`, `.github/dependabot.yml`
- Modify: `.github/workflows/build.yml`, `.github/workflows/publish.yml`

**Interfaces:**
- Produces: `python3 -I build/check-vulnerable.py <report.json>`, reading the output of `dotnet list <sln> package --vulnerable --include-transitive --format json`. Exit 1 and print each package, version, severity and advisory URL when any severity is `High` or `Critical`; exit 0 otherwise (Moderate and Low are printed as warnings). Exit 2 on a report it cannot parse, so a format change never passes silently.

- [ ] **Step 1: Write the failing tests** (`python3 -m unittest` from `build/tests`; standard library only):

```python
test_clean_report_exits_0()
test_high_transitive_exits_1_and_names_package()     # T12: canned report, Pkcs 10.0.0 High
test_moderate_only_exits_0_with_warning()
test_unparseable_report_exits_2()
test_dotnet_exit_code_zero_with_findings_still_fails() # the reason the script exists
```

- [ ] **Step 2:** `cd build/tests && python3 -m unittest -v`. Expected: FAIL, module not found.
- [ ] **Step 3:** Implement `check-vulnerable.py`. In `build.yml`, on both matrix legs: restore with `-warnaserror:NU1903,NU1904` (High, Critical; the csproj keeps them as warnings so local builds are not blocked by an advisory published overnight), then `dotnet list ... package --vulnerable --include-transitive --format json > vuln.json` and `python3 -I build/check-vulnerable.py vuln.json`. Add a `build-scripts` job running the unit tests. In the client step add `npm audit --audit-level=high` (SEC-I3, covers `vitest` and Playwright once added).
- [ ] **Step 4 (SEC-13):** pin every `uses:` in `build.yml` and `publish.yml` to a full commit SHA with the tag in a trailing comment (`actions/checkout@<sha> # v5.x.y`). In `publish.yml`: add `environment: nuget`; add a first step that fails unless `git merge-base --is-ancestor "$GITHUB_SHA" origin/main || git merge-base --is-ancestor "$GITHUB_SHA" origin/v17` (fetch both with `fetch-depth: 0`); keep `id-token: write` scoped to the publish job only. **P20:** in `build.yml` add `push: branches: [main, v17]` beside the PR trigger, so every rebase-merged commit is tested on the branch; in `publish.yml`, before pack, (a) re-run the transitive audit (`dotnet list ... --vulnerable --include-transitive --format json` and `python3 -I build/check-vulnerable.py`) and `npm audit --audit-level=high` in `CLIENT`, and (b) fail unless `gh api "repos/$GITHUB_REPOSITORY/commits/$GITHUB_SHA/check-runs"` shows the `build` job of `build.yml` concluded `success` for the tag's commit (`GH_TOKEN: ${{ github.token }}`, `checks: read`). Add `.github/dependabot.yml` for `github-actions` (weekly), `nuget` and `npm` (security updates).
- [ ] **Step 5 (SEC-I4):** add `codeql.yml` (languages `csharp` and `javascript-typescript`, on PRs to `main` and `v17` and weekly), actions SHA-pinned.
- [ ] **Step 6: Verify.** `cd build/tests && python3 -m unittest -v` (all pass); `dotnet list src/Umbraco.Community.FormsAuditTrail.slnx package --vulnerable --include-transitive --format json > /tmp/v.json && python3 -I build/check-vulnerable.py /tmp/v.json; echo $?` gives `0`; `grep -nE "uses: [^@]+@v[0-9]" .github/workflows/*.yml` prints nothing; `grep -n "check-runs\|check-vulnerable\|npm audit" .github/workflows/publish.yml` shows all three; `actionlint` (if installed) is clean. Push the branch and confirm both matrix legs and CodeQL run green.
- [ ] **Pascal (repo settings, not code):** allow rebase merging (and keep it the only merge method for milestone PRs); create the `nuget` environment with yourself as required reviewer and restrict it to `v*` tags; update the nuget.org Trusted Publishing policy to bind to `publish.yml` and environment `nuget`; add a tag ruleset protecting `v*` (no deletion, no update, restrict creation to admins); turn on secret scanning, push protection and Dependabot security updates. Note in the PR when done.
- [ ] **Checkpoint (Pascal commits):** `git add build .github` / suggested message `ci: fail on transitive High advisories, pin actions by SHA, protect publish and add CodeQL`

### Task 7: `EntryAudit` options

**Reuse (2026-11-16):** copy-adapt the options and validator pattern from UserActivity `Configuration/` (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

**Files:**
- Create: `PKG/Configuration/EntryAuditOptions.cs`, `PKG/Configuration/StoreEditValuesMode.cs`, `PKG/Configuration/FormsAuditTrailOptionsSetup.cs`
- Modify: `PKG/Configuration/FormsAuditTrailOptions.cs`, `PKG/Composers/FormsAuditComposer.cs:49-50`
- Test: `TESTS/EntryAuditOptionsTests.cs`

**Interfaces:**
- Produces: `FormsAuditTrailOptions.EntryAudit` (`EntryAuditOptions`, initialised `new()`), with `bool Enabled = true`, `bool LogListViews = true`, `bool LogListRecordIds = false`, `int DeduplicateSeconds = 60`, `StoreEditValuesMode StoreEditValues = MaskSensitive`, `int RetentionDays = 365`, `int EditValueRetentionDays = 90`, `bool CaptureSearchTerms = false`, `List<string> AccessLogUserGroups = []`, `bool KeepClearedValues = false` (SEC-03), `bool FailClosedOnExport = false` (SEC-02), `bool? EnableCsvExport = null` (SEC-L4; null means "use `FormsAuditTrailOptions.EnableCsvExport`"), `bool EmitLogEvents = false`, `bool EmitLogEventRecordIds = false` (SEC-08). `internal const string DefaultAccessLogGroup = "admin"`. `FormsAuditTrailOptions` gains `bool EntryAuditCsvEnabled => EntryAudit.EnableCsvExport ?? EnableCsvExport`. `internal static readonly IReadOnlyList<string> RecordedKeys` lists the `EntryAudit` property names whose changes Task 16a records (all of the above; never a secret).

- [ ] **Step 1: Write the failing tests** (bind through `ConfigurationBuilder().AddInMemoryCollection(...)` and a `ServiceCollection` using the same registration as the composer):

```csharp
[Fact] void Bind_WithNoEntryAuditSection_AppliesSpecDefaults()
  // Enabled true, LogListViews true, LogListRecordIds false, DeduplicateSeconds 60,
  // StoreEditValues MaskSensitive, RetentionDays 365, EditValueRetentionDays 90,
  // CaptureSearchTerms false, AccessLogUserGroups == ["admin"]; RetentionDays (definition) 0; EnableCsvExport true;
  // KeepClearedValues false, FailClosedOnExport false, EmitLogEvents false, EntryAuditCsvEnabled true
[Fact] void Bind_WithConfiguredGroups_DoesNotAppendAdmin()
  // "FormsAuditTrail:EntryAudit:AccessLogUserGroups:0" = "editors"  =>  ["editors"]
[Theory, InlineData("RetentionDays"), InlineData("EditValueRetentionDays"), InlineData("DeduplicateSeconds")]
void Validate_RejectsNegative(string key) // EntryAudit keys only => OptionsValidationException on .Value
[Fact] void Bind_NegativeDefinitionRetentionDays_CoercedToZeroWithWarning()  // P11: "FormsAuditTrail:RetentionDays" = "-1"
  // => .Value succeeds, RetentionDays == 0, one Warning naming the key; existing installs keep "off"
[Fact] void Bind_StoreEditValuesFromString() // "None" / "All" bind
[Fact] void EntryAuditCsvEnabled_FollowsEnableCsvExportUnlessSet() // T20: (true, null)->true; (false, null)->false; (true, false)->false
[Fact] void RecordedKeys_CoverEveryEntryAuditProperty()        // reflection: a new option cannot be added without deciding if it is recorded
```

- [ ] **Step 2:** `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx --filter EntryAuditOptionsTests`. Expected: FAIL, `EntryAuditOptions` not found.
- [ ] **Step 3:** Implement the options classes. `FormsAuditTrailOptionsSetup` implements `IPostConfigureOptions<FormsAuditTrailOptions>` (apply `["admin"]` only when the bound list is empty; the why-comment names the .NET array-append behaviour; coerce a negative definition `RetentionDays` to 0 and log a Warning through an injected `ILogger<FormsAuditTrailOptionsSetup>`, with a why-comment: the released job treats a negative value as off, so failing start-up would break existing installs, P11) and `IValidateOptions<FormsAuditTrailOptions>` (negative `EntryAudit` values fail; the existing keys are never rejected). In the composer chain `.ValidateOnStart()` and register the setup class for both interfaces.
- [ ] **Step 4:** Rerun. Expected: PASS, and the full suite still passes.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/Configuration PKG/Composers/FormsAuditComposer.cs TESTS/EntryAuditOptionsTests.cs` / suggested message `feat: add EntryAudit configuration with validated defaults`

### Task 8: Access tables, migrations and upgrade test

**Reuse (2026-11-16):** read UserActivity `Recorder/RecorderTable.cs`, `RecorderMigrationPlan.cs` and `RecorderSql.cs` for table shape, indexes and the SQLite vs SQL Server split; keep 02's EF Core migrations (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

**Files:**
- Create: `PKG/Models/AccessEventType.cs`, `PKG/Models/FormAccessEntry.cs`, `PKG/Models/FormAccessRecord.cs`, `PKG/Models/FormEntryChange.cs`, `PKG/Models/AuditStateEntry.cs`
- Modify: `PKG/Persistence/FormsAuditDbContext.cs`
- Create (generated): `PKG/Migrations/Sqlite/<ts>_AddEntryAccessAudit.cs` + `.Designer.cs`, `PKG/Migrations/SqlServer/<ts>_AddEntryAccessAudit.cs` + `.Designer.cs`
- Modify (generated): `PKG/Migrations/Sqlite/SqliteFormsAuditDbContextModelSnapshot.cs`, `PKG/Migrations/SqlServer/SqlServerFormsAuditDbContextModelSnapshot.cs`
- Create: `.config/dotnet-tools.json` (`dotnet-ef` 10.0.x)
- Modify: `.github/workflows/build.yml` (has-pending-model-changes step; SQL Server service on the floor leg)
- Test: `TESTS/MigrationUpgradeTests.cs`, `TESTS/SqlServerFactAttribute.cs`, `TESTS/TestDatabase.cs` (add `AddAccess` helper)

**Interfaces:**
- Produces: `AccessEventType { ListViewed = 10, DetailViewed = 11, Edited = 12, Deleted = 13, Exported = 14, ExportDownloaded = 15, AuditExported = 16, AuditViewed = 17, FileViewed = 18, AuditSearched = 19, WorkflowRetried = 20, Created = 21, MemberSummaryViewed = 22, ConfigChanged = 23, AuditGap = 24 }`. Values are never reused.
- `FormAccessEntry { long Id; DateTime Timestamp; int EventType; Guid FormId; string FormName; Guid? RecordId; int RecordCount; Guid UserKey; string UserName; string? Detail; Guid? CorrelationId; ICollection<FormAccessRecord> Records; ICollection<FormEntryChange> Changes }`
- `FormAccessRecord { long AccessId; Guid RecordId; FormAccessEntry? Access }`
- `FormEntryChange { long Id; long AccessId; Guid FieldId; string FieldCaption; string? OldValue; string? NewValue; bool Masked; bool Cleared; DateTime? ValuesScrubbedAt; FormAccessEntry? Access }` (`Cleared`, SEC-03)
- `AuditStateEntry { string Key; string? Value; DateTime UpdatedUtc }` (table `formsAuditTrailState`, `Key` nvarchar(100) PK, `Value` nvarchar(4000)).
- `internal static class AccessColumnLimits { public const int FormName = 500, UserName = 500, FieldCaption = 500, StateKey = 100, StateValue = 4000; }` used by both `OnModelCreating` and the writer's truncation (Task 11), so the two cannot drift.
- `FormsAuditDbContext`: constants `AccessTableName = "formsAuditTrailAccess"`, `AccessRecordsTableName = "formsAuditTrailAccessRecords"`, `EntryChangesTableName = "formsAuditTrailEntryChanges"`, `StateTableName = "formsAuditTrailState"`; `DbSet`s `AccessEntries`, `AccessRecords`, `EntryChanges`, `State`.
- `TestDatabase.AddAccess(AccessEventType type, Guid formId, DateTime timestampUtc, Guid? recordId = null, Guid? userKey = null, IEnumerable<Guid>? childRecordIds = null, IEnumerable<FormEntryChange>? changes = null, string? detail = null, Guid? correlationId = null) -> FormAccessEntry`

- [ ] **Step 1: Write the failing tests** in `MigrationUpgradeTests`:

```csharp
[Fact] async Task Upgrade_From1802Schema_KeepsDefinitionRowsAndAddsAccessTables()
  // in-memory SQLite (no EnsureCreated); IMigrator.MigrateAsync("20260715114743_InitialCreate");
  // raw INSERT 2 rows into formsAuditTrailEntries + 3 into formsAuditTrailChanges;
  // MigrateAsync(); assert the 5 rows are byte-identical, AccessEntries/AccessRecords/EntryChanges/State queryable and empty,
  // GetAppliedMigrations() ends with "_AddEntryAccessAudit"
[Fact] void AddEntryAccessAudit_IsPurelyAdditive()
  // for both contexts: migration.UpOperations all CreateTableOperation or CreateIndexOperation,
  // none touches formsAuditTrailEntries or formsAuditTrailChanges
[Fact] async Task Model_CascadesChildRowsAndRoundTripsUtc()
  // AddAccess with 2 child ids + 1 change; delete the parent via ExecuteDeleteAsync; children gone;
  // Timestamp and ValuesScrubbedAt come back with DateTimeKind.Utc
[SqlServerFact] async Task Upgrade_From1802Schema_OnSqlServer()  // same as the first, real SQL Server, from "20260715114751_InitialCreate" (P6: per-provider id)
[Fact] void SqlServerFact_InCiWithoutConnectionString_DoesNotSkip()  // CI=true, FORMSAUDIT_SQLSERVER unset => Skip is null
```

`SqlServerFactAttribute : FactAttribute` (P6) sets `Skip` only when `FORMSAUDIT_SQLSERVER` is unset **and** `CI` is unset; in CI the test runs and `SqlServerTestDatabase.Create()` throws `"FORMSAUDIT_SQLSERVER must be set in CI"`. Every SQL Server test also carries `[Trait("Category","SqlServer")]`. The test creates and drops a uniquely named database. `build/check-trx.py <results.trx> --category SqlServer --min <n>` (standard library, with a unit test in `build/tests/`) exits 1 unless at least `n` tests in that category executed and all passed.

- [ ] **Step 2:** `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx --filter MigrationUpgradeTests`. Expected: FAIL, types missing.
- [ ] **Step 3:** Add the entities and the `OnModelCreating` config per spec 7.2: `Id` bigint identity; `FormName`/`UserName`/`FieldCaption` max from `AccessColumnLimits`; `Cleared` bit not null default false; `formsAuditTrailState` per the interface above; UTC converter (reuse the existing lambda) on `Timestamp` and `ValuesScrubbedAt`; indexes on `Timestamp`, `FormId`, `RecordId`, `UserKey`, `CorrelationId`, composite `(FormId, Timestamp)`; `FormAccessRecord` PK `(AccessId, RecordId)`, index on `RecordId`; both child tables cascade from `formsAuditTrailAccess`.
- [ ] **Step 4:** From `PKG`: `dotnet tool restore`, then `dotnet ef migrations add AddEntryAccessAudit --context SqliteFormsAuditDbContext --output-dir Migrations/Sqlite` and the same for `SqlServerFormsAuditDbContext --output-dir Migrations/SqlServer`. Read both generated files: only creates.
- [ ] **Step 5:** In `build.yml` add `dotnet tool restore` and `dotnet ef migrations has-pending-model-changes` for both contexts (working directory `PKG`), and a `services: mssql` container with `FORMSAUDIT_SQLSERVER` set on the floor leg, followed by `python3 -I build/check-trx.py <trx> --category SqlServer --min 1` on that leg. Each later task that adds a `[SqlServerFact]` raises `--min` to the new total, so a silently skipped test fails CI.
- [ ] **Step 6: Verify.** `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release` (Expected: PASS, the SQL Server tests skipped locally unless the env var is set; run them once with `FORMSAUDIT_SQLSERVER` pointing at the Task 2 Docker container) and, in `PKG`, both `has-pending-model-changes` commands. Expected: `No changes have been made to the model since the last migration.` twice.
- [ ] **Step 7:** Start the TestSite on a copy of an 18.0.2 database (Task 1's DB before this task, kept as `umbraco/Data/Umbraco.sqlite.db.bak`). Expected log: `Applying Forms Audit Trail database migrations...` then `...applied.`, and the "Forms Audit Trail" dashboard still lists the Task 1 rows.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/Models PKG/Persistence PKG/Migrations .config .github/workflows/build.yml build TESTS` / suggested message `feat: add entry access audit and state tables with additive migrations for SQLite and SQL Server`

### Task 9: Retention, value expiry, erasure scrub and reconciliation

**Reuse (2026-11-16):** copy-adapt batching and failure handling from UserActivity `Recorder/RetentionJob.cs` and `RecorderSql.cs` (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

**Files:**
- Create: `PKG/EntryAudit/ErasureScrubber.cs`, `PKG/EntryAudit/IFormsRecordReader.cs`, `PKG/EntryAudit/UmbracoFormsRecordReader.cs`, `PKG/EntryAudit/AuditStateStore.cs`
- Modify: `PKG/BackgroundJobs/AuditRetentionJob.cs`, `PKG/Composers/FormsAuditComposer.cs`
- Test: `TESTS/AuditRetentionJobTests.cs` (extend), `TESTS/ErasureScrubberTests.cs`, `TESTS/AuditStateStoreTests.cs`

**Interfaces:**
- Produces: `internal static class ErasureScrubber { Task<int> ScrubRecordsAsync(FormsAuditDbContext db, IReadOnlyCollection<Guid> recordIds, DateTime nowUtc, CancellationToken ct); Task<int> ScrubFormAsync(FormsAuditDbContext db, Guid formId, DateTime nowUtc, CancellationToken ct); }`. Both null `OldValue`/`NewValue` and set `ValuesScrubbedAt = nowUtc` on `EntryChanges` whose parent `RecordId` matches (or parent `FormId` matches), skipping rows already scrubbed.
- `public interface IFormsRecordReader { Task<IReadOnlySet<Guid>> FindExistingAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken ct); }`, implemented via `IScopeProvider.CreateScope(autoComplete: true)` and `SELECT UniqueId FROM UFRecords WHERE UniqueId IN (@ids)`. It never reads `RecordData` (SEC-04); the first draft's `ReadRecordDataInAmbientScope` is gone. It throws on any database error; it never returns an empty set to mean "could not read" (SEC-I1).
- `AuditRetentionJob` gains `internal static Task<int> DeleteAccessEntriesOlderThanAsync(FormsAuditDbContext db, DateTime cutoffUtc, int batchSize = 5000)`, `internal static Task<int> ExpireEditValuesAsync(FormsAuditDbContext db, DateTime cutoffUtc, DateTime nowUtc, int batchSize = 5000)` (P8: both loop `OrderBy(Id).Take(batchSize)` set-based statements until a batch affects fewer than `batchSize` rows, so millions of rows never become one statement or one transaction), `internal static Task<int> ReconcileErasuresAsync(FormsAuditDbContext db, IFormsRecordReader reader, DateTime nowUtc, int batchSize = 500)`. Each step's outcome is collected into `JobRunState { DateTime StartedUtc; DateTime EndedUtc; IReadOnlyDictionary<string, string> Steps }` (`"ok:<count>"`, `"skipped"` or `"failed:<exception type>"`) and written through `IAuditStateStore` under `job.lastRun`.
- `public interface IAuditStateStore { Task<string?> GetAsync(string key, CancellationToken ct); Task SetAsync(string key, string json, CancellationToken ct); }`: upsert into `formsAuditTrailState` in a fresh DI scope; `Value` longer than `AccessColumnLimits.StateValue` throws `ArgumentException` (callers keep their JSON small). Registered singleton (it holds only `IServiceScopeFactory`). Consumed by Tasks 10a and 16a.

- [ ] **Step 1: Write the failing tests:**

```csharp
// ErasureScrubberTests
ScrubRecords_NullsValuesAndStampsOnlyThatRecord()      // two records, scrub one; other keeps values
ScrubRecords_KeepsWhoAndWhen()                         // access row, FieldId, FieldCaption, Masked unchanged
ScrubForm_NullsEveryValueForThatFormOnly()
ScrubRecords_AlreadyScrubbed_KeepsOriginalTimestamp()
ScrubRecords_1200Ids_IssuesThreeSetBasedUpdates()       // SEC-10: batches of 500, no per-record update
// AuditRetentionJobTests (existing two tests unchanged)
DeleteAccessEntriesOlderThan_CascadesRecordsAndChanges()
DeleteAccessEntriesOlderThan_12000Rows_ThreeBatchedStatements()  // P8: batchSize 5000, command interceptor counts 3 DELETEs, all 12,000 gone
ExpireEditValues_NullsValuesOlderThanCutoff_KeepsRows() // ValuesScrubbedAt == now; newer rows untouched
ExpireEditValues_12000Rows_ThreeBatchedStatements()             // P8
[SqlServerFact] RetentionSteps_BatchedOnSqlServer()             // P8: Take-based ExecuteDelete/ExecuteUpdate translate on SQL Server
ReconcileErasures_ScrubsOnlyRecordsMissingFromForms()   // reader substitute says 1 of 2 exists
ReconcileErasures_QueriesInBatchesOf500()               // 1,200 ids => 3 FindExistingAsync calls
RunJob_WithDefinitionRetentionOff_StillRunsEntryAuditSteps()   // Review Focus 1: RetentionDays=0, EntryAudit defaults
RunJob_EntryAuditRetentionZero_KeepsAccessRowsForever()
RunJob_EntryAuditDisabled_StillRunsRetention()                // E10
RunJob_OneStepThrows_OtherStepsStillRun()                     // reader throws; expiry still applied
ReconcileErasures_ReaderThrows_ScrubsNothingAndMarksStepFailed() // T22, SEC-I1, Review Focus 8: Error logged, values untouched
ReconcileErasures_ReaderThrowsOnSecondBatch_ScrubsNothing()     // no partial scrub from a run that failed
RunJob_DeletesOver1000AccessRows_LogsWarning()                  // SEC-07
RunJob_WritesJobLastRunStateWithStepOutcomes()                  // SEC-I1, feeds the health signal
// AuditStateStoreTests
Set_ThenGet_RoundTrips(); Set_SameKeyTwice_Upserts(); Set_TooLong_Throws()
```

`RunJob_*` tests build a `ServiceCollection` with the singleton `FormsAuditDbContext` (as `FormDeleteHandlerTests` does) and a substitute `IFormsRecordReader`. The job's constructor gains `IAuditStateStore` and `TimeProvider`; update the existing `AuditRetentionJobTests` construction to match (P15d).

- [ ] **Step 2:** `dotnet test ... --filter "AuditRetentionJobTests|ErasureScrubberTests|AuditStateStoreTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement `ErasureScrubber` with `ExecuteUpdateAsync`. Rework `RunJobAsync` into four independent steps (spec 7.4), each in its own `try/catch` with its own Error log, each skipping when its days value is 0; remove the early return on `RetentionDays <= 0` from the method as a whole. Reconciliation only looks at change rows with `ValuesScrubbedAt == null` and a parent `RecordId`; it collects every batch's missing ids first and scrubs only after all reads succeed, so a failed read scrubs nothing. Log counts at Information when non-zero; Warning when the access-retention step deletes more than 1,000 rows. Write `job.lastRun` at the end, in its own `try/catch`. Register `IFormsRecordReader` as scoped and resolve it from the job's own `CreateAsyncScope()`, never through the job's constructor (the job is a singleton, P2). Register `TimeProvider.System` if the host has none (`TryAddSingleton`). Real `UFRecords` coverage is the E2E `reconciliation.spec.ts` in Task 18b (P5); this task's tests use the substitute reader.
- [ ] **Step 4:** Rerun. Expected: PASS; full suite PASS.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/EntryAudit PKG/BackgroundJobs PKG/Composers TESTS` / suggested message `feat: add access retention, edit value expiry and erasure reconciliation to the daily job`
- [ ] **PR (Pascal):** push `fc/m1-schema`, open the M1 PR to `main`, CI green on both legs (SQL Server trx check included), rebase-merge.

---

## M2: capture pipeline (list, detail, export, download)

Branch `fc/m2-capture` from the merged `main`. One PR for Tasks 10 to 12b; it merges only after the Task 12b security checkpoint.

### Task 10: Collector, controller map, `Detail` shaping and dedupe

**Reuse (2026-11-16):** compare `EntryAuditDeduplicator` with UserActivity `Security/ViewThrottle.cs` and copy what fits (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

**Files:**
- Create: `PKG/EntryAudit/CaptureWindow.cs`, `EntryAuditCollector.cs`, `EntryAuditCollectorAccessor.cs`, `FormsControllerMap.cs`, `EntryAuditDetail.cs`, `EntryAuditDeduplicator.cs`
- Test: `TESTS/EntryAuditCollectorTests.cs`, `TESTS/EntryAuditDetailTests.cs`, `TESTS/EntryAuditDeduplicatorTests.cs`, `TESTS/CompositionTests.cs` (new, P2)
- Modify: `TESTS/Umbraco.Community.FormsAuditTrail.Tests.csproj` (add `Microsoft.Extensions.TimeProvider.Testing`, `Microsoft.AspNetCore.TestHost`)

**Interfaces:**
- Produces:
  - `internal sealed class CaptureWindow { AccessEventType Kind; Guid? FormId; Guid? RecordId; EntryAuditDetail Detail; List<Guid> RecordIds; int FetchedRowCount; List<EntryFieldChange> Changes; List<(Guid FormId, Guid RecordId)> DeletedRecords; IReadOnlyDictionary<Guid, IReadOnlyList<object?>>? BeforeValues; }` (`BeforeValues` is the SEC-04 snapshot set by the filter before `next()`, Task 13)
  - `public sealed class EntryAuditCollector { CaptureWindow? Window { get; } void Open(AccessEventType kind, Guid? formId, Guid? recordId, EntryAuditDetail detail); bool IsOpen(AccessEventType kind); void SetBeforeValues(IReadOnlyDictionary<Guid, IReadOnlyList<object?>> values); void AddFetchedRecord(Guid recordId); void SetFormIdIfMissing(Guid formId); void SetEdit(Guid formId, Guid recordId, IReadOnlyList<EntryFieldChange> changes); void SetCreated(Guid formId, Guid recordId, IReadOnlyList<string> fieldCaptions); void AddDeletedRecord(Guid formId, Guid recordId); }` (`SetCreated` is used by Task 13b, P4) (all mutators are no-ops when no window is open; the collector always keeps fetched ids and the filter decides at write time whether to store them). Declare here, for Tasks 11 and 13: `public sealed record EntryFieldChange(Guid FieldId, string FieldCaption, string? OldValue, string? NewValue, bool Masked, bool Cleared)` (`Cleared`, SEC-03).
  - `public interface IEntryAuditCollectorAccessor { EntryAuditCollector? Current { get; } }`, implementation via `IHttpContextAccessor.HttpContext?.RequestServices.GetService<EntryAuditCollector>()` (null outside HTTP, so workflows and background jobs never capture).
  - `internal static class FormsControllerMap { IReadOnlyDictionary<Type, AccessEventType> Audited; IReadOnlyDictionary<Type, string> NotAudited; }` built with `typeof(...)`: `Audited` holds the ten controllers in spec 7.3 (`ExecuteActionController` maps to `Deleted`, `RetryWorkflowController` to `WorkflowRetried`, `CreateRecordController` to `Created`, `GetMemberFormSummariesController` to `MemberSummaryViewed`); `NotAudited` holds every other public type in the four namespaces with the one-line reason from spec 7.3, as amended by S11. Task 6's completeness test now reads these two dictionaries instead of its local copy.
  - `public sealed record EntryAuditDetail { Filter? Filter; int? Page; int? PageSize; string? ExportType; string? FileName; bool? RecordIdsCaptured; bool? Bulk; string? DeleteKind; bool? FormUnresolved; bool? UserUnresolved; bool? Truncated; IReadOnlyDictionary<string,int>? OriginalLengths; Guid? WorkflowId; IReadOnlyList<string>? FieldCaptions; string? FilePathHash; string? MemberKey; bool? MemberIdentifierUsed; IReadOnlyDictionary<string, ConfigValueChange>? Config; AuditGapInfo? Gap; bool? BeforeUnavailable; }` with `ConfigValueChange(string? Old, string? New)` and `AuditGapInfo(int Count, DateTime FirstUtc, DateTime LastUtc, IReadOnlyList<string> EventTypes)` with `Filter(DateTime? From, DateTime? To, string? State, string? SortBy, string? SortDirection, bool? SearchTermUsed, string? SearchTerm)`; the JSON has `"v":1`, omits nulls, camelCase; `static EntryAuditDetail Shape(EntryAuditDetail raw, bool captureSearchTerms)` drops `SearchTerm` and sets `SearchTermUsed = true` when a term was present and capture is off, caps `FileName` at 260 characters, and keeps `MemberKey` only when it parses as a GUID or integer (else drops it and sets `MemberIdentifierUsed = true`, E16); one method `string ToJson(int maxBytes = 8192)` (no parameterless overload, P19) drops `Filter`, then `OriginalLengths`, then `FieldCaptions` until it fits and sets `truncated: true` (SEC-02); `string FilterHash()` (SHA-256 of the shaped filter JSON plus page).
  - `public sealed class EntryAuditDeduplicator(IOptionsMonitor<FormsAuditTrailOptions> options, TimeProvider time) : IDisposable { bool ShouldWrite(AccessEventType type, Guid userKey, Guid formId, Guid? recordId, string filterHash); }` owning a private `MemoryCache(new MemoryCacheOptions { SizeLimit = 50_000 })`, every entry `Size = 1` (SEC-L6). When the cache is full the entry is not stored and the method returns true (fails towards writing). Expiry is decided from `time.GetUtcNow()` against the timestamp stored in the entry (the cache's own expiry is only a memory backstop), so tests can move the clock (P15a).
  - `CompositionTests` (P2): substitute `IUmbracoBuilder` whose `Services` is a real `ServiceCollection` and `Config` an in-memory configuration; run `new FormsAuditComposer().Compose(builder)`; register substitutes for the Umbraco and Forms services the package consumes **at the lifetimes S1 recorded**; build with `new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }`; resolve from a scope every service descriptor whose implementation type lives in the package assembly, plus `EntryAuditActionFilter` and `UploadAuditMiddleware` through `ActivatorUtilities`. Each later task adds its new types to `ExpectedPackageTypes`, a list the test also asserts is complete by reflection. Deduplicated types: `ListViewed`, `DetailViewed`, `AuditViewed`, `AuditSearched`, `FileViewed`, `MemberSummaryViewed`; always true for every other type and when `DeduplicateSeconds == 0`.

- [ ] **Step 1: Write the failing tests:**

```csharp
Collector_NoWindow_MutatorsAddNothing()
Collector_AddFetchedRecord_CountsRowsAndKeepsIds()
Collector_SetFormIdIfMissing_DoesNotOverwrite()
Detail_ToJson_WritesVersionAndOnlyAllowedKeys()
  // keys ⊆ {v, filter, page, pageSize, exportType, fileName, recordIdsCaptured, bulk, deleteKind, formUnresolved, userUnresolved,
  //         truncated, originalLengths, workflowId, fieldCaptions, filePathHash, memberKey, memberIdentifierUsed, config, gap, beforeUnavailable}
Detail_ToJson_Over8KB_TruncatesAndFlags()            // SEC-02
Detail_Shape_CapsFileNameAt260()
Detail_Shape_MemberEmailDropped_KeyKept()            // E16
FormsControllerMap_HasTenAuditedAndReasonedNotAudited()
Detail_Shape_DropsSearchTermUnlessCaptureOn()
  // raw term "jane@example.com": off => JSON has "searchTermUsed":true and no "jane@example.com"; on => term kept
Detail_FilterHash_DiffersByPageAndFilter_SameForSame()
Dedupe_SameKeyInsideWindow_Skipped()
Dedupe_DifferentPageOrFilter_Written()
Dedupe_NeverAppliesToEditedDeletedExportedOrDownloaded()
Dedupe_ZeroSeconds_AlwaysWrites()
Dedupe_AppliesToAuditSearchedFileViewedAndMemberSummary()
Dedupe_NeverAppliesToWorkflowRetriedCreatedConfigChangedOrGap()
Dedupe_CacheFull_WritesAndDoesNotThrow()            // SEC-L6: SizeLimit reached
Dedupe_AfterDeduplicateSecondsElapse_WritesAgain()   // P15a: FakeTimeProvider advanced 61 s => true
Collector_SetCreated_StoresIdAndCaptionsOnly()       // P4
// CompositionTests
Compose_ValidateScopesAndOnBuild_ResolvesEveryPackageType()   // P2
Compose_ExpectedPackageTypes_CoversEveryRegisteredPackageType()
```

- [ ] **Step 2:** `dotnet test ... --filter "EntryAuditCollectorTests|EntryAuditDetailTests|EntryAuditDeduplicatorTests|CompositionTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement. Register `EntryAuditCollector` scoped, accessor and deduplicator singleton (the deduplicator holds only its own bounded cache, no user state; a why-comment says why it does not use the shared `IMemoryCache`). Point `FormsManagementApiCompletenessTests` at `FormsControllerMap`.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/EntryAudit PKG/Composers TESTS` / suggested message `feat: add entry audit collector, Detail shaping and view dedupe`

### Task 10a: Health signal (SEC-02, SEC-09, SEC-I1)

**Open input (S12):** no-DDL login: the package API returns 500 until the tables exist; form save with missing tables untested.

**Files:**
- Create: `PKG/EntryAudit/EntryAuditHealth.cs`
- Modify: `PKG/NotificationHandlers/RunAuditMigration.cs`, `PKG/Composers/FormsAuditComposer.cs`
- Test: `TESTS/EntryAuditHealthTests.cs`, `TESTS/RunAuditMigrationTests.cs`

**Interfaces:**
- Consumes: Task 9 `IAuditStateStore` and `job.lastRun`.
- Produces: `public sealed class EntryAuditHealth` (singleton, thread-safe with `Interlocked` and a lock for the timestamps): `void RecordWriteSuccess()`, `void RecordWriteFailure(AccessEventType type, Guid formId)`, `bool MigrationsPending { get; set; }`, `int WriteFailuresSinceStart`, `DateTime? LastWriteFailureUtc`, `DateTime? LastWriteSuccessUtc`, `AuditGapInfo? TakePendingGap()` (returns and clears the failures since the last success; called by the writer after a success), and `Task<EntryAuditHealthReport> GetReportAsync(DateTime nowUtc, CancellationToken ct)` returning `EntryAuditHealthReport(bool Healthy, IReadOnlyList<string> Reasons, DateTime? LastWriteFailureUtc, int WriteFailuresSinceStart, DateTime? LastJobRunUtc)`.
- Unhealthy reasons (fixed strings, the dashboard shows them): `"writeFailures"` when `LastWriteFailureUtc > (LastWriteSuccessUtc ?? MinValue)`; `"migrationsPending"`; `"jobNotRun"` when `job.lastRun` is missing more than 48 hours after start-up or older than 48 hours; `"jobStepFailed"` when the last run has a failed step.
- Per-server and in memory by design: a failing database cannot store its own failure. Persistence comes from the `AuditGap` row (Task 11) and `job.lastRun` (Task 9).

- [ ] **Step 1: Write the failing tests:**

```csharp
Health_NoFailures_Healthy()
Health_FailureAfterSuccess_UnhealthyWithWriteFailures()      // T8
Health_SuccessAfterFailure_HealthyAgain_GapReturnedOnce()    // TakePendingGap: count, first, last, event types; second call null
Health_MigrationsPending_Unhealthy()                        // T15
Health_JobNotRunFor48h_Unhealthy(); Health_JobStepFailed_Unhealthy()   // SEC-I1
Health_ConcurrentFailures_CountIsExact()                    // 1,000 parallel RecordWriteFailure
Migration_Throws_AppKeepsRunning_MigrationsPendingTrue()    // T15, existing catch kept
Migration_Succeeds_MigrationsPendingFalse()
```

- [ ] **Step 2:** Run `--filter "EntryAuditHealthTests|RunAuditMigrationTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement. In `RunAuditMigration`, after the existing `MigrateAsync` (inside the existing `try`) set `MigrationsPending = (await db.Database.GetPendingMigrationsAsync()).Any()`; in the existing `catch` set it to true. The catch still never rethrows (a failed migration must not stop the site). Register `EntryAuditHealth` singleton. **Only if S12 showed a race (P13g):** wrap `MigrateAsync` in the application lock S12 names and add `Migration_TwoConcurrentStarts_AppliesOnce` (`[SqlServerFact]`, two contexts migrating in parallel); otherwise add nothing. S12: no race on SQL Server (EF Core 10's own `__EFMigrationsLock` serialises `MigrateAsync`), so nothing to add there. SQLite two-process migration race not run: Task 10a tests it (P13g guard if it races).
- [ ] **Step 4:** Rerun. Expected: PASS; full suite PASS.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/EntryAudit PKG/NotificationHandlers/RunAuditMigration.cs PKG/Composers TESTS` / suggested message `feat: add entry audit health signal for write failures, pending migrations and job runs`

### Task 11: Writer core (row shape, truncation, failure and gap)

**Open input (S12):** no-DDL login: the package API returns 500 until the tables exist; form save with missing tables untested.

**Reuse (2026-11-16):** copy-adapt the never-blocks write path from UserActivity `Recorder/RecorderWriter.cs`, and actor resolution from `Identity/ActorRef.cs` and `UserIdentityResolver.cs` (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

Split from the first draft's Task 11 (P21): this task writes the parent row; Task 11b adds child record ids and export correlation.

**Files:**
- Create: `PKG/EntryAudit/AccessWrite.cs`, `IEntryAuditWriter.cs`, `EntryAuditWriter.cs`
- Modify: `PKG/Composers/FormsAuditComposer.cs`, `TESTS/CompositionTests.cs` (add the writer)
- Test: `TESTS/EntryAuditWriterTests.cs`, `TESTS/EntryAuditWriterSqlServerTests.cs`

**Interfaces:**
- Consumes: Task 8 entities and `AccessColumnLimits`, Task 10 `EntryAuditDetail`, `EntryAuditDeduplicator`, `EntryFieldChange`, Task 10a `EntryAuditHealth`.
- Produces:
  - `public sealed record AccessWrite(AccessEventType EventType, Guid FormId, string? FormName, Guid? RecordId, int RecordCount, Guid UserKey, string UserName, EntryAuditDetail? Detail, IReadOnlyCollection<Guid> RecordIds, IReadOnlyList<EntryFieldChange> Changes);`
  - `public enum AccessWriteStatus { Written, Deduplicated, Failed }` and `public sealed record AccessWriteResult(AccessWriteStatus Status, long? AccessId, bool RecordIdsCaptured);`
  - `public interface IEntryAuditWriter { Task<AccessWriteResult> WriteAsync(AccessWrite write); }`. **No `CancellationToken` parameter, by design:** every database call inside uses `CancellationToken.None`, so a client abort cannot cancel the evidence (SEC-02). A why-comment on the interface says so. The writer never throws; it returns `Failed`.
  - **Registered as a singleton (P2).** Its constructor takes only singletons (`IServiceScopeFactory`, `EntryAuditDeduplicator`, `EntryAuditHealth`, `IOptionsMonitor<FormsAuditTrailOptions>`, `ILogger<EntryAuditWriter>`); `FormsAuditDbContext` and `IFormService` are resolved inside the per-write scope. A why-comment says the writer is a singleton because the singleton `ConfigChangeRecorder` (Task 16a) consumes it.
- Behaviour (spec 7.1 step 5):
  - Fresh `IServiceScopeFactory.CreateAsyncScope()` per `WriteAsync` and `FormsAuditDbContext` (same pattern as `FormSavedHandler`). `Timestamp = DateTime.UtcNow`.
  - Truncate `FormName`, `UserName` and every `FieldCaption` to `AccessColumnLimits`; record the original length in `Detail.OriginalLengths`. `Detail` serialised with `ToJson(8192)`.
  - `Guid.Empty` form with no name gives `FormName = "(unresolved form)"` when `Detail.FormUnresolved`, else `"(access log)"`. A missing `FormName` for a real form id resolves via `IFormService.Get(formId)?.Name`, else `"(deleted form)"`.
  - Save the parent (with its `Changes`) in one `SaveChangesAsync`. In this task `RecordIds` is ignored (Task 11b stores them); `AccessWriteResult.RecordIdsCaptured` is `RecordIds.Count == 0`.
  - On any exception: `health.RecordWriteFailure(type, formId)`, log Error with `userKey`, event type, form id and record count, return `Failed`.
  - On `Written`: `health.RecordWriteSuccess()`; then if `health.TakePendingGap()` returns a gap, write one `AuditGap` row (system user, `FormId = Guid.Empty`, `Detail.gap`) in the same scope, best effort (a failure here is logged, never loops).
  - When `EmitLogEvents`, after `Written`, log one Information event with message template `"FormsAuditTrail.Access {AccessId} {EventType} {FormId} {UserKey} {RecordCount}"` plus `{RecordIds}` only when `EmitLogEventRecordIds` (SEC-08). Never values or `Detail`.
  - M7 adds one `IEventAggregator.PublishAsync` call after the parent save; keep a marked place for it.

- [ ] **Step 1: Write the failing tests:**

```csharp
Write_ListViewed_StoresRowWithoutChildIdsByDefault()
Write_Edited_StoresChangesIncludingClearedFlag()
Write_DuplicateDetailView_ReturnsDeduplicatedAndWritesNothing()
Write_UnresolvedForm_WritesGuidEmptyWithFlag()          // T7
Write_DbThrows_ReturnsFailed_RecordsHealth_LogsErrorWithUserEventFormCount()  // T8
Write_SuccessAfterFailures_WritesOneAuditGapRow()       // E12
Write_LongStrings_TruncatedWithOriginalLengths()        // SQLite: checks the values, not the DB limit
Write_EmitLogEvents_LogsOneEventWithoutValues()         // SEC-08
Write_FormGone_UsesDeletedFormName()
Write_ConcurrentWritesFromOneSingleton_EachUsesOwnScope() // P2: 20 parallel WriteAsync, 20 rows, no shared DbContext error
// EntryAuditWriterSqlServerTests
[SqlServerFact] Write_OverLengthStrings_TruncatesAndWrites()  // T4, Review Focus 6: 600-char form name, caption, user name
```

The first draft's `Write_IgnoresCallerCancellation` is dropped here: with no token parameter it was trivially true. T6 is now proven end to end in Task 12 (P7).

- [ ] **Step 2:** Run `--filter "EntryAuditWriterTests|EntryAuditWriterSqlServerTests|CompositionTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement. Register `IEntryAuditWriter` as a singleton and add it to `CompositionTests.ExpectedPackageTypes`. Raise the CI `check-trx.py --min` by one.
- [ ] **Step 4:** Rerun, then once with `FORMSAUDIT_SQLSERVER` set. Expected: PASS on both.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/EntryAudit PKG/Composers .github/workflows/build.yml TESTS` / suggested message `feat: add singleton entry audit writer with truncation, failure health and gap rows`

### Task 11b: Writer child record ids and export correlation

**Files:**
- Modify: `PKG/EntryAudit/EntryAuditWriter.cs`, `.github/workflows/build.yml` (`--min`)
- Test: `TESTS/EntryAuditWriterBatchTests.cs`, `TESTS/EntryAuditWriterSqlServerTests.cs` (extend)

**Interfaces:**
- Consumes: Task 11 writer and `AccessWrite`.
- Produces (no signature change): `AccessWrite.RecordIds` stored as `formsAuditTrailAccessRecords` children for any event type, including a bulk `Deleted` row (Task 14, P8); `AccessWriteResult.RecordIdsCaptured`; `CorrelationId` on `Exported` and `ExportDownloaded`.
- Behaviour:
  - After the parent save, child record ids in batches of 1,000 with `AutoDetectChangesEnabled = false` and `ChangeTracker.Clear()` after each. If a batch throws, update the parent's `Detail` to `recordIdsCaptured: false`, record a health failure, and return `Written` with `RecordIdsCaptured = false`.
  - `Exported` gets `CorrelationId = Guid.NewGuid()`. `ExportDownloaded` copies `CorrelationId` from the newest `Exported` row with the same `FormId` and `UserKey` whose `Detail.fileName` equals the download's (scan the newest 50 candidates in memory), else null.

- [ ] **Step 1: Write the failing tests:**

```csharp
Write_ListViewedWithIds_StoresChildRows()
Write_Exported_SetsCorrelationAndChildRows()
Write_Download_CopiesCorrelationFromMatchingExport()
Write_Download_NoMatchingExport_NullCorrelation()        // E6, also a different user's export
Write_Download_DoesNotMatchOtherFileName()
Write_ExportWithTenThousandRecordIds_StoresAll()        // Review Focus 5
Write_Export50k_ChildFailure_KeepsParentWithRecordIdsCapturedFalse()  // T5: interceptor fails the 3rd batch
Write_ChildIds_SavedInBatchesOf1000()                   // interceptor counts SaveChanges calls
Write_BulkDeleted_OneParentWithChildRows()              // P8: RecordCount 3, 3 children, RecordId null
// EntryAuditWriterSqlServerTests
[SqlServerFact] Write_Export50k_OnSqlServer_StoresAll()  // P8: 50,000 children, parent first, under 60 s
```

- [ ] **Step 2:** Run `--filter "EntryAuditWriterBatchTests|EntryAuditWriterSqlServerTests"`. Expected: FAIL (children not stored).
- [ ] **Step 3:** Implement. Raise `check-trx.py --min` by one.
- [ ] **Step 4:** Rerun, then once with `FORMSAUDIT_SQLSERVER` set. Expected: PASS on both; full suite PASS.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/EntryAudit .github/workflows/build.yml TESTS` / suggested message `feat: store child record ids in batches and correlate export downloads`

### Task 12: Capture core, action filter and fetch handlers

**Open input (S12):** no-DDL login: the package API returns 500 until the tables exist; form save with missing tables untested.

**Reuse (2026-11-16):** for `TryGetCurrentUser`, read UserActivity `Identity/UserIdentityResolver.cs` (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

**Files:**
- Create: `PKG/EntryAudit/EntryAuditRequestReader.cs`, `PKG/EntryAudit/EntryAuditCapture.cs`, `PKG/EntryAudit/EntryAuditActionFilter.cs`, `PKG/NotificationHandlers/EntrySearchResultFetchingHandler.cs`, `PKG/NotificationHandlers/RecordFetchingHandler.cs`
- Modify: `PKG/Composers/FormsAuditComposer.cs`, `PKG/NotificationHandlers/AuditHandlerHelpers.cs` (add `TryGetCurrentUser`, P19), `TESTS/CompositionTests.cs`
- Test: `TESTS/EntryAuditActionFilterTests.cs`, `TESTS/EntryAuditRequestReaderTests.cs`, `TESTS/FetchHandlerTests.cs`

**Interfaces:**
- Consumes: Tasks 10, 10a, 11 and 11b; S1 parameter sources and user-key claim; S3 timing (GO: after `await next()`; NO-GO: `IAsyncResultFilter`).
- Produces:
  - `internal sealed record CaptureRequest(Type ControllerType, IReadOnlyDictionary<string, object?> RouteValues, IQueryCollection Query, IReadOnlyDictionary<string, object?> Arguments)` and `internal static class EntryAuditRequestReader { (Guid? FormId, Guid? RecordId, EntryAuditDetail Detail) Read(CaptureRequest request, AccessEventType kind); }` using the route, query and argument names recorded in S1. Built from `ActionExecutingContext` by the filter, or from the endpoint and a buffered body by the S1 NO-GO adapter.
  - `public sealed class EntryAuditCapture` (scoped; takes `EntryAuditCollector`, `IEntryAuditWriter`, `IOptionsMonitor<FormsAuditTrailOptions>`, `IBackOfficeSecurityAccessor`, `ILogger<EntryAuditCapture>`) with `bool TryBegin(CaptureRequest request)` (false when the type is not in `FormsControllerMap.Audited` or `!EntryAudit.Enabled`; else opens the window) and `Task<CaptureOutcome> CompleteAsync(bool success)` returning `CaptureOutcome(AccessWriteResult? Result, bool ReplaceWith503)`. All work after `TryBegin` is inside one `try/catch`; it never throws.
  - `public sealed class EntryAuditActionFilter : IAsyncActionFilter`: builds the `CaptureRequest`, calls `TryBegin`; on false `await next()` and return. Else `await next()`, `CompleteAsync(IsSuccess(executed))`, and when `ReplaceWith503` sets `executed.Result = new StatusCodeResult(503)`.
  - `internal static bool IsSuccess(ActionExecutedContext executed)`: no unhandled exception and either `ObjectResult`/`StatusCodeResult` with null or 2xx status, or any `FileResult`.
  - `AuditHandlerHelpers.TryGetCurrentUser(IBackOfficeSecurityAccessor accessor, out Guid key, out string name) -> bool` (P19): false when `BackOfficeSecurity?.CurrentUser` is null. The existing `GetCurrentUser` calls it and keeps returning `(Guid.Empty, "System")` for jobs. `EntryAuditCapture` uses `TryGetCurrentUser` and records `(Guid.Empty, "Unknown", userUnresolved: true)` on false, never by comparing names, so a user named "System" cannot pass as unresolved (SEC-L5). Under the S1 NO-GO adapter the key comes from the S1 claim on `HttpContext.User` instead.
- **S1 NO-GO adapter (P13a), built only if S1 said NO-GO:** `EntryAuditEndpointMiddleware`, registered with an `UmbracoPipelineFilter` at the post-routing stage so `HttpContext.GetEndpoint()` is set. It replaces the filter and keeps `EntryAuditCapture`, collector, handlers and writer unchanged. What it loses from the MVC action context, and the replacement for each:
  - Controller type: `ControllerActionDescriptor` from endpoint metadata, `ControllerTypeInfo` against the same map.
  - `ActionArguments`: route values and query come from `HttpContext.Request`; for the controllers whose ids S1 found only in the body (bulk delete, create), call `Request.EnableBuffering()`, read the JSON body up to 1 MB with `System.Text.Json`, rewind `Body.Position = 0`, then `await next()`. A larger or unreadable body gives `FormUnresolved` or `RecordIdsCaptured = false`, never an exception.
  - Result type: success is the final `Response.StatusCode` in 200 to 299 after `next()`.
  - User: `HttpContext.User` after `next()` (authorization has authenticated the policy scheme by then), key from the S1 claim; no claim gives "Unknown".
  - Before snapshot: unchanged, taken before `next()` with the record id from the route.
  - `FailClosedOnExport`: only when it is true and the kind is `Exported` or `ExportDownloaded`, swap `Response.Body` for a `FileBufferingWriteStream` before `next()`; after the write, copy it to the real body on success or reset the response and return 503 on `Failed`. With the option off, nothing is buffered.
  - Tests: the filter test list below runs against both adapters through a shared `[Theory]` source; add `Middleware_BodyOver1MB_WritesRowWithRecordIdsCapturedFalse` and `Middleware_FailClosedOff_DoesNotBufferResponse`.

- [ ] **Step 1: Write the failing tests** (fake `ActionExecutingContext` with a `ControllerActionDescriptor` whose `ControllerTypeInfo` is each Forms type; substitute writer, real collector, except where a test says real writer):

```csharp
Filter_UnrelatedController_CallsNextOnlyAndTouchesNothing()
Filter_Disabled_DoesNoWork()                                // E10: no window, no writer call
Filter_ListViewed_WritesOneRowWithRowCountFromHandler()     // 3 fetch notifications => RecordCount 3
Filter_LogListViewsFalse_WritesNoListRow()
Filter_DetailViewed_WritesRecordIdFromRoute()
Filter_DetailViewed_ResolvesFormIdFromRecordFetching()
Filter_UnresolvedFormId_WritesGuidEmptyRowWithFormUnresolvedAndLogsWarning()  // T7, replaces "writes nothing"
Filter_NoResolvableUser_RecordsUnknownWithFlag_NotSystem()                    // SEC-L5
Filter_UserNamedSystem_IsNotTreatedAsUnresolved()                             // P19
Filter_Exported_StoresFileNameExportTypeAndIds()
Filter_ExportWithNoFetchedRows_SetsRecordIdsCapturedFalse()  // E5 exporter gap
Filter_Download_WritesExportDownloaded()
Filter_ListViewed_LogListRecordIds_CapsAt500_SetsRecordIdsCapturedFalse()      // SEC-10
Filter_RetryWorkflow_WritesWorkflowRetriedWithWorkflowIdNoValues()            // T3
Filter_RetryWorkflow_NeverDeduplicated()
Filter_MemberSummary_WritesGuidEmptyRow_MemberKeyOnlyWhenKey()                // E16
Filter_FailClosedOnExport_WriterFailed_Returns503_NoFileName()               // T9: Exported and ExportDownloaded
Filter_FailClosed_BuilderThrows_Returns503ForExports()                       // P16: AccessWrite construction throws
Filter_FailClosedOnExport_Off_WriterFailed_ResponseUnchanged()
Filter_FailClosedOnExport_DoesNotAffectEditOrList()
Filter_ExecuteAction_NonDeleteBulkAction_WritesNothing()                     // P16: approve/reject bulk
[Theory] Filter_Non2xxOrException_WritesNothing(int? status, bool throws)  // 400, 404, 500, exception
Filter_WriterFailed_ResponseUnchanged()                     // E12; the writer logs and records health (Task 11)
Filter_RequestAbortedCancelled_RealWriter_RowStillWritten() // T6 rewritten (P7): HttpContext.RequestAborted already cancelled, real singleton writer on SQLite, row present
Reader_ListViewed_DropsSearchTermByDefault()
FetchHandler_NoWindow_ReturnsImmediately()
FetchHandler_FrontEndSubmissionPath_NoCollector_NoThrow()   // accessor returns null
```

- [ ] **Step 2:** Run `--filter "EntryAuditActionFilterTests|EntryAuditRequestReaderTests|FetchHandlerTests|CompositionTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement per spec 7.1 request flow, with the logic in `EntryAuditCapture` and the filter as a thin adapter. An unresolved form id writes `Guid.Empty` with `FormUnresolved = true` and a Warning (never skips). `ListViewed` keeps child ids only when `LogListRecordIds`, at most 500, else none and `RecordIdsCaptured = false`. `ExecuteActionController` writes only when S5's action type is delete; any other bulk action writes nothing (Task 14 adds the delete row). `Created` is handled in Task 13b (P4); until then a `Created` window writes nothing. If building the write throws or the result is `Failed`, the kind is `Exported` or `ExportDownloaded`, and `FailClosedOnExport` is true, `ReplaceWith503` is true (a why-comment cites SEC-02 and says this is the only place the package changes a Forms response). `Edited` and `Deleted` writes are added in Tasks 13b and 14 (the core must already skip `Edited` with no changes). Register globally: `builder.Services.Configure<MvcOptions>(o => o.Filters.Add<EntryAuditActionFilter>())`, `EntryAuditCapture` scoped, plus the two notification handlers; add them to `CompositionTests.ExpectedPackageTypes`.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5: Verify on the TestSite.** List the entries, open one, export, download. Then `sqlite3 src/Umbraco.Community.FormsAuditTrail.TestSite/umbraco/Data/Umbraco.sqlite.db "select EventType, RecordId, RecordCount, Detail, CorrelationId from formsAuditTrailAccess order by Id"`. Expected: rows `10`, `11`, `14`, `15`; the 15 row's `CorrelationId` equals the 14 row's; no search term text in any `Detail`. Re-run the entry's workflow: a `20` row with `workflowId` and an email in smtp4dev. Open a member's form summary: a `22` row with `FormId` all zeros. The site starts in `Development` with no DI validation error (P2).
- [ ] **Step 6: Overhead check (P14; local, advisory, not a CI gate).** Run S8's 50-load measurement against the real filter with the package on and with `EntryAudit:Enabled=false`, on SQLite and SQL Server. Record median and p95 in the PR. **Above 10 ms median difference:** profile first; if the synchronous write dominates, stop and ask Pascal, proposing a bounded in-process queue for `ListViewed` and `DetailViewed` only (edits, deletes, exports and downloads stay synchronous). A queue is a design change (spec 7.1 keeps it out of MVP), so it is not built without his OK.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/EntryAudit PKG/NotificationHandlers PKG/Composers TESTS` / suggested message `feat: record entry list views, opens, exports, downloads, workflow re-runs and member lookups`

### Task 12a: Uploaded-file capture middleware (SEC-01, E13)

**S9 (Forms 18.3.0):** type, namespace and filter anchor unchanged, but upload 403/206/304 were not re-run on 18.3.0: re-check them on 18.3.0 before relying on the anchor there.

**Files:**
- Create: `PKG/EntryAudit/UploadPathResolver.cs`, `PKG/EntryAudit/UploadAuditMiddleware.cs`
- Create: `PKG/EntryAudit/UploadAuditPipeline.cs` (the one place our middleware is added to the pipeline)
- Modify: `PKG/Composers/FormsAuditComposer.cs`, `TESTS/CompositionTests.cs`
- Test: `TESTS/UploadAuditMiddlewareTests.cs`, `TESTS/UploadPathResolverTests.cs`, `TESTS/UploadMiddlewarePipelineTests.cs` (P9)

**Interfaces:**
- Consumes: S11 (upload path source, middleware position, form-id resolution); Tasks 10, 10a, 11.
- Produces:
  - `public interface IUploadPathResolver { bool IsUploadRequest(PathString path); Guid? ResolveFormId(PathString path); string HashPath(PathString path); }`. The upload root is the literal `/media/forms/upload/`: Forms has no option for it and hard-codes it in `ProtectFormUploadRequestsMiddleware` (S11, docs/spikes.md), so the resolver holds one constant and a contract test pins it. `IsUploadRequest` matches the way Forms does (split on `/`, empty segments dropped, `media`, `forms`, `upload` compared case-insensitively), so `//media/forms/upload/...`, which Forms serves, is matched. `ResolveFormId` takes the first path segment starting with lower-case `form_` and parses the rest as a Guid, as Forms does (`FORM_` gets 403 from Forms). `HashPath` is lowercase hex SHA-256 of the path relative to the upload root, normalised (forward slashes, lower case, no query).
  - `public sealed class UploadAuditMiddleware(RequestDelegate next)` with `InvokeAsync(HttpContext ctx, IUploadPathResolver resolver, IEntryAuditWriter writer, EntryAuditDeduplicator dedupe, IOptionsMonitor<FormsAuditTrailOptions> options, ILogger<UploadAuditMiddleware> logger)`: non-upload path or `!Enabled` → `await next(ctx)` and return. Otherwise `await next(ctx)`, then if `ctx.Response.StatusCode` is 200, 206 or 304 write `FileViewed` (deduplicated, so the several range requests of one open give one row; 304 counts as an open because the user opened the file again from cache, and "a rare duplicate is acceptable, a missing row is not", spec 7.3; Forms authorises before answering 206 and 304, S11: confirmed) (`RecordCount = 1`, `RecordId = null`, `Detail.FilePathHash`, user from the backoffice cookie read the way Forms reads it (`UmbracoBackOffice` scheme cookie, unprotected with its `TicketDataFormat`), because `HttpContext.User` is not authenticated at `PrePipeline` (S11, docs/spikes.md), `FormUnresolved` when needed). Every step after `next` is inside `try/catch`; the response is never touched.
  - `internal static class UploadAuditPipeline { void Use(IApplicationBuilder app); UmbracoPipelineFilter CreateFilter(); }`: `CreateFilter` registers `Use` at `PrePipeline`, inserted immediately before the `UmbracoPipelineFilter` whose `Name` is `ProtectFormUploadRequestsMiddleware` (`PostConfigure<UmbracoPipelineOptions>`, index lookup by name), so a Forms 403 is observed (S11, docs/spikes.md). It must not rely on any later stage: for a served upload the media file provider short-circuits before `PreRouting`, so no later stage runs. A why-comment says the middleware exists because Forms serves uploads outside MVC, so the action filter never sees them.
  - Notes, not run in S11: with `Umbraco:Forms:Security:DisableFileUploadAccessProtection` on, Forms checks nothing but the middleware should still audit served uploads; with a non-default `UmbracoMediaPath`, Forms' literal prefix may not guard uploads at all, and the resolver's behaviour there must be documented in the README.
  - Static `FileViewedMatcher.Matches(string filePathHash, IEnumerable<string> uploadValues, IUploadPathResolver r)` for Task 16's entry history.

- [ ] **Step 1: Write the failing tests** (`DefaultHttpContext`, a terminal delegate that sets the status):

```csharp
Middleware_NonUploadPath_CallsNextOnly()
Middleware_Upload200_WritesFileViewedWithHashNotFileName()   // T2: "Jane_Smith_CV.pdf" never appears in the AccessWrite
Middleware_Upload403FromForms_WritesNothing()               // T2
Middleware_Upload404_WritesNothing()
Middleware_Disabled_WritesNothing()
Middleware_SameFileTwiceInWindow_Deduplicated()
Middleware_WriterFailed_ResponseUnchanged()
Middleware_FormIdUnresolved_WritesFormUnresolved()
Resolver_HashIsStableAcrossCaseAndSlashes()
Resolver_UploadRootIsFormsLiteral()
Resolver_DoubleSlashAndCaseMatchLikeForms()
Resolver_FormIdFromFirstLowerCaseFormSegment()
Matcher_MatchesRecordUploadValue()
// UploadMiddlewarePipelineTests (P9): Microsoft.AspNetCore.TestHost server built as
//   UploadAuditPipeline.Use(app) -> FakeProtectFormUploadRequestsMiddleware (403 unless header X-Test-Allowed) -> terminal (status from query)
// in the order S11 recorded; real middleware, substitute writer
Pipeline_Upload200_WritesFileViewed()
Pipeline_Upload206_TwoRangeRequests_OneDedupedRow()
Pipeline_Upload304_WritesFileViewed()          // the recorded decision
Pipeline_FormsReturns403_WritesNothing()
Pipeline_Upload404_WritesNothing()
Pipeline_ResponseBodyAndHeadersUnchanged()
```

- [ ] **Step 2:** Run `--filter "UploadAuditMiddlewareTests|UploadPathResolverTests|UploadMiddlewarePipelineTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement per spec 7.3 "Uploaded files". Register `IUploadPathResolver` as a singleton (it holds only the upload root constant) and add the middleware to `CompositionTests.ExpectedPackageTypes`.
- [ ] **Step 4:** Rerun. Expected: PASS.
- [ ] **Step 5: Verify on the TestSite.** As admin open `Jane_Smith_CV.pdf` from an entry; as a user without access to the form (create one by hand; Task 18 later seeds `e2e.editor`) request the same URL (expect 403 from Forms). `sqlite3 ... "select EventType, FormId, Detail from formsAuditTrailAccess where EventType = 18"`. Expected: one row, admin only, `filePathHash` present, and `grep -c "Jane_Smith" <<< "$(sqlite3 ... 'select Detail from formsAuditTrailAccess')"` gives `0`.
- [ ] **Checkpoint (Pascal commits):** `git add PKG/EntryAudit PKG/Composers TESTS` / suggested message `feat: record uploaded file opens from Forms' upload path`

### Task 12b: Security checkpoint after M2 (Pascal's build approach, P21)

Not code. Runs before the M2 PR merges, in a fresh context.

**Files:** none changed by the reviewer; fixes go into the M2 branch as their own checkpoints.

- [ ] **Step 1:** Dispatch `security-analyst` (model `opus`) on the `fc/m2-capture` diff against `main`: `EntryAuditCapture`, `EntryAuditActionFilter`, `EntryAuditRequestReader`, `EntryAuditDetail`, `EntryAuditDeduplicator`, `EntryAuditCollector`, `EntryAuditWriter`, `EntryAuditHealth`, `UploadAuditMiddleware`, `UploadPathResolver`, `UploadAuditPipeline`, the composer and their tests. Brief it with the Global Constraints, spec 7.1 and 7.3, `security-review.md` SEC-01, SEC-02, SEC-04, SEC-10, SEC-L5, SEC-L6 (to confirm each is closed in code, with this-session tool evidence), and `~/.claude/references/umbraco-pitfalls.md`. Ask for findings as Critical / High / Medium / Low with file and line.
- [ ] **Step 2:** Critical findings are fixed on the branch before merge; they cannot be waived. High findings are fixed, or accepted by Pascal as residual risk in the PR description, before M3 starts. Medium and Low go in the PR description with a fix task or a reason.
- [ ] **Step 3: Verify.** `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release` passes after the fixes, and the PR description lists every finding with its outcome.
- [ ] **PR (Pascal):** push `fc/m2-capture`, open the M2 PR, CI green, rebase-merge.

---

## M3: edits and deletes

Branch `fc/m3-edits-deletes` from the merged `main`. One PR for Tasks 13 to 14a.

### Task 13: Edit diff units (snapshot, differ, field info, foreign-handler detector)

Split from the first draft's Task 13 (P21): pure units here; the handler and capture wiring are Task 13b.

**Files:**
- Create: `PKG/EntryAudit/RecordValueSnapshot.cs`, `PKG/EntryAudit/EntryEditDiffer.cs`, `PKG/EntryAudit/IFormFieldInfoProvider.cs`, `PKG/EntryAudit/FormFieldInfoProvider.cs`, `PKG/EntryAudit/ForeignValueHandlerDetector.cs`
- Modify: `PKG/Composers/FormsAuditComposer.cs`, `TESTS/CompositionTests.cs`
- Test: `TESTS/RecordValueSnapshotTests.cs`, `TESTS/EntryEditDifferTests.cs`, `TESTS/FormFieldInfoProviderTests.cs`, `TESTS/ForeignValueHandlerDetectorTests.cs`

**Interfaces:**
- Consumes: S2 (value shapes and copy strategy), S4 sensitive member, S10 (detection method, or its fallback).
- Produces:
  - `internal static class RecordValueSnapshot { IReadOnlyDictionary<Guid, IReadOnlyList<object?>> From(Record record); }`: a deep copy of `record.RecordFields` values keyed by field id, so a later mutation of the same instance by Forms cannot change it (SEC-04, S2). Replaces the first draft's `RecordDataParser`; nothing in the package parses `RecordData`.
  - `public sealed record FieldInfo(string Caption, bool IsSensitive);` `public interface IFormFieldInfoProvider { IReadOnlyDictionary<Guid, FieldInfo> GetFields(Guid formId); }` over `IFormService.Get` and the S4 member, registered scoped. Returns an empty dictionary when the form cannot be loaded.
  - `internal static class EntryEditDiffer { IReadOnlyList<EntryFieldChange> Diff(IReadOnlyDictionary<Guid, IReadOnlyList<object?>> before, IReadOnlyDictionary<Guid, IReadOnlyList<object?>> after, IReadOnlyDictionary<Guid, FieldInfo> fields, StoreEditValuesMode mode, bool keepClearedValues); string? FormatValues(IReadOnlyList<object?> values); }` `FormatValues`: none gives null, one gives its string, more than one gives a JSON array string. Masked value literal: `"[masked]"`. A changed field whose new value is null, empty or whitespace gets `Cleared = true`; with `keepClearedValues == false` both values are null (SEC-03).
  - `public interface IForeignValueHandlerDetector { bool HasForeignValueHandlers { get; } }` (singleton), set once at `UmbracoApplicationStartedNotification` by the S10 method. When true and `StoreEditValues != None`, log one Warning at startup. Task 15 exposes it as `config.foreignValueHandlers`. **Under the S10 fallback (P13b) this type is not built** and its two tests are dropped; the README notice from Task 19 stands in.

- [ ] **Step 1: Write the failing tests:**

```csharp
Snapshot_CopiesValues_LaterMutationDoesNotLeak()   // mutate the Record after From(); snapshot unchanged
Snapshot_MultiValueAndUpload_Shapes()               // fixture from S2's recorded shapes
Diff_ReturnsChangedFieldsOnly()
Diff_MaskSensitive_SensitiveFieldStoredAsMaskedBothSides()  // Old == New == "[masked]", Masked true
Diff_MaskSensitive_NormalFieldStoredInClear()
Diff_None_StoresIdsAndCaptionsWithNullValues()
Diff_All_StoresSensitiveInClear_MaskedFalse()
Diff_MultiValue_SerialisedAsJsonArray()          // ["a","b"]
Diff_FieldMissingFromForm_UsesFieldIdAsCaption() // Review Focus 4
Diff_NoValueChanges_ReturnsEmpty()               // approve / reject / state change
Diff_BlankedField_NoValuesStored_ClearedTrue()   // T10, SEC-03
Diff_BlankedField_KeepClearedValues_OldValueKept_ClearedTrue()  // T10
Diff_WhitespaceOnly_CountsAsCleared()
FieldInfo_FormNotLoaded_ReturnsEmpty()
FieldInfo_SensitiveFlagFromS4Member()
Detector_ForeignHandlerRegistered_True_AndWarningLogged()   // T11
Detector_OnlyOurHandlers_False()
```

- [ ] **Step 2:** Run `--filter "RecordValueSnapshotTests|EntryEditDifferTests|FormFieldInfoProviderTests|ForeignValueHandlerDetectorTests|CompositionTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement. Register the provider scoped and the detector singleton; add both to `CompositionTests.ExpectedPackageTypes`.
- [ ] **Step 4:** Rerun. Expected: PASS; full suite PASS.
- [ ] **Checkpoint (Pascal commits):** `git add PKG TESTS` / suggested message `feat: add entry edit snapshot, field diff with masking and cleared values, and foreign value handler detection`

### Task 13b: Edit and create capture through Forms' read path

**Files:**
- Create: `PKG/NotificationHandlers/RecordSavingHandler.cs`, `src/Umbraco.Community.FormsAuditTrail.TestSite/SimulatedEncryptionComposer.cs` (TestSite only, P3)
- Modify: `PKG/EntryAudit/EntryAuditCapture.cs` (before snapshot, `Edited` and `Created` writes), `PKG/Composers/FormsAuditComposer.cs`, `TESTS/CompositionTests.cs`
- Test: `TESTS/RecordSavingHandlerTests.cs`, `TESTS/EntryAuditActionFilterTests.cs` (extend)

**Interfaces:**
- Consumes: Task 13 units; S1 (how `CreateRecordController` returns the new record id), S2 (storage member).
- Produces:
  - `RecordSavingHandler : INotificationHandler<RecordSavingNotification>` per spec 7.3 handler steps 1 to 5. It reads `BeforeValues` from the window and the after values from the notification; it has no database dependency. For a **`Created` window (P4)** it calls `collector.SetCreated(formId, recordId, captions)` with the captions (from `IFormFieldInfoProvider`) of the fields that have a non-empty value, never the values; the insert-path rule (`record.Id <= 0` returns) applies only to `Edited` windows, and a submission with no window does nothing.
  - Capture change: for an `Edited` window, **before** `await next()`, load the record through `IRecordStorage` (S2 member) and call `collector.SetBeforeValues(RecordValueSnapshot.From(record))`. A read failure logs at Error, records a health failure, leaves `BeforeValues` null, and the Forms action still runs (E12). With null `BeforeValues` the handler adds nothing and the core writes an `Edited` row with no changes and `Detail.BeforeUnavailable = true`. `Created` writes one row (`EventType = Created`, `RecordId` = the new id from the S1 source, `Detail.FieldCaptions`, no `Changes`) on 2xx only.
  - `SimulatedEncryptionComposer` (TestSite only, active only when `E2E:SimulateEncryption` is true and the environment is `Development`): a `RecordFetchingNotification` handler that reverses the values of short text fields, standing in for decryption of values stored reversed (P3). Prevalue and upload fields are left alone, because Forms validates them on save and a one-way stand-in cannot round-trip them (S10). It is never in the package. A why-comment says so.

- [ ] **Step 1: Write the failing tests:**

```csharp
Saving_NoEditedWindow_DoesNothing()
Saving_InsertPath_IdZero_EditedWindow_DoesNothing()
Saving_NoWindow_FrontEndSubmission_WritesNothing()    // P4
Saving_CreatedWindow_RecordsCaptionsNotValues()       // P4: "SENTINEL-CREATED" never on the collector
Saving_UsesWindowBeforeValues_NeverReadsDatabase()    // no IFormsRecordReader, no IScopeAccessor dependency
Saving_StoresDiffOnCollector_DoesNotWrite()           // writer substitute receives no calls
Filter_Edited_SnapshotTakenBeforeNext()               // storage substitute called before next(); order asserted
Filter_Edited_BeforeValuesViaRecordStorage_TransformingHandler_DiffCorrect()  // T11, Review Focus 7: storage returns "decrypted" values; only the changed field is stored; no raw value reaches the writer
Filter_Edited_StorageThrows_FormsActionRuns_EditedRowWithBeforeUnavailable()
Filter_Edited_WithChanges_WritesOneEditedRow()
Filter_Edited_NoChanges_WritesNothing()
Filter_Edited_Non2xx_WritesNothing()
Filter_Edited_MaskSensitive_NoClearSensitiveValueReachesWriter()
Filter_Create_2xx_WritesCreatedWithRecordIdAndCaptionsOnly()   // P4, T3
Filter_Create_DetailContainsNoFieldValues()                    // P4
Filter_Create_Non2xx_WritesNothing()                           // P4
```

- [ ] **Step 2:** Run `--filter "RecordSavingHandlerTests|EntryAuditActionFilterTests|CompositionTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement. A why-comment on the snapshot cites SEC-04: reading `RecordData` ourselves would bypass a site's at-rest encryption. Register the handler; add it to `CompositionTests.ExpectedPackageTypes`. Add `SimulatedEncryptionComposer` to the TestSite.
- [ ] **Step 4:** Rerun. Expected: PASS; full suite PASS.
- [ ] **Step 5: Verify on the TestSite (SQLite).** Edit Name and the sensitive Email of one entry, then blank Name in a second edit. `sqlite3 ... "select FieldCaption, OldValue, NewValue, Masked, Cleared from formsAuditTrailEntryChanges"`. Expected: Name row with clear values, Email row `[masked] | [masked] | 1 | 0`, blanked Name row `| | 0 | 1`; `grep -ci "table is locked" .../umbraco/Logs/*.json` gives `0`. Approve the entry: no new `12` row. Create an entry with `CreateRecordController` (bearer token from the browser): one `21` row with captions and no values. With `E2E:SimulateEncryption=true`: one edit stores only the changed field, and the startup log has the foreign-handler Warning (unless the S10 fallback applies).
- [ ] **Step 6: Overhead check (P14; local, advisory).** Repeat Task 12 Step 6 with ten edits per provider added to the run. Same threshold and the same stop-and-ask fallback.
- [ ] **Checkpoint (Pascal commits):** `git add PKG TESTS src/Umbraco.Community.FormsAuditTrail.TestSite` / suggested message `feat: record entry edits through Forms' read path and backoffice-created entries`

### Task 14: Deletes and erasure scrub

**Files:**
- Create: `PKG/NotificationHandlers/RecordDeletingHandler.cs`, `PKG/NotificationHandlers/RecordDeletedHandler.cs`
- Modify: `PKG/NotificationHandlers/FormDeletedHandler.cs`, `PKG/EntryAudit/EntryAuditCapture.cs` (Deleted write and in-request scrub), `PKG/Composers/FormsAuditComposer.cs`, `TESTS/CompositionTests.cs`
- Test: `TESTS/RecordDeleteHandlerTests.cs`, `TESTS/FormDeleteHandlerTests.cs` (extend; `FormDeletedHandler` gains the writer, so update its construction there, P15d), `TESTS/BulkDeleteVolumeTests.cs`

**Interfaces:**
- Consumes: S5 outcome (GO or fallback), payload and the bulk-delete body members; Task 9 `ErasureScrubber`; Tasks 11 and 11b writer.
- Produces:
  - Single delete: one `Deleted` row (`RecordId` set, `RecordCount = 1`, `deleteKind: "single"`).
  - **Bulk delete (P8):** one `Deleted` row per `ExecuteActionController` request (`RecordId = null`, `RecordCount = n`, `deleteKind: "bulk"`, `bulk: true`) with the ids as child rows through Task 11b; entry history finds it through the child row (Task 16 `GetAccess_RecordIdMatchesRowOrChildRow`). If the children fail, the row keeps `recordIdsCaptured: false`, as for exports.
  - Form delete: one `Deleted` row with `deleteKind = "form"`, `RecordId = null`, `RecordCount` = deleted records seen in this request (0 when Forms published none).
  - **In-request scrub, both S5 branches:** after `next()`, `EntryAuditCapture` scrubs the deleted ids of a `Deleted` window set-based (`ErasureScrubber.ScrubRecordsAsync`, batches of 500), in the writer's scope pattern, whenever Forms returned 2xx. Ids come from `RecordDeleting` in the window, or, under the S5 fallback, from the request (route id or the bulk body members S5 recorded). So backoffice single and bulk deletes are scrubbed at once on both branches.
  - `RecordDeletedHandler`, **S5 GO:** inside a window it only adds ids to the collector (the core scrubs them in one set-based pass); outside a window it scrubs the notification's records at once in a fresh scope. **S5 fallback:** it is not registered; non-request deletes are scrubbed by the daily reconciliation, up to 24 hours later (Pascal, final), and Task 19 puts that in the README.

- [ ] **Step 1: Write the failing tests** (the two marked lines depend on the S5 branch; implement only the one that matches the M0 result):

```csharp
Deleting_InWindow_CollectsIdsAndNeverWrites()       // mirrors Deleting_DoesNotWriteToAuditTable
Deleted_ScrubFails_LoggedNotThrown()
// S5 GO only (T23 kept):
Deleted_AlwaysScrubs_EvenWithNoWindow()             // GDPR control independent of request
// S5 fallback only (T23 rewritten, Pascal's decision):
Deleted_NoWindow_LeftToReconciliation_ScrubbedByNextJobRun()
  // a non-request delete leaves values in place; ReconcileErasuresAsync with a reader that no longer finds the id scrubs them;
  // the README lag sentence is asserted by Task 19's README check
Filter_SingleDelete_ScrubsInRequest()                // both branches: values null and ValuesScrubbedAt set before the response returns
Filter_BulkDelete_ScrubsInRequest_SetBased()         // both branches; SEC-10: 1,200 ids => 3 UPDATE statements
Filter_SingleDelete_WritesOneDeletedRowWithUser()
Filter_BulkDelete_WritesOneRowWithChildRecordIds()   // P8, replaces Filter_BulkDelete_WritesOneRowPerRecord
Filter_Delete_Non2xx_WritesNothingAndScrubsNothing()
// BulkDeleteVolumeTests (P8; real writer, SQLite, command interceptor)
Filter_BulkDelete5000_OneParentRowWithChildren_WithinBounds()
  // 1 parent INSERT, 5 child batches, 10 scrub UPDATEs, no per-record statement; completes in under 10 s
// FormDeleteHandlerTests (existing tests unchanged)
Deleted_Form_ScrubsAllEditValuesForThatForm()
Deleted_Form_WritesDeletedAccessRowWithDeleteKindForm()
Deleted_Form_StillWritesDefinitionEntryWhenScrubThrows()
```

- [ ] **Step 2:** Run `--filter "RecordDeleteHandlerTests|FormDeleteHandlerTests|EntryAuditActionFilterTests|BulkDeleteVolumeTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement. `RecordDeletingHandler` only touches the collector. In `FormDeletedHandler`, after the existing definition write and in its own `try/catch`, call `ErasureScrubber.ScrubFormAsync` and write the form `Deleted` access row with the user already in `notification.State`. Register the handlers; add them to `CompositionTests.ExpectedPackageTypes`.
- [ ] **Step 4:** Rerun. Expected: PASS; full suite PASS.
- [ ] **Step 5: Verify on the TestSite.** Delete the entry edited in Task 13b. Expected: its `formsAuditTrailEntryChanges` rows now have null values and a `ValuesScrubbedAt`; a `13` row exists with the user. Bulk delete two entries: one `13` row with `"bulk":true`, `RecordCount` 2 and two `formsAuditTrailAccessRecords` children. Permanently delete a form: a `13` row with `"deleteKind":"form"`, and the definition "Deleted" row still appears in the Form changes view. Then edit an entry, delete its `UFRecords` row with `sqlite3`, restart the TestSite with the job `Delay` reached (or wait 5 minutes): the change rows are scrubbed (the automated version is Task 18b).
- [ ] **Checkpoint (Pascal commits):** `git add PKG TESTS` / suggested message `feat: record entry deletes, one row per bulk delete, and scrub stored edit values on erasure`

### Task 14a: No-values-in-the-database sentinel scan (P7)

Spec 9 requires "no field values in the database when `StoreEditValues=None`". Earlier tasks assert masking at the differ, writer-call and mock level; this task proves it end to end through real components.

**Files:**
- Create: `TESTS/CapturePipelineHarness.cs`, `TESTS/SentinelScanTests.cs`

**Interfaces:**
- Consumes: the composer's real registrations (as `CompositionTests` builds them), `TestDatabase` (SQLite), the real `EntryAuditCapture`, collector, handlers, differ, singleton writer, `UploadAuditMiddleware`. Substitutes only at the Forms boundary: `IRecordStorage`, `IFormService`, `IFormsSecurity`, `IBackOfficeSecurityAccessor`.
- Produces: `CapturePipelineHarness` with `Task RunEditAsync(...)`, `RunListAsync(string searchTerm)`, `RunMemberSummaryAsync(string memberIdentifier)`, `RunCreateAsync(...)`, `RunUploadOpenAsync(string path)`, and `IReadOnlyList<string> ScanTextColumns()` returning every text value in the four new tables (columns found through `PRAGMA table_info`, so a new column is scanned without a test change).
- Sentinels: `SENTINEL-NAME-OLD` / `-NEW` (normal field), `SENTINEL-SENSITIVE-OLD` / `-NEW` (field flagged sensitive), `SENTINEL-CLEARED-OLD` (blanked by the edit), `sentinel-search@example.com` (list search term), `sentinel-member@example.com` (member summary identifier), `SENTINEL_Jane_Smith_CV.pdf` (upload path), `SENTINEL-CREATED` (created-entry value).

- [ ] **Step 1: Write the failing tests:**

```csharp
[Theory, InlineData(StoreEditValuesMode.None), InlineData(StoreEditValuesMode.MaskSensitive), InlineData(StoreEditValuesMode.All)]
Scan_StoreEditValuesMode_OnlyAllowedSentinelsStored(StoreEditValuesMode mode)
  // None: no NAME, SENSITIVE or CLEARED sentinel anywhere
  // MaskSensitive: NAME allowed; SENSITIVE and CLEARED never
  // All: NAME and SENSITIVE allowed; CLEARED never
Scan_KeepClearedValuesOn_ClearedOldValueStoredOnlyThen()
Scan_CaptureSearchTermsOff_SearchTermNowhere()
Scan_CaptureSearchTermsOn_SearchTermOnlyInDetail()
Scan_AnyMode_NeverStoresMemberEmailUploadFileNameOrCreatedValue()
```

- [ ] **Step 2:** Run `--filter SentinelScanTests`. Expected: FAIL to compile (harness missing).
- [ ] **Step 3:** Implement the harness. No production change is expected; a failing scan is a bug in the owning task's code, fixed there with its own test.
- [ ] **Step 4:** Rerun. Expected: PASS; full suite PASS.
- [ ] **Checkpoint (Pascal commits):** `git add TESTS` / suggested message `test: prove no entry values reach the audit tables beyond the configured mode`
- [ ] **PR (Pascal):** push `fc/m3-edits-deletes`, open the M3 PR (note the S5 branch taken), CI green, rebase-merge.

---

## M4: API and dashboard

Branch `fc/m4-api-dashboard` from the merged `main`. One PR for Tasks 15 to 17b.

### Task 15: Access-log policy and config fields

**Reuse (2026-11-16):** copy-adapt UserActivity `Security/UserActivityAuthorizationHandler.cs` and `UserActivityPolicy.cs`. **Register the handler as Singleton (as UserActivity does) or Transient, never Scoped** (UserActivity spikes S6; overrides Step 3 and the Global Constraints DI line) (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

**Files:**
- Create: `PKG/Authorization/AccessLogRequirement.cs`, `PKG/Authorization/AccessLogAuthorizationHandler.cs`, `PKG/Authorization/AccessLogGroupAliasCheck.cs`
- Modify: `PKG/Controllers/FormsAuditApiController.cs` (`GetConfig` only), `PKG/Controllers/Dtos/AuditEntryListDto.cs` (`AuditConfigDto` only), `PKG/Composers/FormsAuditComposer.cs`
- Test: `TESTS/AccessLogAuthorizationHandlerTests.cs`, `TESTS/FormsAuditApiControllerTests.cs` (update `GetConfig_ReflectsCsvExportSetting` to async), `TESTS/EndpointSecurityTests.cs`

**Interfaces:**
- Produces: `public static class AccessLogPolicy { public const string Name = "FormsAuditTrail.AccessLog"; }`. Handler takes the user from `AuthorizationHandlerContext.User` (the backoffice principal the `SectionAccessForms` policy also uses), resolves the user key from its claims (`ClaimsPrincipal.GetUserKey()` or the claim S1 recorded), loads groups through `IUserService`, and succeeds when any group alias is in `EntryAudit.AccessLogUserGroups` (case-insensitive). No principal, no key or no user: fail closed (SEC-L5). `AccessLogGroupAliasCheck` runs at `UmbracoApplicationStartedNotification` and on options change and logs one Warning per configured alias with no matching group. `AuditConfigDto` gains `bool EntryAuditEnabled`, `bool CanReadAccessLog`, `int AccessRetentionDays`, `int EditValueRetentionDays`, `bool EntryAuditCsvEnabled`, and, only when `CanReadAccessLog` (null otherwise): `bool? EntryAuditHealthy`, `List<string>? HealthReasons`, `DateTime? LastWriteFailureUtc`, `int? WriteFailuresSinceStart`, `DateTime? LastJobRunUtc`, `bool? ForeignValueHandlers`, `List<OffPeriodDto>? OffPeriods` (`OffPeriodDto(DateTime FromUtc, DateTime? ToUtc)`; filled by Task 16a, empty until then). `GetConfig` becomes `Task<ActionResult<AuditConfigDto>> GetConfig()` using `IAuthorizationService.AuthorizeAsync(User, AccessLogPolicy.Name)`.

- [ ] **Step 1: Write the failing tests:**

```csharp
Handler_AdminAliasDefault_Succeeds()
Handler_AliasCaseInsensitive_Succeeds()            // "Admin" vs "admin"
Handler_NotInGroup_Fails()
Handler_NoCurrentUser_Fails()
Handler_PrincipalWithoutUserKeyClaim_FailsClosed()   // T21
Handler_DoesNotUseBackOfficeSecurityAccessor()       // constructor has no IBackOfficeSecurityAccessor
AliasCheck_UnknownAlias_LogsWarning()                // T21
GetConfig_HealthFieldsOnlyForAccessLogUsers()        // T8: non-access-log user gets nulls
GetConfig_EntryAuditCsvEnabled_FollowsSplitSetting() // T20
Handler_ConfigChangedAtRuntime_UsesNewList()       // IOptionsMonitor
GetConfig_ReportsEntryAuditFieldsAndAccess()
GetConfig_ReflectsCsvExportSetting()               // existing assertion, now awaited
EveryPackageController_RequiresSectionAccessForms()   // reflection: class-level [Authorize(Policy="SectionAccessForms")], no [AllowAnonymous]
```

`AccessController_RequiresAccessLogPolicy` is written in Task 16, with the controller it checks, so no test is ever skipped (P15c). `FormsAuditApiController`'s constructor gains `IAuthorizationService`, `EntryAuditHealth` and `IForeignValueHandlerDetector` (or not, under the S10 fallback); update every direct construction in `FormsAuditApiControllerTests` (P15d).

- [ ] **Step 2:** Run `--filter "AccessLogAuthorizationHandlerTests|FormsAuditApiControllerTests|EndpointSecurityTests"`. Expected: FAIL.
- [ ] **Step 3:** Implement; register `services.AddAuthorization(o => o.AddPolicy(AccessLogPolicy.Name, p => p.AddRequirements(new AccessLogRequirement())))` and the handler as ~~scoped~~ **Singleton or Transient, never Scoped** (2026-11-16, UserActivity S6) `IAuthorizationHandler`; add the handler and `AccessLogGroupAliasCheck` to `CompositionTests.ExpectedPackageTypes`. The handler reads the user key from the claim S1 recorded. `GetConfig` reads `EntryAuditHealth.GetReportAsync` and `IForeignValueHandlerDetector`.
- [ ] **Step 4:** Rerun. Expected: PASS, nothing skipped.
- [ ] **Checkpoint (Pascal commits):** `git add PKG TESTS` / suggested message `feat: add access-log group policy and config fields`

### Task 16: Access-log read API (list, detail, history, forms, users)

**Reuse (2026-11-16):** for the `AuditViewed` / `AuditSearched` writes, read UserActivity `Security/ViewLogger.cs` and `ViewThrottle.cs` (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

Split from the first draft's Task 16 (P21): the read endpoints and masking here; the streamed CSV export is Task 16b.

**Files:**
- Create: `PKG/Controllers/FormsAuditAccessApiController.cs`, `PKG/Controllers/Dtos/AccessEntryDtos.cs`, `PKG/EntryAudit/ISensitiveDataPermission.cs`, `PKG/EntryAudit/SensitiveDataPermission.cs`, `PKG/EntryAudit/ReadTimeMasker.cs`
- Modify: `PKG/Composers/FormsAuditComposer.cs`, `TESTS/CompositionTests.cs`, `TESTS/EndpointSecurityTests.cs`
- Test: `TESTS/FormsAuditAccessApiControllerTests.cs`, `TESTS/ReadTimeMaskerTests.cs`, `TESTS/AccessApiAuthorizationTests.cs` (P10)

**Interfaces:**
- Consumes: Tasks 8, 10, 11, 12a (`FileViewedMatcher`), 13 (`IFormFieldInfoProvider`), 15; S4 permission check or its fallback.
- Produces: route prefix `umbraco/formsaudit/api/v1/access`, class attributes `[ApiController]`, `[Authorize(Policy = "SectionAccessForms")]`, `[Authorize(Policy = AccessLogPolicy.Name)]`. Actions:
  - `GET ""` `GetAccess(Guid? formId, Guid? userKey, int? eventType, DateTime? fromDate, DateTime? toDate, Guid? recordId, int page = 1, int pageSize = 20, CancellationToken)` returns `PagedResultDto<AccessEntryListDto>`; `pageSize` clamped to 1..100. Writes `AuditSearched` (deduplicated) when `userKey` or `recordId` is set, with the shaped filter in `Detail` and `FormId` = the `formId` filter or `Guid.Empty` (SEC-06).
  - `GET "{id:long}"` `GetAccessEntry` returns `AccessEntryDetailDto`. Writes `AuditViewed` (deduplicated, with the row's form and record id) when the row is `Edited` and any change has `ValuesScrubbedAt == null` (SEC-06).
  - `GET "records/{recordId:guid}"` `GetRecordHistory` returns `List<AccessEntryListDto>`, newest first, including rows whose child record ids contain the id (bulk deletes, exports) and `FileViewed` rows whose `filePathHash` matches one of the record's current upload values (`FileViewedMatcher`, only while the record exists); writes `AuditViewed` with the record's form id.
  - `GET "forms"`, `GET "users"` (with each user's current name).
  - `Cache-Control: no-store` on `{id}` and `records/{recordId}` (SEC-L4).
  - `AccessEntryListDto { long Id; DateTime Timestamp; string EventType; Guid FormId; string FormName; Guid? RecordId; int RecordCount; bool? RecordIdsCaptured; Guid UserKey; string UserName; string? CurrentUserName; string? Detail; Guid? CorrelationId }` (`CurrentUserName` set only when it differs, SEC-L2); `AccessEntryDetailDto : AccessEntryListDto { List<EntryChangeDto> Changes; long? LinkedAccessId; int ChildRecordCount }`; `EntryChangeDto { Guid FieldId; string FieldCaption; string? OldValue; string? NewValue; bool Masked; bool Cleared; DateTime? ValuesScrubbedAt }`.
  - `internal IQueryable<FormAccessEntry> VisibleRows(...)` and `internal Task<bool> CanAccessEveryFormAsync()` on the controller, reused by Task 16b's export so the two cannot drift.
  - `public interface ISensitiveDataPermission { bool CurrentUserCanViewSensitiveData(); }` (scoped)
  - `internal static class ReadTimeMasker { EntryChangeDto Apply(EntryChangeDto change, IReadOnlyDictionary<Guid, FieldInfo> currentFields, bool formLoaded, bool viewerCanSeeSensitive); }`: returns `[masked]` for both values when `Masked`, or the field is sensitive now, or the field is missing, or the form did not load; the last three are lifted only when `viewerCanSeeSensitive` (SEC-12). Under the S4 fallback, `viewerCanSeeSensitive` = "is an access-log user and `StoreEditValues == All`", but missing field or unloaded form still masks.
- Decisions closing spec gaps: `AuditExported` with no `formId` filter, `AuditSearched` with no `formId` filter and `MemberSummaryViewed`, `ConfigChanged`, `AuditGap` and `formUnresolved` rows use `FormId = Guid.Empty`. **`Guid.Empty` rows are returned only to a user who can access every form** (compute once per request: every form id from `IFormService` passes `HasAccessToForm`) (SEC-L3). Record history returns 404 both when no rows exist and when the record's form is inaccessible.

- [ ] **Step 1: Write the failing tests** (seed with `TestDatabase.AddAccess`; substitutes for `IFormsSecurity`, writer, `ISensitiveDataPermission`):

```csharp
GetAccess_FiltersByFormUserEventDatesAndPages()
GetAccess_RecordIdMatchesRowOrChildRow()
GetAccess_InaccessibleFormFilter_Returns403()
GetAccess_HidesRowsForInaccessibleForms()
GetAccess_PageSizeClampedTo100()
GetAccess_WorksWhenEntryAuditDisabled()                // E10
GetAccess_UserKeyOrRecordIdFilter_WritesAuditSearched_Deduped()   // T13
GetAccess_OtherFilters_WriteNothing()
GetAccess_GuidEmptyRows_HiddenFromUserRestrictedToSomeForms()     // T19
GetAccess_GuidEmptyRows_ShownToAllFormsUser()
GetAccessEntry_InaccessibleForm_Returns404()
GetAccessEntry_GuidEmptyRow_RestrictedUser_Returns404()           // P10
GetAccessEntry_IncludesChangesAndLinkedExport()
GetAccessEntry_SensitiveValueMaskedForViewerWithoutPermission()  // StoreEditValues=All, Masked=false, field sensitive
GetAccessEntry_UnscrubbedEdited_WritesAuditViewed_Deduped()       // T13
GetAccessEntry_ScrubbedEdited_WritesNothing()
GetAccessEntry_NoStoreHeader()                                     // T20
Masker_FieldFlaggedSensitiveAfterEdit_Masked()                     // T17
Masker_FieldDeleted_Masked(); Masker_FormNotLoaded_Masked()        // T17
Masker_PermittedViewer_SeesClearUnderAll()                         // T17
Masker_PermittedViewer_FieldDeleted_StillMaskedUnderFallback()
GetRecordHistory_IncludesOpensEditsDeletesExportsAndTheirDownloads()
GetRecordHistory_IncludesBulkDeleteThroughChildRow()               // P8
GetRecordHistory_UnknownAndInaccessible_BothReturn404()          // Review Focus 3
GetRecordHistory_WritesAuditViewed_Deduped()
GetRecordHistory_IncludesFileViewedMatchedByPathHash()
Forms_And_Users_OnlyAccessibleForms()
Users_IncludeCurrentNameWhenChanged()               // SEC-L2
// EndpointSecurityTests
AccessController_RequiresAccessLogPolicy()            // class-level [Authorize(Policy=AccessLogPolicy.Name)] (moved from Task 15, P15c)
// AccessApiAuthorizationTests (P10): TestHost server with the real controller, the real AccessLog policy and handler,
// a stand-in "SectionAccessForms" policy that passes for any authenticated user, and a test auth scheme
Get_Access_NonMemberOfAccessLogGroup_Returns403()
Get_Access_AdminGroupMember_Returns200()
Get_Access_NoUserKeyClaim_Returns403()               // T21, fails closed
```

- [ ] **Step 2:** Run `--filter "FormsAuditAccessApiControllerTests|ReadTimeMaskerTests|EndpointSecurityTests|AccessApiAuthorizationTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement mirroring `FormsAuditApiController` (`RestrictToAccessibleFormsAsync`, `ApplyFilters`, server-side projection), plus the `Guid.Empty` rule. Read-time masking through `ReadTimeMasker` per spec 7.5 step 5, or its fallback when S4 found no public check. Register `ISensitiveDataPermission` scoped and add it to `CompositionTests.ExpectedPackageTypes`.
- [ ] **Step 4:** Rerun the whole suite. Expected: PASS, nothing skipped except `[SqlServerFact]` locally.
- [ ] **Checkpoint (Pascal commits):** `git add PKG TESTS` / suggested message `feat: add access-log API with entry history and fail-closed read masking`

### Task 16a: Config change trail (SEC-07, E17)

**Files:**
- Create: `PKG/EntryAudit/ConfigChangeRecorder.cs`
- Modify: `PKG/Controllers/FormsAuditApiController.cs` (`OffPeriods` in `GetConfig`), `PKG/Composers/FormsAuditComposer.cs`
- Test: `TESTS/ConfigChangeRecorderTests.cs`

**Interfaces:**
- Consumes: Task 7 `RecordedKeys`, Task 9 `IAuditStateStore`, Task 11 `IEntryAuditWriter`, Task 15 `OffPeriodDto`.
- Produces: `public sealed class ConfigChangeRecorder : INotificationAsyncHandler<UmbracoApplicationStartedNotification>, IDisposable` (singleton). At start it compares the current `EntryAudit` values (only `RecordedKeys`, serialised as strings) with `config.entryAudit` in the state store, writes one `ConfigChanged` row (system user, `FormId = Guid.Empty`, `Detail.Config` = changed keys with old and new) when anything differs or the state is missing (first run after upgrade records the starting values with `Old = null`), then saves the new snapshot. It subscribes to `IOptionsMonitor.OnChange` and does the same on every change; the comparison with the stored snapshot absorbs `OnChange` firing twice. Writes run even when `Enabled` is false. Failures are logged at Error and recorded in health, never thrown. Every change is also logged at Warning.
- `internal static IReadOnlyList<OffPeriodDto> ComputeOffPeriods(IEnumerable<FormAccessEntry> configChangedRows)`: pairs `Enabled` true-to-false and false-to-true changes, oldest first; an open period has `ToUtc = null`. `GetConfig` fills `OffPeriods` from `ConfigChanged` rows inside the access retention window, for access-log users only.

- [ ] **Step 1: Write the failing tests:**

```csharp
Start_NoStoredSnapshot_WritesConfigChangedWithStartingValues()
Start_SameAsStored_WritesNothing()
Start_RetentionChangedBetweenRestarts_WritesOldAndNew()    // T14
OnChange_EnabledFalse_WritesRowEvenThoughDisabled()       // T14, E17
OnChange_StoreEditValuesChanged_WritesRow()               // T14
OnChange_FiredTwiceForOneEdit_WritesOneRow()
OnChange_WriterFailed_LoggedNotThrown()
Detail_ContainsOnlyRecordedKeys()
ComputeOffPeriods_PairsOffAndOn_OpenPeriodHasNullTo()
GetConfig_ReturnsOffPeriodsForAccessLogUserOnly()
```

- [ ] **Step 2:** Run `--filter ConfigChangeRecorderTests`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement. Register the recorder as singleton and as the notification handler, and add it to `CompositionTests.ExpectedPackageTypes` (this is the case P2 caught: it consumes the writer, which is a singleton since Task 11). A why-comment says the recorder exists because a config switch-off would otherwise leave a silent gap (SEC-07).
- [ ] **Step 4:** Rerun. Expected: PASS; full suite PASS.
- [ ] **Step 5: Verify on the TestSite.** Set `FormsAuditTrail:EntryAudit:Enabled` to `false` in `appsettings.Development.json` while running, wait 5 s, set it back. `sqlite3 ... "select EventType, Detail from formsAuditTrailAccess where EventType = 23"`. Expected: two rows (plus the start-up row on first run), each with the `enabled` old and new values.
- [ ] **Checkpoint (Pascal commits):** `git add PKG TESTS` / suggested message `feat: record entry audit configuration changes`

### Task 16b: Access-log CSV export

**Reuse (2026-11-16):** copy-adapt the size cap and formula-injection safety from UserActivity `Export/CsvWriter.cs` and `ExportGate.cs`; keep 02's `CsvFormatter` tests (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

**Files:**
- Create: `PKG/Services/AccessCsvWriter.cs`
- Modify: `PKG/Controllers/FormsAuditAccessApiController.cs` (`GET "export"`), `PKG/Services/CsvFormatter.cs` (SEC-L1), `.github/workflows/build.yml` (test filter)
- Test: `TESTS/AccessCsvExportTests.cs`, `TESTS/AccessCsvWriterTests.cs`, `TESTS/CsvFormatterTests.cs` (extend)

**Interfaces:**
- Consumes: Task 16 `VisibleRows`, `CanAccessEveryFormAsync`, DTOs; Task 7 `EntryAuditCsvEnabled`.
- Produces:
  - `GET "export"` (same query parameters as `GetAccess` minus paging): 404 when `EntryAuditCsvEnabled` is false; 403 when `formId` names an inaccessible form; otherwise streams `text/csv` with `Cache-Control: no-store` and writes `AuditExported` (`FormId` = the filter or `Guid.Empty`). Rows come only from `VisibleRows`, so inaccessible forms and, for restricted users, `Guid.Empty` rows are excluded.
  - `internal static class AccessCsvWriter { Task<int> WriteAsync(Stream body, IAsyncEnumerable<AccessEntryListDto> rows, int maxRows = 100_000, CancellationToken ct = default); }` writes the header `Id,Timestamp,Event,FormId,FormName,UserKey,UserName,RecordId,RecordCount,Detail`, one escaped line per row as it arrives (flushing every 1,000 rows), and `# truncated at 100000 rows; narrow the filter` when the cap is hit (SEC-10). Returns the row count. The action returns `new EmptyResult()` after writing to `Response.Body`; the query uses `AsAsyncEnumerable()` with `AsNoTracking()`.
  - `CsvFormatter.Escape` adds `'\r'` to the trigger set and applies the guard when the first non-whitespace character is a trigger (SEC-L1).

- [ ] **Step 1: Write the failing tests:**

```csharp
// AccessCsvExportTests (controller, seeded TestDatabase)
Export_EscapesEveryTextCell_AndHasSpecColumns()  // header "Id,Timestamp,Event,FormId,FormName,UserKey,UserName,RecordId,RecordCount,Detail"; FormName "=HYPERLINK(1)" => "'=HYPERLINK(1)"
Export_HostileFileNameInDetail_AndUserName_Escaped()  // T18
Export_NoStoreHeader()
Export_NeverContainsEditValues()
Export_DisabledByEnableCsvExport_Returns404()
Export_EntryAuditCsvOff_DefinitionCsvOn_AccessCsv404_DefinitionCsv200()   // T20
Export_WritesAuditExported_GuidEmptyFormWhenUnfiltered()
Export_ExcludesInaccessibleFormRows_AndGuidEmptyRowsForRestrictedUser()  // P10
Export_InaccessibleFormFilter_Returns403()                               // P10
// AccessCsvWriterTests
Writer_StopsAtCap_AppendsTruncationLine()           // T16 (cap passed as 1,000)
Writer_Streams_PullsRowsOnlyAsWritten()             // P15b: spy IAsyncEnumerable records, at each MoveNextAsync, the bytes the counting stream has received; never more than one row ahead
[Trait("Category","Slow")] Writer_500kRows_StreamsWithoutBuffering()  // T16 at full size with the same spy; excluded from default CI
// CsvFormatterTests (existing tests unchanged)
Escape_LeadingCarriageReturn_Guarded()              // T18
Escape_LeadingWhitespaceThenEquals_Guarded()        // T18
```

- [ ] **Step 2:** Run `--filter "AccessCsvExportTests|AccessCsvWriterTests|CsvFormatterTests"`. Expected: FAIL to compile.
- [ ] **Step 3:** Implement. The definition CSV is untouched apart from the `CsvFormatter` fix. In `build.yml` run the test step with `--filter "Category!=Slow"` (P15b); the slow test runs locally before beta (Task 19 Step 6).
- [ ] **Step 4:** Rerun. Expected: PASS; full suite PASS.
- [ ] **Checkpoint (Pascal commits):** `git add PKG TESTS .github/workflows/build.yml` / suggested message `feat: add streamed, capped and permission-filtered access-log CSV export`

### Task 17: Client format utilities, API service and lint guard

**Reuse (2026-11-16):** read UserActivity `Client/src/state/activity-filter-state.ts` (URL state) and `components/format.ts` (design "Reuse from User Activity"). New parts only. Log adapted files in COMMITS-TODO.md.

Split from the first draft's Task 17 (P21): pure TypeScript, the API service and the CI guards here; the elements are Task 17b.

**Files:**
- Create: `CLIENT/src/utils/access-format.ts`, `CLIENT/src/utils/access-format.test.ts`, `CLIENT/eslint.config.js`
- Modify: `CLIENT/src/services/audit-api.service.ts`, `CLIENT/package.json` (+ lock), `.github/workflows/build.yml`

**Interfaces:**
- Consumes: Task 15 config fields (including health, `foreignValueHandlers`, `offPeriods`, `entryAuditCsvEnabled`), Tasks 16 and 16b endpoints and DTOs (mirror them as TS interfaces in `audit-api.service.ts` with functions `getAccess`, `getAccessEntry`, `getRecordHistory`, `getAccessForms`, `getAccessUsers`, `accessExportUrl`).
- Produces in `access-format.ts`: `eventTag(eventType: string): { label: string; color: 'default' | 'warning' | 'danger'; look: 'primary' | 'outline' }`, `entryCell(row: AccessEntryListDto): string`, `retentionBanner(cfg: AuditConfigDto): string`, `valueText(value: string | null, masked: boolean, scrubbedAt: string | null, cleared: boolean): string`, `userCell(row: AccessEntryListDto): string`, `healthBanner(cfg: AuditConfigDto): string | null`, `offPeriodText(p: OffPeriodDto): string`, `parseRecordId(raw: string | null): string | null` (GUID or null, SEC-I2).

- [ ] **Step 1:** Add `vitest` as a devDependency and `"test": "vitest run"`. Write the failing tests:

```ts
eventTag('Edited') -> { label: 'Edited', color: 'warning', look: 'primary' }
eventTag('Exported'|'ExportDownloaded') -> danger primary; eventTag('Deleted') -> danger outline
eventTag('ListViewed'|'DetailViewed'|'AuditViewed'|'AuditExported'|'AuditSearched'|'FileViewed'|'MemberSummaryViewed') -> default
eventTag('WorkflowRetried'|'AuditGap') -> danger primary; eventTag('Created'|'ConfigChanged') -> warning primary
entryCell({recordId:'g'}) -> 'g'; ({recordCount:12}) -> '12 entries';
entryCell({eventType:'Exported', recordCount:12, recordIdsCaptured:false}) -> '12 entries (ids not captured)'
entryCell({eventType:'Deleted', recordId:null, recordCount:3}) -> '3 entries'      // bulk delete row (P8)
retentionBanner({accessRetentionDays:365, editValueRetentionDays:90}) ->
  'Entry access is kept for 365 days. Changed values are kept for 90 days.'
retentionBanner({accessRetentionDays:0, ...}) contains 'kept indefinitely'
valueText(null, false, '2026-11-01T00:00:00Z', false) -> 'Value removed on 1 Nov 2026 (retention or erasure)'
valueText('[masked]', true, null, false) -> '[masked]'
valueText(null, false, null, true) -> 'Value cleared'
userCell({userName:'Ann', currentUserName:'Bob'}) -> 'Ann (now: Bob)'; userCell({userName:'Ann'}) -> 'Ann'
healthBanner({entryAuditHealthy:true}) -> null
healthBanner({entryAuditHealthy:false, healthReasons:['writeFailures','migrationsPending'], writeFailuresSinceStart:3, lastWriteFailureUtc:'2026-11-01T09:00:00Z'})
  -> 'Entry auditing has a problem: 3 writes failed since 1 Nov 2026 09:00; database migrations are pending. Some activity may not have been recorded.'
healthBanner({entryAuditHealthy:false, healthReasons:['jobNotRun']})      // P16
  -> 'Entry auditing has a problem: the daily retention and erasure job has not run in 48 hours. Some activity may not have been recorded.'
healthBanner({entryAuditHealthy:false, healthReasons:['jobStepFailed']})  // P16
  -> 'Entry auditing has a problem: a step of the daily retention and erasure job failed. Some activity may not have been recorded.'
offPeriodText -> 'Entry auditing was off from 1 Nov 2026 09:00 to 1 Nov 2026 10:00' (open period: 'to now')
parseRecordId('not-a-guid') -> null; parseRecordId('<valid guid>') -> lower-case guid
```

- [ ] **Step 2:** `cd CLIENT && npm test`. Expected: FAIL, module not found.
- [ ] **Step 3:** Implement `access-format.ts` and the service functions.
- [ ] **Step 4:** Add `eslint` and `typescript-eslint` as devDependencies and `eslint.config.js` with `no-restricted-imports` banning `lit/directives/unsafe-html.js` and `lit-html/directives/unsafe-html.js` (SEC-I2), and `"lint": "eslint src"`. In `build.yml` client step add `npm run lint`, `npm test` and the S3 guard: `! grep -rnE "unsafeHTML|innerHTML" src/`.
- [ ] **Step 5: Verify.** `cd CLIENT && npm run lint && npm test && npm run build && npm audit --audit-level=high && ! grep -rnE "unsafeHTML|innerHTML" src/`. Then add `import { unsafeHTML } from 'lit/directives/unsafe-html.js';` to a scratch file in `src/`, confirm `npm run lint` fails, and delete it. Expected: ESLint clean, Vitest all passed, `tsc` clean, no High npm advisories, grep finds nothing. Commit the rebuilt `PKG/wwwroot/` if it changed.
- [ ] **Checkpoint (Pascal commits):** `git add CLIENT PKG/wwwroot .github/workflows/build.yml` / suggested message `feat: add access-log client utilities, API service and an unsafe-HTML lint guard`

### Task 17b: Dashboard sub-tabs, entry access and entry history

**Reuse (2026-11-16):** copy-adapt the filter bar, table and pager from UserActivity `Client/src/components/` for the new sub-tabs only; 02's existing dashboard stays (design "Reuse from User Activity"). Log adapted files in COMMITS-TODO.md.

**Files:**
- Create: `CLIENT/src/dashboards/audit-root.view.ts`, `CLIENT/src/dashboards/audit-root.view.test.ts`, `CLIENT/src/dashboards/audit-root.element.ts`, `CLIENT/src/dashboards/entry-access-view.element.ts`, `CLIENT/src/dashboards/entry-history-view.element.ts`, `CLIENT/src/components/audit-health-banner.element.ts`
- Modify: `CLIENT/src/manifests.ts`, `CLIENT/src/components/audit-diff-viewer.element.ts`, `CLIENT/src/index.ts`
- Rebuild: `PKG/wwwroot/`

**Interfaces:**
- Consumes: Task 17 utilities and service.
- Produces: `audit-root.view.ts` (P17): `auditRootView(cfg: AuditConfigDto, url: URL, storage: Pick<Storage, 'getItem' | 'setItem'> | null, userKey: string): { tabs: Array<'formChanges' | 'entryAccess' | 'entryHistory'>; initialTab: string; offMessage: string | null; bannerLines: string[]; showGdprNotice: boolean; deepLinkRecordId: string | null }` and `dismissGdprNotice(storage, userKey): void`. The element renders from this object, so tab visibility, banner, off state and notice dismissal are tested in Vitest without mounting the backoffice element context (mounting `@umbraco-cms/backoffice` elements needs the backoffice host, which Vitest does not provide; Task 18 covers the rendered element).

- [ ] **Step 1: Write the failing tests** (`audit-root.view.test.ts`, P17):

```ts
auditRootView({canReadAccessLog:false, entryAuditEnabled:true}) -> tabs ['formChanges']
auditRootView({canReadAccessLog:true,  entryAuditEnabled:true}) -> tabs ['formChanges','entryAccess','entryHistory']
auditRootView(..., url '?recordId=<guid>')  -> initialTab 'entryHistory', deepLinkRecordId lower-case guid
auditRootView(..., url '?recordId=<script>') -> deepLinkRecordId null, initialTab 'formChanges'
auditRootView({canReadAccessLog:true, entryAuditEnabled:false}) -> offMessage 'Entry auditing is switched off. Set FormsAuditTrail:EntryAudit:Enabled to true.'
auditRootView({..., entryAuditHealthy:false, healthReasons:['jobStepFailed'], offPeriods:[p], foreignValueHandlers:true}) -> bannerLines = [healthBanner, offPeriodText(p), foreign-handler notice]
auditRootView(..., storage null)                         -> showGdprNotice true
auditRootView(..., storage whose getItem throws)         -> showGdprNotice true, no throw
dismissGdprNotice(storage whose setItem throws, 'u')      -> no throw
after dismissGdprNotice(storage, 'u'): auditRootView(..., storage, 'u') -> showGdprNotice false; other user 'v' -> true
```

- [ ] **Step 2:** `cd CLIENT && npm test`. Expected: FAIL, module not found.
- [ ] **Step 3:** Implement `audit-root.view.ts`, then the elements, using these fixed strings from spec 7.6:
  - Manifest: alias and pathname unchanged, `meta.label` and `name` `"Audit"`, `elementName: 'forms-audit-root'`.
  - `forms-audit-root`: `uui-tab-group` from `view.tabs`: "Form changes" (renders the existing `forms-audit-dashboard` untouched), "Entry access" and "Entry history". Opens `view.initialTab`.
  - Off state: `view.offMessage`.
  - Footnote: `Records backoffice activity only. Changes made by code, workflows, the Delivery API or directly in the database are not captured, and some Forms screens are not recorded. See the README.`
  - `audit-health-banner` (red, `uui-box` with `role="alert"`): one line per `view.bannerLines` entry. Shown above all tabs to access-log users.
  - Upgrade notice (SEC-11): `Entry auditing is now on. Read the GDPR notes.` with a README link and a Dismiss button calling `dismissGdprNotice`, under `formsAuditTrail.gdprNotice.<userKey>`.
  - Edit diff shows `Value cleared` for cleared fields (via `valueText`). The User column uses `userCell` (SEC-L2). The CSV button is hidden when `entryAuditCsvEnabled` is false.
  - The `?recordId=` deep link uses `view.deepLinkRecordId`; an invalid value leaves the input empty (SEC-I2).
  - History exporter note: `Exports from some exporters don't record which entries they contained. See the Entry access log for all exports of this form.`
  - History 404 message: `No recorded access for this entry, or you don't have access to its form.`
  - Entry access columns: When, User, Event (`uui-tag` from `eventTag`), Form, Entry (`entryCell`, record ids link to the history deep link), Detail (filter summary, export type and file name, or a "View changes" button for edits).
  - `audit-diff-viewer` gains an `accessId` property: when set it loads `access/{id}` and renders a field table (Field, Before, After via `valueText`); focus moves to its heading on open.
  - Record id input has a visible `<label>`; tables keep `uui-table-head-cell`.
  - Every stored string is a Lit text binding.
- [ ] **Step 4: Verify.** `cd CLIENT && npm run lint && npm test && npm run build && ! grep -rnE "unsafeHTML|innerHTML" src/`. Expected: all pass; vite writes `../wwwroot/index.js`; `git status PKG/wwwroot` shows the rebuilt bundle. Then on the TestSite, as admin and as a non-admin, the three tabs and the banner render as the view object says.
- [ ] **Checkpoint (Pascal commits):** `git add CLIENT PKG/wwwroot` / suggested message `feat: add Entry access and Entry history tabs to the Audit dashboard`
- [ ] **PR (Pascal):** push `fc/m4-api-dashboard`, open the M4 PR, CI green, rebase-merge.

---

## M5: E2E, load check, docs, beta

Branch `fc/m5-beta` from the merged `main`. One PR for Tasks 18 to 19, with the E2E output for both providers attached as release evidence (P17).

### Task 18: E2E seed, diagnostics and capture specs

Split (P21; the first draft's Task 18 grew with P3, P4, P5 and P16): capture and permission specs here; reconciliation, health and load are Task 18b.

**Files:**
- Create: `src/Umbraco.Community.FormsAuditTrail.E2E/package.json`, `playwright.config.ts`, `.env.example`, `tests/entry-audit.spec.ts`, `tests/access-log-permissions.spec.ts`, `tests/encryption.spec.ts`, `tests/accessibility.spec.ts`
- Create: `src/Umbraco.Community.FormsAuditTrail.TestSite/E2ESeedComposer.cs`, `src/Umbraco.Community.FormsAuditTrail.TestSite/E2EDiagnosticsController.cs`

**Interfaces:**
- Consumes: Task 1 site; Task 13b `SimulatedEncryptionComposer`; all API endpoints.
- Produces:
  - `E2ESeedComposer` which, when `E2E:Seed` is true, creates once (idempotent by name) form `E2E Contact` (Name; Email flagged sensitive; Interests multi-value; CV file upload; one email workflow) with 3 records, one carrying an uploaded `Jane_Smith_CV.pdf`, and a user group `e2eForms` (Forms section, access to `E2E Contact`, not `admin`) with user `e2e.editor@example.com`. When `E2E:SimulateEncryption` is also true, it seeds form `E2E Encrypted` with one record whose stored string values are reversed (`"htimS enaJ"` for Name), so the simulated decryption on read gives the plaintext (P3).
  - `E2EDiagnosticsController` (TestSite only; active only in `Development` with `E2E:Diagnostics=true`; backoffice-authenticated admin only): `GET /e2e/scan?needle=` returns the table and column of any text value in the four `formsAuditTrail*` access tables containing `needle`; `POST /e2e/run-retention-job` runs `AuditRetentionJob.RunJobAsync` once and returns `job.lastRun`; `POST /e2e/ufrecords/delete?recordId=` deletes one `UFRecords` row directly (simulating a delete that publishes no notification); `POST /e2e/ufrecords/break` and `/restore` rename `UFRecords` away and back on SQLite, or `DENY` / `REVOKE SELECT` on SQL Server (the reconciliation failure case). A why-comment says this controller exists only to test the package against real Forms tables and is never packed.
  - E2E pins `@playwright/test`, `@axe-core/playwright` and the `@umbraco/playwright-testhelpers` release that matches CMS 18.3 (`npm view @umbraco/playwright-testhelpers versions`).

- [ ] **Step 1: Write the specs** (they run against the TestSite, so "failing" means the site has `EntryAudit:Enabled=false`):
  - `entry-audit.spec.ts`: open the entries list, open an entry, edit Name and Email, export CSV, download it, bulk delete two entries. Assert via `GET /umbraco/formsaudit/api/v1/access`: one row each of ListViewed, DetailViewed, Edited, Exported, ExportDownloaded (same `correlationId` as the export), one bulk Deleted row with `recordCount` 2 whose history (`records/{id}`) finds each entry. Edited detail shows Email `[masked]`. After the delete, that entry's changes have `valuesScrubbedAt` set. Also: open the uploaded CV from an entry, expect one `FileViewed` row and no `Jane_Smith` text anywhere in the API response or in `/e2e/scan` (T2); re-run the workflow, expect `WorkflowRetried` (T3); **create an entry by calling `CreateRecordController` with the backoffice bearer token, unconditionally (P4)**, expect `Created` with captions only and `/e2e/scan?needle=<created value>` empty (T3); blank a field, expect `Cleared` and no old value (T10).
  - `access-log-permissions.spec.ts`: as `e2e.editor`: "Form changes" visible, "Entry access" and "Entry history" tabs absent, `GET .../access` and `GET .../access/export` return 403; requesting the CV upload URL directly returns 403 from Forms and writes no `FileViewed` row (T2). As admin: a deep link `?recordId=<id>` opens the history; `?recordId=<script>` leaves the input empty.
  - `encryption.spec.ts` (P3, with `E2E:SimulateEncryption=true`): as admin edit the `E2E Encrypted` record's Name from "Jane Smith" to "Jane Doe" and leave Email unchanged. Expect exactly one `Edited` change row (Name, old "Jane Smith", new "Jane Doe"), no Email row, and `/e2e/scan?needle=htimS` and `?needle=<reversed email>` both empty.
  - `accessibility.spec.ts` (P16): run `AxeBuilder` with tags `wcag2a`, `wcag2aa`, `wcag21aa`, `wcag22aa` on each of the three tabs and on the open diff viewer. Expect zero violations.
- [ ] **Step 2:** Run with `EntryAudit:Enabled=false`: `cd src/Umbraco.Community.FormsAuditTrail.E2E && npx playwright test entry-audit`. Expected: FAIL (no rows).
- [ ] **Step 3:** Enable, seed with `E2E__Seed=true E2E__Diagnostics=true E2E__SimulateEncryption=true dotnet run --project src/Umbraco.Community.FormsAuditTrail.TestSite`, and rerun `npx playwright test`. Repeat once with the TestSite on SQL Server.
- [ ] **Step 4: Verify.** Expected: all four specs passed on SQLite and SQL Server; the TestSite log has zero `table is locked` and zero Error lines from `Umbraco.Community.FormsAuditTrail`. Save both HTML reports (`npx playwright show-report` output folders) for the PR (P17). E2E is run locally before each beta and stable, not in CI (an Umbraco + Forms site in CI is out of MVP scope; note this in the PR).
- [ ] **Checkpoint (Pascal commits):** `git add src/Umbraco.Community.FormsAuditTrail.E2E src/Umbraco.Community.FormsAuditTrail.TestSite` / suggested message `test: add Playwright E2E for entry access capture, permissions, encrypted values and accessibility`

### Task 18b: E2E reconciliation, health and load check

**Files:**
- Create: `src/Umbraco.Community.FormsAuditTrail.E2E/tests/reconciliation.spec.ts`, `tests/health.spec.ts`, `tests/load-check.spec.ts`

**Interfaces:**
- Consumes: Task 18 seed and diagnostics.

- [ ] **Step 1: Write the specs:**
  - `reconciliation.spec.ts` (P5, both providers): (a) edit an entry, `POST /e2e/ufrecords/delete` for it, `POST /e2e/run-retention-job`; expect its change rows to have null values and `valuesScrubbedAt` set, the step `"ok:<n>"` with n ≥ 1, and an untouched second entry's values kept. (b) seed 1,200 records with one edit each (seed endpoint parameter `E2E:SeedBulkEdits=1200`), delete 600 of them through `/e2e/ufrecords/delete`, run the job; expect exactly those 600 scrubbed (more than 500 ids, so at least three reader batches). (c) `POST /e2e/ufrecords/break`, delete another entry's `UFRecords` row first, run the job, then `/restore`; expect nothing scrubbed, the reconciliation step `"failed:..."`, and `GET .../config` showing `healthReasons` containing `jobStepFailed`.
  - `health.spec.ts` (SQL Server run only): `DENY INSERT ON formsAuditTrailAccess TO <site login>` via `sqlcmd`, list entries and edit one: Forms works, no access row, the red health banner shows "writes failed" (T8). `REVOKE` the deny, list again: an `AuditGap` row appears and the banner clears. Skipped on SQLite with a printed reason.
  - `load-check.spec.ts` (local, advisory, P14): 50 loads of a 100-row page with the package on, then with `EntryAudit:Enabled=false` (restart between), plus 10 edits each way. Print median and p95 for both. Fail when the median difference is 10 ms or more, which triggers the Task 12 Step 6 fallback (stop and ask Pascal).
- [ ] **Step 2:** Run against SQLite, then SQL Server, as in Task 18 Step 3.
- [ ] **Step 3: Verify.** Expected: all specs passed on both providers (health skipped on SQLite only); save both reports with Task 18's for the PR (P17).
- [ ] **Checkpoint (Pascal commits):** `git add src/Umbraco.Community.FormsAuditTrail.E2E src/Umbraco.Community.FormsAuditTrail.TestSite` / suggested message `test: add E2E reconciliation on real UFRecords, health and load checks`

### Task 19: Docs, metadata, security review and 18.1.0-beta.1

**Files:**
- Modify: `README.md`, `CLAUDE.md`, `PKG/Umbraco.Community.FormsAuditTrail.csproj`, `CLIENT/package.json`, `CLIENT/package-lock.json`, `CLIENT/public/umbraco-package.json`, `PKG/wwwroot/umbraco-package.json` (via build), `.github/workflows/build.yml` (`--min`)
- Test: `TESTS/DropScriptTests.cs` (P6, P16), `TESTS/ReadmeTests.cs`

- [ ] **Step 0: Write the failing tests:**

```csharp
// DropScriptTests: the script is read from README.md between <!-- drop-script:start --> and <!-- drop-script:end -->
DropScript_Sqlite_RemovesAccessTablesAndHistoryRow_KeepsDefinitionTables()  // then MigrateAsync re-adds them cleanly
[SqlServerFact] DropScript_SqlServer_RemovesAccessTablesAndHistoryRow_KeepsDefinitionTables()
// ReadmeTests
Readme_ListsEveryNotAuditedType()                 // every FormsControllerMap.NotAudited key name appears
Readme_StatesScrubLagForTheS5BranchTaken()        // S5 GO: "bulk, form and scheduled deletes"; S5 fallback: "all deletes made outside the backoffice"
Readme_NeverSaysCompliant()
```

Run `--filter "DropScriptTests|ReadmeTests"`. Expected: FAIL (markers and sections missing). Raise `check-trx.py --min` by one.

- [ ] **Step 1: README.** New intro ("Forms Audit Trail: who touched your form data"), "Entry access audit" section, full config table (spec 7.7, including the five security-review keys), "What is not captured" (code, workflows, Delivery API, direct SQL, front-end submissions, Forms' Examine index; the exporter gap; list views do not store entry ids by default; **every `FormsControllerMap.NotAudited` type with its reason**, SEC-01), a "GDPR" section with every item in spec section 4 (lawful basis, staff privacy notice and staff SARs, DPIA screening, ROPA periods, backups not scrubbed, the scrub lag for the S5 branch taken (GO: up to 24 hours for form-level and scheduled deletes that publish no per-record event; fallback, Pascal's decision: up to 24 hours for **every delete made outside the backoffice**, including code and Forms' scheduled deletion), backoffice single and bulk deletes scrubbed at once, cleared values not kept by default, the static "another package transforms entry values" notice when the S10 fallback applies (P13b), `StoreEditValues=All` disclosure, not tamper-evident, not complete) (SEC-11), "Not tamper-evident" with `EmitLogEvents` (SEC-08), "Operations" (DB principal needs DDL rights at first start, SEC-09; Users-section access equals admin for this log, SEC-L5; the health banner and what each reason means), the spec 7.2 `DROP` script between the `drop-script` markers with its warning above it (SEC-L7), compatibility table (18.1.0: CMS 18.3.0+, Forms 18.2.2+), "Audit" tab naming, and the upgrade notes from spec section 10 with the GDPR notice first. No "compliant".
- [ ] **Step 2: CLAUDE.md.** Update "What this is", the version table (CMS 18.3.0 / 17.8.0 and Forms floors), Layout (`EntryAudit/`, `Authorization/`, `build/`, TestSite, E2E), and "Behaviours to preserve": the four new tables, fail-open rule and its never-hide-itself rules, `FailClosedOnExport` as the only response change, before values via `IRecordStorage` only, scrub-always rule, write-time and fail-closed read-time masking, group policy on `context.User`, `Guid.Empty` rows for all-forms users only, `typeof` controller map, `NotAudited` list and completeness test, dependency pins and `NuGetAuditMode=all` (never remove as a tidy-up), SHA-pinned actions.
- [ ] **Step 3: csproj.** `<Description>` leads with entry access audit, then definition audit; `<PackageTags>` append `gdpr;compliance;entries;umbraco-forms-entries`. Bump the four version files to `18.1.0-beta.1` and rebuild the client.
- [ ] **Step 4:** `dotnet list src/Umbraco.Community.FormsAuditTrail.slnx package --vulnerable --include-transitive --format json > /tmp/v.json && python3 -I build/check-vulnerable.py /tmp/v.json`. Expected: exit 0, no High or Critical, transitive included (SEC-05). Any Moderate goes in the PR description.
- [ ] **Step 5: Security review.** Dispatch `security-analyst` (model `opus`) on the whole `main...fc/m5-beta` surface, starting from the Task 12b findings and their outcomes: `FormsAuditAccessApiController`, `Authorization/`, `EntryAuditCapture`, `EntryAuditActionFilter`, `UploadAuditMiddleware`, `EntryAuditWriter`, `EntryAuditDetail`, `RecordSavingHandler`, `ConfigChangeRecorder`, `ReadTimeMasker`, `AccessCsvWriter`, `publish.yml` and the dashboard elements, with `~/.claude/references/umbraco-pitfalls.md` and `security-review.md` (to confirm each SEC-xx is closed in code). Any Critical is fixed before beta (it cannot be waived); High findings are fixed, or accepted by Pascal as residual risk in the PR, before stable.
- [ ] **Step 6: Verify.** `dotnet build src/Umbraco.Community.FormsAuditTrail.slnx -c Release && dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release && dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release --filter Category=Slow && (cd CLIENT && npm ci && npm test && npm run build) && git diff --exit-code -- PKG/wwwroot && grep -rn "compliant" README.md PKG/*.csproj`. Expected: build and tests pass, wwwroot unchanged after rebuild, the final grep prints nothing.
- [ ] **Checkpoint (Pascal commits):** `git add README.md CLAUDE.md PKG CLIENT TESTS .github/workflows/build.yml` / suggested message `docs: reposition as Forms Audit Trail, who touched your form data; release 18.1.0-beta.1`
- [ ] **Release (Pascal):** open the M5 PR to `main` with the security review outcome and the Task 18 and 18b E2E reports for both providers attached (P17); rebase-merge; tag `v18.1.0-beta.1` (the `nuget` environment asks for your approval). Then (Pascal released stable on 2026-11-25 without the 2 to 3 agency-site run) work through the stable checklist below, bump the four files to `18.1.0`, rerun Step 6 and Task 20, merge and tag `v18.1.0`, then `gh release create v18.1.0 --generate-notes` and move the GDPR notice to the top of the notes.
- [ ] **Stable release checklist (Pascal; spec section 10):**
  - [ ] CI green on both legs: transitive audit, completeness test, CodeQL, `npm audit`.
  - [ ] M0 results S1 to S12 filled; S11 done on 18.2.2 and 17.6.2.
  - [ ] `security-analyst` code review done; every High in `security-review.md` closed or accepted as residual risk in the PR.
  - [x] ~~DPO or legal review of the README "GDPR" section and the upgrade notice (SEC-11, escalated).~~ Waived by Pascal on 2026-11-25: a package author is not the data controller and no law requires the review; the README section is hedged, says it is not legal advice and sends each site owner to their own DPO, and a later version can correct the wording.
  - [ ] Repo settings from Task 6a done (environment, tag ruleset, secret scanning, push protection, Dependabot security updates).

### Task 20: Exercise it in the backoffice

Manual, on the TestSite with `18.1.0-beta.1`, SQLite first then SQL Server. Keep a terminal on `tail -f` of the newest `umbraco/Logs/*.json` filtered to `FormsAuditTrail|table is locked|Error`.

- [ ] **Step 1: Upgrade path.** Restore the 18.0.2-era database from Task 8 Step 7. Start. Expected: migrations applied once; "Form changes" tab shows every old row unchanged; "Audit" is the dashboard label.
- [ ] **Step 2: Views.** As admin open entries, page, filter (with a search term), open an entry, reopen it within 60 s. Expected in Entry access: one ListViewed per distinct page or filter, one DetailViewed, Detail shows `searchTermUsed` and never the term.
- [ ] **Step 3: Edit.** Change Name and Email; then approve the entry. Expected: one Edited row; "View changes" shows Name before and after and Email `[masked]`; approve adds nothing.
- [ ] **Step 4: Export and download.** CSV export, then download; repeat with both uploaded-files exporters. Expected: Exported rows with file names; "Save All Uploaded Files (by entry)" captures the entry ids, and only the "in disk structure" exporter reads "0 entries (ids not captured)"; the download row is linked.
- [ ] **Step 4a: Uploads, workflow, member.** Open an uploaded CV from an entry, re-run its workflow, open a member's form summary. Expected: FileViewed (no file name visible anywhere), WorkflowRetried, MemberSummaryViewed rows; the CV open appears in that entry's history.
- [ ] **Step 5: Entry history.** Paste the edited entry's id, and follow its link from Entry access. Expected: full timeline including the export and its download; an AuditViewed row appears once.
- [ ] **Step 6: Delete and scrub.** Delete the edited entry; bulk delete two more; permanently delete a test form. Expected: Deleted rows with the user (the bulk delete is one row reading "2 entries", and each entry's history shows it); the edit now shows "Value removed on <date> (retention or erasure)"; form delete row with deleteKind form; its definition history is still in Form changes.
- [ ] **Step 7: Access-log CSV.** Export it and open in a spreadsheet. Expected: spec columns including `UserKey`, no edit values, a form named `=1+1` and one named `\r=1+1` (carriage return first) both show as text; an AuditExported row appears; response has `Cache-Control: no-store`. Filter Entry access by a user: an AuditSearched row appears once.
- [ ] **Step 7a: Rename and blank.** Rename the admin's display name, act, rename back. Expected: Entry access shows the stored name with "(now: ...)". Blank a field on an entry: "Value cleared", no old value.
- [ ] **Step 8: Permissions and switch-off.** As `e2e.editor`: only Form changes. Set `AccessLogUserGroups` to `["e2eForms"]` without restart: tabs appear on reload, and one ConfigChanged row is written. Add a non-existent alias `ghosts`: one Warning in the log. Set `EntryAudit:Enabled=false`: actions write nothing, the switched-off message shows, old rows stay readable; set it back: the banner shows "Entry auditing was off from X to Y". As `e2e.editor` (restricted to one form) with access-log rights: ConfigChanged and AuditExported rows are not listed, but the off-period banner is.
- [ ] **Step 9: Logs and health.** Expected: zero `table is locked`, zero Error from the package, Information lines from the retention job on next run (trigger by restarting with `Delay` reached, or wait 5 minutes); afterwards `GET .../config` shows `entryAuditHealthy: true` and a `lastJobRunUtc`.
- [ ] **Step 10:** Optionally run `@review` (design-review skill) on the three tabs. Note anything that could not be verified in the PR.

---

## M6: port to `v17` as 17.1.0

### Task 21: Cherry-pick to `v17` per milestone, 17.1.0, Marketplace

**Files:** the same set as Tasks 6 to 19 on `v17`; manual merge in `PKG/NotificationHandlers/FormDeletedHandler.cs`, `FormDeletingHandler.cs`, `AuditHandlerHelpers.cs` and `PKG/Persistence/FormsAuditDbContextExtensions.cs` (v17 shape); `umbraco-marketplace.json` on `main` at the end.

**Expected conflicts (P12), resolved by hand and never by taking `main`'s side wholesale:**
- `PKG/Umbraco.Community.FormsAuditTrail.csproj`: v17 has a fourth CMS reference, `Umbraco.Cms.Api.Management` (`[17.7.0, 18.0.0)` today), and `Microsoft.EntityFrameworkCore.Design` `[10.0.0, 11.0.0)` against `main`'s `[10.0.11, 11.0.0)`.
- `.github/workflows/build.yml`: v17 triggers on PRs to `[main, v17]`; `main` triggers on PRs to `[main]` plus the P20 push trigger. Keep v17's PR branches, add `push: branches: [v17]`, and keep the matrix, mssql service, `check-trx.py` step and `Category!=Slow` filter.
- The delete path files listed above (v17's `FormDeletedHandler` shape and `GetLastAfterSnapshotAsync`).
- `CLIENT/package.json` and lock (`^17.8.0`), and `PKG/wwwroot` (always rebuilt, never merged).

- [ ] **Step 0 (P12): v17 base.** `git switch v17 && git pull && git switch -c fc17/m0-setup`. Do on `v17` what Task 1 did on `main`, by hand rather than by cherry-pick: copy `docs/design.md` and `docs/plan.md` from `main` (only these two); add the TestSite from `dotnet new install Umbraco.Templates::17.8.0` with `Umbraco.Cms` `17.8.0` and `Umbraco.Forms` `17.6.2` (both exact) and the same `.gitignore` lines; add `UmbracoCmsVersion` (default `[17.8.0, 18.0.0)`) and use it for **all four** CMS references including `Umbraco.Cms.Api.Management`; add `UmbracoFormsVersion` (default `[17.6.2, 18.0.0)`); raise `Microsoft.EntityFrameworkCore.Design` to `[10.0.11, 11.0.0)` to match the version that generated the migrations (design-time only, `PrivateAssets=all`). Verify `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release` passes and the TestSite starts in `Development` with the Forms dashboard. Checkpoint (Pascal commits) `chore: raise CMS floor to 17.8.0 and Forms to 17.6.2, add design, plan and a local test site`; PR to `v17`, rebase-merge.
- [ ] **Step 1: one PR per milestone.** For each milestone M1 to M5 in order: `git switch v17 && git pull && git switch -c fc17/m<N>`, then `git cherry-pick <first>^..<last>` over that milestone's rebase-merged commits on `main` (list them with `git log --oneline --reverse <prev-milestone-tip>..<milestone-tip> origin/main`), never `git merge main`. Resolve the expected conflicts above. Run Step 4's checks for the tasks in that milestone, open the PR to `v17`, CI green, rebase-merge. M2's PR gets a short `security-analyst` delta review only if the v17 conflict resolution changed `EntryAuditCapture`, the writer or the upload middleware.
- [ ] **Step 2:** In M3, resolve the delete path by hand: keep v17's `FormDeletedHandler` structure and `FormsAuditDbContextExtensions.GetLastAfterSnapshotAsync`, then add Task 14's scrub-by-form and form `Deleted` access row in the same place and with the same tests (`FormDeleteHandlerTests` uses the v17 shape).
- [ ] **Step 3:** In M1, keep the Pkcs and MessagePack pins and `NuGetAuditMode=all` exactly as on `main`; confirm `dotnet list ... --include-transitive` shows Pkcs 10.0.13 and MessagePack 3.1.11 with no NU1605. CI matrix latest leg `-p:UmbracoCmsVersion=17.* -p:UmbracoFormsVersion=17.*`. Forms 17.6.2 needs CMS `[17.6.0, 18.0.0)`, so 17.8.0 is in range (verified 2026-10-30). Set npm `@umbraco-cms/backoffice` to `^17.8.0`. Make sure Task 6a's workflows, `build/` scripts and `codeql.yml` are on `v17` (CodeQL and the audit must run on PRs to `v17`). Confirm the `services: mssql` container and `FORMSAUDIT_SQLSERVER` survived the pick and the `check-trx.py` step passes on the v17 floor leg (P6). Do not regenerate migrations: they depend on the provider, not the Umbraco version, so they cherry-pick unchanged. Run both `has-pending-model-changes` to confirm.
- [ ] **Step 4:** Rerun Tasks 2 to 5's probes briefly on the v17 TestSite only if a contract or completeness test fails. S11 already has a 17.6.2 row (Task 5). If one event's hook is missing on 17.6.2, ship 17.1.0 without it and document it in the v17 README (spec section 4).
- [ ] **Step 5:** In M5, bump the four files to `17.1.0-beta.1`, rebuild the client, update the pinned `--version 17.x.y` install command in `README.md` on **both** branches.
- [ ] **Step 6: Verify (per milestone, then in full at M5).** `dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release`, `-p:UmbracoCmsVersion=17.* -p:UmbracoFormsVersion=17.*`, the transitive audit with `check-vulnerable.py` (exit 0), both `has-pending-model-changes`, `cd CLIENT && npm ci && npm test && npm run build && git diff --exit-code -- ../wwwroot`; at M5 also Tasks 18 and 18b's E2E and Task 20's checklist on the v17 TestSite on both providers. Expected: all pass; any skipped event is listed in the v17 README.
- [ ] **Checkpoint (Pascal commits):** per cherry-pick and conflict resolution, then the version bump / suggested message `chore: release 17.1.0-beta.1 with entry access audit`
- [ ] **Release (Pascal):** after the M5 v17 PR merges, tag `v17.1.0-beta.1`, then `17.1.0` after beta feedback and the Task 19 stable checklist (the DPO review covers both lines). Once **both** 18.1.0 and 17.1.0 are stable, on `main` update `umbraco-marketplace.json`: `Title` "Forms Audit Trail: who touched your form data", rewritten `Description` (no "compliant"), add `gdpr`, `compliance`, `entries`, `privacy` to `Tags`, keep `Category` "Editor Tools". Suggested message `docs: retitle the Marketplace listing`.

---

## Later (separate plans)

- **M7:** package notifications `FormsAuditRecordedNotification` and `FormEntriesExportedNotification` (one line after `SaveChangesAsync` in `EntryAuditWriter` and `FormSavedHandler`), email and webhook alerts, and the `Umbraco.Community.FormsAuditTrail.Automate` sub-package (Automate spike first; triggers carry no record ids or values; aliases never renamed).
- **M8:** recycle-bin audit for forms and folders and an `EntityType` column on `formsAuditTrailEntries` (Forms 18.3 floor).
- Also later, unplanned: search by submitted value, hash chain (SEC-08), "Scrub stored values for this entry" action (SEC-03 item 2), per-form workspace view, staff erasure, `LogListRecordIds` default on, E2E in CI, auditing any `NotAudited` surface S11 or a later Forms shows to return entry values.

---

## M0 results

| Spike | Result (yes/no) | Notes and fallback chosen |
|---|---|---|
| S1 filter matching | Yes (GO) | All ten matched, identity holds, both providers; claim `sub`; lifetimes recorded. See docs/spikes.md S1 |
| S2 before values via `IRecordStorage` | Yes (GO) | Copy before `next()` holds pre-edit values on both providers, no lock; diff by `FieldId`. See docs/spikes.md S2 |
| S3 SQLite write order | No (NO-GO on SQLite) | Delete record-set action hangs under concurrent writes; the `IAsyncResultFilter` fallback also hangs; Forms has the hazard with any concurrent SQLite writer, and the package's writes add writers (paging load: 0/71 without, 3/70 with). Pascal (2026-11-16): keep the sync write after `next()`; re-confirmed with the like-for-like comparison; README note in Task 19 (on SQLite the package's writes can trigger Forms' delete hang; SQL Server recommended for multi-user sites). See docs/spikes.md S3 |
| S4 sensitive flag / permission | Yes | `Field.ContainsSensitiveData`; public check is CMS `Umbraco.Cms.Core.Models.UserExtensions.HasAccessToSensitiveData(IUser)`. No fallback; Task 16 uses it. See docs/spikes.md S4 |
| S5 bulk and form delete notifications | No (Fallback) | Delete action: `RecordDeleted` per record after Forms' scope, write there safe. Route delete and permanent form delete: no `RecordDeleted`; a write in `RecordDeleting` locks. Body `RecordActionExecution.RecordKeys`, action type `actionId` route. Task 14 Fallback branch. See docs/spikes.md S5 |
| S6 scheduled deletion | No | Only a batched `RecordDeleting`, no `HttpContext`, no `RecordDeleted`; Forms' retention 0 means off. Reconciliation (Tasks 9, 14). See docs/spikes.md S6 |
| S7 exporters and file name | Yes, except disk-structure zip | Per-row notification for both CSV exporters and uploaded files by entry; none for in disk structure (`recordIdsCaptured=false`). File name `GenerateExportResponseModel.FileName`. Exports ignore paging. See docs/spikes.md S7 |
| S8 overhead | No measurable overhead (advisory) | 50 loads of 100 rows on SQLite, probe on and off: medians 6.0 to 9.4 ms, p95 6.8 to 10.0 ms; the difference is inside roughly 3 ms of boot-to-boot noise (n=2 boots per configuration); cannot size. No fallback; local and advisory, real check in Tasks 12, 13b and 18b. See docs/spikes.md S8 |
| S9 Forms 18.3.0 types (R7: stable, not rc) | Yes | Forms 18.3.0 with CMS 18.3.1 (Forms needs CMS 18.3.0+, the current floor, so no floor change): ten controllers, five notification types, `ProtectFormUploadRequestsMiddleware` type and filter name, the 23 types and 126 endpoints unchanged (type names, routes, binding and middleware identity; upload 403/206/304 not re-run, Task 12a re-checks); completeness list unchanged. Five notification types: the brief's four record events plus `EntrySearchResultFetchingNotification`. No fallback. See docs/spikes.md S9 |
| S10 encryption at rest / foreign handlers | Yes | Before values transformed, diff exact. Detector: at `UmbracoApplicationStartedNotification`, scan `IServiceCollection` for `INotification(Async)Handler<T>`, foreign by assembly; sees the stand-in, not ours. No fallback (Tasks 13, 15). See docs/spikes.md S10 |
| S11 Forms 18.2.2 surface and uploads (then 17.6.2) | Yes (audited set unchanged) | 23 types, same on both lines: 10 audited, 7 controllers, 3 bases and 3 models NotAudited, each confirmed. `Umbraco.Forms.Web.HttpModules.ProtectFormUploadRequestsMiddleware`, `PrePipeline` filter of that name; Forms authorises before 206 and 304. Task 12a: insert before that filter, user from the `UmbracoBackOffice` cookie, Forms' path normalisation and case-sensitive `form_` rule (17.6.2: by decompilation); upload root is a Forms literal, not an option. See docs/spikes.md S11 |
| S12 concurrent migrations / no DDL rights | Yes (no race) | SQL Server, two instances in the same second: applied once in 5 of 5 race rounds plus a lock-held run, no duplicate-object error, EF Core 10.0.12, whose own `__EFMigrationsLock` app lock serialises `MigrateAsync` (proved by holding it externally). So Task 10a adds no lock on SQL Server. No-DDL login: caught `SqlException` 262, site serves pages, package API 500 until the tables exist; covered by SEC-09 (R17: Task 10a `migrationsPending`, README DDL-rights note in Task 19). SQLite two-process migration race not run: Task 10a tests it (P13g guard if it races). See docs/spikes.md S12 |

---

## Plan review changes (2026-10-30)

Source: `plan-review.md` (qa-reviewer, 2026-10-30, ready-with-fixes, 12 major and 9 minor). Pascal's decisions the same day: S5 fallback accepted, public docs limited to `design.md` and `plan.md`, PR checks in the repo's own CI, subagent-driven build with a security checkpoint after M2, one PR per milestone rebase-merged. Every finding is applied; the "How" column notes where the plan departs from the reviewer's wording and why.

| ID | Applied | How and where |
|---|---|---|
| P1 | Yes | Floor properties moved into Task 1 Step 2a; Task 6 keeps pins, audit mode, contract tests, matrix and npm floor. |
| P2 | Yes | Writer is a singleton with a scope per write (Task 11); reader, provider and handler stay scoped and are resolved from scopes (Tasks 9, 13); `CompositionTests` with `ValidateScopes` and `ValidateOnBuild` from Task 10, extended by every registering task; S1 records the Umbraco lifetimes; Global Constraints "DI lifetimes". |
| P3 | Yes | TestSite-only `SimulatedEncryptionComposer` (Task 13b) and `encryption.spec.ts` on both providers (Task 18). Seeded values are stored reversed so the fetch handler acts as decryption; this makes the reviewer's reversing handler a coherent stand-in. |
| P4 | Yes | Created owned by Task 13b with the five named tests; `SetCreated` on the collector (Task 10); the E2E create is unconditional through `CreateRecordController` (Task 18). |
| P5 | Yes | `reconciliation.spec.ts` (Task 18b): delete-then-scrub, 1,200 ids, unreadable `UFRecords` scrubs nothing; TestSite diagnostics endpoints (Task 18). Not in CI, as for all E2E; reports attached to the M5 PR. |
| P6 | Yes | `[SqlServerFact]` fails in CI, `Category=SqlServer` trait, `build/check-trx.py --min` (Task 8, raised by later tasks); per-provider migration id; `DropScriptTests` on both providers (Task 19); mssql service checked after the v17 pick (Task 21). |
| P7 | Yes | Task 14a sentinel scan for None, MaskSensitive, All, `CaptureSearchTerms` and `KeepClearedValues`; T6 rewritten as `Filter_RequestAbortedCancelled_RealWriter_RowStillWritten` (Task 12), the trivial writer test dropped (Task 11). |
| P8 | Yes | Bulk delete is one row plus child ids (Tasks 11b, 14; spec 7.3 and E4 updated); 5,000-record test with statement and time bounds (Task 14); batched retention and expiry (Task 9); 50k export on SQL Server (Task 11b). |
| P9 | Yes | `UploadMiddlewarePipelineTests` on TestHost with a fake Forms-like middleware (Task 12a); 206 deduped, 304 written as a view (decision recorded in design 7.3); contract test asserts namespace and anchor (Task 6). |
| P10 | Yes | Export permission tests (Task 16b), `GetAccessEntry_GuidEmptyRow_RestrictedUser_Returns404` and the in-process `AccessApiAuthorizationTests` (Task 16). |
| P11 | Yes | Negative definition `RetentionDays` coerced to 0 with a Warning; fail-on-negative only for `EntryAudit` keys (Task 7; design 7.7). |
| P12 | Yes | Task 21 Step 0 builds the v17 TestSite, docs and floors (including `Umbraco.Cms.Api.Management` on `UmbracoCmsVersion` and the EF Design floor); expected conflicts listed; one v17 PR per milestone; main merge strategy in Global Constraints. |
| P13 | Yes | (a) S1 NO-GO adapter mini-design in Task 12 and design 7.3; (b) S10 fallback in Task 5; (c) S5 fallback restated with Pascal's decision and T23 rewritten (Task 14); (d) S8 advisory, real check after 12 and 13b; (e) 17.6.2 S11 site step; (f) results recorded on `fc/m0-setup`; (g) S12 race action; (h) claim recorded in S1. |
| P14 | Yes | Overhead measured after Tasks 12 and 13b on both providers and in `load-check.spec.ts`; marked local and advisory; fallback named (bounded queue for view events only, needs Pascal's OK because spec 7.1 keeps queues out of MVP). |
| P15 | Yes | (a) `TimeProvider` in the deduplicator and an expiry test; (b) streaming proved by an enumerator spy, 500k test `Category=Slow` and out of CI; (c) `AccessController_RequiresAccessLogPolicy` moved to Task 16, no Skip; (d) constructor updates named in Tasks 9, 14 and 15. |
| P16 | Yes | `Filter_ExecuteAction_NonDeleteBulkAction_WritesNothing`, `Filter_FailClosed_BuilderThrows_Returns503ForExports` (Task 12); `jobNotRun` and `jobStepFailed` banner strings (Task 17); DROP script test (Task 19); axe on the three tabs (Task 18). |
| P17 | Yes, adjusted | Vitest tests a pure `auditRootView` view model (Task 17b) rather than mounting the element, because the backoffice element context is not available in Vitest; Task 18 covers the rendered element. E2E reports for both providers attached to the M5 PR. |
| P18 | Yes | Review Focus 9 to 14 added; focus 5 now includes SQL Server. |
| P19 | Yes | Composer line ref 49-50; `TryGetCurrentUser` instead of inferring from "System"; one `ToJson(int maxBytes = 8192)`; claim recorded in S1. |
| P20 | Yes | Push trigger on `main` and `v17`; `publish.yml` re-runs the transitive audit and `npm audit` and checks a green `build` run for the tag SHA (Task 6a). Both of the reviewer's options are applied. |
| P21 | Yes | PR per milestone (rebase-merge); security checkpoint Task 12b; Tasks 11, 13, 16, 17 split, and 18 too (it grew with P3, P4, P5, P16); only `design.md` and `plan.md` go into `docs/` (Pascal). Playwright still does not run in CI: Pascal's decisions did not add it, so it stays in Later. |

**Task count:** 25 before, 32 now (new 11b, 12b, 13b, 14a, 16b, 17b, 18b).

**Self-review (2026-10-30, after these changes):** spec coverage re-checked against design 6.2, 7.1 to 7.7, 9 and 10, with no gaps. Names checked across tasks (`EntryAuditCapture`, `TryGetCurrentUser`, `SetCreated`, `VisibleRows`, `UploadAuditPipeline`, `check-trx.py`, `ToJson(int)`). `Filter_*` tests now exercise `EntryAuditCapture` through the filter adapter. Checkpoints keep "Checkpoint (Pascal commits)" with no AI attribution.

---

## Change log

**2026-11-16, reuse from User Activity** (design "Reuse from User Activity" and "Integration note")

- Reuse pointers added to Tasks 7, 8, 9, 10, 11, 12, 15, 16, 16b, 17 and 17b. Copy and adapt, no package reference; log adapted files in COMMITS-TODO.md.
- Task 15 and Global Constraints: the access-log authorization handler is Singleton or Transient, never Scoped (UserActivity S6).
- No new tasks. Hours unchanged.

**2026-10-30, security review** (`security-review.md`; spec section "Security review changes (2026-10-30)")

- **New tasks (4):** 6a dependency audit and supply-chain CI (SEC-05, SEC-13, SEC-I3, SEC-I4); 10a health signal (SEC-02, SEC-09, SEC-I1); 12a uploaded-file middleware (SEC-01); 16a config change trail (SEC-07).
- **Changed tasks (20):**
  - 1: CMS 18.3.0 TestSite; upload field and workflow in the spike form.
  - 2: S1 covers ten controllers and their parameter sources.
  - 3: S2 proves before values via `IRecordStorage`; the raw `RecordData` read is dropped (SEC-04).
  - 5: S10 encryption, S11 18.2.2 surface and uploads (escalated UNKNOWN), S12 migrations.
  - 6: CMS floors (D9), pins and `NuGetAuditMode=all` (D10), completeness contract test (SEC-01).
  - 7: five new options and the split CSV switch (SEC-02, SEC-03, SEC-08, SEC-L4).
  - 8: `Cleared` column, `formsAuditTrailState`, events 18 to 24, shared column limits.
  - 9: reconciliation failure is a failed step, Warning on large deletes, job state, state store, `IFormsRecordReader` reduced to `UniqueId` (SEC-04, SEC-07, SEC-I1).
  - 10: ten-controller map plus `NotAudited`, wider `Detail`, bounded dedupe cache (SEC-L6).
  - 11: writer rewritten: truncation, parent first, batches, no cancellation token, `Failed` result, `AuditGap`, `EmitLogEvents` (SEC-02, SEC-08). Interface changed: `WriteAsync(AccessWrite)` returns `AccessWriteResult`.
  - 12: unresolved form writes a row, Unknown user, list-id cap, three new controllers, `FailClosedOnExport` (SEC-01, SEC-02, SEC-10, SEC-L5).
  - 13: before values via snapshot before `next()`, `Cleared`, foreign-handler detector (SEC-03, SEC-04). `RecordDataParser` replaced by `RecordValueSnapshot`.
  - 14: set-based bulk scrub test (SEC-10).
  - 15: handler on `context.User`, alias Warning, health and CSV fields in config (SEC-L5, SEC-09).
  - 16: `AuditSearched`, `AuditViewed` on detail, `Guid.Empty` for all-forms users only, fail-closed read masking, streamed capped CSV with `UserKey`, `no-store`, `CsvFormatter` fix (SEC-06, SEC-10, SEC-12, SEC-L1 to SEC-L4).
  - 17: health, off-period and upgrade banners, cleared and renamed-user display, ESLint rule, GUID deep link (SEC-07, SEC-11, SEC-I2).
  - 18: E2E for uploads, workflow re-run, create, cleared values and health.
  - 19: README content, CLAUDE.md rules, parsed audit, wider security review, stable release checklist with DPO review (SEC-11).
  - 20: manual checks for uploads, workflow re-run, member lookup, `\r` CSV, rename, cleared values, config trail, health.
  - 21: v17 floors 17.8.0 / 17.6.2, pins and CI on `v17`.
  - Also updated: Global Constraints, Review Focus 5 to 8, File structure, spec gaps 1, 4 and 5, Later, M0 results table.
