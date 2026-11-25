import { afterEach, describe, expect, it, vi } from 'vitest';
import { downloadBlob, getRecordHistory } from '../services/audit-api.service.js';
import type { AccessEntryListDto } from '../services/audit-api.service.js';
import eventTypes from './access-event-types.json';
import {
  ACCESS_EVENT_OPTIONS,
  accessDetailLines,
  detailLines,
  exportBlocked,
  filterIssues,
  normaliseHistory,
  subjectOf,
  truncatedByHeader,
} from './access-detail.js';

const row = (detail: string | null) => ({ detail }) as AccessEntryListDto;

describe('detailLines', () => {
  it('is empty for no detail or bad JSON', () => {
    expect(detailLines(row(null))).toEqual([]);
    expect(detailLines(row('{not json'))).toEqual([]);
    expect(detailLines(row('[1]'))).toEqual([]);
  });
  it('summarises an export', () => {
    const d = JSON.stringify({ v: 1, exportType: 'Csv', fileName: 'a.csv' });
    expect(detailLines(row(d))).toEqual(['Export type: Csv', 'File name: a.csv']);
  });
  it('summarises a shaped filter and paging as text, never markup', () => {
    const d = JSON.stringify({ filter: { state: '<b>x</b>', searchTermUsed: true }, page: 2 });
    expect(detailLines(row(d))).toEqual(['Filter: state: <b>x</b>, search term used', 'Page: 2']);
  });
  it('notes truncation', () => {
    expect(detailLines(row(JSON.stringify({ truncated: true })))).toEqual(['Detail was truncated']);
  });
});

// The real server shape: camelCase keys, numeric eventType (EntryAuditDetail serialised by the API).
const KEY = '8e1a1c1e-0000-4000-8000-000000000001';
const searched = (extra: object = {}) => row(JSON.stringify({ subjectUserKey: KEY, eventType: 19, ...extra }));

describe('subjectOf (R49)', () => {
  it('maps the numeric event type to its label', () => {
    expect(subjectOf(searched())).toEqual({ userKey: KEY, eventType: 'AuditSearched' });
  });
  it('falls back to the number for an unknown event type', () => {
    expect(subjectOf(row(JSON.stringify({ eventType: 99 })))).toEqual({ userKey: null, eventType: '99' });
  });
  it('is null when absent', () => {
    expect(subjectOf(row('{}'))).toBeNull();
    expect(subjectOf(row(null))).toBeNull();
  });
});

describe('accessDetailLines', () => {
  const users = [{ userKey: KEY.toUpperCase(), userName: 'Old Name', currentUserName: 'Ada' }] as never[];
  it.each(['AuditSearched', 'AuditExported'])('shows the subject for %s rows with the event label', (eventType) => {
    const r = { ...searched(), eventType } as AccessEntryListDto;
    expect(accessDetailLines(r, users)).toEqual(['Filtered by user: Ada', 'Filtered by event type: AuditSearched']);
  });
  it('does not show a subject for other rows', () => {
    const r = { ...searched(), eventType: 'Exported' } as AccessEntryListDto;
    expect(accessDetailLines(r, users)).toEqual([]);
  });
  it('shows the raw key when the user is unknown', () => {
    const r = { ...searched(), eventType: 'AuditSearched' } as AccessEntryListDto;
    expect(accessDetailLines(r, [])[0]).toBe(`Filtered by user: ${KEY}`);
  });
});

describe('normaliseHistory', () => {
  it('returns the plain list with the header flag', () => {
    expect(normaliseHistory([row(null)])).toEqual({ items: [row(null)], truncated: false });
  });
});

describe('ACCESS_EVENT_OPTIONS', () => {
  // The server's AccessEventType is mirrored in access-event-types.json, which a .NET test also pins.
  it('carries every value in the shared JSON list, in order', () => {
    expect(ACCESS_EVENT_OPTIONS.map((o) => o.value)).toEqual(eventTypes.map((e) => e.value));
    expect(ACCESS_EVENT_OPTIONS.map((o) => o.label)).toEqual(eventTypes.map((e) => e.name));
  });
});

describe('filterIssues and exportBlocked', () => {
  const ok = { recordId: '', fromDate: '', toDate: '' };
  it('is clean for valid or empty filters', () => {
    expect(exportBlocked(filterIssues(ok))).toBe(false);
    expect(exportBlocked(filterIssues({ ...ok, recordId: '3f2504e0-4f89-11d3-9a0c-0305e82c3301' }))).toBe(false);
  });
  it('blocks export for an invalid record id', () => {
    const i = filterIssues({ ...ok, recordId: 'nope' });
    expect(i.recordId).not.toBeNull();
    expect(exportBlocked(i)).toBe(true);
  });
  it('flags From later than To', () => {
    const i = filterIssues({ ...ok, fromDate: '2026-11-10', toDate: '2026-11-01' });
    expect(i.range).toBe('The From date must not be later than the To date.');
    expect(exportBlocked(i)).toBe(true);
  });
  it('allows equal dates', () => {
    expect(filterIssues({ ...ok, fromDate: '2026-11-01', toDate: '2026-11-01' }).range).toBeNull();
  });
});

describe('truncation header', () => {
  it.each(['history', 'fileViews', 'history,fileViews', 'history, fileViews'])('reads %s as truncated', (v) => {
    expect(truncatedByHeader(v)).toBe(true);
    expect(normaliseHistory([row(null)], v).truncated).toBe(true);
  });
  it.each([null, undefined, '', ' '])('reads %j as not truncated', (v) => {
    expect(truncatedByHeader(v)).toBe(false);
    expect(normaliseHistory([row(null)], v).truncated).toBe(false);
  });
});

describe('getRecordHistory truncation header', () => {
  afterEach(() => vi.unstubAllGlobals());
  const id = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
  it('returns the header beside the plain list', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: async () => [],
      headers: new Headers({ 'X-FormsAuditTrail-Truncated': 'history,fileViews' }),
    }));
    const r = await getRecordHistory(id);
    expect(r.truncatedHeader).toBe('history,fileViews');
    expect(normaliseHistory(r.items, r.truncatedHeader).truncated).toBe(true);
  });
  it('has a null header when the server sent none', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, json: async () => [], headers: new Headers() }));
    expect((await getRecordHistory(id)).truncatedHeader).toBeNull();
  });
});

describe('downloadBlob', () => {
  afterEach(() => vi.unstubAllGlobals());
  it('appends the anchor, clicks it, removes it and revokes after a tick', async () => {
    vi.useFakeTimers();
    const click = vi.fn();
    const anchor = { click, remove: vi.fn(), href: '', download: '' };
    const append = vi.fn();
    const revoke = vi.fn();
    vi.stubGlobal('document', { createElement: () => anchor, body: { appendChild: append } });
    vi.stubGlobal('URL', { createObjectURL: () => 'blob:x', revokeObjectURL: revoke });
    downloadBlob(new Blob(['a']), 'f.csv');
    expect(append).toHaveBeenCalledWith(anchor);
    expect(click).toHaveBeenCalledOnce();
    expect(anchor.remove).toHaveBeenCalledOnce();
    expect(anchor.download).toBe('f.csv');
    expect(revoke).not.toHaveBeenCalled();
    vi.runAllTimers();
    expect(revoke).toHaveBeenCalledWith('blob:x');
    vi.useRealTimers();
  });
});
