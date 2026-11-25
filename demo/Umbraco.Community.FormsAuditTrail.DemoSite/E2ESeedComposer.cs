using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Enums;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Persistence.Dtos;
using Umbraco.Forms.Core.Providers;
using Umbraco.Forms.Core.Services;
using Umbraco.Forms.Data.Storage;
using FormsRecord = Umbraco.Forms.Core.Persistence.Dtos.Record;

namespace Umbraco.Community.FormsAuditTrail.DemoSite;

/// <summary>
/// Seeds the data the Playwright suite needs and turns the test-only diagnostics controller on. It exists only to
/// exercise the package against a real Forms site and is never packed (the demo is not packable, R1).
/// </summary>
/// <remarks>
/// Gating: the seed handler is registered only in Development with <c>E2E:Seed=true</c>; the diagnostics controller is
/// kept in the controller list only in Development with <c>E2E:Diagnostics=true</c>. See <see cref="E2EGate"/>.
/// </remarks>
public sealed class E2EComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        bool diagnostics = E2EGate.IsEnabled(builder, E2EGate.DiagnosticsKey);
        builder.Services.AddMvcCore().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new E2EControllerGate(diagnostics)));

        if (E2EGate.IsEnabled(builder, E2EGate.SeedKey))
        {
            builder.Services.AddSingleton<E2ESeeder>();
            builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, E2ESeedHandler>();
        }
    }
}

internal sealed class E2ESeedHandler(E2ESeeder seeder, IRuntimeState runtime, IWebHostEnvironment env, IConfiguration config, ILogger<E2ESeedHandler> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtime.Level != RuntimeLevel.Run || !E2EGate.IsEnabled(env, config, E2EGate.SeedKey))
        {
            return;
        }

        try
        {
            await seeder.SeedAsync();
        }
        catch (Exception ex)
        {
            // A half-seeded site would make the specs fail for the wrong reason, so say so loudly.
            logger.LogError(ex, "E2E seed failed; the specs will not find their data.");
        }
    }
}

/// <summary>Idempotent by name: a second start finds the forms and users and adds nothing.</summary>
internal sealed class E2ESeeder(
    IFormService forms,
    IWorkflowService workflows,
    IRecordStorage records,
    IScopeProvider scopeProvider,
    WorkflowCollection workflowTypes,
    IUserService users,
    IUserGroupService userGroups,
    IUserSecurityStorage userSecurity,
    IUserFormSecurityStorage userFormSecurity,
    IShortStringHelper shortStringHelper,
    IWebHostEnvironment env,
    IConfiguration config,
    ILogger<E2ESeeder> logger)
{
    internal const string ContactFormName = "E2E Contact";
    internal const string EncryptedFormName = "E2E Encrypted";
    internal const string GroupAlias = "e2eForms";
    // Forms still wants the legacy integer user id; -1 is the built-in super user (Umbraco marks its own constant obsolete).
    private const int SuperUserId = -1;
    internal const string EditorEmail = "e2e.editor@example.com";
    internal const string AuditorEmail = "e2e.auditor@example.com";
    internal const string AuditorGroupAlias = "e2eAuditors";

    // Forms' built-in field types: short answer, checkbox list, file upload (same ids as the M0 spikes).
    private static readonly Guid ShortText = new("3f92e01b-29e2-4a30-bf33-9df5580ed52c");
    private static readonly Guid CheckboxList = new("fab43f20-a6bf-11de-a28f-9b5755d89593");
    private static readonly Guid FileUpload = new("84a17cf8-b711-46a6-9840-0e4a072ad000");

    public async Task SeedAsync()
    {
        Form contact = EnsureContactForm();
        bool simulate = E2EGate.IsEnabled(env, config, E2EGate.SimulateEncryptionKey);
        if (simulate)
        {
            EnsureEncryptedForm();
        }

        Guid? encrypted = forms.GetSlim().FirstOrDefault(f => f.Name == EncryptedFormName)?.Id;
        // The editor has no access-log rights; the auditor has them (the site lists e2eAuditors in AccessLogUserGroups)
        // but, like the editor, only sees E2E Contact and is outside the Sensitive data group.
        await EnsureUserAsync(EditorEmail, "E2E Editor", "E2E:EditorPassword", GroupAlias, "E2E Forms", contact.Id, encrypted);
        await EnsureUserAsync(AuditorEmail, "E2E Auditor", "E2E:AuditorPassword", AuditorGroupAlias, "E2E Auditors", contact.Id, encrypted);
        logger.LogInformation("E2E seed complete (encrypted form: {Encrypted}).", simulate);
    }

    private Form EnsureContactForm()
    {
        FormSlim? existing = forms.GetSlim().FirstOrDefault(f => f.Name == ContactFormName);
        if (existing is not null)
        {
            return forms.Get(existing.Id)!;
        }

        Field name = Text("Name", "name");
        Field email = Text("Email", "email", sensitive: true);
        Field interests = new()
        {
            Id = Guid.NewGuid(), Alias = "interests", Caption = "Interests", FieldTypeId = CheckboxList,
            PreValues = new[] { "Alpha", "Beta", "Gamma" }.Select(v => new FieldPrevalue { Value = v, Caption = v }).ToList(),
        };
        // Sensitive, so Forms refuses the upload to a user outside the Sensitive data group (the permissions spec relies on it).
        // A caption that would run script if any view rendered it as HTML (S3). Stored strings must stay text.
        Field notes = Text("<img src=x onerror=\"window.__x=1\">", "notes");
        Field cv = new() { Id = Guid.NewGuid(), Alias = "cv", Caption = "CV", FieldTypeId = FileUpload, ContainsSensitiveData = true };

        Form form = NewForm(ContactFormName, name, email, interests, notes, cv);
        form = forms.Insert(form, SuperUserId, false)!;
        InsertEmailWorkflow(form);

        Insert(form, ("Alice Example", "alice@example.test"));
        Insert(form, ("Bob Example", "bob@example.test"));
        FormsRecord withCv = Insert(form, ("Jane Smith", "jane.smith@example.test"), upload: true);
        logger.LogInformation("E2E seeded {Form} with three entries; the CV is on entry {Record}.", ContactFormName, withCv.UniqueId);
        return form;
    }

    private void EnsureEncryptedForm()
    {
        if (forms.GetSlim().Any(f => f.Name == EncryptedFormName))
        {
            return;
        }

        Form form = forms.Insert(NewForm(EncryptedFormName, Text("Name", "name"), Text("Email", "email", sensitive: true)), SuperUserId, false)!;
        InsertEncryptedRecord(form);
    }

    /// <summary>
    /// Puts the encrypted form back to one entry stored reversed, so the encryption spec can be run again without a
    /// fresh database. Written straight to Forms' storage: an edit through the API would copy ciphertext into the audit.
    /// </summary>
    internal bool ResetEncryptedRecord()
    {
        FormSlim? slim = forms.GetSlim().FirstOrDefault(f => f.Name == EncryptedFormName);
        if (slim is null)
        {
            return false;
        }

        Form form = forms.Get(slim.Id)!;
        using (IScope scope = scopeProvider.CreateScope(autoComplete: true))
        {
            foreach (FormsRecord existing in records.GetAllRecords(form, true))
            {
                records.DeleteRecord(existing, form);
            }
        }

        InsertEncryptedRecord(form);
        return true;
    }

    // Stored reversed, as an at-rest encryption package would store ciphertext; the stand-in reverses on read (P3).
    private void InsertEncryptedRecord(Form form) => Insert(form, (Reverse("Jane Smith"), Reverse("jane.smith@example.test")));

    private static string Reverse(string s) => new(s.Reverse().ToArray());

    private static Field Text(string caption, string alias, bool sensitive = false) => new()
    {
        Id = Guid.NewGuid(), Alias = alias, Caption = caption, FieldTypeId = ShortText, ContainsSensitiveData = sensitive,
    };

    private static Form NewForm(string name, params Field[] fields)
    {
        var form = new Form
        {
            Id = Guid.NewGuid(),
            Name = name,
            Pages =
            [
                new Page
                {
                    Id = Guid.NewGuid(), Caption = "Page 1",
                    FieldSets = [new FieldSet { Id = Guid.NewGuid(), Containers = [new FieldsetContainer { Id = Guid.NewGuid(), Width = 12, Fields = fields.ToList() }] }],
                },
            ],
        };
        form.EnsureFormStructureIds();
        return form;
    }

    private void InsertEmailWorkflow(Form form)
    {
        // Found by type name so the seed does not hard-code Forms' provider id.
        Guid sendEmail = workflowTypes.First(t => t.GetType().Name == "SendEmail").Id;
        workflows.Insert(form, new Workflow
        {
            Name = "E2E notification", Active = true, WorkflowTypeId = sendEmail, ExecutesOn = FormState.Submitted,
            Settings = new Dictionary<string, string>
            {
                ["Email"] = "e2e-recipient@example.test",
                ["SenderEmail"] = "e2e@example.test",
                ["Subject"] = "E2E submission",
                ["Message"] = "E2E submission received.",
            },
        });
    }

    private FormsRecord Insert(Form form, (string Name, string Email) values, bool upload = false)
    {
        var record = new FormsRecord
        {
            Form = form.Id, UniqueId = Guid.NewGuid(), State = FormState.Approved, Created = DateTime.UtcNow, Updated = DateTime.UtcNow,
            CurrentPage = form.Pages[0].Id,
        };

        foreach (Field field in form.AllFields)
        {
            var rf = new RecordField(field);
            rf.Values = field.Alias switch
            {
                "name" => new List<object> { values.Name },
                "email" => new List<object> { values.Email },
                "interests" => new List<object> { "Alpha", "Beta" },
                "cv" when upload => new List<object> { WriteUpload(form.Id) },
                _ => new List<object>(),
            };
            record.RecordFields[field.Id] = rf;
        }

        // Forms reads field values from the serialised RecordData column, so a record stored without it lists as empty.
        record.RecordData = record.GenerateRecordDataAsJson();
        using IScope scope = scopeProvider.CreateScope(autoComplete: true);
        return records.InsertRecord(record, form);
    }

    // Forms keeps an upload as a path under the media file system and serves it through its own protected route.
    private string WriteUpload(Guid formId)
    {
        string relative = $"/media/forms/upload/form_{formId}/{Guid.NewGuid()}/Jane_Smith_CV.pdf";
        string physical = Path.Combine(env.WebRootPath, relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(physical)!);
        System.IO.File.WriteAllBytes(physical, "%PDF-1.4\n% synthetic E2E file\n%%EOF\n"u8.ToArray());
        return relative;
    }

    private async Task EnsureUserAsync(string email, string name, string passwordKey, string groupAlias, string groupName, Guid contactFormId, Guid? encryptedFormId)
    {
        string? password = config[passwordKey];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("{Key} is not set, so the {Email} user was not created.", passwordKey, email);
            return;
        }

        IUserGroup? group = await userGroups.GetAsync(groupAlias);
        if (group is null)
        {
            var newGroup = new UserGroup(shortStringHelper) { Alias = groupAlias, Name = groupName, Icon = "icon-users" };
            newGroup.AddAllowedSection("forms");
            var created = await userGroups.CreateAsync(newGroup, Constants.Security.SuperUserKey, []);
            if (!created.Success)
            {
                throw new InvalidOperationException($"Could not create the {groupAlias} group: {created.Status}.");
            }

            group = created.Result!;
        }

        IUser? user = users.GetAll(0, 50, out _, "Username", Direction.Ascending, null, null, email).FirstOrDefault(u => u.Email == email);
        if (user is null)
        {
            var created = await users.CreateAsync(
                Constants.Security.SuperUserKey,
                new UserCreateModel { Email = email, UserName = email, Name = name, UserGroupKeys = new HashSet<Guid> { group.Key } },
                approveUser: true);
            if (!created.Success)
            {
                throw new InvalidOperationException($"Could not create the E2E editor: {created.Status}.");
            }

            user = created.Result.CreatedUser!;
            var changed = await users.ChangePasswordAsync(
                Constants.Security.SuperUserKey,
                new ChangeUserPasswordModel { UserKey = user.Key, NewPassword = password, OldPassword = created.Result.InitialPassword });
            if (!changed.Success)
            {
                throw new InvalidOperationException($"Could not set the E2E editor password: {changed.Status}.");
            }
        }

        // Forms keeps per-user entry permissions and per-form access of its own, keyed by the legacy integer user id.
        string key = user.Id.ToString();
        if (userSecurity.GetUserSecurity(user.Id) is null)
        {
            userSecurity.InsertUserSecurity(new UserSecurity { User = key, ViewEntries = true, EditEntries = true });
        }

        if (userFormSecurity.GetUserFormSecurity(key, contactFormId) is null)
        {
            userFormSecurity.InsertUserFormSecurity(new UserFormSecurity { User = key, Form = contactFormId, HasAccess = true });
        }

        // Forms grants every form by default, so "no access to E2E Encrypted" has to be an explicit row (P3, T19).
        if (encryptedFormId is { } denied && userFormSecurity.GetUserFormSecurity(key, denied) is null)
        {
            userFormSecurity.InsertUserFormSecurity(new UserFormSecurity { User = key, Form = denied, HasAccess = false });
        }
    }
}
