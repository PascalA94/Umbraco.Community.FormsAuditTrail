import { parseRecordId } from '../utils/access-format.js';

export interface AuditEntryListDto {
  id: number;
  formId: string;
  formName: string;
  userName: string;
  userKey: string;
  timestamp: string;
  eventType: string;
  changeSummary: string | null;
}

export interface AuditEntryDetailDto extends AuditEntryListDto {
  beforeSnapshot: string;
  afterSnapshot: string | null;
  changes: AuditChangeDto[];
}

export interface AuditChangeDto {
  changeType: string;
  propertyPath: string;
  friendlyDescription: string;
  oldValue: string | null;
  newValue: string | null;
  category: string | null;
}

export interface FormSummaryDto {
  formId: string;
  formName: string;
}

export interface UserSummaryDto {
  userKey: string;
  userName: string;
}

export interface PagedResultDto<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface OffPeriodDto {
  fromUtc: string;
  toUtc: string | null;
  // True when the period was already open where the retained log begins: its real start is older than fromUtc.
  startUnknown?: boolean;
}

export interface AuditConfigDto {
  csvExportEnabled: boolean;
  entryAuditEnabled: boolean;
  canReadAccessLog: boolean;
  accessRetentionDays: number;
  editValueRetentionDays: number;
  entryAuditCsvEnabled: boolean;
  // The members below are absent (null) unless canReadAccessLog: health detail is for the people who read the log.
  entryAuditHealthy?: boolean | null;
  healthReasons?: string[] | null;
  lastWriteFailureUtc?: string | null;
  writeFailuresSinceStart?: number | null;
  lastJobRunUtc?: string | null;
  // null means "not checked", which is different from false ("none found"); see foreignHandlersText.
  foreignValueHandlers?: boolean | null;
  offPeriods?: OffPeriodDto[] | null;
  // The server sends the enum name and only to access-log readers; absent reads as "notice not applicable".
  // A number is still accepted so an older server cannot turn the notice on by mistake.
  storeEditValues?: string | number | null;
}

export interface AccessEntryListDto {
  id: number;
  timestamp: string;
  eventType: string;
  formId: string;
  formName: string;
  recordId: string | null;
  recordCount: number;
  recordIdsCaptured: boolean | null;
  userKey: string;
  userName: string;
  currentUserName: string | null;
  detail: string | null;
  correlationId: string | null;
}

export interface EntryChangeDto {
  fieldId: string;
  fieldCaption: string;
  oldValue: string | null;
  newValue: string | null;
  masked: boolean;
  cleared: boolean;
  valuesScrubbedAt: string | null;
}

export interface AccessEntryDetailDto extends AccessEntryListDto {
  changes: EntryChangeDto[];
  linkedAccessId: number | null;
  childRecordCount: number;
}

export interface AccessUserSummaryDto {
  userKey: string;
  userName: string;
  currentUserName: string | null;
}

export interface AccessFilterParams {
  formId?: string;
  userKey?: string;
  eventType?: number;
  fromDate?: string;
  toDate?: string;
  recordId?: string;
}

const BASE = '/umbraco/formsaudit/api/v1';

async function get<T>(url: string, token?: string): Promise<T> {
  const headers: Record<string, string> = {};
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(url, { credentials: 'include', headers });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json() as Promise<T>;
}

export async function getConfig(token?: string): Promise<AuditConfigDto> {
  return get(`${BASE}/config`, token);
}

export async function getEntries(params: {
  formId?: string;
  page?: number;
  pageSize?: number;
  userKey?: string;
  eventType?: number;
  fromDate?: string;
  toDate?: string;
}, token?: string): Promise<PagedResultDto<AuditEntryListDto>> {
  const q = new URLSearchParams();
  if (params.formId) q.set('formId', params.formId);
  if (params.page) q.set('page', String(params.page));
  if (params.pageSize) q.set('pageSize', String(params.pageSize));
  if (params.userKey) q.set('userKey', params.userKey);
  if (params.eventType !== undefined) q.set('eventType', String(params.eventType));
  if (params.fromDate) q.set('fromDate', params.fromDate);
  if (params.toDate) q.set('toDate', params.toDate);
  return get(`${BASE}/entries?${q}`, token);
}

export async function getEntry(id: number, token?: string): Promise<AuditEntryDetailDto> {
  return get(`${BASE}/entries/${id}`, token);
}

export async function getForms(token?: string): Promise<FormSummaryDto[]> {
  return get(`${BASE}/forms`, token);
}

export async function getUsers(token?: string): Promise<UserSummaryDto[]> {
  return get(`${BASE}/users`, token);
}

/** Saves a blob through a temporary anchor. Shared by both CSV downloads so they behave the same in every browser. */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  // Firefox ignores a click on an anchor that is not in the document.
  document.body.appendChild(a);
  a.click();
  a.remove();
  // Revoking in the same tick can cancel the download in some browsers.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

export async function exportCsv(params: {
  formId?: string;
  userKey?: string;
  eventType?: number;
  fromDate?: string;
  toDate?: string;
}, token?: string): Promise<void> {
  const q = new URLSearchParams();
  if (params.formId) q.set('formId', params.formId);
  if (params.userKey) q.set('userKey', params.userKey);
  if (params.eventType !== undefined) q.set('eventType', String(params.eventType));
  if (params.fromDate) q.set('fromDate', params.fromDate);
  if (params.toDate) q.set('toDate', params.toDate);
  const headers: Record<string, string> = {};
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(`${BASE}/entries/export?${q}`, { credentials: 'include', headers });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  downloadBlob(await res.blob(), `forms-audit-${new Date().toISOString().slice(0, 10)}.csv`);
}

export function accessQuery(params: AccessFilterParams): URLSearchParams {
  const q = new URLSearchParams();
  if (params.formId) q.set('formId', params.formId);
  if (params.userKey) q.set('userKey', params.userKey);
  if (params.eventType !== undefined) q.set('eventType', String(params.eventType));
  if (params.fromDate) q.set('fromDate', params.fromDate);
  if (params.toDate) q.set('toDate', params.toDate);
  if (params.recordId) q.set('recordId', params.recordId);
  return q;
}

export async function getAccess(
  params: AccessFilterParams & { page?: number; pageSize?: number },
  token?: string,
): Promise<PagedResultDto<AccessEntryListDto>> {
  const q = accessQuery(params);
  if (params.page) q.set('page', String(params.page));
  if (params.pageSize) q.set('pageSize', String(params.pageSize));
  return get(`${BASE}/access?${q}`, token);
}

export async function getAccessEntry(id: number, token?: string): Promise<AccessEntryDetailDto> {
  return get(`${BASE}/access/${encodeURIComponent(String(id))}`, token);
}

export interface RecordHistoryResponse {
  items: AccessEntryListDto[];
  /** Value of the X-FormsAuditTrail-Truncated header ("history", "fileViews" or both), null when absent. */
  truncatedHeader: string | null;
}

// Truncation is signalled by a response header, so the body stays a plain list (the contract with the server).
export async function getRecordHistory(recordId: string, token?: string): Promise<RecordHistoryResponse> {
  // Rejected before any request: the id often arrives from a deep link and must prove to be a GUID first (SEC-I2).
  const id = parseRecordId(recordId);
  if (id === null) throw new Error('Record id must be a GUID');
  const headers: Record<string, string> = {};
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(`${BASE}/access/records/${encodeURIComponent(id)}`, { credentials: 'include', headers });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return { items: await res.json(), truncatedHeader: res.headers?.get('X-FormsAuditTrail-Truncated') ?? null };
}

export async function getAccessForms(token?: string): Promise<FormSummaryDto[]> {
  return get(`${BASE}/access/forms`, token);
}

export async function getAccessUsers(token?: string): Promise<AccessUserSummaryDto[]> {
  return get(`${BASE}/access/users`, token);
}

/** URL of the access-log CSV for the current filters. Paging is not sent: the export covers every matching row. */
export function accessExportUrl(params: AccessFilterParams): string {
  const q = accessQuery(params).toString();
  return q ? `${BASE}/access/export?${q}` : `${BASE}/access/export`;
}

/** Downloads the access-log CSV for the current filters. Needs the bearer token, so it cannot be a plain link. */
export async function downloadAccessCsv(params: AccessFilterParams, token?: string): Promise<void> {
  const headers: Record<string, string> = {};
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(accessExportUrl(params), { credentials: 'include', headers });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  downloadBlob(await res.blob(), `forms-access-${new Date().toISOString().slice(0, 10)}.csv`);
}
