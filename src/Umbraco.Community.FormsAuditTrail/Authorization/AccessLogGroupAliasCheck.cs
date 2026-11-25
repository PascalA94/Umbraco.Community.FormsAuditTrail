using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Configuration;

namespace Umbraco.Community.FormsAuditTrail.Authorization;

/// <summary>
/// Warns once per configured access-log alias that matches no user group, at start-up and whenever the options change.
/// A typo here would otherwise lock everyone out of the access log with no message (T21).
/// </summary>
/// <remarks>
/// Singleton, so the options subscription is made once. It only warns and never throws: a failed group lookup must not
/// stop the site starting.
/// </remarks>
public sealed class AccessLogGroupAliasCheck : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    internal const int MaxGroupsChecked = 1000;

    private readonly IOptionsMonitor<FormsAuditTrailOptions> _options;
    private readonly IUserGroupService _groups;
    private readonly ILogger<AccessLogGroupAliasCheck> _logger;
    private readonly object _gate = new();
    private string? _lastChecked;
    private int _subscribed;

    public AccessLogGroupAliasCheck(
        IOptionsMonitor<FormsAuditTrailOptions> options,
        IUserGroupService groups,
        ILogger<AccessLogGroupAliasCheck> logger)
    {
        _options = options;
        _groups = groups;
        _logger = logger;
    }

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            _options.OnChange((_, _) => _ = CheckAsync(CancellationToken.None));
        }

        await CheckAsync(cancellationToken);
    }

    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            string[] configured = _options.CurrentValue.EntryAudit.AccessLogUserGroups
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // OnChange fires for any change in the options section and sometimes twice for one edit, so an unchanged
            // alias set is not warned about again.
            var signature = string.Join('\n', configured.Select(a => a.ToLowerInvariant()));
            lock (_gate)
            {
                if (signature == _lastChecked)
                {
                    return;
                }

                _lastChecked = signature;
            }

            if (configured.Length == 0)
            {
                return;
            }

            // One lookup for all aliases rather than one per alias, bounded so a site with a huge number of groups is not
            // loaded whole at start-up. Past the bound an alias may be warned about wrongly, which is harmless.
            var existing = new HashSet<string>(
                (await _groups.GetAllAsync(0, MaxGroupsChecked)).Items.Select(g => g.Alias),
                StringComparer.OrdinalIgnoreCase);

            foreach (var alias in configured.Where(a => !existing.Contains(a)))
            {
                _logger.LogWarning(
                    "FormsAuditTrail:EntryAudit:AccessLogUserGroups lists '{Alias}', but no user group has that alias. Nobody can read the access log through it.",
                    alias);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Forget the set so the next change retries instead of staying silent after a failed lookup.
            lock (_gate)
            {
                _lastChecked = null;
            }

            _logger.LogWarning("Could not check the access-log user group aliases ({ExceptionType}).", ex.GetType().Name);
        }
    }
}
