import AxeBuilder from '@axe-core/playwright';
import { createRecord, listRecords, updateRecord } from './support/forms';
import { test, expect } from './support/fixtures';
import { DASHBOARD, loginThroughUi } from './support/ui';

// P16: WCAG 2.2 AA on each tab of the Audit dashboard and on the open diff viewer.
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];

test.describe('accessibility of the Audit dashboard', () => {
  test.beforeEach(async ({ page, admin, contactForm }) => {
    // The spec makes its own edit, so the diff viewer test never depends on another spec having run.
    const id = await createRecord(admin, contactForm, { name: [`Axe ${Date.now()}`], email: ['axe@example.test'] });
    const row = (await listRecords(admin, contactForm)).find((r) => r.uniqueId.toLowerCase() === id.toLowerCase())!;
    expect((await updateRecord(admin, contactForm, row, { name: [`Axe edited ${Date.now()}`] })).status()).toBe(200);

    await loginThroughUi(page, process.env.E2E_ADMIN_EMAIL ?? 'admin@example.test', process.env.E2E_ADMIN_PASSWORD ?? '');
    await page.goto(DASHBOARD);
    await expect(page.getByRole('tab', { name: 'Form changes' })).toBeVisible();
  });

  const tabs: [string, string][] = [
    ['Form changes', 'Form changes'],
    ['Entry access', 'Entry access'],
    ['Entry history', 'Entry history'],
  ];
  for (const [tab, heading] of tabs) {
    test(`the ${tab} tab has no WCAG 2.2 AA violations`, async ({ page }) => {
      await page.getByRole('tab', { name: tab }).click();
      if (tab !== 'Form changes') await expect(page.getByRole('heading', { name: heading, level: 2 })).toBeVisible();
      await expect(page.locator('forms-audit-root uui-loader')).toHaveCount(0);
      // Positive control: axe really saw the dashboard, so an empty result is not an empty page.
      const results = await new AxeBuilder({ page }).withTags(TAGS).analyze();
      expect(results.passes.length, 'axe evaluated rules against the page').toBeGreaterThan(0);
      expect(results.violations).toEqual([]);
    });
  }

  test('the open diff viewer has no WCAG 2.2 AA violations', async ({ page }) => {
    await page.getByRole('tab', { name: 'Entry access' }).click();
    await page.getByRole('button', { name: 'View changes' }).first().click();
    const viewer = page.locator('audit-diff-viewer');
    await expect(viewer.getByRole('heading', { name: /^Changes to/ })).toBeVisible();
    await expect(viewer.getByRole('table', { name: 'Field changes' })).toBeVisible();
    const results = await new AxeBuilder({ page }).withTags(TAGS).analyze();
    expect(results.violations).toEqual([]);
  });
});
