using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Records <c>FileViewed</c> when Forms serves an uploaded file (SEC-01, E13). Forms serves uploads from middleware,
/// outside MVC, so <see cref="EntryAuditActionFilter"/> never sees them. It runs outside Forms' own protection
/// middleware (see <see cref="UploadAuditPipeline"/>), so it sees Forms' final status and writes only for a request
/// Forms let through.
/// </summary>
/// <remarks>
/// It never changes the response: every step after <c>next</c> is guarded, and the file name never reaches storage or
/// logs, only the hash of its path. The writer deduplicates on the path hash, so the several range requests a browser
/// makes for one open give one row.
/// </remarks>
internal sealed class UploadAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IUploadPathResolver resolver,
        IUploadUserResolver users,
        IEntryAuditWriter writer,
        IOptionsMonitor<FormsAuditTrailOptions> options,
        EntryAuditHealth health,
        ILogger<UploadAuditMiddleware> logger)
    {
        if (!ShouldAudit(context, resolver, options, health, logger))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch
        {
            // Security checkpoint L3: a failure after the file began streaming (a client abort, a broken file stream)
            // still means file content left the server, so the open is recorded. A 304 sends no content, and with no
            // response started nothing was served. The original exception is rethrown unchanged either way.
            if (context.Response.HasStarted)
            {
                await TryRecordAsync(context, resolver, users, writer, health, logger, status => status is StatusCodes.Status200OK or StatusCodes.Status206PartialContent);
            }

            throw;
        }

        // 304 counts: the user opened the file again from cache, and a rare duplicate is acceptable where a missing
        // row is not (design 7.3). Forms authorises before it answers 206 or 304 (S11), so none of these is a denied request.
        await TryRecordAsync(context, resolver, users, writer, health, logger, status => status is StatusCodes.Status200OK or StatusCodes.Status206PartialContent or StatusCodes.Status304NotModified);
    }

    private static async Task TryRecordAsync(
        HttpContext context,
        IUploadPathResolver resolver,
        IUploadUserResolver users,
        IEntryAuditWriter writer,
        EntryAuditHealth health,
        ILogger logger,
        Func<int, bool> served)
    {
        try
        {
            if (served(context.Response.StatusCode))
            {
                await writer.WriteAsync(BuildWrite(context, resolver, users));
            }
        }
        catch (Exception ex)
        {
            // The writer does not throw; this covers the path, user and detail steps.
            ReportFailure(health, logger, FormIdOrEmpty(resolver, context), ex);
        }
    }

    /// <summary>
    /// A lost row is a compliance gap, so it reaches health (and so the gap row and banner) rather than only a log line.
    /// Guarded on its own: a failing logger or health must never turn a served file into a 500. Exception type only,
    /// because a message can name the path.
    /// </summary>
    private static void ReportFailure(EntryAuditHealth health, ILogger logger, Guid formId, Exception ex)
    {
        try
        {
            health.RecordWriteFailure(AccessEventType.FileViewed, formId);
        }
        catch
        {
            // Health is in memory; if even that fails there is nowhere left to report it.
        }

        SafeLog.Run(() => logger.LogError(
            "FormsAuditTrail could not record an uploaded file open: event {EventType}, form {FormId} ({ExceptionType}).",
            AccessEventType.FileViewed,
            formId,
            ex.GetType().Name));
    }

    private static Guid FormIdOrEmpty(IUploadPathResolver resolver, HttpContext context)
    {
        try
        {
            return resolver.ResolveFormId(context.Request.Path) ?? Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private static bool ShouldAudit(
        HttpContext context, IUploadPathResolver resolver, IOptionsMonitor<FormsAuditTrailOptions> options, EntryAuditHealth health, ILogger logger)
    {
        // Runs on every request in the site, so the common case pays one span scan and no allocation. Every upload
        // path contains "upload" whatever the separators or case, so this never rejects one the resolver would accept.
        if (context.Request.Path.Value is not { } raw || raw.AsSpan().IndexOf("upload", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        try
        {
            if (!resolver.IsUploadRequest(context.Request.Path))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            // An unreadable path may be an upload we then fail to audit, so it is a gap, not a nuisance.
            ReportFailure(health, logger, Guid.Empty, ex);
            return false;
        }

        try
        {
            return options.CurrentValue.EntryAudit.Enabled;
        }
        catch
        {
            // Options that cannot be read cannot say "off", and a missing row is the worse failure.
            return true;
        }
    }

    private static AccessWrite BuildWrite(HttpContext context, IUploadPathResolver resolver, IUploadUserResolver users)
    {
        PathString path = context.Request.Path;
        string pathHash = resolver.HashPath(path);
        Guid? formId = resolver.ResolveFormId(path);
        UploadUser? user = users.Resolve(context);

        var detail = new EntryAuditDetail
        {
            FilePathHash = pathHash,
            FormUnresolved = formId is null ? true : null,
            // SEC-L5: no resolvable user is Unknown, never System, which is kept for jobs.
            UserUnresolved = user is null ? true : null,
            // An expired session is evidence, not a reason to lose the identity (E13).
            SessionExpired = user?.SessionExpired == true ? true : null,
        };

        return new AccessWrite(
            AccessEventType.FileViewed,
            formId ?? Guid.Empty,
            FormName: null,
            RecordId: null,
            RecordCount: 1,
            user?.Key ?? Guid.Empty,
            user?.Name ?? AuditUserNames.Unknown,
            detail,
            [],
            [])
        {
            // The hash is the dedupe key's filter part, so each file dedupes on its own for one user and form.
            DedupeFilterHash = pathHash,
            // With no user, every open is its own row: one shared Guid.Empty key would hide different openers behind the first.
            SkipDedupe = user is null,
        };
    }
}
