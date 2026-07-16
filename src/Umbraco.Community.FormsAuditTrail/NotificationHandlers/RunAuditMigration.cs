using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

public class RunAuditMigration : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRuntimeState _runtimeState;
    private readonly ILogger<RunAuditMigration> _logger;

    public RunAuditMigration(
        IServiceScopeFactory scopeFactory,
        IRuntimeState runtimeState,
        ILogger<RunAuditMigration> logger)
    {
        _scopeFactory = scopeFactory;
        _runtimeState = runtimeState;
        _logger = logger;
    }

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        // Don't attempt migrations while Umbraco is installing or upgrading — the connection
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply Forms Audit Trail database migrations: {Message}", ex.GetBaseException().Message);
        }
    }
}
