# Umbraco.Community.FormsAuditTrail

An Umbraco package that adds a view-only audit trail for **Umbraco Forms design changes**. Tracks who changed what on form designs (not submissions) and surfaces the history in a backoffice dashboard.

Umbraco has a content audit trail, but Forms changes have never had one — this package fills that gap.

## Compatibility

| Dependency | Version |
|---|---|
| Umbraco CMS | 17.x |
| Umbraco Forms | 17.x |
| .NET | 10 |
| Database | SQLite and SQL Server (including Umbraco Cloud) |

## Installation

```bash
dotnet add package Umbraco.Community.FormsAuditTrail
```

That's it. The package self-registers via an `IComposer`, creates its database tables automatically on startup, and the dashboard appears as a **Forms Audit Trail** tab in the Forms section of the backoffice.

## Configuration

No configuration is required. Two optional settings:

```json
{
  "FormsAuditTrail": {
    "RetentionDays": 365,
    "EnableCsvExport": false
  }
}
```

- `RetentionDays` — audit entries older than this are deleted by a daily background job. The default is `0`, which keeps entries forever.
- `EnableCsvExport` — set to `false` to switch off the CSV export endpoint and hide the export button, for hosts that don't want audit data leaving the backoffice. Defaults to `true`.

## Permissions

The dashboard and its API require backoffice access to the Forms section. On top of that, Umbraco Forms' own per-form security is honoured: users only see audit history for forms they have access to. Note that access checks for *deleted* forms follow whatever Forms' security records say about that form id — typically only users with broad forms access will see history for deleted forms.

Changes made without a backoffice user — Umbraco Deploy transfers between environments, code-based saves — are recorded with the user shown as **System**.

## Features

- Captures form create, save, and delete events
- Field-level diffing — shows exactly which fields were added, removed, moved, or modified
- Workflow add/remove and setting-change tracking
- Page-level change tracking
- Change detail grouped by category (Field, FieldSet, Page, Workflow, FormSetting)
- Filter by form, event type, user, and date range (with Today / 7 days / 30 days presets)
- CSV export (respects active filters, hardened against spreadsheet formula injection, can be disabled via config)
- Paginated audit table
- Raw JSON before/after snapshot viewer
- Honours Umbraco Forms per-form user permissions
- Optional retention policy for automatic cleanup of old entries

## How it works

The package hooks into Umbraco Forms notifications (`FormSavingNotification`, `FormSavedNotification`, `FormDeletingNotification`) to capture snapshots of a form before and after each save. A diff is computed between the two snapshots and stored alongside the audit entry.

Audit data is stored in the main Umbraco database using EF Core, with provider-specific migrations for both SQLite and SQL Server selected automatically from your Umbraco connection string. Migrations are applied on startup (skipped while Umbraco is installing or upgrading).

### Database

Three tables are created in the Umbraco database:

- `formsAuditTrailEntries` — one row per create/save/delete event, including the JSON snapshots
- `formsAuditTrailChanges` — one row per field-level change within an entry
- `formsAuditTrailMigrationsHistory` — the package's own EF Core migrations history

Indexes are applied on `Timestamp`, `FormId`, and `UserKey` for filter performance.

## Known limitations

- **Workflow property changes** (rename, settings edits, active toggle made in the workflow dialog) are captured on the *next* form save rather than the moment they happen: Umbraco Forms commits workflows to the database before firing form notifications. Workflow additions and removals are captured correctly.
- **Form copies** are recorded as `Created` events — Umbraco Forms 17 has no copy notification.
- **Umbraco Deploy transfers** are captured but attributed to **System** rather than the user who triggered the deployment — the deploying user's identity is not available in the notification.

## Roadmap

Ideas under consideration for future versions — contributions welcome:

- Backoffice localisation
- Entries surfaced on the form editor itself (per-form history view)
- Auditing of other Umbraco Forms entities (datasources, prevalue sources, folders)

## Building from source

The backoffice dashboard is built with Lit/TypeScript targeting the Umbraco Bellissima backoffice.

```bash
# Frontend (output goes to wwwroot/, served via static web assets)
cd Client
npm install
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
