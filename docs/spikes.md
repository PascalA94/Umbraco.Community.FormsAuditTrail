# Spike results

Recording rule: names, types and shapes only. Values are `<redacted>` or synthetic; never IPs, user keys, real names or typed usernames.

## M0 summary

| Spike | GO/NO-GO | Fallback or note |
|---|---|---|
| S1 | GO | Filter matches all ten controllers by `ControllerTypeInfo` on SQLite and SQL Server; scoped identity holds; user key claim `sub` |
| S2 | GO | `IRecordStorage` before copy correct, no lock, separate instance; diff by `RecordField.FieldId` |
| S3 | NO-GO (SQLite) | Delete record-set action hangs under concurrent writes, with our write after `next()` and with the `IAsyncResultFilter` fallback; Forms has the hazard with any concurrent writer; the package's writes add writers (paging: 0/71 without, 3/70 with). Pascal (2026-11-16): keep sync write after `next()`, README note (Tasks 12, 19) |
| S4 | Result: yes | `Field.ContainsSensitiveData`; check is CMS `UserExtensions.HasAccessToSensitiveData(IUser)` (group key `SensitiveDataGroupKey`). No fallback (Task 16) |
| S5 | Result: Fallback | Route delete and permanent form delete publish no `RecordDeleted`; a write in `RecordDeleting` locks (Deleted is safe). Task 14 Fallback: request deletes scrubbed in request, others by reconciliation |
| S6 | Result: no | Scheduled deletion publishes only a batched `RecordDeleting` (no `HttpContext`), no `RecordDeleted`; retention 0 means off. Reconciliation (Tasks 9, 14) |
| S7 | Result: yes, except one | CSV (both) and uploaded files by entry: one notification per row; uploaded files in disk structure: none, so `recordIdsCaptured=false`. File name `GenerateExportResponseModel.FileName` (Tasks 11b, 12) |
| S8 | Result: no measurable overhead (advisory) | 50 loads of 100 rows, SQLite: on and off medians 6.0 to 9.4 ms, p95 6.8 to 10.0 ms; on/off difference inside roughly 3 ms of boot-to-boot noise (n=2 boots per configuration); cannot size. No fallback; real check after Tasks 12, 13b and 18b |
| S9 | Result: yes | Forms 18.3.0 with CMS 18.3.1 (Forms needs CMS 18.3.0+, the current floor): ten controllers, five notification types, `ProtectFormUploadRequestsMiddleware` type and filter name, 23 types and 126 endpoints unchanged (names, routes, binding, middleware identity; uploads not re-run). No fallback (Tasks 6, 10, 12a) |
| S10 | Result: yes | Before values transformed, diff exact. Detector: started-time `IServiceCollection` descriptor scan, foreign by assembly; compose-time misses it. No fallback (Tasks 13, 15) |
| S11 | Result: audited set unchanged | 23 types same on 18.2.2 and 17.6.2; no NotAudited type returns values, ids or files. Forms authorises before 206 and 304. Upload anchor: `PrePipeline`, before filter `ProtectFormUploadRequestsMiddleware`; user from the `UmbracoBackOffice` cookie; Forms' path normalisation; literal upload root; case-sensitive `form_` (Tasks 6, 12a) |
| S12 | Result: no race | SQL Server: two instances in the same second, 5 of 5 race rounds plus a lock-held run applied once, no duplicate-object error, EF Core 10.0.12; EF's own `__EFMigrationsLock` app lock serialises them (proved by holding it). No-DDL login: caught `SqlException` 262, pages still served, package API 500 (SEC-09, R17). Task 10a needs no own lock on SQL Server; SQLite race not run |

## S1

Filter matching and parameter sources. Run 2026-11-16 on the demo site (CMS 18.3.0, Forms 18.2.2), SQLite then SQL Server 2022 (Docker), spike branch `spike/entry-audit-m0` only. Harness and probe: `spike/README-spike.md` in the spike worktree.

**How it was driven.** A global `IAsyncActionFilter` (`MvcOptions.Filters.Add<T>()`) and a notification handler, both logging at Warning. The backoffice was driven through the same Forms Management API calls its 18.2.2 client makes (query keys taken from the generated client in `Umbraco.Forms.StaticAssets`), authenticated by the backoffice login plus OpenIddict PKCE. On CMS 18 the tokens live in HttpOnly cookies and the client sends `Authorization: Bearer [redacted]`, which the server swaps for the cookie. Routes and binding sources come from a startup dump of `IActionDescriptorCollectionProvider`.

**Matching.** One BEFORE, AFTER and COMPLETED line per request for all ten controllers, on both providers (`grep` counts per controller for the action-list run: GetCollection 3, GetAuditTrail 1, UpdateRecord 1, DeleteRecord 1, ExecuteAction 2 (single and bulk), PostGenerateExport 1, Export 1, RetryWorkflow 1, CreateRecord 1, GetMemberFormSummaries 1; identical on SQLite and SQL Server). Every action returned 2xx.

**Collector identity.** For every handler line inside a filtered request, the scoped probe resolved through `IHttpContextAccessor` had the same identity hash as the filter's (SQLite: 64 list rows, 39 export rows, all record notifications; SQL Server the same). Front-end submissions (Delivery API) publish `RecordSavingNotification` with an `HttpContext` but no filter request, so a window-gated handler ignores them as designed. Three `RecordFetchingNotification`s ran with no `HttpContext` (workflow execution).

| Controller (full type name) | Verb and route (`umbraco/forms/management/api/v1/...`) | `formId` | Record id and other ids | Result |
|---|---|---|---|---|
| `Umbraco.Forms.Web.Api.ManagementApi.Record.GetCollectionController` | GET `form/{formId:guid}/record` | route (Guid) | none; filter is `model: RecordFilter` from the query | `OkObjectResult`, `EntrySearchResultCollection` |
| `Umbraco.Forms.Web.Api.ManagementApi.Record.GetAuditTrailController` | GET `form/{formId:guid}/record/{recordId:guid}/audit-trail` | route | `recordId` route | `OkObjectResult`, `List<RecordAuditEntry>` |
| `Umbraco.Forms.Web.Api.ManagementApi.Record.UpdateRecordController` | PUT `form/{formId:guid}/record/{recordId:guid}` | route | `recordId` route; body `fieldValues: IEnumerable<UpdateRecordField>` (`FieldId`, `Values: object[]`) | `OkResult` 200 |
| `Umbraco.Forms.Web.Api.ManagementApi.Record.DeleteRecordController` | DELETE `form/{formId:guid}/record/{recordId:guid}` | route | `recordId` route | `OkResult` 200 |
| `Umbraco.Forms.Web.Api.ManagementApi.Record.ExecuteActionController` | POST `form/{formId:guid}/record/actions/{actionId:guid}/execute` | route | `actionId` route; record ids in body `model: RecordActionExecution.RecordKeys` (`IEnumerable<Guid>`) | `OkResult` 200 |
| `Umbraco.Forms.Web.Api.ManagementApi.Export.PostGenerateExportController` | POST `export` | query (Guid) | none; `model: RecordExportFilter` bound from the query, no body | `OkObjectResult`, `PostGenerateExportController+GenerateExportResponseModel` (`FormId: string`, `FileName: string`) |
| `Umbraco.Forms.Web.Api.ManagementApi.Export.ExportController` (`GetExport`) | GET `export` | query, **string** | `fileName` query (string) | `PhysicalFileResult`, `application/octet-stream`, `FileDownloadName` set |
| `Umbraco.Forms.Web.Api.ManagementApi.Record.RetryWorkflowController` | POST `form/{formId:guid}/record/{recordId:guid}/workflow/{workflowId:guid}/retry` | route | `recordId` and `workflowId` route | `OkObjectResult`, `RetryWorkflowResult` (`Success`, `Message`) |
| `Umbraco.Forms.Web.Api.ManagementApi.Record.CreateRecordController` | POST `form/{formId:guid}/record` | route | none in; body `fieldValues: IEnumerable<UpdateRecordField>` | `ObjectResult` 201, anonymous `{ id: Guid }`; no `Location` header |
| `Umbraco.Forms.Web.Api.ManagementApi.Member.GetMemberFormSummariesController` | GET `member/{memberKey}/form-summaries` | none | `memberKey` route, `string` | `OkObjectResult`, `List<MemberFormSummary>` |

- **List filter parameters** (`RecordFilter`, query keys as the backoffice sends them): `skip`, `take` (page and page size), `filter` (search term), `states` (repeated), `sortBy`, `sortOrder` (`RecordSorting`), `startDate`, `endDate` (`DateTime`; `DateTime.MinValue` when absent), plus `memberKey`, `recordId`, `recordIds`, `IncludeAdditionalData`. `recordId` does not narrow the list results in 18.2.2.
- **Export type parameter:** `exportType` query key, bound to `RecordExportFilter.ExportType` (string holding the export type id). The rest of `RecordExportFilter` is the same filter shape as the list.
- **PostGenerateExport and Export:** the generated file name is `GenerateExportResponseModel.FileName` in the POST result; the download takes `formId` and `fileName` from the query and returns a `PhysicalFileResult`, so correlation by `formId` + `fileName` works.
- **RetryWorkflow `workflowId`:** route value.
- **CreateRecord new id:** the result value's `id` property (anonymous type), status 201. The same id is on the `RecordSavingNotification` record in the same request.
- **GetMemberFormSummaries `memberKey`:** a `string` route segment. The 18.2.2 backoffice member workspace passes the member's key (GUID string, the workspace `unique`), not the email. Treat it as a key only when it parses as a GUID (E16).
- **Also seen:** `GetAuditTrailController` has `formId` in the route, so no lookup from the record is needed. The 18.2.2 backoffice client never calls `DeleteRecordController` or `CreateRecordController` (both are reachable with a backoffice token only); a single delete in the UI is the Delete record-set action with one key. The Delete action only exists for users with the Forms "delete entries" permission, which is off by default even for the admin. Single delete by route publishes `RecordDeleting` but no `RecordDeleted`; the Delete action publishes `RecordDeleting` twice and `RecordDeleted` once per record (observed on 18.2.2 only; v17 untested; for S5).
- **User key claim type:** `sub` (its value equals the backoffice user key; compared in memory, never logged). Full claim type list: `nameidentifier`, `name`, `emailaddress` (xmlsoap URIs), `AspNet.Identity.SecurityStamp`, `sub`, `givenname`, `locality`, `role` (Microsoft URI), `oi_iss`, `client_id`, `oi_prst`, `oi_scp`, `oi_crt_dt`, `oi_exp_dt`, `oi_au_id`, `oi_tkn_id`, `oi_tkn_typ`. Authentication type `UmbracoBackOffice`.
- **`HttpContext.User` authenticated:** yes before `next()` and yes after, on every request; `IBackOfficeSecurityAccessor.BackOfficeSecurity.CurrentUser` resolved at both points.
- **Registered lifetimes** (from the site's `IServiceCollection`, identical on both providers):

| Service | Lifetime |
|---|---|
| `Umbraco.Forms.Core.Services.IFormService` | Singleton |
| `Umbraco.Forms.Core.Data.Storage.IRecordStorage` | Singleton |
| `Umbraco.Forms.Core.Security.IFormsSecurity` | Singleton |
| `Umbraco.Cms.Core.Services.IUserService` | Singleton |
| `Umbraco.Cms.Core.Scoping.IScopeProvider`, `Umbraco.Cms.Infrastructure.Scoping.IScopeProvider`, `Umbraco.Cms.Core.Scoping.ICoreScopeProvider` | Singleton (factory) |
| `Umbraco.Cms.Core.Security.IBackOfficeSecurityAccessor` | Singleton |
| `Microsoft.AspNetCore.Authorization.IAuthorizationService` | Transient |
| `Umbraco.Forms.Core.Searchers.IFormRecordSearcher` (also used) | Singleton |

None of the consumed Umbraco or Forms services is registered Scoped, so a singleton may take any of them directly. The package's own scoped services (`FormsAuditDbContext` and the per-request collector) are the scoped ones: any singleton (writer, health, config recorder) must open its own scope through `IServiceScopeFactory` to reach them, never inject them (P2).

- **Migrations line** on the fresh SQLite demo DB: `Applying Forms Audit Trail database migrations` appeared on the first boot (after the unattended install) and not on the second. Also seen on the first boot against an empty SQL Server database.

Not verified:

- No real browser UI pass. Calls were shaped from the backoffice's generated client in `Umbraco.Forms.StaticAssets` and sent with the backoffice's own token flow.
- `GetMemberFormSummaries` was called with a member that has no entries, so the result shape was seen empty only.

GO/NO-GO: **GO.** All ten controllers matched by `ControllerTypeInfo`, collector identity holds, on SQLite and SQL Server. No fallback; Task 12 keeps the action filter. Task 10's map and Task 12's parameter reading use the table above (note `ExportController.formId` is a string).

## S2

Before values via `IRecordStorage`. In the filter, for `UpdateRecordController` only and before `await next()`: `IFormService.Get(formId)` then `IRecordStorage.GetRecordByUniqueId(recordId, form)`, field values copied into a dictionary. `RecordSavingNotification` logged the saved record. Ten edits of one entry per provider, cycling Email, Name, the multi-value checkbox list and a blank Name (`spike/s2_edits.py`, checked by `spike/s2_check.py`).

| Check | SQLite | SQL Server |
|---|---|---|
| Edits returning 2xx | 10 of 10 | 10 of 10 |
| `database table is locked`, `SQLite Error 6`, deadlock | 0 | 0 |
| Copy holds the pre-edit values (each copy equals the previous save) | 10 of 10 | 10 of 10 |
| Notification holds the post-edit values, exactly the edited field changed | 10 of 10 | 10 of 10 |
| Same instance as Forms mutates | no (different identity hash every time) | no |
| Our loaded instance changed by Forms after `next()` | no | no |
| Storage read timing (form get plus record get) | 1.4 to 1.8 ms | 4.2 to 7.9 ms |

- **Value shapes** (`RecordField.Values`, a list of `object`): single text, one `string`; multi-value checkbox, one `string` per ticked option; file upload, one `string` holding the upload path relative to the site root (it contains the uploaded file name); blank, an empty list (no element), not an empty string. A blanked field keeps its `RecordFields` entry: the key is present and its value list is empty (seen on both providers).
- **Keying:** for a stored record, `Record.RecordFields` is a dictionary keyed by the record-field key, **not** the form field id. Diff by `RecordField.FieldId` (Task 13).
- Forms loads its own record instance in `UpdateRecordController`, so our instance is never mutated. The copy is still kept, as designed, because that is an implementation detail of 18.2.2.
- An edit with no real change (same values sent again) gave an empty diff, which supports E3 (no row).
- The read publishes one extra `RecordFetchingNotification` in the request (two per edit instead of one), so a decrypting handler runs on our read too (S10 confirms with a transforming handler).

Not verified:

- File-upload values were carried through every edit unchanged, never edited, so a changed or removed upload was not seen in the before/after pair.
- No real browser UI pass (calls shaped from the generated client, as in S1).

GO/NO-GO: **GO.** Pre-edit values captured through `IRecordStorage` with no lock on either provider, and the copy is untouched by Forms. Task 13 / 13b proceed as designed; diff by `FieldId`.

## S3

SQLite write ordering. In the filter, after `await next()`, a dummy `formsAuditTrailEntries` row written and then deleted through a fresh `IServiceScopeFactory` scope and `FormsAuditDbContext`, for update, single delete, bulk action and list requests. Load per run (`spike/s3_load.py`): 20 edits, 5 single deletes (3 by `DeleteRecordController`, 2 by the one-key Delete action, as the UI does), 2 bulk deletes of 2 entries, 30 list loads, with a second "tab" on the same session paging the list throughout. One run takes about 3 seconds; it was repeated many times because the failure is intermittent. A request counts as hung when the client's 60 second timeout expires and the server never logs the request as completed.

| Variant (SQLite unless stated) | Runs | Runs with a hung or failed Forms action | Our writes | `locked` / `SQLITE_BUSY` logged |
|---|---|---|---|---|
| A. Write after `await next()` in the action filter (the design) | 70 | 3 (each a one-key Delete record-set action) | 30,250 ok, 0 failed; p99 0.8 ms, max 303 ms | 0 |
| B. Fallback: write in `IAsyncResultFilter` after the result (`SPIKE_S3=result`) | 70 | 2 (one-key Delete action, bulk Delete action) | 15,684 ok | 0 |
| C. Control: no package write, second tab pages the list | 71 | 0 | none | 0 |
| D. Control: no package write, second tab **edits another entry through Forms** | 70 | 12 (all Delete record-set actions: 5 errored and completed late with status 499 after the client's timeout, 7 never completed before the site was stopped) | none | 12 x `SQLite Error 5: 'database is locked'` (10 in the Delete action path, 1 in the email workflow, 1 in Umbraco's `CacheInstructionsPruningJob`) |
| E. SQL Server, variant A | 20 | 0 | 2,523 ok | 0 |

- **Every hang is in Forms' Delete record-set action.** In each case the request logged `RecordFetching` and one `RecordDeleting`, then stopped. Edits, route deletes and list loads never hung in these runs. The deleting request in variant A logged no error even after several minutes. When the error surfaces (variant D) the stack is `ExecuteActionController.Execute` > `DeleteRecordSet.ExecuteAsync` > `RecordService.DeleteCoreAsync` > `WorkflowExecutionService.DeleteRecord` > `RecordStorage.DeleteRecords` > `RecordFieldValueStorage.DeleteAllRecordFieldValues`, through Umbraco's `RetryPolicy`, ending in `SQLite Error 5`.
- **Cause, as far as the evidence goes:** the Delete action path in Forms 18.2.2 does not survive another writer committing to the SQLite file while it runs. Our write is one more such writer, in a concurrent request (the second tab's list loads), not in the deleting request. Variant D shows Forms has the same hazard with no package code, but D uses a different load (a second user editing) from A (a second tab paging), so the two rates are not comparable. The like-for-like comparison is the paging load: C (no package write) 0 of 71, A (our write after `next()`) 3 of 70, B (result-filter write) 2 of 70. So the package's writes turn read-only paging into a trigger. The samples are small, and the cause is inferred from the logged stack, not observed (no stack dump). The access log adds a write to every list view, detail view and export, which is likely to make concurrent writers more common on a busy SQLite site (not measured).
- Moving the write to the result filter (B) does not help, as expected: the conflicting write is in a different request, so where it sits inside that request does not matter.
- A managed stack dump of the hung request was not taken (the tool install was not permitted in this session), so the exact SQLite lock state is inferred from the logged stack, not observed.
- SQL Server (E) showed no hang, deadlock or timeout.

Not verified:

- No managed stack dump of a hung request (tool install not permitted), so the lock state is inferred.
- In one SQLite variant A batch (20 runs) the client printed no summary line for one run; the server log shows no unfinished request in that batch. Unexplained, client side.
- No real browser UI pass (calls shaped from the generated client, as in S1).

GO/NO-GO: **NO-GO** on SQLite for the brief's criterion ("every Forms action returned 2xx"): variant A hung 3 Delete actions. The brief's fallback (B, `IAsyncResultFilter`) also hung 2. Per the brief, **stopped: Pascal to decide** before Task 12 fixes the write timing. Nothing chosen. Points for the decision: the trigger is Forms' Delete action plus any concurrent SQLite writer (D shows it with no package code), SQL Server is unaffected, and a background queue is out of MVP scope (spec 7.1).

**Pascal decided (2026-11-16), re-confirmed with the like-for-like comparison the same day:** keep the design, a synchronous write after `await next()` (Task 12). Forms has the same hazard with any concurrent SQLite writer (variant D), the package's writes add such writers (A and B against C), and SQL Server is unaffected. No queue. The README (Task 19) says that on SQLite the package's writes can trigger Forms' delete hang (as can Forms' own concurrent edits), and recommends SQL Server for multi-user sites.

## S4

Sensitive-data flag and permission check. Run 2026-11-16 on the demo site (CMS 18.3.0, Forms 18.2.2, SQLite). Member names from the decompiled 18.2.2 assemblies (`ilspycmd`, installed in a scratch folder outside the repo), confirmed at runtime by a startup and per-request probe (`EntryAudit/SpikeTask5.cs` in the spike worktree).

- **Field flag:** `Umbraco.Forms.Core.Models.Field.ContainsSensitiveData` (`bool`, public get and set, JSON `containsSensitiveData`) on 18.2.2, as on 17.6.2. At startup the probe read it through `IFormService.Get()`: the "Spike Contact" Email field `True`, Name, Interests and CV `False`. Related public members: `IFormService.ContainsSensitiveData(Form)`, `IFieldService.ContainsSensitiveData(List<Field>)`, `EntrySearchResultSchema.ContainsSensitiveData`, and the extensions `Umbraco.Forms.Core.Extensions.FormExtensions.HasSensitiveUploadField(Form, IFieldTypeStorage)` / `GetSensitiveUploadFields(...)`.
- **Permission check:** Forms has no check of its own. Every Forms call site uses the CMS public extension `Umbraco.Cms.Core.Models.UserExtensions.HasAccessToSensitiveData(this IUser user)` (assembly `Umbraco.Core`). It returns true when any of `user.Groups` has the key `Constants.Security.SensitiveDataGroupKey` (the built-in "Sensitive data" group, alias `sensitiveData`), and throws `ArgumentNullException` for a null user. Forms calls it in `FormRecordSearcher` (list and export masking), `ProtectFormUploadRequestsMiddleware` (uploads on forms with a sensitive upload field), `SaveAllUploadedFilesBase` (uploaded-files exports) and `UpdateFormController` (flag changes).
- **Version range:** the member is in the `Umbraco.Core` XML docs of every CMS version in the local NuGet cache from 17.0.0 to 18.3.1, so both floors (17.7.0, 18.2.0) have it.
- **Runtime:** in a list request the probe resolved `IBackOfficeSecurityAccessor.BackOfficeSecurity.CurrentUser` and got `HasAccessToSensitiveData() = true`, groups `admin` and `sensitiveData` (the unattended install puts the admin in both).
- **Not verified at runtime:** a user outside the `sensitiveData` group. The negative branch is read from code only (Forms' list masking uses the same call).

Result: **public check exists; no fallback.** Task 16's `ISensitiveDataPermission` wraps `IUser.HasAccessToSensitiveData()` for the user loaded through `IUserService` from the `sub` claim, and fails closed when no user resolves (the method throws on null). Task 13 reads `Field.ContainsSensitiveData`. The spec 7.5 fallback is not used.

## S5

Delete paths and notifications. Run 2026-11-16 on the demo site, SQLite, no concurrent load (one client, sequential requests), the filter's S3 write off (`SPIKE_S3=off`) so only the handler wrote. Probe: `SpikeS5Handler` in `EntryAudit/SpikeTask5.cs` logs, per notification, whether an `HttpContext` exists, whether an Umbraco ambient scope is open (`IScopeAccessor.AmbientScope`), the record count and whether record fields are loaded. With `SPIKE_S5=deleted|deleting|both` it also reads `UFRecords` for the first record id on a separate EF connection and writes and deletes a dummy `formsAuditTrailEntries` row. Driver: `spike/t5_deletes.py`.

| Path (controller, Forms call) | `RecordDeleting` | `RecordDeleted` | Where it fires |
|---|---|---|---|
| Single delete by route: `Record.DeleteRecordController` > `IRecordStorage.DeleteRecord` | once, 1 record, fields loaded | **none** | in the request, inside Forms' scope |
| Delete record-set action, 1 key and 3 keys: `Record.ExecuteActionController` > `DeleteRecordSet.ExecuteAsync` > `IRecordService.DeleteAsync` per record | twice per record (once from `WorkflowExecutionService.DeleteRecord` when the Deleted-state workflows run, once from `RecordService`), 1 record each | **once per record** | Deleting inside the scope; Deleted in the request **after** Forms' scope completed (no ambient scope, the row already gone for another connection, so committed) |
| Permanent form delete: `Form.PermanentDeleteFormController` (DELETE `form/{id}/permanent`) > `FormService.PermanentDelete` > `IRecordStorage.DeleteFormRecords(Form)` | once per batch, all records of the batch (3 here), fields loaded | **none** | in the request, inside Forms' scope |
| Empty recycle bin: `RecycleBin.EmptyRecycleBinController` (DELETE `recycle-bin/empty`), same storage call | as above | **none** | as above |
| Trash a form: `Form.DeleteFormController` (DELETE `form/{id}`) | none (records kept) | none | n/a |

- **Payloads.** `RecordDeletingNotification.DeletedEntities` (`IEnumerable<Record>`, cancelable; `Record.UniqueId`, `Record.Form` and `RecordFields` populated). `RecordDeletedNotification` is a `RecordStateChangeNotification`: `Record` (`Record`) and `Form` (`Form`), one record per notification.
- **EF write in `RecordDeleted` (the brief's check):** 24 of 24 writes ok across six runs (route, one-key and three-key deletes), longest 32 ms, `recordRowVisible=0` every time, no `database is locked`, every request 200, none hung. **No lock.**
- **EF write in `RecordDeleting` (extra, for Task 14):** this is inside Forms' open scope and it **locks**. Permanent form delete: our write waited the 30 s busy timeout and failed with `SQLite Error 5: 'database is locked'`; Forms then returned 200 after 30 s. One-key Delete action: our write succeeded, then Forms' request hung and never completed (no error logged), on two clean starts with no other client. That is the S3 hang signature (one `RecordFetching`, one `RecordDeleting`, then nothing), now reproduced with a single writer inside the request. Inference, not proven, and not for README text as fact: Forms' transaction cannot proceed after another connection commits while it is open; it fits S3 but no SQLite lock state was captured.
- **This is distinct from the S3 hazard.** S3 is a concurrent writer in another request; here no other request ran. A write after Forms' scope (`RecordDeleted`) is safe; a write during it (`RecordDeleting`) is not.
- **`ExecuteActionController` body and action type:** `Execute(Guid formId, Guid actionId, RecordActionExecution model)`; record ids are `model.RecordKeys` (`IEnumerable<Guid>`, JSON `recordKeys`), the action type is the `actionId` route value. Forms checks `FormsSecurity.CanCurrentUserDeleteEntries()` when the action is the Delete record-set action, then loads the records with `IRecordStorage.GetRecords(model.RecordKeys, form, true)`.
- **Not an option under the accepted fallback (P13c):** on permanent form delete and empty recycle bin, `RecordDeleting` fires in the request with an `HttpContext` and the full id list, but Task 14 never writes inside `RecordDeleting`; at most it may hold ids in memory, which is write-free. Permanent form delete stays on reconciliation (up to 24 hours), per the design's "Resolved by Pascal". An in-request scrub by form id is listed for Pascal as a possible later change, not adopted.
- SQL Server was not run for S5 (SQLite is the locking case).

Result: **Fallback.** Route single delete and permanent form delete (both the form route and empty recycle bin) publish no per-record `RecordDeleted`, and a write inside `RecordDeleting` locks on SQLite. Task 14 takes the Fallback branch (P13c): request deletes, single by route (`recordId` route value) and the Delete record-set action (`RecordKeys` body), are scrubbed in the request after 2xx; all non-request deletes are left to the daily reconciliation, up to 24 hours, with the README note; `Deleted_AlwaysScrubs_EvenWithNoWindow` is replaced as Task 14 states. Task 14 never writes from `RecordDeleting` (an in-memory hold of ids is the most it may do); permanent form delete and empty recycle bin stay on reconciliation.

## S6

Scheduled record deletion. Run 2026-11-16 on the demo site, SQLite.

- **How Forms triggers it:** `Umbraco.Forms.Core.HostedServices.ScheduledRecordDeletion` (internal, `RecurringHostedServiceBase`, registered with `AddHostedService`) calls the public `IScheduledRecordDeletionService.PerformScheduledRecordDeletion(DateTime.UtcNow)`, only when enabled, on a `Single` or `SchedulingPublisher` server and on MainDom. That deletes through `IRecordStorage.DeleteFormRecords(Form, FormState, DateTime)` in batches, for live and trashed forms.
- **Settings** (`Umbraco:Forms:Options:ScheduledRecordDeletion`): `Enabled` (default `false`), `Period` (default `1.00:00:00`), `FirstRunTime` (optional cron). Per form: `DaysToRetainSubmittedRecordsFor`, `DaysToRetainApprovedRecordsFor`, `DaysToRetainRejectedRecordsFor`. **A value of 0 or less means keep forever**, so the brief's "retention 0 days" deletes nothing; 1 day with back-dated entries was used instead.
- **What was done:** the job was shortened, not invoked directly. Env vars on the spike site only (no appsettings edit): `Enabled=true`, `Period=00:00:30`, `FirstRunTime="* * * * *"`. A throwaway form with submitted and approved retention 1 day (Delivery API submissions are auto-approved, so the state is `Approved`) got 3 entries; a control form with retention 0 got 3. Both forms' `UFRecords.Created` were set 4 days back in SQLite. The job ran within about two minutes and logged "3 records were deleted across 1 forms"; the retention-1 form went from 3 entries to 0, the control kept 3.
- **Notifications:** one `RecordDeletingNotification` (3 records, fields loaded) with **no `HttpContext`**, inside Forms' scope; **no `RecordDeletedNotification`**. A write there would hit the S5 lock.

Result: **No, `RecordDeleted` is not published** for scheduled deletion (SQLite only; shortened period, back-dated rows). Covered by the daily reconciliation (Task 9) under the S5 Fallback branch of Task 14; the README states the up-to-24-hour lag and that Forms' retention of 0 means off.

## S7

Exporters and the export file name. Run 2026-11-16 on the demo site, SQLite, against "Spike Contact" with 5,912 entries. Each of the four 18.2.2 export types was generated through `PostGenerateExportController` and downloaded through `ExportController` (`spike/t5_exports.py`); the probe counted `EntrySearchResultFetchingNotification`s per request by the scoped probe hash.

| Export type (`ExportType` class, id) | Reads through `IFormRecordSearcher` | Per-row notifications in the POST | File |
|---|---|---|---|
| CSV file (submitted values): `CsvExport`, `e5da2f3c-4e54-4646-aab9-2a265a6c5800` | yes | 5,912 of 5,912 | `.csv` |
| CSV file (display values): `CsvDisplayValuesExport`, `9f195c75-10a1-4285-8d6c-a73cda3293d4` | yes | 5,912 of 5,912 | `.csv` |
| Save All Uploaded Files (by entry): `SaveAllUploadedFilesByEntry`, `fa7ae082-5c6a-4fdc-babd-162c9607b343` | yes (`QueryDataBase`) | 5,912 of 5,912 | `.zip` |
| Save All Uploaded Files (in disk structure): `SaveAllUploadedFiles`, `08479664-4fd9-4c7e-9504-77b764878e86` | **no**, walks the form's upload folder directly | **0** | `.zip` |

- The notifications fire synchronously inside the POST request, so the window collects them.
- **Exports ignore paging.** `PostGenerateExportController` sets `Take = int.MaxValue`; the request sent `take=100` and all 5,912 rows were exported. Child rows per export can reach the whole form (spec Review Focus 5 already sizes this).
- The disk-structure exporter also ignores the filter (it zips every upload of the form), so neither the record ids nor an entry count are known for that row.
- **File name:** `PostGenerateExportController+GenerateExportResponseModel` with `FormId: string` and `FileName: string` (JSON `formId`, `fileName`), as S1 found. The name is a bare file name (no path separator), `.csv` or `.zip` by type. The download is `GET export?formId&fileName`, a `PhysicalFileResult` with `application/octet-stream`.
- The 18.2.2 export type list (`GET export/types`) returns `alias`, `description`, `fileExtension`, `group`, `icon`, `id`, `mimeType`, `name`.

Result: **Yes for three of four exporters, no for "in disk structure".** Spec 7.3's rule stands: record ids come from `EntrySearchResultFetchingNotification`, and an export with none (the disk-structure exporter) writes the `Exported` row with `recordIdsCaptured = false` (Tasks 11b and 12). Correction for Task 20 Step 4: the "uploaded files" row reads "ids not captured" only for the disk-structure exporter; the by-entry exporter captures ids. Download correlation by `formId` + `fileName` holds for all four (Task 11b).

## S8

Hook overhead on the entries list, local and advisory (P13d, P14). Run 2026-11-16 on the demo site (CMS 18.3.0, Forms 18.2.2), SQLite, Development, `https://localhost:44341`, one developer laptop. Script `spike/s8_time.py` in the spike worktree.

**How it was measured.** The backoffice session from the S1 harness (backoffice login plus OpenIddict PKCE, the browser's cookies and `Authorization: Bearer [redacted]`), then `GET form/{formId}/record?skip=0&take=100` on the "Spike Contact" form (5,916 entries, every response 100 rows) over one keep-alive HTTPS connection: 5 warm-up requests, then 50 timed. Time is client wall clock per request (send to last body byte). Two batches of 50 per site boot; the site was restarted between configurations, and the "on" and "off" configurations were each booted twice.

**Probe on and off.** "On" is the full spike probe: the global `IAsyncActionFilter` on every Forms Management API controller, the `EntrySearchResultFetchingNotification` handler (runs once per row, about 100 per request, each logging a Warning line), and the S3 dummy `formsAuditTrailEntries` write and delete in a fresh scope after `next()` on `GetCollection`. "On, no write" sets `SPIKE_S3=off`. "Off" sets `SPIKE_PROBE=off` in the site's environment, which skips the spike registration in the composer, so no filter, handler or dummy write is registered (the log had 0 `SpikeProbe` lines; the "on" run had 113 dummy writes for 110 list requests).

| Configuration (boot) | Batch 1 median / p95 (ms) | Batch 2 median / p95 (ms) |
|---|---|---|
| On (boot 1) | 7.1 / 7.8 | 6.0 / 6.8 |
| On (boot 2) | 11.4 / 14.2 | 9.4 / 10.0 |
| On, no write | 11.0 / 14.2 | 8.6 / 9.2 |
| Off (boot 1) | 10.7 / 13.1 | 7.8 / 8.4 |
| Off (boot 2) | 9.4 / 13.1 | 8.4 / 8.8 |

- The difference between on and off (batch 2 medians: on 6.0 and 9.4, off 7.8 and 8.4) is smaller than the spread between two boots of the same configuration (on: 6.0 against 9.4). No hook cost can be separated from noise at 100 rows: the on/off difference sits inside roughly 3 ms of boot-to-boot noise (n=2 boots per configuration), so this run cannot size the cost.
- The first batch after a boot is 1 to 3 ms slower than the second in every configuration (warm-up), so compare batch 2.
- The probe does more than the real hook will (a Warning log line per row, a write on every list), so it overstates the cost of the shipped code.

Not verified:

- SQL Server. SQLite only.
- A real browser page load (the dashboard's own requests). The collection request was timed alone.
- Concurrency: one client, sequential requests.
- The real pipeline (collector, writer, `EntrySearchResultFetching` capture). That is the point of the checks after Tasks 12 and 13b on both providers and of `load-check.spec.ts` (Task 18b).

Result: **no measurable overhead at 100 rows on SQLite; the probe's cost is inside roughly 3 ms of boot-to-boot noise (n=2 boots per configuration) and cannot be sized.** No fallback (the bounded queue for view events stays unbuilt). Advisory only; the 10 ms target is still checked after Tasks 12 and 13b on both providers and in Task 18b.

## S9

Forms 18.3.0 surface check (ruling R7: the stable 18.3.0 with CMS 18.3.1; the rc is superseded). Run 2026-11-16 in the spike worktree only, SQLite.

**Versions.** The `Umbraco.Forms` 18.3.0 nuspecs (`Umbraco.Forms`, `.Core`, `.Core.Providers`, `.Web`, `.StaticAssets`) require `Umbraco.Cms.Api.Management`, `Umbraco.Cms.Web.Common` and `Umbraco.Cms.Web.Website` `[18.3.0, 19.0.0)` and `Umbraco.Licenses` `[18.0.3, 19.0.0)`. The current CMS floor (18.3.0) already satisfies Forms 18.3.0, so no CMS floor change would be forced by it. Probed with `-p:UmbracoDemoVersion=18.3.1 -p:FormsDemoVersion=18.3.0` on restore (`--force-evaluate`, `RestoreLockedMode=false`) and build; the first restore attempt failed with no logged error (`RestoreTask returned false`) and the identical retry succeeded, so it is taken as a transient feed failure. Resolved: `Umbraco.Cms` 18.3.1, `Umbraco.Forms`, `.Core` and `.Web` 18.3.0, `Microsoft.EntityFrameworkCore` 10.0.12. The spike code (which references the five notification types: the brief's four record events, where "four" counts `RecordDeleting` and `RecordDeleted` as one delete pair, plus `EntrySearchResultFetchingNotification`, which the brief names separately and the Forms controller namespace) compiled with 0 errors. The SQLite database and the three lock files were backed up first and restored afterwards; the lock files are byte-identical to before and the demo is back on CMS 18.3.0 and Forms 18.2.2.

| Check | 18.2.2 (S1, S11) | 18.3.0 |
|---|---|---|
| Ten audited controllers matched by the filter | all ten | all ten, every request 2xx (`CreateRecord` 201), BEFORE, AFTER and COMPLETED once each (S1 action list) |
| `EntrySearchResultFetchingNotification`, `RecordFetchingNotification`, `RecordSavingNotification`, `RecordDeletingNotification`, `RecordDeletedNotification` | resolve and fire | resolve (compile) and fire at runtime; per action-list run `RecordDeleting` 7 and `RecordDeleted` 3, the same pattern as 18.2.2 (route delete: no `RecordDeleted`) |
| `ProtectFormUploadRequestsMiddleware` | `Umbraco.Forms.Web.HttpModules`, `IMiddleware` | same full name and namespace, `Umbraco.Forms.Web` 18.3.0.0, `IMiddleware`, resolves from DI |
| Pipeline filter name and order | `ProtectFormUploadRequestsMiddleware` in `PrePipeline` after `Backoffice` | same name, same order (`Backoffice, ProtectFormUploadRequestsMiddleware, UmbracoFormsOpenApi, ImageSharpComposer, UmbracoApiCommon, BackOfficeManagementApiFilter`) |
| Public types in `ManagementApi.{Record,Export,Member,Media}` | 23 | 23, the same full names, abstract and nested flags (dump diff empty): none added, none removed |
| Forms Management API endpoints (route, verbs, parameter binding) | 126 | 126, startup endpoint dump diff empty |

- The 18.3.0 boot on the 18.2.2 database logged no errors and no migration plan.
- The completeness list (design 7.3) does not change.

Not verified:

- Upload opens on 18.3.0 (the S11 403, 206 and 304 checks). Only the middleware type, filter name and order were checked.
- Response bodies of the 23 types on 18.3.0 (shapes were not re-run; the routes and binding are identical).
- Member and property changes inside the 23 types (only type names and flags were compared).
- SQL Server on 18.3.0.

Result: **yes on Forms 18.3.0 with CMS 18.3.1: type names, routes, binding and middleware identity (type, namespace, filter name and order) are unchanged, and so is the completeness list.** Upload behaviour (403, 206, 304) was not re-run on 18.3.0, so Task 12a re-checks it there before relying on the anchor. No fallback. Task 10's `FormsControllerMap`, Task 6's contract tests (type names, namespace, filter anchor) and Task 12a's anchor hold for 18.3.0; the latest CI leg will pick 18.3.0 up. Forms 18.3.0 needs CMS 18.3.0 or later, which is the current floor, so no floor change.

## S10

Encryption at rest stand-in and foreign-handler detection. Run 2026-11-16 on the demo site, SQLite. The stand-in `SpikeReverseOnRead` lives in the **demo site's assembly** (a foreign package as far as ours is concerned) and is registered by a demo-site composer only when `SPIKE_S10=on`. It handles `RecordFetchingNotification` and `EntrySearchResultFetchingNotification` and reverses each string value of the form's short-text fields; `RecordData` is stored normally. Prevalue fields (checkbox list) and upload paths are left alone because Forms validates them on save and a one-way stand-in cannot round-trip them (a first run that reversed every string got 400 "Unexpected value" on 8 of 10 edits).

**Edits.** Ten edits of one entry (Email, Name, checkbox list, blank Name), the S2 filter capture on:

| Check | Result |
|---|---|
| Edits returning 2xx | 10 of 10 |
| Our `IRecordStorage` read ran the foreign hook | yes: 2 reversals per edit (our read and Forms' own), 20 in all |
| Before values come back transformed | yes: for each text field, the before value equals the previous save reversed (18 of 18); other fields equal the previous save (18 of 18) |
| Diff shows only the edited field | yes, exactly one changed field per edit (10 of 10) |
| `database is locked`, hung requests | 0, 0 |

**Startup check.** Three methods were compared, with and without the stand-in:

| Method | Sees the stand-in | Excludes ours |
|---|---|---|
| Enumerate `builder.Services` inside our composer | **no**: the demo site's composer runs after ours (composer order is not guaranteed, so a compose-time scan is unreliable) | n/a |
| At `UmbracoApplicationStartedNotification`, enumerate the final `IServiceCollection` (captured in a singleton holder at compose) for `ServiceType == typeof(INotificationHandler<RecordFetchingNotification>)` or `typeof(INotificationAsyncHandler<RecordFetchingNotification>)`, foreign when `ImplementationType.Assembly` is not ours | yes (`SpikeReverseOnRead`, Transient) | yes (our `SpikeProbeHandler` marked not foreign) |
| At `UmbracoApplicationStartedNotification`, resolve `IEnumerable<INotificationHandler<...>>` and `IEnumerable<INotificationAsyncHandler<...>>` from a new scope and compare `GetType().Assembly` | yes (2 resolved, 1 foreign) | yes |

- Without the stand-in both started-time methods reported 0 foreign handlers. Forms 18.2.2 itself registers no handler for either notification (checked in the decompiled assemblies), so no allow-list for Forms is needed.
- Umbraco's `AddNotificationHandler<TNotification, THandler>` registers a `ServiceDescriptor` for `INotificationHandler<TNotification>` (async: `INotificationAsyncHandler<TNotification>`), so descriptor enumeration matches what the event aggregator resolves. It does not instantiate handlers, which makes it the safer of the two.
- Limits: it sees notification handlers only. A package that transforms values another way (a decorated `IRecordStorage` or field-value storage, database-level encryption) is not detected; the README notice still applies. The stand-in also did not reverse prevalue or upload fields; real encryption of those would break Forms' own validation on save too, so the README notice stays relevant (Tasks 13, 19). Only `RecordFetchingNotification` was logged; `EntrySearchResultFetchingNotification` uses the same registration path and the detector should check both.

Result: **reliable check found; no fallback.** Task 13 builds `IForeignValueHandlerDetector` with the started-time `IServiceCollection` descriptor enumeration for `INotificationHandler<T>` and `INotificationAsyncHandler<T>` over `RecordFetchingNotification` and `EntrySearchResultFetchingNotification`, foreign when the implementation assembly is not ours (no compose-time check). Task 15 keeps `config.foreignValueHandlers`. Exclusion of our own handlers was only tested against the spike's own `SpikeProbeHandler`, and only `RecordFetchingNotification` was observed at runtime: Task 13 must add a test that the detector excludes the package's own handlers by assembly, covering both `RecordFetchingNotification` and `EntrySearchResultFetchingNotification`. Task 13b's design (before values through Forms' read path) is confirmed with a transforming handler.

## S11

Forms Management API surface and upload protection, SEC-01. The 18.2.2 run is first, then the 17.6.2 run (P13e), then the differences.

### S11 on 18.2.2

Run 2026-11-16 on the demo site (CMS 18.3.0, Forms 18.2.2), SQLite, `https://localhost:44341`, Development, spike branch `spike/entry-audit-m0` only. Driven with the S1 harness (backoffice login plus OpenIddict PKCE). Seed: four new "Spike Contact" entries through the Forms Delivery API, each with a synthetic upload named `Jane_Smith_CV.pdf` and a synthetic marker in Name and Email. Detection looked for the marker, the seeded record GUIDs and `/media/forms/upload/` or the file name in each response body; no values were logged. Probe (worktree only, `EntryAudit/SpikeS11.cs`): a startup dump of the 23 types and the pipeline filter order, the S1 global action filter, and four logging middlewares for `/media/` paths added through `PostConfigure<UmbracoPipelineOptions>`. Scripts: `spike/s11_calls.py`, `spike/s11_uploads.py`.

**Inventory.** Reflection over `Umbraco.Forms.Web` 18.2.2.0 at runtime gives the same 23 public types in `Umbraco.Forms.Web.Api.ManagementApi.{Record,Export,Member,Media}` as design 7.3 (17 controllers, 3 abstract bases, 3 models). Every endpoint is under `umbraco/forms/management/api/v1/`; routes and binding come from the startup endpoint dump.

| Type | Verb and route | Response seen | Values | Record ids | Files | Decision |
|---|---|---|---|---|---|---|
| `Record.GetCollectionController` | GET `form/{formId:guid}/record` (`model: RecordFilter` from query) | 200, `{totalNumberOfResults, totalNumberOfPages, schema, results}`; each result has `uniqueId`, `fields[{fieldId, value}]`, `member`, ... For a member-linked entry `member` is `{name, email, unique}` | yes | yes | yes, upload path string in the field value | Audited (ListViewed) |
| `Record.GetAuditTrailController` | GET `form/{formId:guid}/record/{recordId:guid}/audit-trail` | 200, list of `{id, updatedBy, updatedOn}` (empty before the first edit) | no | no (record id is in the route) | no | Audited (DetailViewed): the call the entry detail view makes |
| `Record.UpdateRecordController` | PUT `form/{formId:guid}/record/{recordId:guid}` (body `fieldValues`) | 200, empty | in request body, not response | route | no | Audited (Edited) |
| `Record.DeleteRecordController` | DELETE `form/{formId:guid}/record/{recordId:guid}` | 200, empty | no | route | no | Audited (Deleted) |
| `Record.ExecuteActionController` | POST `form/{formId:guid}/record/actions/{actionId:guid}/execute` (body `RecordKeys`) | 200, empty, for Approve and Delete | no | in request body | no | Audited (Deleted / bulk action) |
| `Record.RetryWorkflowController` | POST `form/{formId:guid}/record/{recordId:guid}/workflow/{workflowId:guid}/retry` | 200, `{success, message}` | no (the workflow re-sends them by email) | route | no | Audited (WorkflowRetried) |
| `Record.CreateRecordController` | POST `form/{formId:guid}/record` (body `fieldValues`) | 201, `{id}` | in request body | new id in response | no | Audited (Created) |
| `Export.PostGenerateExportController` | POST `export` (`formId` Guid query, `model: RecordExportFilter`, binding inferred) | 200, `{formId, fileName}` for all four exporters | no | no | names a generated file | Audited (Exported) |
| `Export.ExportController` (`GetExport`) | GET `export` (`formId` **string** query, `fileName` query) | 200, `application/octet-stream`, `Content-Disposition` set. Both CSV exporters: entry values and upload paths. Both "Save All Uploaded Files" exporters: a zip of the uploaded PDFs | yes (CSV) | no record GUIDs in the CSV | yes | Audited (ExportDownloaded) |
| `Member.GetMemberFormSummariesController` | GET `member/{memberKey}/form-summaries` (`memberKey` string) | 200, `[{formId, formName, entryCount}]` for a member with one linked entry; `[]` for an unknown member | no | no (counts per form) | no | Audited (MemberSummaryViewed, data-subject lookup) |
| `Record.GetMetadataController` | GET `form/{formId:guid}/record/metadata` | 200, `{count, lastSubmittedDate}` | no | no | no | NotAudited: count and last-submitted date only. Confirmed |
| `Record.GetWorkflowAuditTrailController` | GET `form/{formId:guid}/record/{recordId:guid}/workflow-audit-trail` | 200, list of `{id, workflowKey, name, typeName, executedOn, executionStage, result}` | no | no | no | NotAudited: workflow run status, no field values. Confirmed |
| `Record.GetPageNumberController` | GET `form/{formId:guid}/record/page-number` (`filter: RecordFilter` from query) | 200, int with `recordId` and sort keys; `null` with `recordId` only | no | takes one in the query, returns none | no | NotAudited: page number only. Confirmed |
| `Record.GetRecordSetActionCollectionController` | GET `record-set-actions` | 200, list of 3 actions (`id, name, alias, icon, ...`) | no | no | no | NotAudited: action list. Confirmed |
| `Media.GetByPathMediarController` | GET `media/by-path` (`path` query) | 404 for a Forms upload path (calls CMS `IMediaService.GetMediaByPath`) | no | no | no | NotAudited: CMS media only, cannot reach Forms uploads. Confirmed |
| `Member.GetLinkableMemberPropertiesController` | GET `member/linkable-properties` (`fieldTypeId` query) | 200, list of `{alias, name}` | no | no | no | NotAudited: member type properties. Confirmed |
| `Export.GetExportTypesController` | GET `export/types` (`formId` Guid query) | 200, 4 exporters: CSV submitted values, CSV display values, Save All Uploaded Files in disk structure, by entry | no | no | no | NotAudited: exporter list. Confirmed |
| `Record.RecordControllerBase` | abstract | none | - | - | - | NotAudited: abstract base |
| `Record.GetRecordsControllerBase` | abstract | none | - | - | - | NotAudited: abstract base |
| `Media.MediaControllerBase` | abstract | none | - | - | - | NotAudited: abstract base |
| `Export.PostGenerateExportController+GenerateExportResponseModel` | model | - | - | - | - | NotAudited: model |
| `Member.LinkableMemberProperty` | model | - | - | - | - | NotAudited: model |
| `Member.MemberFormSummary` | model | - | - | - | - | NotAudited: model |

No type on the NotAudited list returned entry values, record ids of a data subject or files. **The audited set does not change for 18.2.2.** Side observations: an Editors user without access to the form got 403 from `GetCollection`, and the action filter saw that request with final status 403 (so the audit must key on the final status, not on the action running); the "in disk structure" export ignored the list filter and zipped every upload of the form (S7 already records that it captures no record ids).

**Upload middleware facts (18.2.2).**

- **Type:** `Umbraco.Forms.Web.HttpModules.ProtectFormUploadRequestsMiddleware`, assembly `Umbraco.Forms.Web` 18.2.2.0, an `IMiddleware` registered as a singleton (resolved from the root provider at startup).
- **Registration and anchor:** `Umbraco.Forms.Web.Extensions.UmbracoBuilderExtensions` adds `new UmbracoPipelineFilter("ProtectFormUploadRequestsMiddleware") { PrePipeline = app => app.UseMiddleware<ProtectFormUploadRequestsMiddleware>() }` through `Configure<UmbracoPipelineOptions>`. Runtime filter order before our inserts: `Backoffice, ProtectFormUploadRequestsMiddleware, UmbracoFormsOpenApi, ImageSharpComposer, UmbracoApiCommon, BackOfficeManagementApiFilter` (Forms' filter at index 1). In CMS 18.3.0 `UmbracoApplicationBuilder`, `PrePipeline` runs before `UseUmbracoMediaFileProvider`, `UseStaticFiles`, `PreRouting`, `UseRouting`, `PostRouting` and `UseAuthentication`.
- **Upload path:** `Umbraco.Forms.Core.Constants.Configuration.UploadPath = "forms/upload"` in the media file system. The middleware matches a hard-coded `/media/forms/upload/` prefix with the internal `Umbraco.Forms.Web.Extensions.StringExtensions.StartsWithNormalizedPath` (case-insensitive, and segment by segment with empty segments dropped). There is no option for the path, so the plan's "upload root from Forms' options" has no member to read; `Umbraco:Forms:Security:DisableFileUploadAccessProtection` turns the check off (unset here). A record's upload value is a string shaped `/media/forms/upload/form_{formGuid}/{guid}/<file>`.
- **Form id from the path:** the first `/` segment that starts with `form_` (case-sensitive), prefix removed, `Guid.TryParse`. No `form_` segment, or `FORM_`, gives 403 even for the admin. An unknown form GUID counts as a form with a sensitive upload field; the admin passes and gets 404 from the file provider.
- **Authorisation:** not from `HttpContext.User` or the bearer header. It reads the `UmbracoBackOffice` cookie scheme's cookie (`UMB_UCONTEXT`, set by the backoffice login next to `__Host-umbAccessToken`), unprotects it with the scheme's `TicketDataFormat`, loads the user by username, then requires the `forms` section, `CanUserViewEntries`, `HasAccessToForm(formId)`, and `HasAccessToSensitiveData` when any upload field on the form is marked sensitive. On deny it sets 403 and returns without calling `next`. On allow it sets `Cache-Control: no-store, no-cache, private` on response start. Our probe repeated that cookie read in `PrePipeline` and resolved a user with a name claim for the admin and the restricted user, and `no-cookie` for anonymous.
- **Results** (P1 = our filter inserted before Forms' filter, `PrePipeline`; P2 = inserted right after it, `PrePipeline`; P3 = `PreRouting`, after static files; P4 = `PostRouting`):

| Caller | Request | Status | P1 ran, final status | P2 ran | P3 / P4 ran |
|---|---|---|---|---|---|
| Admin (cookies; with or without the `Bearer` header) | plain | 200 | yes, 200 | yes | no |
| Admin | `Range: bytes=0-99` | 206 (whole 42-byte synthetic file, `Content-Range` set) | yes, 206 | yes | no |
| Admin | `Range: bytes=0-9` | 206, 10 bytes | yes, 206 | yes | no |
| Admin | `If-None-Match` from the first response | 304 | yes, 304 (`HasStarted` false) | yes | no |
| Admin | `If-Modified-Since` from the first response | 304 | yes, 304 | yes | no |
| Editors user, view entries on, no access to the form | all five requests above | 403 | yes, 403 | no | no |
| Anonymous | all five requests above | 403 | yes, 403 | no | no |
| Editors user with access, CV not sensitive | plain; `Range: bytes=0-9` | 200; 206 | yes, 200; 206 | yes | no |
| Same user, CV marked sensitive (not in Sensitive data) | plain; `Range: bytes=0-9` | 403; 403 | yes, 403 | no | no |
| Admin, CV marked sensitive (in Sensitive data) | plain | 200 | yes, 200 | yes | no |
| Admin | missing file; unknown form GUID | 404; 404 | yes, 404 | yes | yes, both |
| Admin | no `form_` segment; `FORM_` segment | 403; 403 | yes, 403 | no | no |
| Admin; anonymous | `/MEDIA/FORMS/UPLOAD/...` | 200; 403 | yes, 200; 403 | admin only | no |
| Admin | `/media//forms/upload/...`, `/media/forms//upload/...` | 200 | yes, 200 | yes | no |
| Admin; anonymous | `//media/forms/upload/...` | 200; 403 | **no** (our `StartsWith("/media/")` test missed it) | no | no |
| Anonymous | `/media/forms%2Fupload/...` | 404 (not processed by Forms, not served) | yes, 404 | yes | yes |

- **206 and 304:** Forms authorises before answering. Users without access get 403 for range and conditional requests too; the 206 and 304 come later from the media file provider, only after Forms let the request through.
- **Consequences for `UploadAuditMiddleware`:** only P1 sees every outcome (allowed 200, 206, 304 and Forms' 403). P2 sees allowed requests only; `PreRouting` and `PostRouting` never run for a served upload because the media file provider short-circuits first. `HttpContext.User` is not authenticated in `PrePipeline` (all positions logged `userAuthenticated=False`, admin included), so the middleware must resolve the user from the `UmbracoBackOffice` cookie as Forms does. Its path test must match Forms' normalisation (split on `/`, drop empty segments, compare `media`, `forms`, `upload` case-insensitively), or a `//media/...` request serves the file without an audit row. Its form id parse must mirror Forms' first `form_` segment rule. Anchor for the contract test (P9): stage `PrePipeline`, inserted immediately before the `UmbracoPipelineFilter` whose `Name` is `ProtectFormUploadRequestsMiddleware` (`PostConfigure<UmbracoPipelineOptions>`, index lookup by `Name`).

Not verified (18.2.2):

- A real browser opening the file from the entry view. Requests carried the backoffice session's own cookies, as the browser sends them.
- A non-default `UmbracoMediaPath`. Forms matches a hard-coded `/media/forms/upload/` prefix, so a site with a moved media path may not be protected by Forms at all.
- Paths with encoded characters beyond `%2F`, and a site with `DisableFileUploadAccessProtection` on.
- SQL Server. SQLite only, as the brief allows.
- Seeding detail: the member-linked entry was made by setting `UFRecords.MemberKey` on a seeded record directly, not by a logged-in member submitting the form.

### S11 on 17.6.2

Run 2026-11-16 on a throwaway `origin/v17` worktree (deleted afterwards), demo site at CMS 17.8.0 and Forms 17.6.2, SQLite, `https://localhost:44342`, Development. Driven with the backoffice login plus OpenIddict PKCE (the S1 harness, port changed). Seed: "Spike Contact" (Name, Email marked sensitive, Interests, CV upload, the scaffold's email workflow), six Delivery API entries, each with an upload named `Jane_Smith_CV.pdf`. Detection used synthetic seed markers in the response body; no values were logged. Probe: a global action filter (controller full name, status, result type), a startup dump of every endpoint in the four namespaces, and four logging middlewares added through `PostConfigure<UmbracoPipelineOptions>`.

**Inventory.** Reflection over `Umbraco.Forms.Web` 17.6.2.0 gives the same 23 public types in `Umbraco.Forms.Web.Api.ManagementApi.{Record,Export,Member,Media}` as design 7.3. Every endpoint is under `umbraco/forms/management/api/v1/`.

| Type | Verb and route | Response seen | Values | Record ids | Files | Decision |
|---|---|---|---|---|---|---|
| `Record.GetCollectionController` | GET `form/{formId:guid}/record` (`model: RecordFilter` from query) | 200, `{results, schema, totalNumberOfPages, totalNumberOfResults}`; each result has `id`, `uniqueId`, `fields[{fieldId, value}]`, `member`, `state`, ... | yes | yes | yes, upload path string in the field value | Audited (ListViewed) |
| `Record.GetAuditTrailController` | GET `form/{formId:guid}/record/{recordId:guid}/audit-trail` | 200, list of `{id, updatedBy, updatedOn}` | no | no (record id is in the route) | no | Audited (DetailViewed): it is the call the entry detail view makes, so it marks a record being opened |
| `Record.UpdateRecordController` | PUT `form/{formId:guid}/record/{recordId:guid}` (body `fieldValues`) | 200, empty | in request body, not response | route | no | Audited (Edited) |
| `Record.DeleteRecordController` | DELETE `form/{formId:guid}/record/{recordId:guid}` | 200, empty | no | route | no | Audited (Deleted) |
| `Record.ExecuteActionController` | POST `form/{formId:guid}/record/actions/{actionId:guid}/execute` (body `RecordKeys`) | 200, empty, for Approve and Delete | no | in request body | no | Audited (Deleted / bulk action) |
| `Record.RetryWorkflowController` | POST `form/{formId:guid}/record/{recordId:guid}/workflow/{workflowId:guid}/retry` | 200, `{message, success}` | no (the workflow re-sends them by email) | route | no | Audited (WorkflowRetried) |
| `Record.CreateRecordController` | POST `form/{formId:guid}/record` (body `fieldValues`) | 201, `{id}`, no `Location` header | in request body | new id in response | no | Audited (Created) |
| `Export.PostGenerateExportController` | POST `export` (`formId` Guid query, `model: RecordExportFilter`, no explicit binding source; filled from query) | 200, `{fileName, formId}` | no | no | names a generated file | Audited (Exported) |
| `Export.ExportController` (`GetExport`) | GET `export` (`formId` **string** query, `fileName` query) | 200, `application/octet-stream`, `Content-Disposition` set. CSV export: entry values. "Save All Uploaded Files" export: an archive of uploads | yes (CSV) | no record GUIDs in the CSV | yes | Audited (ExportDownloaded) |
| `Member.GetMemberFormSummariesController` | GET `member/{memberKey}/form-summaries` (`memberKey` string) | 200, `List<MemberFormSummary>` `{formId, formName, entryCount}` (seen empty at runtime; shape from the decompiled record) | no | no (counts per form) | no | Audited (MemberSummaryViewed, data-subject lookup) |
| `Record.GetMetadataController` | GET `form/{formId:guid}/record/metadata` | 200, `{count, lastSubmittedDate}` | no | no | no | NotAudited: count and last-submitted date only. Confirmed |
| `Record.GetWorkflowAuditTrailController` | GET `form/{formId:guid}/record/{recordId:guid}/workflow-audit-trail` | 200, list of `{executedOn, executionStage, id, name, result, typeName, workflowKey}` | no (a marker matched only `name`, the workflow's configured name from the form design) | no | no | NotAudited: workflow run status, no field values. Confirmed |
| `Record.GetPageNumberController` | GET `form/{formId:guid}/record/page-number` (`filter: RecordFilter` from query) | 200, nullable int (a page number for `recordId`; `null` without sort keys) | no | takes one in the query, returns none | no | NotAudited: page number only. Confirmed |
| `Record.GetRecordSetActionCollectionController` | GET `record-set-actions` | 200, list of 3 actions (`id, name, alias, ...`) | no | no | no | NotAudited: action list. Confirmed |
| `Media.GetByPathMediarController` | GET `media/by-path` (`path` query) | 404 for a Forms upload path (it calls CMS `IMediaService.GetMediaByPath`) | no | no | no | NotAudited: CMS media only, cannot reach Forms uploads. Confirmed |
| `Member.GetLinkableMemberPropertiesController` | GET `member/linkable-properties` | 200, list of `{alias, name}` | no | no | no | NotAudited: member type properties. Confirmed |
| `Export.GetExportTypesController` | GET `export/types` (`formId` Guid query) | 200, 4 exporters: CSV display values, CSV submitted values, Save All Uploaded Files by entry, in disk structure | no | no | no | NotAudited: exporter list. Confirmed |
| `Record.RecordControllerBase` | abstract | none | - | - | - | NotAudited: abstract base |
| `Record.GetRecordsControllerBase` | abstract | none | - | - | - | NotAudited: abstract base |
| `Media.MediaControllerBase` | abstract | none | - | - | - | NotAudited: abstract base |
| `Export.PostGenerateExportController+GenerateExportResponseModel` | model | - | - | - | - | NotAudited: model |
| `Member.LinkableMemberProperty` | model | - | - | - | - | NotAudited: model |
| `Member.MemberFormSummary` | model | - | - | - | - | NotAudited: model |

No type that design 7.3 lists as NotAudited returned entry values, record ids of a data subject or files. The audited set does not change for 17.6.2.

**Upload middleware facts (17.6.2).**

- **Type:** `Umbraco.Forms.Web.HttpModules.ProtectFormUploadRequestsMiddleware` (assembly `Umbraco.Forms.Web`), an `IMiddleware` registered as a singleton.
- **Registration and anchor:** in `Umbraco.Forms.Web.Extensions.UmbracoBuilderExtensions`, `Configure<UmbracoPipelineOptions>` adds `new UmbracoPipelineFilter("ProtectFormUploadRequestsMiddleware") { PrePipeline = app => app.UseMiddleware<ProtectFormUploadRequestsMiddleware>() }`. Runtime filter order: `Backoffice, ProtectFormUploadRequestsMiddleware, UmbracoFormsOpenApi, ImageSharpComposer, UmbracoApiCommon, BackOfficeManagementApiFilter`. `PrePipeline` runs before `UseUmbracoMediaFileProvider`, `UseStaticFiles`, routing and `UseAuthentication`.
- **Upload path:** `Umbraco.Forms.Core.Constants.Configuration.UploadPath = "forms/upload"` in the media file system. The middleware matches a hard-coded `/media/forms/upload/` prefix (normalised, case-insensitive). There is no setting for the path; `Umbraco:Forms:Security:DisableFileUploadAccessProtection` turns the check off (unset here). A record's upload value is a string shaped `/media/forms/upload/form_{formGuid}/{guid}/<file>`.
- **Form id from the path:** the first `/` segment starting with `form_`, prefix removed, `Guid.TryParse`. No `form_` segment gives 403. An unknown form GUID counts as a form with a sensitive upload field; an admin passes and gets 404 from the file provider.
- **Authorisation:** it does not use `HttpContext.User` or the bearer header. It reads and unprotects the `UmbracoBackOffice` cookie (the `UMB_UCONTEXT` cookie set by the backoffice login), loads the user by username, then requires the `forms` section, `CanUserViewEntries`, `HasAccessToForm(formId)`, and also `HasAccessToSensitiveData` when any upload field on the form is marked sensitive. On deny it sets 403 and returns without calling `next`. On allow it adds `Cache-Control: no-store, no-cache, private` on response start.
- **Results** (P1 = our filter inserted before Forms' filter, `PrePipeline`; P2 = inserted right after it, `PrePipeline`; P3 = `PreRouting`, after static files; P4 = `PostRouting`):

| Caller | Request | Status | P1 ran, final status | P2 ran | P3 / P4 ran |
|---|---|---|---|---|---|
| Admin (cookies; with or without the `Bearer` header) | plain | 200 | yes, 200 | yes | no |
| Admin | `Range: bytes=0-99` | 206 (whole 38-byte synthetic file, `Content-Range` set) | yes, 206 | yes | no |
| Admin | `Range: bytes=0-9` | 206, 10 bytes | yes, 206 | yes | no |
| Admin | `If-None-Match` from the first response | 304 | yes, 304 (`HasStarted` false) | yes | no |
| Admin | `If-Modified-Since` from the first response | 304 | yes, 304 | yes | no |
| User in Editors without access to the form | all five requests above | 403 | yes, 403 | no | no |
| Anonymous | all five requests above | 403 | yes, 403 | no | no |
| Editors user with access, CV not sensitive | plain | 200 | yes, 200 | yes | no |
| Same user, CV marked sensitive (no Sensitive data group) | plain | 403 | yes, 403 | no | no |
| Admin | missing `/media/...` path | 404 | yes, 404 | yes | yes, both |

- **206 and 304:** Forms authorises before answering. A user without access gets 403 for range and conditional requests too; the 206 and 304 are produced later by the media file provider, only after Forms let the request through.
- **Consequences for `UploadAuditMiddleware`:** only P1 sees every outcome (allowed 200/206/304 and Forms' 403). P2 sees only allowed requests. The `PreRouting` stage never runs for a served upload, because the media file provider short-circuits first. In `PrePipeline`, `HttpContext.User` is not authenticated (P1 and P2 logged `userAuthenticated=False` for the admin), so the middleware must resolve the user the way Forms does (unprotect the `UmbracoBackOffice` cookie) to record who opened the file. Anchor for the contract test (P9): stage `PrePipeline`, inserted immediately before the `UmbracoPipelineFilter` named `ProtectFormUploadRequestsMiddleware` (`PostConfigure<UmbracoPipelineOptions>`, index lookup by `Name`).

**Differences from the 18.2.2 S1 facts.** None found for the ten audited controllers: same full type names, verbs, route templates and binding (`ExportController.formId` is a string, `GetMemberFormSummaries.memberKey` is a string, `CreateRecord` returns 201 `{id}` with no `Location`). The decompiled `ProtectFormUploadRequestsMiddleware` source and its `UmbracoPipelineFilter("ProtectFormUploadRequestsMiddleware")` `PrePipeline` registration are identical on 17.6.2 and 18.2.2, which is a static comparison only.

Not verified:

- Path variants (`//media/...`, `FORM_`) were not run on 17.6.2; the decompiled middleware is identical to 18.2.2, where they were run (see the 18.2.2 subsection).
- `GetMemberFormSummaries` with a member who has entries. It returned an empty list; the `{formId, formName, entryCount}` shape comes from the decompiled `MemberFormSummary` record.
- A real browser opening the file from the entry view. Requests were sent with the backoffice session's own cookies, as the browser would send them.
- A non-default `UmbracoMediaPath`. Forms matches a hard-coded `/media/forms/upload/` prefix, so a site with a moved media path may not be protected by Forms at all. Not tested.
- SQL Server. SQLite only.

17.6.2 result: the 17.6.2 audited set matches design 7.3 (10 audited controllers; 7 controllers, 3 bases and 3 models on `NotAudited`, each confirmed). `UploadAuditMiddleware` goes in `PrePipeline` immediately before Forms' `ProtectFormUploadRequestsMiddleware` filter and resolves the user from the `UmbracoBackOffice` cookie. Affects Task 10 (`FormsControllerMap`, unchanged) and the upload middleware task (anchor and identity resolution).

### Differences between lines

- **Surface:** none found. The same 23 types, verbs, route templates, binding and response shapes on 18.2.2 and 17.6.2, and the same ten audited controllers and NotAudited reasons, confirmed at runtime on both, except `GetMemberFormSummaries`: on 17.6.2 it was only seen returning an empty list, so its non-empty shape there is the same by decompilation only.
- **Upload middleware:** none found. Confirmed at runtime on both: same type and namespace, same `PrePipeline` filter name, same filter order (`Backoffice, ProtectFormUploadRequestsMiddleware, UmbracoFormsOpenApi, ImageSharpComposer, UmbracoApiCommon, BackOfficeManagementApiFilter`), same cookie-based authorisation, same 403 for users without access on plain, range and conditional requests, and the sensitive-upload 403 (on 17.6.2 for a plain request only; on 18.2.2 plain and range). The `Cache-Control: no-store, no-cache, private` on allowed responses comes from the decompiled source on both lines, which is identical; the response header itself was seen on 18.2.2 only.
- **Checked on 18.2.2 only:** `GetMemberFormSummaries` for a member with an entry (`[{formId, formName, entryCount}]`, no values), the path variants (`//media/...` served to the admin and missed by a plain `StartsWith`; `FORM_` gives 403), and the cookie user read inside `PrePipeline`. The decompiled `ProtectFormUploadRequestsMiddleware` is identical on both lines, so the path behaviour is expected to match on 17.6.2, but it was not run there.

Result: **the audited set does not change on either line** (10 audited controllers; 7 controllers, 3 bases and 3 models on `NotAudited`, each confirmed on 18.2.2 and 17.6.2), so design 7.3 and Task 10's `FormsControllerMap` stay as they are. Forms authorises before answering 206 and 304, so Task 12a's `FileViewed` rule for 200, 206 and 304 holds. `UploadAuditMiddleware` goes in `PrePipeline` immediately before the `UmbracoPipelineFilter` named `ProtectFormUploadRequestsMiddleware` (`Umbraco.Forms.Web.HttpModules`), resolves the user from the `UmbracoBackOffice` cookie, and matches paths with Forms' segment normalisation and case-sensitive `form_` rule (run on 18.2.2; 17.6.2: by decompilation). The upload root is a Forms constant plus the hard-coded `/media/` prefix, not an option, so `IUploadPathResolver` mirrors Forms' literal and a contract test pins it. Affects Task 6 / P9 (anchor contract test), Task 10 (unchanged) and Task 12a (anchor, identity, path matching, upload root source).

## S12

Concurrent migrations and a login without DDL rights (SEC-09, SEC-I5). Run 2026-11-16 on the demo site (CMS 18.3.0, Forms 18.2.2) against SQL Server 2022 (RTM-CU28, 16.0.4325.4) in Docker, spike worktree only. A dedicated container on localhost, port 14333, a random SA password from `openssl rand` held in a file outside the repo, never printed; container and file removed at the end. Script `spike/s12_race.sh`.

**Setup.** Fresh database, unattended install, package migrations applied once (`InitialCreate`, history `ProductVersion` 10.0.12), then a second boot with no migration line.

**Two instances, same database, same second.** Each round drops `formsAuditTrailChanges`, `formsAuditTrailEntries` and `formsAuditTrailMigrationsHistory`, then starts two instances from the same build (`dotnet run --no-build`, ports 44351 and 44352) with one command each, back to back, so both launch within the same second. Each instance gets its own `Umbraco:CMS:Global:MainDomKeyDiscriminator`, so both acquire MainDom and boot fully, as two servers would.

| Round | Both logged "Applying" in the same second | Both logged "applied" | History rows | Package tables / indexes | Errors |
|---|---|---|---|---|---|
| 1 to 5 | yes (5 of 5) | yes (5 of 5) | 1 | 3 / 4 | none other than shutdown cancellation (`TaskCanceledException` in Examine's index rebuild when the script stopped the instances); no duplicate-object error |
| 6, with the lock held externally | yes | yes, both at the moment the external lock was released | 1 | 3 / 4 | none other than shutdown cancellation |

- **Why there is no race.** `RunAuditMigration` checks `GetPendingMigrationsAsync` and then calls `MigrateAsync`, so both instances do try to migrate. EF Core 10's SQL Server migrator takes `sp_getapplock @Resource = '__EFMigrationsLock', @LockOwner = 'Session', @LockMode = 'Exclusive'` (the statement is in `Microsoft.EntityFrameworkCore.SqlServer` 10.0.12) and reads the history again once it holds the lock, so the second instance finds nothing to apply.
- **Proof at runtime (round 6).** A `sqlcmd` session took that same app lock and held it for 10 s while both instances started. Both instances logged "Applying" 4 s after launch and "applied" 5 s later, at the moment the external session released the lock. So both instances wait on EF's own lock. The lock does not fail; it serialised them.
- Both sites answered `/umbraco` with 200 while their migration was blocked (the handler runs at `UmbracoApplicationStartedNotification`).
- **EF Core version resolved:** `Microsoft.EntityFrameworkCore` 10.0.12 (also the history `ProductVersion`), from the package's floor restore.

**Login with only `db_datareader` and `db_datawriter`.** A SQL login mapped to a database user with only those two roles (checked in `sys.database_role_members`), package tables and history dropped, one start with that login's connection string:

| Check | Result |
|---|---|
| Logged exception | `Microsoft.Data.SqlClient.SqlException`, error 262, class 14 ("CREATE TABLE permission denied in database ..."), logged at Error by `RunAuditMigration` ("Failed to apply Forms Audit Trail database migrations") and caught |
| Site still serves pages | yes: `/` 200, `/umbraco` 200, `server/status` 200, backoffice login and `user/current` 200 |
| Package dashboard API (`GET umbraco/formsaudit/api/v1/forms`) | 500, `SqlException` "Invalid object name 'formsAuditTrailEntries'" (unhandled, logged at Error) |
| State left behind | no package tables, no history table, no application lock held (`sys.dm_tran_locks`) |
| Next start with a login that has DDL rights | migrations applied, 3 tables, 1 history row |

Not verified:

- SQLite with two processes on one file. EF Core 10 also has a migration lock for SQLite (a lock table), but no SQLite race was run.
- Two separate hosts or deploy folders. Both instances ran from one build folder on one machine, with distinct MainDom keys.
- More than two instances, or a database already holding an older package migration.
- Saving a form while the tables are missing (`FormSavingHandler` and `FormSavedHandler` writing to absent tables); only page and API reads were checked.
- The exact SQL text EF ran (a Serilog override for EF command logging was not permitted in this session); the lock was shown by holding it externally instead.

Result: **5 of 5 race rounds, plus a lock-held run: migrations applied once, no duplicate-object error, EF Core 10.0.12; no race on SQL Server.** EF Core's own `__EFMigrationsLock` app lock already serialises concurrent `MigrateAsync` calls, and holding it externally proved the instances wait on it. P13g's race action is not triggered: Task 10a does not need its own `sp_getapplock` around `MigrateAsync` on SQL Server (it may keep a test that two concurrent `MigrateAsync` calls apply once). A login without DDL rights logs a caught `SqlException` (262) and the site keeps serving pages, but the package's API answers 500 until the tables exist. The no-DDL case is covered by SEC-09 (ruling R17): Task 10a's `migrationsPending` health signal and the README DDL-rights note (Task 19). SQLite two-process migration race not run: Task 10a tests it (P13g guard if it races).
