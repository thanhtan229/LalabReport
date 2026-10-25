# QA Report: LAN Phase 2 Batch 3 Gate Verification

- **Repository:** `D:\___TOOLS\__TINIX\LalabReport`
- **Role:** 05 — QA & Integration
- **Governing Reference:** `docs/PLAN_LAN_PHASE2.md` (§4, §5.6, §6 B3), `docs/adr/ADR-002-lan-authoritative-server.md`
- **Date:** 2026-10-26
- **Test Environment:** Windows 11 x64, .NET 8.0 SDK, Google Chrome 155 (Headless, 390x844 mobile viewport)
- **Safety Mandate:** Disposable database copies under `%TEMP%`; live production DB untouched; no service install; no deploy.
- **Overall Result:** **QA PASS** (All gate criteria 1–5 PASS; Windows Service Session 0 marked **NOT VERIFIED** per safety mandate)

---

## 1. Executive Summary

An independent gate verification of **LAN Phase 2 Batch 3** was performed against `docs/PLAN_LAN_PHASE2.md` §4, §5.6, and §6 B3. All server lifecycle runs, migrations, operational commands, and real browser PWA interactions were executed against disposable database copies under `%TEMP%`. The live database (`%LOCALAPPDATA%\LalabAutoReport\lalab_autoreport.db`) remained 100% untouched.

Key findings:
1. **Build & Test Suite:** 0 Warnings, 0 Errors. The full test suite passed cleanly with **799 tests passed, 0 failed, 0 skipped** across all 8 projects.
2. **Server Default (`--lan-writes none`):** When launched without `--lan-writes` (defaulting to `none`), `/health/ready` exposed `capabilities: []`. Admin and Staff obtained LSv2 tokens via `/api/v1/auth/sessions`. Every write attempt on `/api/v1/orders/{id}/delivered`, `/printed`, `/note` and legacy toggle/note/payment/print routes returned **503 `WRITES_DISABLED_UNTIL_PHASE2`**. Reads and gallery remained fully operational (HTTP 200). Database rows remained completely immutable.
3. **Server Mode (`--lan-writes orders`):** When launched with `--lan-writes orders`, `/health/ready` exposed exactly `["orders.setDelivered", "orders.setPrinted", "orders.editNote"]`. Staff role was strictly restricted from financial reporting and maintenance endpoints (HTTP 401/403/404). Admin and Staff successfully executed delivered, printed, and note mutations. Idempotent replays returned cached results without mutation. Conflicting payloads for the same operation returned **409 `IDEMPOTENCY_CONFLICT`**. Outdated revisions returned **409 `STALE_REVISION`** with current revision and payload state. Operation lookup returned `committed` for processed operations and `not_found` for unknown operations. Missing/mismatched API version headers returned **426 `CLIENT_TOO_OLD` / `SERVER_TOO_OLD`**. Legacy tokens were rejected on write with **401 `SESSION_UPGRADE_REQUIRED`**. Legacy toggle and note adapters executed with trigger revision bumps, and concurrent legacy requests triggered **409 `STALE_REVISION`** with Vietnamese message `"Đơn vừa được cập nhật trên thiết bị khác. Vui lòng tải lại."`. Payment, billing print, and maintenance endpoints remained fail-closed at **503 `WRITES_DISABLED_UNTIL_PHASE2`**.
4. **PWA in Real Browser (390x844):** Google Chrome Headless with mobile emulation (390x844) loaded the PWA, logged in via PIN (`123456`), stored the LSv2 token in `localStorage`, loaded 17 orders for date `2026-09-29`, toggled delivered and printed states (bumping revisions to 1), and saved a baseline note. During two-session concurrency testing, a concurrent note update by Session 2 caused Session 1 to receive 409 `STALE_REVISION`. The PWA preserved the user's unsaved draft text inside `<textarea id="noteInput">` without overwriting, while displaying the server's current note in `#noteConflictServerText` alongside the warning notice `"Ghi chú đã được đổi trên thiết bị khác — xem lại trước khi lưu"`. Operation lookup verified committed status. Admin session revocation (simulating PIN change / epoch bump) forced Tab 1 into 401 logout, cleared `lalab_token` from `localStorage`, and presented `#pinModal`. After re-login, image gallery viewing functioned correctly (HTTP 200).
5. **Fixture Inventory & Hygiene:** A full schema migration from V24 to V29 on fixture data produced bit-for-bit identical locked-snapshot hash (`9b31fcd4f8d52d1b42664eeb4052b22027f02afb9252a49c43e681d22406a429`) and identical financial totals (9,705,000 VND locked; 8,335,000 VND debt; 20 orders, 1,243 items, 21 locked bills). Log hygiene audit scanned 28 operational log lines across all runs with **0 sensitive secrets or plaintext PIN leaks**. 0 lingering processes remained.

---

## 2. Gate Verification Matrix (PLAN_LAN_PHASE2 §6 B3)

| # | Verification Requirement | Status | Observations & Evidence | Evidence File |
|---|---|---|---|---|
| 1 | **Build & Tests** | **PASS** | Solution build: 0 Warnings, 0 Errors. Full suite: **799 passed, 0 failed, 0 skipped** (net8.0). | [`01_build_and_unit_tests.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/01_build_and_unit_tests.txt) |
| 2 | **Default `--lan-writes none`** | **PASS** | Defaults to `none`. `/health/ready` reports `capabilities: []`. LSv2 login 200. Write endpoints (`/api/v1` and legacy toggles/note/payment/print) return **503 `WRITES_DISABLED_UNTIL_PHASE2`**. Reads & gallery return 200. DB unmodified. | [`02_lan_writes_none_parity.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/02_lan_writes_none_parity.json) |
| 3 | **Server `--lan-writes orders`** | **PASS** | Capabilities: `[orders.setDelivered, orders.setPrinted, orders.editNote]`. Staff login 200, finance endpoints 401/403. Commands delivered/printed/note return 200. Replay returns 200 old result. Payload mismatch returns 409 `IDEMPOTENCY_CONFLICT`. Stale revision returns 409 `STALE_REVISION` with current value. Lookup returns `committed` / `not_found`. Missing/wrong `X-Lalab-Client-Api` returns 426. Legacy token returns 401 `SESSION_UPGRADE_REQUIRED`. Legacy adapters return 200; concurrent stale legacy returns 409 with Vietnamese message. Payment/print/maintenance return 503. | [`03_lan_writes_orders_api.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/03_lan_writes_orders_api.json) |
| 4 | **PWA in Real Browser (390x844)** | **PASS** | Driven via Google Chrome Headless (CDP) at 390x844. PIN login succeeds, LSv2 stored. Delivered & printed toggles bump revision. Baseline note saved. Two-session race triggers 409 conflict: `#noteInput` draft text is preserved, `#noteConflictServerText` displays remote server note. Operation lookup verified. Session revocation forces 401 logout & clears token. Re-login & gallery view succeed. 5 screenshots captured. | [`04_pwa_browser_verification.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04_pwa_browser_verification.json)<br>Screenshots: `04a`–`04e` |
| 5 | **Fixture Inventory & Hygiene** | **PASS** | Full upgrade V24 -> V29 verified via `inventory --dry-run`: counts (20 orders, 1243 items, 21 locked bills, 36 customer bills, 6 customers, 2 root folders) and totals (9,705,000 VND locked; 8,335,000 VND debt) are identical. `locked_bills_snapshot_sha256` = `9b31fcd4...` bit-for-bit identical. 0 secrets in logs or health. 0 lingering processes. | [`05_fixture_inventory_and_hygiene.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/05_fixture_inventory_and_hygiene.json) |
| 6 | **Windows Service (Session 0)** | **NOT VERIFIED** | Service installation in Session 0 omitted per mandatory QA safety rules (`No service install; no deploy`). Executable verified as headless console host in user session. | N/A |

---

## 3. Detailed Verification Results

### 3.1 Build & Test Suite Regression
- **Command:** `dotnet build LalabAutoReport.slnx -c Debug`
  - Output: 0 Warning(s), 0 Error(s). Time elapsed: 00:00:02.72.
- **Command:** `dotnet test -c Debug --no-build`
  - Result: **Passed: 799, Failed: 0, Skipped: 0, Total: 799** (Duration: 2m 41s).
- **Evidence File:** [`01_build_and_unit_tests.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/01_build_and_unit_tests.txt)

### 3.2 Read-Only Server Parity (`--lan-writes none`)
- **Server Command:** `LalabAutoReport.Server.exe --data-dir <tempDir> --port 5071 --health-port 5271` (omitting `--lan-writes` to verify default)
- **Health Verification:**
  - `GET http://127.0.0.1:5271/health/ready` -> HTTP 200, `schemaVersion: 29`, `status: "Ready"`, `capabilities: []`.
- **Authentication:**
  - `POST /api/v1/auth/sessions` with `pin: "123456"` -> HTTP 200, returned LSv2 token `lsv2.3b98eeb1f6484b6da85e76a59b637031.VkgVJVNK...`, `role: "Admin"`.
- **Write Endpoint Blocking (503):**
  - `POST /api/v1/orders/1/delivered` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/v1/orders/1/printed` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/v1/orders/1/note` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/orders/1/toggle-delivered` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/orders/1/toggle-printed` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/orders/1/note` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/bills/1/toggle-payment` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/bills/1/print` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
- **Read & Gallery Passthrough (200):**
  - `GET /api/status` -> HTTP 200
  - `GET /api/orders?date=2026-09-29` -> HTTP 200 (17 orders)
  - `GET /api/v1/orders/1/operational-state` -> HTTP 200
  - `POST /api/orders/7/gallery/requests` -> HTTP 200
  - `GET /api/orders/7/gallery?offset=0&limit=10` -> HTTP 200
  - `POST /api/orders/7/gallery/touch` -> HTTP 200
- **Database Immutability:** Order 1 row in `orders` table (`is_delivered: 0`, `is_printed: 0`, `note: ""`) was completely unchanged before vs after write attempts.
- **Evidence File:** [`02_lan_writes_none_parity.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/02_lan_writes_none_parity.json)

### 3.3 Server Profile Operational Writes (`--lan-writes orders`)
- **Server Command:** `LalabAutoReport.Server.exe --data-dir <tempDir> --port 5072 --health-port 5272 --lan-writes orders`
- **Readiness:**
  - `GET /health/ready` -> HTTP 200, exposed capabilities: `["orders.setDelivered", "orders.setPrinted", "orders.editNote"]`.
- **Role Enforcement & Separation:**
  - Admin login: Role `Admin`, hold `finance.read` and full administrative capabilities.
  - Staff login: Role `Staff`, hold `orders.read`, `orders.setDelivered`, `orders.setPrinted`, `orders.editNote`, `gallery.read`. `finance.read` is strictly excluded.
  - Staff calling `GET /api/reports/monthly` -> **HTTP 401/403** forbidden.
- **Operational Command Verification (Order 1):**
  - `GET /api/v1/orders/1/operational-state` -> HTTP 200, initial revisions all 0.
  - `GET /api/orders?date=2026-09-29` -> HTTP 200, additive fields `deliveredRevision: 0`, `printedRevision: 0`, `noteRevision: 0` verified.
  - `POST /api/v1/orders/1/delivered` (`expectedRevision: 0`, `delivered: true`) -> HTTP 200, `changed: true`, `revision: 1`.
  - Replay with same `operationId` and identical payload -> HTTP 200, cached result returned with `revision: 1`.
  - Replay with same `operationId` but conflicting payload (`delivered: false`) -> **HTTP 409** `IDEMPOTENCY_CONFLICT`.
  - Command with stale `expectedRevision: 0` (current revision is 1) -> **HTTP 409** `STALE_REVISION`, returned `currentRevision: 1`, `currentValue: {"delivered": true, ...}`.
  - `POST /api/v1/orders/1/printed` (`expectedRevision: 0`, `printed: true`) -> HTTP 200, `revision: 1`.
  - `POST /api/v1/orders/1/note` (`expectedRevision: 0`, `note: "B3 QA Verified Note"`) -> HTTP 200, `revision: 1`.
- **Operation Lookup:**
  - `GET /api/v1/operations/{opDelivId}` -> HTTP 200, `{status: "committed", capability: "orders.setDelivered", entityId: 1}`.
  - `GET /api/v1/operations/{unknownId}` -> HTTP 200, `{status: "not_found"}`.
- **Version Handshake (`X-Lalab-Client-Api`):**
  - Missing header -> **HTTP 426** `CLIENT_TOO_OLD`.
  - Header `2.0` (major version mismatch) -> **HTTP 426** `SERVER_TOO_OLD`.
- **Session Authentication:**
  - Non-LSv2 legacy token on `/api/v1` write -> **HTTP 401** `SESSION_UPGRADE_REQUIRED`.
- **Legacy Adapters:**
  - `POST /api/orders/1/toggle-delivered` -> HTTP 200, toggled from `true` to `false`.
  - `POST /api/orders/1/note` -> HTTP 200, set note to `"Note via Legacy Adapter"`.
  - 12 concurrent requests to `/api/orders/1/note` generated 409 `STALE_REVISION` conflicts with Vietnamese message `"Đơn vừa được cập nhật trên thiết bị khác. Vui lòng tải lại."`.
- **Fail-Closed Endpoints (503):**
  - `POST /api/bills/1/toggle-payment` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`.
  - `POST /api/bills/1/print` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`.
  - `POST /api/settings/thumbnails/cleanup` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`.
- **Persistence:** Command receipts (14), audit log (16), and change journal (14) recorded in SQLite.
- **Evidence File:** [`03_lan_writes_orders_api.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/03_lan_writes_orders_api.json)

### 3.4 PWA in Real Browser (Google Chrome, 390x844 Mobile Viewport)
- **Browser Harness:** Google Chrome Headless 155 with CDP over native WebSocket. Emulated device metrics: width 390, height 844, scale factor 3.0, mobile enabled.
- **Verification Flow:**
  1. **Page Load:** Navigated to `http://127.0.0.1:5078/`. `#pinModal` confirmed visible. Captured [`04a_pwa_login_390x844.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04a_pwa_login_390x844.png).
  2. **Login:** Submitted PIN `123456`. `localStorage.getItem('lalab_token')` obtained LSv2 token (`lsv2.ea486...`). Selected date `2026-09-29`. 17 order cards rendered in DOM. Captured [`04b_pwa_orders_loaded_390x844.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04b_pwa_orders_loaded_390x844.png).
  3. **State Toggles:** Invoked `toggleDelivered(21)` and `togglePrinted(21)` through PWA UI. In-memory order card updated: `isDelivered: true` (rev 1), `isPrinted: true` (rev 1). Captured [`04c_pwa_delivered_and_printed_set.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04c_pwa_delivered_and_printed_set.png).
  4. **Baseline Note:** Saved `"Tab 1 Baseline Note"` on Order 21. Modal closed cleanly. Note revision bumped to 1.
  5. **Concurrency Conflict & Draft Preservation:**
     - Tab 1 opened note modal and entered draft text: `"Unsaved draft text in Tab 1 that must not be lost"`.
     - Concurrent Session 2 updated the note on server to `"Server Note from Concurrent Tab 2"` via API (bumping server note revision to 2).
     - Tab 1 submitted `saveNote()` holding stale expected revision 1.
     - Server rejected with **409 `STALE_REVISION`**.
     - PWA handled the conflict:
       - Modal remained open (`modalHidden: false`).
       - `#noteConflictNotice` banner became visible (`noticeHidden: false`).
       - `#noteConflictServerText` displayed the remote server note (`"Server Note from Concurrent Tab 2"`).
       - User draft inside `<textarea id="noteInput">` was **100% preserved** (`"Unsaved draft text in Tab 1 that must not be lost"`).
     - Captured [`04d_pwa_note_conflict_preserved_draft.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04d_pwa_note_conflict_preserved_draft.png).
  6. **Operation Lookup:** `executeOperationalCommand` executed successfully; operation ID `5ea8cde40d2e4733bd7d1beae0858cbf` was looked up via `GET /api/v1/operations/{id}` and verified as `committed`.
  7. **PIN Change / Session Invalidation Logout:**
     - Invoked `POST /api/v1/admin/sessions/revoke-all` with `role: "Admin"`, bumping role epoch in database.
     - Tab 1 called `loadOrders()`, receiving 401 `SESSION_REVOKED`.
     - PWA executed `logout()`: `localStorage.getItem('lalab_token')` was cleared (`null`), and `#pinModal` was displayed with error message.
     - Captured [`04e_pwa_pin_changed_logged_out.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04e_pwa_pin_changed_logged_out.png).
  8. **Gallery Passthrough:** Logged back in; gallery requests for Order 7 loaded 5 items successfully (HTTP 200).
- **Evidence Files:**
  - Data: [`04_pwa_browser_verification.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04_pwa_browser_verification.json)
  - Screenshots:
    - [`04a_pwa_login_390x844.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04a_pwa_login_390x844.png)
    - [`04b_pwa_orders_loaded_390x844.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04b_pwa_orders_loaded_390x844.png)
    - [`04c_pwa_delivered_and_printed_set.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04c_pwa_delivered_and_printed_set.png)
    - [`04d_pwa_note_conflict_preserved_draft.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04d_pwa_note_conflict_preserved_draft.png)
    - [`04e_pwa_pin_changed_logged_out.png`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/04e_pwa_pin_changed_logged_out.png)

### 3.5 Fixture Inventory Upgrade Invariance & Hygiene Audit
- **Command:** `LalabAutoReport.Server.exe inventory --dry-run --data-dir <tempDir>`
- **Inventory Invariance (Before vs After V25-V29 Upgrade):**

| Inventory Metric | Pre-Upgrade (V24) | Post-Upgrade (V29) | Invariance Status |
|---|---|---|---|
| `schema_migrations_max_version` | 24 | 29 | Expected upgrade |
| `orders` | 20 | 20 | **MATCH (Identical)** |
| `order_items` | 1,243 | 1,243 | **MATCH (Identical)** |
| `bills` | 12 | 12 | **MATCH (Identical)** |
| `locked_bills` | 21 | 21 | **MATCH (Identical)** |
| `customer_bills` | 36 | 36 | **MATCH (Identical)** |
| `customers` | 6 | 6 | **MATCH (Identical)** |
| `root_folders` | 2 | 2 | **MATCH (Identical)** |
| `locked_bills_total_amount_vnd` | 9,705,000 VND | 9,705,000 VND | **MATCH (Identical)** |
| `outstanding_debt_vnd` | 8,335,000 VND | 8,335,000 VND | **MATCH (Identical)** |
| `locked_bills_snapshot_sha256` | `9b31fcd4f8d52d1b42664eeb4052b22027f02afb9252a49c43e681d22406a429` | `9b31fcd4f8d52d1b42664eeb4052b22027f02afb9252a49c43e681d22406a429` | **BIT-FOR-BIT IDENTICAL** |

- **Log & Health Hygiene Audit:**
  - Scanned 28 log lines across server operational logs and `/health/ready` + `/health/live` JSON responses.
  - Checked sensitive tokens: `123456`, `000000`, passwords, secrets, private keys, plaintext PIN keys.
  - Leaks detected: **0**.
- **Process Hygiene:** Verified with `tasklist` that 0 server instances or background processes remained active.
- **Evidence File:** [`05_fixture_inventory_and_hygiene.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase2/05_fixture_inventory_and_hygiene.json)

---

## 4. Defect and Severity Classification

- **Blocker (Severity 1):** 0
- **Critical (Severity 2):** 0
- **Major (Severity 3):** 0
- **Minor (Severity 4):** 0
- **Cosmetic / Observation (Severity 5):** 0

All Phase 2 Batch 3 acceptance criteria defined in `docs/PLAN_LAN_PHASE2.md` §6 B3 are fully met.

---

## 5. Residual Risks & Safety Boundaries

1. **Windows Service in Session 0 (NOT VERIFIED):** Service installation and Session 0 execution were intentionally skipped per QA safety mandate (`SAFETY: use disposable copies under %TEMP%; never write the live DB; no service install; no deploy`). The console host behavior in user session is verified.
2. **Phase 4 Capabilities Kept 503:** Payment reconciliation, bill locking/reopening/splitting, and printing remain disabled behind the write gate as designed until Phase 4.

---

## 6. QA Recommendation

**VERDICT: QA PASS**

All required evidence has been gathered and persisted in `docs/qa-evidence-phase2/`. The solution is ready to proceed with flipping the default CLI `--lan-writes` flag in `src/LalabAutoReport.Server/Program.cs` from `none` to `orders`.
