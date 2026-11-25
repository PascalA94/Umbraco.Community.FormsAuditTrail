import { ACCESS_API_MISSING, UI_MISSING, accessRows, hasAccessApi, lastAccessId, rowsSince } from './support/audit';
import { test, expect } from './support/fixtures';
import { AUDIT, createRecord, listRecords, updateRecord, valuesOf } from './support/forms';

test.describe('access log permissions', () => {
  test('a Forms editor outside the access-log group cannot read or export the access log', async ({ admin, editor }) => {
    test.fixme(!(await hasAccessApi(admin)), ACCESS_API_MISSING);
    expect((await editor.get(`${AUDIT}/access`)).status()).toBe(403);
    expect((await editor.get(`${AUDIT}/access/export`)).status()).toBe(403);
  });

  test('Forms refuses the editor the CV upload and no FileViewed row is written (T2)', async ({ admin, editor, contactForm }) => {
    const jane = (await listRecords(admin, contactForm)).find((r) => valuesOf(r, contactForm, 'name')[0] === 'Jane Smith')!;
    const [path] = valuesOf(jane, contactForm, 'cv');
    const accessApi = await hasAccessApi(admin);
    const before = accessApi ? await lastAccessId(admin) : 0;

    // The CV field is marked sensitive and the editor is not in the Sensitive data group, so Forms answers 403.
    expect((await editor.getWithCookiesOnly(path)).status()).toBe(403);

    test.fixme(!accessApi, ACCESS_API_MISSING);
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
      test.fixme(!(await hasAccessApi(admin)), ACCESS_API_MISSING);
    });

    test('refuses a filter on the form they cannot see', async ({ auditor, encryptedForm }) => {
      expect((await auditor.get(`${AUDIT}/access`, { formId: encryptedForm.id })).status()).toBe(403);
    });

    test('answers 404 for rows and record history of the form they cannot see', async ({ admin, auditor, encryptedForm }) => {
      const [record] = await listRecords(admin, encryptedForm);
      const [row] = await accessRows(admin, { formId: encryptedForm.id });
      expect(row, 'an access row for the encrypted form exists').toBeTruthy();
      expect((await auditor.get(`${AUDIT}/access/${row.id}`)).status()).toBe(404);
      expect((await auditor.get(`${AUDIT}/access/records/${record.uniqueId}`)).status()).toBe(404);
      // Positive control: the administrator can read the same row.
      expect((await admin.get(`${AUDIT}/access/${row.id}`)).status()).toBe(200);
    });

    test('the unfiltered list holds no row for that form and no unresolved-form row', async ({ admin, auditor, encryptedForm, contactForm }) => {
      await listRecords(admin, encryptedForm);
      const visible = await accessRows(auditor);
      expect(visible.length, 'the auditor sees E2E Contact rows').toBeGreaterThan(0);
      expect(visible.every((r) => r.formId === contactForm.id)).toBe(true);
      const all = await accessRows(admin);
      expect(all.some((r) => r.formId === encryptedForm.id), 'positive control: the admin does see the encrypted form rows').toBe(true);
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

  // The UI specs below drive the Audit dashboard. They are written against the contract in Task 17b
  // (tabs "Form changes", "Entry access", "Entry history"; deep link ?recordId=).
  test('markup in a stored field caption is rendered as text in the access view and the diff viewer (S3)', async ({ page, admin, contactForm }) => {
    test.fixme(true, UI_MISSING);
    const id = await createRecord(admin, contactForm, { name: [`Markup UI ${Date.now()}`], notes: ['a'] });
    const row = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === id.toLowerCase())!;
    await updateRecord(admin, contactForm, row, { notes: ['b'] });
    await loginThroughUi(page, process.env.E2E_ADMIN_EMAIL ?? 'admin@example.test', process.env.E2E_ADMIN_PASSWORD ?? '');
    await page.goto('/umbraco/section/forms/dashboard/forms-audit-trail');
    await page.getByRole('tab', { name: 'Entry access' }).click();
    await page.getByRole('button', { name: 'View changes' }).first().click();
    const caption = '<img src=x onerror="window.__x=1">';
    await expect(page.getByText(caption, { exact: true }).first()).toBeVisible();
    expect(await page.evaluate(() => (window as unknown as { __x?: number }).__x)).toBeUndefined();
  });

  test('the access-log user restricted to one form sees [masked] for Email in the diff viewer', async ({ browser }) => {
    test.fixme(true, UI_MISSING);
    const page = await browser.newPage({ ignoreHTTPSErrors: true });
    await loginThroughUi(page, process.env.E2E_AUDITOR_EMAIL ?? 'e2e.auditor@example.com', process.env.E2E_AUDITOR_PASSWORD ?? '');
    await page.goto('/umbraco/section/forms/dashboard/forms-audit-trail');
    await page.getByRole('tab', { name: 'Entry access' }).click();
    await page.getByRole('button', { name: 'View changes' }).first().click();
    await expect(page.getByText('[masked]').first()).toBeVisible();
  });

  test('the editor sees only the Form changes tab', async ({ page }) => {
    test.fixme(true, UI_MISSING);
    await loginThroughUi(page, process.env.E2E_EDITOR_EMAIL ?? 'e2e.editor@example.com', process.env.E2E_EDITOR_PASSWORD ?? '');
    await page.goto('/umbraco/section/forms/dashboard/forms-audit-trail');
    await expect(page.getByRole('tab', { name: 'Form changes' })).toBeVisible();
    await expect(page.getByRole('tab', { name: 'Entry access' })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: 'Entry history' })).toHaveCount(0);
  });

  test('the admin deep link opens the entry history and a script value leaves the input empty', async ({ page, admin, contactForm }) => {
    test.fixme(true, UI_MISSING);
    const [first] = await listRecords(admin, contactForm);
    await loginThroughUi(page, process.env.E2E_ADMIN_EMAIL ?? 'admin@example.test', process.env.E2E_ADMIN_PASSWORD ?? '');
    await page.goto(`/umbraco/section/forms/dashboard/forms-audit-trail?recordId=${first.uniqueId}`);
    await expect(page.getByLabel('Record id')).toHaveValue(first.uniqueId.toLowerCase());

    await page.goto('/umbraco/section/forms/dashboard/forms-audit-trail?recordId=%3Cscript%3E');
    await expect(page.getByLabel('Record id')).toHaveValue('');
  });
});

async function loginThroughUi(page: import('@playwright/test').Page, email: string, password: string) {
  await page.goto('/umbraco');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: /login/i }).click();
  await page.waitForURL(/\/umbraco\/section\//);
}
