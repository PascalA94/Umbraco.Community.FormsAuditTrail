# Contributing

Issues and pull requests are welcome. For anything bigger than a small fix, open an issue first so we can agree the approach.

## Branches

- `main` builds both Umbraco lines: package 18.x for Umbraco 18 and 17.x for Umbraco 17. `-p:UmbracoMajor=17` or `18` (default 18) picks the line. There is no `v17` branch.
- Open pull requests against `main`. A change must build and pass the tests on both lines. Code for one line only goes behind `#if UMBRACO_17` (18 code in `#else`).

## Build and test

You need the .NET 10 SDK and Node (the version in `src/Umbraco.Community.FormsAuditTrail/Client/.nvmrc`).

```bash
dotnet restore src/Umbraco.Community.FormsAuditTrail.slnx --locked-mode
dotnet build src/Umbraco.Community.FormsAuditTrail.slnx -c Release --no-restore
dotnet test src/Umbraco.Community.FormsAuditTrail.slnx -c Release --no-build --filter "Category!=SqlServer&Category!=Slow"

cd src/Umbraco.Community.FormsAuditTrail/Client
npm ci --ignore-scripts && npm run build
```

That builds the Umbraco 18 line. Add `-p:UmbracoMajor=17` to the `dotnet` commands for the Umbraco 17 line, or run `bash build/test.sh` to restore, build and test both (`bash build/test.sh 17` for one).

The lock files (`packages.17.lock.json` and `packages.18.lock.json` in each project) pin Umbraco and Forms at each line's minimum versions. If you change a package reference, run a plain `dotnet restore -p:UmbracoMajor=17` and `-p:UmbracoMajor=18`, and commit both lock files.

One client bundle serves both lines: it is built against `@umbraco-cms/backoffice` 17 and CI type-checks it against 18 as well. The client version files stay at `0.0.0`; the pack step stamps the real version.

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
dotnet run --launch-profile Umbraco.Web.UI                       # Umbraco 18
dotnet run --launch-profile Umbraco.Web.UI -p:UmbracoMajor=17 \
  -- --ConnectionStrings:umbracoDbDSN="Data Source=|DataDirectory|/Umbraco17.sqlite.db;Foreign Keys=True;Pooling=True"   # Umbraco 17
```

Give each line its own database, as above: an Umbraco 18 database cannot be opened by Umbraco 17.

Without the key, Umbraco generates one on install and writes it into `appsettings.json`. Don't commit that change.

Open https://localhost:44341/umbraco and log in as `admin@example.test`. The audit trail is the **Forms Audit Trail** tab in the Forms section. Forms runs in trial mode, which is fine locally. Delete `umbraco/Data` to start again from an empty database.

## Try it in your own site

Pack with a unique version each time. NuGet's global cache keeps serving the first build of any version number, so a second pack with the same version is silently ignored:

```bash
dotnet pack src/Umbraco.Community.FormsAuditTrail -c Release -o ~/local-nuget -p:Version=0.0.0-local.$(date +%s)   # add -p:UmbracoMajor=17 for an Umbraco 17 site
dotnet nuget add source ~/local-nuget --name local   # once
dotnet add package Umbraco.Community.FormsAuditTrail --version 0.0.0-local.<timestamp>
```

## Before you open a pull request

- Add or update tests for what you changed, and run them.
- Keep the per-form security checks intact: every API response is filtered through Umbraco Forms' form permissions.
- Security issues go through private reporting (the Security tab), never a public issue or pull request.
