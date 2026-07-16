import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { css, html } from '@umbraco-cms/backoffice/external/lit';
import { customElement, property, state } from '@umbraco-cms/backoffice/external/lit';
import { UMB_AUTH_CONTEXT } from '@umbraco-cms/backoffice/auth';
import type { AuditEntryDetailDto } from '../services/audit-api.service.js';
import { getEntry } from '../services/audit-api.service.js';

@customElement('audit-diff-viewer')
export class AuditDiffViewerElement extends UmbLitElement {
  @property({ type: Number }) entryId?: number;
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
      if (this.entryId !== undefined) await this._load();
    });
  }

  override updated(changed: Map<string, unknown>) {
    if (changed.has('entryId') && this.entryId !== undefined && this._authContext !== undefined) {
      this._load();
    }
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
    if (this._loading) return html`<uui-loader></uui-loader>`;
    if (this._error) return html`<uui-tag color="danger">Error: ${this._error}</uui-tag>`;
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

  static override styles = css`
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
    pre { background: var(--uui-color-surface-alt, #f5f5f5); padding: 12px; overflow: auto; font-size: 0.8em; }
  `;
}
