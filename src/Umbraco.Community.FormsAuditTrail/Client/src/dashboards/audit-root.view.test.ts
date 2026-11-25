import { describe, expect, it } from 'vitest';
import type { AuditConfigDto } from '../services/audit-api.service.js';
import { healthBanner, offPeriodText } from '../utils/access-format.js';
import { auditRootView, dismissGdprNotice, focusWhenRendered, foreignNotice, openHistoryTarget } from './audit-root.view.js';

const cfg = (p: Partial<AuditConfigDto>) => p as AuditConfigDto;
const url = (q = '') => new URL(`https://example.test/umbraco/section/forms/dashboard/forms-audit-trail${q}`);
const GUID = '3F2504E0-4F89-11D3-9A0C-0305E82C3301';

function memoryStorage() {
  const map = new Map<string, string>();
  return {
    getItem: (k: string) => map.get(k) ?? null,
    setItem: (k: string, v: string) => void map.set(k, v),
  };
}
const throwing = {
  getItem: () => { throw new Error('blocked'); },
  setItem: () => { throw new Error('blocked'); },
};

const reader = cfg({ canReadAccessLog: true, entryAuditEnabled: true });

describe('auditRootView tabs', () => {
  it('shows only Form changes without access-log permission', () => {
    const v = auditRootView(cfg({ canReadAccessLog: false, entryAuditEnabled: true }), url(), null, 'u');
    expect(v.tabs).toEqual(['formChanges']);
    expect(v.bannerLines).toEqual([]);
    expect(v.showGdprNotice).toBe(false);
  });
  it('shows all three tabs to an access-log user', () => {
    expect(auditRootView(reader, url(), null, 'u').tabs).toEqual(['formChanges', 'entryAccess', 'entryHistory']);
  });
  it('opens Entry history on a valid recordId deep link, lower-cased', () => {
    const v = auditRootView(reader, url(`?recordId=${GUID}`), null, 'u');
    expect(v.initialTab).toBe('entryHistory');
    expect(v.deepLinkRecordId).toBe(GUID.toLowerCase());
  });
  it('ignores an invalid recordId (SEC-I2)', () => {
    const v = auditRootView(reader, url('?recordId=<script>'), null, 'u');
    expect(v.deepLinkRecordId).toBeNull();
    expect(v.initialTab).toBe('formChanges');
  });
  it('ignores a deep link for a user without the Entry history tab', () => {
    const v = auditRootView(cfg({ canReadAccessLog: false }), url(`?recordId=${GUID}`), null, 'u');
    expect(v.deepLinkRecordId).toBeNull();
    expect(v.initialTab).toBe('formChanges');
  });
});

describe('auditRootView off state', () => {
  it('explains how to switch auditing on', () => {
    const v = auditRootView(cfg({ canReadAccessLog: true, entryAuditEnabled: false }), url(), null, 'u');
    expect(v.offMessage).toBe('Entry auditing is switched off. Set FormsAuditTrail:EntryAudit:Enabled to true.');
  });
  it('has no off message while auditing is on', () => {
    expect(auditRootView(reader, url(), null, 'u').offMessage).toBeNull();
  });
});

describe('auditRootView banner', () => {
  const p = { fromUtc: '2026-11-01T10:00:00Z', toUtc: '2026-11-02T10:00:00Z' };
  const full = cfg({
    canReadAccessLog: true,
    entryAuditEnabled: true,
    entryAuditHealthy: false,
    healthReasons: ['jobStepFailed'],
    offPeriods: [p],
    foreignValueHandlers: true,
    storeEditValues: 'MaskSensitive',
  });
  it('lists health, off periods and the foreign-handler notice in that order', () => {
    expect(auditRootView(full, url(), null, 'u').bannerLines).toEqual([
      healthBanner(full),
      offPeriodText(p),
      foreignNotice(full),
    ]);
  });
  it('hides the foreign-handler notice when storeEditValues is None (R50)', () => {
    const v = auditRootView({ ...full, storeEditValues: 'None' }, url(), null, 'u');
    expect(v.bannerLines).toHaveLength(2);
  });
  it('hides the foreign-handler notice when storeEditValues is absent', () => {
    const v = auditRootView({ ...full, storeEditValues: undefined }, url(), null, 'u');
    expect(v.bannerLines).toHaveLength(2);
  });
  it('treats a numeric 0 as None', () => {
    expect(foreignNotice({ ...full, storeEditValues: 0 })).toBeNull();
  });
  it('says "Not checked" when the detector has not run (R42)', () => {
    const line = foreignNotice({ ...full, foreignValueHandlers: null });
    expect(line).toContain('Not checked');
  });
  it('is silent when no foreign handler was found', () => {
    expect(foreignNotice({ ...full, foreignValueHandlers: false })).toBeNull();
  });
  it('is empty for a healthy site', () => {
    expect(auditRootView(reader, url(), null, 'u').bannerLines).toEqual([]);
  });
});

describe('GDPR notice', () => {
  it('shows with no storage', () => {
    expect(auditRootView(reader, url(), null, 'u').showGdprNotice).toBe(true);
  });
  it('shows, and does not throw, when reading storage throws', () => {
    expect(auditRootView(reader, url(), throwing, 'u').showGdprNotice).toBe(true);
  });
  it('does not throw when writing storage throws', () => {
    expect(() => dismissGdprNotice(throwing, 'u')).not.toThrow();
    expect(() => dismissGdprNotice(null, 'u')).not.toThrow();
  });
  it('stays dismissed per user', () => {
    const s = memoryStorage();
    dismissGdprNotice(s, 'u');
    expect(auditRootView(reader, url(), s, 'u').showGdprNotice).toBe(false);
    expect(auditRootView(reader, url(), s, 'v').showGdprNotice).toBe(true);
  });
  it('is not shown while auditing is off', () => {
    const v = auditRootView(cfg({ canReadAccessLog: true, entryAuditEnabled: false }), url(), null, 'u');
    expect(v.showGdprNotice).toBe(false);
  });
});

describe('GDPR notice and user key', () => {
  it('waits for the user key to resolve', () => {
    expect(auditRootView(reader, url(), null, '').showGdprNotice).toBe(false);
  });
  it('never writes a dismissal for an empty user key', () => {
    const calls: string[] = [];
    dismissGdprNotice({ getItem: () => null, setItem: (k: string) => void calls.push(k) }, '');
    expect(calls).toEqual([]);
  });
});

describe('openHistoryTarget (record link wiring)', () => {
  it('accepts a GUID and lower-cases it', () => {
    expect(openHistoryTarget({ recordId: GUID })).toBe(GUID.toLowerCase());
  });
  it.each([null, undefined, {}, { recordId: 5 }, { recordId: '<script>' }])('rejects %j', (d) => {
    expect(openHistoryTarget(d)).toBeNull();
  });
});

describe('focusWhenRendered', () => {
  const tick = () => new Promise((r) => setTimeout(r, 0));

  it('waits for the child to finish rendering before focusing its heading', async () => {
    const order: string[] = [];
    let rendered = false;
    const child = {
      updateComplete: tick().then(() => { rendered = true; order.push('child rendered'); }),
      focusHeading: () => { order.push(rendered ? 'heading focused' : 'heading missing'); return rendered; },
    };
    const fallback = { focus: () => order.push('fallback') };
    await focusWhenRendered({ updateComplete: Promise.resolve() }, () => child, () => fallback);
    expect(order).toEqual(['child rendered', 'heading focused']);
  });
  it('falls back to the panel when the heading is still absent', async () => {
    const order: string[] = [];
    await focusWhenRendered(
      { updateComplete: Promise.resolve() },
      () => ({ updateComplete: Promise.resolve(), focusHeading: () => false }),
      () => ({ focus: () => order.push('fallback') }),
    );
    expect(order).toEqual(['fallback']);
  });
  it('falls back when there is no child, or it has no focusHeading', async () => {
    const order: string[] = [];
    const fb = () => ({ focus: () => order.push('fb') });
    await focusWhenRendered({ updateComplete: Promise.resolve() }, () => null, fb);
    await focusWhenRendered({ updateComplete: Promise.resolve() }, () => ({}), fb);
    expect(order).toEqual(['fb', 'fb']);
  });
});
