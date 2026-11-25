import { createRecord } from './support/forms';
import { test, expect } from './support/fixtures';
import {
  blankRow,
  breakUfRecords,
  createAndEdit,
  deleteUfRecord,
  editChanges,
  healthConfig,
  pool,
  reconciliationScrubbed,
  restoreUfRecords,
  runRetentionJob,
} from './support/reconcile';
import { updateRecord } from './support/forms';
import { writeConcurrency } from './support/sqlserver';

/**
 * P5: erasure reconciliation against the real Forms `UFRecords` table, on whichever provider the site runs on.
 * An entry is "erased" by deleting its UFRecords row directly, which is what a deletion that publishes no notification
 * (a GDPR erasure by SQL, Forms' own retention) looks like to the package. The daily job must then scrub the values it
 * quoted while keeping who changed what, and must never scrub on a read it cannot trust.
 */
test.describe.configure({ mode: 'serial' });

const run = Date.now().toString(36);

test.describe('reconciliation on the real UFRecords table', () => {
  test.beforeAll(async ({ admin }) => {
    // Leftovers from other specs (entries deleted behind the package's back) must not count against the exact numbers below.
    reconciliationScrubbed(await runRetentionJob(admin));
  });

  test('scrubs the values of an erased entry, keeps an untouched entry, and reports ok:n', async ({ admin, contactForm }) => {
    const erased = await createAndEdit(admin, contactForm, `Erased ${run}`, `erased.${run}@example.test`);
    const kept = await createAndEdit(admin, contactForm, `Kept ${run}`, `kept.${run}@example.test`);

    // Positive control: before the erasure both entries' edits hold their values, so a "scrubbed" result below cannot
    // be a capture that never stored any.
    for (const id of [erased, kept]) {
      const before = await editChanges(admin, id);
      expect(before.length, 'an Edited row with a Name change exists').toBeGreaterThanOrEqual(1);
      expect(before.some((c) => c.fieldCaption === 'Name' && c.newValue !== null && c.valuesScrubbedAt === null)).toBe(true);
    }

    await deleteUfRecord(admin, contactForm, erased);
    const job = await runRetentionJob(admin);
    expect(reconciliationScrubbed(job)).toBeGreaterThanOrEqual(1);

    const scrubbed = await editChanges(admin, erased);
    expect(scrubbed.length).toBeGreaterThanOrEqual(1);
    for (const change of scrubbed) {
      expect(change.oldValue, 'old value removed').toBeNull();
      expect(change.newValue, 'new value removed').toBeNull();
      expect(change.valuesScrubbedAt, 'scrub time stamped').not.toBeNull();
    }

    const untouched = await editChanges(admin, kept);
    const name = untouched.find((c) => c.fieldCaption === 'Name');
    expect(name?.valuesScrubbedAt, 'the second entry is not scrubbed').toBeNull();
    expect(name?.newValue).toBe(`Kept ${run} edited`);
  });

  test('scrubs exactly the 600 erased entries of 1,200 across more than one reader batch', async ({ admin, contactForm }) => {
    test.setTimeout(900_000);
    const total = 1_200;
    const erasedCount = 600;

    // One create and one single-field edit each, so every entry owns exactly one change row and the job's count is
    // the number of entries it scrubbed. 1,200 change rows against a reader page of 500 is at least three batches.
    const ids: string[] = new Array(total);
    await pool(ids, writeConcurrency(), async (_, i) => {
      ids[i] = await createRecord(admin, contactForm, { name: [`Bulk ${run} ${i}`], email: [`bulk.${run}.${i}@example.test`] });
      const edit = await updateRecord(admin, contactForm, blankRow(ids[i]), { name: [`Bulk ${run} ${i} edited`], email: [`bulk.${run}.${i}@example.test`] });
      expect(edit.status(), `edit ${i}`).toBe(200);
    });

    const erased = ids.slice(0, erasedCount);
    const kept = ids.slice(erasedCount);
    await pool(erased, writeConcurrency(), (id) => deleteUfRecord(admin, contactForm, id));

    const job = await runRetentionJob(admin);
    expect(reconciliationScrubbed(job), 'exactly the erased entries were scrubbed').toBe(erasedCount);

    // Spot check both sides by id, spread across the range (every call is a request, so not all 1,200).
    for (const id of [erased[0], erased[199], erased[299], erased[599]]) {
      const changes = await editChanges(admin, id);
      expect(changes).toHaveLength(1);
      expect(changes[0].valuesScrubbedAt).not.toBeNull();
      expect(changes[0].newValue).toBeNull();
    }
    for (const id of [kept[0], kept[199], kept[299], kept[599]]) {
      const changes = await editChanges(admin, id);
      expect(changes).toHaveLength(1);
      expect(changes[0].valuesScrubbedAt).toBeNull();
      expect(changes[0].newValue).not.toBeNull();
    }

    // Nothing is left to scrub: a second run finds no more missing entries among the 600 that remain.
    expect(reconciliationScrubbed(await runRetentionJob(admin))).toBe(0);
  });

  test('scrubs nothing when UFRecords cannot be read, fails the step and shows jobStepFailed, then recovers', async ({ admin, contactForm }) => {
    const gone = await createAndEdit(admin, contactForm, `Gone ${run}`, `gone.${run}@example.test`);
    const live = await createAndEdit(admin, contactForm, `Live ${run}`, `live.${run}@example.test`);
    await deleteUfRecord(admin, contactForm, gone);

    let broken = false;
    try {
      broken = true;
      await breakUfRecords(admin);

      const job = await runRetentionJob(admin);
      expect(job.steps.reconciliation, 'reconciliation step').toMatch(/^failed:/);
      // The other steps are independent: one failing step never stops them.
      expect(job.steps.accessRetention).toMatch(/^(ok:\d+|skipped)$/);

      const config = await healthConfig(admin);
      expect(config.entryAuditHealthy).toBe(false);
      expect(config.healthReasons).toContain('jobStepFailed');
    } finally {
      if (broken) {
        await restoreUfRecords(admin);
      }
    }

    // Nothing was scrubbed during the failure, even though one of the two entries really is gone.
    for (const change of await editChanges(admin, gone)) {
      expect(change.valuesScrubbedAt, 'a failed read scrubs nothing').toBeNull();
      expect(change.newValue).not.toBeNull();
    }
    for (const change of await editChanges(admin, live)) {
      expect(change.valuesScrubbedAt).toBeNull();
    }

    // With the table back, the next run does the work and the health signal clears.
    const recovered = await runRetentionJob(admin);
    expect(reconciliationScrubbed(recovered)).toBeGreaterThanOrEqual(1);
    for (const change of await editChanges(admin, gone)) {
      expect(change.valuesScrubbedAt).not.toBeNull();
    }
    for (const change of await editChanges(admin, live)) {
      expect(change.valuesScrubbedAt).toBeNull();
    }
    expect((await healthConfig(admin)).healthReasons ?? []).not.toContain('jobStepFailed');
  });
});
