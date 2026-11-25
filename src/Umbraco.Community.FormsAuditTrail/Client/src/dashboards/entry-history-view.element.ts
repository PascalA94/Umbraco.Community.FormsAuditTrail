import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { css, html, nothing } from '@umbraco-cms/backoffice/external/lit';
import { customElement, property, state } from '@umbraco-cms/backoffice/external/lit';
import { UMB_AUTH_CONTEXT } from '@umbraco-cms/backoffice/auth';
import type { AccessEntryListDto } from '../services/audit-api.service.js';
import { getAccessUsers, getRecordHistory } from '../services/audit-api.service.js';
import type { AccessUserSummaryDto } from '../services/audit-api.service.js';
import { RECORD_ID_MESSAGE, accessDetailLines, normaliseHistory } from '../utils/access-detail.js';
import { eventTag, parseRecordId, userCell, utcDateTime } from '../utils/access-format.js';
import { bearerToken, focusHeadingIn } from '../utils/view-helpers.js';
import { auditSharedStyles } from '../styles/audit-shared.styles.js';
import '../components/audit-diff-viewer.element.js';

const NOT_FOUND = "No recorded access for this entry, or you don't have access to its form.";
const EXPORTER_NOTE = "Exports from some exporters don't record which entries they contained. See the Entry access log for all exports of this form.";

@customElement('forms-audit-entry-history')
export class EntryHistoryViewElement extends UmbLitElement {
  /** Already validated by the caller (SEC-I2); validated again here because it reaches a request. */
  @property({ type: String }) recordId = '';

  @state() private _input = '';
  @state() private _inputError = false;
  @state() private _rows: AccessEntryListDto[] = [];
  @state() private _truncated = false;
  @state() private _loading = false;
  @state() private _message?: string;
  @state() private _searched = false;
  @state() private _users: AccessUserSummaryDto[] = [];
  @state() private _selectedAccessId?: number;

  private _authContext?: typeof UMB_AUTH_CONTEXT.TYPE;
  private _requestId = 0;

  override connectedCallback() {
    super.connectedCallback();
    this.consumeContext(UMB_AUTH_CONTEXT, async (authContext) => {
      if (!authContext) return;
      this._authContext = authContext;
      try {
        this._users = await getAccessUsers(await this._token());
      } catch (e) {
        console.error('[audit-history] User list failed', e);
      }
      if (parseRecordId(this.recordId)) await this._lookUp(this.recordId);
    });
  }

  override willUpdate(changed: Map<string, unknown>) {
    if (changed.has('recordId')) {
      const id = parseRecordId(this.recordId);
      // An invalid deep link leaves the input empty (SEC-I2).
      this._input = id ?? '';
      if (id && this._authContext) this._lookUp(id);
    }
  }

  focusHeading(): boolean {
    return focusHeadingIn(this.shadowRoot);
  }

  private _token() {
    return bearerToken(this._authContext);
  }

  private async _lookUp(id: string) {
    const request = ++this._requestId;
    this._loading = true;
    this._message = undefined;
    this._truncated = false;
    this._selectedAccessId = undefined;
    this._searched = true;
    try {
      const response = await getRecordHistory(id, await this._token());
      const history = normaliseHistory(response.items, response.truncatedHeader);
      if (request !== this._requestId) return;
      this._rows = history.items;
      this._truncated = history.truncated;
    } catch (e) {
      if (request !== this._requestId) return;
      this._rows = [];
      this._message = String(e).includes('HTTP 404') ? NOT_FOUND : 'The entry history could not be loaded.';
      if (!String(e).includes('HTTP 404')) console.error('[audit-history] Load failed', e);
    } finally {
      if (request === this._requestId) this._loading = false;
    }
  }

  private _submit(e: Event) {
    e.preventDefault();
    const id = parseRecordId(this._input.trim());
    this._inputError = id === null;
    if (id) this._lookUp(id);
  }

  private _detail(row: AccessEntryListDto) {
    if (row.eventType === 'Edited') {
      return html`<uui-button
        look="outline"
        compact
        label=${`View changes at ${utcDateTime(row.timestamp)} UTC`}
        @click=${() => (this._selectedAccessId = row.id)}
        >View changes</uui-button
      >`;
    }
    const lines = accessDetailLines(row, this._users);
    return lines.length === 0 ? nothing : html`<ul class="lines">${lines.map((l) => html`<li>${l}</li>`)}</ul>`;
  }

  override render() {
    return html`
      <uui-box>
        <h2 slot="headline" id="heading" tabindex="-1">Entry history</h2>
        <form class="filters" @submit=${(e: Event) => this._submit(e)}>
          <label class="field">Entry id
            <input id="record-id" type="text" .value=${this._input} spellcheck="false" autocomplete="off"
              aria-invalid=${this._inputError ? 'true' : 'false'}
              aria-describedby=${this._inputError ? 'record-id-error' : nothing}
              @input=${(e: Event) => (this._input = (e.target as HTMLInputElement).value)} />
          </label>
          <uui-button type="submit" look="primary" label="Show history">Show history</uui-button>
        </form>
        ${this._inputError ? html`<p id="record-id-error" class="error" role="alert">${RECORD_ID_MESSAGE}</p>` : nothing}

        <div role="status" aria-live="polite">
          ${this._loading ? html`<span class="sr-only">Loading entry history</span>` : nothing}
          ${this._message ? html`<p class="error">${this._message}</p>` : nothing}
          <p class="note">${this._truncated ? 'This history is incomplete: the server limited how many rows it returned. Use the Entry access log to see the rest.' : ''}</p>
          ${!this._loading && !this._searched
            ? html`<div class="empty-state"><p>Enter an entry id to see who has viewed, changed or exported it.</p></div>`
            : nothing}
        </div>

        ${this._loading
          ? html`<uui-loader></uui-loader>`
          : this._rows.length > 0
            ? html`
                <uui-table aria-label="Entry history">
                  <uui-table-head>
                    <uui-table-head-cell>When (UTC)</uui-table-head-cell>
                    <uui-table-head-cell>User</uui-table-head-cell>
                    <uui-table-head-cell>Event</uui-table-head-cell>
                    <uui-table-head-cell>Form</uui-table-head-cell>
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
                        <uui-table-cell>${this._detail(row)}</uui-table-cell>
                      </uui-table-row>
                    `;
                  })}
                </uui-table>
                <p class="note">${EXPORTER_NOTE}</p>
              `
            : nothing}
      </uui-box>

      ${this._selectedAccessId !== undefined
        ? html`<uui-box headline="Change detail"><audit-diff-viewer .accessId=${this._selectedAccessId}></audit-diff-viewer></uui-box>`
        : nothing}
    `;
  }

  static override styles = [auditSharedStyles, css`uui-box + uui-box { margin-top: 16px; }`];
}

declare global {
  interface HTMLElementTagNameMap {
    'forms-audit-entry-history': EntryHistoryViewElement;
  }
}
