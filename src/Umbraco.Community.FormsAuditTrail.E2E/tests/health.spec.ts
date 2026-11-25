import { Page } from '@playwright/test';
import { listRecords, updateRecord, valuesOf } from './support/forms';
import { test, expect } from './support/fixtures';
import { lastAccessId, rowsSince } from './support/audit';
import { healthConfig } from './support/reconcile';
import { provider, siteDatabaseUser, sqlcmd } from './support/sqlserver';
import { DASHBOARD, loginThroughUi } from './support/ui';

/**
 * T8: a database that refuses the audit's writes must not break Forms, must be visible to the person who can read the
 * access log, and must leave a trace (an AuditGap row) once it can be written again.
 *
 * SQL Server only. The writes are refused with DENY INSERT on the access table for the user the site connects as, which
 * binds a plain database user but not dbo or a sysadmin login, so the site must connect as a non-owner (see .env.example).
 */
const REASON = 'health.spec.ts needs a DENY on the site login, which only SQL Server can do; it runs on the SQL Server pass only.';

test.describe.configure({ mode: 'serial' });

test.describe('audit write failures are visible, and recorded once writes work again', () => {
  test.skip(provider() !== 'sqlserver', REASON);

  const table = 'dbo.formsAuditTrailAccess';

  // Always put the permission back, even when an assertion fails halfway, or every later spec runs against a denied table.
  const revoke = () => sqlcmd(`REVOKE INSERT ON ${table} FROM [${siteDatabaseUser()}];`);

  test.afterAll(() => {
    if (provider() === 'sqlserver') revoke();
  });

  // The shared UI helpers: the backoffice login field is named "username", not "email".
  async function loginUi(page: Page): Promise<void> {
    await loginThroughUi(page, process.env.E2E_ADMIN_EMAIL ?? '', process.env.E2E_ADMIN_PASSWORD ?? '');
  }

  async function openDashboard(page: Page): Promise<void> {
    await page.goto(DASHBOARD);
    // Locators pierce the dashboard's shadow DOM; page text does not.
    await page.getByText('Forms Audit Trail').first().waitFor();
  }

  test('a denied insert does not break Forms, shows a red banner, and the gap is recorded after the REVOKE', async ({ admin, contactForm, browser }) => {
    test.setTimeout(180_000);
    const before = await lastAccessId(admin);

    sqlcmd(`DENY INSERT ON ${table} TO [${siteDatabaseUser()}];`);
    let denied = true;
    try {
      // Forms keeps working: the list loads and an edit is applied.
      const rows = await listRecords(admin, contactForm);
      expect(rows.length).toBeGreaterThan(0);
      const row = rows[0];
      const original = valuesOf(row, contactForm, 'name')[0] ?? '';
      const edited = `${original.replace(/ \(health\)$/, '')} (health)`;
      expect((await updateRecord(admin, contactForm, row, { name: [edited] })).status(), 'edit while the audit cannot write').toBe(200);
      const after = (await listRecords(admin, contactForm)).find((r) => r.uniqueId === row.uniqueId)!;
      expect(valuesOf(after, contactForm, 'name')[0], 'the edit took effect in Forms').toBe(edited);

      // No access row was written for any of it.
      expect(await rowsSince(admin, before, 'ListViewed', contactForm.id), 'no ListViewed row').toHaveLength(0);
      expect(await rowsSince(admin, before, 'Edited', contactForm.id), 'no Edited row').toHaveLength(0);

      const config = await healthConfig(admin);
      expect(config.entryAuditHealthy).toBe(false);
      expect(config.healthReasons).toContain('writeFailures');
      expect(config.writeFailuresSinceStart ?? 0).toBeGreaterThanOrEqual(2);

      // The red banner, as the person reading the access log sees it.
      const context = await browser.newContext({ ignoreHTTPSErrors: true, baseURL: process.env.E2E_BASE_URL ?? 'https://localhost:44391' });
      const page = await context.newPage();
      try {
        await loginUi(page);
        await openDashboard(page);
        await expect(page.getByText(/writes? failed/)).toBeVisible({ timeout: 30_000 });
      } finally {
        await context.close();
      }

      revoke();
      denied = false;

      // The next write succeeds and brings the gap with it.
      await listRecords(admin, contactForm);
      const gaps = await rowsSince(admin, before, 'AuditGap');
      expect(gaps.length, 'an AuditGap row records the outage').toBeGreaterThanOrEqual(1);
      expect(gaps[0].detail ?? '').toContain('"v":1');
      expect(await rowsSince(admin, before, 'ListViewed', contactForm.id), 'writes work again').not.toHaveLength(0);

      const recovered = await healthConfig(admin);
      expect(recovered.healthReasons ?? []).not.toContain('writeFailures');

      const context2 = await browser.newContext({ ignoreHTTPSErrors: true, baseURL: process.env.E2E_BASE_URL ?? 'https://localhost:44391' });
      const page2 = await context2.newPage();
      try {
        await loginUi(page2);
        await openDashboard(page2);
        await expect(page2.getByText(/writes? failed/)).toHaveCount(0);
      } finally {
        await context2.close();
      }
    } finally {
      if (denied) revoke();
    }
  });
});
