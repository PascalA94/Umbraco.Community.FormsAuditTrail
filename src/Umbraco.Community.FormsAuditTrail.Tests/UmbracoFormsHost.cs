using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Install;
using Umbraco.Cms.Infrastructure.Migrations;
using Xunit;
using Umbraco.Extensions;
using Umbraco.Forms.Core.Extensions;
using Umbraco.Forms.Core.Providers.Extensions;
using Umbraco.Forms.Web.Extensions;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// A real Umbraco and Umbraco Forms runtime over a real database, booted with Umbraco's own unattended install so the
/// <c>UFRecords</c> table is the one Forms creates, not a copy of its DDL. Used to run the real
/// <c>UmbracoFormsRecordReader</c> through a real <c>IScopeProvider</c>. Not started as a web server: booting the
/// runtime is enough to resolve services.
/// </summary>
internal sealed class UmbracoFormsHost : IAsyncDisposable
{
    private readonly string _contentRoot;

    private UmbracoFormsHost(WebApplication app, string contentRoot)
    {
        App = app;
        _contentRoot = contentRoot;
    }

    public WebApplication App { get; }

    // Internal in Umbraco; the value is part of what its own unattended upgrader reads.
    private const string PendingPackageMigrationsStateKey = "PendingPackageMigrations";

    public static async Task<UmbracoFormsHost> StartAsync(string providerName, string connectionString)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "formsaudit-host-" + Guid.NewGuid().ToString("N"));
        // Umbraco's unattended upgrader lives in the backoffice package, which this project does not reference, so
        // the pending package migrations (the ones that create the Forms tables) are run here as it would run them.
        Directory.CreateDirectory(Path.Combine(contentRoot, "wwwroot", "media"));

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            WebRootPath = Path.Combine(contentRoot, "wwwroot"),
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN"] = connectionString,
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = providerName,
            ["Umbraco:CMS:Unattended:InstallUnattended"] = "true",
            ["Umbraco:CMS:Unattended:UnattendedUserName"] = "Test admin",
            ["Umbraco:CMS:Unattended:UnattendedUserEmail"] = "admin@example.test",
            ["Umbraco:CMS:Unattended:UnattendedUserPassword"] = "Aa1!" + Guid.NewGuid().ToString("N"),
            ["Umbraco:CMS:Unattended:UpgradeUnattended"] = "true",
            ["Umbraco:CMS:Unattended:PackageMigrationsUnattended"] = "true",
            ["Umbraco:CMS:ModelsBuilder:ModelsMode"] = "Nothing",
        });

        // Umbraco's own backoffice-less core (AddCore) plus Forms core: the backoffice and website packages are not
        // referenced by this test project. Validation is off because services only they add are not registered.
        builder.Host.UseDefaultServiceProvider(o =>
        {
            o.ValidateOnBuild = false;
            o.ValidateScopes = false;
        });
        builder.CreateUmbracoBuilder().AddCore().AddUmbracoFormsCore().AddUmbracoFormsCoreProviders().AddUmbracoFormsWeb().AddComposers().Build();
        WebApplication app = builder.Build();
        await app.BootUmbracoAsync();
        var state = app.Services.GetRequiredService<IRuntimeState>();
        if (state.StartupState.TryGetValue(PendingPackageMigrationsStateKey, out var pending)
            && pending is IReadOnlyList<string> plans && plans.Count > 0)
        {
            var runner = app.Services.GetRequiredService<PackageMigrationRunner>();
            foreach (ExecutedMigrationPlan plan in await runner.RunPackagePlansAsync(plans))
            {
                Assert.True(plan.Successful, "Package migration failed: " + plan.Plan.Name + " " + plan.Exception);
            }
        }

        return new UmbracoFormsHost(app, contentRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        try
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering file handle must not fail a test that already passed.
        }
    }
}
