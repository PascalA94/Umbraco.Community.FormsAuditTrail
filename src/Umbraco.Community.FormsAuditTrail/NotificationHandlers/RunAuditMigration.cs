using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

public class RunAuditMigration : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRuntimeState _runtimeState;
    private readonly ILogger<RunAuditMigration> _logger;
    private readonly EntryAuditHealth _health;

    public RunAuditMigration(
        IServiceScopeFactory scopeFactory,
        IRuntimeState runtimeState,
        ILogger<RunAuditMigration> logger,
        EntryAuditHealth health)
    {
        _scopeFactory = scopeFactory;
        _runtimeState = runtimeState;
        _logger = logger;
        _health = health;
    }

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        // Don't attempt migrations while Umbraco is installing or upgrading: the connection
        // string may not be configured yet. After install completes the application restarts
        // and this handler runs again at RuntimeLevel.Run.
        if (_runtimeState.Level != global::Umbraco.Cms.Core.RuntimeLevel.Run)
        {
            return;
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();
            IEnumerable<string> pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            if (pending.Any())
            {
                _logger.LogInformation("Applying Forms Audit Trail database migrations...");
                await db.Database.MigrateAsync(cancellationToken);
                _logger.LogInformation("Forms Audit Trail database migrations applied.");
            }

            // Asked again rather than assumed false: a provider can report success while leaving migrations unapplied.
            _health.MigrationsPending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
        }
        catch (Exception ex)
        {
            // Never rethrown: a failed migration must not stop the site. The health signal tells the dashboard why
            // the audit is not recording (SEC-09, E18).
            _health.MigrationsPending = await StillPendingAsync(cancellationToken);
            _logger.LogError(ex, "Failed to apply Forms Audit Trail database migrations: {Message}", ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// A failure is not proof that anything is pending: Migrate can fail after applying everything (for example while
    /// writing history), and a false "pending" would send the owner after a problem that is not there. One re-check
    /// decides it; if that also throws the database is unreachable, so pending stays true.
    /// </summary>
    private async Task<bool> StillPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();
            return (await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
        }
        catch
        {
            return true;
        }
    }
}
