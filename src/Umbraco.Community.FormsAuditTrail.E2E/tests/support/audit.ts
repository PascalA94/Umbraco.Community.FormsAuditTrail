import { APIResponse } from '@playwright/test';
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
