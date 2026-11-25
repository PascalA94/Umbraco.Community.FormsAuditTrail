# CLAUDE.md

Guidance for Claude Code (and other contributors) working in this repository.

## What this is

`Umbraco.Community.FormsAuditTrail` is a NuGet package with two view-only audit trails, shown in one backoffice dashboard (the **Audit** tab, with sub-tabs Form changes, Entry access and Entry history):

- **Entry access audit** (18.1.0 and later): who viewed, opened, edited, deleted and exported Forms entries in the backoffice, and who opened uploaded files. It is personal data about staff, and on by default.
- **Form design audit**: changes to fields, pages, workflows and form settings, with field-level diffs and CSV export.

Front-end submissions are not audited.

## Branches and versioning

The package major version matches the Umbraco major it targets. Each line lives on its own branch:

| Branch | Package | Umbraco CMS | Umbraco Forms | backoffice npm |
|---|---|---|---|---|
| `main` | 18.x | `[18.3.0, 19.0.0)` | `[18.2.2, 19.0.0)` | `^18.3.0` |
| `v17` | 17.x | `[17.8.0, 18.0.0)` | `[17.6.2, 18.0.0)` | `^17.8.0` |

- **This branch is `v17` (Umbraco 17 maintenance).** Fixes that apply to both lines must be made on both branches; do not merge `main` into `v17`.
- The package's Umbraco floors live in `Directory.Build.props` as `UmbracoCmsVersion` (used by all four CMS references, including `Umbraco.Cms.Api.Management`) and `UmbracoFormsVersion`; CI's latest leg overrides them with `17.*`. Tests and the demo use exact `Umbraco*Version`/`Forms*Version` values at the same floors. Forms 17.6.2 needs CMS `[17.6.0, 18.0.0)`.
- Umbraco and EF Core references are **range floors**. Raising a floor forces every consuming site to upgrade, so only raise one when there is a concrete reason (a required API, a security fix) and say why in the PR.
- A version bump touches four files, which must stay in step: the `<Version>` in `Umbraco.Community.FormsAuditTrail.csproj`, `Client/package.json`, `Client/package-lock.json` (run `npm install --package-lock-only`) and `Client/public/umbraco-package.json` (the version shown in the backoffice). Then rebuild the client.
- A 17.x release also updates the pinned Umbraco 17 install command (`--version 17.x.y`) in `README.md` on **both** branches: `main`'s README is what GitHub shows, and each branch's README ships on its own NuGet versions.
- NuGet audit runs in `all` mode, so transitive advisories reached through the Umbraco floors fail loudly. Fix one by adding an explicit `PackageReference` minimum in the package project (see the security minimums `ItemGroup`), or raise an Umbraco floor if that is what pulls a patched version in. `NuGetAuditMode` is `all` in the csproj and the pins for `System.Security.Cryptography.Pkcs` and `MessagePack` stay: removing either is a deliberate change with its own PR, never a tidy-up. Still run `dotnet list src/Umbraco.Community.FormsAuditTrail.slnx package --vulnerable --include-transitive` before a release.

## Commands

Requires the .NET 10 SDK and Node.js 24.13+ (`src/Umbraco.Community.FormsAuditTrail/Client/.nvmrc`).

```bash
# Backend
dotnet restore src/Umbraco.Community.FormsAuditTrail.slnx --locked-mode   # lock files pin the Umbraco/Forms floors
dotnet build src/Umbraco.Community.FormsAuditTrail.slnx -c Release --no-restore
dotnet test  src/Umbraco.Community.FormsAuditTrail.slnx -c Release --no-build
# What the PR build runs: SQL Server and Slow tests are left to release-checks.yml (Actions, Run workflow), which
# starts SQL Server and runs both categories. Locally, SqlServer tests skip unless FORMSAUDIT_SQLSERVER is set.
dotnet test  src/Umbraco.Community.FormsAuditTrail.slnx -c Release --no-build --filter "Category!=SqlServer&Category!=Slow"

# Build script tests (check-vulnerable, check-trx, workflow filters)
python3 -I -m unittest discover -s build/tests

# Latest leg (newest 17.x, unlocked restore; never commit the lock files it rewrites)
dotnet restore src/Umbraco.Community.FormsAuditTrail.slnx '-p:UmbracoCmsVersion=17.*' '-p:UmbracoFormsVersion=17.*' '-p:UmbracoTestVersion=17.*' '-p:FormsTestVersion=17.*' '-p:UmbracoDemoVersion=17.*' '-p:FormsDemoVersion=17.*' --force-evaluate -p:RestoreLockedMode=false

# Security gates (also run in CI)
bash build/check-vulnerable.sh
bash build/gitleaks.sh "origin/v17..HEAD"

# Client (Lit + TypeScript + Vite); output goes to ../wwwroot
cd src/Umbraco.Community.FormsAuditTrail/Client
npm ci --ignore-scripts
npm run build        # tsc --noEmit, then vite build
npm run lint         # ESLint, including the rule that bans unsafeHTML and innerHTML
npm test             # Vitest

# E2E (Playwright) against a running demo site. Copy .env.example to .env in the E2E folder and fill in the values:
# E2E_BASE_URL, E2E_ADMIN_EMAIL/PASSWORD, E2E_EDITOR_EMAIL/PASSWORD, E2E_AUDITOR_EMAIL/PASSWORD, E2E_EXPECT_ACCESS_API.
# The demo site's seeding and diagnostics are behind the E2E:Seed and E2E:Diagnostics settings, in Development only.
# Run the site with FormsAuditTrail__EntryAudit__DeduplicateSeconds=0, Umbraco__CMS__Security__AllowConcurrentLogins=true
# (a UI login otherwise revokes the fixtures' API session), Umbraco__Forms__Options__EnableFormsApi=true and
# Umbraco__Forms__Security__EnableAntiForgeryTokenForFormsApi=false (the public-submit helper gets 403/400 without them).
cd src/Umbraco.Community.FormsAuditTrail.E2E
npm ci --ignore-scripts
npm test

# EF Core: check for model drift on both providers (from src/Umbraco.Community.FormsAuditTrail)
dotnet ef migrations has-pending-model-changes --context SqliteFormsAuditDbContext
dotnet ef migrations has-pending-model-changes --context SqlServerFormsAuditDbContext
```

## Layout

All package code is in `src/Umbraco.Community.FormsAuditTrail/`:

| Area | Files |
|---|---|
| Notification handlers | `NotificationHandlers/FormSavingHandler.cs`, `FormSavedHandler.cs`, `FormDeletingHandler.cs`, `RunAuditMigration.cs` |
| Snapshot and diff | `Services/FormSnapshotService.cs` (form to clean JSON, including workflows), `Services/FormDiffService.cs` (flattened JSON diff: Added/Removed/Modified/Moved) |
| Persistence | `Persistence/FormsAuditDbContext.cs` (abstract) with `SqliteFormsAuditDbContext` / `SqlServerFormsAuditDbContext`; migrations in `Migrations/Sqlite` and `Migrations/SqlServer` |
| API | `Controllers/FormsAuditApiController.cs`, route `/umbraco/formsaudit/api/v1/`, policy `SectionAccessForms` |
| Retention | `BackgroundJobs/AuditRetentionJob.cs`, a daily `IRecurringBackgroundJob`; options in `Configuration/FormsAuditTrailOptions.cs` |
| DI | `Composers/FormsAuditComposer.cs` |
| Entry audit | `EntryAudit/` and its notification handlers (`RecordFetchingHandler`, `RecordSavingHandler`, `RecordDeletingHandler`, `EntrySearchResultFetchingHandler`): `EntryAuditActionFilter` and `EntryAuditCapture` (capture window per request), `EntryAuditWriter`, `EntryAuditHealth`, `UploadAuditMiddleware`, `ConfigChangeRecorder` (one shared snapshot, server named per row, in-memory retry queue capped at 100), `ReadTimeMasker`, scrubbers, and `FormsControllerMap` (audited and `NotAudited` Forms controllers) |
| Access-log policy | `Authorization/`: requirement, handler and group alias check for `EntryAudit:AccessLogUserGroups` |
| Access API | `Controllers/FormsAuditAccessApiController.cs`, route `/umbraco/formsaudit/api/v1/access`, plus `Services/AccessCsvWriter.cs` |
| Client | `Client/src/` - dashboards (root, form changes, entry access, entry history), health banner, diff viewer, API service; `Client/public/umbraco-package.json` is the extension manifest |
| Build scripts | `build/`: `check-vulnerable.*`, `check-trx.py`, `gitleaks.sh`, `check-release-version.sh`, with tests in `build/tests/` |
| Demo site | `demo/Umbraco.Community.FormsAuditTrail.DemoSite/` |

Tests are in `src/Umbraco.Community.FormsAuditTrail.Tests/`, a sibling folder, because a nested test project would be picked up by the package's compile globs. They use xUnit v2 and NSubstitute; internals are exposed through `InternalsVisibleTo`.

## Behaviours to preserve

- **Committed `wwwroot/`.** The built client is committed because `dotnet pack` and project references need it. CI rebuilds it and fails if it differs, so rebuild and commit `wwwroot/` with any `Client/` change.
- **Dual-provider migrations.** One DbContext and migration set per provider, chosen from `umbracoDbDSN_ProviderName`. Umbraco's `UseUmbracoDatabaseProvider` is deliberately not used: it pins `MigrationsAssembly` to Umbraco's assemblies and hides this package's migrations. History lives in `formsAuditTrailMigrationsHistory`. Any model change needs a migration for **both** providers.
- **Before-state.** `FormSavedHandler` diffs against the previous audit entry's `AfterSnapshot`, not the notification state, because Forms writes workflows to the database before `FormSavingNotification` fires.
- **Workflow property changes** surface on the next form save. This is a known limitation, documented in the README.
- **No-change suppression.** `Saved` entries with zero changes are dropped.
- **Per-form security.** Everything the API returns is filtered through `IFormsSecurity.HasAccessToForm`. Requesting an inaccessible form's list returns 403, and an entry detail returns 404 to prevent id enumeration. Do not weaken this.
- **System user.** Changes with no backoffice user (Umbraco Deploy, code) are recorded as "System" with an empty user key.
- **UTC timestamps.** A value converter restores `DateTimeKind.Utc` on read, because SQLite loses the kind and the dashboard would show the wrong local time.
- **CSV export** is hardened against spreadsheet formula injection and can be switched off with `FormsAuditTrail:EnableCsvExport`. The access-log CSV has its own switch, `EntryAudit:EnableCsvExport`, and never contains edit values.

### Entry audit

- **Four more tables.** `formsAuditTrailAccess`, `formsAuditTrailAccessRecords`, `formsAuditTrailEntryChanges` and `formsAuditTrailState`, added by one `AddEntryAccessAudit` migration per provider. `AccessEventType` values are stored: never reuse or renumber one.
- **Fail open.** An audit write that fails never fails the Forms action. It is logged at Error, counted in `EntryAuditHealth`, and the next good write adds an `AuditGap` row. The audit must never hide its own failure: health is shown in the dashboard, `ConfigChanged` rows are written even when auditing is off, and an unresolvable user is recorded as Unknown, never System. The writer never takes `HttpContext.RequestAborted`.
- **`FailClosedOnExport` is the only setting that can refuse an export.** Opt in, and an export, download or access-log CSV whose row cannot be written returns 503. The access-log CSV also returns 429 for a second concurrent export by the same user and 404 when disabled.
- **Before values come from `IRecordStorage` only**, never raw SQL, so a site's decrypting hook runs. Do not parse `UFRecords.RecordData`.
- **Scrub always, scrub only what Forms confirmed.** Form deletes and the daily job are not gated by `EntryAudit:Enabled`; the in-request entry scrubs are. An entry scrub covers only ids Forms confirmed deleting in that request (none confirmed: a 0-entry `Deleted` row with `recordIdsCaptured: false` and `requestedCount`, and no scrub). A form delete scrubs only values of entries that no longer exist in `UFRecords`, with the probe self-test. A cleared field with `KeepClearedValues` off also scrubs earlier values of that field for the record. The daily job reconciles the rest against `UFRecords` ids. Never write to the audit database inside `RecordDeletingNotification`: it locks SQLite. A failed reconciliation read is a failed step, never "all missing".
- **Masking.** At write time under `MaskSensitive`, and again at read time, which fails closed: a field now sensitive, deleted, or on a form that cannot be loaded is masked unless the viewer is in Umbraco's built-in Sensitive data group (`HasAccessToSensitiveData`); known non-sensitive fields show to everyone.
- **Access-log policy.** The group check reads the user from `AuthorizationHandlerContext.User`, not from a held accessor, and fails closed. It narrows `SectionAccessForms` and never replaces it. Rows with `FormId = Guid.Empty`, and rows of a form that no longer exists (deleted or trashed), go only to users who can access every form, and that test includes trashed forms.
- **Controller map.** Audited and `NotAudited` Forms controllers are referenced with `typeof`, so a Forms rename breaks our build. `FormsManagementApiCompletenessTests` fails when Forms adds a type to neither set, and the README lists every `NotAudited` type. Keep them in step.
- **README drop script.** The SQL between the `drop-script` markers is read by tests. Keep it in step with the migration.

## Releasing

1. Check for outdated dependencies, since only security alerts are automated (Dependabot version-update PRs are deliberately off): `npm outdated` in `Client/` and `dotnet list src/Umbraco.Community.FormsAuditTrail.slnx package --outdated`. Keep Umbraco floors and `Microsoft.EntityFrameworkCore.Design` where they are unless there is a reason to move them.
2. Open a PR against `main` or `v17`. Branches are protected by rulesets. Day-to-day PRs are squash-merged. The multi-commit milestone PRs for the 18.1.0 and 17.1.0 release are rebase-merged instead, so each milestone's commits land on their own and the `v17` port can cherry-pick them one at a time. The required checks are `build (floor)` and `build (latest)` (.NET build and test at the floor and newest 17.x, client rebuild and `wwwroot` drift), `secrets` (gitleaks), `osv / osv-scan` and `deps` (vulnerable packages). A package or lock file change regenerates `packages.lock.json` with a plain `dotnet restore` at the floor; never commit lock files from a latest-version restore.
3. After merging, dispatch `release-checks.yml` on the merged commit (Actions, Run workflow, choose `main` or `v17`) and wait for it to pass. It runs the SQL Server and Slow tests, which no other workflow runs, and `publish.yml` refuses to publish a commit that has no successful run of it. Then tag the merged commit `v<version>` (for example `v17.0.1`) and push the tag. `publish.yml` builds, tests, packs with the version taken from the tag, and pushes to NuGet through Trusted Publishing (`NuGet/login`).
4. Create a GitHub release: `gh release create v<version> --generate-notes`.
5. GitHub Actions are pinned to full commit SHAs, and `publish.yml` runs in the protected `nuget` environment. Keep both.
6. The Umbraco Marketplace syncs from NuGet about every 2 hours. `umbraco-marketplace.json` is read from the root of `main`.

NuGet versions can be unlisted but never deleted, so treat a tag push as final.
