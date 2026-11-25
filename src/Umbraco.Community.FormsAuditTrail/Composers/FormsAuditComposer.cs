using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Infrastructure.DependencyInjection;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
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

        FormsAuditTrailOptionsRegistration.Add(builder.Services, builder.Config);
        builder.Services.TryAddSingleton(TimeProvider.System);
        // Singleton because it holds only the scope factory; the reader is scoped and resolved inside the job's own scope.
        builder.Services.AddSingleton<IAuditStateStore, AuditStateStore>();
        builder.Services.AddScoped<IFormsRecordReader, UmbracoFormsRecordReader>();
        builder.Services.AddRecurringBackgroundJob<AuditRetentionJob>();


        // Umbraco registers this already; AddHttpContextAccessor is a TryAdd, so naming the dependency here is harmless.
        builder.Services.AddHttpContextAccessor();
        // Scoped so each request gets its own capture window. The accessor is a singleton that reaches it through
        // RequestServices, which is why it never holds the scoped instance itself (P2).
        builder.Services.AddScoped<EntryAuditCollector>();
        builder.Services.AddSingleton<IEntryAuditCollectorAccessor, EntryAuditCollectorAccessor>();
        // Singleton, and it owns a private bounded cache rather than the shared IMemoryCache (SEC-L6).
        builder.Services.AddSingleton<EntryAuditDeduplicator>();

        builder.Services.AddScoped<IFormSnapshotService, FormSnapshotService>();
        builder.Services.AddScoped<IFormDiffService, FormDiffService>();

        builder.AddNotificationHandler<FormSavingNotification, FormSavingHandler>();
        builder.AddNotificationAsyncHandler<FormSavedNotification, FormSavedHandler>();
        builder.AddNotificationHandler<FormDeletingNotification, FormDeletingHandler>();
        builder.AddNotificationAsyncHandler<FormDeletedNotification, FormDeletedHandler>();

        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, RunAuditMigration>();
    }

    private static string GetRequiredConnectionString(IServiceProvider serviceProvider, out string? providerName) =>
        serviceProvider.GetRequiredService<IConfiguration>().GetUmbracoConnectionString(out providerName)
            ?? throw new InvalidOperationException(
                "The Umbraco connection string (umbracoDbDSN) is not configured; the Forms audit trail database is unavailable.");
}
