# Agent Progress — Lalab Auto Report

Updated: 2026-10-30. Git history and current inspection are authoritative.

## Current milestone

- Current source of truth: branch `master`, HEAD `cdbac5c` (v2.1.3 released 2026-10-30), synced with `origin/master`.
- LAN Phase 2 remains recorded as FINAL PASS. LAN Phase 3 (desktop workstation client) was explicitly cancelled on 2026-10-27. On 2026-10-30, the Product Owner confirmed the direction: continue independent desktop/PWA/cloud installations per workshop, each with its own DB, credentials, and printer. Shared LAN server/workstation phases are out of scope; do not resume Phase 3/4.
- The latest tag is `v2.1.2` at `f692e37`; HEAD contains later untagged fixes, including `43389f1` for clearing payment state when reopening a paid bill.
- Changes after the 2026-10-27 handoff include removing the headless Server/LoadHarness projects, command-receipt retention, CI workflow updates, removal of legacy PIN login paths, gallery nullable annotations, and the paid-bill reopen fix. See `git log` for exact commits.
- Validation evidence in this file is older than HEAD. The 2026-10-27 master check recorded 861 passed and 3 failures in `LiveAutoUpdateAcceptanceTests` because those tests expected `v2.0`; it is not evidence for HEAD `1d52aea`.
- Validation on 2026-10-29: `dotnet test LalabAutoReport.slnx --nologo -p:UseAppHost=false` passed 805/805 tests in 3m35s. Build emitted MSB3061 because the running app PID 33200 holds the existing UI apphost EXE; the solution projects and test assembly built, and tests completed successfully. Plain `dotnet test LalabAutoReport.slnx --nologo` could not build while that EXE was locked. The running app remains the prior 2.1.2 process, so this test result does not mean the live app loaded HEAD.
- Runtime updated on 2026-10-30 via `DEV_START.bat`: build succeeded with 0 warnings/errors; old PID 33200 was replaced by PID 6024. The new process reports ProductVersion `2.1.0+1d52aea` and owns TCP 5050. `/api/status` returns HTTP 200 on loopback and `100.81.149.85`. Tailscale lists `iphone-13-pro-max`; `tailscale ping` from PC A timed out, but the Product Owner reported phone access/login/order view/bill-label print PASS on PC A on 2026-10-30. No PC-B node appeared in the observed tailnet inventory.

## Active tasks

- No coding task is currently in progress.
- PC A phone acceptance is reported PASS by the Product Owner on 2026-10-30. Next operational check: onboard and validate PC B only when that independent workshop is available/in scope.
- Product direction is resolved: keep each workshop independent. No shared LAN database/client milestone is active.
- Runtime update is complete for PC A. The app is running from HEAD `1d52aea`; restart is not needed unless another source change is made.
- Update 2026-10-30 (later, supersedes the two lines above): PC A runs the official release **v2.1.3** (ProductVersion `2.1.3+cdbac5c`), updated through the in-app updater. The executable SHA-256 matches the published `LalabAutoReport.exe.sha256`. PWA returns HTTP 200 on `127.0.0.1:5050` and on `100.81.149.85:5050`.

## Completed work

- Gallery PC/LAN/Cloud + retention + thermal labels (xem mục trước trong Git history/QA evidence).
- LAN Phase 1:
  - Shared composition root `AddLalabCore(options)`; desktop standalone giữ nguyên hành vi; Kestrel/SPA chuyển sang Infrastructure.
  - `LalabAutoReport.Server` (console/Windows Service-ready): `--data-dir` (mặc định `%ProgramData%\LalabAutoReport\Server`), Global mutex + `server.lock`, backup fail-closed trước migrate, migration V24 `server_identity`, health live/ready loopback với BuildId.
  - Mobile API re-host loopback-only; gate đọc-only trả 503 `WRITES_DISABLED_UNTIL_PHASE2` cho business writes; auth/login + gallery manifest/touch được phép; Cloud/AutoScan tắt.
  - `inventory --dry-run`: không tạo file/thư mục trong data dir, snapshot WAL-safe qua Backup API, fail-loud exit 8 khi thiếu schema bắt buộc, exit 7 khi thiếu DB; hash snapshot v2 đầy đủ header/line/adjustment.
  - `render-spike`: render bill JPEG + tem trong tiến trình server, hash pixel khớp desktop.
  - Headless `--toggle-printed` từ chối (exit 5) khi `server.lock` đang bị giữ.

## Phase 1 handoff context (historical)

The following Phase 1 notes describe the design and review state at that milestone. The current milestone and active tasks at the top of this file supersede them.

## Important technical decisions (Phase 1; historical)

- The then-proposed model used one writer host for server-owned DB/scan/cloud and API-only clients. The server/workstation implementation was later cancelled and removed from `master`.
- Phase 1 kept business writes disabled until Phase 2 added revisions and idempotency.
- Inventory was designed as a read-only cutover tool; the headless server and inventory tooling were later removed from the current solution.

## Gate / review status

- QA: PASS (evidence `docs/qa-evidence-phase1/QA_REPORT.md`, addendum #3).
- Code Reviewer: LGTM cho range chính; fixes được re-review.
- Final Reviewer: **FINAL PASS** tại `e229d5c`; 2 non-blocking deviations ghi ở `docs/phase1-inventory-contract.md` §8.

## Phase 1 follow-up items (historical)

These items were recorded before LAN Phase 2. Do not treat them as current tasks; consult the current milestone above and the Phase 2 final status below. Windows Service Session 0 was not verified, and the server profile had Cloud/AutoScan disabled. The server inventory-specific caveats no longer apply after removal of that host.

## Commits

- `9f3bfba` gallery/labels; `5960bfa` docs gallery/LAN Phase 0.
- `08f1b3f` composition root + move transport.
- `e29b3ae` server host skeleton.
- `739ea1e` read-only loopback API.
- `425bf86` renderer spike + headless guard.
- `fdb4d7f`, `6513edf`, `e229d5c` final-review fixes.

## Phase 2 progress (historical detail; final status below is authoritative)


- **B1 — Auth foundation: DONE** (commit `0f2a7b9`, review LGTM).
  - V25 `auth_credentials` (PBKDF2-SHA256 600k, per-role epoch), V26 `auth_sessions`; `LatestVersion=26`.
  - Legacy plaintext PIN migrated once + startup scrubber; PIN change/clear revokes role sessions and legacy grace.
  - `lsv2` opaque sessions (hashed at rest, revoke/expiry/epoch), login rate limit, capability matrix; legacy HMAC GET/HEAD-only and pre-cutoff only; query token restricted to image GET.
  - Settings PIN write-only + explicit clear; server business writes still 503.
  - Validation: build 0 warnings; 735 tests pass; runtime smoke (lsv2 login, pre/post-cutoff legacy, standalone v2 write 200 vs server 503, plaintext cleared, audit no PIN).
  - Behavior changes to release-note: PIN not viewable (empty = unchanged, explicit clear); PWA re-login once; (double-bill/marker/note-limit changes land in B2/B3).
- **B2 — Revisions/receipts/command pipeline: DONE** (commit `5ec3e21`, review LGTM).
  - V27 aggregate revisions, V28 command_receipts/audit, V29 change_journal + revision triggers; `LatestVersion=29`.
  - Idempotent desired-state command handler with receipt-before-revision, stale 409 + current value, crash harness, lock claim guards, marker non-resurrection, outbox operation id; no HTTP surface yet.
  - Validation: 781 tests; inventory on V24 copy identical before/after V29 migration (hash/counts same).
- **B3 — HTTP v1 commands + legacy adapters + PWA + order writes: DONE** (commit `209914c`, QA PASS, reviewer LGTM after fixes).
  - `/api/v1` delivered/printed/note, operational-state, operation lookup, handshake; legacy toggle/note adapters.
  - LanWriteGate exact allowlist; default `--lan-writes orders` (payment/print/maintenance still 503; rollback `--lan-writes none`).
  - PWA set-state + revisions/operationId/409 draft preservation/lookup retry; UUID via `crypto.getRandomValues` (LAN-safe).
  - Validation: 810 tests; runtime default note 200, payment 503, revoke-all 200; QA PASS; re-review LGTM.
- **B4 (hardening/load/docs/final review): COMPLETE**; see Phase 2 final status below.

Phase 2 release-note behavior changes (acknowledged): PIN no longer viewable (empty = unchanged, explicit clear); PWA re-login once after upgrade; lock rejected if an included order is already in another locked/exported bill (double-bill fix); scan no longer resurrects an old printed marker after explicit un-print; notes limited to 4000 characters.

The following gaps were listed before B4 and are historical; B4 completion and the current milestone above supersede this snapshot.


---

## Phase 2 final status (26/10/2026)

- **B4 complete; Phase 2 milestone = FINAL PASS** (Final Reviewer, uncommitted B4 content; auth race commit `44a53d4`).
- Commits: B1 `0f2a7b9`, B2 `5ec3e21`, B3+gate `209914c`, docs `ebf61a3`, auth-race fix `44a53d4`, B4 harness/docs commit (this batch).
- Validation: solution build 0 warnings; **856 tests pass**; load harness 60s/10 clients (fresh fixture) PASS — 12,437 requests, reconciliation 2,669/2,669, reads p95 8.1 ms (<1 s), commands p95 323.3 ms (<2 s), 0 SQLITE_BUSY, integrity/FK/receipt/journal/audit OK. Fault injections (missing receipts, orphan audit, corrupted results) fail with nonzero exit.
- Enabled writes: default `--lan-writes orders` (delivered/printed/note); payment/print/maintenance remain 503; rollback `--lan-writes none`.
- Evidence: `docs/qa-evidence-phase2/` (QA + B4 load report/JSON/README).
- Remaining limitations: Session 0 Windows Service install/rendering NOT VERIFIED; scan contention is simulated (real scan disabled in server profile per Q-B7); production-scale performance unproven; command receipt retention needs later maintenance; desktop standalone still writes its own LocalAppData DB (Phase 3 client); desktop startup credential scrubber optional.
- **Phase 3 (desktop workstation client) — ĐÃ HỦY theo quyết định Product Owner (27/10/2026).** Không có code Phase 3 trên master. Code/tài liệu Phase 3 được lưu trong bundle `artifacts/archive/phase3-archive-20261027.bundle` (ref cũ: `backup/phase3-wip-20261026`, tag `phase3-final-20261026`, `pre-phase3-restore`; đã xóa khỏi repo, kiểm tra bằng `git bundle verify`).
- Next: Phase 4 chưa bắt đầu; chờ Product Owner xác nhận hướng đi (LAN desktop/workstation hay roadmap khác) trước khi dispatch.


---

## Runtime & Cloud deployment snapshot (27/10/2026; historical)

- **.NET 10 LTS**: toàn bộ solution retarget `net10.0`/`net10.0-windows`; WindowsServices 10.0.12; workflow/scripts/docs cập nhật; commit `c274598`. Build Debug/Release 0 errors, full suite 858 passed trên net10; app net10 chạy LAN (PID 107796) với PWA/Tailwind local.
- **Cloud production**: worker `9226ad9a-9ef0-408e-aa4a-7c4c23ae6917` deployed; D1 migration `0008_bill_label_snapshots.sql` applied (3 cột bill snapshot còn thiếu do 0007 đã được apply bản cũ). Xem `docs/CLOUD_DEPLOY_20261027.md`; backup D1 `artifacts/cloud-d1-backup-20261027.sql` (local).
- Verify: health 200; PWA gallery/label 200; gallery request 401; sync jobs 403; D1 đủ bảng gallery và cột bill; secrets giữ nguyên.
- Lưu ý LAN PWA: Tailwind đã vendor local (commit `63d1e4b`), không phụ thuộc internet.

- **Tailscale 2 xưởng**: runbook docs/TAILSCALE_TWO_WORKSHOPS.md; máy A 	iniwin01 100.81.149.85 đã verify HTTP PWA qua Tailscale; PC B + điện thoại làm theo runbook.

- **Release v2.1.0** (tag 2.1.0, commit 79d903a): GitHub Actions success; asset LalabAutoReport.exe 217,428,437 bytes, sha256 19a3ab2a...2cb57, kèm .sha256. Auto-update versioning đã sửa (AssemblyVersion 2.1.0.0); xem docs/RELEASE_NOTES_v2.1.0.md.

---

## Status checkpoint (2026-10-30)

- **Release:** v2.1.3 is the latest published release. PC A runs the official executable (SHA-256 matches the published checksum).
- **Master:** `52a7a8c`, synced with `origin/master`.
- **Confirmed decision:** album included sheets default to **10** (Product Owner, 2026-10-30). Album billing by sheets is documented in `agents.md` rule 3.2.
- **Merged since v2.1.2:** payment cancelled on reopen (F1); migration refused when pre-migration backup fails (F2); updater verifies published SHA-256 (F3); print-status marker failures are logged and the window-close handler no longer blocks the UI thread.
- **Verified:** full suite 815/815 on a clean copy of master; PC A phone acceptance (login, order view, bill/label print) reported PASS on 2026-10-30.
- **Not verified:** dashboard and thermal printing on the real workshop PC with the new build; PWA over Tailscale from an external network with real photos; restore from backup on a copy; closing the app while the phone is writing (accepted as skipped by the Product Owner).
- **Out of scope for now:** workshop B onboarding; Phase 3 (desktop workstation client) cancelled.
- **Next step:** Product Owner runs the acceptance checks above on PC A; any findings are recorded here before new code work.
