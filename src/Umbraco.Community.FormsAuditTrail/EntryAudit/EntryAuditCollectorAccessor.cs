using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>Reaches the current request's collector from singleton or notification-handler code.</summary>
public interface IEntryAuditCollectorAccessor
{
    /// <summary>The request's collector, or null outside an HTTP request (workflows and background jobs never capture).</summary>
    EntryAuditCollector? Current { get; }
}

/// <summary>
/// Resolves through <c>RequestServices</c> rather than injecting the scoped collector, so this can be a singleton (P2)
/// and handlers resolved outside a request get null instead of a scope-validation failure.
/// </summary>
internal sealed class EntryAuditCollectorAccessor(IHttpContextAccessor httpContextAccessor) : IEntryAuditCollectorAccessor
{
    public EntryAuditCollector? Current =>
        httpContextAccessor.HttpContext?.RequestServices.GetService<EntryAuditCollector>();
}
