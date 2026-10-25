# PLAN — LAN Phase 2: Command safety, auth/sessions, concurrency

> **Status:** Proposed (architect plan, not yet implemented)
> **Date:** 2026-10-25 · **Baseline:** `master` @ `6a64faf` (Phase 1 FINAL PASS @ `e229d5c`), clean tree
> **Risk class:** HIGH (auth + concurrency + writes)
> **Governing docs:** `docs/adr/ADR-002-lan-authoritative-server.md` D3/D4 (D8 for Cloud inbound), `docs/PLAN_LAN_SERVER_WORKSTATIONS.md` §6, §7, §8.1, §12 Phase 2, §13; `docs/LAN_PHASE0_INVENTORY.md` §3, §5; `docs/LAN_PHASE0_QUESTIONS.md` §9 (PO defaults Q-B1…Q-B7); `AGENTS.md`; `ux_ui.md` (PWA/Settings UI parts only)

Line numbers below were read at `6a64faf`. Grep by symbol before editing; do not trust line numbers blindly.

---

## 0. Scope

**Goal (ADR D3/D4):** make LAN writes safe and turn them on step by step. By the end of Phase 2 the server profile accepts exactly three write capabilities: `orders.setDelivered`, `orders.setPrinted` and `orders.editNote`. They go through revocable sessions, capability checks, desired-state commands, revisions, idempotency receipts, and audit/journal/outbox written in one transaction. Billing and payment writes stay `503 WRITES_DISABLED_UNTIL_PHASE2`.

Phase 2 also adds the **DB-level guards** for money paths: atomic bill-lock claim, revision columns with triggers on `customer_bills`, and split by revision. The desktop standalone profile uses these guards immediately. The LAN *commands* for billing and payment come in Phase 4.

The server stays **loopback-only** in Phase 2. LAN binding, TLS and device pairing are Phase 3. So "enabling writes" here means loopback/PWA-on-server and standalone PWA through the same handlers.

### Code findings that shape this plan (beyond the Phase 0 inventory)

| # | Finding | Evidence | Consequence |
|---|---|---|---|
| F1 | Lock verify **re-scans each order and rebinds `ProductJobId` to fresh `order_item_scans` rows**, so two drafts holding the same physical order lock *different* job IDs | `CustomerBillingService.cs:497-526` (`ScanOrderInRootAsync`, `line.ProductJobId = item.Id`) | ADR D4.6's job-level claim alone **cannot** stop a double bill. Phase 2 adds an **order-level claim** in the same transaction (§3.5). |
| F2 | The verify scan also **re-syncs the active draft** (`SaveBillAsync`, `SetCustomerBillIdForItemsAsync`) | `ScanService.cs:945-958`, `CustomerBillingService.cs:950-958` | A `customer_bills.revision` check at lock time would fail on the lock's own verify. The **lock revision check moves to Phase 4**, together with "verify without draft sync". Phase 2 locks with claim conditions only. |
| F3 | Draft jobs may already point to *another draft* (`customer_bill_id = draftId`) | `ScanService.cs:958`, `SplitBillAtomicAsync` `:274-279` | The claim condition must allow taking over from a **Draft** bill and reject only Locked/Exported owners. Otherwise valid locks after split/re-scan would break. |
| F4 | `SaveOrderAsync` (scan) UPDATE writes `is_printed/printed_at/print_progress` from the order object read at scan start | `SqliteOrderRepository.cs:309-316`; `ScanService.cs:590-632` | Scan vs set-printed race: a lost update not listed in inventory R-list. It is fixed with a revision-guarded write (§3.6). |
| F5 | Scan imports marker→`is_printed=true` when DB says not printed | `ScanService.cs:625-632` | This is the order-level form of R4 (resurrection). It gets guarded by `printed_revision = 0` (§3.7). |
| F6 | `PrintStatusService.ReconcileFolderStatusAsync` (R4) has **no callers** | grep: only `IPrintStatusService.cs:47` + impl `:309` | Dead code. Do not touch it in Phase 2; record it as a known item. |
| F7 | Desktop `SettingsView.xaml:596,602` shows PINs in plain `TextBox`; `SaveSettingsAsync` upserts every key | `SqliteSettingsRepository.cs:278-458` | After hashing, the PIN field becomes **write-only** (§2.2). This is a UX consequence that has to be communicated (§8). |
| F8 | Server readiness hard-codes schema 24 | `Server/Program.cs:281,308` | Replace with `DatabaseMigrator.LatestVersion`. |
| F9 | Inventory snapshot hash v2 uses an **explicit column list** (no `SELECT *`) | `ServerInventoryService.cs:466-490` | New `revision` columns do not change locked-snapshot hashes. A test locks this in. |
| F10 | PWA `sw.js` is network-only; on 401 the SPA calls `logout()` | `KestrelMobileWebServer.cs:1141-1146`; `MobileSpaHtmlProvider.cs:507` | Old cached JS is not a risk. A forced re-login only costs one PIN entry. |
| F11 | `crypto.randomUUID()` is unavailable in insecure contexts (`http://<LAN-IP>`) | Web platform | The PWA builds the operation ID from `crypto.getRandomValues` (§4.4). |

---

## 1. Migrations (V25–V29)

### 1.1 SQLite rules for every Phase 2 migration

- **Additive only.** Use `CREATE TABLE/INDEX/TRIGGER IF NOT EXISTS` and `ALTER TABLE … ADD COLUMN … NOT NULL DEFAULT <const>`. No DROP, no table rebuild, no column rename.
- **No CHECK constraints on enum-like columns** (`status`, `outcome`, `role`, …). SQLite cannot alter a CHECK without rebuilding the table. Validate enums in code. The only CHECKs allowed are singleton guards (`id = 1`).
- `ALTER TABLE ADD COLUMN` cannot add UNIQUE or PK. Use `CREATE UNIQUE INDEX` instead.
- **One concern per migration, one transaction per migration.** This uses the existing `DatabaseMigrator.MigrateToVersionAsync` loop (`:73-129`). SQLite DDL is transactional, so a failing migration rolls back completely and `schema_migrations` is not written. `ADD COLUMN` is not idempotent on its own; `schema_migrations` plus the per-migration transaction is the idempotency guard, matching the V2–V24 pattern.
- `foreign_key_check` after each migration stays as is.
- Server profile: backup fail-closed before migrate (Phase 1 `ServerDataLifecycle`). Standalone: unchanged warning behavior (`DatabaseMigrator.cs:55-66`). Do not change it in Phase 2.
- Add `public const int LatestVersion = 29;` to `DatabaseMigrator`. Add a unit test asserting it equals `GetMigrations().Max(Version)`.
- **Order matters.** The triggers (V29) reference the revision columns (V27) and `change_journal` (V29), and the receipts (V28) need nothing earlier. Apply in version order.
- **Downgrade tolerance:** the Phase 1 binary ignores unknown applied versions, because pending = not applied ∧ in its list. It runs on a V29 DB, and the triggers keep bumping revisions, which is harmless. Known downgrade effect: plaintext PINs were blanked by V25, so mobile login is disabled until the PIN is re-entered (§2.2 scrubber handles re-upgrade).

### 1.2 V25 `AuthCredentials` (credential storage) — decision: dedicated table

**Decision:** use a dedicated `auth_credentials` table, not `app_settings` keys. The reasons:

- `SaveSettingsAsync` upserts every key from several callers (R5).
- `GetSettingsAsync` hands every key to the UI.
- Keeping hashes in `app_settings` would let any whole-object save write stale hash or epoch values back.

```sql
CREATE TABLE IF NOT EXISTS auth_credentials (
    role              TEXT PRIMARY KEY,        -- 'Admin' | 'Staff' (validated in code)
    algorithm         TEXT NOT NULL,           -- 'PBKDF2-SHA256'
    iterations        INTEGER NOT NULL,        -- 600000 at creation; per-row so it can be raised later
    salt              BLOB,                    -- 16 random bytes; NULL = PIN not configured
    hash              BLOB,                    -- 32 bytes; NULL = PIN not configured
    credential_epoch  INTEGER NOT NULL DEFAULT 1,
    updated_at_utc    TEXT NOT NULL
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS auth_state (
    id                                INTEGER PRIMARY KEY CHECK (id = 1),
    legacy_tokens_issued_before_utc   TEXT NOT NULL,  -- legacy HMAC tokens must be issued before this
    legacy_tokens_accepted_until_utc  TEXT NOT NULL   -- read-only grace end; set to now() on any PIN change/revoke-all
);
```

**PostAction (same transaction):**

1. Read `app_settings` keys `AdminPin`, `StaffPin`.
2. For each role, `INSERT OR IGNORE` a row. If the trimmed plaintext is non-empty, store the PBKDF2 hash with a fresh salt; otherwise store `salt=NULL, hash=NULL`. **Rows for both roles always exist.** If a row were deleted when a PIN is cleared, the epoch would restart at 1 and revoked sessions would come back.
3. `UPDATE app_settings SET value='' WHERE key IN ('AdminPin','StaffPin')`.
4. `INSERT OR IGNORE INTO auth_state VALUES (1, now, now + 7 days)`.

Hashing two PINs at 600k iterations takes well under 1 s inside the migration. `MobileAuthSecret` stays: it is used only to validate legacy tokens during the grace window (§2.3). DPAPI protection of `MobileAuthSecret`/`CloudSyncSecret` is ADR D6 work for Phase 5, not Phase 2.

### 1.3 V26 `AuthSessions`

**Decision:** name it `auth_sessions`, not `mobile_sessions`. The same table serves PWA sessions now and desktop-client sessions in Phase 3.

```sql
CREATE TABLE IF NOT EXISTS auth_sessions (
    session_id        TEXT PRIMARY KEY,   -- 32 lowercase hex (128-bit random)
    token_hash        BLOB NOT NULL,      -- SHA-256(secret bytes)
    role              TEXT NOT NULL,
    credential_epoch  INTEGER NOT NULL,   -- role epoch at issuance
    client_kind       TEXT NOT NULL,      -- 'pwa' | 'desktop' (desktop used from Phase 3)
    device_id         TEXT,               -- self-declared, informational only (NOT an authenticator)
    device_label      TEXT,
    issued_ip         TEXT,
    issued_at_utc     TEXT NOT NULL,
    expires_at_utc    TEXT NOT NULL,
    last_seen_at_utc  TEXT,
    revoked_at_utc    TEXT,
    revoked_reason    TEXT                -- 'logout' | 'credential_changed' | 'revoke_all' | 'admin'
) WITHOUT ROWID;
CREATE UNIQUE INDEX IF NOT EXISTS ux_auth_sessions_token_hash ON auth_sessions(token_hash);
CREATE INDEX IF NOT EXISTS idx_auth_sessions_role_active ON auth_sessions(role, revoked_at_utc, expires_at_utc);
```

**Retention:** at server startup, after migration, delete rows where `expires_at_utc` or `revoked_at_utc` is older than 90 days. `audit_log` keeps the session ID string, so no FK is needed.

**`mobile_devices` is deferred to Phase 3.** Device pairing needs four things that do not exist in Phase 2, which is loopback-only with no desktop client:

- a one-time pairing code issued by Admin;
- a per-device secret;
- DPAPI storage on the workstation;
- TLS.

Adding the table now would be unused schema. Phase 2 records `device_id` only as an informational value supplied by the client.

### 1.4 V27 `AggregateRevisions`

```sql
ALTER TABLE orders ADD COLUMN delivered_revision INTEGER NOT NULL DEFAULT 0;  -- is_delivered, delivered_at, delivered_by
ALTER TABLE orders ADD COLUMN printed_revision   INTEGER NOT NULL DEFAULT 0;  -- is_printed, printed_at, print_progress
ALTER TABLE orders ADD COLUMN note_revision      INTEGER NOT NULL DEFAULT 0;  -- note
ALTER TABLE customer_bills ADD COLUMN revision   INTEGER NOT NULL DEFAULT 0;  -- every persisted change of the bill row
```

- **Backfill:** none. `DEFAULT 0` is the starting revision for every existing row. Nothing interprets 0 except the marker-import guard (§3.7).
- **Catalog revisions** (`customers`, `customer_aliases`, `print_specifications`, `root_folders`) are **deferred to the phase that first exposes catalog writes over the API** (Phase 3/4). No Phase 2 command touches the catalog, and the columns can be added later just as additively.
- **Rule:** application code never writes revision columns. Only the V29 triggers do. Commands only *compare* revisions in `WHERE`.

### 1.5 V28 `CommandReceiptsAndAudit`

```sql
CREATE TABLE IF NOT EXISTS command_receipts (
    operation_id      TEXT PRIMARY KEY,   -- client UUID normalised to Guid "N" (32 lowercase hex)
    capability        TEXT NOT NULL,      -- e.g. 'orders.setDelivered'
    entity_type       TEXT NOT NULL,      -- 'order'
    entity_id         INTEGER NOT NULL,
    payload_hash      TEXT NOT NULL,      -- 64 lowercase hex SHA-256 of canonical payload (§3.3)
    status            TEXT NOT NULL,      -- 'Committed' (only value written in Phase 2; no CHECK, see §1.1)
    result_json       TEXT NOT NULL,      -- exact response body returned on first commit
    actor_kind        TEXT NOT NULL,      -- 'session' | 'local_desktop'
    actor_session_id  TEXT,
    actor_role        TEXT NOT NULL,
    created_at_utc    TEXT NOT NULL,
    completed_at_utc  TEXT NOT NULL
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS idx_command_receipts_entity  ON command_receipts(entity_type, entity_id);
CREATE INDEX IF NOT EXISTS idx_command_receipts_created ON command_receipts(created_at_utc);

CREATE TABLE IF NOT EXISTS audit_log (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    occurred_at_utc   TEXT NOT NULL,
    action            TEXT NOT NULL,      -- capability, or 'auth.login'|'auth.logout'|'auth.revokeAll'|'auth.pinChanged'
    outcome           TEXT NOT NULL,      -- 'Committed' | 'NoChange' | 'Succeeded'
    operation_id      TEXT,
    entity_type       TEXT,
    entity_id         INTEGER,
    before_json       TEXT,
    after_json        TEXT,
    reason            TEXT,
    actor_kind        TEXT NOT NULL,
    actor_role        TEXT,
    actor_session_id  TEXT,
    actor_device_id   TEXT,
    actor_label       TEXT,               -- e.g. 'Nhân viên' (PIN is shared; never a person identity)
    client_ip         TEXT
);
CREATE INDEX IF NOT EXISTS idx_audit_log_entity    ON audit_log(entity_type, entity_id, id);
CREATE INDEX IF NOT EXISTS idx_audit_log_operation ON audit_log(operation_id);
CREATE INDEX IF NOT EXISTS idx_audit_log_time      ON audit_log(occurred_at_utc);
```

- **Receipt rows exist only for committed commands.** Rejections (401/403/404/409/422/503) leave nothing behind, because the business transaction rolled back. That is the "no partial receipt" invariant.
- **Retention:**
  - `command_receipts`: **no automatic deletion in Phase 2.** At fewer than 500 orders/day this is under about 1M rows/year. Phase 5 maintenance may purge rows older than 90 days for `orders.*` only, and **never** for `bills.*`/`payments.*`, per plan §7.
  - `audit_log`: never auto-deleted.
- Failed logins and capability denials go to the Serilog structured log, not `audit_log`. This avoids write amplification under PIN guessing; the rate limiter caps the volume anyway.

### 1.6 V29 `ChangeJournalAndRevisionTriggers`

```sql
ALTER TABLE server_identity ADD COLUMN database_generation INTEGER NOT NULL DEFAULT 1;

CREATE TABLE IF NOT EXISTS change_journal (
    seq                 INTEGER PRIMARY KEY AUTOINCREMENT,   -- AUTOINCREMENT: never reused -> safe cursor
    database_generation INTEGER NOT NULL,
    entity_type         TEXT NOT NULL,                       -- 'order' | 'customer_bill'
    entity_id           INTEGER NOT NULL,
    field_group         TEXT NOT NULL,                       -- 'delivered' | 'printed' | 'note' | 'bill'
    revision            INTEGER NOT NULL,                    -- revision AFTER the change
    operation_id        TEXT,                                -- set by command pipeline; NULL for non-command producers
    occurred_at_utc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_change_journal_entity ON change_journal(entity_type, entity_id, seq);

CREATE TRIGGER IF NOT EXISTS trg_orders_delivered_revision
AFTER UPDATE OF is_delivered, delivered_at, delivered_by ON orders
WHEN OLD.is_delivered IS NOT NEW.is_delivered
  OR OLD.delivered_at IS NOT NEW.delivered_at
  OR OLD.delivered_by IS NOT NEW.delivered_by
BEGIN
    UPDATE orders SET delivered_revision = OLD.delivered_revision + 1 WHERE id = NEW.id;
    INSERT INTO change_journal(database_generation, entity_type, entity_id, field_group, revision, occurred_at_utc)
    VALUES (COALESCE((SELECT database_generation FROM server_identity WHERE id = 1), 1),
            'order', NEW.id, 'delivered', OLD.delivered_revision + 1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
END;

-- trg_orders_printed_revision: same shape, columns is_printed, printed_at, print_progress -> printed_revision, 'printed'
-- trg_orders_note_revision:    same shape, column note -> note_revision, 'note'

CREATE TRIGGER IF NOT EXISTS trg_customer_bills_revision
AFTER UPDATE ON customer_bills
WHEN NEW.revision IS OLD.revision
BEGIN
    UPDATE customer_bills SET revision = OLD.revision + 1 WHERE id = NEW.id;
    INSERT INTO change_journal(database_generation, entity_type, entity_id, field_group, revision, occurred_at_utc)
    VALUES (COALESCE((SELECT database_generation FROM server_identity WHERE id = 1), 1),
            'customer_bill', NEW.id, 'bill', OLD.revision + 1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
END;
```

**Decision: bump revisions with triggers, not in each repository method.** ADR D4 and plan §7.8 require *every* producer to participate:

- scan `SaveOrderAsync`;
- `PrintStatusService` → `UpdateOrderPrintProgressAsync` / `…ByRelativePath`;
- Cloud inbound `ApplyEventAsync` (`SqliteCloudSyncStateRepository.cs:91`);
- desktop ViewModels (inventory §3.1/§3.4);
- the new command pipeline.

Triggers enforce this as a DB invariant that nobody can forget. The cost is logic that is not visible in repository code, so it gets two mitigations:

- (a) document it in `ARCHITECTURE.md` (§6 B4);
- (b) the `RevisionTriggerTests` suite (§5.3) asserts the behavior for each producer.

`recursive_triggers` is OFF by default, so the trigger's inner UPDATE does not fire it again. The `WHEN` clauses prevent bumps on no-op writes, for example scan re-writing an unchanged `is_printed`.

- **Cloud interplay (ADR D8):** when Cloud inbound writes `orders.note`, `is_delivered` or `customer_bills.is_paid`, the triggers bump the LAN revision. A LAN client holding the old revision then gets 409 instead of overwriting the change. The Cloud `cloud_field_revisions` stays a **separate** revision space; this plan does not claim the two are unified.
- **`change_journal` retention:** no consumer exists in Phase 2. The table only fills on real changes. Purging and cursor-expiry semantics belong to Phase 3, together with the change feed. Generation bumps on restore/reset are a Phase 5 item (backup/restore phase).
- **Inventory:** `ServerInventoryService` is unaffected (F9). `ValidateRequiredSchemaAsync` needs no new required tables.

### 1.7 Migration order summary

| Ver | Name | Concern | Batch |
|---|---|---|---|
| 25 | `AuthCredentials` | hashed PINs, epochs, legacy-token window | B1 |
| 26 | `AuthSessions` | stateful revocable sessions | B1 |
| 27 | `AggregateRevisions` | orders field-group revisions + bill revision | B2 |
| 28 | `CommandReceiptsAndAudit` | idempotency ledger + audit | B2 |
| 29 | `ChangeJournalAndRevisionTriggers` | journal, generation, triggers | B2 |

**Deferred tables:** `mobile_devices` (Phase 3) and catalog revisions (Phase 3/4).

**`side_effect_jobs` is deferred to the batch that first enqueues a job.** No Phase 2 command writes markers:

- PWA and desktop *order-level* printed is a DB flag only today (inventory §3.1).
- *Item-level* Explorer marking is Phase 5.
- `FolderVisualMarkerService` icon paths point at the user's `%LocalAppData%` (Q-B3 §3), so they are wrong under a service account anyway.

The DDL is fixed now so Phase 5 does not have to redesign it:

```sql
CREATE TABLE IF NOT EXISTS side_effect_jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_type TEXT NOT NULL,            -- 'folder_marker.apply' | 'folder_marker.remove'
    dedupe_key TEXT NOT NULL,          -- e.g. 'marker:order:123'
    entity_type TEXT NOT NULL, entity_id INTEGER NOT NULL,
    operation_id TEXT,                 -- originating command
    payload_json TEXT NOT NULL,        -- RootId + RelativePath, never absolute client paths
    status TEXT NOT NULL,              -- 'Pending'|'Running'|'Succeeded'|'Failed'|'Superseded'
    attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 5,
    next_attempt_at_utc TEXT NOT NULL, last_error TEXT,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS ux_side_effect_jobs_active ON side_effect_jobs(dedupe_key) WHERE status IN ('Pending','Running');
CREATE INDEX IF NOT EXISTS idx_side_effect_jobs_due ON side_effect_jobs(status, next_attempt_at_utc);
```

Semantics: a newer job marks the active one `Superseded` in the same transaction before inserting. Jobs are retried with bounded backoff. The DB is authoritative, and a job never writes business state. If ADR owners prefer the table to exist in Phase 2 anyway, it can ship as V30 in B2 with no producer. This plan recommends against shipping schema without a producer.

---

## 2. Auth, sessions, capabilities

### 2.1 PIN hashing

- **Algorithm:** `Rfc2898DeriveBytes.Pbkdf2(pinUtf8, salt16, iterations, HashAlgorithmName.SHA256, 32)`. This is inbox .NET 8 and uses the CNG backend, so **no new dependency**. Argon2 would need a third-party package, so it is rejected.
- **Iterations:** 600,000 (OWASP 2023 for PBKDF2-SHA256), stored per row. After a successful login, if the stored iterations are below the current constant, rehash in the same request.
- **Compare:** `CryptographicOperations.FixedTimeEquals(derived, stored)`.
- **Input normalization:** `pin.Trim()`. This matches the existing `MobileAuthService.cs:157,163`.
- **Honest limitation:** PINs are low entropy (4–6 digits). Hashing stops casual disclosure (backups, someone reading the DB or a support dump), but **not** a determined offline brute force. The primary control stays the DB ACL (ADR D6). A DPAPI-LocalMachine pepper is a possible Phase 5 add-on. Recommending ≥6-digit PINs is optional and is not enforced in Phase 2, since that would change behavior.
- **Kept invariants** (ADR D3):
  - empty Admin PIN means nobody can log in;
  - role PINs must differ — when setting a role PIN, verify the candidate against the *other* role's hash and reject on match;
  - no default PIN.

### 2.2 Credential store, migration and desktop compatibility

- **New `Application/Auth/IRoleCredentialStore`**, implemented by `Infrastructure/Data/SqliteRoleCredentialStore`. It provides:
  - `GetStatusAsync()` → `(AdminConfigured, StaffConfigured, epochs)`;
  - `VerifyAsync(pin)` → `Role?`;
  - `SetPinAsync(role, newPin, actor)`;
  - `ClearPinAsync(role, actor)`;
  - `BumpEpochAsync(role?, reason, actor)`.
- **`SetPinAsync` and `ClearPinAsync`** compute the hash *before* opening the transaction. Then, in one transaction, they:
  1. update the row;
  2. run `credential_epoch = credential_epoch + 1`;
  3. revoke sessions with `UPDATE auth_sessions SET revoked_at_utc=now, revoked_reason='credential_changed' WHERE role=@Role AND revoked_at_utc IS NULL`;
  4. run `UPDATE auth_state SET legacy_tokens_accepted_until_utc = now`;
  5. write the `audit_log` row `auth.pinChanged`, which never contains the PIN.
  
  If the candidate equals the current PIN, nothing changes and the epoch is not bumped.
- **`SqliteSettingsRepository`:**
  - `GetSettingsAsync` always returns `AdminPin = ""` and `StaffPin = ""`, plus new read-only flags `AppSettings.AdminPinConfigured` and `StaffPinConfigured` filled from `auth_credentials`.
  - `SaveSettingsAsync`: a **non-empty** `AdminPin`/`StaffPin` means "set this new PIN" and goes through `SetPinAsync`. **Empty means unchanged.** The `app_settings` PIN keys are always written as `''`.
  - The duplicate-PIN check moves from `SettingsViewModel.cs:862` (a plaintext compare) to the store.
- **Startup scrubber `CredentialPlaintextScrubber`.** It runs after migration in both profiles and is idempotent. If `app_settings` contains a non-empty plaintext PIN (for example after a downgrade and re-entry), it hashes it via `SetPinAsync` and blanks the key. V25's PostAction calls the same routine.
- **Desktop UI** (03 frontend, `SettingsViewModel.cs` + `SettingsView.xaml:596,602`, follow `ux_ui.md`):
  - Make the PIN inputs write-only: use a `PasswordBox` or an empty `TextBox`.
  - Show the status text "Đã đặt — để trống nếu không đổi" or "Chưa đặt" from the `…Configured` flags.
  - Add an explicit "Xóa PIN" action with confirmation. It calls `ClearPinAsync` and uses the existing confirmation dialog pattern.
  - Remove the plaintext duplicate check from the ViewModel; surface the store's error instead.
- **Legacy validation:** `MobileAuthService.AuthenticateWithPin(pin, settings)` (plaintext) is kept **only** so existing unit tests compile. Mark it `[Obsolete]`. No production caller remains after B1. Tests that drive login over HTTP (`MobileWebServerTests`, `MobileWebServerReadOnlyTests`) are updated to seed PINs through `IRoleCredentialStore` on a temp SQLite DB. That updates the setup only; the assertions are not weakened.

### 2.3 Sessions and token format

- **Token format** (opaque, no HMAC needed): `lsv2.<sessionId>.<secret>`.
  - `sessionId` is 32 lowercase hex characters.
  - `secret` is base64url of 32 random bytes.
  - The DB stores `token_hash = SHA-256(secretBytes)`.
- **Validation on every authenticated request:**
  1. parse the token;
  2. look up the row by `session_id` (PK, so cheap);
  3. compare `SHA-256(secret)` with `FixedTimeEquals`;
  4. check `revoked_at_utc IS NULL`;
  5. check `expires_at_utc > now`;
  6. check `credential_epoch == auth_credentials.credential_epoch(role)`.
  
  There is **no in-memory cache**, so revocation takes effect immediately. `last_seen_at_utc` is updated only when it is more than 5 minutes old, which avoids a write on every read.
- **Expiry:** absolute 30 days, no sliding. This matches today's `MobileAuthService.cs:12`. It is configurable in code and could be shortened later without a migration.
- **Revocation:**
  - logout revokes the current session;
  - a PIN change or clear revokes that role (§2.2);
  - `revoke-all` (Admin) bumps both epochs and revokes every session.
- **Login** (both `/api/auth/login` and `/api/v1/auth/sessions`):
  1. rate-limit check;
  2. `VerifyAsync`;
  3. insert the session;
  4. write `audit_log` `auth.login`;
  5. return the token.
  
  **The legacy login endpoint now issues v2 tokens** with the response shape unchanged (`{success, role, token, workshopName}`). The old PWA keeps working because it treats the token as an opaque Bearer value.
- **Legacy HMAC tokens** (`payload.sig`, two dot-separated parts) are accepted only when all of these hold:
  - the HMAC is valid (`MobileAuthService.TryValidateToken`, unchanged);
  - the token was issued before `auth_state.legacy_tokens_issued_before_utc`;
  - `now < legacy_tokens_accepted_until_utc` (migration + 7 days, or earlier after any PIN change/revoke-all);
  - **the request is a GET/HEAD.**
  
  Any write with a legacy token returns `401 SESSION_UPGRADE_REQUIRED`. The old SPA logs out on 401 (F10), and one PIN entry then yields a v2 token. `/api/v1/*` never accepts legacy tokens.
- **Query-string token:** `?token=` is accepted only for **GET** on image routes, matched by one regex:
  - `/api/orders/{id}/thumbnail`
  - `/api/orders/{id}/gallery/images/{fileId}`
  - `/api/orders/{id}/label`
  - `/api/bills/{id}/image`
  - `/api/bills/{id}/label`
  - `/api/bills/{id}/qr`
  
  Drop the `?auth=` alias after confirming the SPA no longer uses it (grep at B1). Writes and `/api/v1` accept only `Authorization: Bearer`.
- **CORS:** `/api/v1/*` gets no CORS policy, because the PWA is same-origin and native clients do not need CORS. The legacy `AllowAnyOrigin` stays unchanged for `/api/...` in Phase 2. Bearer tokens (not cookies) mean CSRF does not apply.

### 2.4 Rate limiting

- **`Application/Auth/LoginRateLimiter`** (singleton, in-memory, `TimeProvider`-injected for tests). The server is the single authority process, so in-memory state is sufficient. Restarting the service resets the counters; this is documented and needs admin rights anyway.
- **Keys and policy:**
  - **Per client IP** (`HttpContext.Connection.RemoteIpAddress`): 10 failures in 10 min lock the IP for 1 min. Each further lockout doubles, up to 15 min.
  - **Per IP+deviceId:** 5 failures in 10 min lock for 1 min. This keeps one misbehaving tab from locking out other devices behind the same IP.
  - A success resets both keys for that IP+device.
- **Response:** `429` with `Retry-After` and code `LOGIN_RATE_LIMITED`. The limiter runs **before** PBKDF2, so locked-out requests cost no CPU.
- The device ID is self-declared, which is why the IP key is the real limiter. A DB-persisted limiter is deferred and is not needed at pilot scale.

### 2.5 Device ID handling (Phase 2 vs Phase 3)

- **Phase 2:** the PWA creates a random device UUID once and stores it as `localStorage['lalab_device_id']`. It sends it in the login body as `deviceId` (≤64 chars, `[A-Za-z0-9-]`) together with an optional `deviceLabel`. The server stores it on the session and in audit rows. It is **never** used for authorization.
- **Deferred to Phase 3:** `mobile_devices`, one-time pairing codes, per-device secrets in DPAPI CurrentUser, "lock device" (which bumps epoch per device), and the server-identity confirmation at pairing.

### 2.6 Capability matrix (Q-B1 default: no Cashier role)

Constants live in `Contracts/Capabilities.cs`. The role map lives in `Application/Auth/RoleCapabilityMap.cs`.

| Capability | Staff | Admin | Phase-2 LAN write enabled |
|---|---|---|---|
| `orders.read` | ✔ | ✔ | — |
| `orders.setDelivered` | ✔ | ✔ | **yes** |
| `orders.setPrinted` | ✔ | ✔ | **yes** |
| `orders.editNote` | ✔ | ✔ | **yes** |
| `gallery.read` (incl. requests/touch, already allowed in Phase 1) | ✔ | ✔ | n/a |
| `labels.printOrder` (legacy `POST /api/orders/{id}/print-label`, physical print on server) | ✔ | ✔ | no (503 in server profile; D5/Phase 4) |
| `finance.read` (money fields, reports, unpaid, bill image/QR) | ✘ | ✔ | — |
| `bills.draft`, `bills.lock`, `bills.split`, `bills.overrideQuantityPrice`, `bills.reopen` | ✘ | ✔ | no (Phase 4) |
| `payments.confirm`, `payments.revoke` | ✘ | ✔ | no (Phase 4) |
| `catalog.edit`, `scan.request` | ✘ | ✔ | no (Phase 3/4) |
| `admin.maintenance` (thumbnail cleanup/clear) | ✘ | ✔ | no (503 in server profile) |
| `admin.sessions` (revoke-all) | ✘ | ✔ | yes (auth op, not business write) |

This maps 1:1 onto today's PWA rights (`KestrelMobileWebServer.cs:532,555,814-854,863,887,917,967,1052,1083,1097,1113`): Staff gets **no** new capability. The Cashier role, if PO adds it later, is just another row in `RoleCapabilityMap` and needs no migration.

**Where enforcement lives:**

- **Commands:** in the **Application handler**, as the first statement: `actor.Require(Capabilities.OrdersSetDelivered)`, which throws `CapabilityDeniedException` and maps to 403 `FORBIDDEN_CAPABILITY`. The handler cannot be reached without passing it, whatever the transport.
- **Legacy read endpoints** stay at endpoint level in Phase 2. Replace `auth.Role != MobileUserRole.Admin` with `actor.Has(Capabilities.FinanceRead)`, and replace `isAdmin` DTO filtering with `actor.Has(FinanceRead)`. Behavior is identical. Moving reads into Application query services is Phase 3.
- **Mapping HTTP to capability:** `Infrastructure/Web/LanWriteGate.cs` holds a static route table `(method, path regex) → capability`. The server-profile write gate (§4.3) uses this table. Handlers re-check the capability themselves; the gate is not the authority.
- **Operation lookup** only returns a receipt if the caller holds the receipt's `capability`. Otherwise it returns `not_found`, so finance operations do not leak to Staff in Phase 4.

---

## 3. Commands and concurrency

### 3.1 Phase 2 command contracts (`Contracts/Commands/OrderOperationalCommands.cs`)

```csharp
public sealed record SetOrderDeliveredRequest(string OperationId, long ExpectedRevision, bool Delivered);
public sealed record SetOrderPrintedRequest  (string OperationId, long ExpectedRevision, bool Printed);
public sealed record SetOrderNoteRequest     (string OperationId, long ExpectedRevision, string? Note);

public sealed record OrderFieldStateDto(long OrderId, string FieldGroup, long Revision, JsonElement Value,
                                        bool Changed, string OperationId, string? SyncState);
```

- **`OperationId`:** a client UUID in any `Guid.TryParse` format, normalized to `"N"` (32 lowercase hex). That is also the format `cloud_operational_outbox.operation_id` already uses (`SqliteCloudSyncStateRepository.cs:115`). An invalid value returns 422.
- **Server-derived values** are not part of the client contract or the hash:
  - delivered: `delivered_at = now`; `delivered_by` = "Chủ tiệm" for Admin, "Nhân viên" for Staff, exactly as `KestrelMobileWebServer.cs:621-622`; `NULL` when `false`;
  - printed: `printed_at = now` or `NULL`; `print_progress` = `Printed` or `NotPrinted`, the same values the desktop order-level toggle writes.
- **Note normalization**, matching `KestrelMobileWebServer.cs:685`: `Trim()`, and empty becomes `null`. Maximum 4000 characters, otherwise 422. That is a new bound on request size (§8).

**Explicitly deferred to Phase 4:** `bills.setPaid` (desired-state payment), `bills.lock`, `bills.reopen`, `bills.split`, draft build/review, overrides and adjustments. Their legacy endpoints stay `503` in the server profile. Standalone keeps today's behavior for those endpoints, unchanged apart from the trigger revision bumps.

### 3.2 Layering and files

```text
Infrastructure/Web (HTTP)  ->  Application handler (authz, validate, canonicalize, hash)
                           ->  Infrastructure store (ONE SQLite transaction: receipt, conditional UPDATE,
                                                      audit, journal tag, cloud outbox, receipt insert)
```

- `Application/Commands/OrderOperationalCommandHandler.cs` provides `SetDeliveredAsync`, `SetPrintedAsync` and `SetNoteAsync(ActorContext, request, ct)`.
- `Application/Commands/IOrderOperationalCommandStore.cs` is implemented by `Infrastructure/Data/SqliteOrderOperationalCommandStore.cs`.
- `Infrastructure/Data/SqliteCommandPipeline.cs` is a reusable transactional skeleton. It is used for orders now and for bills in Phase 4.
- `Application/Commands/ICommandPipelineObserver.cs` is an internal hook with a no-op default. Tests use it for barriers and fault injection (§5.4). It is never registered in production.
- `Application/Commands/InFlightOperationRegistry.cs` (in-memory) reports `pending` for an operation whose transaction is still open.

### 3.3 Payload hash canonicalization

- **Canonical form:** a UTF-8 JSON string written by `Utf8JsonWriter`, with no whitespace and a **fixed property order**. All strings use the already-normalized values from §3.1.

  ```json
  {"v":1,"cap":"orders.setNote","entity":"order","id":123,"expectedRevision":4,"value":{"note":"..."}}
  ```

- The hash is the SHA-256 of that string as 64 lowercase hex characters.
- `v` is the canonicalization version. Bumping it later never collides with old receipts, because the hash differs and the result is an IDEMPOTENCY_CONFLICT on reuse, which is correct.
- `expectedRevision` **is included.** If a client re-reads and changes its intent, it must use a new `OperationId`.
- A golden-vector unit test pins the exact bytes for each command.

### 3.4 Exact transaction pattern (`SqliteCommandPipeline.ExecuteAsync`)

Before the transaction, in the handler:

1. Authenticate the session (§2.3).
2. Run `actor.Require(capability)`.
3. Validate and normalize the payload.
4. Compute the hash.
5. Register the operation ID in `InFlightOperationRegistry`. If it is already in flight, return `409 OPERATION_IN_PROGRESS`; the client polls the lookup.

Inside **one** transaction (`connection.BeginTransaction()`; Microsoft.Data.Sqlite issues `BEGIN IMMEDIATE` for non-deferred transactions, and the test `Pipeline_UsesImmediateTransaction` proves it). The connection's command timeout is **5 s** for SQLITE_BUSY handling. Do not change the global connection factory.

```text
 1. r = SELECT * FROM command_receipts WHERE operation_id=@Op
    r exists && r.payload_hash == @Hash && r.capability == @Cap -> COMMIT(no-op tx); return r.result_json (200, header Idempotent-Replay: true)
    r exists && (hash or capability differs)                   -> ROLLBACK; 409 IDEMPOTENCY_CONFLICT
 2. cur = SELECT <value cols>, <group>_revision FROM orders WHERE id=@Id
    cur missing -> ROLLBACK; 404 NOT_FOUND
 3. if cur.value == desired  (convergent desired-state)        -> outcome NoChange, newRev = cur.revision, skip 4-6
    else if cur.revision != @Expected                          -> ROLLBACK; 409 STALE_REVISION {currentRevision, currentValue}
 4. affected = UPDATE orders SET <cols>=@Values, updated_at=@Now
               WHERE id=@Id AND <group>_revision=@Expected     (trigger bumps revision + inserts journal row)
    affected != 1 -> ROLLBACK; 409 STALE_REVISION (defence in depth; unreachable under IMMEDIATE)
 5. newRev = SELECT <group>_revision FROM orders WHERE id=@Id
    UPDATE change_journal SET operation_id=@Op
      WHERE entity_type='order' AND entity_id=@Id AND field_group=@Group AND revision=newRev AND operation_id IS NULL
 6. delivered/note only: SqliteCloudSyncStateRepository.EnqueueAsync(conn, tx, 'order_delivered'|'order_note', id, value, actor, operationId: @Op)
    (printed has no Cloud outbox today, inventory §3.1 — unchanged)
 7. INSERT audit_log (action=@Cap, outcome=Committed|NoChange, before/after json, actor fields, client_ip)
 8. INSERT command_receipts (status='Committed', result_json=<response>)
 9. COMMIT
```

- **Why the receipt check comes first** (ADR D4.3): a retry of a committed command must get the stored success back, not a fake `STALE_REVISION`. Under `BEGIN IMMEDIATE`, a concurrent duplicate with the same operation ID blocks at `BEGIN` and then sees the committed receipt at step 1. The PK on `operation_id` is the last backstop: a constraint violation means rollback, then re-run step 1 on a fresh transaction once.
- **Convergent no-op (step 3):** setting `printed=true` when the row is already `true` succeeds whatever `expectedRevision` the client sent. Both clients that wanted the same state succeed, and nothing ever gets toggled. A NoChange outcome still writes the receipt and the audit row, but no outbox row and no revision bump.
- **409 body:**

  ```json
  {"code":"STALE_REVISION","message":"Đơn vừa được cập nhật trên thiết bị khác.",
   "entityType":"order","entityId":123,"fieldGroup":"note",
   "currentRevision":5,"currentValue":{"note":"..."},"correlationId":"..."}
  ```

  For notes this lets the UI show the current text while keeping the user's draft (plan §7.6).
- **Busy:** `SqliteException` with SQLITE_BUSY or SQLITE_LOCKED after 5 s returns `503 SERVER_BUSY` with `retryable: true` and the same operation ID. Nothing was committed, so a retry is safe.
- **Rollback guarantee:** any exception between `BEGIN` and `COMMIT` → `Rollback()` (in a `catch`, then rethrow mapped error). The receipt is inserted last (step 8), so no partial receipt can exist without the business change.
- **Error handling:** do not swallow exceptions. Map known domain exceptions to the error envelope. Anything else returns 500 with a correlation ID and is logged with context, never with PIN or token.

### 3.5 Bill-lock claim: job and order level, same transaction (Phase 2, DB guard)

These changes go in `SqliteCustomerBillRepository.LockCustomerBillAtomicAsync` (`:286-323`). The surrounding flow (`CustomerBillingService.LockBillAsync`) stays **unchanged**, and the billing math is untouched.

```sql
-- (a) order-level: no included order of this bill is already included in ANOTHER Locked/Exported bill (F1)
SELECT COUNT(*) FROM customer_bill_orders cbo
JOIN customer_bills cb ON cb.id = cbo.bill_id
WHERE cbo.order_id IN @OrderIds AND cbo.is_included = 1
  AND cbo.bill_id <> @BillId AND cb.status IN ('Locked','Exported');
-- > 0  -> rollback, throw BillClaimConflictException (maps to 409 BILL_CLAIM_CONFLICT; desktop shows message)

-- (b) job-level claim: may take over from NULL, self, or a DRAFT owner; never from Locked/Exported (F3)
UPDATE order_item_scans SET customer_bill_id = @BillId
WHERE id IN @Ids
  AND (customer_bill_id IS NULL OR customer_bill_id = @BillId
       OR customer_bill_id IN (SELECT id FROM customer_bills WHERE status = 'Draft'));
-- affected != @Ids.Count (distinct) -> rollback, throw BillClaimConflictException
```

- Both statements run **after** `SaveBillInternalAsync` and **before** `UPDATE orders SET status='Billed'`, all in the existing transaction. Under `BEGIN IMMEDIATE`, two concurrent locks serialize: the second one sees the first one's Locked rows and fails.
- **Rule for valid splits:** the split moves orders between **drafts** in its own transaction (`:248-284`), so (a) and (b) both allow it. `is_included = 1` means an order that was *excluded* in a locked bill can still be billed elsewhere. This is purely a guard against double billing and does not change any rule.
- **Not in Phase 2:** a `customer_bills.revision` check at lock (F2). It moves to Phase 4, when verify stops re-syncing drafts. The Phase 4 prerequisite tests are listed in §5.5.

**Split** (`SplitBillAtomicAsync`) changes only how staleness is detected:

- Replace the `expectedUpdatedAt` check (`:262`) with `stored.revision == original.Revision`.
- `CustomerBill` gains `Revision`, loaded by the repository DTO mapping.
- The `expectedUpdatedAt` parameter is removed. Update the callers (`CustomerBillingService` split path and `CustomerBillReviewViewModel.cs:1105` chain) to pass the loaded bill.
- The semantics are equivalent: any persisted change bumps both `updated_at` and `revision`. Revisions are just exact, while timestamp round-trips could drift.

**Reopen vs payment, payment vs lock:** in Phase 2 these get only trigger revision bumps. There is no new rule (`ReopenBillAsync` may currently reopen a paid bill; that business decision is under Q-B1/Phase 4). The commands that will carry an expected revision are Phase 4.

### 3.6 Scan vs set-printed (F4) — Phase 2

In `SqliteOrderRepository.SaveOrderAsync`, UPDATE branch (`:309-316`), the printed group gets a revision guard:

```sql
is_printed     = CASE WHEN printed_revision = @ObservedPrintedRevision THEN @IsPrinted     ELSE is_printed     END,
printed_at     = CASE WHEN printed_revision = @ObservedPrintedRevision THEN @PrintedAt     ELSE printed_at     END,
print_progress = CASE WHEN printed_revision = @ObservedPrintedRevision THEN @PrintProgress ELSE print_progress END,
```

- `@ObservedPrintedRevision` is `order.PrintedRevision`, captured when the scan read the existing order. `Order` gains `DeliveredRevision`, `PrintedRevision` and `NoteRevision`, mapped in the order DTO.
- SQLite evaluates SET expressions against the old row, so the guard is atomic within the statement.
- The scan UPDATE does not write delivered or note today, so no change is needed there. The test `ScanSaveOrder_DoesNotTouchDeliveredOrNote` confirms it.
- The `PrintStatusService` writers (`UpdateOrderPrintedStatusAsync`, `…ByRelativePath`, `UpdateOrderPrintProgressAsync`) stay desktop/Explorer producers. They get revisions and journal rows through the trigger automatically, with no code change. They remain last-write-wins for **standalone desktop** until Phase 3/5 routes them through commands; that limitation is recorded in §7.

### 3.7 Marker → DB: no resurrection (F5, ADR D4.9)

In `ScanService.cs:625`, change the guard to `if (!order.IsPrinted && order.PrintedRevision == 0 && _printStatusService != null)`. The marker is imported only when the DB has never recorded a printed decision for that order.

`PrintStatusService.GetStatusAsync`, which recovers marker→DB only when **no** DB record exists, already complies with ADR D4.9 and is unchanged. `ReconcileFolderStatusAsync` is dead code (F6) and is left untouched, with a note in §7.

### 3.8 Cloud outbox uses the command operation ID

`SqliteCloudSyncStateRepository.EnqueueAsync(…, string? actor = null, string? operationId = null)` (`:109-116`) uses `operationId ?? Guid.NewGuid().ToString("N")`. Existing callers are unchanged. The command store passes the command's operation ID, so one LAN command produces exactly one Cloud operation, and replays return early at step 1 without enqueuing again. With Cloud disabled (server profile Phase 1/2), `EnqueueAsync` is already a no-op (`:111-112`).

### 3.9 Operation lookup

`GET /api/v1/operations/{operationId}` responds as follows:

| Situation | Response |
|---|---|
| Receipt exists and the caller holds its capability | `{status:"committed", capability, entityType, entityId, result}` |
| Operation is in the in-flight registry | `{status:"pending"}` |
| Neither | `{status:"not_found"}` |

`not_found` means nothing was committed and nothing is in flight, so the client may retry **with the same operation ID**. The ADR's `failed` status is not persisted in Phase 2, because rejected commands leave no record (§1.5). It stays reserved for Phase 4 long-running operations.

---

## 4. Server/API and PWA

### 4.1 New `/api/v1` endpoints

New file: `Infrastructure/Web/ApiV1Endpoints.cs` (static `MapApiV1(WebApplication, IServiceProvider)`), called from `KestrelMobileWebServer.ConfigureEndpoints`. This keeps the 1269-line file from growing.

| Method + path | Capability | Notes |
|---|---|---|
| `POST /api/v1/auth/sessions` `{pin, deviceId?, deviceLabel?, clientKind}` | — | rate-limited; returns `{token, sessionId, role, capabilities[], expiresAtUtc}` |
| `GET /api/v1/auth/sessions/current` | any session | role, capabilities, expiry |
| `DELETE /api/v1/auth/sessions/current` | any session | logout = revoke |
| `POST /api/v1/admin/sessions/revoke-all` `{role?}` | `admin.sessions` | bumps epoch(s) |
| `GET /api/v1/orders/{id}/operational-state` | `orders.read` | `{delivered:{value,at,by,revision}, printed:{value,at,revision}, note:{value,revision}}` |
| `POST /api/v1/orders/{id}/delivered` | `orders.setDelivered` | body `SetOrderDeliveredRequest` |
| `POST /api/v1/orders/{id}/printed` | `orders.setPrinted` | body `SetOrderPrintedRequest` |
| `POST /api/v1/orders/{id}/note` | `orders.editNote` | body `SetOrderNoteRequest` |
| `GET /api/v1/operations/{operationId}` | receipt's capability | §3.9 |

- **Handshake** (ADR D9): commands require the header `X-Lalab-Client-Api: 1.x`. If it is missing or the major version differs → `426 CLIENT_TOO_OLD` / `SERVER_TOO_OLD`. `Contracts/ApiVersion.cs` holds `Current = "1.0"` and `MinClient = "1.0"`. Reads do not require the header.
- **Error envelope** for every `/api/v1` error: `{code, message (vi), correlationId, …context}`. It uses the codes from `Contracts/ApiErrorCodes.cs`:
  - `UNAUTHORIZED`, `SESSION_EXPIRED`, `SESSION_REVOKED`, `SESSION_UPGRADE_REQUIRED`;
  - `FORBIDDEN_CAPABILITY`, `LOGIN_RATE_LIMITED`;
  - `IDEMPOTENCY_CONFLICT`, `OPERATION_IN_PROGRESS`, `STALE_REVISION`;
  - `NOT_FOUND`, `VALIDATION_FAILED`, `SERVER_BUSY`;
  - `WRITES_DISABLED_UNTIL_PHASE2`, `CLIENT_TOO_OLD`, `SERVER_TOO_OLD`, `BILL_CLAIM_CONFLICT`.
- **Legacy read DTOs** gain *additive* fields `deliveredRevision`, `printedRevision` and `noteRevision` on `GET /api/orders` and `GET /api/orders/{id}`. Existing fields are unchanged.

### 4.2 Legacy PWA adapters (toggle → set-state, explicitly not retry-safe)

`POST /api/orders/{id}/toggle-delivered`, `/toggle-printed` and `/note` keep their URL and response shape, but their bodies become adapters:

1. Authenticate. A legacy token here returns `401 SESSION_UPGRADE_REQUIRED` (§2.3).
2. Read the operational state (value + revision).
3. Compute the target: toggles use `!value`; note uses the body value.
4. Generate a **server-side** operation ID, which is why the adapter is *not retry-safe* and is commented as such.
5. Call the same handler with `ExpectedRevision = read revision`.
6. On `STALE_REVISION` return `409 {success:false, code, message:"Đơn vừa được cập nhật trên thiết bị khác. Vui lòng tải lại."}`.

These adapters exist **in both profiles**. Standalone PWA writes therefore also go through receipts, audit, journal and outbox.

Not touched in Phase 2: `print-label`, `bills/{id}/print`, `toggle-payment` and `settings/thumbnails/*`. In standalone they keep today's code and behavior, with trigger revision bumps only. In the server profile they stay 503.

### 4.3 Removing the 503: driven by capability, behind a flag

- `MobileWebServerOptions.ReadOnlyMode` (bool) is replaced by `EnabledWriteCapabilities` (`IReadOnlySet<string>?`). `null` means every capability is enabled, which is standalone and today's behavior.
- The middleware in `ConfigureEndpoints` (`:208-226`) does this for each non-GET/HEAD/OPTIONS request:
  - auth and session routes and the gallery `requests`/`touch` routes are always allowed, as `IsAllowedWritePath` does today;
  - otherwise it resolves the route's capability through `LanWriteGate`;
  - if the capability is not in the enabled set, or the route is unmapped, it returns `503 WRITES_DISABLED_UNTIL_PHASE2` with the **same code** as Phase 1 and an updated message;
  - unmapped routes fail closed.
- `IsAllowedWritePath` stays as a thin wrapper so the existing tests keep their meaning.
- **Server `Program.cs`:**
  - New CLI flag `--lan-writes <none|orders>`.
  - Until the B3 gate passes, the default is `none`, which keeps the Phase 1 behavior. When the gate passes, B3 flips the default to `orders`, which means `{orders.setDelivered, orders.setPrinted, orders.editNote}`.
  - **Rollback is instant:** restart with `--lan-writes none`, with no schema change.
  - Readiness also changes: use `DatabaseMigrator.LatestVersion` instead of 24 (F8), and expose the capability list in `/health/ready` as ADR D9 asks.
- **End of Phase 2, server profile:** note, delivered and printed are enabled. Payment, billing, print endpoints and maintenance stay 503.

### 4.4 PWA update (B3, same release as the adapters)

File: `Infrastructure/Web/MobileSpaHtmlProvider.cs`, owned by 03.

- **Login:** send `deviceId` (generated once, stored in `localStorage`) and `deviceLabel` (`navigator.userAgent` truncated).
- **Operation ID:** UUID v4 built from `crypto.getRandomValues(new Uint8Array(16))` with the version and variant bits set (F11). Never use `crypto.randomUUID`.
- **Toggles at `:699`, `:725` and the note at `:762`:** call `/api/v1/orders/{id}/delivered|printed|note` with `{operationId, expectedRevision, <value>}`. The revision comes from the order DTO, and the header `X-Lalab-Client-Api: 1.0` is sent.
- **Retry policy:**
  - On network error or timeout, call `GET /api/v1/operations/{id}`.
  - If `committed`, apply the result.
  - If `pending`, poll with backoff 1 s, 2 s, 4 s, up to 10 s in total, then show "Chưa xác nhận được, hãy tải lại".
  - If `not_found`, resend the **same** request once.
  - The operation ID lives only in memory. After a reload the user just presses the desired state again, and the convergent no-op (§3.4) makes that safe.
- **409 `STALE_REVISION`:** refresh the order card. For notes, keep the typed text in the textarea and show the current server note above it with "Ghi chú đã được đổi trên thiết bị khác — xem lại trước khi lưu". Never overwrite automatically.
- **`401 SESSION_UPGRADE_REQUIRED` / `SESSION_REVOKED`:** use the existing `logout()` and show the message "Phiên đã hết hiệu lực, vui lòng nhập lại PIN".
- **Query tokens:** keep `?token=` only in the image `src` URLs listed in §2.3.
- **Payment buttons** (`:1120`, `:1465`): unchanged. In the server profile they show the existing 503 message.
- **Must pass:** `MobileSpaScriptSyntaxTests`, plus the visual states from `ux_ui.md`. Review problems are prominent and successful rows stay quiet.

---

## 5. Mandatory test matrix (owner 04; names are binding)

All tests use a **real temp SQLite DB**, following the existing patterns (`TestFileSystemFixture`, `MobileWebServerReadOnlyTests` real `HttpClient`). Concurrency tests use **barriers** through `ICommandPipelineObserver` (§3.2), not sleeps.

### 5.1 Auth — `AuthCredentialMigrationTests.cs`, `SessionAuthTests.cs`, `LoginRateLimiterTests.cs`, `CapabilityMatrixTests.cs`

- `V25_HashesExistingPlaintextPins_AndBlanksAppSettings`
- `V25_NoPinsConfigured_CreatesRowsWithNullHash_LoginDisabled`
- `V25_IsIdempotent_RerunMigrateDoesNothing`
- `PlaintextScrubber_RehashesPinReenteredByOlderBinary`
- `Pbkdf2_VerifiesCorrectPin_RejectsWrong_UsesFixedTimeCompare`
- `SetPin_RejectsDuplicateOfOtherRole`
- `SetPin_SameValue_DoesNotBumpEpoch`
- `SettingsRepository_GetSettings_NeverReturnsPin_SetsConfiguredFlags`
- `SettingsRepository_SaveEmptyPin_LeavesCredentialUnchanged`
- `Login_IssuesV2Token_SessionRowPersisted`
- `Login_LegacyEndpoint_ReturnsV2TokenSameShape`
- `Session_Expired_Returns401SessionExpired`
- `Session_Logout_RevokesImmediately`
- `PinChange_BumpsEpoch_RevokesRoleSessions_OtherRoleUnaffected`
- `RevokeAll_RevokesEverySession_AndEndsLegacyWindow`
- `LegacyToken_AcceptedForGetWithinWindow`
- `LegacyToken_RejectedAfterWindow`
- `LegacyToken_RejectedAfterPinChange`
- `LegacyToken_WriteReturns401SessionUpgradeRequired`
- `LegacyToken_RejectedOnApiV1`
- `QueryToken_AcceptedOnlyForImageGets`
- `QueryToken_RejectedForWritesAndApiV1`
- `RateLimit_LocksIpAfter10Failures_RetryAfterHeader`
- `RateLimit_LockoutDoublesUpTo15Minutes`
- `RateLimit_SuccessResetsDeviceKey`
- `RateLimit_LockedRequestDoesNotRunPbkdf2`
- `Capability_StaffCannotReadFinanceFields`
- `Capability_StaffGets403OnFinanceEndpoints` (reports/unpaid/bill image/qr/toggle-payment)
- `Capability_StaffMatrixEqualsPhase1PwaRights`
- `Capability_HandlerDeniesEvenIfGateBypassed`
- `OperationLookup_HidesReceiptWithoutCapability`

### 5.2 Commands — `OrderCommandIdempotencyTests.cs`

- `SameOperationSamePayload_ReturnsStoredResult_AfterAuth` (also: an expired or revoked session gets 401 before the replay)
- `SameOperationDifferentPayload_Returns409IdempotencyConflict`
- `SameOperationDifferentCapability_Returns409IdempotencyConflict`
- `RetryAfterCommit_ReturnsSuccessNotStale` (A commits, B bumps the revision, A retries with the old expected revision → stored success)
- `StaleRevision_Returns409WithCurrentRevisionAndValue`
- `SetPrintedTrueTwice_NoToggle_SecondIsNoChange`
- `SetDelivered_ConvergentNoOp_IgnoresStaleExpected`
- `Commit_WritesReceiptAuditJournalOutbox_InOneTransaction` (assert all four rows plus the business row; journal `operation_id` = command ID; outbox `operation_id` = command ID)
- `Printed_DoesNotEnqueueCloudOutbox` (parity)
- `NoChange_WritesReceiptAndAudit_NoOutboxNoRevisionBump`
- `FaultBeforeReceiptInsert_RollsBackBusinessChange_NoPartialReceipt`
- `PayloadHash_GoldenVectors`
- `OperationId_NormalisedToGuidN_InvalidReturns422`
- `Note_TrimmedEmptyBecomesNull_Over4000Returns422`
- `Pipeline_UsesImmediateTransaction`
- `Busy_Returns503ServerBusy_Retryable_NoCommit`
- `OperationLookup_CommittedPendingNotFound`

### 5.3 Revisions and triggers — `RevisionTriggerTests.cs`

- `Trigger_DeliveredChange_BumpsDeliveredRevisionOnly_AndJournals`
- `Trigger_NoOpWrite_DoesNotBump`
- `Trigger_ScanSaveOrder_UnchangedPrinted_DoesNotBump`
- `Trigger_CloudApplyEvent_BumpsLanRevision_LanClientGets409`
- `Trigger_PrintStatusServiceWriter_BumpsPrintedRevision`
- `Trigger_CustomerBillUpdate_BumpsRevisionOnce_NoRecursion`
- `Inventory_SnapshotHashUnchangedAfterV25toV29` (fixture DB inventory before and after the migration: identical counts, totals, hashes)
- `Migrator_LatestVersionEqualsMaxMigration`
- `PhaseOneBinarySchemaList_IgnoresV25toV29` (simulated: run `MigrateToVersionAsync(24)` on a V29 DB → no error)

### 5.4 Concurrency — `OrderCommandConcurrencyTests.cs`, `BillLockClaimTests.cs`, `MarkerResurrectionTests.cs`, `CommandCrashRecoveryTests.cs`

- `TwoClients_SetDifferentDeliveredStates_OneWinsOther409` (barrier inside A's transaction; B blocks on `BEGIN IMMEDIATE`)
- `TwoClients_SameDesiredState_BothSucceed_OneNoChange`
- `TwoClients_DifferentFieldGroups_NoCrossConflict` (A sets the note while B sets delivered; both commit)
- `NoteConflict_SecondGets409WithFirstNote_NoOverwrite`
- `DuplicateInFlightOperation_Returns409OperationInProgress_ThenReplay`
- `LockClaimRace_TwoDraftsSameOrder_OnlyOneLocks` (two drafts, the verify scan produces different job IDs (F1), barrier between the locks → exactly one Locked, the other gets `BillClaimConflictException`, and the losing bill stays Draft)
- `LockClaim_JobOwnedByLockedBill_Rejected`
- `LockClaim_JobOwnedByOtherDraft_TakenOver`
- `LockClaim_ExcludedOrderInLockedBill_CanBeBilledElsewhere`
- `SplitThenLock_ValidSplitStillLocks` (existing `SplitBillTests` must pass unchanged in assertions)
- `SplitVsLock_ConcurrentSplitAfterLock_Rejected`
- `Split_StaleRevision_Rejected`
- `ScanVsSetPrinted_ScanDoesNotOverwriteConcurrentCommand` (barrier: scan reads the order, the command sets printed=false, scan saves → printed stays false)
- `ScanSaveOrder_DoesNotTouchDeliveredOrNote`
- `ScanVsVerify_LockStillVerifiesFreshObservation` (regression of the existing verify behavior; no new rule)
- `MarkerResurrection_ExplicitUnprintedOrder_ScanDoesNotReimportMarker`
- `MarkerImport_NeverDecidedOrder_StillImports` (keeps today's behavior for revision 0)
- `CrashAfterBusinessUpdate_BeforeCommit_NoChangeNoReceipt` (child process)
- `CrashAfterCommit_BeforeResponse_RetryReturnsStoredResult` (child process)
- `RestartBetweenCommitAndRetry_ReceiptSurvives`

**Crash harness:** add a new test-only console project `tests/LalabAutoReport.CrashHarness` (net8.0-windows). It references Infrastructure and Application and registers an `ICommandPipelineObserver` that calls `Environment.FailFast` at a named point, chosen by argument. The test spawns it against a temp DB and then inspects the DB with a fresh connection. This satisfies plan §13 ("process riêng").

### 5.5 Reopen vs payment, payment vs lock

**Phase 2** tests only that the revision is bumped by both paths: `ReopenAndPayment_BothBumpBillRevision`.

**Phase 4 prerequisites** (listed here; must exist before `bills.*`/`payments.*` writes are enabled):

- `SetPaid_DesiredState_NoToggle`
- `SetPaid_StaleRevision_409`
- `ReopenVsPayment_ConcurrentOnlyOneApplies`
- `LockRevisionCheck_AfterVerifyWithoutDraftSync`
- `TwoMachinesLockOverlappingOrders_OneResult`
- `PaymentAudit_RevokeRequiresReason`

### 5.6 HTTP integration and regression — `ApiV1EndpointTests.cs`, `LegacyPwaAdapterTests.cs`, existing suites

- `ApiV1_CommandWithoutClientApiHeader_Returns426`
- `ApiV1_NoCorsHeaders`
- `LegacyToggle_RoutesThroughHandler_WritesReceipt`
- `LegacyToggle_StaleReturns409LegacyShape`
- `LegacyNote_SameShapeAsPhase1`
- `ServerProfile_LanWritesNone_All503` (Phase 1 parity)
- `ServerProfile_LanWritesOrders_OnlyThreeEnabled_PaymentPrintMaintenanceStill503`
- `ServerProfile_UnmappedPost_FailsClosed503`
- `Ready_ReportsLatestSchemaAndCapabilities`

**Unchanged suites that must still pass:** the full existing suite (692 tests at `e229d5c`), especially `BillingEngineTests`, `EffectiveBillingFolderTests`, `FinalPrintFolderAndBillingTests`, `CustomerBillingTests`, `SplitBillTests`, `GuestBillingTests`, `PaymentStatusAndDebtTests`, `DurableCloudSyncTests`, `CloudPrintRecoveryTests`, `OrderDeliveryAndNoteTests`, `PrintProgressWorkflowTests`, `FolderPrintStatusTests`, `DatabaseUpgradeBackupRestoreFixtureTests`, `ServerTests`, `MobileSpaScriptSyntaxTests`.

---

## 6. Batch breakdown

Owners: **02** backend, **03** frontend (WPF/PWA), **04** tests. Within a batch, file ownership does not overlap, and 04 never edits `src/`. Each batch ends with targeted tests, then the full suite, then `dotnet build` for the solution, the diff review and the AGENTS §18 report. The parent integrates; a reviewer approves *after* the final code of the batch.

### B1 — Auth foundation (writes still 503 in the server profile)

**02 owns:**

- New projects:
  - `src/LalabAutoReport.Contracts/` (net8.0): `Capabilities.cs`, `ApiErrorCodes.cs`, `ApiVersion.cs`, `Auth/*Dto.cs`;
  - `src/LalabAutoReport.Application/` (net8.0, refs Core + Contracts): `Auth/ActorContext.cs`, `RoleCapabilityMap.cs`, `Pbkdf2PinHasher.cs`, `IRoleCredentialStore.cs`, `ISessionStore.cs`, `SessionService.cs`, `LoginRateLimiter.cs`, `CapabilityDeniedException.cs`;
  - both added to `LalabAutoReport.slnx`.
- `Infrastructure/Data/DatabaseMigrator.cs`: V25, V26 and `LatestVersion`.
- New `Infrastructure/Data/SqliteRoleCredentialStore.cs`, `SqliteSessionStore.cs`, `CredentialPlaintextScrubber.cs`.
- `Infrastructure/Data/SqliteSettingsRepository.cs` (PIN semantics).
- `Core/Domain/Models.cs` (`AdminPinConfigured`/`StaffPinConfigured`).
- `Core/Services/MobileAuthService.cs` (`[Obsolete]` on the plaintext method; legacy validation kept).
- `Infrastructure/Web/KestrelMobileWebServer.cs`: auth plumbing only.
  - `CheckAuth` → `AuthenticateAsync` returning `ActorContext`.
  - The login endpoint uses `SessionService`.
  - Restrict the query token.
  - Swap role checks for capability checks with identical behavior.
- New `Infrastructure/Web/ApiV1Endpoints.cs` with the auth/session routes only.
- `Infrastructure/Composition/LalabServiceRegistration.cs`.
- `Server/Program.cs`: readiness `LatestVersion`, scrubber and session purge at startup.

**03 owns:** `UI/ViewModels/SettingsViewModel.cs` and `UI/Views/SettingsView.xaml` (write-only PIN UX, §2.2).

**04 owns:** the §5.1 tests, updated HTTP test setups (seed credentials), and `Migrator_LatestVersionEqualsMaxMigration`.

**Acceptance:**

- the §5.1 tests are green;
- an old PWA can log in (it receives a v2 token) and read;
- standalone PWA writes work with a v2 token and return 401 with a legacy token;
- the server profile still returns 503 for every business write;
- the desktop Settings PIN set/clear works and is audited;
- the full suite and the build are green.

**Rollback:** the Phase 1 binary runs on a V26 DB. PINs need re-entry (documented).

### B2 — Revisions, receipts, command pipeline, DB guards (no new HTTP surface)

**02 owns:**

- `DatabaseMigrator.cs`: V27, V28, V29.
- `Core/Domain/Models.cs` (order/bill revisions).
- `Infrastructure/Data/SqliteOrderRepository.cs` (DTO mapping, guarded `SaveOrderAsync`).
- `Infrastructure/Data/SqliteCustomerBillRepository.cs` (claim guards, split by revision, DTO revision).
- `Core/Interfaces/Interfaces.cs` (`SplitBillAtomicAsync` signature).
- `Core/Services/CustomerBillingService.cs`: split caller **only**, no billing-math edits.
- `UI/ViewModels/CustomerBillReviewViewModel.cs`: split call site **only**.
- `Core/Services/ScanService.cs`: marker guard line only.
- `Infrastructure/Data/SqliteCloudSyncStateRepository.cs` (`EnqueueAsync` operation ID).
- New `Application/Commands/*` (§3.2, §3.3).
- New `Infrastructure/Data/SqliteCommandPipeline.cs`, `SqliteOrderOperationalCommandStore.cs`, `SqliteCommandReceiptReader.cs`.

**04 owns:** the §5.2, §5.3 and §5.4 tests, and the new `tests/LalabAutoReport.CrashHarness/`.

**Acceptance:**

- the handler- and repository-level matrices pass, including the crash harness;
- the inventory dry-run on the fixture is identical before and after the migration (except schema version and new tables);
- no billing test assertion changed except the mechanical `SplitBillTests` signature update (review checks this);
- the full suite and the build are green.

**Rollback:** the triggers are harmless under the old binary. `--lan-writes` is not changed yet.

### B3 — HTTP commands, legacy adapters, PWA, enable the three writes

**02 owns:**

- `Infrastructure/Web/ApiV1Endpoints.cs` (order commands, operational state, lookup, handshake).
- `Infrastructure/Web/KestrelMobileWebServer.cs` (adapters, revision DTO fields, write-gate middleware).
- New `Infrastructure/Web/LanWriteGate.cs`.
- `Infrastructure/Web/MobileWebServerOptions.cs`.
- `Infrastructure/Composition/LalabServiceRegistration.cs`.
- `Server/Program.cs` (`--lan-writes`; default flips to `orders` **only in the gate commit**).

**03 owns:** `Infrastructure/Web/MobileSpaHtmlProvider.cs` (§4.4).

**04 owns:** the §5.6 tests and the PWA script syntax test updates.

**Gate (all required before flipping the default):**

1. Every §5 Phase 2 test is green, the full suite is green and the build has 0 warnings and 0 errors.
2. Manual loopback smoke against the server profile, with evidence in `docs/qa-evidence-phase2/`:
   - Staff and Admin log in;
   - set delivered, printed and note;
   - a forced retry with the same operation returns the replay;
   - two browser tabs produce a stale conflict, and the note draft is preserved;
   - after a PIN change, the old tab is logged out;
   - payment, print and maintenance still return 503.
3. Fixture inventory before and after the full upgrade: identical counts, totals and locked-snapshot hashes.
4. Code review LGTM on the final B3 diff, plus independent QA PASS.

**Rollback:** `--lan-writes none` immediately restores the Phase 1 surface. There is no schema rollback.

### B4 — Hardening, load and documentation (04 lead, 02 fixes only)

- **Load test:** 10 concurrent clients on representative fixture data, with mixed reads and set-state commands, with scan running in parallel. Measure p95 (target: reads under 1 s, state commands under 2 s, plan §13) and count SQLITE_BUSY → `SERVER_BUSY`. Record it as evidence; these are targets, not claims.
- **Documentation:**
  - `ARCHITECTURE.md`: auth/session model, capability enforcement location, revision triggers, command pipeline, journal;
  - `ADR-002` §6 "Phase 2 gate result";
  - `DEVELOPMENT_WORKFLOW.md`: the `--lan-writes` flag and the B3 smoke procedure;
  - `docs/PLAN_LAN_SERVER_WORKSTATIONS.md` §18 status line.
- **Acceptance:** the evidence is recorded and the Final Reviewer passes it.

### Do NOT change in Phase 2

- **Billing math:**
  - `BillingService`;
  - `CustomerBillingService` recalculation, `RecalculateTotals`, the lock verify flow, the draft sync in `ScanService`;
  - pricing tiers, album rules;
  - the C1–C10 conflict register, which stays frozen.
- **Locked-snapshot invariants:** `SaveBillInternalAsync` immutability checks (`:374-394`), `SnapshotContent`, the inventory snapshot hash v2 format.
- **Integer VND** everywhere; no new money columns.
- Migrations V1–V24; the Cloud wire protocol (ADR-001); the gallery; the renderers; `WpfShippingLabelExporter` printing.
- The AutoScan and Cloud enablement in the server profile (Q-B4, Q-B7); the loopback-only binding.
- **Standalone desktop ViewModel writers** (Dashboard toggles, label auto-deliver, payment toggle) stay last-write-wins. Moving them to commands is Phase 3. They participate only through the triggers.

---

## 7. Risks and non-goals

### Risks

| ID | Risk | Mitigation |
|---|---|---|
| P2-R1 | Trigger logic is invisible from repository code | Documented in ARCHITECTURE; §5.3 suite per producer; rule "app never writes revision columns" |
| P2-R2 | `BEGIN IMMEDIATE` contention with scan transactions → busy | 5 s busy window, `503 SERVER_BUSY` retryable with same operation ID; B4 load measurement; scan transactions are already short (`SaveOrderAsync`) |
| P2-R3 | Low-entropy PIN hashes are brute-forceable offline | Stated honestly; DB ACL (D6) is the primary control; optional pepper in Phase 5 |
| P2-R4 | Lock still verifies by re-scanning, which re-syncs drafts (F2) | Phase 2 relies only on claim guards (which hold regardless); revision check deferred with explicit Phase 4 tests |
| P2-R5 | Standalone desktop toggles can still overwrite a PWA change made after the dashboard loaded | Pre-existing; triggers make it visible to LAN clients (they get 409); fixed in Phase 3 (client facade + conflict UX) |
| P2-R6 | Users must log in again once to write after the upgrade | Old SPA's existing 401→logout path; release note |
| P2-R7 | Desktop PIN becomes write-only | Inherent to hashing (ADR D3 approved); clear "Đã đặt" state and an explicit clear action |
| P2-R8 | `ReconcileFolderStatusAsync` resurrection logic remains (dead code) | No callers (F6); flagged for removal or fix when Explorer moves to the server (Phase 5) |
| P2-R9 | .NET 8 end of support on 2026-11-10, inside the Phase 2 window | Q-B6: separate runtime batch; Phase 2 adds no runtime-specific API beyond net8 inbox crypto |
| P2-R10 | `command_receipts` grows without purge | Small volume at Q-B2 scale; purge policy defined for Phase 5 (never for finance) |

### Non-goals for this session (Phase 2)

- No LAN binding, TLS, firewall, device pairing, `mobile_devices` or DPAPI secrets (Phase 3/5).
- No bill, draft, lock, reopen, split or payment **commands or endpoints**, no artifacts, no server-side printing (Phase 4).
- No catalog revisions or catalog commands; no settings field-level revisions (R5 stays covered by single-writer server ownership).
- No `side_effect_jobs` producer or worker; no marker writes from the service (Phase 5).
- No change-feed or SignalR consumers, no journal purge (Phase 3).
- No Cashier role (Q-B1); no change to Cloud roles or the worker.
- No standalone desktop ViewModel migration to commands (Phase 3); no Explorer or headless path changes beyond Phase 1.
- No runtime upgrade; no new NuGet dependencies.

---

## 8. Decisions and PO input

**Decisions taken in this plan (architect):**

- a dedicated `auth_credentials` table;
- `auth_sessions` with opaque `lsv2` tokens;
- PBKDF2-SHA256 at 600k iterations;
- legacy tokens read-only for 7 days;
- in-memory rate limiter;
- revision triggers;
- convergent no-op desired-state;
- receipts stored only for committed commands;
- order-level plus job-level lock claim;
- `side_effect_jobs` deferred;
- capability-set write gate behind `--lan-writes`.

**Truly blocking PO decisions: none.** Phase 2 stays within the ADR D3/D4 decisions and the PO defaults approved for Q-B1…Q-B7.

`LAN_PHASE0_QUESTIONS.md` §9 requires PO to *acknowledge* Phase 2 behavior changes. Present them as one release-note list, not as open questions:

1. The desktop PIN can no longer be viewed. Empty means unchanged, and there is an explicit "Xóa PIN" action.
2. PWA users log in once more before their first write after the upgrade.
3. A lock is now **rejected** when the same included order is already in another Locked/Exported bill. Before, it silently succeeded, which is a double-bill fix.
4. Scan no longer re-marks an order as printed from an old folder marker after someone explicitly un-printed it.
5. Order notes are limited to 4000 characters.


---

## 9. Phase 2 B3 gate result (25/10/2026)

- B1 commit `0f2a7b9`, B2 `5ec3e21`, B3 + gate `209914c` on `master` (not pushed/deployed).
- Validation: build 0 warnings/0 errors; **810 tests passed**; QA PASS (evidence `docs/qa-evidence-phase2/`); Code Reviewer LGTM after gate/PWA fixes.
- Default flipped: `LalabAutoReport.Server --lan-writes` now defaults to `orders`; `--lan-writes none` is the instant rollback. Payment, print and maintenance remain `503 WRITES_DISABLED_UNTIL_PHASE2`.
- Verified at gate: v1 note/delivered/printed 200 with revisions/idempotency/stale 409; legacy adapters; revoke-all 200 Admin / 403 Staff; inventory unchanged by migration; PWA conflict preserves note draft.
- Not verified: Session 0 Windows Service execution/rendering; 10-client load (B4); desktop client mode (Phase 3).
