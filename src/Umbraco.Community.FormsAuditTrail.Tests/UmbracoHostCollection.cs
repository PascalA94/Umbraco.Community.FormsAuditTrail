using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Tests that boot a real Umbraco runtime or change process-wide environment variables. One collection with
/// parallelisation off, so none of them runs beside another test that could observe the change.
/// </summary>
[CollectionDefinition("UmbracoHost", DisableParallelization = true)]
public class UmbracoHostCollection
{
}
