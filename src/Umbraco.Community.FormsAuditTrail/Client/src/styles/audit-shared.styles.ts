import { css } from '@umbraco-cms/backoffice/external/lit';

// Copy-adapted from the existing dashboard's filter bar and table styles (design "Reuse from User Activity").
export const auditSharedStyles = css`
  :host { display: block; }
  .filters { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 16px; align-items: flex-end; }
  .field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--uui-color-text-alt); }
  .filters select, .filters input {
    padding: 6px 10px;
    border: 1px solid var(--uui-color-border-standalone, var(--uui-color-border));
    border-radius: 4px;
    font-size: 14px;
    min-height: 34px;
    box-sizing: border-box;
    background: var(--uui-color-surface);
    color: var(--uui-color-text);
  }
  .filters select:focus-visible, .filters input:focus-visible { outline: 2px solid var(--uui-color-focus); outline-offset: 1px; }
  .push-right { margin-left: auto; }
  .empty-state { display: flex; flex-direction: column; align-items: center; padding: 40px; color: var(--uui-color-text-alt); }
  .error { color: var(--uui-color-danger-standalone, var(--uui-color-danger)); margin: 8px 0; }
  .muted { color: var(--uui-color-text-alt); }
  .note { margin: 12px 0 0; font-size: 13px; color: var(--uui-color-text-alt); }
  uui-table { width: 100%; margin-bottom: 16px; }
  ul.lines { margin: 0; padding-left: 16px; }
  .sr-only {
    position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
    overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0;
  }
`;
