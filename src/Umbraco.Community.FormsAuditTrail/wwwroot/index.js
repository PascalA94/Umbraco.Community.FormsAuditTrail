import { umbExtensionsRegistry as ce } from "@umbraco-cms/backoffice/extension-registry";
import { UmbLitElement as D } from "@umbraco-cms/backoffice/lit-element";
import { css as E, property as S, state as a, customElement as C, html as s, nothing as u } from "@umbraco-cms/backoffice/external/lit";
import { UMB_AUTH_CONTEXT as A } from "@umbraco-cms/backoffice/auth";
import { UMB_CURRENT_USER_CONTEXT as de } from "@umbraco-cms/backoffice/current-user";
const he = [
  {
    type: "dashboard",
    alias: "UmbracoCommunity.FormsAuditTrail.Dashboard",
    name: "Audit",
    elementName: "forms-audit-root",
    weight: 20,
    meta: {
      label: "Audit",
      pathname: "forms-audit-trail"
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "Umb.Section.Forms"
      }
    ]
  }
], H = {
  Edited: { color: "warning", look: "primary" },
  Created: { color: "warning", look: "primary" },
  ConfigChanged: { color: "warning", look: "primary" },
  Exported: { color: "danger", look: "primary" },
  ExportDownloaded: { color: "danger", look: "primary" },
  WorkflowRetried: { color: "danger", look: "primary" },
  AuditGap: { color: "danger", look: "primary" },
  Deleted: { color: "danger", look: "outline" }
};
function R(e) {
  const t = Object.hasOwn(H, e) ? H[e] : void 0;
  return { label: e, ...t ?? { color: "default", look: "outline" } };
}
function pe(e) {
  return `${e} ${e === 1 ? "entry" : "entries"}`;
}
function V(e) {
  if (e.recordId) return e.recordId;
  const t = pe(e.recordCount);
  return e.recordIdsCaptured === !1 ? `${t} (ids not captured)` : t;
}
function K(e) {
  return e === 0 ? "kept indefinitely" : `kept for ${e} days`;
}
function fe(e) {
  return `Entry access is ${K(e.accessRetentionDays)}. Changed values are ${K(e.editValueRetentionDays)}.`;
}
const _e = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
function X(e) {
  const t = new Date(e);
  return Number.isNaN(t.getTime()) ? e : `${t.getUTCDate()} ${_e[t.getUTCMonth()]} ${t.getUTCFullYear()}`;
}
function b(e) {
  const t = new Date(e);
  if (Number.isNaN(t.getTime())) return e;
  const r = (i) => String(i).padStart(2, "0");
  return `${X(e)} ${r(t.getUTCHours())}:${r(t.getUTCMinutes())}`;
}
function j(e, t, r, i) {
  return r ? `Value removed on ${X(r)} (retention or erasure)` : t ? e ?? "[masked]" : i ? "Value cleared" : e ?? "";
}
function Z(e) {
  return e.currentUserName ? `${e.userName} (now: ${e.currentUserName})` : e.userName;
}
function ge(e, t) {
  switch (e) {
    case "writeFailures": {
      const r = t.writeFailuresSinceStart ?? 0, i = t.lastWriteFailureUtc ? ` since ${b(t.lastWriteFailureUtc)}` : "";
      return `${r} ${r === 1 ? "write" : "writes"} failed${i}`;
    }
    case "migrationsPending":
      return "database migrations are pending";
    case "jobNotRun":
      return "the daily retention and erasure job has not run in 48 hours";
    case "jobStepFailed":
      return "a step of the daily retention and erasure job failed";
    default:
      return "see the server log";
  }
}
function ye(e) {
  if (e.entryAuditHealthy !== !1) return null;
  const t = (e.healthReasons ?? []).map((i) => ge(i, e));
  return `Entry auditing has a problem: ${t.length > 0 ? t.join("; ") : "see the server log"}. Some activity may not have been recorded.`;
}
function be(e) {
  const t = e.toUtc ? b(e.toUtc) : "now";
  return `Entry auditing was off from ${e.startUnknown ? `some time before ${b(e.fromUtc)}` : b(e.fromUtc)} to ${t}`;
}
function me(e) {
  return e.foreignValueHandlers === null || e.foreignValueHandlers === void 0 ? "Not checked" : e.foreignValueHandlers ? "Found" : "None found";
}
const ve = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
function m(e) {
  return e !== null && ve.test(e) ? e.toLowerCase() : null;
}
const f = "/umbraco/formsaudit/api/v1";
async function $(e, t) {
  const r = {};
  t && (r.Authorization = `Bearer ${t}`);
  const i = await fetch(e, { credentials: "include", headers: r });
  if (!i.ok) throw new Error(`HTTP ${i.status}`);
  return i.json();
}
async function Q(e) {
  return $(`${f}/config`, e);
}
async function $e(e, t) {
  const r = new URLSearchParams();
  return e.formId && r.set("formId", e.formId), e.page && r.set("page", String(e.page)), e.pageSize && r.set("pageSize", String(e.pageSize)), e.userKey && r.set("userKey", e.userKey), e.eventType !== void 0 && r.set("eventType", String(e.eventType)), e.fromDate && r.set("fromDate", e.fromDate), e.toDate && r.set("toDate", e.toDate), $(`${f}/entries?${r}`, t);
}
async function xe(e, t) {
  return $(`${f}/entries/${e}`, t);
}
async function we(e) {
  return $(`${f}/forms`, e);
}
async function Te(e) {
  return $(`${f}/users`, e);
}
function Y(e, t) {
  const r = URL.createObjectURL(e), i = document.createElement("a");
  i.href = r, i.download = t, document.body.appendChild(i), i.click(), i.remove(), setTimeout(() => URL.revokeObjectURL(r), 0);
}
async function Ee(e, t) {
  const r = new URLSearchParams();
  e.formId && r.set("formId", e.formId), e.userKey && r.set("userKey", e.userKey), e.eventType !== void 0 && r.set("eventType", String(e.eventType)), e.fromDate && r.set("fromDate", e.fromDate), e.toDate && r.set("toDate", e.toDate);
  const i = {};
  t && (i.Authorization = `Bearer ${t}`);
  const o = await fetch(`${f}/entries/export?${r}`, { credentials: "include", headers: i });
  if (!o.ok) throw new Error(`HTTP ${o.status}`);
  Y(await o.blob(), `forms-audit-${(/* @__PURE__ */ new Date()).toISOString().slice(0, 10)}.csv`);
}
function ee(e) {
  const t = new URLSearchParams();
  return e.formId && t.set("formId", e.formId), e.userKey && t.set("userKey", e.userKey), e.eventType !== void 0 && t.set("eventType", String(e.eventType)), e.fromDate && t.set("fromDate", e.fromDate), e.toDate && t.set("toDate", e.toDate), e.recordId && t.set("recordId", e.recordId), t;
}
async function Ie(e, t) {
  const r = ee(e);
  return e.page && r.set("page", String(e.page)), e.pageSize && r.set("pageSize", String(e.pageSize)), $(`${f}/access?${r}`, t);
}
async function De(e, t) {
  return $(`${f}/access/${encodeURIComponent(String(e))}`, t);
}
async function Ce(e, t) {
  const r = m(e);
  if (r === null) throw new Error("Record id must be a GUID");
  const i = {};
  t && (i.Authorization = `Bearer ${t}`);
  const o = await fetch(`${f}/access/records/${encodeURIComponent(r)}`, { credentials: "include", headers: i });
  if (!o.ok) throw new Error(`HTTP ${o.status}`);
  return { items: await o.json(), truncatedHeader: o.headers?.get("X-FormsAuditTrail-Truncated") ?? null };
}
async function Se(e) {
  return $(`${f}/access/forms`, e);
}
async function te(e) {
  return $(`${f}/access/users`, e);
}
function Ae(e) {
  const t = ee(e).toString();
  return t ? `${f}/access/export?${t}` : `${f}/access/export`;
}
async function ke(e, t) {
  const r = {};
  t && (r.Authorization = `Bearer ${t}`);
  const i = await fetch(Ae(e), { credentials: "include", headers: r });
  if (!i.ok) throw new Error(`HTTP ${i.status}`);
  Y(await i.blob(), `forms-access-${(/* @__PURE__ */ new Date()).toISOString().slice(0, 10)}.csv`);
}
const Ue = "Entry auditing is switched off. Set FormsAuditTrail:EntryAudit:Enabled to true.", ie = (e) => `formsAuditTrail.gdprNotice.${e}`;
function Ne(e) {
  const t = e.storeEditValues;
  return t == null ? !0 : t === 0 || typeof t == "string" && t.toLowerCase() === "none";
}
function Re(e) {
  return Ne(e) ? null : e.foreignValueHandlers === !0 ? "Another package processes entry values. Edit values are stored unencrypted in the audit tables; consider StoreEditValues=None." : e.foreignValueHandlers === null || e.foreignValueHandlers === void 0 ? `Whether another package processes entry values: ${me(e)}.` : null;
}
function Pe(e, t) {
  if (!e) return !1;
  try {
    return e.getItem(ie(t)) !== null;
  } catch {
    return !1;
  }
}
function Fe(e, t) {
  if (t !== "")
    try {
      e?.setItem(ie(t), "1");
    } catch {
    }
}
function z(e, t, r, i) {
  const o = e.canReadAccessLog === !0, n = o ? ["formChanges", "entryAccess", "entryHistory"] : ["formChanges"], l = o ? m(t.searchParams.get("recordId")) : null, k = [];
  if (o) {
    const O = ye(e);
    O && k.push(O);
    for (const ue of e.offPeriods ?? []) k.push(be(ue));
    const L = Re(e);
    L && k.push(L);
  }
  return {
    tabs: n,
    initialTab: l ? "entryHistory" : "formChanges",
    offMessage: o && e.entryAuditEnabled === !1 ? Ue : null,
    bannerLines: k,
    // Not shown until the current user key has resolved: the dismissal is stored per user.
    showGdprNotice: o && e.entryAuditEnabled === !0 && i !== "" && !Pe(r, i),
    deepLinkRecordId: l
  };
}
function Oe(e) {
  return new CustomEvent("open-record-history", { detail: { recordId: e }, bubbles: !0, composed: !0 });
}
function Le(e) {
  const t = e?.recordId;
  return typeof t == "string" ? m(t) : null;
}
async function q(e, t, r) {
  await e.updateComplete;
  const i = t();
  await i?.updateComplete, i?.focusHeading?.() !== !0 && r()?.focus();
}
function re(e) {
  const t = e?.querySelector("#heading");
  return t?.focus(), t != null;
}
async function P(e) {
  return await e?.getLatestToken() ?? void 0;
}
const F = E`
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
var He = Object.defineProperty, Ve = Object.getOwnPropertyDescriptor, x = (e, t, r, i) => {
  for (var o = i > 1 ? void 0 : i ? Ve(t, r) : t, n = e.length - 1, l; n >= 0; n--)
    (l = e[n]) && (o = (i ? l(t, r, o) : l(o)) || o);
  return i && o && He(t, r, o), o;
};
let y = class extends D {
  constructor() {
    super(...arguments), this._loading = !1, this._showRaw = !1, this._focusHeading = !1;
  }
  async connectedCallback() {
    super.connectedCallback(), this.consumeContext(A, async (e) => {
      e && (this._authContext = e, this.accessId !== void 0 ? await this._loadAccess() : this.entryId !== void 0 && await this._load());
    });
  }
  updated(e) {
    this._authContext !== void 0 && (e.has("accessId") && this.accessId !== void 0 ? this._loadAccess() : e.has("entryId") && this.entryId !== void 0 && this.accessId === void 0 && this._load(), this._focusHeading && this._access && (this._focusHeading = !1, this.shadowRoot?.querySelector("#access-heading")?.focus()));
  }
  async _loadAccess() {
    if (this.accessId === void 0) return;
    const e = this.accessId;
    this._loading = !0, this._error = void 0, this._access = void 0;
    try {
      const t = await this._authContext?.getLatestToken() ?? void 0, r = await De(e, t);
      if (e !== this.accessId) return;
      this._access = r, this._focusHeading = !0;
    } catch (t) {
      this._error = String(t), console.error("[audit-diff-viewer] Failed to load access entry", e, t);
    } finally {
      this._loading = !1;
    }
  }
  _renderAccess(e) {
    return s`
      <h3 id="access-heading" tabindex="-1">Changes to ${e.formName}, ${b(e.timestamp)} UTC by ${e.userName}</h3>
      ${e.changes.length === 0 ? s`<p>No field values were stored for this edit.</p>` : s`
            <uui-table aria-label="Field changes">
              <uui-table-head>
                <uui-table-head-cell>Field</uui-table-head-cell>
                <uui-table-head-cell>Before</uui-table-head-cell>
                <uui-table-head-cell>After</uui-table-head-cell>
              </uui-table-head>
              ${e.changes.map((t) => s`
                <uui-table-row>
                  <uui-table-cell>${t.fieldCaption}</uui-table-cell>
                  <uui-table-cell>${j(t.oldValue, t.masked, t.valuesScrubbedAt, !1)}</uui-table-cell>
                  <uui-table-cell>${j(t.newValue, t.masked, t.valuesScrubbedAt, t.cleared)}</uui-table-cell>
                </uui-table-row>
              `)}
            </uui-table>
          `}
    `;
  }
  async _load() {
    if (this.entryId !== void 0) {
      this._loading = !0, this._error = void 0;
      try {
        const e = await this._authContext?.getLatestToken() ?? void 0;
        this._entry = await xe(this.entryId, e);
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
      const r = t.category ?? "Other", i = e.get(r) ?? [];
      i.push(t), e.set(r, i);
    }
    return e;
  }
  render() {
    return s`
      <div role="status" aria-live="polite" class="sr-only">${this._loading ? "Loading changes" : ""}</div>
      ${this._loading ? s`<uui-loader></uui-loader>` : this._body()}
    `;
  }
  _body() {
    if (this._error) return s`<uui-tag color="danger" role="alert">Error: ${this._error}</uui-tag>`;
    if (this.accessId !== void 0)
      return this._access ? this._renderAccess(this._access) : s`<p>Loading changes…</p>`;
    if (!this._entry) return s`<p>Loading entry ${this.entryId}…</p>`;
    const e = this._entry, t = this._groupByCategory();
    return s`
      <p><strong>Date:</strong> ${new Date(e.timestamp).toLocaleString()} &nbsp; <strong>User:</strong> ${e.userName}</p>

      ${t.size === 0 ? s`<uui-box><p>No field-level changes recorded.</p></uui-box>` : Array.from(t.entries()).map(([r, i]) => s`
            <uui-box headline=${r}>
              ${i.map((o) => s`
                <div class="change-row">
                  <uui-tag look=${this._badgeColor(o.changeType)} color=${this._badgeColor(o.changeType)}>${o.changeType}</uui-tag>
                  <span class="description">${o.friendlyDescription}</span>
                  ${o.oldValue || o.newValue ? s`
                    <span class="values">
                      ${o.oldValue ? s`<del>${o.oldValue}</del>` : ""}
                      ${o.oldValue && o.newValue ? s` → ` : ""}
                      ${o.newValue ? s`<ins>${o.newValue}</ins>` : ""}
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
y.styles = [F, E`
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
x([
  S({ type: Number })
], y.prototype, "entryId", 2);
x([
  S({ type: Number })
], y.prototype, "accessId", 2);
x([
  a()
], y.prototype, "_access", 2);
x([
  a()
], y.prototype, "_entry", 2);
x([
  a()
], y.prototype, "_loading", 2);
x([
  a()
], y.prototype, "_showRaw", 2);
x([
  a()
], y.prototype, "_error", 2);
y = x([
  C("audit-diff-viewer")
], y);
var Ke = Object.defineProperty, je = Object.getOwnPropertyDescriptor, p = (e, t, r, i) => {
  for (var o = i > 1 ? void 0 : i ? je(t, r) : t, n = e.length - 1, l; n >= 0; n--)
    (l = e[n]) && (o = (i ? l(t, r, o) : l(o)) || o);
  return i && o && Ke(t, r, o), o;
};
const M = [
  { value: "", label: "All time", days: void 0 },
  { value: "today", label: "Today", days: 0 },
  { value: "7", label: "Last 7 days", days: 7 },
  { value: "30", label: "Last 30 days", days: 30 },
  { value: "custom", label: "Custom", days: void 0 }
];
function ze(e) {
  const t = /* @__PURE__ */ new Date();
  return t.setDate(t.getDate() - e), t.toISOString().slice(0, 10);
}
let d = class extends D {
  constructor() {
    super(...arguments), this._entries = [], this._forms = [], this._users = [], this._total = 0, this._page = 1, this._pageSize = 20, this._loading = !1, this._selectedFormId = "", this._selectedEventType = "", this._fromDate = "", this._toDate = "", this._selectedUserKey = "", this._datePreset = "", this._csvExportEnabled = !0;
  }
  async connectedCallback() {
    super.connectedCallback(), this.consumeContext(A, async (e) => {
      if (!e) return;
      this._authContext = e;
      const t = await this._getToken(), [r, i, o] = await Promise.all([we(t), Te(t), Q(t)]);
      this._forms = r, this._users = i, this._csvExportEnabled = o.csvExportEnabled, await this._load();
    });
  }
  _onPresetChange(e) {
    if (this._datePreset = e, e === "custom") return;
    const t = M.find((r) => r.value === e);
    this._fromDate = t?.days !== void 0 ? ze(t.days) : "", this._toDate = "", this._onFilterChange();
  }
  async _getToken() {
    return P(this._authContext);
  }
  async _load() {
    this._loading = !0;
    try {
      const e = await this._getToken(), t = await $e({
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
              ${M.map((e) => s`<option value=${e.value} ?selected=${e.value === this._datePreset}>${e.label}</option>`)}
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
      await Ee({
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
                      <uui-table-cell>${e.changeSummary ?? "None"}</uui-table-cell>
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
d.styles = E`
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
p([
  a()
], d.prototype, "_entries", 2);
p([
  a()
], d.prototype, "_forms", 2);
p([
  a()
], d.prototype, "_users", 2);
p([
  a()
], d.prototype, "_total", 2);
p([
  a()
], d.prototype, "_page", 2);
p([
  a()
], d.prototype, "_pageSize", 2);
p([
  a()
], d.prototype, "_loading", 2);
p([
  a()
], d.prototype, "_selectedFormId", 2);
p([
  a()
], d.prototype, "_selectedEventType", 2);
p([
  a()
], d.prototype, "_fromDate", 2);
p([
  a()
], d.prototype, "_toDate", 2);
p([
  a()
], d.prototype, "_selectedEntryId", 2);
p([
  a()
], d.prototype, "_selectedUserKey", 2);
p([
  a()
], d.prototype, "_datePreset", 2);
p([
  a()
], d.prototype, "_csvExportEnabled", 2);
d = p([
  C("forms-audit-dashboard")
], d);
const qe = [
  {
    value: 10,
    name: "ListViewed"
  },
  {
    value: 11,
    name: "DetailViewed"
  },
  {
    value: 12,
    name: "Edited"
  },
  {
    value: 13,
    name: "Deleted"
  },
  {
    value: 14,
    name: "Exported"
  },
  {
    value: 15,
    name: "ExportDownloaded"
  },
  {
    value: 16,
    name: "AuditExported"
  },
  {
    value: 17,
    name: "AuditViewed"
  },
  {
    value: 18,
    name: "FileViewed"
  },
  {
    value: 19,
    name: "AuditSearched"
  },
  {
    value: 20,
    name: "WorkflowRetried"
  },
  {
    value: 21,
    name: "Created"
  },
  {
    value: 22,
    name: "MemberSummaryViewed"
  },
  {
    value: 23,
    name: "ConfigChanged"
  },
  {
    value: 24,
    name: "AuditGap"
  }
];
function oe(e) {
  if (!e) return null;
  try {
    const t = JSON.parse(e);
    return t !== null && typeof t == "object" && !Array.isArray(t) ? t : null;
  } catch {
    return null;
  }
}
function w(e, t) {
  const r = Object.keys(e).find((i) => i.toLowerCase() === t.toLowerCase());
  return r === void 0 ? void 0 : e[r];
}
function T(e) {
  return typeof e == "string" || typeof e == "number" ? String(e) : null;
}
function B(e) {
  return e.replace(/([a-z])([A-Z])/g, "$1 $2").toLowerCase();
}
function Me(e) {
  if (e === null || typeof e != "object" || Array.isArray(e)) return null;
  const t = [];
  for (const [r, i] of Object.entries(e))
    i === !0 ? t.push(B(r)) : T(i) !== null && t.push(`${B(r)}: ${T(i)}`);
  return t.length > 0 ? t.join(", ") : null;
}
function Be(e) {
  const t = oe(e.detail);
  if (!t) return [];
  const r = [], i = Me(w(t, "filter"));
  i && r.push(`Filter: ${i}`);
  const o = T(w(t, "exportType"));
  o && r.push(`Export type: ${o}`);
  const n = T(w(t, "fileName"));
  n && r.push(`File name: ${n}`);
  const l = T(w(t, "page"));
  return l && r.push(`Page: ${l}`), w(t, "truncated") === !0 && r.push("Detail was truncated"), r;
}
function Ge(e) {
  const t = oe(e.detail);
  if (!t) return null;
  const r = T(w(t, "subjectUserKey")), i = w(t, "eventType"), n = ae.find((l) => l.value === i)?.label ?? T(i);
  return r || n ? { userKey: r, eventType: n } : null;
}
function We(e, t) {
  const r = e.find((i) => i.userKey.toLowerCase() === t.toLowerCase());
  return r ? r.currentUserName ?? r.userName : t;
}
function se(e, t) {
  const r = Be(e), i = e.eventType === "AuditSearched" || e.eventType === "AuditExported" ? Ge(e) : null;
  return i?.userKey && r.push(`Filtered by user: ${We(t, i.userKey)}`), i?.eventType && r.push(`Filtered by event type: ${i.eventType}`), r;
}
function Je(e) {
  return typeof e == "string" && e.split(",").some((t) => t.trim() !== "");
}
function Xe(e, t) {
  return { items: e, truncated: Je(t) };
}
const ae = qe.map((e) => ({
  value: e.value,
  label: e.name
})), ne = "Enter an entry id as a GUID, for example 3f2504e0-4f89-11d3-9a0c-0305e82c3301.", Ze = "The From date must not be later than the To date.";
function Qe(e) {
  return {
    recordId: e.recordId !== "" && m(e.recordId) === null ? ne : null,
    // ISO yyyy-mm-dd strings sort chronologically.
    range: e.fromDate !== "" && e.toDate !== "" && e.fromDate > e.toDate ? Ze : null
  };
}
function G(e) {
  return e.recordId !== null || e.range !== null;
}
var Ye = Object.defineProperty, et = Object.getOwnPropertyDescriptor, h = (e, t, r, i) => {
  for (var o = i > 1 ? void 0 : i ? et(t, r) : t, n = e.length - 1, l; n >= 0; n--)
    (l = e[n]) && (o = (i ? l(t, r, o) : l(o)) || o);
  return i && o && Ye(t, r, o), o;
};
const W = 20;
let c = class extends D {
  constructor() {
    super(...arguments), this._rows = [], this._forms = [], this._users = [], this._total = 0, this._page = 1, this._loading = !1, this._formId = "", this._userKey = "", this._eventType = "", this._fromDate = "", this._toDate = "", this._recordId = "", this._exporting = !1, this._requestId = 0;
  }
  connectedCallback() {
    super.connectedCallback(), this.consumeContext(A, async (e) => {
      if (!e) return;
      this._authContext = e;
      const t = await this._token();
      try {
        [this._forms, this._users] = await Promise.all([Se(t), te(t)]);
      } catch (r) {
        console.error("[audit-access] Filter lists failed", r);
      }
      await this._load();
    });
  }
  _token() {
    return P(this._authContext);
  }
  _filters() {
    return {
      formId: this._formId || void 0,
      userKey: this._userKey || void 0,
      eventType: this._eventType !== "" ? Number(this._eventType) : void 0,
      fromDate: this._fromDate || void 0,
      toDate: this._toDate || void 0,
      recordId: m(this._recordId) ?? void 0
    };
  }
  async _load() {
    const e = ++this._requestId;
    this._loading = !0, this._error = void 0;
    try {
      const t = await Ie({ ...this._filters(), page: this._page, pageSize: W }, await this._token());
      if (e !== this._requestId) return;
      this._rows = t.items, this._total = t.total;
    } catch (t) {
      if (e !== this._requestId) return;
      this._rows = [], this._total = 0, this._error = "The entry access log could not be loaded.", console.error("[audit-access] Load failed", t);
    } finally {
      e === this._requestId && (this._loading = !1);
    }
  }
  _filterChanged() {
    this._page = 1, this._selectedAccessId = void 0, this._load();
  }
  _onRecordIdChange(e) {
    this._recordId = e.trim(), this._issues().recordId || this._filterChanged();
  }
  focusHeading() {
    return re(this.shadowRoot);
  }
  _issues() {
    return Qe({ recordId: this._recordId, fromDate: this._fromDate, toDate: this._toDate });
  }
  _openHistory(e) {
    this.dispatchEvent(Oe(e));
  }
  _entry(e) {
    const t = e.recordId ? m(e.recordId) : null;
    return t ? s`<button
      type="button"
      class="link-button"
      aria-label=${`Open history for entry ${t}`}
      @click=${() => this._openHistory(t)}>
      ${V(e)}
    </button>` : V(e);
  }
  _detail(e) {
    if (e.eventType === "Edited")
      return s`<uui-button
        look="outline"
        compact
        label=${`View changes to ${e.formName} at ${b(e.timestamp)} UTC`}
        @click=${() => this._selectedAccessId = e.id}
        >View changes</uui-button
      >`;
    const t = se(e, this._users);
    return t.length === 0 ? u : s`<ul class="lines">${t.map((r) => s`<li>${r}</li>`)}</ul>`;
  }
  async _export() {
    if (!(G(this._issues()) || this._exporting)) {
      this._exporting = !0;
      try {
        await ke(this._filters(), await this._token());
      } catch (e) {
        this._error = "The CSV export failed.", console.error("[audit-access] CSV export failed", e);
      } finally {
        this._exporting = !1;
      }
    }
  }
  render() {
    const e = Math.max(1, Math.ceil(this._total / W)), t = this._issues(), r = this._exporting ? "Preparing the CSV export" : this._loading ? "Loading entry access" : `${this._total} matching rows`;
    return s`
      <uui-box>
        <h2 slot="headline" id="heading" tabindex="-1">Entry access</h2>
        ${this.config ? s`<p class="muted">${fe(this.config)}</p>` : u}
        <div class="filters" role="search" aria-label="Entry access filters">
          <label class="field">Form
            <select @change=${(i) => {
      this._formId = i.target.value, this._filterChanged();
    }}>
              <option value="">All forms</option>
              ${this._forms.map((i) => s`<option value=${i.formId} ?selected=${i.formId === this._formId}>${i.formName}</option>`)}
            </select>
          </label>
          <label class="field">User
            <select @change=${(i) => {
      this._userKey = i.target.value, this._filterChanged();
    }}>
              <option value="">All users</option>
              ${this._users.map((i) => s`<option value=${i.userKey} ?selected=${i.userKey === this._userKey}>${i.currentUserName ?? i.userName}</option>`)}
            </select>
          </label>
          <label class="field">Event
            <select @change=${(i) => {
      this._eventType = i.target.value, this._filterChanged();
    }}>
              <option value="">All events</option>
              ${ae.map((i) => s`<option value=${String(i.value)} ?selected=${String(i.value) === this._eventType}>${R(i.label).label}</option>`)}
            </select>
          </label>
          <label class="field">From
            <input type="date" .value=${this._fromDate}
              aria-invalid=${t.range ? "true" : "false"}
              aria-describedby=${t.range ? "range-error" : u}
              @change=${(i) => {
      this._fromDate = i.target.value, this._filterChanged();
    }} />
          </label>
          <label class="field">To
            <input type="date" .value=${this._toDate}
              aria-invalid=${t.range ? "true" : "false"}
              aria-describedby=${t.range ? "range-error" : u}
              @change=${(i) => {
      this._toDate = i.target.value, this._filterChanged();
    }} />
          </label>
          <label class="field">Entry id
            <input type="text" .value=${this._recordId} spellcheck="false" autocomplete="off"
              aria-invalid=${t.recordId ? "true" : "false"}
              aria-describedby=${t.recordId ? "record-id-error" : u}
              @change=${(i) => this._onRecordIdChange(i.target.value)} />
          </label>
          ${this.config?.entryAuditCsvEnabled ? s`<uui-button class="push-right" look="outline" label="Export CSV"
                ?disabled=${G(t)} state=${this._exporting ? "waiting" : u}
                @click=${() => this._export()}>Export CSV</uui-button>` : u}
        </div>
        ${t.recordId ? s`<p id="record-id-error" class="error" role="alert">${t.recordId}</p>` : u}
        ${t.range ? s`<p id="range-error" class="error" role="alert">${t.range}</p>` : u}
        ${this._error ? s`<p class="error" role="alert">${this._error}</p>` : u}

        <div role="status" aria-live="polite">
          <span class="sr-only">${r}</span>
          ${!this._loading && this._rows.length === 0 ? s`<div class="empty-state"><uui-icon name="icon-info"></uui-icon><p>No entry access recorded for these filters.</p></div>` : u}
        </div>

        <!-- The table and pager stay mounted while loading, so focus on the pager is not lost on a page change. -->
        ${this._loading ? s`<uui-loader></uui-loader>` : u}
        ${this._rows.length > 0 ? s`
              <uui-table aria-label="Entry access log" aria-busy=${this._loading ? "true" : "false"}>
                <uui-table-head>
                  <uui-table-head-cell>When (UTC)</uui-table-head-cell>
                  <uui-table-head-cell>User</uui-table-head-cell>
                  <uui-table-head-cell>Event</uui-table-head-cell>
                  <uui-table-head-cell>Form</uui-table-head-cell>
                  <uui-table-head-cell>Entry</uui-table-head-cell>
                  <uui-table-head-cell>Detail</uui-table-head-cell>
                </uui-table-head>
                ${this._rows.map((i) => {
      const o = R(i.eventType);
      return s`
                    <uui-table-row>
                      <uui-table-cell>${b(i.timestamp)}</uui-table-cell>
                      <uui-table-cell>${Z(i)}</uui-table-cell>
                      <uui-table-cell><uui-tag color=${o.color} look=${o.look}>${o.label}</uui-tag></uui-table-cell>
                      <uui-table-cell>${i.formName}</uui-table-cell>
                      <uui-table-cell>${this._entry(i)}</uui-table-cell>
                      <uui-table-cell>${this._detail(i)}</uui-table-cell>
                    </uui-table-row>
                  `;
    })}
              </uui-table>
            ` : u}
        ${this._total > 0 ? s`<uui-pagination
              label="Entry access pages"
              .total=${e}
              .current=${this._page}
              @change=${(i) => {
      this._page = i.target.current, this._selectedAccessId = void 0, this._load();
    }}
            ></uui-pagination>` : u}
      </uui-box>

      ${this._selectedAccessId !== void 0 ? s`<uui-box headline="Change detail"><audit-diff-viewer .accessId=${this._selectedAccessId}></audit-diff-viewer></uui-box>` : u}
    `;
  }
};
c.styles = [
  F,
  E`
      uui-box + uui-box { margin-top: 16px; }
      .link-button { color: var(--uui-color-interactive); }
      .link-button:focus-visible { outline: 2px solid var(--uui-color-focus); outline-offset: 2px; }
      .link-button {
        background: none;
        border: 0;
        padding: 0;
        min-block-size: 24px;
        display: inline-flex;
        align-items: center;
        font: inherit;
        text-decoration: underline;
        cursor: pointer;
        text-align: start;
      }
      .link-button:hover { color: var(--uui-color-interactive-emphasis); }
      @media (forced-colors: active) {
        .link-button { color: LinkText; }
        .link-button:hover { color: LinkText; text-decoration-thickness: 2px; }
        .link-button:focus-visible { outline-color: Highlight; }
      }
    `
];
h([
  S({ attribute: !1 })
], c.prototype, "config", 2);
h([
  a()
], c.prototype, "_rows", 2);
h([
  a()
], c.prototype, "_forms", 2);
h([
  a()
], c.prototype, "_users", 2);
h([
  a()
], c.prototype, "_total", 2);
h([
  a()
], c.prototype, "_page", 2);
h([
  a()
], c.prototype, "_loading", 2);
h([
  a()
], c.prototype, "_error", 2);
h([
  a()
], c.prototype, "_formId", 2);
h([
  a()
], c.prototype, "_userKey", 2);
h([
  a()
], c.prototype, "_eventType", 2);
h([
  a()
], c.prototype, "_fromDate", 2);
h([
  a()
], c.prototype, "_toDate", 2);
h([
  a()
], c.prototype, "_recordId", 2);
h([
  a()
], c.prototype, "_selectedAccessId", 2);
h([
  a()
], c.prototype, "_exporting", 2);
c = h([
  C("forms-audit-entry-access")
], c);
var tt = Object.defineProperty, it = Object.getOwnPropertyDescriptor, g = (e, t, r, i) => {
  for (var o = i > 1 ? void 0 : i ? it(t, r) : t, n = e.length - 1, l; n >= 0; n--)
    (l = e[n]) && (o = (i ? l(t, r, o) : l(o)) || o);
  return i && o && tt(t, r, o), o;
};
const rt = "No recorded access for this entry, or you don't have access to its form.", ot = "Exports from some exporters don't record which entries they contained. See the Entry access log for all exports of this form.";
let _ = class extends D {
  constructor() {
    super(...arguments), this.recordId = "", this._input = "", this._inputError = !1, this._rows = [], this._truncated = !1, this._loading = !1, this._searched = !1, this._users = [], this._requestId = 0;
  }
  connectedCallback() {
    super.connectedCallback(), this.consumeContext(A, async (e) => {
      if (e) {
        this._authContext = e;
        try {
          this._users = await te(await this._token());
        } catch (t) {
          console.error("[audit-history] User list failed", t);
        }
        m(this.recordId) && await this._lookUp(this.recordId);
      }
    });
  }
  willUpdate(e) {
    if (e.has("recordId")) {
      const t = m(this.recordId);
      this._input = t ?? "", t && this._authContext && this._lookUp(t);
    }
  }
  focusHeading() {
    return re(this.shadowRoot);
  }
  _token() {
    return P(this._authContext);
  }
  async _lookUp(e) {
    const t = ++this._requestId;
    this._loading = !0, this._message = void 0, this._truncated = !1, this._selectedAccessId = void 0, this._searched = !0;
    try {
      const r = await Ce(e, await this._token()), i = Xe(r.items, r.truncatedHeader);
      if (t !== this._requestId) return;
      this._rows = i.items, this._truncated = i.truncated;
    } catch (r) {
      if (t !== this._requestId) return;
      this._rows = [], this._message = String(r).includes("HTTP 404") ? rt : "The entry history could not be loaded.", String(r).includes("HTTP 404") || console.error("[audit-history] Load failed", r);
    } finally {
      t === this._requestId && (this._loading = !1);
    }
  }
  _submit(e) {
    e.preventDefault();
    const t = m(this._input.trim());
    this._inputError = t === null, t && this._lookUp(t);
  }
  _detail(e) {
    if (e.eventType === "Edited")
      return s`<uui-button
        look="outline"
        compact
        label=${`View changes at ${b(e.timestamp)} UTC`}
        @click=${() => this._selectedAccessId = e.id}
        >View changes</uui-button
      >`;
    const t = se(e, this._users);
    return t.length === 0 ? u : s`<ul class="lines">${t.map((r) => s`<li>${r}</li>`)}</ul>`;
  }
  render() {
    return s`
      <uui-box>
        <h2 slot="headline" id="heading" tabindex="-1">Entry history</h2>
        <form class="filters" @submit=${(e) => this._submit(e)}>
          <label class="field">Entry id
            <input id="record-id" type="text" .value=${this._input} spellcheck="false" autocomplete="off"
              aria-invalid=${this._inputError ? "true" : "false"}
              aria-describedby=${this._inputError ? "record-id-error" : u}
              @input=${(e) => this._input = e.target.value} />
          </label>
          <uui-button type="submit" look="primary" label="Show history">Show history</uui-button>
        </form>
        ${this._inputError ? s`<p id="record-id-error" class="error" role="alert">${ne}</p>` : u}

        <div role="status" aria-live="polite">
          ${this._loading ? s`<span class="sr-only">Loading entry history</span>` : u}
          ${this._message ? s`<p class="error">${this._message}</p>` : u}
          <p class="note">${this._truncated ? "This history is incomplete: the server limited how many rows it returned. Use the Entry access log to see the rest." : ""}</p>
          ${!this._loading && !this._searched ? s`<div class="empty-state"><p>Enter an entry id to see who has viewed, changed or exported it.</p></div>` : u}
        </div>

        ${this._loading ? s`<uui-loader></uui-loader>` : this._rows.length > 0 ? s`
                <uui-table aria-label="Entry history">
                  <uui-table-head>
                    <uui-table-head-cell>When (UTC)</uui-table-head-cell>
                    <uui-table-head-cell>User</uui-table-head-cell>
                    <uui-table-head-cell>Event</uui-table-head-cell>
                    <uui-table-head-cell>Form</uui-table-head-cell>
                    <uui-table-head-cell>Detail</uui-table-head-cell>
                  </uui-table-head>
                  ${this._rows.map((e) => {
      const t = R(e.eventType);
      return s`
                      <uui-table-row>
                        <uui-table-cell>${b(e.timestamp)}</uui-table-cell>
                        <uui-table-cell>${Z(e)}</uui-table-cell>
                        <uui-table-cell><uui-tag color=${t.color} look=${t.look}>${t.label}</uui-tag></uui-table-cell>
                        <uui-table-cell>${e.formName}</uui-table-cell>
                        <uui-table-cell>${this._detail(e)}</uui-table-cell>
                      </uui-table-row>
                    `;
    })}
                </uui-table>
                <p class="note">${ot}</p>
              ` : u}
      </uui-box>

      ${this._selectedAccessId !== void 0 ? s`<uui-box headline="Change detail"><audit-diff-viewer .accessId=${this._selectedAccessId}></audit-diff-viewer></uui-box>` : u}
    `;
  }
};
_.styles = [F, E`uui-box + uui-box { margin-top: 16px; }`];
g([
  S({ type: String })
], _.prototype, "recordId", 2);
g([
  a()
], _.prototype, "_input", 2);
g([
  a()
], _.prototype, "_inputError", 2);
g([
  a()
], _.prototype, "_rows", 2);
g([
  a()
], _.prototype, "_truncated", 2);
g([
  a()
], _.prototype, "_loading", 2);
g([
  a()
], _.prototype, "_message", 2);
g([
  a()
], _.prototype, "_searched", 2);
g([
  a()
], _.prototype, "_users", 2);
g([
  a()
], _.prototype, "_selectedAccessId", 2);
_ = g([
  C("forms-audit-entry-history")
], _);
var st = Object.defineProperty, at = Object.getOwnPropertyDescriptor, le = (e, t, r, i) => {
  for (var o = i > 1 ? void 0 : i ? at(t, r) : t, n = e.length - 1, l; n >= 0; n--)
    (l = e[n]) && (o = (i ? l(t, r, o) : l(o)) || o);
  return i && o && st(t, r, o), o;
};
let U = class extends D {
  constructor() {
    super(...arguments), this.lines = [];
  }
  render() {
    return this.lines.length === 0 ? u : s`
      <uui-box role="alert">
        <ul>
          ${this.lines.map((e) => s`<li>${e}</li>`)}
        </ul>
      </uui-box>
    `;
  }
};
U.styles = E`
    :host { display: block; margin-bottom: 16px; }
    uui-box {
      border-left: 4px solid var(--uui-color-danger);
      background: var(--uui-color-surface);
    }
    ul { margin: 0; padding-left: 20px; }
    li { color: var(--uui-color-danger-standalone, var(--uui-color-danger)); font-weight: 600; }
  `;
le([
  S({ attribute: !1 })
], U.prototype, "lines", 2);
U = le([
  C("audit-health-banner")
], U);
var nt = Object.defineProperty, lt = Object.getOwnPropertyDescriptor, I = (e, t, r, i) => {
  for (var o = i > 1 ? void 0 : i ? lt(t, r) : t, n = e.length - 1, l; n >= 0; n--)
    (l = e[n]) && (o = (i ? l(t, r, o) : l(o)) || o);
  return i && o && nt(t, r, o), o;
};
const J = {
  formChanges: "Form changes",
  entryAccess: "Entry access",
  entryHistory: "Entry history"
}, ut = "Records backoffice activity only. Changes made by code, workflows, the Delivery API or directly in the database are not captured, and some Forms screens are not recorded. See the README.", ct = "https://github.com/PascalA94/Umbraco.Community.FormsAuditTrail#gdpr";
function N() {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}
let v = class extends D {
  constructor() {
    super(...arguments), this._userKey = "", this._historyRecordId = "", this._noticeDismissed = !1, this._focusTarget = null, this._onOpenHistory = (e) => {
      const t = Le(e.detail);
      t && (this._historyRecordId = t, this._activeTab = "entryHistory", this._focusTarget = "panel");
    };
  }
  connectedCallback() {
    super.connectedCallback(), this.consumeContext(A, async (e) => {
      if (e)
        try {
          this._config = await Q(await e.getLatestToken() ?? void 0);
          const t = z(this._config, new URL(window.location.href), N(), this._userKey);
          this._activeTab ??= t.initialTab, t.deepLinkRecordId && (this._historyRecordId = t.deepLinkRecordId);
        } catch (t) {
          this._error = "The audit dashboard could not be loaded.", console.error("[audit-root] Config load failed", t);
        }
    }), this.consumeContext(de, (e) => {
      e && this.observe(e.unique, (t) => this._userKey = t ?? "", "_observeCurrentUserKey");
    });
  }
  _dismissNotice() {
    Fe(N(), this._userKey), this._noticeDismissed = !0, this._focusTarget = "tab";
  }
  updated() {
    const e = this._focusTarget;
    if (!e) return;
    this._focusTarget = null;
    const t = () => this.shadowRoot?.querySelector("uui-tab[active]"), r = () => this.shadowRoot?.querySelector('[role="tabpanel"]:not([hidden])');
    if (e === "tab") {
      q(this, () => null, t);
      return;
    }
    q(this, () => r()?.firstElementChild, r);
  }
  _panel(e) {
    switch (e) {
      case "entryAccess":
        return s`<forms-audit-entry-access .config=${this._config}></forms-audit-entry-access>`;
      case "entryHistory":
        return s`<forms-audit-entry-history .recordId=${this._historyRecordId}></forms-audit-entry-history>`;
      default:
        return s`<forms-audit-dashboard></forms-audit-dashboard>`;
    }
  }
  render() {
    if (this._error) return s`<uui-box><p class="error" role="alert">${this._error}</p></uui-box>`;
    if (!this._config) return s`<uui-loader></uui-loader>`;
    const e = z(this._config, new URL(window.location.href), N(), this._userKey), t = this._activeTab && e.tabs.includes(this._activeTab) ? this._activeTab : e.initialTab, r = e.showGdprNotice && !this._noticeDismissed;
    return s`
      <audit-health-banner .lines=${e.bannerLines}></audit-health-banner>

      ${e.offMessage ? s`<uui-box class="gap" role="status"><p>${e.offMessage}</p></uui-box>` : u}

      ${r ? s`
            <uui-box class="gap" role="status">
              <div class="notice">
                <span>Entry auditing is now on. <a href=${ct} target="_blank" rel="noopener noreferrer">Read the GDPR notes.</a></span>
                <uui-button look="outline" label="Dismiss the GDPR notice" @click=${() => this._dismissNotice()}>Dismiss</uui-button>
              </div>
            </uui-box>
          ` : u}

      <uui-tab-group class="tabs" label="Audit sections">
        ${e.tabs.map(
      (i) => s`
            <uui-tab
              id=${`tab-${i}`}
              label=${J[i]}
              ?active=${i === t}
              @click=${() => this._activeTab = i}
              >${J[i]}</uui-tab
            >
          `
    )}
      </uui-tab-group>

      <!-- The active tab is not in the URL, and only the active panel is mounted, so filters reset on a tab change. -->
      ${e.tabs.map(
      (i) => s`
          <div
            id=${`panel-${i}`}
            role="tabpanel"
            tabindex="0"
            aria-labelledby=${`tab-${i}`}
            ?hidden=${i !== t}
            @open-record-history=${this._onOpenHistory}
          >
            ${i === t ? this._panel(i) : u}
          </div>
        `
    )}

      <p class="footnote">${ut}</p>
    `;
  }
};
v.styles = E`
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
I([
  a()
], v.prototype, "_config", 2);
I([
  a()
], v.prototype, "_error", 2);
I([
  a()
], v.prototype, "_userKey", 2);
I([
  a()
], v.prototype, "_activeTab", 2);
I([
  a()
], v.prototype, "_historyRecordId", 2);
I([
  a()
], v.prototype, "_noticeDismissed", 2);
v = I([
  C("forms-audit-root")
], v);
he.forEach((e) => ce.register(e));
