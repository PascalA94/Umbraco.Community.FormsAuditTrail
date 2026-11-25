import { UmbLitElement } from '@umbraco-cms/backoffice/lit-element';
import { css, html, nothing } from '@umbraco-cms/backoffice/external/lit';
import { customElement, property } from '@umbraco-cms/backoffice/external/lit';

/** Red alert box above all tabs. One list item per line; every line is a text binding (S3). */
@customElement('audit-health-banner')
export class AuditHealthBannerElement extends UmbLitElement {
  @property({ attribute: false }) lines: string[] = [];

  override render() {
    if (this.lines.length === 0) return nothing;
    return html`
      <uui-box role="alert">
        <ul>
          ${this.lines.map((line) => html`<li>${line}</li>`)}
        </ul>
      </uui-box>
    `;
  }

  static override styles = css`
    :host { display: block; margin-bottom: 16px; }
    uui-box {
      border-left: 4px solid var(--uui-color-danger);
      background: var(--uui-color-surface);
    }
    ul { margin: 0; padding-left: 20px; }
    li { color: var(--uui-color-danger-standalone, var(--uui-color-danger)); font-weight: 600; }
  `;
}

declare global {
  interface HTMLElementTagNameMap {
    'audit-health-banner': AuditHealthBannerElement;
  }
}
