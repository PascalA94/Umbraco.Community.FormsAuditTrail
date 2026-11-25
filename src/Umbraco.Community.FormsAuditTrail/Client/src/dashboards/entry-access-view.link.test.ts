import { describe, expect, it } from 'vitest';
import { openHistoryEvent } from './audit-root.view.js';
import source from './entry-access-view.element.ts?raw';

const ID = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';

describe('openHistoryEvent', () => {
  it('carries the record id and crosses shadow roots', () => {
    const e = openHistoryEvent(ID);
    expect(e.type).toBe('open-record-history');
    expect(e.detail.recordId).toBe(ID);
    expect(e.bubbles).toBe(true);
    expect(e.composed).toBe(true);
  });
});

// The element needs the Umbraco runtime and this suite has no DOM, so the template is also guarded from source.
// The real-router behaviour is covered by the E2E walkthrough.
describe('entry access record link template', () => {
  it('is a labelled button and renders no anchor', () => {
    expect(source).toMatch(/<button\s+type="button"\s+class="link-button"\s+aria-label=\$\{`Open history for entry \$\{id\}`\}/);
    expect(source).not.toMatch(/<a[\s>]/);
    expect(source).not.toMatch(/href=/);
  });

  it('dispatches through openHistoryEvent', () => {
    expect(source).toContain('this.dispatchEvent(openHistoryEvent(recordId))');
  });
});
