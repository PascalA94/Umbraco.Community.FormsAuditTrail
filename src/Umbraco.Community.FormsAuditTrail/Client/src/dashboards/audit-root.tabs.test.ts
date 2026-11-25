import { describe, expect, it } from 'vitest';
import source from './audit-root.element.ts?raw';

// The element needs a DOM and the Umbraco backoffice runtime, which this suite does not load, so the tab wiring
// is asserted against the template source.

describe('audit root tab wiring', () => {
  it('gives each tab panel role=tabpanel and an aria-labelledby that points at its tab', () => {
    const tab = /<uui-tab\s[^>]*id=\$\{`tab-\$\{tab\}`\}/s;
    const panel = /<div\s[^>]*role="tabpanel"[^>]*aria-labelledby=\$\{`tab-\$\{tab\}`\}/s;
    expect(source).toMatch(tab);
    expect(source).toMatch(panel);
  });

  // aria-controls was removed from the uui-tab hosts because axe flagged aria-required-children: the host is
  // not a tablist child it can resolve. The panel's aria-labelledby keeps the association.
  it('does not set aria-controls on the uui-tab hosts', () => {
    const tabs = source.match(/<uui-tab\s[^>]*>/gs) ?? [];
    expect(tabs.length).toBeGreaterThan(0);
    for (const t of tabs) expect(t).not.toContain('aria-controls');
  });
});
