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

export interface AuditConfigDto {
  csvExportEnabled: boolean;
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
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `forms-audit-${new Date().toISOString().slice(0, 10)}.csv`;
  a.click();
  URL.revokeObjectURL(url);
}
