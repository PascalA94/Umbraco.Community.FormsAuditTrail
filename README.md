# Umbraco.Community.FormsAuditTrail

A view-only audit trail for **Umbraco Forms design changes**. It records who changed what on a form design (not form submissions) and shows the history in a backoffice dashboard.

Umbraco keeps an audit trail for content, but Forms changes have never had one. This package adds it.

## Compatibility

| Package version | Branch | Umbraco CMS | Umbraco Forms | .NET | Database |
|---|---|---|---|---|---|
| 18.x | `main` | 18.2+ | 18.1.3+ | 10 | SQLite and SQL Server (including Umbraco Cloud) |
| 17.x | `v17` | 17.7+ | 17.5.2+ | 10 | SQLite and SQL Server (including Umbraco Cloud) |

### Versioning

The package's major version matches the Umbraco major it targets. `18.x` is for Umbraco 18 and is developed on `main`. `17.x` is for Umbraco 17 and is maintained on the `v17` branch, which still receives fixes.

The first release, `1.0.0`, targeted Umbraco 17 before this scheme existed. It is deprecated on NuGet, so Umbraco 17 sites should use `17.x` instead.

## Installation

```bash
# Umbraco 18
dotnet add package Umbraco.Community.FormsAuditTrail

# Umbraco 17 - pin a 17.x version, otherwise the latest (18.x) is selected and restore fails
dotnet add package Umbraco.Community.FormsAuditTrail --version 17.0.1
```

The package registers itself through an `IComposer` and creates its database tables on startup. The dashboard appears as a **Forms Audit Trail** tab in the Forms section of the backoffice.

## Configuration

No configuration is required. There are two optional settings:

```json
{
  "FormsAuditTrail": {
    "RetentionDays": 365,
    "EnableCsvExport": false
  }
}
```

- `RetentionDays`: a daily background job deletes audit entries older than this many days. The default, `0`, keeps entries forever.
- `EnableCsvExport`: set to `false` to turn off the CSV export endpoint and hide the export button, for hosts that don't want audit data to leave the backoffice. The default is `true`.

## Permissions

The dashboard and its API require backoffice access to the Forms section. The package also honours Umbraco Forms' own per-form security, so users only see audit history for forms they can access. For a *deleted* form, the check uses whatever Forms' security records hold for that form id, which usually means only users with broad Forms access see its history.

Changes made without a backoffice user, such as Umbraco Deploy transfers between environments or saves from code, are recorded with the user shown as **System**.

## Features

- Captures form create, save and delete events
- Field-level diffs showing which fields were added, removed, moved or modified
- Tracks workflows being added or removed, and changes to their settings
- Tracks page-level changes
- Groups change details by category (Field, FieldSet, Page, Workflow, FormSetting)
- Filters by form, event type, user and date range, with Today, 7 days and 30 days presets
- CSV export that respects the active filters, is hardened against spreadsheet formula injection, and can be turned off in config
- Paginated audit table
- Raw JSON viewer for the before and after snapshots
- Honours Umbraco Forms' per-form user permissions
- Optional retention policy that deletes old entries automatically

## How it works

The package handles the Umbraco Forms notifications `FormSavingNotification`, `FormSavedNotification` and `FormDeletingNotification`, and takes a snapshot of the form before and after each save. It computes a diff between the two snapshots and stores it with the audit entry.

Audit data lives in the main Umbraco database and is accessed through EF Core. The package has separate migrations for SQLite and SQL Server and picks the right set from your Umbraco connection string. It applies them on startup, except while Umbraco is installing or upgrading.

### Database

The package creates three tables in the Umbraco database:

- `formsAuditTrailEntries`: one row per create, save or delete event, including the JSON snapshots
- `formsAuditTrailChanges`: one row per field-level change within an entry
- `formsAuditTrailMigrationsHistory`: the package's own EF Core migrations history

`Timestamp`, `FormId` and `UserKey` are indexed to keep filtering fast.

## Known limitations

- Workflow property changes (renames, settings edits, or toggling a workflow active in the workflow dialog) are captured on the *next* form save, not when they happen. Umbraco Forms writes workflows to the database before it fires form notifications. Workflow additions and removals are captured correctly.
- Form copies are recorded as `Created` events, because Umbraco Forms 17 has no copy notification.
- Umbraco Deploy transfers are recorded but attributed to **System**, not to the user who ran the deployment, because the notification doesn't include the deploying user.

## Roadmap

Ideas for future versions. Contributions are welcome.

- Backoffice localisation
- Audit entries shown on the form editor itself (a per-form history view)
- Auditing other Umbraco Forms entities (datasources, prevalue sources, folders)

## Building from source

You need the .NET 10 SDK and Node.js 24.13 or later. The Node version is pinned in `Client/.nvmrc`: run `nvm use` with nvm or fnm, or `nvm use 24` with nvm-windows.

The backoffice dashboard is written in Lit and TypeScript for the Umbraco Bellissima backoffice. Its built output in `wwwroot/` is committed, and CI fails a pull request when `wwwroot/` is out of date with the client source. Rebuild and commit it whenever you change anything under `Client/`.

```bash
# Frontend (output goes to wwwroot/, served via static web assets)
cd src/Umbraco.Community.FormsAuditTrail/Client
npm ci
npm run build

# Package
cd ..
dotnet pack -c Release
```

To regenerate the EF Core migrations (rarely needed):

```bash
dotnet ef migrations add <Name> --context SqliteFormsAuditDbContext --output-dir Migrations/Sqlite
dotnet ef migrations add <Name> --context SqlServerFormsAuditDbContext --output-dir Migrations/SqlServer
```

## License

[MIT](LICENSE)
