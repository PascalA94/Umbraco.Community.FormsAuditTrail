import {
  ACCESS_API_MISSING,
  DELETES_MISSING,
  accessDetail,
  accessRows,
  expectCaptureOn,
  hasAccessApi,
  lastAccessId,
  rowsSince,
  scan,
} from './support/audit';
import { test, expect } from './support/fixtures';
import { AUDIT, FORMS, createRecord, listRecords, submitPublicEntry, updateRecord, valuesOf } from './support/forms';

// A per-run suffix keeps each run's values unique, so a scan for one can only find that run's own write.
const run = Date.now().toString(36);

test.describe.serial('entry access capture', () => {
  let accessApi = false;

  test.beforeAll(async ({ admin }) => {
    accessApi = await hasAccessApi(admin);
  });

  test('opening the entries list is recorded as ListViewed', async ({ admin, contactForm }) => {
    const before = accessApi ? await lastAccessId(admin) : 0;
    const rows = await listRecords(admin, contactForm);
    expect(rows.length).toBeGreaterThanOrEqual(3);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    expect(await rowsSince(admin, before, 'ListViewed', contactForm.id)).toHaveLength(1);
  });

  test('opening an entry is recorded as DetailViewed with its record id', async ({ admin, contactForm }) => {
    const [first] = await listRecords(admin, contactForm);
    const before = accessApi ? await lastAccessId(admin) : 0;
    const response = await admin.get(`${FORMS}/form/${contactForm.id}/record/${first.uniqueId}/audit-trail`);
    expect(response.status()).toBe(200);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    const written = await rowsSince(admin, before, 'DetailViewed', contactForm.id);
    expect(written).toHaveLength(1);
    expect(written[0].recordId?.toLowerCase()).toBe(first.uniqueId.toLowerCase());
  });

  test('editing Name and Email writes one Edited row and masks the sensitive Email', async ({ admin, contactForm }) => {
    const email = `edit.${run}@example.test`;
    const editedEmail = `edited.${run}@example.test`;
    const id = await createRecord(admin, contactForm, { name: [`Edit Target ${run}`], email: [email] });
    const row = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === id.toLowerCase())!;
    const before = accessApi ? await lastAccessId(admin) : 0;

    const response = await updateRecord(admin, contactForm, row, { name: [`Edited Name ${run}`], email: [editedEmail] });
    expect(response.status()).toBe(200);

    // Masking happens at write time (SEC-04), so neither Email value may be in any audit table, with or without the API.
    // Positive control: the non-sensitive Name is stored in clear, so this fails if capture is off.
    expect((await scan(admin, `Edited Name ${run}`)).length, 'the edited Name was captured').toBeGreaterThan(0);
    expect(await scan(admin, email)).toEqual([]);
    expect(await scan(admin, editedEmail)).toEqual([]);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    const written = await rowsSince(admin, before, 'Edited', contactForm.id);
    expect(written).toHaveLength(1);
    const detail = await accessDetail(admin, written[0].id);
    const byField = Object.fromEntries(detail.changes.map((c) => [c.fieldCaption, c]));
    expect(byField['Name']).toMatchObject({ oldValue: `Edit Target ${run}`, newValue: `Edited Name ${run}`, masked: false });
    expect(byField['Email']).toMatchObject({ oldValue: '[masked]', newValue: '[masked]', masked: true });
  });

  test('exporting and downloading CSV records Exported and ExportDownloaded with one correlation id', async ({ admin, contactForm }) => {
    const types = await admin.json<{ id: string; name: string }[]>(await admin.get(`${FORMS}/export/types`, { formId: contactForm.id }));
    const csv = types.find((t) => /csv/i.test(t.name));
    expect(csv, 'a CSV exporter').toBeTruthy();
    const before = accessApi ? await lastAccessId(admin) : 0;

    const generated = await admin.post(`${FORMS}/export`, undefined, { formId: contactForm.id, exportType: csv!.id });
    expect(generated.status()).toBe(200);
    const { fileName } = await admin.json<{ fileName: string }>(generated);
    const download = await admin.get(`${FORMS}/export`, { formId: contactForm.id, fileName });
    expect(download.status()).toBe(200);
    expect((await download.body()).length).toBeGreaterThan(0);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    const exported = await rowsSince(admin, before, 'Exported', contactForm.id);
    const downloaded = await rowsSince(admin, before, 'ExportDownloaded', contactForm.id);
    expect(exported).toHaveLength(1);
    expect(downloaded).toHaveLength(1);
    expect(downloaded[0].correlationId).toBeTruthy();
    expect(downloaded[0].correlationId).toBe(exported[0].correlationId);
  });

  test('opening the uploaded CV writes FileViewed and leaves no file name in the audit data (T2)', async ({ admin, contactForm }) => {
    const jane = (await listRecords(admin, contactForm)).find((r) => valuesOf(r, contactForm, 'name')[0] === 'Jane Smith')!;
    const [path] = valuesOf(jane, contactForm, 'cv');
    expect(path, 'the seeded CV path').toContain('Jane_Smith_CV.pdf');
    const before = accessApi ? await lastAccessId(admin) : 0;

    const response = await admin.getWithCookiesOnly(path);
    expect(response.status()).toBe(200);

    // Positive control: the FileViewed row stores only a hash of the path.
    expect((await scan(admin, 'filePathHash')).length, 'the file view was captured').toBeGreaterThan(0);
    expect(await scan(admin, 'Jane_Smith')).toEqual([]);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    expect(await rowsSince(admin, before, 'FileViewed', contactForm.id)).toHaveLength(1);
    const everything = JSON.stringify(await accessRows(admin));
    expect(everything).not.toContain('Jane_Smith');
  });

  test('re-running a workflow is recorded as WorkflowRetried (T3)', async ({ admin, contactForm }) => {
    await submitPublicEntry(admin.baseURL, contactForm, { name: [`Workflow ${run}`], email: [`workflow.${run}@example.test`] });
    const row = (await listRecords(admin, contactForm)).find((r) => valuesOf(r, contactForm, 'name')[0] === `Workflow ${run}`)!;
    const trail = await admin.get(`${FORMS}/form/${contactForm.id}/record/${row.uniqueId}/workflow-audit-trail`);
    expect(trail.status()).toBe(200);
    const [executed] = await admin.json<{ workflowKey: string }[]>(trail);
    expect(executed, 'the public submission ran the form workflow').toBeTruthy();
    const before = accessApi ? await lastAccessId(admin) : 0;

    const retry = await admin.post(`${FORMS}/form/${contactForm.id}/record/${row.uniqueId}/workflow/${executed.workflowKey}/retry`);
    expect(retry.status()).toBe(200);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    expect(await rowsSince(admin, before, 'WorkflowRetried', contactForm.id)).toHaveLength(1);
  });

  test('creating an entry through the backoffice token path leaves no value in the audit tables (T3, P4)', async ({ admin, contactForm }) => {
    const before = accessApi ? await lastAccessId(admin) : 0;
    const marker = `Created-${run}`;
    const id = await createRecord(admin, contactForm, { name: [marker], email: [`${marker}@example.test`] });
    expect(id).toBeTruthy();

    // Positive control: the Created row stores field captions, so this fails if capture is off.
    expect((await scan(admin, 'fieldCaptions')).length, 'the create was captured').toBeGreaterThan(0);
    expect(await scan(admin, marker)).toEqual([]);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    const written = await rowsSince(admin, before, 'Created', contactForm.id);
    expect(written).toHaveLength(1);
    expect(written[0].recordId?.toLowerCase()).toBe(id.toLowerCase());
    // Field captions only (design 7.7): the detail names the fields filled in and holds none of their values.
    expect(written[0].detail ?? '').toContain('Name');
    expect(written[0].detail ?? '').not.toContain(marker);
  });

  test('blanking a field records Cleared and keeps no old value (T10)', async ({ admin, contactForm }) => {
    const marker = `Cleared-${run}`;
    const id = await createRecord(admin, contactForm, { name: [marker], email: [`${marker}@example.test`] });
    const row = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === id.toLowerCase())!;
    const before = accessApi ? await lastAccessId(admin) : 0;

    const response = await updateRecord(admin, contactForm, row, { name: [] });
    expect(response.status()).toBe(200);

    await expectCaptureOn(admin);
    expect(await scan(admin, marker)).toEqual([]);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    const [edited] = await rowsSince(admin, before, 'Edited', contactForm.id);
    const detail = await accessDetail(admin, edited.id);
    const name = detail.changes.find((c) => c.fieldCaption === 'Name')!;
    expect(name.cleared).toBe(true);
    expect(name.oldValue).toBeNull();
  });

  test('a field caption holding markup is returned as plain text (S3)', async ({ admin, contactForm }) => {
    const id = await createRecord(admin, contactForm, { name: [`Markup ${run}`], notes: ['before'] });
    const row = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === id.toLowerCase())!;
    const before = accessApi ? await lastAccessId(admin) : 0;
    expect((await updateRecord(admin, contactForm, row, { notes: ['after'] })).status()).toBe(200);

    test.fixme(!accessApi, ACCESS_API_MISSING);
    const [edited] = await rowsSince(admin, before, 'Edited', contactForm.id);
    const detail = await accessDetail(admin, edited.id);
    // The API hands the caption back verbatim as data; the dashboard test below proves it is rendered as text.
    expect(detail.changes.map((c) => c.fieldCaption)).toContain('<img src=x onerror="window.__x=1">');
  });

  test('a bulk delete of two entries writes one Deleted row, finds each entry in its history and scrubs its edits', async ({ admin, contactForm }) => {
    test.fixme(true, DELETES_MISSING);
    const a = await createRecord(admin, contactForm, { name: [`Delete A ${run}`], email: [`delete.a.${run}@example.test`] });
    const b = await createRecord(admin, contactForm, { name: [`Delete B ${run}`], email: [`delete.b.${run}@example.test`] });
    const rowA = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === a.toLowerCase())!;
    await updateRecord(admin, contactForm, rowA, { name: [`Delete A edited ${run}`] });
    const before = await lastAccessId(admin);

    const actions = await admin.json<{ id: string; name: string }[]>(await admin.get(`${FORMS}/record-set-actions`));
    const deleteAction = actions.find((x) => /delete/i.test(x.name))!;
    const response = await admin.post(`${FORMS}/form/${contactForm.id}/record/actions/${deleteAction.id}/execute`, { recordKeys: [a, b] });
    expect(response.status()).toBe(200);

    const deleted = await rowsSince(admin, before, 'Deleted', contactForm.id);
    expect(deleted).toHaveLength(1);
    expect(deleted[0].recordCount).toBe(2);
    for (const id of [a, b]) {
      const history = await admin.json<{ id: number }[]>(await admin.get(`${AUDIT}/access/records/${id}`));
      expect(history.map((h) => h.id)).toContain(deleted[0].id);
    }
    const editedRows = (await accessRows(admin, { recordId: a })).filter((r) => r.eventType === 'Edited');
    const detail = await accessDetail(admin, editedRows[0].id);
    expect(detail.changes.every((c) => c.valuesScrubbedAt !== null)).toBe(true);
  });
});
