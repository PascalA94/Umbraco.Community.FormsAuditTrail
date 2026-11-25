import type {
  AccessEntryListDto,
  AuditConfigDto,
  OffPeriodDto,
} from '../services/audit-api.service.js';

export interface EventTag {
  label: string;
  color: 'default' | 'warning' | 'danger';
  look: 'primary' | 'outline';
}

const TAGS: Record<string, Pick<EventTag, 'color' | 'look'>> = {
  Edited: { color: 'warning', look: 'primary' },
  Created: { color: 'warning', look: 'primary' },
  ConfigChanged: { color: 'warning', look: 'primary' },
  Exported: { color: 'danger', look: 'primary' },
  ExportDownloaded: { color: 'danger', look: 'primary' },
  WorkflowRetried: { color: 'danger', look: 'primary' },
  AuditGap: { color: 'danger', look: 'primary' },
  Deleted: { color: 'danger', look: 'outline' },
};

/** Read-only events and anything this client does not know yet get the quiet tag, with the server's name as label. */
export function eventTag(eventType: string): EventTag {
  const known = Object.hasOwn(TAGS, eventType) ? TAGS[eventType] : undefined;
  return { label: eventType, ...(known ?? { color: 'default', look: 'outline' }) };
}

function entries(count: number): string {
  return `${count} ${count === 1 ? 'entry' : 'entries'}`;
}

export function entryCell(row: AccessEntryListDto): string {
  if (row.recordId) return row.recordId;
  const text = entries(row.recordCount);
  return row.recordIdsCaptured === false ? `${text} (ids not captured)` : text;
}

function days(count: number): string {
  return count === 0 ? 'kept indefinitely' : `kept for ${count} days`;
}

export function retentionBanner(cfg: AuditConfigDto): string {
  return `Entry access is ${days(cfg.accessRetentionDays)}. Changed values are ${days(cfg.editValueRetentionDays)}.`;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

// UTC and fixed English names, not Intl: the output must not depend on the browser's locale or time zone, or the
// banners would disagree with the server's evidence and with each other.
function utcDate(iso: string): string {
  const d = new Date(iso);
  // A malformed timestamp must not render as "NaN undefined NaN"; show what the server sent.
  if (Number.isNaN(d.getTime())) return iso;
  return `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
}

export function utcDateTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${utcDate(iso)} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
}

export function valueText(value: string | null, masked: boolean, scrubbedAt: string | null, cleared: boolean): string {
  if (scrubbedAt) return `Value removed on ${utcDate(scrubbedAt)} (retention or erasure)`;
  if (masked) return value ?? '[masked]';
  if (cleared) return 'Value cleared';
  return value ?? '';
}

export function userCell(row: AccessEntryListDto): string {
  return row.currentUserName ? `${row.userName} (now: ${row.currentUserName})` : row.userName;
}

function healthReason(reason: string, cfg: AuditConfigDto): string {
  switch (reason) {
    case 'writeFailures': {
      const n = cfg.writeFailuresSinceStart ?? 0;
      const since = cfg.lastWriteFailureUtc ? ` since ${utcDateTime(cfg.lastWriteFailureUtc)}` : '';
      return `${n} ${n === 1 ? 'write' : 'writes'} failed${since}`;
    }
    case 'migrationsPending':
      return 'database migrations are pending';
    case 'jobNotRun':
      return 'the daily retention and erasure job has not run in 48 hours';
    case 'jobStepFailed':
      return 'a step of the daily retention and erasure job failed';
    default:
      // A newer server may add a reason; still warn rather than hide the problem.
      return 'see the server log';
  }
}

/** Null when auditing is healthy, or when the user is not allowed to see health at all (the field is then absent). */
export function healthBanner(cfg: AuditConfigDto): string | null {
  if (cfg.entryAuditHealthy !== false) return null;
  const reasons = (cfg.healthReasons ?? []).map((r) => healthReason(r, cfg));
  const detail = reasons.length > 0 ? reasons.join('; ') : 'see the server log';
  return `Entry auditing has a problem: ${detail}. Some activity may not have been recorded.`;
}

export function offPeriodText(p: OffPeriodDto): string {
  const to = p.toUtc ? utcDateTime(p.toUtc) : 'now';
  // startUnknown: fromUtc is only where the retained log begins, so stating it as the start would shorten the gap.
  const from = p.startUnknown ? `some time before ${utcDateTime(p.fromUtc)}` : utcDateTime(p.fromUtc);
  return `Entry auditing was off from ${from} to ${to}`;
}

/** R42: null means the detector has not run, which must never read as "none found". */
export function foreignHandlersText(cfg: AuditConfigDto): string {
  if (cfg.foreignValueHandlers === null || cfg.foreignValueHandlers === undefined) return 'Not checked';
  return cfg.foreignValueHandlers ? 'Found' : 'None found';
}

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** SEC-I2: a deep-link value reaches a request and the DOM only after it has proved to be a GUID. */
export function parseRecordId(raw: string | null): string | null {
  return raw !== null && GUID.test(raw) ? raw.toLowerCase() : null;
}
