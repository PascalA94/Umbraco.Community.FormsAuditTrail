import { defineConfig } from '@playwright/test';
import dotenv from 'dotenv';

dotenv.config({ path: '.env', quiet: true });

// The site under test must run with EntryAudit__DeduplicateSeconds=0, otherwise repeat reads of the same record
// inside the window collapse into one entry and the specs miss the accesses they expect. It also needs
// Umbraco__CMS__Security__AllowConcurrentLogins=true (see the workers note below).
export default defineConfig({
  testDir: './tests',
  // One worker, in order: every spec reads the same site-wide access log and some change shared records.
  // Also required by the site: UI specs sign in through the login form, and with Umbraco's default of one login per
  // user that revokes the API session the fixtures hold. Start the demo site with
  // Umbraco__CMS__Security__AllowConcurrentLogins=true (local E2E only).
  workers: 1,
  fullyParallel: false,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'https://localhost:44371',
    // The demo site runs on the ASP.NET Core development certificate.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
});
