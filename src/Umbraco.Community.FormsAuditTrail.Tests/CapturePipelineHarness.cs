using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Community.FormsAuditTrail.Composers;
using Umbraco.Community.FormsAuditTrail.Controllers;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Searchers;
using Umbraco.Forms.Core.Security;
using Umbraco.Forms.Core.Services;
using Umbraco.Forms.Core.Services.Notifications;
using Umbraco.Forms.Web.Api.ManagementApi.Export;
using Umbraco.Forms.Web.Api.ManagementApi.Member;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Xunit;
using Umbraco.Forms.Web.Models.Backoffice;
using Record = Umbraco.Forms.Core.Persistence.Dtos.Record;
using RecordField = Umbraco.Forms.Core.Persistence.Dtos.RecordField;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Task 14a: the real composer's registrations (as <see cref="CompositionTests"/> builds them), the real capture
/// pipeline (action filter, capture, collector, notification handlers, differ, singleton writer, upload middleware)
/// and a real SQLite file. Only the Forms and Umbraco boundary is substituted: <see cref="IRecordStorage"/>,
/// <see cref="IFormService"/>, <see cref="IFormsSecurity"/>, <see cref="IBackOfficeSecurityAccessor"/>, and the user
/// service behind the upload cookie. The point is to read every stored value back and prove none of it is personal data
/// the configured mode forbids.
/// </summary>
internal sealed class CapturePipelineHarness : IDisposable
{
    public const string NameOld = "SENTINEL-NAME-OLD";
    public const string NameNew = "SENTINEL-NAME-NEW";
    public const string SensitiveOld = "SENTINEL-SENSITIVE-OLD";
    public const string SensitiveNew = "SENTINEL-SENSITIVE-NEW";
    public const string ClearedOld = "SENTINEL-CLEARED-OLD";
    public const string SearchTerm = "sentinel-search@example.com";
    public const string MemberIdentifier = "sentinel-member@example.com";
    public const string UploadFileName = "SENTINEL_Jane_Smith_CV.pdf";
    public const string CreatedValue = "SENTINEL-CREATED";
    public const string SensitiveClearedOld = "SENTINEL-SENSCLEARED-OLD";
    public const string MultiAOld = "SENTINEL-MULTI-A-OLD";
    public const string MultiBOld = "SENTINEL-MULTI-B-OLD";
    public const string MultiANew = "SENTINEL-MULTI-A-NEW";
    public const string MultiBNew = "SENTINEL-MULTI-B-NEW";
    // Non-ASCII (e acute, and a non-BMP character that JSON escapes as a surrogate pair) so escaped forms are scanned too.
    public const string UnicodeOld = "SENTINEL-UNI-OLD-\u00e9-\U0001F600";
    public const string UnicodeNew = "SENTINEL-UNI-NEW-\u00e9-\U0001F600";
    // Quotes and a backslash, which JSON escapes and which a naive Contains on the raw text would miss.
    public const string QuoteOld = "SENTINEL-QUOTE-OLD \"q\" back\\slash";
    public const string QuoteNew = "SENTINEL-QUOTE-NEW \"q\" back\\slash";
    public const string ExportSearchTerm = "sentinel-export-search@example.com";
    public const string ExportFileName = "SENTINEL-EXPORT-FILE.csv";
    public const string UploadFileNameSpaced = "SENTINEL Jane Smith CV.pdf";
    public const string GroupAlias = "SENTINEL-GROUP-ALIAS";

    public static readonly Guid FormId = Guid.NewGuid();
    public static readonly Guid RecordId = Guid.NewGuid();
    public static readonly Guid UserKey = Guid.NewGuid();

    private static readonly Guid NameField = Guid.NewGuid();
    private static readonly Guid SensitiveField = Guid.NewGuid();
    private static readonly Guid ClearedField = Guid.NewGuid();
    private static readonly Guid CreatedField = Guid.NewGuid();
    private static readonly Guid SensitiveClearedField = Guid.NewGuid();
    private static readonly Guid MultiField = Guid.NewGuid();
    private static readonly Guid UnicodeField = Guid.NewGuid();
    private static readonly Guid QuoteField = Guid.NewGuid();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"formsaudit-sentinel-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;
    private readonly IHttpContextAccessor _http;
    private readonly bool _viewerCanSeeSensitive;
    private readonly IRuntimeState _runtime = Substitute.For<IRuntimeState>();

    public CapturePipelineHarness(StoreEditValuesMode mode, bool keepClearedValues = false, bool captureSearchTerms = false, string? accessLogGroupAlias = null, bool viewerCanSeeSensitive = false, IFormsRecordReader? recordReader = null)
    {
        _viewerCanSeeSensitive = viewerCanSeeSensitive;
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:umbracoDbDSN"] = $"Data Source={_dbPath}",
            ["ConnectionStrings:umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
            ["FormsAuditTrail:EntryAudit:StoreEditValues"] = mode.ToString(),
            ["FormsAuditTrail:EntryAudit:KeepClearedValues"] = keepClearedValues.ToString(),
            ["FormsAuditTrail:EntryAudit:CaptureSearchTerms"] = captureSearchTerms.ToString(),
        }).Build();
        if (accessLogGroupAlias is not null)
        {
            config["FormsAuditTrail:EntryAudit:AccessLogUserGroups:0"] = accessLogGroupAlias;
        }

        var services = new ServiceCollection();
        IUmbracoBuilder builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);
        builder.Config.Returns(config);
        services.AddSingleton(config);
        services.AddLogging();
        RegisterBoundary(services);
        new FormsAuditComposer().Compose(builder);
        if (recordReader is not null)
        {
            // The real reader queries UFRecords through Umbraco's scope, which this harness substitutes; a test that needs
            // Forms' entries table states what it holds instead. Registered last, so it is the one resolved.
            services.AddScoped(_ => recordReader);
        }

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _http = _provider.GetRequiredService<IHttpContextAccessor>();

        using IServiceScope scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>().Database.EnsureCreated();
    }

    private void RegisterBoundary(IServiceCollection services)
    {
        IBackOfficeSecurityAccessor security = Substitute.For<IBackOfficeSecurityAccessor>();
        IUser user = Substitute.For<IUser>();
        user.Key.Returns(UserKey);
        user.Name.Returns("Editor");
        user.Username.Returns("editor@example.test");
        // Set explicitly rather than left to a substitute's empty default: the access-log detail masks sensitive values
        // unless the viewer is in the sensitive data group (matched by key), and each test must say which viewer it is exercising.
        IReadOnlyUserGroup[] groups = _viewerCanSeeSensitive
            ? [Group(Umbraco.Cms.Core.Constants.Security.SensitiveDataGroupKey)]
            : [];
        user.Groups.Returns(groups);
        IBackOfficeSecurity backOffice = Substitute.For<IBackOfficeSecurity>();
        backOffice.CurrentUser.Returns(user);
        security.BackOfficeSecurity.Returns(backOffice);
        services.AddSingleton(security);

        IUserService users = Substitute.For<IUserService>();
        users.GetByUsername(Arg.Any<string>()).Returns(user);
        services.AddSingleton(users);

        // The real UploadBackOfficeUserResolver, fed a cookie whose ticket decrypts to the editor.
        ISecureDataFormat<AuthenticationTicket> ticketFormat = Substitute.For<ISecureDataFormat<AuthenticationTicket>>();
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "editor@example.test")], "test")),
            new AuthenticationProperties(),
            "test");
        ticketFormat.Unprotect(Arg.Any<string>()).Returns(ticket);
        services.Configure<CookieAuthenticationOptions>(Umbraco.Cms.Core.Constants.Security.BackOfficeAuthenticationType, o =>
        {
            o.Cookie.Name = "test-backoffice";
            o.TicketDataFormat = ticketFormat;
        });

        IFormService formService = Substitute.For<IFormService>();
        formService.Get(Arg.Any<Guid>()).Returns(call => BuildForm(call.Arg<Guid>()));
        services.AddSingleton(formService);

        IRecordStorage storage = Substitute.For<IRecordStorage>();
        // The entry as stored before the edit: Forms' read path hands back clear text (SEC-04).
        storage.GetRecordByUniqueId(Arg.Any<Guid>(), Arg.Any<Form>()).Returns(_ => Stored(after: false));
        services.AddSingleton(storage);

        // Explicit, not left to a substitute's default (false): the access-log API filters every row through this, so a
        // default would hide all rows and the API scans would pass over an empty result.
        IFormsSecurity formsSecurity = Substitute.For<IFormsSecurity>();
        formsSecurity.HasAccessToForm(Arg.Any<Guid>()).Returns(true);
        services.AddSingleton(formsSecurity);
        formService.GetSlim().Returns([new FormSlim { Id = FormId, Name = "Sentinel form" }]);

        // Consumed by the package at the lifetimes spike S1 recorded; the same set CompositionTests registers.
        services.AddSingleton(Substitute.For<IUserGroupService>());
        services.AddSingleton(Substitute.For<Umbraco.Cms.Core.Scoping.ICoreScopeProvider>());
        services.AddSingleton(Substitute.For<Umbraco.Cms.Infrastructure.Scoping.IScopeProvider>());
        services.AddSingleton(Substitute.For<IFormRecordSearcher>());
        _runtime.Level.Returns(RuntimeLevel.Run);
        services.AddSingleton(_runtime);
        services.AddSingleton(Substitute.For<IFieldTypeStorage>());
        services.AddSingleton(Substitute.For<IWorkflowService>());
        services.AddSingleton(new Umbraco.Forms.Core.Providers.WorkflowCollection(() => []));
        services.AddTransient(_ => Substitute.For<Microsoft.AspNetCore.Authorization.IAuthorizationService>());
        services.AddSingleton<EndpointDataSource>(new CompositeEndpointDataSource([]));
    }

    private static IReadOnlyUserGroup Group(Guid key)
    {
        IReadOnlyUserGroup group = Substitute.For<IReadOnlyUserGroup>();
        group.Key.Returns(key);
        return group;
    }

    private static Form BuildForm(Guid id)
    {
        var fields = new[]
        {
            new Field { Id = NameField, Caption = "Name", ContainsSensitiveData = false },
            new Field { Id = SensitiveField, Caption = "National insurance number", ContainsSensitiveData = true },
            new Field { Id = ClearedField, Caption = "Notes", ContainsSensitiveData = false },
            new Field { Id = CreatedField, Caption = "Message", ContainsSensitiveData = false },
            new Field { Id = SensitiveClearedField, Caption = "Passport number", ContainsSensitiveData = true },
            new Field { Id = MultiField, Caption = "Interests", ContainsSensitiveData = false },
            new Field { Id = UnicodeField, Caption = "Nickname", ContainsSensitiveData = false },
            new Field { Id = QuoteField, Caption = "Quote", ContainsSensitiveData = false },
        };
        return new Form
        {
            Id = id,
            Name = "Sentinel form",
            Pages = [new Page { FieldSets = [new FieldSet { Containers = [new FieldsetContainer { Fields = [.. fields] }] }] }],
        };
    }

    /// <summary>The entry before an edit (<paramref name="after"/> false) or as saved by it. The after entry blanks both cleared fields.</summary>
    private static Record Stored(bool after, Guid? recordId = null)
    {
        var record = new Record { Id = 7, UniqueId = recordId ?? RecordId, Form = FormId };
        void Add(Guid field, params object[] values) => record.RecordFields[Guid.NewGuid()] = new RecordField { FieldId = field, Values = [.. values] };
        Add(NameField, after ? NameNew : NameOld);
        Add(SensitiveField, after ? SensitiveNew : SensitiveOld);
        // Forms stores a blanked field as an empty value, which is what a clear looks like to the differ.
        Add(ClearedField, after ? string.Empty : ClearedOld);
        Add(SensitiveClearedField, after ? string.Empty : SensitiveClearedOld);
        Add(MultiField, after ? [MultiANew, MultiBNew] : [MultiAOld, MultiBOld]);
        Add(UnicodeField, after ? UnicodeNew : UnicodeOld);
        Add(QuoteField, after ? QuoteNew : QuoteOld);
        return record;
    }

    /// <summary>An edit that changes every field kind: plain, sensitive, multi-value, non-ASCII, quoted, and two blanked (SEC-03).</summary>
    public Task RunEditAsync() =>
        RunAsync(
            typeof(UpdateRecordController),
            route: FormRoute(RecordId),
            result: () => new OkResult(),
            duringAction: provider => Publish(provider, new RecordSavingNotification(Stored(after: true), new EventMessages())));

    /// <summary>Opening one entry: Forms publishes the fetched record, whose values must never reach the audit row.</summary>
    public Task RunDetailAsync() =>
        RunAsync(
            typeof(GetAuditTrailController),
            route: FormRoute(RecordId),
            duringAction: provider => Publish(provider, new RecordFetchingNotification(Stored(after: true), new EventMessages())));

    /// <summary>Forms' export request: the search term is in the query and the generated file name in the response.</summary>
    public Task RunExportAsync(string searchTerm, string fileName) =>
        RunAsync(
            typeof(PostGenerateExportController),
            route: new Dictionary<string, object?>(),
            query: new Dictionary<string, string> { ["formId"] = FormId.ToString(), ["exportType"] = "csv", ["filter"] = searchTerm },
            result: () => new OkObjectResult(new PostGenerateExportController.GenerateExportResponseModel { FormId = FormId.ToString(), FileName = fileName }),
            duringAction: provider =>
            {
                foreach (Guid id in new[] { Guid.NewGuid(), Guid.NewGuid() })
                {
                    Publish(provider, new EntrySearchResultFetchingNotification(
                        new EntrySearchResult { UniqueId = id, Form = FormId.ToString() }, new EventMessages()));
                }
            });

    public Task RunExportDownloadAsync(string fileName) =>
        RunAsync(
            typeof(ExportController),
            route: new Dictionary<string, object?>(),
            query: new Dictionary<string, string> { ["formId"] = FormId.ToString(), ["fileName"] = fileName },
            result: () => new PhysicalFileResult(Path.Combine(Path.GetTempPath(), "not-served.csv"), "text/csv"));

    /// <summary>A single delete: Forms publishes RecordDeleting for the id from inside its delete scope (S5).</summary>
    public Task RunDeleteAsync(Guid? recordId = null) =>
        RunAsync(
            typeof(DeleteRecordController),
            route: FormRoute(recordId ?? RecordId),
            result: () => new OkResult(),
            duringAction: provider => Publish(provider, new RecordDeletingNotification(
                [new Record { Id = 7, UniqueId = recordId ?? RecordId, Form = FormId }], new EventMessages())));

    /// <summary>The record-set Delete action, which is how the backoffice deletes one key or many (S5).</summary>
    public Task RunBulkDeleteAsync(params Guid[] recordIds) =>
        RunAsync(
            typeof(ExecuteActionController),
            route: new Dictionary<string, object?>
            {
                ["formId"] = FormId,
                ["actionId"] = Guid.Parse(Umbraco.Forms.Core.Constants.RecordSetActionTypes.DeleteRecordSet),
            },
            arguments: new Dictionary<string, object?> { ["model"] = new RecordActionExecution { RecordKeys = recordIds } },
            result: () => new OkResult(),
            duringAction: provider => Publish(provider, new RecordDeletingNotification(
                recordIds.Select(id => new Record { Id = 7, UniqueId = id, Form = FormId }).ToArray(), new EventMessages())));

    /// <summary>
    /// A permanent form delete as Forms 18 publishes it: <c>FormDeletedNotification</c> after its delete scope, with no
    /// per-record notification (S5). Outside a request, so the row is recorded as System.
    /// </summary>
    public async Task RunPermanentFormDeleteAsync()
    {
        _http.HttpContext = null;
        // Umbraco's publisher resolves handlers from a scope; the handler takes scoped services.
        using IServiceScope scope = _provider.CreateScope();
        FormDeletedHandler handler = ActivatorUtilities.CreateInstance<FormDeletedHandler>(scope.ServiceProvider);
        await handler.HandleAsync(new FormDeletedNotification(BuildForm(FormId), new EventMessages()), CancellationToken.None);
    }

    /// <summary>
    /// What a site start does: the recorder diffs the options against the stored snapshot, writes a ConfigChanged row and
    /// saves the snapshot to the state table. The harness's runtime state reports Run, as the real one does by then.
    /// </summary>
    public Task RunConfigChangeAsync() =>
        _provider.GetRequiredService<ConfigChangeRecorder>().HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);

    public Task RunListAsync(string searchTerm) =>
        RunAsync(
            typeof(GetCollectionController),
            route: FormRoute(),
            query: new Dictionary<string, string> { ["filter"] = searchTerm },
            duringAction: provider =>
            {
                foreach (Guid id in new[] { Guid.NewGuid(), Guid.NewGuid() })
                {
                    Publish(provider, new EntrySearchResultFetchingNotification(
                        new EntrySearchResult { UniqueId = id, Form = FormId.ToString() }, new EventMessages()));
                }
            });

    public Task RunMemberSummaryAsync(string memberIdentifier) =>
        RunAsync(typeof(GetMemberFormSummariesController), route: new Dictionary<string, object?> { ["memberKey"] = memberIdentifier });

    public Task RunCreateAsync(string createdValue = CreatedValue)
    {
        Guid newId = Guid.NewGuid();
        var created = new Record { Id = 0, UniqueId = newId, Form = FormId };
        created.RecordFields[Guid.NewGuid()] = new RecordField { FieldId = CreatedField, Values = [createdValue] };
        return RunAsync(
            typeof(CreateRecordController),
            route: FormRoute(),
            result: () => new ObjectResult(new { id = newId }) { StatusCode = 201 },
            duringAction: provider => Publish(provider, new RecordSavingNotification(created, new EventMessages())));
    }

    /// <summary>Opens an uploaded file through the real middleware, as a signed-in backoffice user.</summary>
    public async Task RunUploadOpenAsync(string path)
    {
        using IServiceScope scope = _provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Path = path;
        http.Request.Headers.Cookie = "test-backoffice=ticket";
        _http.HttpContext = http;

        UploadAuditMiddleware middleware = ActivatorUtilities.CreateInstance<UploadAuditMiddleware>(
            scope.ServiceProvider,
            (RequestDelegate)(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            }));
        await middleware.InvokeAsync(
            http,
            scope.ServiceProvider.GetRequiredService<IUploadPathResolver>(),
            scope.ServiceProvider.GetRequiredService<IUploadUserResolver>(),
            scope.ServiceProvider.GetRequiredService<IEntryAuditWriter>(),
            scope.ServiceProvider.GetRequiredService<IOptionsMonitor<FormsAuditTrailOptions>>(),
            scope.ServiceProvider.GetRequiredService<EntryAuditHealth>(),
            scope.ServiceProvider.GetRequiredService<ILogger<UploadAuditMiddleware>>());
    }

    /// <summary>The distinct event types in the access table, so a test can show the scan looked at exactly the events driven.</summary>
    public IReadOnlySet<AccessEventType> EventTypes()
    {
        var types = new HashSet<AccessEventType>();
        using var connection = OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT EventType FROM formsAuditTrailAccess";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            types.Add((AccessEventType)reader.GetInt32(0));
        }

        return types;
    }

    /// <summary>The stored Detail JSON of every row of one event type.</summary>
    public IReadOnlyList<string> DetailsOf(AccessEventType type)
    {
        var details = new List<string>();
        using var connection = OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT Detail FROM formsAuditTrailAccess WHERE EventType = {(int)type} AND Detail IS NOT NULL";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            details.Add(reader.GetString(0));
        }

        return details;
    }

    /// <summary>The stored field changes: caption, old and new value, and whether they were scrubbed.</summary>
    public IReadOnlyList<(string Caption, string? Old, string? New, bool Scrubbed)> EntryChangeRows()
    {
        var rows = new List<(string, string?, string?, bool)>();
        using var connection = OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT FieldCaption, OldValue, NewValue, ValuesScrubbedAt IS NOT NULL FROM formsAuditTrailEntryChanges";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3)));
        }

        return rows;
    }

    public long IdOfFirst(AccessEventType type)
    {
        using var connection = OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT Id FROM formsAuditTrailAccess WHERE EventType = {(int)type} ORDER BY Id LIMIT 1";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The access-log list as the dashboard receives it, serialised.</summary>
    public async Task<string> ReadAccessLogListAsync()
    {
        using IServiceScope scope = _provider.CreateScope();
        FormsAuditAccessApiController controller = NewAccessController(scope, new MemoryStream());
        var result = await controller.GetAccess(pageSize: 100, cancellationToken: CancellationToken.None);
        return System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    /// <summary>One access-log entry with its field changes as the viewer sees them (read-time masking applies).</summary>
    public async Task<string> ReadAccessLogEntryAsync(long id)
    {
        using IServiceScope scope = _provider.CreateScope();
        FormsAuditAccessApiController controller = NewAccessController(scope, new MemoryStream());
        var result = await controller.GetAccessEntry(id, CancellationToken.None);
        return System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    /// <summary>The access-log CSV body.</summary>
    public async Task<string> ReadAccessLogCsvAsync()
    {
        using IServiceScope scope = _provider.CreateScope();
        var body = new MemoryStream();
        FormsAuditAccessApiController controller = NewAccessController(scope, body);
        IActionResult result = await controller.ExportAccess(cancellationToken: CancellationToken.None);
        Assert.True(result is EmptyResult or null, $"The CSV export answered {result?.GetType().Name} instead of streaming.");
        return System.Text.Encoding.UTF8.GetString(body.ToArray());
    }

    private FormsAuditAccessApiController NewAccessController(IServiceScope scope, Stream body)
    {
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Response.Body = body;
        _http.HttpContext = http;
        // The real MVC controller, built from the composer's registrations (it is activated by MVC, not registered).
        return ActivatorUtilities.CreateInstance<FormsAuditAccessApiController>(scope.ServiceProvider) is { } controller
            ? WithContext(controller, http)
            : throw new InvalidOperationException();
    }

    private static FormsAuditAccessApiController WithContext(FormsAuditAccessApiController controller, HttpContext http)
    {
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private SqliteConnection OpenReadOnly()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    /// <summary>Every value in the four new tables, as text. Columns come from PRAGMA table_info, so a new one is scanned untouched.</summary>
    public IReadOnlyList<string> ScanTextColumns() => ScanWithOrigin().Select(v => v.Value).ToList();

    public IReadOnlyList<(string Table, string Column, string Value)> ScanWithOrigin()
    {
        // formsAuditTrailEntries and formsAuditTrailChanges are deliberately not listed: they hold design diffs of form
        // definitions only, never entry values, so they are out of scope for this scan.
        string[] tables =
        [
            FormsAuditDbContext.AccessTableName,
            FormsAuditDbContext.AccessRecordsTableName,
            FormsAuditDbContext.EntryChangesTableName,
            FormsAuditDbContext.StateTableName,
        ];

        var found = new List<(string, string, string)>();
        using var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        connection.Open();
        foreach (string table in tables)
        {
            var columns = new List<string>();
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info(\"{table}\")";
                using SqliteDataReader reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    columns.Add(reader.GetString(reader.GetOrdinal("name")));
                }
            }

            if (columns.Count == 0)
            {
                throw new InvalidOperationException($"Table {table} has no columns; the scan would prove nothing.");
            }

            foreach (string column in columns)
            {
                using SqliteCommand select = connection.CreateCommand();
                select.CommandText = $"SELECT CAST(\"{column}\" AS TEXT) FROM \"{table}\" WHERE \"{column}\" IS NOT NULL";
                using SqliteDataReader reader = select.ExecuteReader();
                while (reader.Read())
                {
                    found.Add((table, column, reader.GetString(0)));
                }
            }
        }

        return found;
    }

    private static Dictionary<string, object?> FormRoute(Guid? record = null)
    {
        var route = new Dictionary<string, object?> { ["formId"] = FormId };
        if (record is { } id)
        {
            route["recordId"] = id;
        }

        return route;
    }

    private static void Publish<T>(IServiceProvider provider, T notification) where T : class, Umbraco.Cms.Core.Notifications.INotification
    {
        foreach (INotificationHandler<T> handler in provider.GetServices<INotificationHandler<T>>())
        {
            handler.Handle(notification);
        }
    }

    /// <summary>Plays one request through the real filter the way MVC does, with the real handlers publishing from inside the action.</summary>
    private async Task RunAsync(
        Type controller,
        IDictionary<string, object?>? route = null,
        IDictionary<string, string>? query = null,
        Func<IActionResult?>? result = null,
        Action<IServiceProvider>? duringAction = null,
        IDictionary<string, object?>? arguments = null)
    {
        using IServiceScope scope = _provider.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (query is not null)
        {
            http.Request.QueryString = QueryString.Create(query.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
        }

        _http.HttpContext = http;
        var routeData = new RouteData();
        foreach ((string key, object? value) in route ?? new Dictionary<string, object?>())
        {
            routeData.Values[key] = value;
        }

        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = System.Reflection.IntrospectionExtensions.GetTypeInfo(controller),
            MethodInfo = typeof(object).GetMethod(nameof(ToString))!,
        };
        var actionContext = new ActionContext(http, routeData, descriptor);
        var executing = new ActionExecutingContext(actionContext, [], arguments ?? new Dictionary<string, object?>(), controller: new object());

        // The filter is built from the container as MVC's TypeFilter does, so its real dependencies are the composer's.
        EntryAuditActionFilter filter = ActivatorUtilities.CreateInstance<EntryAuditActionFilter>(scope.ServiceProvider);
        await filter.OnActionExecutionAsync(executing, () =>
        {
            duringAction?.Invoke(scope.ServiceProvider);
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object())
            {
                Result = result?.Invoke() ?? new OkObjectResult(new object()),
            });
        });
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (string file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { /* best effort */ }
        }
    }
}
