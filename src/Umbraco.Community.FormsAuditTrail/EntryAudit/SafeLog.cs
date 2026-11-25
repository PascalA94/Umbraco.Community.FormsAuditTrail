namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Runs a log call that must not throw. The capture pipeline runs inside Forms' own requests and file downloads, so a
/// broken logger or sink must never fail a Forms response or turn a stored audit row into a reported failure; there is
/// nowhere left to report the logger's own failure.
/// </summary>
/// <remarks>
/// Callers pass the exception type name, never the exception: its message can carry the values being handled.
/// </remarks>
internal static class SafeLog
{
    public static void Run(Action log)
    {
        try
        {
            log();
        }
        catch
        {
            // See summary.
        }
    }
}
