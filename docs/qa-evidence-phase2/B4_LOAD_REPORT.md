# Lalab Auto Report — Phase 2 B4 Load Test Report

> **Test Execution Timestamp:** 2026-10-26 04:59:03 UTC
> **Harness Profile:** 10 concurrent clients, 60.0s bounded run, parallel scan-shaped SQLite contention
> **Write Mode:** `--lan-writes orders` (Loopback port binding)
> **Server Revision:** BuildId: `2.0.0+ebf61a36d7db5966cdffc68ecdb03eb657e00b81+ebf61a36_20261026033537` | Git SHA: `ebf61a36d7db5966cdffc68ecdb03eb657e00b81`
> **Fixture Mode:** Deterministic Synthetic Fixture (Clean Checkout Compatible)
> **Overall Measured Verdict:** **PASS**

---

## 1. Executive Summary

| Metric | Actual Result | Target (Plan §13) | Evidence Status |
|---|---|---|---|
| **Read Latency (p95)** | **8.1 ms** | < 1,000 ms | PASS (Observed) |
| **Command Latency (p95)** | **323.3 ms** | < 2,000 ms | PASS (Observed) |
| **Total Requests** | **12,437** | — | Completed |
| **Throughput** | **207.3 req/s** | — | Completed |
| **Status 200 OK** | **12,144** (97.6%) | High | Healthy |
| **Status 409 Conflict** | **293** | Handled | Optimistic Concurrency |
| **Status 503 SERVER_BUSY** | **0** | Handled | Contention Backstop |
| **Other / Unexpected Errors** | **0** | 0 | Zero |
| **Command Reconciliation** | **2,669 / 2,669** | 100% Matching | PASS (100%) |
| **Overall Verdict** | **PASS** | All Criteria Met | PASS |

---

## 2. Latency Distributions (Milliseconds)

| Category | Requests | Min (ms) | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) | Avg (ms) |
|---|---|---|---|---|---|---|---|
| **Reads** (`/api/orders`, `/operational-state`, `/gallery`) | 9,059 | 0.3 | 2.1 | 8.1 | 13.9 | 36.4 | 2.9 |
| **Commands** (`/delivered`, `/printed`, `/note`) | 3,378 | 0.4 | 3.6 | 323.3 | 630.8 | 952.8 | 78.5 |
| **Overall** | 12,437 | 0.3 | 2.4 | 161.5 | 462.2 | 952.8 | 23.4 |

---

## 3. Idempotency & Concurrency Validation

- **Same-Operation Replays under Load:**
  - Total replays attempted: **416**
  - Exact match responses: **416** (100.0%)
  - Duplicate revision bump effects (queried from DB): **0**
  - Duplicate audit log effects (queried from DB): **0**
  - Duplicate outbox effects (queried from DB): **0**

- **Optimistic Concurrency & 409 STALE_REVISION Handling:**
  - Stale revision conflicts encountered: **225**
  - Re-read operational state + retry succeeded: **190** (84.4%)
  - False success entries (queried from DB): **0**

- **Scan-Shaped Write Contention Simulation (Q-B7):**
  - Parallel short-transaction write bursts executed: **118**
  - Resulting `503 SERVER_BUSY` responses: **0** (Server translated `SQLITE_BUSY` to retryable `503 SERVER_BUSY` with zero crashed connections).
  - Note: Real AutoScan coordinator is intentionally disabled in the server profile (Q-B7); this background worker modeled scan write pressure on the shared SQLite database.

---

## 4. Post-Load Database Integrity Verification

| Check | Expected | Actual | Result |
|---|---|---|---|
| `PRAGMA integrity_check` | ok | ok | PASS |
| `PRAGMA foreign_key_check` | 0 rows | 0 violations | PASS |
| Incomplete / Non-committed Receipts | 0 | 0 | PASS |
| Receipts missing Audit Log row | 0 | 0 | PASS |
| Duplicate Revision Bump Effects | 0 | 0 | PASS |
| Duplicate Audit Log Effects | 0 | 0 | PASS |
| Duplicate Outbox Effects | 0 | 0 | PASS |
| False Success Entries | 0 | 0 | PASS |
| Server `inventory --dry-run` | Exit 0 | Exit 0 (Success) | PASS |

---

## 5. Client Command Reconciliation & Double-Entry Integrity

| Verification Check | Target | Actual | Result |
|---|---|---|---|
| Expected Operations Recorded by Clients | > 0 | 2,669 | Completed |
| Exact Matching Receipts in `command_receipts` | 100% | 2,669 / 2,669 | PASS (100%) |
| Missing Command Receipts | 0 | 0 | PASS |
| Duplicate Command Receipts | 0 | 0 | PASS |
| Mismatched Receipt Status/Payload | 0 | 0 | PASS |
| Missing Audit Log Entries | 0 | 0 | PASS |
| Orphan Audit Log Entries (No Receipt) | 0 | 0 | PASS |
| Missing Change Journal Effects (Committed) | 0 | 0 | PASS |
| Duplicate Change Journal Entries | 0 | 0 | PASS |
| Duplicate Outbox Entries | 0 | 0 | PASS |

> **Reconciliation Matching Rule:** Client response JSON and stored DB `result_json` are compared using full recursive canonical JSON equality. Top-level keys (`orderId`, `fieldGroup`, `revision`, `changed`, `operationId`, `syncState`) and the full nested `value` object (`delivered`/`printed`/`note` metadata) are strictly validated for complete schema presence. Property names are compared case-insensitively (handling PascalCase in SQLite receipts vs camelCase in HTTP responses), keys are sorted deterministically, numbers are compared invariants, and empty `{}` or truncated objects are rejected.

---

## 6. Artifacts and Raw Output

- JSON Evidence: `b4_load_evidence.json`
- Server Inventory Snapshot after load:
```json
{
  "orders": 4,
  "order_items": 6,
  "bills": 0,
  "locked_bills": 0,
  "locked_bills_count": 0,
  "locked_bills_total_amount_vnd": 0,
  "locked_bills_snapshot_sha256": "9fd481b1a0430bb935b60a4feafb88db6fb30ba8d8f2680dc0c161b5bbfaa03e",
  "locked_bills_sha256": "9fd481b1a0430bb935b60a4feafb88db6fb30ba8d8f2680dc0c161b5bbfaa03e",
  "outstanding_debt_vnd": 0,
  "customer_bills": 0,
  "customers": 0,
  "print_specifications": 4,
  "product_families": 2,
  "product_variants": 4,
  "customer_aliases": 0,
  "aliases": 18,
  "cloud_endpoint": null,
  "cloud_stream_id": null,
  "cloud_last_cursor": 0,
  "cloud_requires_reconciliation": false,
  "cloud_sync_queue_pending": 0,
  "cloud_operational_outbox_pending": 1620,
  "cloud_print_command_ledger_unacked": 0,
  "root_folders": 1,
  "schema_migrations_max_version": 29
}
```
