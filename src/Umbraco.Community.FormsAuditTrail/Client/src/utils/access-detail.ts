import type { AccessEntryListDto, AccessUserSummaryDto } from '../services/audit-api.service.js';
import eventTypes from './access-event-types.json';
import { parseRecordId } from './access-format.js';

type Json = Record<string, unknown>;

// Detail is server-shaped JSON (design 7.2) that arrives as a string. It is parsed defensively and every value ends up
// as a plain string for a Lit text binding: nothing here ever produces markup.
function parse(detail: string | null): Json | null {
  if (!detail) return null;
  try {
    const value: unknown = JSON.parse(detail);
    return value !== null && typeof value === 'object' && !Array.isArray(value) ? (value as Json) : null;
  } catch {
    return null;
  }
}

function pick(obj: Json, name: string): unknown {
  const key = Object.keys(obj).find((k) => k.toLowerCase() === name.toLowerCase());
  return key === undefined ? undefined : obj[key];
}

function scalar(value: unknown): string | null {
  return typeof value === 'string' || typeof value === 'number' ? String(value) : null;
}

function humanise(key: string): string {
  const spaced = key.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced;
}

function filterSummary(filter: unknown): string | null {
  if (filter === null || typeof filter !== 'object' || Array.isArray(filter)) return null;
  const parts: string[] = [];
  for (const [k, v] of Object.entries(filter as Json)) {
    if (v === true) parts.push(humanise(k));
    else if (scalar(v) !== null) parts.push(`${humanise(k)}: ${scalar(v)}`);
  }
  return parts.length > 0 ? parts.join(', ') : null;
}

/** One short text line per interesting Detail member: filter summary, export type, file name, paging. */
export function detailLines(row: AccessEntryListDto): string[] {
  const d = parse(row.detail);
  if (!d) return [];
  const lines: string[] = [];
  const filter = filterSummary(pick(d, 'filter'));
  if (filter) lines.push(`Filter: ${filter}`);
  const exportType = scalar(pick(d, 'exportType'));
  if (exportType) lines.push(`Export type: ${exportType}`);
  const fileName = scalar(pick(d, 'fileName'));
  if (fileName) lines.push(`File name: ${fileName}`);
  const page = scalar(pick(d, 'page'));
  if (page) lines.push(`Page: ${page}`);
  if (pick(d, 'truncated') === true) lines.push('Detail was truncated');
  return lines;
}

export interface Subject {
  userKey: string | null;
  eventType: string | null;
}

/**
 * R49: an AuditSearched or AuditExported row says whose activity, and which event type, was asked about. Null when it
 * says neither. The server stores the event type as its number, so it is shown as the label (the number if unknown).
 */
export function subjectOf(row: AccessEntryListDto): Subject | null {
  const d = parse(row.detail);
  if (!d) return null;
  const userKey = scalar(pick(d, 'subjectUserKey'));
  const raw = pick(d, 'eventType');
  const label = ACCESS_EVENT_OPTIONS.find((o) => o.value === raw)?.label;
  const eventType = label ?? scalar(raw);
  return userKey || eventType ? { userKey, eventType } : null;
}

export function userNameFor(users: AccessUserSummaryDto[], key: string): string {
  const u = users.find((x) => x.userKey.toLowerCase() === key.toLowerCase());
  return u ? (u.currentUserName ?? u.userName) : key;
}

/** Every text line for a row's Detail cell, shared by the access log and the entry history. */
export function accessDetailLines(row: AccessEntryListDto, users: AccessUserSummaryDto[]): string[] {
  const lines = detailLines(row);
  const subject = row.eventType === 'AuditSearched' || row.eventType === 'AuditExported' ? subjectOf(row) : null;
  if (subject?.userKey) lines.push(`Filtered by user: ${userNameFor(users, subject.userKey)}`);
  if (subject?.eventType) lines.push(`Filtered by event type: ${subject.eventType}`);
  return lines;
}

export interface RecordHistory {
  items: AccessEntryListDto[];
  truncated: boolean;
}

/** The header is the contract: any non-empty value ("history", "fileViews" or both) means rows were cut. */
export function truncatedByHeader(value: string | null | undefined): boolean {
  return typeof value === 'string' && value.split(',').some((v) => v.trim() !== '');
}

export function normaliseHistory(items: AccessEntryListDto[], truncatedHeader?: string | null): RecordHistory {
  return { items, truncated: truncatedByHeader(truncatedHeader) };
}

// Values mirror the server's AccessEventType (Models/AccessEventType.cs) and are never renumbered. They come from
// access-event-types.json, which a .NET test pins to the enum, so a new server event cannot silently go missing here.
// The label is the server's name, which eventTag() also uses, so the filter and the tag always read the same.

export const ACCESS_EVENT_OPTIONS: ReadonlyArray<{ value: number; label: string }> = eventTypes.map((e) => ({
  value: e.value,
  label: e.name,
}));

export const RECORD_ID_MESSAGE = 'Enter an entry id as a GUID, for example 3f2504e0-4f89-11d3-9a0c-0305e82c3301.';
export const RANGE_MESSAGE = 'The From date must not be later than the To date.';

export interface FilterIssues {
  recordId: string | null;
  range: string | null;
}

/** Problems that would make the list or the CSV differ from what the filters on screen say. */
export function filterIssues(f: { recordId: string; fromDate: string; toDate: string }): FilterIssues {
  return {
    recordId: f.recordId !== '' && parseRecordId(f.recordId) === null ? RECORD_ID_MESSAGE : null,
    // ISO yyyy-mm-dd strings sort chronologically.
    range: f.fromDate !== '' && f.toDate !== '' && f.fromDate > f.toDate ? RANGE_MESSAGE : null,
  };
}

/** The CSV must carry exactly the filters on screen, so it is blocked while any filter is invalid. */
export function exportBlocked(issues: FilterIssues): boolean {
  return issues.recordId !== null || issues.range !== null;
}
