using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Infrastructure.DependencyInjection;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Extensions;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.Composers;

public class FormsAuditComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // EF Core migrations are provider-specific, so each supported provider has its own derived
        // context and migration set. Both are registered and the provider is decided when the
        // context is first resolved: the connection string is not always available at compose time
        // (e.g. it can be injected into configuration later by hosting environments), and the
        // umbracoDbDSN_ProviderName setting is not always present.
        //
        // Umbraco's own UseUmbracoDatabaseProvider helper is not used here: it pins
        // MigrationsAssembly to Umbraco's provider assemblies, which would stop EF discovering
        // this package's migrations.
        builder.Services.AddDbContext<SqliteFormsAuditDbContext>((serviceProvider, options) =>
            options.UseSqlite(
                GetRequiredConnectionString(serviceProvider, out _),
                sqlite => sqlite.MigrationsHistoryTable(FormsAuditDbContext.MigrationsHistoryTableName)));

        builder.Services.AddDbContext<SqlServerFormsAuditDbContext>((serviceProvider, options) =>
            options.UseSqlServer(
                GetRequiredConnectionString(serviceProvider, out _),
                sqlServer => sqlServer.MigrationsHistoryTable(FormsAuditDbContext.MigrationsHistoryTableName)));

        builder.Services.AddScoped<FormsAuditDbContext>(serviceProvider =>
        {
            var connectionString = GetRequiredConnectionString(serviceProvider, out var providerName);
            return UmbracoDatabaseProviderDetector.IsSqlite(providerName, connectionString)
                ? serviceProvider.GetRequiredService<SqliteFormsAuditDbContext>()
                : serviceProvider.GetRequiredService<SqlServerFormsAuditDbContext>();
        });

        builder.Services.AddOptions<FormsAuditTrailOptions>()
            .Bind(builder.Config.GetSection(FormsAuditTrailOptions.SectionName));
        builder.Services.AddRecurringBackgroundJob<AuditRetentionJob>();

        builder.Services.AddScoped<IFormSnapshotService, FormSnapshotService>();
        builder.Services.AddScoped<IFormDiffService, FormDiffService>();

        builder.AddNotificationHandler<FormSavingNotification, FormSavingHandler>();
        builder.AddNotificationAsyncHandler<FormSavedNotification, FormSavedHandler>();
        builder.AddNotificationHandler<FormDeletingNotification, FormDeletingHandler>();

        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, RunAuditMigration>();
    }

    private static string GetRequiredConnectionString(IServiceProvider serviceProvider, out string? providerName) =>
        serviceProvider.GetRequiredService<IConfiguration>().GetUmbracoConnectionString(out providerName)
            ?? throw new InvalidOperationException(
                "The Umbraco connection string (umbracoDbDSN) is not configured; the Forms audit trail database is unavailable.");
}
