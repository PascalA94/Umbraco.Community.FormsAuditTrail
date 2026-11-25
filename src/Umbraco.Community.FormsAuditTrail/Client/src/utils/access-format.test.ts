import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  accessExportUrl,
  accessQuery,
  getRecordHistory,
  type AccessEntryListDto,
  type AuditConfigDto,
} from '../services/audit-api.service.js';
import {
  entryCell,
  eventTag,
  foreignHandlersText,
  healthBanner,
  offPeriodText,
  parseRecordId,
  retentionBanner,
  userCell,
  valueText,
} from './access-format.js';

const row = (p: Partial<AccessEntryListDto>) => p as AccessEntryListDto;
const cfg = (p: Partial<AuditConfigDto>) => p as AuditConfigDto;

describe('eventTag', () => {
  it('marks edits as a warning', () => {
    expect(eventTag('Edited')).toEqual({ label: 'Edited', color: 'warning', look: 'primary' });
  });
  it.each(['Exported', 'ExportDownloaded', 'WorkflowRetried', 'AuditGap'])('marks %s as danger primary', (t) => {
    expect(eventTag(t)).toMatchObject({ color: 'danger', look: 'primary' });
  });
  it('marks deletes as danger outline', () => {
    expect(eventTag('Deleted')).toMatchObject({ color: 'danger', look: 'outline' });
  });
  it.each(['Created', 'ConfigChanged'])('marks %s as warning primary', (t) => {
    expect(eventTag(t)).toMatchObject({ color: 'warning', look: 'primary' });
  });
  it.each([
    'ListViewed', 'DetailViewed', 'AuditViewed', 'AuditExported', 'AuditSearched', 'FileViewed', 'MemberSummaryViewed',
  ])('marks %s as default', (t) => {
    expect(eventTag(t).color).toBe('default');
  });
  it('does not read inherited object keys as known types', () => {
    expect(eventTag('toString')).toEqual({ label: 'toString', color: 'default', look: 'outline' });
  });
  it('falls back to a default tag carrying the raw name for an unknown type', () => {
    expect(eventTag('Mystery')).toMatchObject({ label: 'Mystery', color: 'default' });
  });
});

describe('entryCell', () => {
  it('shows the record id', () => expect(entryCell(row({ recordId: 'g' }))).toBe('g'));
  it('shows a count', () => expect(entryCell(row({ recordCount: 12 }))).toBe('12 entries'));
  it('says when ids were not captured', () => {
    expect(entryCell(row({ eventType: 'Exported', recordCount: 12, recordIdsCaptured: false }))).toBe(
      '12 entries (ids not captured)',
    );
  });
  it('shows a bulk delete as a count', () => {
    expect(entryCell(row({ eventType: 'Deleted', recordId: null, recordCount: 3 }))).toBe('3 entries');
  });
  it('uses the singular for one entry', () => expect(entryCell(row({ recordCount: 1 }))).toBe('1 entry'));
});

describe('retentionBanner', () => {
  it('states both periods', () => {
    expect(retentionBanner(cfg({ accessRetentionDays: 365, editValueRetentionDays: 90 }))).toBe(
      'Entry access is kept for 365 days. Changed values are kept for 90 days.',
    );
  });
  it('says indefinitely for zero', () => {
    expect(retentionBanner(cfg({ accessRetentionDays: 0, editValueRetentionDays: 90 }))).toContain('kept indefinitely');
    expect(retentionBanner(cfg({ accessRetentionDays: 30, editValueRetentionDays: 0 }))).toContain(
      'Changed values are kept indefinitely',
    );
  });
});

describe('valueText', () => {
  it('explains a scrubbed value', () => {
    expect(valueText(null, false, '2026-11-01T00:00:00Z', false)).toBe(
      'Value removed on 1 Nov 2026 (retention or erasure)',
    );
  });
  it('shows a masked value as stored', () => expect(valueText('[masked]', true, null, false)).toBe('[masked]'));
  it('says cleared', () => expect(valueText(null, false, null, true)).toBe('Value cleared'));
  it('shows a plain value', () => expect(valueText('abc', false, null, false)).toBe('abc'));
  it('shows an empty string when there is no value and no flag', () => expect(valueText(null, false, null, false)).toBe(''));
});

describe('userCell', () => {
  it('shows the current name when it differs', () => {
    expect(userCell(row({ userName: 'Ann', currentUserName: 'Bob' }))).toBe('Ann (now: Bob)');
  });
  it('shows the stored name alone', () => expect(userCell(row({ userName: 'Ann' }))).toBe('Ann'));
});

describe('healthBanner', () => {
  const prefix = 'Entry auditing has a problem: ';
  const suffix = ' Some activity may not have been recorded.';
  it('is null when healthy', () => expect(healthBanner(cfg({ entryAuditHealthy: true }))).toBeNull());
  it('is null when health is not reported', () => expect(healthBanner(cfg({}))).toBeNull());
  it('joins reasons', () => {
    expect(
      healthBanner(
        cfg({
          entryAuditHealthy: false,
          healthReasons: ['writeFailures', 'migrationsPending'],
          writeFailuresSinceStart: 3,
          lastWriteFailureUtc: '2026-11-01T09:00:00Z',
        }),
      ),
    ).toBe(`${prefix}3 writes failed since 1 Nov 2026 09:00; database migrations are pending.${suffix}`);
  });
  it('explains jobNotRun', () => {
    expect(healthBanner(cfg({ entryAuditHealthy: false, healthReasons: ['jobNotRun'] }))).toBe(
      `${prefix}the daily retention and erasure job has not run in 48 hours.${suffix}`,
    );
  });
  it('explains jobStepFailed', () => {
    expect(healthBanner(cfg({ entryAuditHealthy: false, healthReasons: ['jobStepFailed'] }))).toBe(
      `${prefix}a step of the daily retention and erasure job failed.${suffix}`,
    );
  });
  it('uses the singular for one failed write', () => {
    expect(
      healthBanner(cfg({ entryAuditHealthy: false, healthReasons: ['writeFailures'], writeFailuresSinceStart: 1 })),
    ).toBe(`${prefix}1 write failed.${suffix}`);
  });
  it('still warns for an unknown reason', () => {
    expect(healthBanner(cfg({ entryAuditHealthy: false, healthReasons: ['somethingNew'] }))).toBe(
      `${prefix}see the server log.${suffix}`,
    );
  });
});

describe('offPeriodText', () => {
  it('formats a closed period', () => {
    expect(offPeriodText({ fromUtc: '2026-11-01T09:00:00Z', toUtc: '2026-11-01T10:00:00Z' })).toBe(
      'Entry auditing was off from 1 Nov 2026 09:00 to 1 Nov 2026 10:00',
    );
  });
  it('formats an open period', () => {
    expect(offPeriodText({ fromUtc: '2026-11-01T09:00:00Z', toUtc: null })).toBe(
      'Entry auditing was off from 1 Nov 2026 09:00 to now',
    );
  });
  it('does not present an unknown start as the real start (startUnknown)', () => {
    // The span was already open when the retained log begins, so the real start is older than fromUtc.
    expect(offPeriodText({ fromUtc: '2026-11-01T09:00:00Z', toUtc: '2026-11-02T10:00:00Z', startUnknown: true })).toBe(
      'Entry auditing was off from some time before 1 Nov 2026 09:00 to 2 Nov 2026 10:00',
    );
    expect(offPeriodText({ fromUtc: '2026-11-01T09:00:00Z', toUtc: null, startUnknown: true })).toBe(
      'Entry auditing was off from some time before 1 Nov 2026 09:00 to now',
    );
  });
  it('treats startUnknown false like a known start', () => {
    expect(offPeriodText({ fromUtc: '2026-11-01T09:00:00Z', toUtc: null, startUnknown: false })).toBe(
      'Entry auditing was off from 1 Nov 2026 09:00 to now',
    );
  });
});

describe('invalid dates', () => {
  it('shows the raw text instead of NaN', () => {
    expect(valueText(null, false, 'garbage', false)).toBe('Value removed on garbage (retention or erasure)');
    expect(offPeriodText({ fromUtc: 'nope', toUtc: null })).toBe('Entry auditing was off from nope to now');
  });
});

describe('accessQuery and accessExportUrl', () => {
  const base = '/umbraco/formsaudit/api/v1/access/export';
  it('has no query string for an empty filter', () => expect(accessExportUrl({})).toBe(base));
  it('still sends eventType 0', () => {
    expect(accessQuery({ eventType: 0 }).get('eventType')).toBe('0');
    expect(accessExportUrl({ eventType: 0 })).toBe(`${base}?eventType=0`);
  });
  it('omits empty strings', () => expect(accessExportUrl({ formId: '', userKey: '' })).toBe(base));
  it('encodes values', () => {
    expect(accessExportUrl({ fromDate: '2026-11-01T09:00:00+01:00' })).toBe(
      `${base}?fromDate=2026-11-01T09%3A00%3A00%2B01%3A00`,
    );
  });
});

describe('getRecordHistory', () => {
  afterEach(() => vi.unstubAllGlobals());
  it('rejects a non-guid before any request', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    await expect(getRecordHistory('../entries')).rejects.toThrow('GUID');
    expect(fetchMock).not.toHaveBeenCalled();
  });
  it('requests the lower-cased guid', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => [] });
    vi.stubGlobal('fetch', fetchMock);
    await getRecordHistory('3F2504E0-4F89-11D3-9A0C-0305E82C3301');
    expect(fetchMock.mock.calls[0]![0]).toBe(
      '/umbraco/formsaudit/api/v1/access/records/3f2504e0-4f89-11d3-9a0c-0305e82c3301',
    );
  });
});

describe('parseRecordId', () => {
  it('rejects non-guids', () => {
    expect(parseRecordId('not-a-guid')).toBeNull();
    expect(parseRecordId('<script>')).toBeNull();
    expect(parseRecordId(null)).toBeNull();
    expect(parseRecordId('')).toBeNull();
  });
  it('lower-cases a valid guid', () => {
    expect(parseRecordId('3F2504E0-4F89-11D3-9A0C-0305E82C3301')).toBe('3f2504e0-4f89-11d3-9a0c-0305e82c3301');
  });
  it('rejects a guid with trailing text', () => {
    expect(parseRecordId('3f2504e0-4f89-11d3-9a0c-0305e82c3301x')).toBeNull();
  });
});

describe('foreignHandlersText (R42)', () => {
  it('never reports null as none found', () => {
    expect(foreignHandlersText(cfg({ foreignValueHandlers: null }))).toBe('Not checked');
    expect(foreignHandlersText(cfg({}))).toBe('Not checked');
  });
  it('reports found and none found', () => {
    expect(foreignHandlersText(cfg({ foreignValueHandlers: true }))).toBe('Found');
    expect(foreignHandlersText(cfg({ foreignValueHandlers: false }))).toBe('None found');
  });
});
