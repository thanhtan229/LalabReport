# Phase 2 B4 Load Harness

## Overview
This console application executes the bounded 10-client concurrency and load test against a real `LalabAutoReport.Server.exe` process running in `--lan-writes orders` mode on free loopback ports.

The harness evaluates an explicit overall **PASS / FAIL** verdict derived from measured coverage, error rates, 100% idempotency replay fidelity, latency SLAs (reads < 1000ms, commands < 2000ms), zero duplicate effects in change journal/audit log/outbox, zero simulator exceptions, SQLite database integrity checks, and 100% client-to-DB command reconciliation.

## Clean-Checkout Compatibility
The harness works out-of-the-box from a fresh checkout with zero external dependencies:
1. If `--source-db` is specified, it uses that database.
2. Otherwise, if `artifacts/acceptance/profile/lalab_autoreport.db` exists, it uses it.
3. Otherwise (or when `--fresh-fixture` is set), it automatically creates a fresh SQLite database, seeds schema migrations, creates a deterministic synthetic root directory with valid test JPEGs, executes an initial scan date, and cleans up completely after execution. Live application databases (`%LOCALAPPDATA%`) are never touched.

## Workload Characteristics
- **Concurrency:** 10 concurrent async client tasks
- **Workload Mix:**
  - Reads: `GET /api/orders?date=2026-09-29`, `GET /api/v1/orders/{id}/operational-state`, `POST /api/orders/{id}/gallery/requests`, `GET /api/orders/{id}/gallery`
  - Commands: `POST /api/v1/orders/{id}/delivered`, `POST /api/v1/orders/{id}/printed`, `POST /api/v1/orders/{id}/note` with unique `operationId` + `expectedRevision`
  - Optimistic Concurrency: `409 STALE_REVISION` is handled by re-reading operational state and retrying once
  - Idempotency Validation: Client 0 executes immediate same-operation replays asserting identical 200 OK responses with zero duplicate revision increments
  - Simulated Scan Contention: Parallel background worker executes brief guarded write transactions to simulate scan write pressure on SQLite WAL (Q-B7)
- **Integrity & Reconciliation Validation:**
  - PRAGMA integrity_check & foreign_key_check
  - Zero partial/uncommitted receipts
  - Client-side recorded operations reconciled against `command_receipts`, `audit_log`, and `change_journal` (100% receipt presence, matching outcomes, and zero orphan audit rows)
  - Zero duplicate revision/audit/outbox effects
  - Server `inventory --dry-run`

## How to Run

From repository root:
```powershell
# 1. Clean-checkout simulation with fresh synthetic fixture (60s, 10 clients):
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 60 --clients 10

# 2. Standard run (uses profile DB if available, else synthetic fixture):
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release

# 3. Custom duration or client count:
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --duration 90 --clients 10

# 4. Injected auth failure verification (verifies non-zero exit code on auth error):
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --inject-auth-failure --duration 5 --clients 2

# 5. Fault injection verification (verifies non-zero exit code when receipts or audits are compromised):
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-delete-receipts 1
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-delete-receipts all
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-corrupt-receipt-results 1
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-corrupt-receipt-results all
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-orphan-audit
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --fresh-fixture --duration 5 --clients 2 --fault-drop-receipt-after-http

# 6. Keep temporary directory for post-run forensic inspection:
dotnet run --project tests/LalabAutoReport.LoadHarness -c Release -- --keep-temp
```

## Evidence Output
Output files are written directly to `docs/qa-evidence-phase2/`:
- `B4_LOAD_REPORT.md` — Complete markdown report with percentiles, status counts, measured verdict, reconciliation details, and integrity proof.
- `b4_load_evidence.json` — Raw JSON metrics, server buildId, git SHA, reconciliation stats, and verdict criteria.
- `README.md` — Execution instructions and reproduction documentation.
