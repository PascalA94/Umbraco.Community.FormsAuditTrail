import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { css, html, nothing } from '@umbraco-cms/backoffice/external/lit';
import { customElement, state } from '@umbraco-cms/backoffice/external/lit';
import { UMB_AUTH_CONTEXT } from '@umbraco-cms/backoffice/auth';
import { UMB_CURRENT_USER_CONTEXT } from '@umbraco-cms/backoffice/current-user';
import type { AuditConfigDto } from '../services/audit-api.service.js';
import { getConfig } from '../services/audit-api.service.js';
import type { AuditTab } from './audit-root.view.js';
import type { FocusChild } from './audit-root.view.js';
import { auditRootView, dismissGdprNotice, focusWhenRendered, openHistoryTarget } from './audit-root.view.js';
import './audit-dashboard.element.js';
import './entry-access-view.element.js';
import './entry-history-view.element.js';
import '../components/audit-health-banner.element.js';

const TAB_LABELS: Record<AuditTab, string> = {
  formChanges: 'Form changes',
  entryAccess: 'Entry access',
  entryHistory: 'Entry history',
};

const FOOTNOTE =
  'Records backoffice activity only. Changes made by code, workflows, the Delivery API or directly in the database are not captured, and some Forms screens are not recorded. See the README.';
const README_URL = 'https://github.com/PascalA94/Umbraco.Community.FormsAuditTrail#gdpr';

function safeStorage(): Storage | null {
  try {
    return window.localStorage;
  } catch {
    return null; // blocked storage throws on access in some privacy modes
  }
}

@customElement('forms-audit-root')
export class AuditRootElement extends UmbLitElement {
  @state() private _config?: AuditConfigDto;
  @state() private _error?: string;
  @state() private _userKey = '';
  @state() private _activeTab?: AuditTab;
  @state() private _historyRecordId = '';
  @state() private _noticeDismissed = false;

  override connectedCallback() {
    super.connectedCallback();
    this.consumeContext(UMB_AUTH_CONTEXT, async (authContext) => {
      if (!authContext) return;
      try {
        this._config = await getConfig((await authContext.getLatestToken()) ?? undefined);
        const view = auditRootView(this._config, new URL(window.location.href), safeStorage(), this._userKey);
        this._activeTab ??= view.initialTab;
        if (view.deepLinkRecordId) this._historyRecordId = view.deepLinkRecordId;
      } catch (e) {
        this._error = 'The audit dashboard could not be loaded.';
        console.error('[audit-root] Config load failed', e);
      }
    });
    this.consumeContext(UMB_CURRENT_USER_CONTEXT, (ctx) => {
      if (!ctx) return;
      this.observe(ctx.unique, (unique) => (this._userKey = unique ?? ''), '_observeCurrentUserKey');
    });
  }

  // The dismiss button disappears with the notice, so focus would fall to the page. Send it to the active tab.
  private _focusTarget: 'tab' | 'panel' | null = null;

  private _dismissNotice() {
    dismissGdprNotice(safeStorage(), this._userKey);
    this._noticeDismissed = true;
    this._focusTarget = 'tab';
  }

  private _onOpenHistory = (e: Event) => {
    const id = openHistoryTarget((e as CustomEvent).detail);
    if (!id) return;
    this._historyRecordId = id;
    this._activeTab = 'entryHistory';
    this._focusTarget = 'panel';
  };

  override updated() {
    const target = this._focusTarget;
    if (!target) return;
    this._focusTarget = null;
    const activeTab = () => this.shadowRoot?.querySelector<HTMLElement>('uui-tab[active]');
    const activePanel = () => this.shadowRoot?.querySelector<HTMLElement>('[role="tabpanel"]:not([hidden])');
    if (target === 'tab') {
      void focusWhenRendered(this, () => null, activeTab);
      return;
    }
    // A tab switch made by code, not by the user pressing a tab: land on the new panel's heading, once the new child
    // has rendered it.
    void focusWhenRendered(this, () => activePanel()?.firstElementChild as FocusChild | null, activePanel);
  }

  private _panel(tab: AuditTab) {
    switch (tab) {
      case 'entryAccess':
        return html`<forms-audit-entry-access .config=${this._config}></forms-audit-entry-access>`;
      case 'entryHistory':
        return html`<forms-audit-entry-history .recordId=${this._historyRecordId}></forms-audit-entry-history>`;
      default:
        return html`<forms-audit-dashboard></forms-audit-dashboard>`;
    }
  }

  override render() {
    if (this._error) return html`<uui-box><p class="error" role="alert">${this._error}</p></uui-box>`;
    if (!this._config) return html`<uui-loader></uui-loader>`;

    const view = auditRootView(this._config, new URL(window.location.href), safeStorage(), this._userKey);
    const active = this._activeTab && view.tabs.includes(this._activeTab) ? this._activeTab : view.initialTab;
    const showNotice = view.showGdprNotice && !this._noticeDismissed;

    return html`
      <audit-health-banner .lines=${view.bannerLines}></audit-health-banner>

      ${view.offMessage ? html`<uui-box class="gap" role="status"><p>${view.offMessage}</p></uui-box>` : nothing}

      ${showNotice
        ? html`
            <uui-box class="gap" role="status">
              <div class="notice">
                <span>Entry auditing is now on. <a href=${README_URL} target="_blank" rel="noopener noreferrer">Read the GDPR notes.</a></span>
                <uui-button look="outline" label="Dismiss the GDPR notice" @click=${() => this._dismissNotice()}>Dismiss</uui-button>
              </div>
            </uui-box>
          `
        : nothing}

      <uui-tab-group class="tabs" label="Audit sections">
        ${view.tabs.map(
          (tab) => html`
            <uui-tab
              id=${`tab-${tab}`}
              label=${TAB_LABELS[tab]}
              ?active=${tab === active}
              @click=${() => (this._activeTab = tab)}
              >${TAB_LABELS[tab]}</uui-tab
            >
          `,
        )}
      </uui-tab-group>

      <!-- The active tab is not in the URL, and only the active panel is mounted, so filters reset on a tab change. -->
      ${view.tabs.map(
        (tab) => html`
          <div
            id=${`panel-${tab}`}
            role="tabpanel"
            tabindex="0"
            aria-labelledby=${`tab-${tab}`}
            ?hidden=${tab !== active}
            @open-record-history=${this._onOpenHistory}
          >
            ${tab === active ? this._panel(tab) : nothing}
          </div>
        `,
      )}

      <p class="footnote">${FOOTNOTE}</p>
    `;
  }

  static override styles = css`
    :host { display: block; padding: 16px; }
    .gap { display: block; margin-bottom: 16px; }
    .notice { display: flex; gap: 16px; align-items: center; justify-content: space-between; flex-wrap: wrap; }
    .tabs { margin-bottom: 16px; }
    .error { color: var(--uui-color-danger-standalone, var(--uui-color-danger)); }
    a { color: var(--uui-color-interactive); }
    a:focus-visible { outline: 2px solid var(--uui-color-focus); outline-offset: 2px; }
    .footnote { margin-top: 24px; font-size: 13px; color: var(--uui-color-text-alt); }
    forms-audit-dashboard { padding: 0; }
  `;
}

declare global {
  interface HTMLElementTagNameMap {
    'forms-audit-root': AuditRootElement;
  }
}
