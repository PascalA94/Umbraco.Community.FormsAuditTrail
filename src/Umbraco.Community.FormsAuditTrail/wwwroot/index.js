import { umbExtensionsRegistry as $ } from "@umbraco-cms/backoffice/extension-registry";
import { UmbLitElement as y } from "@umbraco-cms/backoffice/lit-element";
import { css as v, property as w, state as r, customElement as m, html as s } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT as b } from "@umbraco-cms/backoffice/auth";
const x = [
  {
    type: "dashboard",
    alias: "UmbracoCommunity.FormsAuditTrail.Dashboard",
    name: "Forms Audit Trail",
    elementName: "forms-audit-dashboard",
    weight: 20,
    meta: {
      label: "Forms Audit Trail",
      pathname: "forms-audit-trail"
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Forms"
      }
    ]
  }
], h = "/umbraco/formsaudit/api/v1";
async function _(e, t) {
  const o = {};
  t && (o.Authorization = `Bearer ${t}`);
  const i = await fetch(e, { credentials: "include", headers: o });
  if (!i.ok) throw new Error(`HTTP ${i.status}`);
  return i.json();
}
async function D(e) {
  return _(`${h}/config`, e);
}
async function T(e, t) {
  const o = new URLSearchParams();
  return e.formId && o.set("formId", e.formId), e.page && o.set("page", String(e.page)), e.pageSize && o.set("pageSize", String(e.pageSize)), e.userKey && o.set("userKey", e.userKey), e.eventType !== void 0 && o.set("eventType", String(e.eventType)), e.fromDate && o.set("fromDate", e.fromDate), e.toDate && o.set("toDate", e.toDate), _(`${h}/entries?${o}`, t);
}
async function E(e, t) {
  return _(`${h}/entries/${e}`, t);
}
async function C(e) {
  return _(`${h}/forms`, e);
}
async function I(e) {
  return _(`${h}/users`, e);
}
async function S(e, t) {
  const o = new URLSearchParams();
  e.formId && o.set("formId", e.formId), e.userKey && o.set("userKey", e.userKey), e.eventType !== void 0 && o.set("eventType", String(e.eventType)), e.fromDate && o.set("fromDate", e.fromDate), e.toDate && o.set("toDate", e.toDate);
  const i = {};
  t && (i.Authorization = `Bearer ${t}`);
  const a = await fetch(`${h}/entries/export?${o}`, { credentials: "include", headers: i });
  if (!a.ok) throw new Error(`HTTP ${a.status}`);
  const u = await a.blob(), d = URL.createObjectURL(u), f = document.createElement("a");
  f.href = d, f.download = `forms-audit-${(/* @__PURE__ */ new Date()).toISOString().slice(0, 10)}.csv`, f.click(), URL.revokeObjectURL(d);
}
var F = Object.defineProperty, P = Object.getOwnPropertyDescriptor, p = (e, t, o, i) => {
  for (var a = i > 1 ? void 0 : i ? P(t, o) : t, u = e.length - 1, d; u >= 0; u--)
    (d = e[u]) && (a = (i ? d(t, o, a) : d(a)) || a);
  return i && a && F(t, o, a), a;
};
let c = class extends y {
  constructor() {
    super(...arguments), this._loading = !1, this._showRaw = !1;
  }
  async connectedCallback() {
    super.connectedCallback(), this.consumeContext(b, async (e) => {
      e && (this._authContext = e, this.entryId !== void 0 && await this._load());
    });
  }
  updated(e) {
    e.has("entryId") && this.entryId !== void 0 && this._authContext !== void 0 && this._load();
  }
  async _load() {
    if (this.entryId !== void 0) {
      this._loading = !0, this._error = void 0;
      try {
        const e = await this._authContext?.getLatestToken() ?? void 0;
        this._entry = await E(this.entryId, e);
      } catch (e) {
        this._error = String(e), console.error("[audit-diff-viewer] Failed to load entry", this.entryId, e);
      } finally {
        this._loading = !1;
      }
    }
  }
  _badgeColor(e) {
    switch (e.toLowerCase()) {
      case "added":
        return "positive";
      case "removed":
        return "danger";
      case "modified":
        return "warning";
      case "moved":
        return "default";
      default:
        return "default";
    }
  }
  _groupByCategory() {
    const e = /* @__PURE__ */ new Map();
    for (const t of this._entry?.changes ?? []) {
      const o = t.category ?? "Other", i = e.get(o) ?? [];
      i.push(t), e.set(o, i);
    }
    return e;
  }
  render() {
    if (this._loading) return s`<uui-loader></uui-loader>`;
    if (this._error) return s`<uui-tag color="danger">Error: ${this._error}</uui-tag>`;
    if (!this._entry) return s`<p>Loading entry ${this.entryId}…</p>`;
    const e = this._entry, t = this._groupByCategory();
    return s`
      <p><strong>Date:</strong> ${new Date(e.timestamp).toLocaleString()} &nbsp; <strong>User:</strong> ${e.userName}</p>

      ${t.size === 0 ? s`<uui-box><p>No field-level changes recorded.</p></uui-box>` : Array.from(t.entries()).map(([o, i]) => s`
            <uui-box headline=${o}>
              ${i.map((a) => s`
                <div class="change-row">
                  <uui-tag look=${this._badgeColor(a.changeType)} color=${this._badgeColor(a.changeType)}>${a.changeType}</uui-tag>
                  <span class="description">${a.friendlyDescription}</span>
                  ${a.oldValue || a.newValue ? s`
                    <span class="values">
                      ${a.oldValue ? s`<del>${a.oldValue}</del>` : ""}
                      ${a.oldValue && a.newValue ? s` → ` : ""}
                      ${a.newValue ? s`<ins>${a.newValue}</ins>` : ""}
                    </span>
                  ` : ""}
                </div>
              `)}
            </uui-box>
          `)}

      <uui-button @click=${() => this._showRaw = !this._showRaw}>
        ${this._showRaw ? "Hide" : "Show"} raw JSON
      </uui-button>
      ${this._showRaw ? s`
        <details open>
          <summary>Before</summary>
          <pre>${e.beforeSnapshot}</pre>
        </details>
        <details open>
          <summary>After</summary>
          <pre>${e.afterSnapshot ?? "N/A"}</pre>
        </details>
      ` : ""}
    `;
  }
};
c.styles = v`
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
p([
  w({ type: Number })
], c.prototype, "entryId", 2);
p([
  r()
], c.prototype, "_entry", 2);
p([
  r()
], c.prototype, "_loading", 2);
p([
  r()
], c.prototype, "_showRaw", 2);
p([
  r()
], c.prototype, "_error", 2);
c = p([
  m("audit-diff-viewer")
], c);
var U = Object.defineProperty, A = Object.getOwnPropertyDescriptor, n = (e, t, o, i) => {
  for (var a = i > 1 ? void 0 : i ? A(t, o) : t, u = e.length - 1, d; u >= 0; u--)
    (d = e[u]) && (a = (i ? d(t, o, a) : d(a)) || a);
  return i && a && U(t, o, a), a;
};
const g = [
  { value: "", label: "All time", days: void 0 },
  { value: "today", label: "Today", days: 0 },
  { value: "7", label: "Last 7 days", days: 7 },
  { value: "30", label: "Last 30 days", days: 30 },
  { value: "custom", label: "Custom", days: void 0 }
];
function K(e) {
  const t = /* @__PURE__ */ new Date();
  return t.setDate(t.getDate() - e), t.toISOString().slice(0, 10);
}
let l = class extends y {
  constructor() {
    super(...arguments), this._entries = [], this._forms = [], this._users = [], this._total = 0, this._page = 1, this._pageSize = 20, this._loading = !1, this._selectedFormId = "", this._selectedEventType = "", this._fromDate = "", this._toDate = "", this._selectedUserKey = "", this._datePreset = "", this._csvExportEnabled = !0;
  }
  async connectedCallback() {
    super.connectedCallback(), this.consumeContext(b, async (e) => {
      if (!e) return;
      this._authContext = e;
      const t = await this._getToken(), [o, i, a] = await Promise.all([C(t), I(t), D(t)]);
      this._forms = o, this._users = i, this._csvExportEnabled = a.csvExportEnabled, await this._load();
    });
  }
  _onPresetChange(e) {
    if (this._datePreset = e, e === "custom") return;
    const t = g.find((o) => o.value === e);
    this._fromDate = t?.days !== void 0 ? K(t.days) : "", this._toDate = "", this._onFilterChange();
  }
  async _getToken() {
    return await this._authContext?.getLatestToken() ?? void 0;
  }
  async _load() {
    this._loading = !0;
    try {
      const e = await this._getToken(), t = await T({
        formId: this._selectedFormId || void 0,
        userKey: this._selectedUserKey || void 0,
        page: this._page,
        pageSize: this._pageSize,
        eventType: this._selectedEventType !== "" ? Number(this._selectedEventType) : void 0,
        fromDate: this._fromDate || void 0,
        toDate: this._toDate || void 0
      }, e);
      this._entries = t.items, this._total = t.total;
    } catch (e) {
      console.error("[audit-dashboard] Load failed", e);
    } finally {
      this._loading = !1;
    }
  }
  _onFilterChange() {
    this._page = 1, this._selectedEntryId = void 0, this._load();
  }
  _onPageChange(e) {
    this._page = e.detail.value, this._load();
  }
  render() {
    return s`
      <uui-box headline="Forms Audit Trail">
        <!-- Filter bar -->
        <div class="filters">
          <label class="date-label">
            Form
            <select .value=${this._selectedFormId} @change=${(e) => {
      this._selectedFormId = e.target.value, this._onFilterChange();
    }}>
              <option value="">All forms</option>
              ${this._forms.map((e) => s`<option value=${e.formId} ?selected=${e.formId === this._selectedFormId}>${e.formName}</option>`)}
            </select>
          </label>

          <label class="date-label">
            Event
            <select .value=${this._selectedEventType} @change=${(e) => {
      this._selectedEventType = e.target.value, this._onFilterChange();
    }}>
              <option value="" ?selected=${this._selectedEventType === ""}>All events</option>
              <option value="0" ?selected=${this._selectedEventType === "0"}>Created</option>
              <option value="1" ?selected=${this._selectedEventType === "1"}>Saved</option>
              <option value="2" ?selected=${this._selectedEventType === "2"}>Deleted</option>
            </select>
          </label>

          <label class="date-label">
            User
            <select .value=${this._selectedUserKey} @change=${(e) => {
      this._selectedUserKey = e.target.value, this._onFilterChange();
    }}>
              <option value="" ?selected=${this._selectedUserKey === ""}>All users</option>
              ${this._users.map((e) => s`<option value=${e.userKey} ?selected=${e.userKey === this._selectedUserKey}>${e.userName}</option>`)}
            </select>
          </label>

          <label class="date-label">
            Date range
            <select .value=${this._datePreset} @change=${(e) => this._onPresetChange(e.target.value)}>
              ${g.map((e) => s`<option value=${e.value} ?selected=${e.value === this._datePreset}>${e.label}</option>`)}
            </select>
          </label>

          ${this._datePreset === "custom" ? s`
            <label class="date-label">
              From
              <input type="date"
                .value=${this._fromDate}
                @change=${(e) => {
      this._fromDate = e.target.value, this._onFilterChange();
    }} />
            </label>
            <label class="date-label">
              To
              <input type="date"
                .value=${this._toDate}
                @change=${(e) => {
      this._toDate = e.target.value, this._onFilterChange();
    }} />
            </label>
          ` : ""}
          ${this._csvExportEnabled ? s`
            <uui-button class="export-link" look="outline" @click=${async () => {
      const e = await this._getToken();
      await S({
        formId: this._selectedFormId || void 0,
        userKey: this._selectedUserKey || void 0,
        eventType: this._selectedEventType !== "" ? Number(this._selectedEventType) : void 0,
        fromDate: this._fromDate || void 0,
        toDate: this._toDate || void 0
      }, e);
    }}>Export CSV</uui-button>
          ` : ""}
        </div>

        ${this._loading ? s`<uui-loader></uui-loader>` : this._entries.length === 0 ? s`<div class="empty-state"><uui-icon name="icon-info"></uui-icon><p>No audit entries found.</p></div>` : s`
                <uui-table>
                  <uui-table-head>
                    <uui-table-head-cell>Date / Time</uui-table-head-cell>
                    <uui-table-head-cell>Form</uui-table-head-cell>
                    <uui-table-head-cell>User</uui-table-head-cell>
                    <uui-table-head-cell>Event</uui-table-head-cell>
                    <uui-table-head-cell>Summary</uui-table-head-cell>
                  </uui-table-head>
                  ${this._entries.map((e) => s`
                    <uui-table-row
                      selectable
                      ?selected=${this._selectedEntryId === e.id}
                      @click=${() => this._selectedEntryId = e.id}>
                      <uui-table-cell>${new Date(e.timestamp).toLocaleString()}</uui-table-cell>
                      <uui-table-cell>${e.formName}</uui-table-cell>
                      <uui-table-cell>${e.userName}</uui-table-cell>
                      <uui-table-cell style="white-space: nowrap;">
                        <uui-tag look=${e.eventType === "Created" ? "positive" : e.eventType === "Deleted" ? "danger" : "default"} color=${e.eventType === "Created" ? "positive" : e.eventType === "Deleted" ? "danger" : "default"}>
                          ${e.eventType}
                        </uui-tag>
                      </uui-table-cell>
                      <uui-table-cell>${e.changeSummary ?? "—"}</uui-table-cell>
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

      ${this._selectedEntryId !== void 0 ? s`
        <uui-box headline="Change Detail">
          <audit-diff-viewer .entryId=${this._selectedEntryId}></audit-diff-viewer>
        </uui-box>
      ` : ""}
    `;
  }
};
l.styles = v`
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
n([
  r()
], l.prototype, "_entries", 2);
n([
  r()
], l.prototype, "_forms", 2);
n([
  r()
], l.prototype, "_users", 2);
n([
  r()
], l.prototype, "_total", 2);
n([
  r()
], l.prototype, "_page", 2);
n([
  r()
], l.prototype, "_pageSize", 2);
n([
  r()
], l.prototype, "_loading", 2);
n([
  r()
], l.prototype, "_selectedFormId", 2);
n([
  r()
], l.prototype, "_selectedEventType", 2);
n([
  r()
], l.prototype, "_fromDate", 2);
n([
  r()
], l.prototype, "_toDate", 2);
n([
  r()
], l.prototype, "_selectedEntryId", 2);
n([
  r()
], l.prototype, "_selectedUserKey", 2);
n([
  r()
], l.prototype, "_datePreset", 2);
n([
  r()
], l.prototype, "_csvExportEnabled", 2);
l = n([
  m("forms-audit-dashboard")
], l);
x.forEach((e) => $.register(e));
