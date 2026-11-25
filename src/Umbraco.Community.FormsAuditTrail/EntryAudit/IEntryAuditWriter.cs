namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Stores one access event. There is deliberately no <see cref="CancellationToken"/> parameter: every database call
/// inside uses <see cref="CancellationToken.None"/>, so a client that aborts its request cannot cancel the evidence of
/// what it just did (SEC-02). The writer never throws; a failure comes back as <see cref="AccessWriteStatus.Failed"/>
/// and is recorded in <see cref="EntryAuditHealth"/>.
/// </summary>
/// <remarks>
/// Registered as a singleton because the singleton <c>ConfigChangeRecorder</c> consumes it. Anything scoped is resolved
/// inside the per-write scope.
/// </remarks>
public interface IEntryAuditWriter
{
    Task<AccessWriteResult> WriteAsync(AccessWrite write);
}
