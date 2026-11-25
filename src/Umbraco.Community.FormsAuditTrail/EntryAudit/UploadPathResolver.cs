using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Reads Forms' upload paths the way Forms does (spike S11, docs/spikes.md), so a request Forms serves is never one the
/// audit fails to recognise.
/// </summary>
public interface IUploadPathResolver
{
    bool IsUploadRequest(PathString path);

    Guid? ResolveFormId(PathString path);

    /// <summary>Lower-case hex SHA-256 of the normalised path below the upload root. The path itself is never stored.</summary>
    string HashPath(PathString path);
}

/// <remarks>
/// Singleton: it holds only the upload root constant. Forms has no option for the root and hard-codes
/// <c>/media/forms/upload/</c> in <c>ProtectFormUploadRequestsMiddleware</c>, so this holds the same literal and a
/// contract test pins it. A site with a non-default <c>UmbracoMediaPath</c> may not be guarded by Forms at all, and the
/// audit follows Forms: it matches the same literal and so does not see uploads served from another root (README).
/// </remarks>
public sealed class UploadPathResolver : IUploadPathResolver
{
    internal static readonly string[] UploadRoot = ["media", "forms", "upload"];

    /// <summary>Forms compares this prefix case-sensitively: <c>FORM_</c> gets a 403 from Forms.</summary>
    internal const string FormSegmentPrefix = "form_";

    public bool IsUploadRequest(PathString path)
    {
        string[] segments = Segments(path);
        // A request for the root folder itself serves no file, so it is not an open.
        return segments.Length > UploadRoot.Length && UnderRoot(segments);
    }

    public Guid? ResolveFormId(PathString path)
    {
        // Forms takes the first form_ segment and parses the rest; a later segment is never tried if that one fails.
        string? segment = Segments(path).FirstOrDefault(s => s.StartsWith(FormSegmentPrefix, StringComparison.Ordinal));
        return segment is not null && Guid.TryParse(segment.AsSpan(FormSegmentPrefix.Length), out Guid formId) ? formId : null;
    }

    public string HashPath(PathString path)
    {
        string[] segments = Segments(path);
        IEnumerable<string> relative = UnderRoot(segments) ? segments.Skip(UploadRoot.Length) : segments;
        string normalised = string.Join('/', relative).ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }

    // PathString holds the query separately, so the hash never includes one. A backslash separates too: the file
    // provider on Windows treats it as a separator, so matching only '/' could miss a path that serves a file.
    private static readonly char[] Separators = ['/', '\\'];

    private static string[] Segments(PathString path) =>
        (path.Value ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static bool UnderRoot(string[] segments)
    {
        if (segments.Length < UploadRoot.Length)
        {
            return false;
        }

        for (int i = 0; i < UploadRoot.Length; i++)
        {
            if (!string.Equals(segments[i], UploadRoot[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Matches an entry's own upload values to a <c>FileViewed</c> row, by hashing them at read time (design 7.3). It works
/// while the record exists and stops working after erasure, which is intended.
/// </summary>
public static class FileViewedMatcher
{
    public static bool Matches(string filePathHash, IEnumerable<string> uploadValues, IUploadPathResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(uploadValues);
        ArgumentNullException.ThrowIfNull(resolver);

        foreach (string value in uploadValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // A stored value is a site-relative path; a query or fragment is not part of what Forms served.
            int cut = value.IndexOfAny(['?', '#']);
            string pathText = cut >= 0 ? value[..cut] : value;
            if (!pathText.StartsWith('/'))
            {
                continue;
            }

            var path = new PathString(pathText);
            if (resolver.IsUploadRequest(path)
                && string.Equals(resolver.HashPath(path), filePathHash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
