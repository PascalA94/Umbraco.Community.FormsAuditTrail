import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { css, html } from '@umbraco-cms/backoffice/external/lit';
import { customElement, property, state } from '@umbraco-cms/backoffice/external/lit';
import { UMB_AUTH_CONTEXT } from '@umbraco-cms/backoffice/auth';
import type { AccessEntryDetailDto, AuditEntryDetailDto } from '../services/audit-api.service.js';
import { getAccessEntry, getEntry } from '../services/audit-api.service.js';
import { auditSharedStyles } from '../styles/audit-shared.styles.js';
import { utcDateTime, valueText } from '../utils/access-format.js';

@customElement('audit-diff-viewer')
export class AuditDiffViewerElement extends UmbLitElement {
  @property({ type: Number }) entryId?: number;
  /** When set, shows the field-by-field values of an Edited access-log row instead of a form-definition diff. */
  @property({ type: Number }) accessId?: number;
  @state() private _access?: AccessEntryDetailDto;
  @state() private _entry?: AuditEntryDetailDto;
  @state() private _loading = false;
  @state() private _showRaw = false;
  @state() private _error?: string;

  private _authContext?: typeof UMB_AUTH_CONTEXT.TYPE;

  override async connectedCallback() {
    super.connectedCallback();
    this.consumeContext(UMB_AUTH_CONTEXT, async (authContext) => {
      if (!authContext) return;
      this._authContext = authContext;
      if (this.accessId !== undefined) await this._loadAccess();
      else if (this.entryId !== undefined) await this._load();
    });
  }

  override updated(changed: Map<string, unknown>) {
    if (this._authContext === undefined) return;
    if (changed.has('accessId') && this.accessId !== undefined) {
      this._loadAccess();
    } else if (changed.has('entryId') && this.entryId !== undefined && this.accessId === undefined) {
      this._load();
    }
    // Keyboard and screen-reader users must land on the new content, not stay on the button that opened it.
    if (this._focusHeading && this._access) {
      this._focusHeading = false;
      this.shadowRoot?.querySelector<HTMLElement>('#access-heading')?.focus();
    }
  }

  private _focusHeading = false;

  private async _loadAccess() {
    if (this.accessId === undefined) return;
    const requested = this.accessId;
    this._loading = true;
    this._error = undefined;
    this._access = undefined;
    try {
      const token = (await this._authContext?.getLatestToken()) ?? undefined;
      const result = await getAccessEntry(requested, token);
      if (requested !== this.accessId) return; // a newer row was opened meanwhile
      this._access = result;
      this._focusHeading = true;
    } catch (e) {
      this._error = String(e);
      console.error('[audit-diff-viewer] Failed to load access entry', requested, e);
    } finally {
      this._loading = false;
    }
  }

  private _renderAccess(entry: AccessEntryDetailDto) {
    return html`
      <h3 id="access-heading" tabindex="-1">Changes to ${entry.formName}, ${utcDateTime(entry.timestamp)} UTC by ${entry.userName}</h3>
      ${entry.changes.length === 0
        ? html`<p>No field values were stored for this edit.</p>`
        : html`
            <uui-table aria-label="Field changes">
              <uui-table-head>
                <uui-table-head-cell>Field</uui-table-head-cell>
                <uui-table-head-cell>Before</uui-table-head-cell>
                <uui-table-head-cell>After</uui-table-head-cell>
              </uui-table-head>
              ${entry.changes.map((c) => html`
                <uui-table-row>
                  <uui-table-cell>${c.fieldCaption}</uui-table-cell>
                  <uui-table-cell>${valueText(c.oldValue, c.masked, c.valuesScrubbedAt, false)}</uui-table-cell>
                  <uui-table-cell>${valueText(c.newValue, c.masked, c.valuesScrubbedAt, c.cleared)}</uui-table-cell>
                </uui-table-row>
              `)}
            </uui-table>
          `}
    `;
  }

  private async _load() {
    if (this.entryId === undefined) return;
    this._loading = true;
    this._error = undefined;
    try {
      const token = (await this._authContext?.getLatestToken()) ?? undefined;
      this._entry = await getEntry(this.entryId, token);
    } catch (e) {
      this._error = String(e);
      console.error('[audit-diff-viewer] Failed to load entry', this.entryId, e);
    } finally {
      this._loading = false;
    }
  }

  private _badgeColor(changeType: string) {
    switch (changeType.toLowerCase()) {
      case 'added': return 'positive';
      case 'removed': return 'danger';
      case 'modified': return 'warning';
      case 'moved': return 'default';
      default: return 'default';
    }
  }

  private _groupByCategory() {
    const groups = new Map<string, AuditEntryDetailDto['changes']>();
    for (const change of this._entry?.changes ?? []) {
      const key = change.category ?? 'Other';
      const existing = groups.get(key) ?? [];
      existing.push(change);
      groups.set(key, existing);
    }
    return groups;
  }

  override render() {
    // The status element is always in the DOM and only its text changes, so screen readers announce the change.
    return html`
      <div role="status" aria-live="polite" class="sr-only">${this._loading ? 'Loading changes' : ''}</div>
      ${this._loading ? html`<uui-loader></uui-loader>` : this._body()}
    `;
  }

  private _body() {
    if (this._error) return html`<uui-tag color="danger" role="alert">Error: ${this._error}</uui-tag>`;
    if (this.accessId !== undefined) {
      return this._access ? this._renderAccess(this._access) : html`<p>Loading changes…</p>`;
    }
    if (!this._entry) return html`<p>Loading entry ${this.entryId}…</p>`;

    const entry = this._entry;
    const groups = this._groupByCategory();

    return html`
      <p><strong>Date:</strong> ${new Date(entry.timestamp).toLocaleString()} &nbsp; <strong>User:</strong> ${entry.userName}</p>

      ${groups.size === 0
        ? html`<uui-box><p>No field-level changes recorded.</p></uui-box>`
        : Array.from(groups.entries()).map(([category, changes]) => html`
            <uui-box headline=${category}>
              ${changes.map(c => html`
                <div class="change-row">
                  <uui-tag look=${this._badgeColor(c.changeType)} color=${this._badgeColor(c.changeType)}>${c.changeType}</uui-tag>
                  <span class="description">${c.friendlyDescription}</span>
                  ${c.oldValue || c.newValue ? html`
                    <span class="values">
                      ${c.oldValue ? html`<del>${c.oldValue}</del>` : ''}
                      ${c.oldValue && c.newValue ? html` → ` : ''}
                      ${c.newValue ? html`<ins>${c.newValue}</ins>` : ''}
                    </span>
                  ` : ''}
                </div>
              `)}
            </uui-box>
          `)}

      <uui-button @click=${() => (this._showRaw = !this._showRaw)}>
        ${this._showRaw ? 'Hide' : 'Show'} raw JSON
      </uui-button>
      ${this._showRaw ? html`
        <details open>
          <summary>Before</summary>
          <pre>${entry.beforeSnapshot}</pre>
        </details>
        <details open>
          <summary>After</summary>
          <pre>${entry.afterSnapshot ?? 'N/A'}</pre>
        </details>
      ` : ''}
    `;
  }

  static override styles = [auditSharedStyles, css`
    .change-row {
      display: flex;
      align-items: flex-start;
      gap: 8px;
      padding: 6px 0;
      border-bottom: 1px solid var(--uui-color-border, #e0e0e0);
    }
    .description { flex: 1; }
    .values { color: var(--uui-color-text-alt, #555); font-size: 0.9em; }
    del { color: var(--uui-color-danger, red); }
    ins { color: var(--uui-color-positive, green); text-decoration: none; }
    h3:focus-visible { outline: 2px solid var(--uui-color-focus); outline-offset: 2px; }
    uui-table { width: 100%; }
    pre { background: var(--uui-color-surface-alt, #f5f5f5); padding: 12px; overflow: auto; font-size: 0.8em; }
  `];
}
