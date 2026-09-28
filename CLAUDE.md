# CLAUDE.md

Guidance for Claude Code (and other contributors) working in this repository.

## What this is

`Umbraco.Community.FormsAuditTrail` is a NuGet package that records a view-only audit trail of **Umbraco Forms design changes** (fields, pages, workflows, form settings - not submissions) and shows the history in a backoffice dashboard with filtering, field-level diffs and CSV export.

## Branches and versioning

The package major version matches the Umbraco major it targets. Each line lives on its own branch:

| Branch | Package | Umbraco CMS | Umbraco Forms | backoffice npm |
|---|---|---|---|---|
| `main` | 18.x | `[18.2.0, 19.0.0)` | `[18.1.3, 19.0.0)` | `^18.2.0` |
| `v17` | 17.x | `[17.7.0, 18.0.0)` | `[17.5.2, 18.0.0)` | `^17.7.0` |

- **This branch is `v17` (Umbraco 17 maintenance).** Fixes that apply to both lines must be made on both branches; do not merge `main` into `v17`.
- Umbraco and EF Core references are **range floors**. Raising a floor forces every consuming site to upgrade, so only raise one when there is a concrete reason (a required API, a security fix) and say why in the PR.
- A version bump touches four files, which must stay in step: the `<Version>` in `Umbraco.Community.FormsAuditTrail.csproj`, `Client/package.json`, `Client/package-lock.json` (run `npm install --package-lock-only`) and `Client/public/umbraco-package.json` (the version shown in the backoffice). Then rebuild the client.
- NuGet audit runs in `direct` mode on purpose: transitive advisories are resolved by the consuming site. Still run `dotnet list src/Umbraco.Community.FormsAuditTrail.slnx package --vulnerable --include-transitive` before a release, and raise an Umbraco floor if that is what pulls a patched version in.

## Commands

Requires the .NET 10 SDK and Node.js 24.13+ (`src/Umbraco.Community.FormsAuditTrail/Client/.nvmrc`).

```bash
# Backend
dotnet build src/Umbraco.Community.FormsAuditTrail.slnx -c Release
dotnet test  src/Umbraco.Community.FormsAuditTrail.slnx -c Release

# Client (Lit + TypeScript + Vite); output goes to ../wwwroot
cd src/Umbraco.Community.FormsAuditTrail/Client
npm ci
npm run build        # tsc --noEmit, then vite build

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
| Client | `Client/src/` - dashboard, diff viewer, API service; `Client/public/umbraco-package.json` is the extension manifest |

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
- **CSV export** is hardened against spreadsheet formula injection and can be switched off with `FormsAuditTrail:EnableCsvExport`.

## Releasing

1. Open a PR against `main` or `v17`. Branches are protected, and the `build` check must pass: .NET build and test, plus the client rebuild and `wwwroot` check.
2. After merging, tag the merged commit `v<version>` (for example `v17.0.1`) and push the tag. `publish.yml` builds, tests, packs with the version taken from the tag, and pushes to NuGet through Trusted Publishing (`NuGet/login`).
3. Create a GitHub release: `gh release create v<version> --generate-notes`.
4. The Umbraco Marketplace syncs from NuGet about every 2 hours. `umbraco-marketplace.json` is read from the root of `main`.

NuGet versions can be unlisted but never deleted, so treat a tag push as final.
