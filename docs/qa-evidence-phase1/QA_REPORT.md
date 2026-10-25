# QA Report: LAN Phase 1 Gate Verification

- **Repository:** `D:\___TOOLS\__TINIX\LalabReport`
- **Git HEAD:** `425bf865fc75a3fbb1ef582c158324109dd25a3e`
- **Commit Message:** `feat(server): add renderer spike and headless server-lock guard`
- **Date:** 2026-10-25
- **Role:** 05 — QA & Integration
- **Reference Document:** `docs/adr/ADR-002-lan-authoritative-server.md` (§3, §12)
- **Overall Result:** **QA PASS** (Gate lines 2–6 PASS; Gate line 1 NOT VERIFIED per safety instructions — Windows Service not installed in Session 0)

---

## 1. Executive Summary

An independent gate verification of LAN Phase 1 was conducted against ADR-002 §3 and §12. All operations were performed against disposable copies under `%TEMP%` without opening or modifying the production/live database (`%LOCALAPPDATA%\LalabAutoReport\lalab_autoreport.db`). No Windows Service was installed, maintaining strict environment safety boundaries.

All 692 unit/integration tests passed. The new server host (`LalabAutoReport.Server.exe`), the single-writer guard (`ServerInstanceGuard` + `server.lock`), the read-only loopback API gate (503 `WRITES_DISABLED_UNTIL_PHASE2`), the headless lock detector guard (exit 5/0), the deterministic in-host WPF bill/label renderer spike, and the immutable `inventory --dry-run` subcommand with comprehensive v2 canonical snapshot hashing and strict required-schema validation (Exit 8 on dropped required tables/columns) were verified end-to-end.

---

## 2. Gate Verification Matrix (ADR-002 §3 / §12)

| # | ADR Gate Requirement | Result | Verified Metric / Observation | Evidence File |
|---|---|---|---|---|
| 1 | **Service chạy khi chưa có ai đăng nhập (Session 0)** | **NOT VERIFIED** | Windows Service installation omitted per QA safety mandate (`Do not install a Windows Service. Do not deploy`). Host runs as console executable in Session 1. | N/A |
| 2 | **Đóng UI không làm dừng service** | **PASS** | UI started with `--isolated-data-directory`, Kestrel started, UI was killed (`Stop-Process -Force`); Server remained alive with `/health/ready` = 200. | `07_desktop_standalone_parity.json` |
| 3 | **Process thứ hai không mở được DB** | **PASS** | Second server instance rejected with exit code 3 (`Global\LalabAutoReport.Server` mutex held). `inventory --dry-run` on running server also rejected with exit code 3. | `05_server_second_instance_exit3.txt`, `10_inventory_dry_run.json` |
| 4 | **PWA đọc được fixture** | **PASS** | `/api/status` = 200, `/api/auth/login` = 200 (Staff role), `/api/orders` = 200 (17 orders), gallery requests = 200 (33 images in Order 7), gallery page = 200 (24 items), image = 200 (2,134 B), thumbnail = 200 (2,134 B), touch = 200. | `03_server_allowed_endpoints.json` |
| 5 | **Toàn bộ regression core pass** | **PASS** | Full suite: 680 passed, 0 failed, 0 skipped. `LalabAutoReport.Server` and `LalabAutoReport.UI` build with 0 warnings and 0 errors. | `01_build_and_unit_tests.txt` |
| 6 | **Spike renderer có kết quả** | **PASS** | In-host bill & thermal label rendering on Bill 1 produced bit-for-bit identical SHA-256 hashes across consecutive runs. Dimensions: JPEG 1080x930, Label 800x480. DB rows completely unmodified. | `09_renderer_spike_determinism.json` |

---

## 3. Detailed Verification Results

### A) Build & Test Suite Regression
- **Command:** `dotnet build src\LalabAutoReport.Server\LalabAutoReport.Server.csproj -c Debug --no-incremental`
  - Result: 0 Warnings, 0 Errors in Server project. (Exit code: 0)
- **Command:** `dotnet build src\LalabAutoReport.UI\LalabAutoReport.UI.csproj -c Debug --no-incremental`
  - Result: 0 Warnings, 0 Errors in UI project. (Exit code: 0)
- **Command:** `dotnet test -c Debug --no-build`
  - Result: **Passed: 692, Failed: 0, Skipped: 0, Total: 692** (Duration: 1m 28s, Exit code: 0).
- **Working Tree:** `git status` clean before and after verification.
- **Evidence:** [`01_build_and_unit_tests.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/01_build_and_unit_tests.txt)

### B) Server Runtime, Health & Read-Only API Gate
- **Command:** `LalabAutoReport.Server.exe --data-dir <temp> --port 5065 --health-port 5205`
- **Health Endpoints:**
  - `GET http://127.0.0.1:5205/health/live` -> HTTP 200, `{"status":"Live", ...}`
  - `GET http://127.0.0.1:5205/health/ready` -> HTTP 200, `{"status":"Ready", "schemaVersion":24, "buildId":"2.0.0+425bf865fc75a3fbb1ef582c158324109dd25a3e+2.0.0", "cloud":"disabled", "autoScan":"disabled"}`
  - BuildId verified to contain HEAD SHA `425bf86`.
- **Network Listeners:**
  - `Get-NetTCPConnection` confirmed both port 5065 (Kestrel) and port 5205 (Health) bind strictly to `127.0.0.1` (no `0.0.0.0`).
- **Allowed Endpoints:**
  - `GET /api/status` -> HTTP 200
  - `POST /api/auth/login` (with Staff PIN) -> HTTP 200, JWT token returned
  - `GET /api/orders?date=2026-09-29` -> HTTP 200, returned 17 orders
  - `POST /api/orders/7/gallery/requests` -> HTTP 200 (33 images)
  - `GET /api/orders/7/gallery?offset=0&limit=24` -> HTTP 200 (24 items)
  - `GET /api/orders/7/gallery/images/785069ec3bf1e407` -> HTTP 200, JPEG bytes (2,134 B)
  - `GET /api/orders/7/thumbnail` -> HTTP 200, image/jpeg (2,134 B)
  - `POST /api/orders/7/gallery/touch` -> HTTP 200, `{"success":true}` (allowed, did NOT return 503).
- **Business Write Rejections (503 Gate):**
  - `POST /api/orders/2/note` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/orders/2/toggle-delivered` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/orders/2/toggle-printed` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/bills/1/toggle-payment` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - `POST /api/bills/1/print` -> **HTTP 503** `WRITES_DISABLED_UNTIL_PHASE2`
  - Database row comparison before and after 503 calls proved `orders` (row 2: `note`, `is_delivered`, `is_printed`, `updated_at`) and `customer_bills` (row 1: `status`, `is_paid`, `updated_at`) remained 100% identical.
- **Second Server Instance Collision:**
  - `LalabAutoReport.Server.exe --data-dir <temp> --port 5067 --health-port 5207` exited with **Exit Code 3** (`Named mutex 'Global\LalabAutoReport.Server' đang được nắm giữ`).
- **Stale Lock Recovery:**
  - First server killed via `Stop-Process -Force`.
  - Confirmed stale `server.lock` file remained on disk.
  - Restarted server with same `--data-dir`; server acquired lock immediately without error; `/health/ready` returned 200.
- **Evidence Files:**
  - [`02_server_health_and_ports.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/02_server_health_and_ports.json)
  - [`02b_net_tcp_listeners.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/02b_net_tcp_listeners.txt)
  - [`03_server_allowed_endpoints.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/03_server_allowed_endpoints.json)
  - [`04_server_write_503_and_db_immutability.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/04_server_write_503_and_db_immutability.json)
  - [`05_server_second_instance_exit3.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/05_server_second_instance_exit3.txt)
  - [`06_server_stale_lock_recovery.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/06_server_stale_lock_recovery.txt)

### C) Standalone Desktop Parity & Decoupling
- **Command:** `LalabAutoReport.UI.exe --isolated-data-directory <copyDir2>` (configured with port 5066)
- **Findings:**
  - Listener binds to `0.0.0.0:5066` (`IPAddress.Any`), confirming desktop profile remains accessible to LAN devices.
  - `GET /api/status` returned HTTP 200.
  - `POST /api/auth/login` returned HTTP 200.
  - `GET /api/orders/7/gallery` returned HTTP 200.
  - Real business write (`POST /api/orders/2/note`) returned **HTTP 200** (NOT 503), confirming standalone profile retains full write capability.
  - While UI ran and after UI was terminated, Server running on an independent data directory maintained uninterrupted HTTP 200 on `/health/ready`.
- **Evidence:** [`07_desktop_standalone_parity.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/07_desktop_standalone_parity.json)

### D) Headless Server-Lock Guard
- **Command:** `LalabAutoReport.UI.exe --toggle-printed "<folder>" --isolated-data-directory <tempIso>`
- **Scenario 1 (Server Running):**
  - Server held `server.lock` under directory specified by `LALAB_SERVER_DATA_DIR`.
  - Headless UI exited immediately with **Exit Code 5** (`Máy chủ Lalab Server đang hoạt động (server.lock đang được nắm giữ). Thao tác --toggle-printed ngoại tuyến bị từ chối để tránh xung đột dữ liệu`).
- **Scenario 2 (Server Stopped):**
  - Server stopped. Stale `server.lock` file on disk was recognized as unlocked (`FileShare.None` open succeeded).
  - Exact same command exited with **Exit Code 0**.
- **Process Hygiene:** Verified 0 lingering UI or Server processes.
- **Evidence:** [`08_headless_guard_exit5_and_exit0.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/08_headless_guard_exit5_and_exit0.txt)

### E) Renderer Spike Determinism
- **Command:** `LalabAutoReport.Server.exe render-spike --bill-id 1 --out <outDir> --data-dir <temp>` (executed twice)
- **Hash Determinism:**
  - Run 1 JPEG SHA-256: `49caea17e448fdede704024ba7cd69cb59421b15958e42a9149ff1f3805adb89`
  - Run 2 JPEG SHA-256: `49caea17e448fdede704024ba7cd69cb59421b15958e42a9149ff1f3805adb89` (**MATCH**)
  - Run 1 Label SHA-256: `58950385618e4f4b7845a0a6bd142db4de3011b948c9e66247399c0133683576`
  - Run 2 Label SHA-256: `58950385618e4f4b7845a0a6bd142db4de3011b948c9e66247399c0133683576` (**MATCH**)
- **Dimensions:**
  - JPEG: `1080 x 930` px (148,361 bytes)
  - Shipping Label: `800 x 480` px (44,276 bytes)
- **Database Immutability:**
  - Bill 1 row (`status`, `is_paid`, `locked_at`, `updated_at`) and aggregate bill counts were identical before and after both runs.
- **Evidence:** [`09_renderer_spike_determinism.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/09_renderer_spike_determinism.json)

### F) Cutover Inventory Dry-Run
- **Command:** `LalabAutoReport.Server.exe inventory --dry-run --data-dir <temp>`
- **Exit Code:** 0
- **Reported Counts:**
  - Orders: 20
  - Order items: 1,243
  - Bills (V1): 12
  - Locked bills (V1 + V2): 21
  - Customer bills (V2): 36
  - Cloud queue pending: 0
  - Cloud outbox pending: 0
  - Cloud ledger unacked: 0
  - Root folders: 2
  - Schema migrations max version: 24
- **Database Hash Immutability:**
  - SHA-256 before: `2584ff5a3de457809e7ec97bc865978ab3249d6f875ed7cdc0b2716e712b9e75`
  - SHA-256 after: `2584ff5a3de457809e7ec97bc865978ab3249d6f875ed7cdc0b2716e712b9e75` (**MATCH**)
- **Concurrency Guard:** When executed while server was running on the same data dir, it exited with **Exit Code 3**.
- **Evidence:** [`10_inventory_dry_run.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/10_inventory_dry_run.json)

### G) Server Log Hygiene Audit
- **Audited Logs:** 7 log files across all temporary test directories (115 total log lines).
- **Sensitive Values Scanned:**
  - Admin PIN (`123456`): 0 leaks
  - Staff PIN (`000000`): 0 leaks
  - Mobile Auth Secret (`xB4Gz...`): 0 leaks
  - JWT Tokens (`eyJhbGci...`): 0 leaks
- **Summary:** Only operational messages (startup, backup created, migration status, endpoints listening, mutex/lock errors) are emitted.
- **Evidence:** [`11_server_log_hygiene_audit.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/11_server_log_hygiene_audit.txt)

---

## 4. Severities, Failing Items & Limitations

- **Blocking Failures:** None.
- **High / Medium / Low Defects:** None found in Phase 1 scope.
- **Explicit Limitations:**
  1. *Session 0 Verification:* As mandated by the testing safety instructions, the Windows Service was not installed into the Windows Service Control Manager (`sc.exe`). Testing was executed against `LalabAutoReport.Server.exe` running in user interactive session (Session 1). Session 0 rendering and service lifecycle must be formally validated during Phase 5/6 cutover when installation scripts are executed.
  2. *Phase 1 Read-Only Constraint:* LAN write operations intentionally return 503 `WRITES_DISABLED_UNTIL_PHASE2` to protect against dual-write split-brain until Phase 2 revision/idempotency mechanisms are implemented.

---

## 5. Artifacts and Evidence File Index

All evidence files are stored in [`docs/qa-evidence-phase1/`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/):
- [`01_build_and_unit_tests.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/01_build_and_unit_tests.txt): Full build output (Server + UI) and 680 unit test execution log.
- [`02_server_health_and_ports.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/02_server_health_and_ports.json): `/health/live` and `/health/ready` responses with BuildId and schema 24.
- [`02b_net_tcp_listeners.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/02b_net_tcp_listeners.txt): `Get-NetTCPConnection` table proving loopback binding (`127.0.0.1`) on ports 5065 & 5205.
- [`03_server_allowed_endpoints.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/03_server_allowed_endpoints.json): Verified status, login, orders, gallery request, page, image, thumbnail, and touch.
- [`04_server_write_503_and_db_immutability.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/04_server_write_503_and_db_immutability.json): 503 responses for note, delivered, printed, payment, print, and pre/post database row equality proof.
- [`05_server_second_instance_exit3.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/05_server_second_instance_exit3.txt): Exit code 3 output on second server instance collision.
- [`06_server_stale_lock_recovery.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/06_server_stale_lock_recovery.txt): Proof that stale `server.lock` file does not block server restart after hard kill.
- [`07_desktop_standalone_parity.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/07_desktop_standalone_parity.json): Standalone UI `0.0.0.0` binding, successful write (HTTP 200), and decoupling proof.
- [`08_headless_guard_exit5_and_exit0.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/08_headless_guard_exit5_and_exit0.txt): Exit code 5 with running server, exit code 0 when stopped.
- [`09_renderer_spike_determinism.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/09_renderer_spike_determinism.json): Dual run SHA-256 match for bill JPEG & thermal label; DB immutability.
- [`10_inventory_dry_run.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/10_inventory_dry_run.json): `inventory --dry-run` counts, DB SHA-256 preservation, and exit code 3 on running server.
- [`11_server_log_hygiene_audit.txt`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/11_server_log_hygiene_audit.txt): Audit of 7 log files confirming 0 credential or token exposures.
- [`12_v23_inventory_dry_run_recheck.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/12_v23_inventory_dry_run_recheck.json): Re-check of inventory dry-run on V23 schema with immutability and D7 fields.
- [`13_readiness_accuracy_recheck.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/13_readiness_accuracy_recheck.json): Exclusive port collision yielding 503 NotReady with component statuses, followed by 200 Ready on free port.
- [`14_health_privacy_recheck.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/14_health_privacy_recheck.json): Sanitized health payloads proving absence of dataDir/paths/secrets and presence of short SHA in BuildId.
- [`15_regression_write_gate_recheck.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/15_regression_write_gate_recheck.json): Regression verification of write gate 503, gallery allowed, and loopback listener.
- [`16_wal_safe_inventory_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/16_wal_safe_inventory_recheck2.json): Verification of WAL-safe inventory snapshot counting uncheckpointed WAL row without mutating source.
- [`17_readiness_dynamic_probe_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/17_readiness_dynamic_probe_recheck2.json): Verification of readiness dynamic probe (200 -> drop schema -> 503 -> recreate schema -> 200 recovery).
- [`18_fail_loud_inventory_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/18_fail_loud_inventory_recheck2.json): Verification of inventory exit 8 with empty stdout on missing required column, and exit 0 with null field on dropped optional table.
- [`19_snapshot_hash_sensitivity_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/19_snapshot_hash_sensitivity_recheck2.json): Proof of locked bills snapshot hash determinism, draft change insensitivity, and locked change sensitivity.
- [`20_regression_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/20_regression_recheck2.json): Regression verification for re-check #2 (689 tests passed, write gate 503, loopback binding).
- [`21_required_schema_validation_recheck3.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/21_required_schema_validation_recheck3.json): Verification of required-schema validation (Exit 8, empty stdout on missing tables/columns, Exit 0 and null on optional table, and V23 immutability).
- [`22_snapshot_hash_v2_completeness_recheck3.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/22_snapshot_hash_v2_completeness_recheck3.json): Canonical snapshot hash v2 completeness testing across 11 header/line/adjustment fields, draft insensitivity, and determinism.
- [`23_regressions_recheck3.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/23_regressions_recheck3.json): Regression verification for re-check #3 (692 tests passed, WAL-safe inventory, readiness dynamic recovery, write gate 503, loopback binding, hygiene).
- [`QA_REPORT.md`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/QA_REPORT.md): This report.

---

## 6. Addendum: Focused QA Re-Check (Commit fdb4d7f)

Following the final fixes in commit `fdb4d7fe126ecbbcf845023cd63be8e5886809f8` (`fix(server): make inventory non-mutating and harden readiness/health`), an independent runtime re-check was executed across the 3 reported blockers and core regressions.

### Re-Check Results Summary

| Blocker # | Target Description | Status | Evidence File |
|---|---|---|---|
| **Blocker 1** | `inventory --dry-run` non-mutating on TRUE V23 source + expanded D7 fields + missing DB exit 7 | **CLOSED** | [`12_v23_inventory_dry_run_recheck.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/12_v23_inventory_dry_run_recheck.json) |
| **Blocker 2** | Readiness accuracy: Exclusive port collision -> 503 `NotReady` with component states; normal start -> 200 `Ready` | **CLOSED** | [`13_readiness_accuracy_recheck.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/13_readiness_accuracy_recheck.json) |
| **Blocker 3** | Health privacy: no `dataDir`, no file/UNC paths, no PIN/secret/token, `BuildId` contains `fdb4d7f` | **CLOSED** | [`14_health_privacy_recheck.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/14_health_privacy_recheck.json) |
| **Regression** | Full test suite (684 passed), write gate 503 on `POST note`, gallery requests/touch 200, loopback only | **PASS** | [`15_regression_write_gate_recheck.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/15_regression_write_gate_recheck.json) |
| **Service Install** | Service runs with nobody logged in (Session 0) | **NOT VERIFIED** | Windows Service not installed in Session 0 per QA testing safety rule. |

### Detailed Blocker Verifications

#### Blocker 1 Details: True V23 Non-Mutating Inventory & Missing DB Exit 7
1. **Fixture Setup:** A disposable copy of the database was explicitly downgraded to V23 via `DROP TABLE IF EXISTS server_identity; DELETE FROM schema_migrations WHERE version=24; PRAGMA wal_checkpoint(TRUNCATE);` and removal of `-wal`/`-shm`.
2. **Directory & Hash Baseline:** Database SHA-256 was `e2ed7c9fa9882e142a3955f14dd2ba8b23c301187999254d384eb3af2ae79d4b`. Directory contained strictly `['lalab_autoreport.db']`.
3. **Execution & Immutability:** `LalabAutoReport.Server.exe inventory --dry-run --data-dir <tempV23>` exited with **Exit Code 0**.
   - Database SHA-256 after execution remained identical (`e2ed7c9f...`).
   - Schema remained 23; `server_identity` table was completely absent.
   - Directory listing remained identical (no `logs/`, `backups/`, `server.lock`, `-wal`, or `-shm` created).
4. **Expanded D7 Fields Verified:**
   - `locked_bills_count`: 21
   - `locked_bills_total_amount_vnd`: 9,705,000 VND
   - `locked_bills_sha256`: `0bf5f9d61ae4785599610448001cb6bc4a7477f307374c842ff917a68a03b202`
   - `outstanding_debt_vnd`: 8,335,000 VND
   - `customers`: 6, `customer_aliases`: 10, `aliases`: 66, `print_specifications`: 7
   - `cloud_endpoint`: `https://lalab.tinix.io.vn`, `cloud_stream_id`: `35b6779a89585e0a896c5c9db7b70e38`, `cloud_last_cursor`: 43
   - `cloud_requires_reconciliation`: false
   - `cloud_sync_queue_pending`: 0, `cloud_operational_outbox_pending`: 0, `cloud_print_command_ledger_unacked`: 0
5. **Nonexistent Path Test:** Running with a nonexistent path exited with **Exit Code 7**, and the directory was **not** created on disk.

#### Blocker 2 Details: Readiness Accuracy & Component Breakdown
1. **Exclusive Port Collision Test:** Bound port 5077 using a PowerShell `System.Net.Sockets.TcpListener` with `ExclusiveAddressUse = $true`.
2. **Server Response Under Collision:** Started server with `--port 5077 --health-port 5217`:
   - `/health/live` returned **HTTP 200**
   - `/health/ready` returned **HTTP 503**
   - Payload: `{"status":"NotReady", ..., "components":{"database":"ready","mobileApi":"unavailable"}}`
3. **Normal Recovery Test:** Stopped collision listener and started server on free port 5078:
   - `/health/live` returned **HTTP 200**
   - `/health/ready` returned **HTTP 200**
   - Payload: `{"status":"Ready", ..., "components":{"database":"ready","mobileApi":"ready"}}`

#### Blocker 3 Details: Health Privacy & BuildId
1. Both `/health/live` and `/health/ready` JSON payloads were audited:
   - `dataDir` property was completely removed from the DTO.
   - Zero occurrences of `C:\`, `\\`, UNC, or `%ProgramData%` paths.
   - Zero occurrences of `pin`, `secret`, `token`, `password`, or `key`.
2. `buildId` field in both responses contains `fdb4d7f`:
   - Value: `"2.0.0+fdb4d7fe126ecbbcf845023cd63be8e5886809f8+2.0.0"`

#### Regression Verification
1. Full test suite: **Passed: 689, Failed: 0, Skipped: 0, Total: 689** (command: `dotnet test tests/LalabAutoReport.Tests/LalabAutoReport.Tests.csproj --nologo`, duration: 1m 51s).
   > **Note on Test Count History:**
   > - The initial baseline recorded in `01_build_and_unit_tests.txt` at commit `425bf86` was **680** tests.
   > - Review Cycle 1 (`fdb4d7f`) added 4 integration tests (`InventoryDryRun_OnV23Database_LeavesSchemaAtV23_AndDoesNotCreateFilesOrMutateHash`, `InventoryDryRun_WhenDatabaseDoesNotExist_ReturnsExit7_AndDoesNotCreateDirectory`, `Readiness_WhenMobilePortOccupied_Returns503_WithComponentStatusUnavailable_AndLiveRemains200`, `HealthEndpoints_DoNotExposeAbsolutePaths_OrSecrets`), bringing the total to **684**.
   > - Review Cycle 2 added 4 comprehensive tests for Blockers A, B, and C (`Readiness_WhenSchemaMigrationsDropped_Returns503_DatabaseUnavailable_AndRecoversWhenRestored`, `InventoryDryRun_WhenGrandTotalColumnMissing_Exits8_AndOutputsNoJson`, `InventoryDryRun_WhenOptionalLegacyTableDropped_Exits0_WithNullField`, `InventoryDryRun_LockedBillsSnapshotSha256_SensitivityAndDeterminism`), and 1 WAL recovery regression test (`InventoryDryRun_WithUncheckpointedWal_CapturesCommittedRows_AndLeavesSourceUntouched`), resulting in the current reproducible count of **689** tests.
2. Write gate: `POST /api/orders/7/note` returned **HTTP 503** with code `WRITES_DISABLED_UNTIL_PHASE2`.
3. Allowed paths: `POST /api/orders/7/gallery/requests` returned HTTP 200; `POST /api/orders/7/gallery/touch` returned HTTP 200 (touch was not 503).
4. Network binding: Listeners confirmed exclusively on `127.0.0.1` (no `0.0.0.0`).
5. Process and Data Hygiene: 0 lingering server, UI, or python processes; live database (`%LOCALAPPDATA%\LalabAutoReport\lalab_autoreport.db`) untouched (verified timestamp preserved).

---

## 7. Addendum #2: Focused QA Re-Check #2 (Commit 6513edf)

Following the final review round-2 fixes in commit `6513edfd7ac67bf38934732983b683553b52ddf3` (`fix(server): probe readiness, fail loud inventory, hash locked snapshots`), an independent fresh runtime re-check was executed across all 5 required verification areas.

### Re-Check #2 Matrix

| Item # | Verification Target | Status | Key Metrics & Observation | Evidence File |
|---|---|---|---|---|
| **1** | **WAL-safe inventory** | **PASS** | Active writer connection held with `wal_autocheckpoint=0`; customer inserted in WAL was counted (`customers` = 7, base 6); exit code 0; source DB hash and directory entries preserved. | [`16_wal_safe_inventory_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/16_wal_safe_inventory_recheck2.json) |
| **2** | **Readiness dynamic probe** | **PASS** | Server started: 200 Ready. Dropped `schema_migrations` while running: 503 `NotReady` with `components.database: "unavailable:sql_error_1"`, live remained 200. Recreated `schema_migrations`: recovered to 200 `Ready` dynamically without restart. Zero path/secret leaks in any payload. | [`17_readiness_dynamic_probe_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/17_readiness_dynamic_probe_recheck2.json) |
| **3** | **Fail-loud inventory** | **PASS** | Missing required column (`grand_total_x`): exit code 8, stdout completely empty, stderr diagnostic emitted. Dropped optional table (`cloud_operational_outbox`): exit code 0, field reported as explicit `null`. | [`18_fail_loud_inventory_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/18_fail_loud_inventory_recheck2.json) |
| **4** | **Snapshot hash sensitivity** | **PASS** | Deterministic baseline hash: `a3f818fc81f0912459181f16dea0a7f3c67049271a79e5fad2926440ce26da70`. Draft bill note modified: hash unchanged. Locked bill customer name modified: hash changed (`c0c09289...`). Locked line billed quantity modified: hash changed (`11c04589...`). | [`19_snapshot_hash_sensitivity_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/19_snapshot_hash_sensitivity_recheck2.json) |
| **5** | **Regression suite & gate** | **PASS** | 689 passed unit/integration tests (0 failed). Write gate returned 503 `WRITES_DISABLED_UNTIL_PHASE2` for `POST note`. Gallery requests & touch returned 200 (not 503). Listeners on 127.0.0.1 only. Zero lingering processes. Live DB untouched. | [`20_regression_recheck2.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/20_regression_recheck2.json) |
| **—** | **Service Install (Session 0)** | **NOT VERIFIED** | Windows Service not installed in Session 0 per QA testing safety rule. | N/A |

### Detailed Findings for Re-Check #2

#### 1) WAL-Safe Inventory Snapshot
- An active writer connection was opened against a temporary copy of the database with `PRAGMA wal_autocheckpoint=0`.
- Inserted a uniquely named customer (`Customer WAL Active Unique <timestamp>`), creating an active uncheckpointed WAL file (`lalab_autoreport.db-wal`, 8,272 bytes).
- Executed `LalabAutoReport.Server.exe inventory --dry-run --data-dir <temp>` while the writer was holding the open connection.
- **Results:**
  - Exit code: **0**.
  - `customers` count in report: **7** (correctly captured the uncheckpointed customer from WAL; baseline was 6).
  - Source database file SHA-256 and directory contents were verified before and after closing the writer: completely identical (`db_hash_unchanged`: true).

#### 2) Readiness Dynamic Probe & Self-Recovery
- Server started on free ports (`--port 5095 --health-port 5235`).
- Initial state:
  - `GET /health/live` -> 200 `Live`
  - `GET /health/ready` -> 200 `Ready`, `components: {"mobileApi":"ready","database":"ready"}`
- Dynamically dropped `schema_migrations` table via a secondary SQLite connection while server was actively serving:
  - `GET /health/live` remained **200**
  - `GET /health/ready` dynamically switched to **503** with payload:
    ```json
    {
      "status": "NotReady",
      "schemaVersion": 0,
      "components": {
        "mobileApi": "ready",
        "database": "unavailable:sql_error_1"
      }
    }
    ```
- Recreated `schema_migrations` with version 24 via the secondary connection:
  - `GET /health/ready` dynamically recovered to **200** `Ready` (`components.database: "ready"`) **without restarting the server process**.
- Payload privacy audit: Verified zero occurrences of absolute filesystem paths (`C:\`, `\\`, UNC, `%ProgramData%`) or sensitive credentials.

#### 3) Fail-Loud Inventory
- **Required Schema Corruption:** Renamed `grand_total` to `grand_total_x` in `customer_bills`:
  - `inventory --dry-run` exited with **Exit Code 8**.
  - `stdout` was completely **EMPTY** (zero success JSON leaked).
  - `stderr` contained explicit diagnostic error:
    `[LỖI INVENTORY] Lỗi truy vấn hoặc lược đồ cơ sở dữ liệu không hợp lệ: SQLite Error 1: 'no such column: grand_total'.`
- **Optional Table Absence:** Dropped optional legacy table `cloud_operational_outbox` on a fresh copy:
  - `inventory --dry-run` exited with **Exit Code 0**.
  - Field `cloud_operational_outbox_pending` in JSON report was correctly rendered as **`null`** (not `0`).

#### 4) Snapshot Hash Sensitivity & Determinism
- **Determinism:** Two independent runs on identical database copies produced identical snapshot hashes:
  - Run 1 Hash: `a3f818fc81f0912459181f16dea0a7f3c67049271a79e5fad2926440ce26da70`
  - Run 2 Hash: `a3f818fc81f0912459181f16dea0a7f3c67049271a79e5fad2926440ce26da70` (**MATCH**)
- **Draft Note Insensitivity:** Modified `note` on a Draft bill (id = 5):
  - Hash remained: `a3f818fc81f0912459181f16dea0a7f3c67049271a79e5fad2926440ce26da70` (**UNCHANGED**)
- **Locked Bill Customer Snapshot Sensitivity:** Modified `customer_name_snapshot` on a Locked bill (id = 1):
  - Hash changed to: `c0c0928926cf03e53fcd13624f7b0f25e8d5a9f5be014ce43971c078421b0110` (**CHANGED**)
- **Locked Bill Line Quantity Sensitivity:** Modified `billed_quantity` on a line belonging to Locked bill 1:
  - Hash changed to: `11c04589ab8c1ca5059842bdce68188d220494191a07d8041271154d2b094911` (**CHANGED**)

#### 5) Full Regression & Gate Verification
- **Automated Tests:** `dotnet test tests/LalabAutoReport.Tests/LalabAutoReport.Tests.csproj --nologo` executed **689 passed, 0 failed, 0 skipped** across all test suites (including all 52 server tests).
- **LAN Write Gate:** `POST /api/orders/7/note` returned **HTTP 503** with code `WRITES_DISABLED_UNTIL_PHASE2`.
- **Allowed Operations:** `POST /api/orders/7/gallery/requests` and `POST /api/orders/7/gallery/touch` returned **HTTP 200** (touch was not 503).
- **Loopback Listeners:** Confirmed binding to `127.0.0.1` on ports 5096 and 5236 (`0.0.0.0` absent).
- **Hygiene:** 0 lingering processes; live production database (`%LOCALAPPDATA%\LalabAutoReport\lalab_autoreport.db`) untouched throughout all runs.

---

## 8. Addendum #3: Focused QA Re-Check #3 (Commit e229d5c)

Following the implementation of canonical snapshot hash v2 and strict required-schema inventory validation in commit `e229d5cbae9a9556050c6332e4f42592de927c91` (`fix(server): validate required inventory schema and hash full locked snapshots`), an independent verification was conducted to validate the two round-3 findings and complete system regression.

### Re-Check #3 Matrix

| Item # | Verification Target | Status | Key Metrics & Observation | Evidence File |
|---|---|---|---|---|
| **1** | **Required-Schema Validation** | **PASS** | Dropping `customer_bills`: Exit 8, stdout empty, stderr diagnostic emitted. Missing required columns (`phone_snapshot`, `quantity_override_reason`): Exit 8, stdout empty. Dropping optional table (`cloud_operational_outbox`): Exit 0, field `null`. V23 downgrade: Exit 0, schema remains 23, directory entries and DB SHA preserved. | [`21_required_schema_validation_recheck3.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/21_required_schema_validation_recheck3.json) |
| **2** | **Snapshot Hash v2 Completeness** | **PASS** | Baseline hash: `9b31fcd4f8d5...`. Dual-run determinism confirmed (`True`). Mutating each of the 11 locked snapshot fields (header: `phone_snapshot`, `shipping_address_snapshot`, `export_file_path`, `note`; line: `note`, `quantity_override_reason`, `price_override_reason`, `final_print_folder_path`, `specification_folder_name`, `issue_message`; adjustment: `note`) produced distinct hash changes. Draft bill note mutation left hash unchanged. | [`22_snapshot_hash_v2_completeness_recheck3.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/22_snapshot_hash_v2_completeness_recheck3.json) |
| **3** | **Full Regression & Gate** | **PASS** | 692 passed unit/integration tests (0 failed). WAL-safe inventory correctly counted committed customer (`customers` = 7, baseline 6) while source remained identical. Dynamic readiness probe recovered from 503 back to 200 upon schema recreation. Write gate returned 503 `WRITES_DISABLED_UNTIL_PHASE2` for `POST note`, while gallery requests & touch returned 200. Listeners bound to `127.0.0.1` only. Zero lingering processes; live DB untouched. | [`23_regressions_recheck3.json`](file:///D:/___TOOLS/__TINIX/LalabReport/docs/qa-evidence-phase1/23_regressions_recheck3.json) |
| **—** | **Service Install (Session 0)** | **NOT VERIFIED** | Windows Service not installed in Session 0 per QA testing safety rule. | N/A |

### Detailed Findings for Re-Check #3

#### 1) Required-Schema Validation & Fail-Loud Contract
- **Dropped Table `customer_bills`:**
  - Command: `LalabAutoReport.Server.exe inventory --dry-run --data-dir <temp>`
  - Exit code: **8**
  - Standard output: completely **EMPTY** (`""`)
  - Standard error: `[LỖI INVENTORY] Lỗi truy vấn hoặc lược đồ cơ sở dữ liệu không hợp lệ: Lược đồ thiếu bảng bắt buộc: customer_bills`
  - Verified: inventory does **NOT** fall back to legacy `bills` table on V24 schema.
- **Missing Required Header Column (`customer_bills.phone_snapshot`):**
  - Renamed column to `phone_snapshot_x`
  - Exit code: **8**, standard output: **EMPTY**, standard error: `[LỖI INVENTORY] Lỗi truy vấn hoặc lược đồ cơ sở dữ liệu không hợp lệ: Bảng 'customer_bills' thiếu cột bắt buộc: phone_snapshot`
- **Missing Required Line Column (`customer_bill_lines.quantity_override_reason`):**
  - Renamed column to `quantity_override_reason_x`
  - Exit code: **8**, standard output: **EMPTY**, standard error: `[LỖI INVENTORY] Lỗi truy vấn hoặc lược đồ cơ sở dữ liệu không hợp lệ: Bảng 'customer_bill_lines' thiếu cột bắt buộc: quantity_override_reason`
- **Dropped Optional Table (`cloud_operational_outbox`):**
  - Dropped table from a fresh copy.
  - Exit code: **0**, report field `cloud_operational_outbox_pending` was explicitly **`null`** (not `0`).
- **V23 Schema Immutability:**
  - Fresh copy downgraded to V23 (`DROP TABLE IF EXISTS server_identity; DELETE FROM schema_migrations WHERE version=24; PRAGMA wal_checkpoint(TRUNCATE);`).
  - Pre-run SHA-256: `e2ed7c9fa9882e142a3955f14dd2ba8b23c301187999254d384eb3af2ae79d4b`.
  - Exit code: **0**, reported `schema_migrations_max_version`: **23**.
  - Post-run SHA-256: `e2ed7c9fa9882e142a3955f14dd2ba8b23c301187999254d384eb3af2ae79d4b` (**UNCHANGED**).
  - Directory contents: strictly `['lalab_autoreport.db']` before and after execution.

#### 2) Snapshot Hash v2 Completeness & Sensitivity Audit
- **Baseline Determinism:**
  - Run 1 Hash: `9b31fcd4f8d52d1b42664eeb4052b22027f02afb9252a49c43e681d22406a429`
  - Run 2 Hash: `9b31fcd4f8d52d1b42664eeb4052b22027f02afb9252a49c43e681d22406a429` (**DETERMINISTIC MATCH**)
- **Draft Bill Modification (id = 5):**
  - `UPDATE customer_bills SET note = 'Draft updated note QA' WHERE id = 5;`
  - Hash: `9b31fcd4f8d52d1b42664eeb4052b22027f02afb9252a49c43e681d22406a429` (**UNCHANGED**)
- **Comprehensive Sensitivity Across 11 Fields on Locked Bill 1:**
  1. `phone_snapshot` (bill): `dcd93dd4db227b387ef33c4ef57c2d9100d2e42a7737aed6a4a2e41585987cf6` (**CHANGED**)
  2. `shipping_address_snapshot` (bill): `2778a1a866bc13b4eca072b7d487e984e9b5f1f0b4cfb893e2bc7ca6d623d333` (**CHANGED**)
  3. `export_file_path` (bill): `3d747acc07c05e9d071a2c21ec80889cf0ac77327219c6d4c813fa5679211119` (**CHANGED**)
  4. `note` (bill): `eef99c50004d02c7b65efce373ac382a6bc2ecd3dbf9a1659fe5c03157a82ab4` (**CHANGED**)
  5. `note` (line): `d0c7e7b2f347ccb7201b57505ac65437efa5e6a00f74b9a8d123c47d00333c8a` (**CHANGED**)
  6. `quantity_override_reason` (line): `2311bd4b35b679a27067b81c7f03ee1ba693deb71d3801fdde31226a7e479a53` (**CHANGED**)
  7. `price_override_reason` (line): `3b7b803e4522e6e4ab5daf200c81b6317e7886b2864e8cad806d401b09aba1b1` (**CHANGED**)
  8. `final_print_folder_path` (line): `b5d8c4b6c9f8cea479ee6b2772449bc54bd91fc8be7056c7d1e941faaf212614` (**CHANGED**)
  9. `specification_folder_name` (line): `29a8fdea2ea9c28be7d0b0bf77c0721db2d76be597885dfd288cf4c1ae6198e2` (**CHANGED**)
  10. `issue_message` (line): `a9be1e7ae7ceb0f5304a5701362acc6481cf2a7246fbff01aa02e8d364250d61` (**CHANGED**)
  11. `note` (adjustment): `1c6f95c5076ad02885a0085d0e48d0585b28d446cd3b0f0d73aaa03e390b52b3` (**CHANGED**)

#### 3) Regressions & System Hygiene
- **Automated Tests:** Full test suite execution: **692 passed, 0 failed, 0 skipped** across all test suites (duration: 1m 33s).
- **WAL-Safe Inventory:**
  - Active writer connection held with `wal_autocheckpoint=0` and unique customer inserted.
  - WAL file present and active (8,272 bytes).
  - Dry-run exit code: **0**, `customers` count: **7** (correctly counted uncheckpointed WAL row).
  - Hash during writer, hash after run, and hash after close: `a11cd88fd353907cdadf6d37bfe700bd7debe20df526cd1e4a759a6f5adb500b` (**IDENTICAL**).
  - Directory entries preserved: no sidecar or lock files created by inventory dry-run.
- **Dynamic Readiness Probe:**
  - Initial `/health/ready`: **200** `Ready`.
  - Dropped `schema_migrations`: `/health/ready` returned **503** `NotReady` (`components.database: "unavailable:sql_error_1"`), while `/health/live` remained **200** `Live`.
  - Recreated `schema_migrations` with version 24: dynamically recovered to **200** `Ready` without process restart.
- **LAN Write Gate:**
  - `POST /api/orders/7/note`: **503** with `code: "WRITES_DISABLED_UNTIL_PHASE2"`.
  - `POST /api/orders/7/gallery/requests`: **200** `OK`.
  - `POST /api/orders/7/gallery/touch`: **200** `OK`.
- **TCP Listeners:** `Get-NetTCPConnection` confirmed ports 5073 and 5213 bound strictly to `127.0.0.1` (`0.0.0.0` absent).
- **Process & DB Hygiene:** 0 lingering Server/UI/Python processes. Production database (`%LOCALAPPDATA%\LalabAutoReport\lalab_autoreport.db`) untouched (verified timestamp `10/25/2026 5:55:19 PM` and size `884736` bytes preserved).

