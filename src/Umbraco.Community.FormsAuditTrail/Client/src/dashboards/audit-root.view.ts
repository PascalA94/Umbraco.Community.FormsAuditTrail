import type { AuditConfigDto } from '../services/audit-api.service.js';
import { foreignHandlersText, healthBanner, offPeriodText, parseRecordId } from '../utils/access-format.js';

export type AuditTab = 'formChanges' | 'entryAccess' | 'entryHistory';

export interface AuditRootView {
  tabs: AuditTab[];
  initialTab: AuditTab;
  offMessage: string | null;
  bannerLines: string[];
  showGdprNotice: boolean;
  deepLinkRecordId: string | null;
}

type NoticeStorage = Pick<Storage, 'getItem' | 'setItem'>;

export const OFF_MESSAGE = 'Entry auditing is switched off. Set FormsAuditTrail:EntryAudit:Enabled to true.';

const noticeKey = (userKey: string) => `formsAuditTrail.gdprNotice.${userKey}`;

function storeEditValuesIsNone(cfg: AuditConfigDto): boolean {
  const v = cfg.storeEditValues;
  if (v === undefined || v === null) return true; // not on the DTO yet: the notice does not apply
  return v === 0 || (typeof v === 'string' && v.toLowerCase() === 'none');
}

/**
 * Only meaningful while edit values are stored. "Not checked" (detector has not run) is shown as such and
 * never as "none found".
 */
export function foreignNotice(cfg: AuditConfigDto): string | null {
  if (storeEditValuesIsNone(cfg)) return null;
  if (cfg.foreignValueHandlers === true) {
    return 'Another package processes entry values. Edit values are stored unencrypted in the audit tables; consider StoreEditValues=None.';
  }
  if (cfg.foreignValueHandlers === null || cfg.foreignValueHandlers === undefined) {
    return `Whether another package processes entry values: ${foreignHandlersText(cfg)}.`;
  }
  return null;
}

function noticeDismissed(storage: NoticeStorage | null, userKey: string): boolean {
  if (!storage) return false;
  try {
    return storage.getItem(noticeKey(userKey)) !== null;
  } catch {
    return false; // storage blocked: showing the notice again is the safe failure
  }
}

export function dismissGdprNotice(storage: NoticeStorage | null, userKey: string): void {
  // An empty key would dismiss the notice for every user whose key had not resolved yet.
  if (userKey === '') return;
  try {
    storage?.setItem(noticeKey(userKey), '1');
  } catch {
    // Storage blocked or full: the notice simply returns next visit.
  }
}

export function auditRootView(
  cfg: AuditConfigDto,
  url: URL,
  storage: NoticeStorage | null,
  userKey: string,
): AuditRootView {
  const reader = cfg.canReadAccessLog === true;
  const tabs: AuditTab[] = reader ? ['formChanges', 'entryAccess', 'entryHistory'] : ['formChanges'];
  // SEC-I2: only a GUID gets through, and only where the tab that uses it exists.
  const deepLinkRecordId = reader ? parseRecordId(url.searchParams.get('recordId')) : null;

  const bannerLines: string[] = [];
  if (reader) {
    const health = healthBanner(cfg);
    if (health) bannerLines.push(health);
    for (const p of cfg.offPeriods ?? []) bannerLines.push(offPeriodText(p));
    const foreign = foreignNotice(cfg);
    if (foreign) bannerLines.push(foreign);
  }

  return {
    tabs,
    initialTab: deepLinkRecordId ? 'entryHistory' : 'formChanges',
    offMessage: reader && cfg.entryAuditEnabled === false ? OFF_MESSAGE : null,
    bannerLines,
    // Not shown until the current user key has resolved: the dismissal is stored per user.
    showGdprNotice: reader && cfg.entryAuditEnabled === true && userKey !== '' && !noticeDismissed(storage, userKey),
    deepLinkRecordId,
  };
}

/** Event raised by the Entry access cell; the root listens for it to switch to Entry history. */
export function openHistoryEvent(recordId: string): CustomEvent<{ recordId: string }> {
  return new CustomEvent('open-record-history', { detail: { recordId }, bubbles: true, composed: true });
}

/** The record id carried by an `open-record-history` event: a GUID or nothing (SEC-I2). */
export function openHistoryTarget(detail: unknown): string | null {
  const id = (detail as { recordId?: unknown } | null | undefined)?.recordId;
  return typeof id === 'string' ? parseRecordId(id) : null;
}

export interface FocusChild {
  updateComplete?: Promise<unknown>;
  /** Returns true when the heading existed and took focus. */
  focusHeading?: () => boolean;
}

/**
 * Focus after a code-triggered change. The new child may not have rendered yet, so wait for the host and then the
 * child before looking for its heading; fall back to the panel (or tab) when the heading is still absent.
 */
export async function focusWhenRendered(
  host: { updateComplete: Promise<unknown> },
  getChild: () => FocusChild | null | undefined,
  getFallback: () => { focus(): void } | null | undefined,
): Promise<void> {
  await host.updateComplete;
  const child = getChild();
  await child?.updateComplete;
  if (child?.focusHeading?.() === true) return;
  getFallback()?.focus();
}
