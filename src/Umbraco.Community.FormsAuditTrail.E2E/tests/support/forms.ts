import { expect, request } from '@playwright/test';
import { UmbracoApi } from './umbraco-api';

export const FORMS = '/umbraco/forms/management/api/v1';
export const AUDIT = '/umbraco/formsaudit/api/v1';

export interface FormInfo {
  id: string;
  name: string;
  /** Field id by alias. */
  fields: Record<string, string>;
}

export interface RecordRow {
  uniqueId: string;
  fields: { fieldId: string; value: unknown }[];
}

export async function findForm(api: UmbracoApi, name: string): Promise<FormInfo> {
  const list = await api.json<{ id: string; name: string }[]>(await api.get(`${FORMS}/form`));
  const match = list.find((f) => f.name === name);
  if (!match) {
    throw new Error(`Form "${name}" not found. Start the site with E2E__Seed=true (see .env.example).`);
  }
  const form = await api.json<any>(await api.get(`${FORMS}/form/${match.id}`));
  const fields: Record<string, string> = {};
  for (const page of form.pages)
    for (const set of page.fieldSets)
      for (const container of set.containers)
        for (const field of container.fields) fields[field.alias] = field.id;
  return { id: match.id, name, fields };
}

export async function listRecords(api: UmbracoApi, form: FormInfo, take = 100): Promise<RecordRow[]> {
  const response = await api.get(`${FORMS}/form/${form.id}/record`, { skip: 0, take, sortBy: 'created', sortOrder: 'Descending' });
  expect(response.status(), 'list records').toBe(200);
  return (await api.json<{ results: RecordRow[] }>(response)).results;
}

export function valuesOf(row: RecordRow, form: FormInfo, alias: string): string[] {
  const raw = row.fields.find((f) => f.fieldId === form.fields[alias])?.value;
  if (raw === null || raw === undefined || raw === '') return [];
  return (Array.isArray(raw) ? raw : [raw]).map(String);
}

/** Sends every field of the record back with `changes` applied, so an edit never blanks a field it did not mean to. */
export async function updateRecord(
  api: UmbracoApi,
  form: FormInfo,
  row: RecordRow,
  changes: Record<string, string[]>,
) {
  const fieldValues = Object.entries(form.fields).map(([alias, fieldId]) => ({
    fieldId,
    values: changes[alias] ?? valuesOf(row, form, alias),
  }));
  return api.put(`${FORMS}/form/${form.id}/record/${row.uniqueId}`, fieldValues);
}

/** The backoffice token path, not the Delivery API: the one way Forms lets an editor create an entry (P4). */
export async function createRecord(api: UmbracoApi, form: FormInfo, values: Record<string, string[]>): Promise<string> {
  const fieldValues = Object.entries(values).map(([alias, v]) => ({ fieldId: form.fields[alias], values: v }));
  const response = await api.post(`${FORMS}/form/${form.id}/record`, fieldValues);
  expect(response.status(), 'create record').toBe(201);
  return (await api.json<{ id: string }>(response)).id;
}

export async function recordByName(api: UmbracoApi, form: FormInfo, name: string): Promise<RecordRow> {
  const row = (await listRecords(api, form)).find((r) => valuesOf(r, form, 'name')[0] === name);
  if (!row) throw new Error(`No "${name}" entry on ${form.name}. Re-seed the site.`);
  return row;
}

/**
 * A front-end submission through the Forms Delivery API, the only path that runs the form's Submitted workflows. It is
 * anonymous, so the audit's capture window never sees it, which is why it is safe to use as a set-up step.
 */
export async function submitPublicEntry(baseURL: string, form: FormInfo, values: Record<string, string[]>) {
  const ctx = await request.newContext({ baseURL, ignoreHTTPSErrors: true });
  try {
    const response = await ctx.post(`/umbraco/forms/delivery/api/v1/entries/${form.id}`, { data: { values } });
    expect([200, 202], `public submit gave HTTP ${response.status()}`).toContain(response.status());
  } finally {
    await ctx.dispose();
  }
}
