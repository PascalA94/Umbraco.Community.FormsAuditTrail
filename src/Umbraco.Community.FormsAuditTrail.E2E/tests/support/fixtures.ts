import { test as base } from '@playwright/test';
import { FormInfo, findForm } from './forms';
import { UmbracoApi } from './umbraco-api';

interface WorkerFixtures {
  admin: UmbracoApi;
  editor: UmbracoApi;
  auditor: UmbracoApi;
  contactForm: FormInfo;
  encryptedForm: FormInfo;
}

const baseURL = () => process.env.E2E_BASE_URL ?? 'https://localhost:44371';

/** Sessions and form ids are resolved once per worker; the seed is idempotent so they are stable across runs. */
export const test = base.extend<object, WorkerFixtures>({
  admin: [
    async ({}, use) => {
      const api = await UmbracoApi.login(baseURL(), process.env.E2E_ADMIN_EMAIL ?? 'admin@example.test', process.env.E2E_ADMIN_PASSWORD ?? '');
      // Deleting entries is off by default even for the administrator; grant it as the Forms Security screen would.
      const mine = await api.json<any>(await api.get('/umbraco/forms/management/api/v1/security/user/current/form-security'));
      if (!mine.userSecurity.deleteEntries) {
        mine.userSecurity.deleteEntries = true;
        await api.put(`/umbraco/forms/management/api/v1/security/user/${mine.key}/form-security`, mine);
      }
      await use(api);
      await api.dispose();
    },
    { scope: 'worker' },
  ],
  editor: [
    async ({}, use) => {
      const api = await UmbracoApi.login(baseURL(), process.env.E2E_EDITOR_EMAIL ?? 'e2e.editor@example.com', process.env.E2E_EDITOR_PASSWORD ?? '');
      await use(api);
      await api.dispose();
    },
    { scope: 'worker' },
  ],
  auditor: [
    async ({}, use) => {
      const api = await UmbracoApi.login(baseURL(), process.env.E2E_AUDITOR_EMAIL ?? 'e2e.auditor@example.com', process.env.E2E_AUDITOR_PASSWORD ?? '');
      await use(api);
      await api.dispose();
    },
    { scope: 'worker' },
  ],
  contactForm: [async ({ admin }, use) => use(await findForm(admin, 'E2E Contact')), { scope: 'worker' }],
  encryptedForm: [async ({ admin }, use) => use(await findForm(admin, 'E2E Encrypted')), { scope: 'worker' }],
});

export { expect } from '@playwright/test';
