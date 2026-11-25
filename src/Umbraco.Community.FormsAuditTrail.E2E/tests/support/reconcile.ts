import { expect } from '@playwright/test';
import { AUDIT, FormInfo, RecordRow, createRecord, updateRecord } from './forms';
import { EntryChange } from './audit';
import { UmbracoApi } from './umbraco-api';
import { provider, siteDatabaseUser, sqlcmd } from './sqlserver';

export interface JobRun {
  steps: Record<string, string>;
}

export interface HealthConfig {
  entryAuditHealthy?: boolean;
  healthReasons?: string[];
  writeFailuresSinceStart?: number;
}

/** Runs the retention job once and returns its recorded outcome (the job writes `job.lastRun` itself). */
export async function runRetentionJob(api: UmbracoApi): Promise<JobRun> {
  const response = await api.post('/e2e/run-retention-job');
  expect(response.status(), 'run-retention-job').toBe(200);
  const raw = await api.json<any>(response);
  // The state is serialised with the default (Pascal case) naming; accept either so a naming change cannot hide a run.
  const steps = raw?.steps ?? raw?.Steps;
  if (!steps) throw new Error('The job recorded no steps.');
  return { steps };
}

/**
 * Erases a Forms entry the way an erasure outside the backoffice would: through Forms' storage layer, so its child
 * rows go too, and with no notification, so no in-request scrub. (A raw DELETE on UFRecords alone fails on the child
 * tables' foreign keys, which is why this is not /e2e/ufrecords/delete.)
 */
export async function deleteUfRecord(api: UmbracoApi, form: FormInfo, recordId: string): Promise<void> {
  const response = await api.post('/e2e/load/erase-record', undefined, { formId: form.id, recordId });
  expect(response.status(), 'erase-record').toBe(200);
}

export function reconciliationScrubbed(run: JobRun): number {
  const step = run.steps.reconciliation;
  const match = /^ok:(\d+)$/.exec(step ?? '');
  if (!match) throw new Error(`The reconciliation step did not succeed: "${step}".`);
  return Number(match[1]);
}

export async function healthConfig(api: UmbracoApi): Promise<HealthConfig> {
  const response = await api.get(`${AUDIT}/config`);
  expect(response.status(), 'config').toBe(200);
  return api.json<HealthConfig>(response);
}

/** A synthetic row: `updateRecord` fills every field it is not told about from the row, and a new entry has none. */
export const blankRow = (uniqueId: string): RecordRow => ({ uniqueId, fields: [] });

export async function createAndEdit(api: UmbracoApi, form: FormInfo, name: string, email: string): Promise<string> {
  const id = await createRecord(api, form, { name: [name], email: [email] });
  const response = await updateRecord(api, form, blankRow(id), { name: [`${name} edited`], email: [email] });
  expect(response.status(), 'edit').toBe(200);
  return id;
}

/** Every stored change row of every Edited access row for one entry, read through the same API the dashboard uses. */
export async function editChanges(api: UmbracoApi, recordId: string): Promise<EntryChange[]> {
  const history = await api.json<{ id: number; eventType: string }[]>(await api.get(`${AUDIT}/access/records/${recordId}`));
  const changes: EntryChange[] = [];
  for (const row of history.filter((r) => r.eventType === 'Edited')) {
    const detail = await api.json<{ changes: EntryChange[] }>(await api.get(`${AUDIT}/access/${row.id}`));
    changes.push(...detail.changes);
  }
  return changes;
}

/** Runs `fn` over `items` with at most `limit` in flight; fails on the first rejection. */
export async function pool<T>(items: readonly T[], limit: number, fn: (item: T, index: number) => Promise<void>): Promise<void> {
  let next = 0;
  const workers = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (next < items.length) {
      const index = next++;
      await fn(items[index], index);
    }
  });
  await Promise.all(workers);
}

/**
 * Makes UFRecords unreadable for the site. SQLite renames the table through the diagnostics endpoint. On SQL Server the
 * site's own login cannot DENY itself ("Cannot grant, deny, or revoke permissions to ... yourself"), so the DENY is run
 * as the privileged login through E2E_SQLCMD, against the non-owner database user the site connects as.
 */
export async function breakUfRecords(api: UmbracoApi): Promise<void> {
  if (provider() === 'sqlserver') {
    sqlcmd(`DENY SELECT ON OBJECT::UFRecords TO [${siteDatabaseUser()}]`);
    return;
  }
  expect((await api.post('/e2e/ufrecords/break')).status(), 'break').toBe(204);
}

export async function restoreUfRecords(api: UmbracoApi): Promise<void> {
  if (provider() === 'sqlserver') {
    sqlcmd(`REVOKE SELECT ON OBJECT::UFRecords FROM [${siteDatabaseUser()}]`);
    return;
  }
  expect((await api.post('/e2e/ufrecords/restore')).status(), 'restore').toBe(204);
}
