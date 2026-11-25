using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Umbraco.Community.FormsAuditTrail.Configuration;

/// <summary>
/// Post-configures and validates <see cref="FormsAuditTrailOptions"/>. Pattern adapted from the UserActivity options validator.
/// </summary>
internal sealed class FormsAuditTrailOptionsSetup(ILogger<FormsAuditTrailOptionsSetup> logger)
    : IPostConfigureOptions<FormsAuditTrailOptions>, IValidateOptions<FormsAuditTrailOptions>
{
    private const string Prefix = FormsAuditTrailOptions.SectionName + ":";

    public void PostConfigure(string? name, FormsAuditTrailOptions options)
    {
        // .NET binding appends array items onto a default list, so the default can only be applied
        // after binding, and only when nothing was configured (design 7.7).
        if (options.EntryAudit.AccessLogUserGroups.Count == 0)
        {
            options.EntryAudit.AccessLogUserGroups.Add(EntryAuditOptions.DefaultAccessLogGroup);
        }

        // The released retention job treats a negative value as "off", so rejecting it at start-up
        // would stop existing installs booting (plan review P11). Coerce and say so instead.
        if (options.RetentionDays < 0)
        {
            logger.LogWarning(
                "{Key} is negative and is treated as 0 (keep entries forever). Set it to 0 or a positive number of days.",
                Prefix + nameof(FormsAuditTrailOptions.RetentionDays));
            options.RetentionDays = 0;
        }
    }

    // Messages name the key only, never the value. The existing keys are never rejected here.
    public ValidateOptionsResult Validate(string? name, FormsAuditTrailOptions options)
    {
        var failures = new List<string>();
        var entryAudit = options.EntryAudit;
        var entryPrefix = Prefix + nameof(FormsAuditTrailOptions.EntryAudit) + ":";

        if (entryAudit.RetentionDays < 0)
        {
            failures.Add(entryPrefix + nameof(EntryAuditOptions.RetentionDays) + " must be 0 or greater.");
        }

        if (entryAudit.EditValueRetentionDays < 0)
        {
            failures.Add(entryPrefix + nameof(EntryAuditOptions.EditValueRetentionDays) + " must be 0 or greater.");
        }

        if (entryAudit.DeduplicateSeconds < 0)
        {
            failures.Add(entryPrefix + nameof(EntryAuditOptions.DeduplicateSeconds) + " must be 0 or greater.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>One registration shared by the composer and the tests, so tests bind exactly what ships.</summary>
internal static class FormsAuditTrailOptionsRegistration
{
    public static void Add(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FormsAuditTrailOptions>()
            .Bind(configuration.GetSection(FormsAuditTrailOptions.SectionName))
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<FormsAuditTrailOptions>, FormsAuditTrailOptionsSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FormsAuditTrailOptions>, FormsAuditTrailOptionsSetup>());
    }
}
