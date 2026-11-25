using Microsoft.AspNetCore.Authorization;

namespace Umbraco.Community.FormsAuditTrail.Authorization;

/// <summary>The caller belongs to a group listed in <c>EntryAudit:AccessLogUserGroups</c>.</summary>
internal sealed class AccessLogRequirement : IAuthorizationRequirement;
