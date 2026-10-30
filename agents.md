# AGENTS.md — Lalab Auto Report

> Operating instructions for Codex and any coding agent working in this repository.
>
> Product: **Lalab Auto Report**
>
> Primary reference: `docs/archive/PLAN_lalab.md`
>
> UX reference: `ux_ui.md`

---

## 1. Mission

Build **Lalab Auto Report** as a reliable, local-first Windows desktop application for a photo-print workshop.

The product converts the existing filesystem workflow into structured orders, billing decisions, and reports while minimizing changes to how the workshop already stores files.

The application must prioritize, in this order:

1. Billing correctness.
2. Preservation of filesystem/order provenance.
3. Explicit human review when data is ambiguous.
4. Low idle resource usage.
5. Fast, scoped scanning.
6. Simple day-to-day operation.
7. Maintainable architecture and tests.

Never trade correctness for automatic behavior when the filesystem is ambiguous.

---

## 2. Source of truth and document precedence

Before implementing a feature, read the relevant parts of:

1. `docs/archive/PLAN_lalab.md` — product rules, architecture, domain model, phases and acceptance criteria.
2. `ux_ui.md` — interaction design, screen behavior, review flows and visual states.
3. `AGENTS.md` — execution discipline and coding-agent rules.

If documents appear inconsistent, use this precedence:

```text
Explicit latest user requirement
> PLAN_lalab.md locked business rules
> ux_ui.md interaction rules
> AGENTS.md implementation guidance
> existing code behavior
```

Do not silently reinterpret a locked business rule to fit existing code.

---

## 3. Locked domain rules

Treat the following as non-negotiable unless the user explicitly changes them.

### 3.1 Physical folder = Order

A direct customer folder under a date folder is one physical Order.

Example:

```text
2026-09-28/
  Văn An/
  Anh An/
  A.An/
```

If all three aliases resolve to one canonical customer, they still remain **three separate Orders**.

Aggregation may occur in reports, but provenance must never be flattened away.

### 3.2 One file = one printed copy (standard) or one album sheet

No filename quantity multiplier exists in V1.

Standard print specifications:

```text
1 supported image file = quantity 1 printed copy
```

Album specifications (billing method `AlbumBasePlusExtra`):

- Each supported image file in the final print folder is one album sheet.
- The album is billed as one line. Its quantity stays 1; the sheet count is stored as `SheetCount`.
- The base price covers the first `IncludedSheets` sheets (default 10 when not configured).
- Each sheet beyond `IncludedSheets` is charged the extra-sheet price for the customer's price tier.

```text
album line total = base price + max(0, SheetCount - IncludedSheets) x extra-sheet price
```

### 3.3 Source count

For a print-specification folder:

```text
Date / Customer / Print Specification /
```

`SourceCount` is the number of supported image files **directly inside** the print-specification folder.

Do not recurse for SourceCount.

### 3.4 Print folder

The final print folder is not identified by name.

Resolve it structurally from image-bearing descendant folders. The normal automatic case is a single deepest image-bearing leaf folder.

If multiple valid leaf folders exist, mark the result ambiguous and require human selection.

Never silently choose between ambiguous candidates.

### 3.5 Print count and bill quantity

`PrintCount` is the count of supported image files directly inside the selected Print Folder.

Default:

```text
BillQuantity = PrintCount
```

`SourceCount` is for cross-checking.

If:

```text
SourceCount == PrintCount
```

quantity can resolve automatically.

If:

```text
SourceCount != PrintCount
```

billing must remain unresolved until the user explicitly chooses one of:

```text
USE_PRINT
USE_SOURCE
CUSTOM
```

Persist the selected quantity and the resolution mode.

### 3.6 Customer identity

A folder name is not a customer identity.

Use:

```text
Customer ID
Canonical name
Aliases
```

Known exact/normalized aliases may auto-map.

Fuzzy matching may only produce suggestions. Never silently fuzzy-merge customers.

### 3.7 Locked bills

A locked bill is a historical snapshot.

Ordinary rescans must never silently modify a locked bill.

A later filesystem difference may produce a warning/state, but changing the locked bill requires an explicit reopen/revision workflow.

### 3.8 Scan policy

V1 is **manual Smart Scan**, not continuous monitoring.

Do not add:

- realtime filesystem watchers,
- periodic background polling,
- automatic full-month rescans,
- idle directory crawling.

Supported scoped scan actions are defined in `PLAN_lalab.md`.

---

## 4. Implementation strategy

Implement incrementally by phase. Do not attempt the entire product in one undifferentiated change.

Recommended phase order:

```text
Phase 0  Project bootstrap
Phase 1  Core filesystem scanner
Phase 2  Customer + print-spec resolution
Phase 3  Scan snapshots + Smart Scan
Phase 4  Billing engine
Phase 5  Verification + locking
Phase 6  Dashboard + reports
Phase 7  Hardening
Phase 8  Packaging
```

At the end of each phase:

1. Run the relevant automated tests.
2. Run build/type/static checks as applicable.
3. Review changed files and git diff.
4. Compare implementation against phase acceptance criteria.
5. Report what is complete, what remains, known limitations and exact failing tests if any.

Do not proceed through a failing foundation silently.

---

## 5. Architecture boundaries

Keep these concerns separable:

```text
UI
Application Services
Domain
Infrastructure
```

The scanner must not depend on WPF controls or UI state.

The domain/billing logic must be independently testable without accessing the real filesystem.

Recommended service boundaries include:

```text
IScanService
IFileSystemAdapter
IFolderStructureParser
IPrintFolderResolver
ICustomerResolver
IBillingService
IReportService
ILockingService
ISettingsRepository
```

Exact names may differ, but responsibilities should remain clear.

Avoid putting business rules directly in ViewModels or code-behind.

---

## 6. Filesystem implementation rules

### 6.1 Never decode images for counting

Normal scan operations should enumerate filesystem metadata only.

Do not:

- open image pixels,
- generate thumbnails,
- read EXIF,
- inspect image contents,

unless a future feature explicitly requires it.

### 6.2 Supported extension filtering

Image extensions must be configurable and compared case-insensitively.

Start with the defaults from `PLAN_lalab.md`.

### 6.3 Relative paths

Store paths relative to the configured Root Folder wherever possible.

Do not couple persisted order identity unnecessarily to a drive letter.

### 6.4 Concurrent filesystem changes

A scan must tolerate files/folders being renamed, copied or deleted while scanning.

Recoverable filesystem problems must become scan warnings/errors attached to the relevant scope instead of crashing the application.

### 6.5 Never silently ignore unknown business folders

Unknown customer folder, unknown print specification, ambiguous print folder, missing print folder and inaccessible path must all produce explicit state.

---

## 7. Database and data-integrity rules

SQLite stores persisted decisions and history.

### 7.1 Historical scans

Rescanning should create a new scan snapshot rather than destructively overwriting all previous observation data.

### 7.2 Money

Store VND as integer values. Never use floating-point arithmetic for money.

### 7.3 Transactions

Use transactions for operations that must remain consistent, especially:

- bill creation,
- bill-line creation,
- bill locking,
- alias changes affecting identity mappings,
- schema migrations.

### 7.4 Auditability

Persist enough information to answer:

```text
What did the filesystem contain?
What did the app detect?
What ambiguity occurred?
What did the user choose?
Which quantity was billed?
Which price was used?
When was the bill locked?
```

### 7.5 Migrations

Database migrations must be explicit and repeatable.

Never rely on deleting the user's database as a normal upgrade path.

---

## 8. UI implementation rules

Follow `ux_ui.md`.

Key principles:

- review problems are visually prominent;
- normal successful rows are quiet;
- source, print and bill quantities are never conflated;
- canonical customer grouping never hides physical orders;
- destructive or irreversible actions require clear confirmation;
- locked historical data is visually distinct from current filesystem state;
- scanning must not freeze the main UI thread.

Do not add decorative complexity that reduces scan/bill readability.

---

## 9. State modeling

Prefer explicit enums/value objects over ad-hoc booleans.

Examples:

```text
OrderStatus
ScanStatus
PrintFolderResolutionStatus
CustomerResolutionStatus
PrintSpecificationResolutionStatus
QuantityResolutionMode
BillStatus
```

Avoid combinations such as:

```text
isScanned
isReviewed
isBilled
isLocked
hasError
```

when an explicit state machine is clearer and prevents invalid combinations.

---

## 10. Required automated tests

Tests are part of implementation, not optional cleanup.

### 10.1 Scanner fixtures

At minimum cover:

1. source files only / no print folder,
2. one child retouch level,
3. multiple linear retouch levels,
4. multiple competing leaf print folders,
5. empty source folder,
6. empty final folder,
7. supported + unsupported file mix,
8. uppercase/lowercase extensions,
9. inaccessible subfolder behavior,
10. path disappears during scan.

### 10.2 Customer resolution

Cover:

- exact alias match,
- normalized alias match,
- unresolved name,
- fuzzy suggestion does not auto-merge,
- alias collision,
- three physical folders mapping to one canonical customer while remaining three orders.

### 10.3 Billing

Cover:

```text
Source=100, Print=100 -> Bill=100, AUTO_MATCH
Source=100, Print=98  -> unresolved
USE_PRINT             -> Bill=98
USE_SOURCE            -> Bill=100
CUSTOM=99             -> Bill=99
```

Also test missing price, zero counts and invalid custom quantities.

### 10.4 Locking

Cover:

- unresolved order cannot lock,
- fresh verification scan occurs before lock,
- lock persists snapshot,
- later filesystem changes do not mutate locked values,
- explicit reopen path is separate from ordinary rescan.

### 10.5 Reporting

Cover:

- customer daily aggregation across multiple physical orders,
- monthly aggregation from database,
- opening monthly report does not invoke filesystem scanning,
- missing-day detection.

---

## 11. Performance expectations

### Idle

The application should be effectively idle when the user is not scanning.

No hidden continuous scan loop in V1.

### Scan

Prefer one-pass directory enumeration and avoid unnecessary allocations or repeated traversal.

Scoped actions must remain scoped:

```text
Rescan specification != rescan order
Rescan order != rescan date
Open monthly report != rescan month
```

### UI responsiveness

Longer scans run asynchronously with cancellation support where practical.

Never perform filesystem traversal synchronously on the WPF UI thread.

---

## 12. Error-handling discipline

Do not use broad exception swallowing.

For recoverable errors:

1. preserve the exception/context in logs,
2. surface a user-readable state,
3. keep unaffected orders usable,
4. allow scoped retry.

The app should degrade per order/specification instead of failing the entire date scan whenever possible.

---

## 13. Logging

Use structured local logging.

Log at least:

- application startup/version,
- database migration result,
- root-folder configuration changes,
- scan start/end/scope/duration,
- scan errors,
- print-folder ambiguity,
- customer/spec resolution changes,
- mismatch decisions,
- bill lock/reopen operations.

Never log image contents.

Avoid logging excessive per-file noise during normal successful scans.

---

## 14. Security and privacy

V1 is local-first.

Do not introduce telemetry, cloud upload, account requirements or remote APIs unless explicitly requested.

Customer/order data and filesystem paths should remain on the local machine by default.

Do not access folders outside the configured scan root except for explicit user-driven file/folder operations.

---

## 15. Change discipline for Codex

Before editing:

1. Inspect the current repository and relevant files.
2. Identify which phase/acceptance criterion the change belongs to.
3. Prefer the smallest coherent change that completes a behavior end-to-end.

While editing:

- preserve existing working behavior unless intentionally changing it;
- do not rewrite unrelated code for style;
- avoid speculative abstractions;
- keep naming aligned with domain terminology from `PLAN_lalab.md`;
- add migrations when persistence changes;
- add/update tests with behavior changes.

After editing:

1. run targeted tests first;
2. run the broader relevant suite;
3. build the app;
4. inspect git diff;
5. report changed files and validation results.

---

## 16. Prohibited shortcuts

Do not:

- infer customer identity solely by fuzzy similarity;
- infer print folder from folder names such as `retouch` or `final`;
- recursively count source files;
- sum all retouch levels as print quantity;
- silently pick one of multiple print-folder leaves;
- silently replace PrintCount with SourceCount on mismatch;
- mutate locked bill totals after rescan;
- flatten multiple same-customer physical folders into one Order;
- scan the whole archive simply to render a report;
- introduce realtime monitoring in V1 without an explicit requirement change;
- store money in floating point;
- block UI during normal scanning;
- hide recoverable errors from the user.

---

## 17. Definition of done for a feature

A feature is complete only when all applicable conditions are satisfied:

```text
[ ] Behavior matches PLAN_lalab.md
[ ] UX matches ux_ui.md
[ ] Domain rules are not implemented only in UI code
[ ] Persistence/migration is complete if required
[ ] Automated tests cover normal + important error paths
[ ] Relevant tests pass
[ ] Build succeeds
[ ] No unrelated regression is introduced
[ ] Error/review states are user-visible
[ ] Logs are sufficient for diagnosis
[ ] Documentation is updated when behavior changes
```

A UI mockup without working domain behavior is not complete.
A backend implementation without the required review UX is not complete.

---

## 18. Required completion report format

After each meaningful implementation batch, report concisely:

```text
STATUS: COMPLETE | PARTIAL | BLOCKED

IMPLEMENTED
- ...

KEY FILES CHANGED
- ...

VALIDATION
- Tests: ...
- Build: ...

KNOWN LIMITATIONS / FOLLOW-UP
- ...

NEXT RECOMMENDED PHASE
- ...
```

If tests fail, name the failing tests and distinguish new failures from pre-existing failures when evidence is available.

Never claim a test/build passed unless it was actually run.

---

## 19. First task for a fresh repository

When starting from an empty/new repo:

1. Read `PLAN_lalab.md`, `AGENTS.md`, and `ux_ui.md` completely.
2. Inspect the actual machine/project environment before selecting exact package versions.
3. Implement **Phase 0 only** first.
4. Establish test infrastructure and database migration discipline immediately.
5. Then implement **Phase 1 scanner** with fixture-based automated tests before billing logic.

The first milestone should prove that Lalab Auto Report can reliably understand the workshop's folder structure without modifying user files.
