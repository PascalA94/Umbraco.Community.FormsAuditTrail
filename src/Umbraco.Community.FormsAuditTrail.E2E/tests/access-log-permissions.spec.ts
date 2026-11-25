import { accessRows, lastAccessId, rowsSince } from './support/audit';
import { test, expect } from './support/fixtures';
import { AUDIT, FORMS, createRecord, listRecords, updateRecord, valuesOf } from './support/forms';
import { DASHBOARD, loginThroughUi } from './support/ui';

test.describe('access log permissions', () => {
  test('a Forms editor outside the access-log group cannot read or export the access log', async ({ admin, editor }) => {
    expect((await editor.get(`${AUDIT}/access`)).status()).toBe(403);
    expect((await editor.get(`${AUDIT}/access/export`)).status()).toBe(403);
  });

  test('Forms refuses the editor the CV upload and no FileViewed row is written (T2)', async ({ admin, editor, contactForm }) => {
    const jane = (await listRecords(admin, contactForm)).find((r) => valuesOf(r, contactForm, 'name')[0] === 'Jane Smith')!;
    const [path] = valuesOf(jane, contactForm, 'cv');
    const before = await lastAccessId(admin);

    // The CV field is marked sensitive and the editor is not in the Sensitive data group, so Forms answers 403.
    expect((await editor.getWithCookiesOnly(path)).status()).toBe(403);

    expect(await rowsSince(admin, before, 'FileViewed', contactForm.id)).toHaveLength(0);
  });


  test('a Forms editor is refused every diagnostics endpoint (admin only)', async ({ admin, editor }) => {
    // Positive control: the same call works for the administrator, so a 403 here is the policy, not a missing route.
    expect((await admin.get('/e2e/scan', { needle: 'e2e-control' })).status()).toBe(200);
    expect((await editor.get('/e2e/scan', { needle: 'x' })).status()).toBe(403);
    for (const url of [
      '/e2e/run-retention-job',
      '/e2e/reseed-encrypted',
      '/e2e/ufrecords/delete?recordId=00000000-0000-0000-0000-000000000000',
      '/e2e/ufrecords/break',
      '/e2e/ufrecords/restore',
    ]) {
      expect((await editor.post(url)).status(), `editor POST ${url}`).toBe(403);
    }
  });

  // T19, T8: an access-log user who may see only E2E Contact. The seed lists the e2eAuditors group in
  // AccessLogUserGroups and denies the group's user E2E Encrypted explicitly.
  test.describe('an access-log user restricted to one form', () => {
    test.beforeEach(async ({ admin }) => {
    });

    test('refuses a filter on the form they cannot see', async ({ auditor, encryptedForm }) => {
      expect((await auditor.get(`${AUDIT}/access`, { formId: encryptedForm.id })).status()).toBe(403);
    });

    test('answers 404 for rows and record history of the form they cannot see', async ({ admin, auditor, encryptedForm }) => {
      // Create this test's own Encrypted access rows (a list view and an entry view) instead of relying on earlier specs.
      const before = await lastAccessId(admin);
      const [record] = await listRecords(admin, encryptedForm);
      expect((await admin.get(`${FORMS}/form/${encryptedForm.id}/record/${record.uniqueId}/audit-trail`)).status()).toBe(200);
      const listed = await rowsSince(admin, before, 'ListViewed', encryptedForm.id);
      const viewed = await rowsSince(admin, before, 'DetailViewed', encryptedForm.id);
      expect(listed, 'this test wrote one ListViewed row for the encrypted form').toHaveLength(1);
      expect(viewed, 'this test wrote one DetailViewed row for the encrypted form').toHaveLength(1);
      expect(viewed[0].recordId?.toLowerCase()).toBe(record.uniqueId.toLowerCase());

      // Positive controls: the administrator reads the same row and the same history.
      expect((await admin.get(`${AUDIT}/access/${listed[0].id}`)).status()).toBe(200);
      const history = await admin.json<{ id: number }[]>(await admin.get(`${AUDIT}/access/records/${record.uniqueId}`));
      expect(history.map((h) => h.id)).toContain(viewed[0].id);

      expect((await auditor.get(`${AUDIT}/access/${listed[0].id}`)).status()).toBe(404);
      expect((await auditor.get(`${AUDIT}/access/${viewed[0].id}`)).status()).toBe(404);
      expect((await auditor.get(`${AUDIT}/access/records/${record.uniqueId}`)).status()).toBe(404);
    });

    test('the unfiltered list holds no row for that form and no unresolved-form row', async ({ admin, auditor, encryptedForm, contactForm }) => {
      const before = await lastAccessId(admin);
      await listRecords(admin, contactForm);
      await listRecords(admin, encryptedForm);
      const contactRows = await rowsSince(admin, before, 'ListViewed', contactForm.id);
      const encryptedRows = await rowsSince(admin, before, 'ListViewed', encryptedForm.id);
      expect(contactRows, 'this test wrote a Contact list row').toHaveLength(1);
      expect(encryptedRows, 'this test wrote an Encrypted list row').toHaveLength(1);

      const visible = await accessRows(auditor);
      expect(visible.map((r) => r.id), 'the auditor sees the Contact row this test just caused').toContain(contactRows[0].id);
      expect(visible.map((r) => r.id)).not.toContain(encryptedRows[0].id);
      expect(visible.every((r) => r.formId === contactForm.id)).toBe(true);
      const all = await accessRows(admin);
      expect(all.map((r) => r.id), 'positive control: the administrator does see the Encrypted row').toContain(encryptedRows[0].id);
    });

    test('shows the sensitive Email change as [masked]', async ({ admin, auditor, contactForm }) => {
      const id = await createRecord(admin, contactForm, { name: [`Auditor ${Date.now()}`], email: ['auditor.before@example.test'] });
      const row = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === id.toLowerCase())!;
      const before = await lastAccessId(admin);
      expect((await updateRecord(admin, contactForm, row, { email: ['auditor.after@example.test'] })).status()).toBe(200);
      const [edited] = await rowsSince(admin, before, 'Edited', contactForm.id);
      const detail = await auditor.json<{ changes: { fieldCaption: string; oldValue: string | null; newValue: string | null }[] }>(
        await auditor.get(`${AUDIT}/access/${edited.id}`),
      );
      const email = detail.changes.find((c) => c.fieldCaption === 'Email')!;
      expect(email).toMatchObject({ oldValue: '[masked]', newValue: '[masked]' });
      expect(JSON.stringify(detail)).not.toContain('auditor.');
    });
  });

  // The UI specs drive the Audit dashboard (tabs "Form changes", "Entry access", "Entry history"; deep link ?recordId=).
  test('markup in a stored field caption is rendered as text in the diff viewer (S3)', async ({ page, admin, contactForm }) => {
    const caption = '<img src=x onerror="window.__x=1">';
    const id = await createRecord(admin, contactForm, { name: [`Markup UI ${Date.now()}`], notes: ['a'] });
    const row = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === id.toLowerCase())!;
    expect((await updateRecord(admin, contactForm, row, { notes: ['b'] })).status()).toBe(200);

    await loginThroughUi(page, process.env.E2E_ADMIN_EMAIL ?? 'admin@example.test', process.env.E2E_ADMIN_PASSWORD ?? '');
    await page.goto(DASHBOARD);
    await page.getByRole('tab', { name: 'Entry access' }).click();
    await page.getByLabel('Entry id').fill(id);
    await page.getByLabel('Entry id').blur();

    // Open the diff viewer for this entry's own Edited row.
    const edited = page.getByRole('row').filter({ hasText: 'Edited' });
    await expect(edited).toHaveCount(1);
    await edited.getByRole('button', { name: 'View changes' }).click();
    const viewer = page.locator('audit-diff-viewer');
    await expect(viewer.getByRole('heading', { name: /^Changes to E2E Contact/ })).toBeVisible();
    await expect(viewer.getByRole('cell', { name: caption, exact: true })).toBeVisible();

    // Rendered as text, never as markup: no injected element and the handler never ran.
    await expect(page.locator('img[src="x"]')).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __x?: number }).__x)).toBeUndefined();
  });

  test('the access-log user restricted to one form sees [masked] for Email in the diff viewer', async ({ browser, admin, contactForm }) => {
    const id = await createRecord(admin, contactForm, { name: [`Masked UI ${Date.now()}`], email: ['masked.ui.before@example.test'] });
    const row = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === id.toLowerCase())!;
    expect((await updateRecord(admin, contactForm, row, { email: ['masked.ui.after@example.test'] })).status()).toBe(200);

    const page = await browser.newPage({ ignoreHTTPSErrors: true });
    try {
      await loginThroughUi(page, process.env.E2E_AUDITOR_EMAIL ?? 'e2e.auditor@example.com', process.env.E2E_AUDITOR_PASSWORD ?? '');
      await page.goto(DASHBOARD);
      await page.getByRole('tab', { name: 'Entry access' }).click();
      await page.getByLabel('Entry id').fill(id);
      await page.getByLabel('Entry id').blur();
      const edited = page.getByRole('row').filter({ hasText: 'Edited' });
      await expect(edited).toHaveCount(1);
      await edited.getByRole('button', { name: 'View changes' }).click();
      const viewer = page.locator('audit-diff-viewer');
      await expect(viewer.getByRole('row').filter({ hasText: 'Email' }).getByText('[masked]')).toHaveCount(2);
      await expect(page.getByText('masked.ui')).toHaveCount(0);
    } finally {
      await page.close();
    }
  });

  test('the editor sees only the Form changes tab', async ({ page }) => {
    await loginThroughUi(page, process.env.E2E_EDITOR_EMAIL ?? 'e2e.editor@example.com', process.env.E2E_EDITOR_PASSWORD ?? '');
    await page.goto(DASHBOARD);
    await expect(page.getByRole('tab', { name: 'Form changes' })).toBeVisible();
    await expect(page.getByRole('tab', { name: 'Entry access' })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: 'Entry history' })).toHaveCount(0);
  });

  test('the admin deep link opens the entry history and a script value leaves the input empty', async ({ page, admin, contactForm }) => {
    const [first] = await listRecords(admin, contactForm);
    await loginThroughUi(page, process.env.E2E_ADMIN_EMAIL ?? 'admin@example.test', process.env.E2E_ADMIN_PASSWORD ?? '');
    await page.goto(`${DASHBOARD}?recordId=${first.uniqueId}`);
    await expect(page.getByLabel('Entry id')).toHaveValue(first.uniqueId.toLowerCase());

    await page.goto(`${DASHBOARD}?recordId=%3Cscript%3E`);
    await expect(page.getByRole('tab', { name: 'Form changes' })).toBeVisible();
    // An invalid id never opens the history tab on its own, and the input it would have filled stays empty.
    await expect(page.getByLabel('Entry id')).toHaveCount(0);
    await page.getByRole('tab', { name: 'Entry history' }).click();
    await expect(page.getByLabel('Entry id')).toHaveValue('');
  });
});
