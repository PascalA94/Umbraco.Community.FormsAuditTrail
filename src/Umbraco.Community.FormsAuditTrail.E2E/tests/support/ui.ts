import type { Page } from '@playwright/test';

export const DASHBOARD = '/umbraco/section/forms/dashboard/forms-audit-trail';

/** Signs in through the backoffice login form, the way a person does, and waits for the backoffice to load. */
export async function loginThroughUi(page: Page, email: string, password: string): Promise<void> {
  if (!password) throw new Error(`No password for ${email}. Copy .env.example to .env and fill it in.`);
  await page.goto('/umbraco');
  await page.locator('input[name="username"]').fill(email);
  await page.locator('input[name="password"]').fill(password);
  await page.getByRole('button', { name: /login/i }).click();
  await page.waitForURL(/\/umbraco\/section\//);
}
