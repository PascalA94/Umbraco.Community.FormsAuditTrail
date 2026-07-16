using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.BackgroundJobs;

/// <summary>
/// Deletes audit entries older than the configured retention period once a day.
/// Does nothing while <see cref="FormsAuditTrailOptions.RetentionDays"/> is zero (the default).
/// </summary>
public class AuditRetentionJob : IRecurringBackgroundJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _options;
    private readonly ILogger<AuditRetentionJob> _logger;

    public AuditRetentionJob(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<FormsAuditTrailOptions> options,
        ILogger<AuditRetentionJob> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public TimeSpan Period => TimeSpan.FromHours(24);

    public TimeSpan Delay => TimeSpan.FromMinutes(5);

    // Cleanup must only run on one server in a load-balanced setup
    public ServerRole[] ServerRoles => [ServerRole.Single, ServerRole.SchedulingPublisher];

    public event EventHandler PeriodChanged { add { } remove { } }

    public async Task RunJobAsync()
    {
        var retentionDays = _options.CurrentValue.RetentionDays;
        if (retentionDays <= 0)
        {
            return;
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();
            var deleted = await DeleteEntriesOlderThanAsync(db, DateTime.UtcNow.AddDays(-retentionDays));
            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Forms Audit Trail retention: deleted {Count} audit entries older than {RetentionDays} days.",
                    deleted,
                    retentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Forms Audit Trail retention job failed.");
        }
    }

    // Child change rows are removed by the database-level cascade delete
    internal static Task<int> DeleteEntriesOlderThanAsync(FormsAuditDbContext db, DateTime cutoffUtc) =>
        db.AuditEntries.Where(e => e.Timestamp < cutoffUtc).ExecuteDeleteAsync();
}
