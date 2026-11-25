using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Infrastructure.DependencyInjection;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Extensions;
using Umbraco.Forms.Core.Searchers;
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
        // Singleton: the in-memory failure state is per server and must outlive any request.
        builder.Services.AddSingleton<EntryAuditHealth>();
        // Singleton because the singleton ConfigChangeRecorder consumes it; it opens a scope per write.
        builder.Services.AddSingleton<IEntryAuditWriter, EntryAuditWriter>();
        // Singleton so the options-change subscription is made once. It writes the ConfigChanged row that stops a config
        // switch-off from leaving a silent gap (SEC-07); registered by factory so the publisher gets this same instance.
        builder.Services.AddSingleton<ConfigChangeRecorder>();
        // Scoped like the collector it drives: one capture per request, resolved by the filter only on a Forms match.
        builder.Services.AddScoped<EntryAuditCapture>();
        // Singletons: the resolver holds only a constant, and the user resolver reads the request it is handed.
        builder.Services.AddSingleton<IUploadPathResolver, UploadPathResolver>();
        builder.Services.AddSingleton<IUploadUserResolver, UploadBackOfficeUserResolver>();
        // Scoped: it answers for the request's user, so no singleton may hold it (P2).
        builder.Services.AddScoped<ISensitiveDataPermission, SensitiveDataPermission>();
        // Forms serves uploads outside MVC, so the action filter never sees them; this is the one place the middleware joins the pipeline.
        builder.Services.AddUploadAuditFilter();
        // Global, because the Forms controllers are not ours to decorate. Filters.Add<T> creates it per request through
        // ActivatorUtilities, so it may depend on scoped services without holding them (P2).
        builder.Services.Configure<MvcOptions>(mvc => mvc.Filters.Add<EntryAuditActionFilter>());
        builder.AddNotificationHandler<EntrySearchResultFetchingNotification, EntrySearchResultFetchingHandler>();
        builder.AddNotificationHandler<RecordFetchingNotification, RecordFetchingHandler>();
        // Edits and backoffice creates: it only reads the window and the notification, never the database (spec 7.3).
        builder.AddNotificationHandler<RecordSavingNotification, RecordSavingHandler>();
        // Deletes (S5 fallback): the handler only holds ids in memory, because a write inside Forms' delete scope locks
        // SQLite. The capture core records the delete and scrubs it after the response is 2xx, through this singleton,
        // which opens its own scope per scrub. There is no RecordDeleted handler: Forms does not publish it on most paths.
        builder.AddNotificationHandler<RecordDeletingNotification, RecordDeletingHandler>();
        builder.Services.AddSingleton<DeletedRecordScrubber>();

        builder.Services.AddScoped<IFormFieldInfoProvider, FormFieldInfoProvider>();
        // The final service collection is needed at started time, after every composer has run (S10), so the factory
        // captures the live collection rather than copying it now. The same instance doubles as the started handler.
        builder.Services.AddSingleton<IForeignValueHandlerDetector>(sp => new ForeignValueHandlerDetector(
            builder.Services,
            sp.GetRequiredService<IOptionsMonitor<FormsAuditTrailOptions>>(),
            sp.GetRequiredService<ILogger<ForeignValueHandlerDetector>>()));
        builder.Services.AddSingleton<INotificationAsyncHandler<UmbracoApplicationStartedNotification>>(
            sp => (ForeignValueHandlerDetector)sp.GetRequiredService<IForeignValueHandlerDetector>());

        builder.Services.AddScoped<IFormSnapshotService, FormSnapshotService>();
        builder.Services.AddScoped<IFormDiffService, FormDiffService>();

        builder.AddNotificationHandler<FormSavingNotification, FormSavingHandler>();
        builder.AddNotificationAsyncHandler<FormSavedNotification, FormSavedHandler>();
        builder.AddNotificationHandler<FormDeletingNotification, FormDeletingHandler>();
        builder.AddNotificationAsyncHandler<FormDeletedNotification, FormDeletedHandler>();

        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, RunAuditMigration>();
        // Registered after RunAuditMigration on purpose: handlers run in registration order, and the recorder reads and
        // writes tables that migration creates (SEC-07). It also checks the runtime level and migration health itself.
        builder.Services.AddSingleton<INotificationAsyncHandler<UmbracoApplicationStartedNotification>>(
            sp => sp.GetRequiredService<ConfigChangeRecorder>());

        builder.Services.AddAuthorization(o => o.AddPolicy(AccessLogPolicy.Name, AccessLogPolicy.Configure));
        // Singleton, never scoped: a scoped authorisation handler crashes start-up under scope validation (UserActivity S6).
        builder.Services.AddSingleton<IAuthorizationHandler, AccessLogAuthorizationHandler>();
        // Singleton so the options-change subscription is made once; registered by factory so the notification publisher
        // receives that same instance instead of a transient copy that would subscribe on every publish.
        builder.Services.AddSingleton<AccessLogGroupAliasCheck>();
        builder.Services.AddSingleton<INotificationAsyncHandler<UmbracoApplicationStartedNotification>>(
            sp => sp.GetRequiredService<AccessLogGroupAliasCheck>());
    }

    private static string GetRequiredConnectionString(IServiceProvider serviceProvider, out string? providerName) =>
        serviceProvider.GetRequiredService<IConfiguration>().GetUmbracoConnectionString(out providerName)
            ?? throw new InvalidOperationException(
                "The Umbraco connection string (umbracoDbDSN) is not configured; the Forms audit trail database is unavailable.");
}
