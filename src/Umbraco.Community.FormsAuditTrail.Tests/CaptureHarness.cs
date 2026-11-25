using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Record = Umbraco.Forms.Core.Persistence.Dtos.Record;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Searchers;
using Umbraco.Forms.Core.Services;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Drives <see cref="EntryAuditActionFilter"/> the way MVC does: a request scope, an <see cref="ActionExecutingContext"/>
/// whose descriptor names a Forms controller type, and a <c>next</c> delegate that plays Forms by publishing the fetch
/// notifications to the real handlers before returning its result. The writer is a substitute unless a test asks for
/// the real singleton writer on a SQLite file.
/// </summary>
internal sealed class CaptureHarness : IDisposable
{
    public static readonly Guid FormId = Guid.NewGuid();
    public static readonly Guid UserKey = Guid.NewGuid();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"formsaudit-capture-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _root;
    private readonly HttpContextAccessor _httpAccessor = new();

    private readonly Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] _interceptors;

    public CaptureHarness(bool realWriter = false, bool captureUnavailable = false, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        _interceptors = interceptors;
        Monitor.CurrentValue.Returns(Options);
        SetUser(UserKey, "Editor");

        var services = new ServiceCollection();
        services.AddSingleton(Monitor);
        services.AddSingleton(Security);
        services.AddSingleton<ILogger<EntryAuditCapture>>(CaptureLog);
        services.AddScoped<EntryAuditCollector>();
        services.AddSingleton(RecordStorage);
        services.AddSingleton(FormService);
        services.AddSingleton(Health);
        // The substitute by default; the real provider when a test sets UseRealFieldInfo, so it reads the same
        // per-request form cache as the capture (perf-edit: one form read per edit).
        services.AddScoped<IFormFieldInfoProvider>(sp => UseRealFieldInfo
            ? new FormFieldInfoProvider(sp.GetRequiredService<RequestFormCache>(), NullLogger<FormFieldInfoProvider>.Instance)
            : FieldInfo);
        services.AddScoped<RequestFormCache>();
        // The in-request erasure scrub (Task 14). Without the real writer no audit database is registered, so a scrub
        // fails exactly as it would on a broken database, which the fail-open tests rely on.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILogger<DeletedRecordScrubber>>(ScrubLog);
        services.AddSingleton<DeletedRecordScrubber>();
        FormService.Get(Arg.Any<Guid>()).Returns(call => new Form { Id = call.Arg<Guid>() });
        RecordStorage.GetRecordByUniqueId(Arg.Any<Guid>(), Arg.Any<Form>()).Returns(_ => StoredRecord);
        FieldInfo.GetLoadedFields(Arg.Any<Guid>()).Returns(new Dictionary<Guid, EntryAudit.FieldInfo>());
        if (captureUnavailable)
        {
            // Stands in for a broken registration or scope: resolving the capture throws.
            services.AddScoped<EntryAuditCapture>(_ => throw new InvalidOperationException("capture unavailable"));
        }
        else
        {
            services.AddScoped<EntryAuditCapture>();
        }

        if (realWriter)
        {
            services.AddScoped<FormsAuditDbContext>(_ => OpenDb());
            services.AddSingleton<EntryAuditDeduplicator>();
            services.AddSingleton<ILogger<EntryAuditWriter>>(NullLogger<EntryAuditWriter>.Instance);
            services.AddSingleton<IEntryAuditWriter, EntryAuditWriter>();
            using SqliteFormsAuditDbContext setup = OpenDb();
            setup.Database.EnsureCreated();
        }
        else
        {
            WriterSub.WriteAsync(Arg.Any<AccessWrite>()).Returns(call =>
            {
                Writes.Enqueue(call.Arg<AccessWrite>());
                return WriterResult;
            });
            services.AddSingleton(WriterSub);
        }

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Filter = new EntryAuditActionFilter(Monitor, Health, Security, FilterLog);
        var accessor = new EntryAuditCollectorAccessor(_httpAccessor);
        SearchHandler = new EntrySearchResultFetchingHandler(accessor, NullLogger<EntrySearchResultFetchingHandler>.Instance);
        RecordHandler = new RecordFetchingHandler(accessor, NullLogger<RecordFetchingHandler>.Instance);
        SavingHandler = new RecordSavingHandler(accessor, _httpAccessor, Monitor, NullLogger<RecordSavingHandler>.Instance);
        DeletingHandler = new RecordDeletingHandler(accessor, NullLogger<RecordDeletingHandler>.Instance);
    }

    public FormsAuditTrailOptions Options { get; } = new();
    public IOptionsMonitor<FormsAuditTrailOptions> Monitor { get; } = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
    public IBackOfficeSecurityAccessor Security { get; } = Substitute.For<IBackOfficeSecurityAccessor>();
    public IEntryAuditWriter WriterSub { get; } = Substitute.For<IEntryAuditWriter>();
    public ConcurrentQueue<AccessWrite> Writes { get; } = new();
    public AccessWriteResult WriterResult { get; set; } = new(AccessWriteStatus.Written, 1, true);
    public ListLogger<EntryAuditCapture> CaptureLog { get; } = new();
    public ListLogger<EntryAuditActionFilter> FilterLog { get; } = new();
    public EntryAuditActionFilter Filter { get; }
    public EntrySearchResultFetchingHandler SearchHandler { get; }
    public RecordFetchingHandler RecordHandler { get; }
    public RecordSavingHandler SavingHandler { get; }
    public RecordDeletingHandler DeletingHandler { get; }
    public ListLogger<DeletedRecordScrubber> ScrubLog { get; } = new();

    /// <summary>Forms' storage, read by the capture before the action for an edit (SEC-04, S2).</summary>
    public IRecordStorage RecordStorage { get; } = Substitute.For<IRecordStorage>();
    public IFormService FormService { get; } = Substitute.For<IFormService>();
    public IFormFieldInfoProvider FieldInfo { get; } = Substitute.For<IFormFieldInfoProvider>();

    /// <summary>Resolve the real <see cref="FormFieldInfoProvider"/> over <see cref="FormService"/> instead of <see cref="FieldInfo"/>.</summary>
    public bool UseRealFieldInfo { get; set; }
    public EntryAuditHealth Health { get; } = new(Substitute.For<IAuditStateStore>(), TimeProvider.System);

    /// <summary>What the storage read returns: the entry as stored before the edit. Empty unless a test sets it.</summary>
    public Record StoredRecord { get; set; } = new() { Form = FormId };

    /// <summary>The scope of the last request run, kept so a test can inspect the collector after the filter returns.</summary>
    public IServiceScope? LastScope { get; private set; }

    /// <summary>The executing context of the last request, where a filter that short-circuits leaves its result.</summary>
    public ActionExecutingContext? LastExecuting { get; private set; }

    /// <summary>How many times the action itself ran, across all requests.</summary>
    public int NextCalls { get; private set; }

    public void SetUser(Guid key, string? name)
    {
        IUser user = Substitute.For<IUser>();
        user.Key.Returns(key);
        user.Name.Returns(name);
        IBackOfficeSecurity security = Substitute.For<IBackOfficeSecurity>();
        security.CurrentUser.Returns(user);
        Security.BackOfficeSecurity.Returns(security);
    }

    public void ClearUser() => Security.BackOfficeSecurity.Returns((IBackOfficeSecurity?)null);

    public SqliteFormsAuditDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<SqliteFormsAuditDbContext>().UseSqlite($"Data Source={_dbPath}").AddInterceptors(_interceptors).Options);

    /// <summary>Plays one request through the filter and returns the executed context MVC would hand on.</summary>
    public async Task<ActionExecutedContext> RunAsync(
        Type controller,
        IDictionary<string, object?>? route = null,
        IDictionary<string, string>? query = null,
        IDictionary<string, object?>? arguments = null,
        Func<IActionResult?>? result = null,
        Action? duringAction = null,
        Exception? exception = null,
        Action<HttpContext>? configure = null)
    {
        LastScope?.Dispose();
        LastScope = _root.CreateScope();
        var http = new DefaultHttpContext { RequestServices = LastScope.ServiceProvider };
        if (query is not null)
        {
            http.Request.QueryString = QueryString.Create(query.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
        }

        configure?.Invoke(http);
        _httpAccessor.HttpContext = http;

        var routeData = new RouteData();
        foreach ((string key, object? value) in route ?? new Dictionary<string, object?>())
        {
            routeData.Values[key] = value;
        }

        var descriptor = new ControllerActionDescriptor { ControllerTypeInfo = controller.GetTypeInfo(), MethodInfo = typeof(object).GetMethod(nameof(ToString))! };
        var actionContext = new ActionContext(http, routeData, descriptor);
        var executing = new ActionExecutingContext(actionContext, [], arguments ?? new Dictionary<string, object?>(), controller: new object());

        LastExecuting = executing;
        ActionExecutedContext? executed = null;
        await Filter.OnActionExecutionAsync(executing, () =>
        {
            NextCalls++;
            duringAction?.Invoke();
            executed = new ActionExecutedContext(actionContext, [], new object())
            {
                Result = exception is null ? (result?.Invoke() ?? new OkObjectResult(new object())) : null,
                Exception = exception,
            };
            return Task.FromResult(executed);
        });

        return executed ?? new ActionExecutedContext(actionContext, [], new object());
    }

    /// <summary>
    /// SEC-02: a failure counted in health must surface as an <c>AuditGap</c> row once a write succeeds. Runs one
    /// successful write through a real writer that shares this harness's health and returns the gap rows' detail JSON.
    /// </summary>
    public async Task<IReadOnlyList<string?>> GapRowsAfterNextSuccessfulWriteAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<FormsAuditDbContext>(_ => OpenDb());
        services.AddSingleton(FormService);
        using ServiceProvider provider = services.BuildServiceProvider();
        using (SqliteFormsAuditDbContext setup = OpenDb())
        {
            setup.Database.EnsureCreated();
        }

        var writer = new EntryAuditWriter(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EntryAuditDeduplicator(Monitor, TimeProvider.System),
            Health,
            Monitor,
            TimeProvider.System,
            NullLogger<EntryAuditWriter>.Instance);
        AccessWriteResult result = await writer.WriteAsync(new AccessWrite(
            Models.AccessEventType.WorkflowRetried, FormId, "Form", null, 0, UserKey, "Editor", null, [], []));
        if (result.Status != AccessWriteStatus.Written)
        {
            throw new InvalidOperationException("The follow-up write did not succeed.");
        }

        using SqliteFormsAuditDbContext db = OpenDb();
        return await db.AccessEntries.Where(a => a.EventType == (int)Models.AccessEventType.AuditGap).Select(a => a.Detail).ToListAsync();
    }

    /// <summary>What Forms' searcher does once per row it returns.</summary>
    public void FetchRows(params Guid[] ids)
    {
        foreach (Guid id in ids)
        {
            SearchHandler.Handle(new EntrySearchResultFetchingNotification(new EntrySearchResult { UniqueId = id, Form = FormId.ToString() }, new Umbraco.Cms.Core.Events.EventMessages()));
        }
    }

    public void FetchRecord(Guid recordId, Guid formId) =>
        RecordHandler.Handle(new RecordFetchingNotification(new Record { UniqueId = recordId, Form = formId }, new Umbraco.Cms.Core.Events.EventMessages()));

    /// <summary>What Forms does when it saves a record: an edit and a backoffice create both publish this (spec 7.3).</summary>
    public void SaveRecord(Record record) =>
        SavingHandler.Handle(new RecordSavingNotification(record, new Umbraco.Cms.Core.Events.EventMessages()));

    /// <summary>What Forms does inside its delete scope, once per batch of records it is about to delete (S5).</summary>
    public void DeleteRecords(Guid formId, params Guid[] ids) =>
        DeletingHandler.Handle(new RecordDeletingNotification(
            ids.Select(id => new Record { UniqueId = id, Form = formId }).ToArray(),
            new Umbraco.Cms.Core.Events.EventMessages()));

    public void Dispose()
    {
        LastScope?.Dispose();
        _root.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { /* best effort */ }
        }
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception)));
}
