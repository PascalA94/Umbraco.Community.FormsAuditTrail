import AxeBuilder from '@axe-core/playwright';
import { UI_MISSING } from './support/audit';
import { test, expect } from './support/fixtures';

// P16: WCAG 2.2 AA on each tab of the Audit dashboard and on the open diff viewer. The tabs are Task 17b.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
const DASHBOARD = '/umbraco/section/forms/dashboard/forms-audit-trail';

test.describe('accessibility of the Audit dashboard', () => {
  test.beforeEach(async ({ page }) => {
    test.fixme(true, UI_MISSING);
    await page.goto('/umbraco');
    await page.getByLabel('Email').fill(process.env.E2E_ADMIN_EMAIL ?? 'admin@example.test');
    await page.getByLabel('Password').fill(process.env.E2E_ADMIN_PASSWORD ?? '');
    await page.getByRole('button', { name: /login/i }).click();
    await page.goto(DASHBOARD);
  });

  for (const tab of ['Form changes', 'Entry access', 'Entry history']) {
    test(`the ${tab} tab has no WCAG 2.2 AA violations`, async ({ page }) => {
      await page.getByRole('tab', { name: tab }).click();
      const results = await new AxeBuilder({ page }).withTags(TAGS).analyze();
      expect(results.violations).toEqual([]);
    });
  }

  test('the open diff viewer has no WCAG 2.2 AA violations', async ({ page }) => {
    await page.getByRole('tab', { name: 'Entry access' }).click();
    await page.getByRole('button', { name: 'View changes' }).first().click();
    const results = await new AxeBuilder({ page }).withTags(TAGS).analyze();
    expect(results.violations).toEqual([]);
  });
});
