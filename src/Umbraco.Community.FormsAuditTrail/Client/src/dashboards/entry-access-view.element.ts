import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { css, html, nothing } from '@umbraco-cms/backoffice/external/lit';
import { customElement, property, state } from '@umbraco-cms/backoffice/external/lit';
import { UMB_AUTH_CONTEXT } from '@umbraco-cms/backoffice/auth';
import type {
  AccessEntryListDto,
  AccessFilterParams,
  AccessUserSummaryDto,
  AuditConfigDto,
  FormSummaryDto,
} from '../services/audit-api.service.js';
import { downloadAccessCsv, getAccess, getAccessForms, getAccessUsers } from '../services/audit-api.service.js';
import { ACCESS_EVENT_OPTIONS, accessDetailLines, exportBlocked, filterIssues } from '../utils/access-detail.js';
import { entryCell, eventTag, parseRecordId, retentionBanner, userCell, utcDateTime } from '../utils/access-format.js';
import { bearerToken, focusHeadingIn } from '../utils/view-helpers.js';
import { auditSharedStyles } from '../styles/audit-shared.styles.js';
import '../components/audit-diff-viewer.element.js';

const PAGE_SIZE = 20;

@customElement('forms-audit-entry-access')
export class EntryAccessViewElement extends UmbLitElement {
  @property({ attribute: false }) config?: AuditConfigDto;

  @state() private _rows: AccessEntryListDto[] = [];
  @state() private _forms: FormSummaryDto[] = [];
  @state() private _users: AccessUserSummaryDto[] = [];
  @state() private _total = 0;
  @state() private _page = 1;
  @state() private _loading = false;
  @state() private _error?: string;
  @state() private _formId = '';
  @state() private _userKey = '';
  @state() private _eventType = '';
  @state() private _fromDate = '';
  @state() private _toDate = '';
  @state() private _recordId = '';
  @state() private _selectedAccessId?: number;
  @state() private _exporting = false;

  private _authContext?: typeof UMB_AUTH_CONTEXT.TYPE;
  private _requestId = 0;

  override connectedCallback() {
    super.connectedCallback();
    this.consumeContext(UMB_AUTH_CONTEXT, async (authContext) => {
      if (!authContext) return;
      this._authContext = authContext;
      const token = await this._token();
      try {
        [this._forms, this._users] = await Promise.all([getAccessForms(token), getAccessUsers(token)]);
      } catch (e) {
        console.error('[audit-access] Filter lists failed', e);
      }
      await this._load();
    });
  }

  private _token() {
    return bearerToken(this._authContext);
  }

  private _filters(): AccessFilterParams {
    return {
      formId: this._formId || undefined,
      userKey: this._userKey || undefined,
      eventType: this._eventType !== '' ? Number(this._eventType) : undefined,
      fromDate: this._fromDate || undefined,
      toDate: this._toDate || undefined,
      recordId: parseRecordId(this._recordId) ?? undefined,
    };
  }

  private async _load() {
    const request = ++this._requestId;
    this._loading = true;
    this._error = undefined;
    try {
      const result = await getAccess({ ...this._filters(), page: this._page, pageSize: PAGE_SIZE }, await this._token());
      if (request !== this._requestId) return;
      this._rows = result.items;
      this._total = result.total;
    } catch (e) {
      if (request !== this._requestId) return;
      this._rows = [];
      this._total = 0;
      this._error = 'The entry access log could not be loaded.';
      console.error('[audit-access] Load failed', e);
    } finally {
      if (request === this._requestId) this._loading = false;
    }
  }

  private _filterChanged() {
    this._page = 1;
    this._selectedAccessId = undefined;
    this._load();
  }

  private _onRecordIdChange(raw: string) {
    this._recordId = raw.trim();
    // An invalid id is never sent (SEC-I2); the error shows and the CSV button is disabled until it is fixed.
    if (!this._issues().recordId) this._filterChanged();
  }

  focusHeading(): boolean {
    return focusHeadingIn(this.shadowRoot);
  }

  private _issues() {
    return filterIssues({ recordId: this._recordId, fromDate: this._fromDate, toDate: this._toDate });
  }

  private _openHistory(recordId: string) {
    this.dispatchEvent(new CustomEvent('open-record-history', { detail: { recordId }, bubbles: true, composed: true }));
  }

  private _entry(row: AccessEntryListDto) {
    const id = row.recordId ? parseRecordId(row.recordId) : null;
    if (!id) return entryCell(row);
    return html`<a
      href=${`?recordId=${id}`}
      @click=${(e: MouseEvent) => {
        e.preventDefault();
        this._openHistory(id);
      }}
      >${entryCell(row)}</a
    >`;
  }

  private _detail(row: AccessEntryListDto) {
    if (row.eventType === 'Edited') {
      return html`<uui-button
        look="outline"
        compact
        label=${`View changes to ${row.formName} at ${utcDateTime(row.timestamp)} UTC`}
        @click=${() => (this._selectedAccessId = row.id)}
        >View changes</uui-button
      >`;
    }
    const lines = accessDetailLines(row, this._users);
    if (lines.length === 0) return nothing;
    return html`<ul class="lines">${lines.map((l) => html`<li>${l}</li>`)}</ul>`;
  }

  private async _export() {
    // The CSV must carry exactly the filters on screen; an invalid one would be dropped silently.
    if (exportBlocked(this._issues()) || this._exporting) return;
    this._exporting = true;
    try {
      await downloadAccessCsv(this._filters(), await this._token());
    } catch (e) {
      this._error = 'The CSV export failed.';
      console.error('[audit-access] CSV export failed', e);
    } finally {
      this._exporting = false;
    }
  }

  override render() {
    const pages = Math.max(1, Math.ceil(this._total / PAGE_SIZE));
    const issues = this._issues();
    const status = this._exporting ? 'Preparing the CSV export' : this._loading ? 'Loading entry access' : `${this._total} matching rows`;
    return html`
      <uui-box>
        <h2 slot="headline" id="heading" tabindex="-1">Entry access</h2>
        ${this.config ? html`<p class="muted">${retentionBanner(this.config)}</p>` : nothing}
        <div class="filters" role="search" aria-label="Entry access filters">
          <label class="field">Form
            <select @change=${(e: Event) => { this._formId = (e.target as HTMLSelectElement).value; this._filterChanged(); }}>
              <option value="">All forms</option>
              ${this._forms.map((f) => html`<option value=${f.formId} ?selected=${f.formId === this._formId}>${f.formName}</option>`)}
            </select>
          </label>
          <label class="field">User
            <select @change=${(e: Event) => { this._userKey = (e.target as HTMLSelectElement).value; this._filterChanged(); }}>
              <option value="">All users</option>
              ${this._users.map((u) => html`<option value=${u.userKey} ?selected=${u.userKey === this._userKey}>${u.currentUserName ?? u.userName}</option>`)}
            </select>
          </label>
          <label class="field">Event
            <select @change=${(e: Event) => { this._eventType = (e.target as HTMLSelectElement).value; this._filterChanged(); }}>
              <option value="">All events</option>
              ${ACCESS_EVENT_OPTIONS.map((o) => html`<option value=${String(o.value)} ?selected=${String(o.value) === this._eventType}>${eventTag(o.label).label}</option>`)}
            </select>
          </label>
          <label class="field">From
            <input type="date" .value=${this._fromDate}
              aria-invalid=${issues.range ? 'true' : 'false'}
              aria-describedby=${issues.range ? 'range-error' : nothing}
              @change=${(e: Event) => { this._fromDate = (e.target as HTMLInputElement).value; this._filterChanged(); }} />
          </label>
          <label class="field">To
            <input type="date" .value=${this._toDate}
              aria-invalid=${issues.range ? 'true' : 'false'}
              aria-describedby=${issues.range ? 'range-error' : nothing}
              @change=${(e: Event) => { this._toDate = (e.target as HTMLInputElement).value; this._filterChanged(); }} />
          </label>
          <label class="field">Entry id
            <input type="text" .value=${this._recordId} spellcheck="false" autocomplete="off"
              aria-invalid=${issues.recordId ? 'true' : 'false'}
              aria-describedby=${issues.recordId ? 'record-id-error' : nothing}
              @change=${(e: Event) => this._onRecordIdChange((e.target as HTMLInputElement).value)} />
          </label>
          ${this.config?.entryAuditCsvEnabled
            ? html`<uui-button class="push-right" look="outline" label="Export CSV"
                ?disabled=${exportBlocked(issues)} state=${this._exporting ? 'waiting' : nothing}
                @click=${() => this._export()}>Export CSV</uui-button>`
            : nothing}
        </div>
        ${issues.recordId ? html`<p id="record-id-error" class="error" role="alert">${issues.recordId}</p>` : nothing}
        ${issues.range ? html`<p id="range-error" class="error" role="alert">${issues.range}</p>` : nothing}
        ${this._error ? html`<p class="error" role="alert">${this._error}</p>` : nothing}

        <div role="status" aria-live="polite">
          <span class="sr-only">${status}</span>
          ${!this._loading && this._rows.length === 0
            ? html`<div class="empty-state"><uui-icon name="icon-info"></uui-icon><p>No entry access recorded for these filters.</p></div>`
            : nothing}
        </div>

        <!-- The table and pager stay mounted while loading, so focus on the pager is not lost on a page change. -->
        ${this._loading ? html`<uui-loader></uui-loader>` : nothing}
        ${this._rows.length > 0
          ? html`
              <uui-table aria-label="Entry access log" aria-busy=${this._loading ? 'true' : 'false'}>
                <uui-table-head>
                  <uui-table-head-cell>When (UTC)</uui-table-head-cell>
                  <uui-table-head-cell>User</uui-table-head-cell>
                  <uui-table-head-cell>Event</uui-table-head-cell>
                  <uui-table-head-cell>Form</uui-table-head-cell>
                  <uui-table-head-cell>Entry</uui-table-head-cell>
                  <uui-table-head-cell>Detail</uui-table-head-cell>
                </uui-table-head>
                ${this._rows.map((row) => {
                  const tag = eventTag(row.eventType);
                  return html`
                    <uui-table-row>
                      <uui-table-cell>${utcDateTime(row.timestamp)}</uui-table-cell>
                      <uui-table-cell>${userCell(row)}</uui-table-cell>
                      <uui-table-cell><uui-tag color=${tag.color} look=${tag.look}>${tag.label}</uui-tag></uui-table-cell>
                      <uui-table-cell>${row.formName}</uui-table-cell>
                      <uui-table-cell>${this._entry(row)}</uui-table-cell>
                      <uui-table-cell>${this._detail(row)}</uui-table-cell>
                    </uui-table-row>
                  `;
                })}
              </uui-table>
            `
          : nothing}
        ${this._total > 0
          ? html`<uui-pagination
              label="Entry access pages"
              .total=${pages}
              .current=${this._page}
              @change=${(e: CustomEvent) => { this._page = (e.target as HTMLElement & { current: number }).current; this._selectedAccessId = undefined; this._load(); }}
            ></uui-pagination>`
          : nothing}
      </uui-box>

      ${this._selectedAccessId !== undefined
        ? html`<uui-box headline="Change detail"><audit-diff-viewer .accessId=${this._selectedAccessId}></audit-diff-viewer></uui-box>`
        : nothing}
    `;
  }

  static override styles = [
    auditSharedStyles,
    css`
      uui-box + uui-box { margin-top: 16px; }
      a { color: var(--uui-color-interactive); }
      a:focus-visible { outline: 2px solid var(--uui-color-focus); outline-offset: 2px; }
    `,
  ];
}

declare global {
  interface HTMLElementTagNameMap {
    'forms-audit-entry-access': EntryAccessViewElement;
  }
}
