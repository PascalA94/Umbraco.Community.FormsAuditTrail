using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.Controllers;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class EndpointSecurityTests
{
    [Fact]
    public void EveryPackageController_RequiresSectionAccessForms() // S1 [Critical]
    {
        Type[] controllers = typeof(FormsAuditApiController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .ToArray();

        Assert.NotEmpty(controllers);
        foreach (Type controller in controllers)
        {
            Assert.Contains(controller.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(),
                a => a.Policy == "SectionAccessForms");
            // An anonymous attribute on the class or any action would override the class-level policy.
            Assert.Empty(controller.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
            Assert.Empty(controller.GetMethods().SelectMany(m => m.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)));
        }
    }

    [Fact]
    public void AccessController_RequiresAccessLogPolicy() // @M4-T16 P15c
    {
        // Both at class level: the access-log policy narrows the Forms section policy, it does not replace it.
        string?[] policies = typeof(FormsAuditAccessApiController).GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToArray();

        Assert.Contains("SectionAccessForms", policies);
        Assert.Contains(AccessLogPolicy.Name, policies);
        Assert.True(typeof(FormsAuditAccessApiController).IsDefined(typeof(ApiControllerAttribute), false));
        Assert.Empty(typeof(FormsAuditAccessApiController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)));
    }
}
