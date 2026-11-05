# Contributing

Issues and pull requests are welcome. For anything bigger than a small fix, open an issue first so we can agree the approach.

## Branches

- `main` is the Umbraco 18 line (package 18.x). `v17` is the Umbraco 17 line (package 17.x).
- Open pull requests against `main`. Fixes that apply to both lines are then made on `v17` too. `main` is never merged into `v17`.

## Build and test

You need the .NET 10 SDK and Node (the version in `src/Umbraco.Community.FormsAuditTrail/Client/.nvmrc`).

```bash
dotnet build src/Umbraco.Community.FormsAuditTrail.slnx -c Release
dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release --no-build

cd src/Umbraco.Community.FormsAuditTrail/Client
npm ci --ignore-scripts && npm run build
```

The built client in `wwwroot/` is committed. Rebuild it when you change anything under `Client/`, and commit the result: CI fails if it drifts.

A change to the EF Core model needs a migration for both database providers. Run these from `src/Umbraco.Community.FormsAuditTrail`:

```bash
dotnet ef migrations add <Name> --context SqliteFormsAuditDbContext --output-dir Migrations/Sqlite
dotnet ef migrations add <Name> --context SqlServerFormsAuditDbContext --output-dir Migrations/SqlServer
dotnet ef migrations has-pending-model-changes --context SqliteFormsAuditDbContext
dotnet ef migrations has-pending-model-changes --context SqlServerFormsAuditDbContext
```

## Try it on the demo site

`demo/` is an Umbraco site with Umbraco Forms and this package, on SQLite. Set the admin password and an image-signing key as user secrets, then run it:

```bash
cd demo/Umbraco.Community.FormsAuditTrail.DemoSite
dotnet user-secrets set "Umbraco:CMS:Unattended:UnattendedUserPassword" "<pw>"
dotnet user-secrets set "Umbraco:CMS:Imaging:HMACSecretKey" "$(openssl rand -base64 64 | tr -d '\n')"
dotnet run --launch-profile Umbraco.Web.UI
```

Without the key, Umbraco generates one on install and writes it into `appsettings.json`. Don't commit that change.

Open https://localhost:44341/umbraco and log in as `admin@example.test`. The audit trail is the **Forms Audit Trail** tab in the Forms section. Forms runs in trial mode, which is fine locally. Delete `umbraco/Data` to start again from an empty database.

## Try it in your own site

Pack with a unique version each time. NuGet's global cache keeps serving the first build of any version number, so a second pack with the same version is silently ignored:

```bash
dotnet pack src/Umbraco.Community.FormsAuditTrail -c Release -o ~/local-nuget -p:Version=0.0.0-local.$(date +%s)
dotnet nuget add source ~/local-nuget --name local   # once
dotnet add package Umbraco.Community.FormsAuditTrail --version 0.0.0-local.<timestamp>
```

## Before you open a pull request

- Add or update tests for what you changed, and run them.
- Keep the per-form security checks intact: every API response is filtered through Umbraco Forms' form permissions.
- Security issues go through private reporting (the Security tab), never a public issue or pull request.
