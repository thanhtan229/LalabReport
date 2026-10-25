# Phase 1 Inventory Contract (`inventory --dry-run`)

> **Document Version:** 1.0.0  
> **Status:** Authoritative Contract  
> **Referenced By:** `LalabAutoReport.Server`, `ServerInventoryService`, ADR-002 §3

---

## 1. Overview & Operational Principles

The subcommand `inventory --dry-run` produces an authoritative, immutable read-only snapshot report of database state for physical and historical auditing, integrity verification, and pre-migration cross-checking.

### Core Guarantees
1. **True Read-Only Dispatch:**
   - Runs before logging into the target data directory, before acquiring instance locks (`server.lock`), and before any database lifecycle or migration operations.
   - Does NOT create directories or files in `<data-dir>`.
   - Never modifies the source database file or its directory (source SHA-256 and directory file list/mtimes are verified post-execution).
2. **Crash-Safe Consistent Snapshotting:**
   - Copies the database and any active WAL (`-wal`) / shared memory (`-shm`) sidecars to a temporary sandbox directory via non-locking shared read streams (`FileShare.ReadWrite | FileShare.Delete`).
   - Opens the temporary copy normally so SQLite recovers uncheckpointed WAL transactions without modifying the source database.
   - Executes the SQLite Online Backup API from the recovered connection to create a cleanly compacted single-file snapshot in a secondary temporary directory.
   - Queries the compacted snapshot; securely purges all temporary directories in a `finally` block.
3. **Fail-Closed Required Schema Enforcement:**
   - Required tables and columns are strictly validated before any report queries run.
   - If any required table or required column is missing, execution immediately aborts with **Exit Code 8**, writes a diagnostic message to `stderr`, and suppresses all `stdout` output (empty stdout, no JSON).

---

## 2. Exit Code Matrix

| Exit Code | Classification | Condition | Standard Output (`stdout`) | Standard Error (`stderr`) |
| :--- | :--- | :--- | :--- | :--- |
| **0** | Success | Snapshot extracted and verified successfully | Full indented JSON report | Empty |
| **1** | Integrity Violation | Source DB hash or directory listing/mtime modified, or unhandled exception | **May contain the JSON emitted before the post-check** — consumers MUST check the exit code and ignore stdout when non-zero | Diagnostic error message |
| **2** | CLI Parse Error | Invalid or unrecognized arguments (e.g. unknown options) | Usage help | Diagnostic error message |
| **7** | Source DB Not Found | `<data-dir>\lalab_autoreport.db` does not exist (directory not created) | Empty | Diagnostic error message |
| **8** | Schema / Query Error | Missing required table, missing required column, or SQL query failure | **EMPTY (No JSON)** | Diagnostic error message |

---

## 3. Required Schema Validation (V23/V24)

Prior to executing any aggregation or snapshot hashing queries, the database schema is verified against strict required contracts based on the schema version recorded in `schema_migrations`.

### 3.1 Table Requirement Matrix

| Table Name | Schema Scope | Requirement Level | Behavior on Absence |
| :--- | :--- | :--- | :--- |
| `schema_migrations` | All | **Required** | Exit 8, stdout empty |
| `orders` | All | **Required** | Exit 8, stdout empty |
| `order_item_scans` | All | **Required** | Exit 8, stdout empty |
| `customers` | All | **Required** | Exit 8, stdout empty |
| `customer_aliases` | All | **Required** | Exit 8, stdout empty |
| `print_specifications` | All | **Required** | Exit 8, stdout empty |
| `print_specification_aliases` | All | **Required** | Exit 8, stdout empty |
| `product_families` | All | **Required** | Exit 8, stdout empty |
| `product_variants` | All | **Required** | Exit 8, stdout empty |
| `product_family_aliases` | All | **Required** | Exit 8, stdout empty |
| `product_specific_aliases` | All | **Required** | Exit 8, stdout empty |
| `root_folders` | All | **Required** | Exit 8, stdout empty |
| `customer_bills` | V23/V24 | **Required** | Exit 8, stdout empty (does not fall back to legacy `bills`) |
| `customer_bill_lines` | V23/V24 | **Required** | Exit 8, stdout empty |
| `customer_bill_adjustments` | V23/V24 | **Required** | Exit 8, stdout empty |
| `bills` | Legacy V1 | **Optional** | Exit 0; reported as 0 if `customer_bills` exists |
| `bill_lines` | Legacy V1 | **Conditional** | Optional only when no locked legacy `bills` rows exist; if locked legacy bills exist and `bill_lines` is missing, exit 8, stdout empty |
| `cloud_sync_state` | Cloud | **Optional** | Exit 0; reported as `null` when missing |
| `cloud_sync_queue` | Cloud | **Optional** | Exit 0; reported as `null` when missing |
| `cloud_operational_outbox` | Cloud | **Optional** | Exit 0; reported as `null` when missing |
| `cloud_print_command_ledger` | Cloud | **Optional** | Exit 0; reported as `null` when missing |
| `server_identity` | V24+ | **Optional** | Exit 0 |

### 3.2 Required Columns Matrix

#### `customer_bills` (Strictly Required on V23/V24)
- `id`
- `bill_number`
- `bill_type`
- `customer_id`
- `customer_name_snapshot`
- `phone_snapshot`
- `shipping_address_snapshot`
- `period_start`
- `period_end`
- `status`
- `product_subtotal`
- `adjustments_total`
- `grand_total`
- `note`
- `export_file_path`
- `locked_at`
- `exported_at`
- `is_paid`
- `paid_at`

*Note: Missing any column above immediately triggers Exit Code 8.*

#### `customer_bill_lines` (Strictly Required on V23/V24)
- `id`
- `bill_id`
- `order_id`
- `product_job_id`
- `product_specification_id`
- `product_name_snapshot`
- `variant_snapshot`
- `billing_method_snapshot`
- `scanned_quantity`
- `billed_quantity`
- `quantity_override_reason`
- `sheet_count`
- `included_sheets_snapshot`
- `extra_sheet_count`
- `base_price_snapshot`
- `extra_sheet_price_snapshot`
- `configured_unit_price`
- `billed_unit_price`
- `price_override_reason`
- `line_total`
- `is_included`
- `sort_order`
- `final_print_folder_path`
- `folder_resolution_mode_snapshot`
- `issue_message`
- `note`
- `specification_folder_name`

#### `customer_bill_adjustments` (Strictly Required on V23/V24)
- `id`
- `bill_id`
- `type`
- `label`
- `direction`
- `amount`
- `note`
- `sort_order`

---

## 4. Canonical Snapshot Hash Specification (Version: `v2`)

The snapshot hash covers every locked bill and all associated snapshot lines and financial adjustments.

### 4.1 Hash Properties
- **Algorithm:** SHA-256
- **Encoding:** UTF-8
- **Formatting:** `CultureInfo.InvariantCulture`, integer VND, lowercase hex string.
- **Report Keys:** Exposes `locked_bills_snapshot_sha256` and backward-compatible alias `locked_bills_sha256`.

### 4.2 Record Format & Ordering Rules
Locked bills are queried with `WHERE status IN ('Locked', 'Exported') ORDER BY id ASC`.

1. **Bill Header Record:**
   ```text
   v2|bill:id={id}|bill_number={bill_number}|bill_type={bill_type}|customer_id={customer_id}|customer_name={customer_name_snapshot}|phone={phone_snapshot}|shipping_address={shipping_address_snapshot}|period_start={period_start}|period_end={period_end}|status={status}|grand_total={grand_total}|product_subtotal={product_subtotal}|adjustments_total={adjustments_total}|is_paid={is_paid}|paid_at={paid_at}|locked_at={locked_at}|exported_at={exported_at}|export_file_path={export_file_path}|note={note}\n
   ```
2. **Bill Line Records (`ORDER BY sort_order ASC, id ASC`):**
   ```text
   line:id={id}|order_id={order_id}|job_id={product_job_id}|spec_id={product_specification_id}|product={product_name_snapshot}|variant={variant_snapshot}|method={billing_method_snapshot}|scanned_qty={scanned_quantity}|billed_qty={billed_quantity}|qty_override_reason={quantity_override_reason}|sheets={sheet_count}|inc_sheets={included_sheets_snapshot}|extra_sheets={extra_sheet_count}|base_price={base_price_snapshot}|extra_price={extra_sheet_price_snapshot}|cfg_price={configured_unit_price}|billed_price={billed_unit_price}|price_override_reason={price_override_reason}|total={line_total}|included={is_included}|sort={sort_order}|final_print_folder={final_print_folder_path}|mode={folder_resolution_mode_snapshot}|spec_folder={specification_folder_name}|issue={issue_message}|note={note}\n
   ```
3. **Bill Adjustment Records (`ORDER BY sort_order ASC, id ASC`):**
   ```text
   adj:id={id}|type={type}|label={label}|direction={direction}|amount={amount}|note={note}|sort={sort_order}\n
   ```
4. **Legacy Locked Bills (if present, `ORDER BY id ASC`):**
   ```text
   v2|legacy_bill:id={id}|order_id={order_id}|customer_id={customer_id}|subtotal={subtotal}|locked_at={locked_at}\n
   legacy_line:id={id}|spec_id={print_specification_id}|source_count={source_count}|print_count={print_count}|bill_qty={bill_quantity}|mode={quantity_resolution_mode}|unit_price={unit_price}|total={line_total}\n
   ```
5. **Empty State:**
   If no locked bills exist in the database:
   ```text
   v2|empty\n
   ```

### 4.3 Intentional Exclusions List

| Field | Exclusion Justification |
| :--- | :--- |
| `customer_bills.created_at` | Purely operational database timestamp recording row creation time. Does not alter billing calculation, quantities, legal customer terms, or snapshot provenance. |
| `customer_bills.updated_at` | Purely operational database timestamp recording row update time. Does not alter business snapshot meaning. |
| Live mutable tables (`orders`, `order_item_scans`) | Direct joins to live operational tables are intentionally prohibited. Locked bills must reflect strictly persisted frozen snapshot columns. |


---

## 8. Known Non-Blocking Deviations (accepted at Phase 1 gate)

These were reported by the Final Reviewer as non-blocking at commit `e229d5c`:

1. **Exit code 1 may have already-emitted JSON on stdout.** The report is written before the post-run source-integrity verification. Automation MUST treat any non-zero exit code as failure and ignore stdout. Fixing the ordering is scheduled for Phase 2 (or any inventory touch), together with a regression test.
2. **`bill_lines` is conditionally optional.** It may be absent only while there are no locked legacy `bills` rows. If locked legacy bills exist, the legacy snapshot hash requires `bill_lines` and absence fails with exit 8 (documented in the table above).
