using Microsoft.Extensions.Hosting;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Community.FormsAuditTrail.DemoSite;

/// <summary>
/// The one switch behind every end-to-end test helper in this site: the HOST environment is Development, and the
/// named flag is true. Shared so the seed composer, the diagnostics controller and the encryption stand-in cannot
/// drift apart on what "on" means. The environment is never read from a configuration key, because a key named
/// <c>environment</c> can be set by anything that controls configuration, while the host environment is fixed when the
/// host is built.
/// </summary>
internal static class E2EGate
{
    internal const string SeedKey = "E2E:Seed";
    internal const string DiagnosticsKey = "E2E:Diagnostics";
    internal const string SimulateEncryptionKey = "E2E:SimulateEncryption";

    /// <summary>
    /// Compose-time check. The builder exposes no environment, so it is read from the host environment instance the
    /// web application builder registered. Not found counts as not Development (fail closed).
    /// </summary>
    internal static bool IsEnabled(IUmbracoBuilder builder, string flagKey) =>
        builder.Services
            .Where(d => d.ServiceType == typeof(IHostEnvironment) || d.ServiceType == typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment))
            .Select(d => d.ImplementationInstance as IHostEnvironment)
            .Any(e => e is not null && e.IsDevelopment())
        && builder.Config.GetValue<bool>(flagKey);

    /// <summary>Request-time check, repeated so a route that slipped through still answers 404 outside Development.</summary>
    internal static bool IsEnabled(IHostEnvironment env, IConfiguration config, string flagKey) =>
        env.IsDevelopment() && config.GetValue<bool>(flagKey);
}
