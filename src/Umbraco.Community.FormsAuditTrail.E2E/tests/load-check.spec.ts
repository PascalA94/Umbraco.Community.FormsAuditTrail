import fs from 'node:fs';
import { AUDIT, FORMS, FormInfo, RecordRow, createRecord, listRecords, updateRecord } from './support/forms';
import { test, expect } from './support/fixtures';
import { lastAccessId, rowsSince } from './support/audit';
import { Pass, Stats, fmt, noTimeout, passFile, readPass, stats, timed } from './support/load';
import { pool } from './support/reconcile';
import { provider, writeConcurrency } from './support/sqlserver';
import { UmbracoApi } from './support/umbraco-api';

/**
 * P14: local and advisory. What the package costs per request, and how it behaves at the sizes its design caps name.
 *
 * Overhead needs two site boots, so it runs in two passes with the same specs:
 *   1. site started normally:                      E2E_LOAD_MODE=on  npx playwright test load-check
 *   2. site started with EntryAudit:Enabled=false: E2E_LOAD_MODE=off npx playwright test load-check
 *   3. compare (either mode, both passes done):                      npx playwright test load-check -g compare
 * The volume checks (5,000 entry bulk delete, 100,000 row export) run with E2E_LOAD_VOLUME=1 on the "on" pass.
 *
 * A median difference of 10 ms or more fails the compare, which is the trigger for the Task 12 Step 6 fallback: stop and
 * ask Pascal, do not tune around it.
 */
test.describe.configure({ mode: 'serial' });

const THRESHOLD_MS = 10;
const LOADS = 50;
const EDITS = 50;
const SAMPLES = 3;
const WARMUPS = 5;
const mode = process.env.E2E_LOAD_MODE === 'on' || process.env.E2E_LOAD_MODE === 'off' ? process.env.E2E_LOAD_MODE : null;
const volume = process.env.E2E_LOAD_VOLUME === '1';

async function ensureRows(api: UmbracoApi, form: FormInfo, wanted: number): Promise<void> {
  const page = await api.json<{ totalNumberOfResults: number }>(await api.get(`${FORMS}/form/${form.id}/record`, { skip: 0, take: 1 }));
  const missing = wanted - page.totalNumberOfResults;
  if (missing > 0) {
    await pool(Array.from({ length: missing }), writeConcurrency(), async (_, i) => {
      await createRecord(api, form, { name: [`Load ${Date.now().toString(36)} ${i}`], email: [`load.${i}@example.test`] });
    });
  }
}

test.describe('per-request overhead of the entry audit', () => {
  test(`measure this pass (E2E_LOAD_MODE=${mode ?? 'on|off'})`, async ({ admin, contactForm }) => {
    test.skip(mode === null, 'Set E2E_LOAD_MODE=on, or off with the site started with EntryAudit:Enabled=false, to record a pass.');
    test.setTimeout(600_000);

    // The pass must be what it says it is: a pass recorded against the wrong site would make the compare meaningless.
    const config = await admin.json<{ entryAuditEnabled?: boolean }>(await admin.get(`${AUDIT}/config`));
    expect(config.entryAuditEnabled, `the site's EntryAudit:Enabled must be ${mode === 'on'} for this pass`).toBe(mode === 'on');

    await ensureRows(admin, contactForm, 100);
    const listUrl = `${FORMS}/form/${contactForm.id}/record`;
    const query = { skip: 0, take: 100, sortBy: 'created', sortOrder: 'Descending' };

    const listOnce = async () => {
      const { ms, response } = await timed(() => admin.get(listUrl, query));
      expect(response.status()).toBe(200);
      return ms;
    };
    for (let i = 0; i < WARMUPS; i++) await listOnce();
    const list: Stats[] = [];
    for (let k = 0; k < SAMPLES; k++) {
      const one: number[] = [];
      for (let i = 0; i < LOADS; i++) one.push(await listOnce());
      list.push(stats(one));
    }

    const rows = await listRecords(admin, contactForm, 100);
    expect(rows.length, 'a 100-row page').toBe(100);

    // The call the entry detail view makes, and the one that marks a record as opened.
    const detailUrl = `${FORMS}/form/${contactForm.id}/record/${rows[0].uniqueId}/audit-trail`;
    const detailOnce = async () => {
      const { ms, response } = await timed(() => admin.get(detailUrl));
      expect(response.status()).toBe(200);
      return ms;
    };
    for (let i = 0; i < WARMUPS; i++) await detailOnce();
    const detail: Stats[] = [];
    for (let k = 0; k < SAMPLES; k++) {
      const one: number[] = [];
      for (let i = 0; i < LOADS; i++) one.push(await detailOnce());
      detail.push(stats(one));
    }

    // Each edit changes the name, so the capture has a real before/after to read, diff and store.
    const target: RecordRow = rows[1];
    const tag = Date.now().toString(36);
    const editOnce = async (i: number) => {
      const { ms, response } = await timed(() => updateRecord(admin, contactForm, target, { name: [`Load edit ${tag} ${i}`] }));
      expect(response.status()).toBe(200);
      return ms;
    };
    await editOnce(-1);
    const edit: Stats[] = [];
    for (let k = 0; k < SAMPLES; k++) {
      const one: number[] = [];
      for (let i = 0; i < EDITS; i++) one.push(await editOnce(k * EDITS + i));
      edit.push(stats(one));
    }

    const pass: Pass = { mode: mode!, provider: provider(), list, detail, edit };
    fs.writeFileSync(passFile(pass.provider, pass.mode), JSON.stringify(pass, null, 2));
    console.log(`LOAD ${pass.provider} ${pass.mode}\n${(['list', 'detail', 'edit'] as const).map((k) => pass[k].map((st, i) => `  ${k} sample ${i + 1}: ${fmt(st)}`).join('\n')).join('\n')}`);
  });

  test('compare the on and off passes', async () => {
    const on = readPass(provider(), 'on');
    const off = readPass(provider(), 'off');
    test.skip(!on || !off, `Both passes are needed for ${provider()}: run the on pass and the off pass first (see the header of this file).`);

    const lines: string[] = [];
    const over: string[] = [];
    for (const key of ['list', 'detail', 'edit'] as const) {
      const med = (xs: Stats[]) => stats(xs.map((x) => x.medianMs)).medianMs;
      const diff = med(on![key]) - med(off![key]);
      lines.push(`  ${key}: sample medians on [${on![key].map((x) => x.medianMs.toFixed(1)).join(', ')}] off [${off![key].map((x) => x.medianMs.toFixed(1)).join(', ')}] ms; median of medians on ${med(on![key]).toFixed(1)}, off ${med(off![key]).toFixed(1)}, difference ${diff.toFixed(1)} ms`);
      if (diff >= THRESHOLD_MS) over.push(`${key} ${diff.toFixed(1)} ms`);
    }
    console.log(`COMPARE ${provider()}\n${lines.join('\n')}`);
    expect(over, `median overhead of ${THRESHOLD_MS} ms or more triggers the Task 12 Step 6 fallback: stop and ask Pascal`).toEqual([]);
  });
});

test.describe('large volumes', () => {
  test.skip(!volume, 'Set E2E_LOAD_VOLUME=1 (and run the site with the audit on) to run the 5,000 entry delete and the 100,000 row export.');

  test('bulk delete of 5,000 entries', async ({ admin, contactForm }) => {
    test.setTimeout(1_800_000);
    const total = Number(process.env.E2E_BULK_TOTAL ?? 5_000);
    const tag = Date.now().toString(36);

    const ids: string[] = new Array(total);
    const createStart = performance.now();
    await pool(ids, writeConcurrency(), async (_, i) => {
      ids[i] = await createRecord(admin, contactForm, { name: [`Bulk delete ${tag} ${i}`], email: [`bulk.delete.${i}@example.test`] });
      if (i % 500 === 0) console.log(`BULK progress ${i} at ${((performance.now() - createStart) / 1000).toFixed(0)} s`);
    });
    console.log(`BULK created ${total} entries in ${((performance.now() - createStart) / 1000).toFixed(1)} s`);

    const actions = await admin.json<{ id: string; name: string }[]>(await admin.get(`${FORMS}/record-set-actions`));
    const deleteAction = actions.find((a) => /delete/i.test(a.name))!;
    const before = await lastAccessId(admin);

    // Forms' own RecordStorage.GetRecords sends one parameter per key, so one request of more than about 2,000 keys fails
    // on SQL Server (2,100 parameter limit) with a 500 before the package runs. E2E_BULK_CHUNK splits the delete into
    // requests of that size; unset, the whole set goes in one request, as the SQLite run does.
    const chunk = Number(process.env.E2E_BULK_CHUNK ?? 0) || total;
    const start = performance.now();
    for (let from = 0; from < total; from += chunk) {
      const response = await noTimeout(admin).post(`${FORMS}/form/${contactForm.id}/record/actions/${deleteAction.id}/execute`, { recordKeys: ids.slice(from, from + chunk) });
      console.log(`BULK delete of ${Math.min(chunk, total - from)} entries (from ${from}): HTTP ${response.status()} at ${((performance.now() - start) / 1000).toFixed(1)} s (${provider()})`);
      expect(response.status()).toBe(200);
    }
    console.log(`BULK delete of ${total} entries took ${((performance.now() - start) / 1000).toFixed(1)} s in ${Math.ceil(total / chunk)} request(s) (${provider()})`);

    // The delete is audited as one bulk row per request, with each entry findable.
    const deleted = await rowsSince(admin, before, 'Deleted', contactForm.id);
    expect(deleted).toHaveLength(Math.ceil(total / chunk));
    expect(deleted.reduce((sum, row) => sum + row.recordCount, 0)).toBe(total);
    for (const id of [ids[0], ids[total >> 1], ids[total - 1]]) {
      const history = await admin.json<{ id: number }[]>(await admin.get(`${AUDIT}/access/records/${id}`));
      expect(history.map((h) => h.id)).toEqual(expect.arrayContaining([expect.any(Number)]));
      expect(history.some((h) => deleted.some((d) => d.id === h.id))).toBe(true);
    }
  });

  test('access-log CSV export of 100,000 rows streams in flat memory', async ({ admin, contactForm }) => {
    test.setTimeout(900_000);
    const rows = 100_000;

    const seed = await admin.post('/e2e/load/seed-access', undefined, { count: String(rows), formId: contactForm.id });
    expect(seed.status(), 'seed-access').toBe(200);
    const seeded = await admin.json<{ inserted: number; milliseconds: number }>(seed);
    console.log(`EXPORT seeded ${seeded.inserted} access rows in ${(seeded.milliseconds / 1000).toFixed(1)} s`);

    try {
      type Sample = { workingSetBytes: number; managedHeapBytes: number };
      const sample = async (gc = false) => admin.json<Sample>(await admin.get('/e2e/load/memory', gc ? { gc: true } : undefined));
      const baseline = await sample(true);

      const samples: Sample[] = [];
      let stop = false;
      const sampler = (async () => {
        while (!stop) {
          samples.push(await sample());
          await new Promise((r) => setTimeout(r, 150));
        }
      })();

      const start = performance.now();
      const response = await noTimeout(admin).get(`${AUDIT}/access/export`);
      const body = await response.body();
      const seconds = (performance.now() - start) / 1000;
      stop = true;
      await sampler;
      const after = await sample(true);

      expect(response.status()).toBe(200);
      const lines = body.toString('utf8').split('\r\n').filter((l) => l.length > 0);
      // Header, the capped number of rows, and the truncation comment (the log holds more than the cap).
      expect(lines.length, 'header + 100,000 rows + truncation comment').toBe(rows + 2);
      expect(lines[lines.length - 1]).toMatch(/^# truncated at 100000 rows/);

      const mb = (n: number) => n / 1024 / 1024;
      const peakHeap = Math.max(...samples.map((s) => s.managedHeapBytes));
      const peakWs = Math.max(...samples.map((s) => s.workingSetBytes));
      console.log(
        [
          `EXPORT ${rows} rows, ${mb(body.length).toFixed(1)} MB, ${seconds.toFixed(1)} s (${provider()}), ${samples.length} memory samples`,
          `  managed heap: baseline ${mb(baseline.managedHeapBytes).toFixed(1)} MB, peak ${mb(peakHeap).toFixed(1)} MB, after GC ${mb(after.managedHeapBytes).toFixed(1)} MB`,
          `  working set : baseline ${mb(baseline.workingSetBytes).toFixed(1)} MB, peak ${mb(peakWs).toFixed(1)} MB, after GC ${mb(after.workingSetBytes).toFixed(1)} MB`,
        ].join('\n'),
      );

      // A generous bound: the file is tens of megabytes, so a buffered build would push the heap up by at least that and
      // keep it there, while a stream moves in 1,000 row steps. This is a smoke test of "flat", not a profile.
      expect(mb(peakHeap - baseline.managedHeapBytes), 'managed heap growth during the export, MB').toBeLessThan(100);
      expect(mb(after.managedHeapBytes - baseline.managedHeapBytes), 'managed heap retained after the export, MB').toBeLessThan(25);
    } finally {
      await noTimeout(admin).delete('/e2e/load/seed-access');
    }
  });
});
