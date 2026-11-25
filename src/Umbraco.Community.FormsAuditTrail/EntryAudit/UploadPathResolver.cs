using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Resolves the path the way the file system serves it, so the recorded form and hash are those of the file served and
/// a served request is never one the audit fails to recognise (spike S11, docs/spikes.md).
/// </summary>
public interface IUploadPathResolver
{
    internal bool IsUploadRequest(PathString path);

    internal Guid? ResolveFormId(PathString path);

    /// <summary>Lower-case hex SHA-256 of the normalised path below the upload root. The path itself is never stored.</summary>
    internal string HashPath(PathString path);
}

/// <remarks>
/// Singleton: it holds only the upload root constant. Forms has no option for the root and hard-codes
/// <c>/media/forms/upload/</c> in <c>ProtectFormUploadRequestsMiddleware</c>, so this holds the same literal and a
/// contract test pins it. A site with a non-default <c>UmbracoMediaPath</c> may not be guarded by Forms at all, and the
/// audit follows Forms: it matches the same literal and so does not see uploads served from another root (README).
/// </remarks>
internal sealed class UploadPathResolver : IUploadPathResolver
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
        // The first form_ segment decides; a later segment is never tried if that one fails to parse.
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

    private static readonly char[] TrailingIgnored = ['.', ' '];

    /// <summary>
    /// Trims trailing dots and spaces from every segment, a superset of Windows' rules, so an equivalent path is not
    /// missed; only a served status is recorded, so a non-matching variant adds no row. A <c>.</c> segment is a no-op
    /// and <c>..</c> steps up. Applied on every platform, so the result does not depend on the host's file system.
    /// A segment of only dots and spaces (<c>...</c>, a space) is an ordinary name on Windows that a later <c>..</c>
    /// removes, so it is held as a placeholder while resolving. If that reading does not land under the upload root
    /// but the reading that simply drops such segments does, the second is used: fail toward recording.
    /// </summary>
    private static string[] Segments(PathString path)
    {
        string[] raw = (path.Value ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        string[] withPlaceholders = Resolve(raw, keepEmptyAsPlaceholder: true);
        if (IsUploadFile(withPlaceholders))
        {
            return withPlaceholders;
        }

        string[] dropped = Resolve(raw, keepEmptyAsPlaceholder: false);
        return IsUploadFile(dropped) ? dropped : withPlaceholders;
    }

    private static bool IsUploadFile(string[] segments) => segments.Length > UploadRoot.Length && UnderRoot(segments);

    private static string[] Resolve(string[] raw, bool keepEmptyAsPlaceholder)
    {
        var resolved = new List<string>();
        foreach (string part in raw)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                }

                continue;
            }

            string segment = part.TrimEnd(TrailingIgnored);
            if (segment.Length > 0)
            {
                resolved.Add(segment);
            }
            else if (keepEmptyAsPlaceholder)
            {
                resolved.Add(string.Empty);
            }
        }

        return [.. resolved.Where(static s => s.Length > 0)];
    }

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
internal static class FileViewedMatcher
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
