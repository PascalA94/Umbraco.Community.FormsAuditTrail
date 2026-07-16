import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { css, html } from '@umbraco-cms/backoffice/external/lit';
import { customElement, state } from '@umbraco-cms/backoffice/external/lit';
import { UMB_AUTH_CONTEXT } from '@umbraco-cms/backoffice/auth';
import type { AuditEntryListDto, FormSummaryDto, UserSummaryDto } from '../services/audit-api.service.js';
import { getConfig, getEntries, getForms, getUsers, exportCsv } from '../services/audit-api.service.js';
import '../components/audit-diff-viewer.element.js';

const DATE_PRESETS = [
  { value: '', label: 'All time', days: undefined },
  { value: 'today', label: 'Today', days: 0 },
  { value: '7', label: 'Last 7 days', days: 7 },
  { value: '30', label: 'Last 30 days', days: 30 },
  { value: 'custom', label: 'Custom', days: undefined },
] as const;

function isoDateDaysAgo(days: number): string {
  const d = new Date();
  d.setDate(d.getDate() - days);
  return d.toISOString().slice(0, 10);
}

@customElement('forms-audit-dashboard')
export class AuditDashboardElement extends UmbLitElement {
  @state() private _entries: AuditEntryListDto[] = [];
  @state() private _forms: FormSummaryDto[] = [];
  @state() private _users: UserSummaryDto[] = [];
  @state() private _total = 0;
  @state() private _page = 1;
  @state() private _pageSize = 20;
  @state() private _loading = false;
  @state() private _selectedFormId = '';
  @state() private _selectedEventType = '';
  @state() private _fromDate = '';
  @state() private _toDate = '';
  @state() private _selectedEntryId?: number;
  @state() private _selectedUserKey = '';
  @state() private _datePreset = '';
  @state() private _csvExportEnabled = true;

  private _authContext?: typeof UMB_AUTH_CONTEXT.TYPE;

  override async connectedCallback() {
    super.connectedCallback();
    this.consumeContext(UMB_AUTH_CONTEXT, async (authContext) => {
      if (!authContext) return;
      this._authContext = authContext;
      const token = await this._getToken();
      const [forms, users, config] = await Promise.all([getForms(token), getUsers(token), getConfig(token)]);
      this._forms = forms;
      this._users = users;
      this._csvExportEnabled = config.csvExportEnabled;
      await this._load();
    });
  }

  private _onPresetChange(value: string) {
    this._datePreset = value;
    if (value === 'custom') return; // keep whatever dates are set; user edits the inputs
    const preset = DATE_PRESETS.find(p => p.value === value);
    this._fromDate = preset?.days !== undefined ? isoDateDaysAgo(preset.days) : '';
    this._toDate = '';
    this._onFilterChange();
  }

  private async _getToken(): Promise<string | undefined> {
    return (await this._authContext?.getLatestToken()) ?? undefined;
  }

  private async _load() {
    this._loading = true;
    try {
      const token = await this._getToken();
      const result = await getEntries({
        formId: this._selectedFormId || undefined,
        userKey: this._selectedUserKey || undefined,
        page: this._page,
        pageSize: this._pageSize,
        eventType: this._selectedEventType !== '' ? Number(this._selectedEventType) : undefined,
        fromDate: this._fromDate || undefined,
        toDate: this._toDate || undefined,
      }, token);
      this._entries = result.items;
      this._total = result.total;
    } catch (e) {
      console.error('[audit-dashboard] Load failed', e);
    } finally {
      this._loading = false;
    }
  }

  private _onFilterChange() {
    this._page = 1;
    this._selectedEntryId = undefined;
    this._load();
  }

  private _onPageChange(e: CustomEvent) {
    this._page = e.detail.value as number;
    this._load();
  }

  override render() {
    return html`
      <uui-box headline="Forms Audit Trail">
        <!-- Filter bar -->
        <div class="filters">
          <label class="date-label">
            Form
            <select .value=${this._selectedFormId} @change=${(e: Event) => { this._selectedFormId = (e.target as HTMLSelectElement).value; this._onFilterChange(); }}>
              <option value="">All forms</option>
              ${this._forms.map(f => html`<option value=${f.formId} ?selected=${f.formId === this._selectedFormId}>${f.formName}</option>`)}
            </select>
          </label>

          <label class="date-label">
            Event
            <select .value=${this._selectedEventType} @change=${(e: Event) => { this._selectedEventType = (e.target as HTMLSelectElement).value; this._onFilterChange(); }}>
              <option value="" ?selected=${this._selectedEventType === ''}>All events</option>
              <option value="0" ?selected=${this._selectedEventType === '0'}>Created</option>
              <option value="1" ?selected=${this._selectedEventType === '1'}>Saved</option>
              <option value="2" ?selected=${this._selectedEventType === '2'}>Deleted</option>
            </select>
          </label>

          <label class="date-label">
            User
            <select .value=${this._selectedUserKey} @change=${(e: Event) => { this._selectedUserKey = (e.target as HTMLSelectElement).value; this._onFilterChange(); }}>
              <option value="" ?selected=${this._selectedUserKey === ''}>All users</option>
              ${this._users.map(u => html`<option value=${u.userKey} ?selected=${u.userKey === this._selectedUserKey}>${u.userName}</option>`)}
            </select>
          </label>

          <label class="date-label">
            Date range
            <select .value=${this._datePreset} @change=${(e: Event) => this._onPresetChange((e.target as HTMLSelectElement).value)}>
              ${DATE_PRESETS.map(p => html`<option value=${p.value} ?selected=${p.value === this._datePreset}>${p.label}</option>`)}
            </select>
          </label>

          ${this._datePreset === 'custom' ? html`
            <label class="date-label">
              From
              <input type="date"
                .value=${this._fromDate}
                @change=${(e: Event) => { this._fromDate = (e.target as HTMLInputElement).value; this._onFilterChange(); }} />
            </label>
            <label class="date-label">
              To
              <input type="date"
                .value=${this._toDate}
                @change=${(e: Event) => { this._toDate = (e.target as HTMLInputElement).value; this._onFilterChange(); }} />
            </label>
          ` : ''}
          ${this._csvExportEnabled ? html`
            <uui-button class="export-link" look="outline" @click=${async () => {
              const token = await this._getToken();
              await exportCsv({
                formId: this._selectedFormId || undefined,
                userKey: this._selectedUserKey || undefined,
                eventType: this._selectedEventType !== '' ? Number(this._selectedEventType) : undefined,
                fromDate: this._fromDate || undefined,
                toDate: this._toDate || undefined,
              }, token);
            }}>Export CSV</uui-button>
          ` : ''}
        </div>

        ${this._loading
          ? html`<uui-loader></uui-loader>`
          : this._entries.length === 0
            ? html`<div class="empty-state"><uui-icon name="icon-info"></uui-icon><p>No audit entries found.</p></div>`
            : html`
                <uui-table>
                  <uui-table-head>
                    <uui-table-head-cell>Date / Time</uui-table-head-cell>
                    <uui-table-head-cell>Form</uui-table-head-cell>
                    <uui-table-head-cell>User</uui-table-head-cell>
                    <uui-table-head-cell>Event</uui-table-head-cell>
                    <uui-table-head-cell>Summary</uui-table-head-cell>
                  </uui-table-head>
                  ${this._entries.map(entry => html`
                    <uui-table-row
                      selectable
                      ?selected=${this._selectedEntryId === entry.id}
                      @click=${() => this._selectedEntryId = entry.id}>
                      <uui-table-cell>${new Date(entry.timestamp).toLocaleString()}</uui-table-cell>
                      <uui-table-cell>${entry.formName}</uui-table-cell>
                      <uui-table-cell>${entry.userName}</uui-table-cell>
                      <uui-table-cell style="white-space: nowrap;">
                        <uui-tag look=${entry.eventType === 'Created' ? 'positive' : entry.eventType === 'Deleted' ? 'danger' : 'default'} color=${entry.eventType === 'Created' ? 'positive' : entry.eventType === 'Deleted' ? 'danger' : 'default'}>
                          ${entry.eventType}
                        </uui-tag>
                      </uui-table-cell>
                      <uui-table-cell>${entry.changeSummary ?? '—'}</uui-table-cell>
                    </uui-table-row>
                  `)}
                </uui-table>

                <uui-pagination
                  .total=${Math.ceil(this._total / this._pageSize)}
                  .current=${this._page}
                  @change=${this._onPageChange}>
                </uui-pagination>
              `}
      </uui-box>

      ${this._selectedEntryId !== undefined ? html`
        <uui-box headline="Change Detail">
          <audit-diff-viewer .entryId=${this._selectedEntryId}></audit-diff-viewer>
        </uui-box>
      ` : ''}
    `;
  }

  static override styles = css`
    :host { display: block; padding: 16px; }
    .filters {
      display: flex;
      gap: 12px;
      flex-wrap: wrap;
      margin-bottom: 16px;
    }
    .filters select,
    .filters input {
      padding: 6px 10px;
      border: 1px solid var(--uui-color-border, #ccc);
      border-radius: 4px;
      font-size: 14px;
      height: 34px;
      box-sizing: border-box;
    }
    .date-label {
      display: flex;
      flex-direction: column;
      gap: 4px;
      font-size: 12px;
      color: var(--uui-color-text-alt, #666);
    }
    .export-link {
      align-self: flex-end;
      text-decoration: none;
      margin-left: auto;
    }
    .empty-state {
      display: flex;
      flex-direction: column;
      align-items: center;
      padding: 40px;
      color: var(--uui-color-text-alt, #888);
    }
    uui-table { width: 100%; margin-bottom: 16px; }
    uui-pagination { margin-top: 16px; }
  `;
}
