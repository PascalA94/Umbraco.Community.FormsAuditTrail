import { APIResponse, expect } from '@playwright/test';
import { AUDIT } from './forms';
import { UmbracoApi } from './umbraco-api';

export interface AccessRow {
  id: number;
  eventType: string;
  formId: string;
  recordId: string | null;
  recordCount: number;
  correlationId: string | null;
  detail: string | null;
  userName: string;
}

export interface EntryChange {
  fieldCaption: string;
  oldValue: string | null;
  newValue: string | null;
  masked: boolean;
  cleared: boolean;
  valuesScrubbedAt: string | null;
}

export const ACCESS_API_MISSING =
  'The access-log API (Tasks 15 and 16) is not in this tree yet; remove this fixme once it is merged.';
export const DELETES_MISSING = 'Record deletes are captured by Task 14, which is not in this tree yet; remove this fixme once it is merged.';
export const UI_MISSING = 'The Entry access and Entry history tabs (Task 17b) are not in this tree yet; remove this fixme once they are merged.';

/**
 * True once the access-log read API exists. Only a 404 means "not merged yet"; any other non-200 is a real fault and
 * throws. With E2E_EXPECT_ACCESS_API=1 a 404 throws too, so once the API is merged a missing route fails the suite
 * instead of silently skipping every access assertion.
 */
export async function hasAccessApi(api: UmbracoApi): Promise<boolean> {
  const status = (await api.get(`${AUDIT}/access`, { pageSize: 1 })).status();
  if (status === 200) return true;
  if (status === 404 && process.env.E2E_EXPECT_ACCESS_API !== '1') return false;
  throw new Error(`GET ${AUDIT}/access gave HTTP ${status}${status === 404 ? ' and E2E_EXPECT_ACCESS_API=1 is set' : ''}.`);
}

/**
 * Positive control for every "this value is not stored" check: audit rows written by this suite do exist, so an
 * absence scan cannot pass merely because capture is switched off. Every row's Detail starts {"v":1,...}.
 */
export async function expectCaptureOn(api: UmbracoApi): Promise<void> {
  const hits = await scan(api, '{"v":1');
  expect(hits.length, 'capture is on: access rows exist in the audit tables').toBeGreaterThan(0);
}

export async function accessRows(api: UmbracoApi, query: Record<string, string | number> = {}): Promise<AccessRow[]> {
  const response = await api.get(`${AUDIT}/access`, { pageSize: 100, page: 1, ...query });
  if (response.status() !== 200) throw new Error(`access list gave HTTP ${response.status()}`);
  return (await api.json<{ items: AccessRow[] }>(response)).items;
}

/** The newest access id, so a test can ask for "rows written since I started" without clocks (ids only rise). */
export async function lastAccessId(api: UmbracoApi): Promise<number> {
  return Math.max(0, ...(await accessRows(api)).map((r) => r.id));
}

export async function rowsSince(api: UmbracoApi, since: number, eventType?: string, formId?: string): Promise<AccessRow[]> {
  return (await accessRows(api, formId ? { formId } : {})).filter(
    (r) => r.id > since && (!eventType || r.eventType === eventType),
  );
}

export async function accessDetail(api: UmbracoApi, id: number): Promise<AccessRow & { changes: EntryChange[] }> {
  const response = await api.get(`${AUDIT}/access/${id}`);
  if (response.status() !== 200) throw new Error(`access detail gave HTTP ${response.status()}`);
  return api.json(response);
}

export interface ScanHit {
  table: string;
  column: string;
  count: number;
}

/** Where, if anywhere, the four audit access tables hold `needle`. Counts only, never values. */
export async function scan(api: UmbracoApi, needle: string): Promise<ScanHit[]> {
  const response: APIResponse = await api.get('/e2e/scan', { needle });
  if (response.status() !== 200) {
    throw new Error(`/e2e/scan gave HTTP ${response.status()}; start the site with E2E__Diagnostics=true in Development.`);
  }
  return api.json<ScanHit[]>(response);
}
