import { accessDetail, lastAccessId, rowsSince, scan } from './support/audit';
import { test, expect } from './support/fixtures';
import { listRecords, updateRecord } from './support/forms';

// P3: the demo site's stand-in stores values reversed and "decrypts" them on read, as an at-rest encryption package
// would. The audit must diff what Forms' read path returns (plaintext) and never store the stored (reversed) form.
// Needs E2E__SimulateEncryption=true; without it the encrypted form is not seeded.
test.describe.serial('values held encrypted at rest', () => {
  let before = 0;
  let editedId: number | undefined;

  test.beforeAll(async ({ admin, encryptedForm }) => {

    // Restore the seeded entry, stored reversed, so the spec can be run again on the same site.
    expect((await admin.post('/e2e/reseed-encrypted')).status()).toBe(204);
    const [row] = await listRecords(admin, encryptedForm);
    before = await lastAccessId(admin);

    // The stand-in decrypts on Forms' record read only, not on the backoffice list, so the list shows the stored
    // text. A real encryption package would decrypt both; an editor then sends the plaintext, so this edit does too.
    const response = await updateRecord(admin, encryptedForm, row, { name: ['Jane Doe'], email: ['jane.smith@example.test'] });
    expect(response.status()).toBe(200);
  });

  test('stores neither the ciphertext nor the plaintext Email', async ({ admin, encryptedForm }) => {
    // Positive control tied to this run's edit: an Edited row for the encrypted form written since the edit, plus the
    // new non-sensitive Name stored in clear. "Jane Doe" alone is not enough, as earlier runs on the same site leave it.
    expect(await rowsSince(admin, before, 'Edited', encryptedForm.id), 'this run\'s edit was captured').toHaveLength(1);
    expect((await scan(admin, 'Jane Doe')).length, 'the new Name was stored in clear').toBeGreaterThan(0);
    expect(await scan(admin, 'htimS')).toEqual([]);
    expect(await scan(admin, 'moc.elpmaxe')).toEqual([]);
    expect(await scan(admin, 'jane.smith@example.test')).toEqual([]);
  });

  test('records exactly one Edited change, Name from Jane Smith to Jane Doe, and none for the unchanged Email', async ({ admin, encryptedForm }) => {
    const written = await rowsSince(admin, before, 'Edited', encryptedForm.id);
    expect(written).toHaveLength(1);
    editedId = written[0].id;
    const { changes } = await accessDetail(admin, editedId);
    expect(changes).toHaveLength(1);
    expect(changes[0]).toMatchObject({ fieldCaption: 'Name', oldValue: 'Jane Smith', newValue: 'Jane Doe' });
  });
});
