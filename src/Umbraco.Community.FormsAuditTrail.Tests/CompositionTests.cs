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
        typeof(EntryAudit.EntryAuditCapture),
        typeof(EntryAudit.FormFieldInfoProvider),
        typeof(EntryAudit.IForeignValueHandlerDetector), // factory-registered: it needs the composer's final service collection
        typeof(EntryAudit.UploadPathResolver),
        typeof(EntryAudit.UploadBackOfficeUserResolver),
        typeof(NotificationHandlers.EntrySearchResultFetchingHandler),
        typeof(NotificationHandlers.RecordFetchingHandler),
        typeof(NotificationHandlers.RecordSavingHandler),
        typeof(FormSnapshotService),
        typeof(FormDiffService),
        typeof(NotificationHandlers.FormSavingHandler),
        typeof(NotificationHandlers.FormSavedHandler),
        typeof(NotificationHandlers.FormDeletingHandler),
        typeof(NotificationHandlers.FormDeletedHandler),
        typeof(NotificationHandlers.RunAuditMigration),
    ];

    /// <summary>
    /// Types the framework builds per request or per pipeline rather than the container (MVC filters, middleware), so
    /// they are not descriptors. Tasks 12 (<c>EntryAuditActionFilter</c>) and 12a (<c>UploadAuditMiddleware</c>) add
    /// themselves here and are then created through <see cref="ActivatorUtilities"/> exactly as MVC and the pipeline do.
    /// </summary>
    internal static readonly Type[] ActivatorUtilitiesTypes = [typeof(EntryAudit.EntryAuditActionFilter), typeof(EntryAudit.UploadAuditMiddleware)];

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
            Assert.True(provider.GetServices<Umbraco.Cms.Core.Events.INotificationAsyncHandler<Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification>>().Any(h => ReferenceEquals(h, detector)));
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
        Dictionary<Type, string> notInjected = new()
        {
            [typeof(Controllers.FormsAuditApiController)] = "An MVC controller, activated by MVC and not registered in the container.",
        };

        Type[] candidates = PackageAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsNested: false } && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            // Records are data shapes (EntryAuditFilter ends in "Filter" but is not an MVC filter).
            .Where(t => t.GetMethod("<Clone>$") is null)
            .Where(t => serviceSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.Ordinal)))
            .ToArray();

        Type[] unaccounted = candidates
            .Where(t => !ExpectedPackageTypes.Contains(t) && !ActivatorUtilitiesTypes.Contains(t) && !notInjected.ContainsKey(t))
            .ToArray();

        Assert.True(unaccounted.Length == 0,
            "Package types that look injectable but are not in ExpectedPackageTypes: " + string.Join(", ", unaccounted.Select(t => t.FullName)));
    }

    private static IEnumerable<ServiceDescriptor> PackageDescriptors(IServiceCollection services) =>
        services.Where(d => d.ServiceType.Assembly == PackageAssembly
            || d.ImplementationType?.Assembly == PackageAssembly
            || (d.ImplementationFactory is not null && d.ServiceType.Assembly == PackageAssembly));

    private static (ServiceCollection Services, ServiceProvider Provider) BuildProvider()
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
        RegisterConsumedUmbracoServices(services);

        new FormsAuditComposer().Compose(builder);

        // Resolving the package's types, not building a host: the container is built with the strictest options.
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        return (services, provider);
    }

    /// <summary>
    /// Substitutes for the Umbraco and Forms services the package consumes, at the lifetimes spike S1 recorded from a
    /// running site. None is Scoped, so a singleton of ours may take any of them directly.
    /// </summary>
    private static void RegisterConsumedUmbracoServices(IServiceCollection services)
    {
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Services.IFormService>());
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Data.Storage.IRecordStorage>());
        services.AddSingleton(Substitute.For<Umbraco.Forms.Core.Security.IFormsSecurity>());
        services.AddSingleton(Substitute.For<IUserService>());
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
        services.AddTransient(_ => Substitute.For<Microsoft.AspNetCore.Authorization.IAuthorizationService>());
    }
}
