namespace Umbraco.Community.FormsAuditTrail.Persistence;

/// <summary>
/// Column widths shared by the model configuration and the writer's truncation, so the two cannot drift (SEC-02).
/// Changing one is a schema change and needs a migration for both providers.
/// </summary>
internal static class AccessColumnLimits
{
    public const int FormName = 500;
    public const int UserName = 500;
    public const int FieldCaption = 500;
    public const int StateKey = 100;
    public const int StateValue = 4000;
}
