using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// The one place <see cref="UploadAuditMiddleware"/> is added to the pipeline. The middleware exists because Forms
/// serves uploads outside MVC, so the action filter never sees them (SEC-01, spike S11).
/// </summary>
/// <remarks>
/// It is inserted at <c>PrePipeline</c> immediately before Forms' own filter, so a Forms 403 is observed and skipped.
/// No later stage can be used: for a served upload the media file provider short-circuits before <c>PreRouting</c>.
/// Inserting after Forms' filter would miss every denied request, and at the very start would run before the
/// backoffice filter for no gain. If the anchor is ever missing the filter goes first, which still sees every outcome.
/// </remarks>
internal static class UploadAuditPipeline
{
    internal const string FormsFilterName = "ProtectFormUploadRequestsMiddleware";
    internal const string FilterName = "FormsAuditTrailUploadAudit";

    public static void Use(IApplicationBuilder app) => app.UseMiddleware<UploadAuditMiddleware>();

    public static UmbracoPipelineFilter CreateFilter() => new(FilterName) { PrePipeline = Use };

    /// <summary>PostConfigure so Forms' own Configure has already added its filter, whatever the registration order.</summary>
    public static IServiceCollection AddUploadAuditFilter(this IServiceCollection services) =>
        services.PostConfigure<UmbracoPipelineOptions>(Insert);

    internal static void Insert(UmbracoPipelineOptions options)
    {
        if (options.PipelineFilters.Any(f => f.Name == FilterName))
        {
            return;
        }

        int anchor = 0;
        for (int i = 0; i < options.PipelineFilters.Count; i++)
        {
            if (options.PipelineFilters[i].Name == FormsFilterName)
            {
                anchor = i;
                break;
            }
        }

        options.PipelineFilters.Insert(anchor, CreateFilter());
    }
}
