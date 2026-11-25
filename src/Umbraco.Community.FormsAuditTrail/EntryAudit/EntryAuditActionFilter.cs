using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// The MVC adapter for <see cref="EntryAuditCapture"/>, registered globally (spec 7.3). Spike S1 showed a global action
/// filter sees all ten audited Forms controllers by <c>ControllerTypeInfo</c>, so no middleware fallback is built.
/// </summary>
/// <remarks>
/// It runs on every MVC action in the site, so an unrelated request costs one type check and one dictionary lookup.
/// The scoped capture is resolved from <c>RequestServices</c> only on a match, rather than injected, so other requests
/// never build it. The write happens synchronously after <c>await next()</c>, when Forms' scope has completed; on SQLite
/// that adds a writer that can trigger Forms' bulk-delete hang under concurrent load (S3), which Pascal accepted on
/// 2026-11-16 with a README note, since a queued write could be lost. Health and the security accessor are singletons
/// and the user is resolved per call, only for a failure's log line (P2).
/// </remarks>
internal sealed class EntryAuditActionFilter(
    IOptionsMonitor<FormsAuditTrailOptions> options,
    EntryAuditHealth health,
    IBackOfficeSecurityAccessor securityAccessor,
    ILogger<EntryAuditActionFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor
            || !FormsControllerMap.Audited.TryGetValue(descriptor.ControllerTypeInfo.AsType(), out AccessEventType kind))
        {
            await next();
            return;
        }

        (EntryAuditCapture? capture, bool refuse) = TryBegin(context, descriptor, kind);
        if (refuse)
        {
            // SEC-02: capture could not start for an export or download under FailClosedOnExport. Refused before the
            // action runs, so Forms never generates or serves a file that would leave no audit row.
            context.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
            return;
        }

        if (capture is null)
        {
            await next();
            return;
        }

        ActionExecutedContext executed = await next();

        if (kind is (AccessEventType.Exported or AccessEventType.ExportDownloaded) && IsUnrecognisedResult(executed))
        {
            // Security checkpoint L4: Forms answers an export with an OkObjectResult and a download with a
            // PhysicalFileResult (S1, pinned in FormsContractTests). Any other result type may still have handed out the
            // file, so it cannot be read as "nothing to record": it is a failed audit, and under FailClosedOnExport a 503.
            ReportFailure(kind, context, $"Forms returned an unrecognised result type ({executed.Result?.GetType().Name ?? "none"})");
            if (Refused(kind, "the export result type is not recognised"))
            {
                executed.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
            }

            return;
        }

        // CompleteAsync never throws; it reports what to do with the response instead.
        CaptureOutcome outcome = await capture.CompleteAsync(IsSuccess(executed), (executed.Result as ObjectResult)?.Value);
        if (outcome.ReplaceWith503)
        {
            // Only reached for an export or download under FailClosedOnExport (SEC-02). The status alone is returned,
            // so the refused file name and contents never reach the client.
            executed.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// True when the action completed without an unhandled exception but with a result <see cref="IsSuccess"/> cannot
    /// classify, so whether Forms served anything is unknown. A denial is not unknown: it served nothing, so it must not
    /// count as a failed audit, add an <c>AuditGap</c> or become a 503.
    /// </summary>
    internal static bool IsUnrecognisedResult(ActionExecutedContext executed) =>
        (executed.Exception is null || executed.ExceptionHandled)
        && executed.Result is not (FileResult or ObjectResult or StatusCodeResult)
        && !IsDenial(executed.Result);

    /// <summary>
    /// An authentication or authorisation outcome, or any result that states a non-2xx status. A result with no status
    /// or a 2xx one could still have served the file, so it is left to <see cref="IsUnrecognisedResult"/>.
    /// </summary>
    private static bool IsDenial(IActionResult? result) => result switch
    {
        ForbidResult or ChallengeResult or SignOutResult => true,
        IStatusCodeActionResult { StatusCode: { } status } => status is < 200 or > 299,
        _ => false,
    };

    /// <summary>
    /// Success as spec 7.1 step 4 defines it: no unhandled exception, and an object or status result with no status or a
    /// 2xx, or any file result. Anything else records nothing; for an export or download a result type this does not
    /// recognise is handled first, as a failed audit (see <see cref="IsUnrecognisedResult"/>).
    /// </summary>
    internal static bool IsSuccess(ActionExecutedContext executed)
    {
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return false;
        }

        return executed.Result switch
        {
            FileResult => true,
            ObjectResult objectResult => objectResult.StatusCode is null or (>= 200 and <= 299),
            StatusCodeResult statusResult => statusResult.StatusCode is >= 200 and <= 299,
            _ => false,
        };
    }

    /// <summary>
    /// Starts capture. Never throws: a failure fails open (E12), unless the request is an export or download and
    /// <c>FailClosedOnExport</c> is on, in which case <c>Refuse</c> is true.
    /// </summary>
    private (EntryAuditCapture? Capture, bool Refuse) TryBegin(ActionExecutingContext context, ControllerActionDescriptor descriptor, AccessEventType kind)
    {
        try
        {
            EntryAuditCapture? capture = context.HttpContext.RequestServices.GetService<EntryAuditCapture>();
            if (capture is null)
            {
                return (null, CaptureUnavailable(kind, context, "the capture service is not available"));
            }

            var request = new CaptureRequest(
                descriptor.ControllerTypeInfo.AsType(),
                context.RouteData.Values,
                context.HttpContext.Request.Query,
                new ReadOnlyDictionary<string, object?>(context.ActionArguments));

            if (capture.TryBegin(request, kind))
            {
                return (capture, false);
            }

            // The capture has already logged why it could not start, and counted it in health when it was a failure.
            return (null, capture.RefuseBeforeAction && Refused(kind, "capture could not start"));
        }
        catch (Exception ex)
        {
            return (null, CaptureUnavailable(kind, context, $"capture could not start ({ex.GetType().Name})"));
        }
    }

    /// <summary>
    /// SEC-02: capture never reached the request, so the action runs unrecorded. That is a failure health must count,
    /// unless capture is off for this kind, in which case nothing was due to be recorded.
    /// </summary>
    private bool CaptureUnavailable(AccessEventType kind, ActionExecutingContext context, string reason)
    {
        if (!IsCaptured(kind))
        {
            return false;
        }

        ReportFailure(kind, context, reason);
        return Refused(kind, reason);
    }

    private bool IsCaptured(AccessEventType kind)
    {
        try
        {
            EntryAuditOptions audit = options.CurrentValue.EntryAudit;
            return audit.Enabled && (kind != AccessEventType.ListViewed || audit.LogListViews);
        }
        catch
        {
            // Options that cannot be read cannot say capture is off, and a missing row is the worse failure.
            return true;
        }
    }

    private void ReportFailure(AccessEventType kind, ActionExecutingContext context, string reason)
    {
        Guid? formId = BestEffortFormId(context);
        try
        {
            health.RecordWriteFailure(kind, formId ?? Guid.Empty);
        }
        catch
        {
            // Health is in memory; if even that fails there is nowhere left to report it.
        }

        SafeLog.Run(() => logger.LogError(
            "FormsAuditTrail could not record an entry access because {Reason}: event {EventType}, form {FormId}, user {UserKey}; the request is not recorded.",
            reason,
            kind,
            formId,
            AuditHandlerHelpers.CurrentUserKeyOrEmpty(securityAccessor)));
    }

    /// <summary>
    /// For the failure log line only, when the capture's own reader may be what failed: the form id from the route or
    /// query, where spike S1 found every audited controller that names one puts it.
    /// </summary>
    private static Guid? BestEffortFormId(ActionExecutingContext context)
    {
        try
        {
            object? value = context.RouteData.Values.TryGetValue("formId", out object? routed) ? routed : context.HttpContext.Request.Query["formId"].ToString();
            return value switch
            {
                Guid g => g,
                _ when Guid.TryParse(value?.ToString(), out Guid parsed) => parsed,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private bool Refused(AccessEventType kind, string reason)
    {
        if (!EntryAuditCapture.FailsClosed(kind, options))
        {
            return false;
        }

        SafeLog.Run(() => logger.LogError("FormsAuditTrail refused an entry {EventType} with 503 because {Reason} and FailClosedOnExport is on.", kind, reason));
        return true;
    }
}
