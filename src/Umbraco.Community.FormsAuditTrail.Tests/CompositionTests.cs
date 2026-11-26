using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Composers;
using Umbraco.Community.FormsAuditTrail.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// P2: a singleton that captures a scoped service is a runtime failure that only shows under the real container, so
/// this builds the package's registrations with scope validation on and resolves every one of them.
/// </summary>
public class CompositionTests
{
    private static readonly Assembly PackageAssembly = typeof(FormsAuditComposer).Assembly;

    /// <summary>
    /// Every type the composer registers with an implementation in this package. A later task that registers a new
    /// service adds it here, so the lifetime check below covers it; the completeness test fails until it does.
    /// </summary>
    internal static readonly Type[] ExpectedPackageTypes =
    [
        typeof(Persistence.FormsAuditDbContext), // factory-registered: it picks the provider's context at resolve time
        typeof(Persistence.SqliteFormsAuditDbContext),
        typeof(Persistence.SqlServerFormsAuditDbContext),
        typeof(Configuration.FormsAuditTrailOptionsSetup),
        typeof(EntryAudit.AuditStateStore),
        typeof(EntryAudit.UmbracoFormsRecordReader),
        typeof(BackgroundJobs.AuditRetentionJob),
        typeof(EntryAudit.EntryAuditCollector),
        typeof(EntryAudit.EntryAuditCollectorAccessor),
        typeof(EntryAudit.EntryAuditDeduplicator),
        typeof(EntryAudit.EntryAuditHealth),
        typeof(EntryAudit.EntryAuditWriter),
        typeof(EntryAudit.ConfigChangeRecorder),
        typeof(EntryAudit.EntryAuditCapture),
        typeof(EntryAudit.FormFieldInfoProvider),
        typeof(EntryAudit.RequestFormCache),
        typeof(EntryAudit.IForeignValueHandlerDetector), // factory-registered: it needs the composer's final service collection
        typeof(EntryAudit.UploadPathResolver),
        typeof(EntryAudit.UploadBackOfficeUserResolver),
        typeof(NotificationHandlers.EntrySearchResultFetchingHandler),
        typeof(NotificationHandlers.RecordFetchingHandler),
        typeof(NotificationHandlers.RecordSavingHandler),
        typeof(NotificationHandlers.RecordDeletingHandler),
        typeof(EntryAudit.DeletedRecordScrubber),
        typeof(FormSnapshotService),
        typeof(FormDiffService),
        typeof(NotificationHandlers.FormSavingHandler),
        typeof(NotificationHandlers.FormSavedHandler),
        typeof(NotificationHandlers.FormDeletingHandler),
        typeof(NotificationHandlers.FormDeletedHandler),
        typeof(NotificationHandlers.RunAuditMigration),
        typeof(Authorization.AccessLogAuthorizationHandler),
        typeof(Authorization.AccessLogGroupAliasCheck),
        typeof(EntryAudit.SensitiveDataPermission),
    ];

    /// <summary>
    /// Types the framework builds per request or per pipeline rather than the container (MVC filters, middleware), so
    /// they are not descriptors. Tasks 12 (<c>EntryAuditActionFilter</c>) and 12a (<c>UploadAuditMiddleware</c>) add
    /// themselves here and are then created through <see cref="ActivatorUtilities"/> exactly as MVC and the pipeline do.
    /// </summary>
    internal static readonly Type[] ActivatorUtilitiesTypes = [typeof(EntryAudit.EntryAuditActionFilter), typeof(EntryAudit.UploadAuditMiddleware), typeof(Controllers.FormsAuditApiController), typeof(Controllers.FormsAuditAccessApiController)];

    [Fact]
    public void Compose_ValidateScopesAndOnBuild_ResolvesEveryPackageType() // P2
    {
        (ServiceCollection services, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            using IServiceScope scope = provider.CreateScope();
            foreach (ServiceDescriptor descriptor in PackageDescriptors(services))
            {
                object resolved = descriptor.ServiceType.IsGenericTypeDefinition
                    ? throw new InvalidOperationException($"Open generic {descriptor.ServiceType} cannot be resolved by this test.")
                    : scope.ServiceProvider.GetRequiredService(descriptor.ServiceType);
                Assert.NotNull(resolved);
            }

            foreach (Type type in ActivatorUtilitiesTypes)
            {
                // Middleware is built with its RequestDelegate, as UseMiddleware does; the pipeline supplies it, not the container.
                object[] arguments = type.Name.EndsWith("Middleware", StringComparison.Ordinal)
                    ? [(Microsoft.AspNetCore.Http.RequestDelegate)(_ => Task.CompletedTask)]
                    : [];
                Assert.NotNull(ActivatorUtilities.CreateInstance(scope.ServiceProvider, type, arguments));
            }
        }
    }

    [Fact]
    public void Compose_RegistersTheActionFilterGlobally() // @M2-T12
    {
        (_, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            var mvc = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.MvcOptions>>().Value;
            Assert.Single(mvc.Filters.OfType<Microsoft.AspNetCore.Mvc.TypeFilterAttribute>(), f => f.ImplementationType == typeof(EntryAudit.EntryAuditActionFilter));
        }
    }

    [Fact]
    public void Compose_StartedHandlers_IncludeTheSingletonRecorder_AfterTheMigration() // R53 item 1, item 8
    {
        (_, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            var handlers = provider
                .GetServices<Umbraco.Cms.Core.Events.INotificationAsyncHandler<Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification>>()
                .ToList();

            int recorder = handlers.FindIndex(h => ReferenceEquals(h, provider.GetRequiredService<EntryAudit.ConfigChangeRecorder>()));
            int migration = handlers.FindIndex(h => h is NotificationHandlers.RunAuditMigration);
            Assert.True(recorder >= 0, "The recorder singleton is not among the started handlers.");
            Assert.True(migration >= 0);
            Assert.True(recorder > migration, "The recorder must run after RunAuditMigration.");
        }
    }

    [Fact]
    public void Compose_SingletonsDoNotCaptureScopedServices() // P2
    {
        (_, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            // Resolving from the root provider is what a singleton consumer does; scope validation throws if any
            // singleton in the graph holds a scoped service.
            Assert.NotNull(provider.GetRequiredService<EntryAudit.IEntryAuditCollectorAccessor>());
            Assert.NotNull(provider.GetRequiredService<EntryAudit.EntryAuditDeduplicator>());
            Assert.NotNull(provider.GetRequiredService<EntryAudit.IAuditStateStore>());
        }
    }

    [Fact]
    public void Compose_AccessorReturnsNullOutsideARequestAndTheScopedCollectorInside()
    {
        (_, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            var accessor = provider.GetRequiredService<EntryAudit.IEntryAuditCollectorAccessor>();
            Assert.Null(accessor.Current);

            using IServiceScope scope = provider.CreateScope();
            var http = provider.GetRequiredService<IHttpContextAccessor>();
            http.HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

            Assert.Same(scope.ServiceProvider.GetRequiredService<EntryAudit.EntryAuditCollector>(), accessor.Current);
        }
    }

    [Fact]
    public void Compose_ForeignValueHandlerDetector_IsOneSingletonAlsoRunAtStarted() // @M3-T13 S10
    {
        (_, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            var detector = provider.GetRequiredService<EntryAudit.IForeignValueHandlerDetector>();

            Assert.Same(detector, provider.GetRequiredService<EntryAudit.IForeignValueHandlerDetector>());
            Assert.Contains(provider.GetServices<Umbraco.Cms.Core.Events.INotificationAsyncHandler<Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification>>(), h => ReferenceEquals(h, detector));
        }
    }

    [Fact]
    public void Compose_AccessLogPolicyIsRegisteredAndItsHandlerIsNeverScoped() // @M4-T15, UserActivity S6
    {
        (ServiceCollection services, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Authorization.AuthorizationOptions>>().Value;
            Assert.NotNull(options.GetPolicy(Authorization.AccessLogPolicy.Name));

            ServiceDescriptor handler = Assert.Single(services, d => d.ImplementationType == typeof(Authorization.AccessLogAuthorizationHandler));
            Assert.Equal(typeof(Microsoft.AspNetCore.Authorization.IAuthorizationHandler), handler.ServiceType);
            Assert.NotEqual(ServiceLifetime.Scoped, handler.Lifetime);
        }
    }

    [Fact]
    public async Task Compose_ForeignValueHandlerDetector_OwnRegistrationsAreNotForeign() // @M2-FIX C11 S10
    {
        // The detector counts a factory-only registration as foreign. If one of our own fetch handlers were registered
        // that way (or an Umbraco upgrade changed how AddNotificationHandler registers), every site would be flagged,
        // so this runs it over the real composer's service collection.
        (_, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            var detector = provider.GetRequiredService<EntryAudit.IForeignValueHandlerDetector>();
            var started = (Umbraco.Cms.Core.Events.INotificationAsyncHandler<Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification>)detector;

            await started.HandleAsync(new Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification(false), CancellationToken.None);

            Assert.False(detector.HasForeignValueHandlers);
        }
    }

    [Fact]
    public async Task Compose_AccessLogPolicyShape_CarriesSchemeAuthenticatedUserAndSectionRequirements() // @M4-T15 fix1, merge-fix 21 R75
    {
        // The scheme is the Management API token scheme that Forms' SectionAccessForms and Umbraco's policies name. The
        // backoffice cookie scheme was pinned here until the demo showed its forbid redirecting a denied API caller to
        // the login page (302) instead of 403; ruling R75 moved the policy to the token scheme, and the cookie must stay out.
        (_, ServiceProvider provider) = BuildProvider(realAuthorization: true);
        using (provider)
        {
            var policy = await provider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>()
                .GetPolicyAsync(Authorization.AccessLogPolicy.Name);

            Assert.NotNull(policy);
            Assert.Equal([OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme], policy.AuthenticationSchemes);
            Assert.DoesNotContain(Umbraco.Cms.Core.Constants.Security.BackOfficeAuthenticationType, policy.AuthenticationSchemes);
            Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
#if UMBRACO_17
            // Forms 17's section policy is a claim check, which the access-log policy repeats.
            Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement c
                && c.ClaimType == "http://umbraco.org/2015/02/identity/claims/backoffice/allowedapp"
                && c.AllowedValues is not null && c.AllowedValues.Contains("forms"));
#else
            Assert.Contains(policy.Requirements, r => r is Umbraco.Forms.Web.Authorization.SectionAccess.SectionAccessRequirement s && s.SectionAliases.Contains("forms"));
#endif
            Assert.Contains(policy.Requirements, r => r is Authorization.AccessLogRequirement);
        }
    }

    [Theory]
    [InlineData("admin", true, true)]
    [InlineData("editor", true, false)]
    [InlineData("admin", false, false)] // the real Forms section check must be able to deny on its own
    public async Task Compose_RealAuthorizationService_AuthorisesBackofficePrincipalAgainstThePolicy(string group, bool formsSection, bool expected) // @M4-T15 fix1, @M4-T16 fix1
    {
        (ServiceCollection services, ServiceProvider provider) = BuildProvider(realAuthorization: true);
        using (provider)
        {
            // Only the Umbraco boundary is substituted: the user service and the helper that turns the principal into a
            // user. The Forms section check (18: Forms' real section handler; 17: the allowedapp claim Forms 17's policy
            // requires), the policy, the real IAuthorizationService and our handler are composed.
            var key = Guid.NewGuid();
            var groupSub = Substitute.For<Umbraco.Cms.Core.Models.Membership.IReadOnlyUserGroup>();
            groupSub.Alias.Returns(group);
            var user = Substitute.For<Umbraco.Cms.Core.Models.Membership.IUser>();
            user.IsApproved.Returns(true);
            user.Groups.Returns(new List<Umbraco.Cms.Core.Models.Membership.IReadOnlyUserGroup> { groupSub });
            user.AllowedSections.Returns(formsSection ? ["content", "forms"] : ["content"]);
            provider.GetRequiredService<IUserService>().GetAsync(key).Returns(user);
            var helper = provider.GetRequiredService<Umbraco.Cms.Core.Security.Authorization.IAuthorizationHelper>();
            helper.TryGetUmbracoUser(Arg.Any<System.Security.Principal.IPrincipal>(), out Arg.Any<Umbraco.Cms.Core.Models.Membership.IUser?>())
                .Returns(call =>
                {
                    call[1] = user;
                    return true;
                });
            helper.GetUmbracoUser(Arg.Any<System.Security.Principal.IPrincipal>()).Returns(user);

            // The token carries one allowedapp claim per allowed section, as Umbraco issues it. Forms 17's check reads it;
            // Forms 18's section handler reads the user's sections instead and ignores the claim.
            var claims = new List<System.Security.Claims.Claim> { new("sub", key.ToString()) };
            if (formsSection)
            {
                claims.Add(new("http://umbraco.org/2015/02/identity/claims/backoffice/allowedapp", "forms"));
            }

            var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                claims, Umbraco.Cms.Core.Constants.Security.BackOfficeAuthenticationType));

            using IServiceScope scope = provider.CreateScope();
            var authorization = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
            var result = await authorization.AuthorizeAsync(principal, null, Authorization.AccessLogPolicy.Name);

            Assert.Equal(expected, result.Succeeded);
        }
    }

    [Fact]
    public void Compose_ExpectedPackageTypes_CoversEveryRegisteredPackageType()
    {
        (ServiceCollection services, ServiceProvider provider) = BuildProvider();
        using (provider)
        {
            Type[] registered = PackageDescriptors(services)
                // A factory hides the concrete type, so its service type stands in for it.
                .Select(d => d.ImplementationType ?? d.ImplementationInstance?.GetType() ?? d.ServiceType)
                .Distinct()
                .ToArray();

            Assert.Empty(registered.Where(t => !ExpectedPackageTypes.Contains(t)).Select(t => t.FullName));
            Assert.Empty(ExpectedPackageTypes.Where(t => !registered.Contains(t)).Select(t => t.FullName));
        }
    }

    [Fact]
    public void Compose_ExpectedPackageTypes_CoversEveryInjectableTypeByReflection()
    {
        // The list above could drift from the code if someone adds a service and forgets both the registration and the
        // list. Any concrete package class named like a service must be listed, created by ActivatorUtilities, or be an
        // explicit, reasoned exception.
        string[] serviceSuffixes = ["Handler", "Job", "Store", "Reader", "Collector", "Accessor", "Deduplicator", "Writer", "Health", "Middleware", "Filter", "Service"];
        Type[] candidates = PackageAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsNested: false } && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            // Records are data shapes (EntryAuditFilter ends in "Filter" but is not an MVC filter).
            .Where(t => t.GetMethod("<Clone>$") is null)
            .Where(t => serviceSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.Ordinal)))
            .ToArray();

        Type[] unaccounted = candidates
            .Where(t => !ExpectedPackageTypes.Contains(t) && !ActivatorUtilitiesTypes.Contains(t))
            .ToArray();

        Assert.True(unaccounted.Length == 0,
            "Package types that look injectable but are not in ExpectedPackageTypes: " + string.Join(", ", unaccounted.Select(t => t.FullName)));
    }

    private static IEnumerable<ServiceDescriptor> PackageDescriptors(IServiceCollection services) =>
        services.Where(d => d.ServiceType.Assembly == PackageAssembly
            || d.ImplementationType?.Assembly == PackageAssembly
            || (d.ImplementationFactory is not null && d.ServiceType.Assembly == PackageAssembly));

    private static (ServiceCollection Services, ServiceProvider Provider) BuildProvider(bool realAuthorization = false)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN"] = "Data Source=:memory:",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
        }).Build();

        var services = new ServiceCollection();
        IUmbracoBuilder builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        builder.Config.Returns(config);

        services.AddSingleton(config);
        services.AddLogging();
        RegisterConsumedUmbracoServices(services, realAuthorization);

        new FormsAuditComposer().Compose(builder);

        // Resolving the package's types, not building a host: the container is built with the strictest options.
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        return (services, provider);
    }

    /// <summary>
    /// Substitutes for the Umbraco and Forms services the package consumes, at the lifetimes spike S1 recorded from a
    /// running site. None is Scoped, so a singleton of ours may take any of them directly.
    /// </summary>
    private static void RegisterConsumedUmbracoServices(IServiceCollection services, bool realAuthorization = false)
    {
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Services.IFormService>());
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Data.Storage.IRecordStorage>());
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Security.IFormsSecurity>());
        services.AddSingleton(Substitute.For<IUserService>());
        services.AddSingleton(Substitute.For<IUserGroupService>());
        services.AddSingleton(Substitute.For<Umbraco.Cms.Core.Scoping.ICoreScopeProvider>());
        services.AddSingleton(Substitute.For<Umbraco.Cms.Infrastructure.Scoping.IScopeProvider>());
        services.AddSingleton(Substitute.For<Umbraco.Cms.Core.Security.IBackOfficeSecurityAccessor>());
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Searchers.IFormRecordSearcher>());
        services.AddSingleton(Substitute.For<IRuntimeState>());
        // Not in the S1 table (the spike did not consume it). Forms registers its storage services as singletons, and
        // the snapshot service is the only consumer here; if a later spike finds it Scoped, change it here.
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Services.IFieldTypeStorage>());
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Services.IWorkflowService>());
        // A collection is a concrete class, so a real empty one stands in for the one Forms builds from its providers.
        services.AddSingleton(new Umbraco.Forms.Core.Providers.WorkflowCollection(() => []));
        // Registered Transient by Umbraco (S1): a singleton that takes it still works, but it must not be Scoped.
        if (realAuthorization)
        {
            // A substituted helper that turns the principal into a user. On 18, Forms' own section handler over it, so a
            // denied Forms section really denies (Task 16 fix1); Forms 17 has no section handler, its check is a claim.
            services.AddSingleton(Substitute.For<Umbraco.Cms.Core.Security.Authorization.IAuthorizationHelper>());
#if !UMBRACO_17
            services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Umbraco.Forms.Web.Authorization.SectionAccess.SectionAccessHandler>();
#endif
        }
        else
        {
            services.AddTransient(_ => Substitute.For<Microsoft.AspNetCore.Authorization.IAuthorizationService>());
        }

        // The policy cache that AddAuthorization registers needs it; the host provides it in a running site.
        services.AddSingleton<Microsoft.AspNetCore.Routing.EndpointDataSource>(new Microsoft.AspNetCore.Routing.CompositeEndpointDataSource([]));
    }
}
