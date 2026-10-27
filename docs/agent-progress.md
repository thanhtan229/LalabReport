# Agent Progress — Lalab Auto Report

Cập nhật: 27/10/2026. Bản ghi bàn giao ngắn; Git + test evidence là nguồn sự thật.

## Current milestone

- **Release v2.1.0 đã phát hành** (tag `2.1.0`, commit `79d903a`).
- **LAN Phase 2: FINAL PASS** (chi tiết ở mục "Phase 2 final status" bên dưới). **Phase 3 đã hủy** (xem mục 27/10/2026 cuối file).
- **Xác minh master 27/10/2026** (export `master` sang thư mục tạm): build 0 lỗi; test 861 pass, 3 fail (`LiveAutoUpdateAcceptanceTests` kỳ vọng `v2.0` là latest).
- **Nhánh `chore/remove-lan-server` (27/10/2026):** đã gỡ headless `LalabAutoReport.Server` và `LoadHarness` khỏi solution (PWA điện thoại qua app desktop, `LanWriteGate` và pipeline command vẫn giữ nguyên). Đã gỡ guard `server.lock` của `--toggle-printed`. Test live updater đổi sang kiểm tra theo logic và checksum `.sha256` đã publish, không còn hằng số `v2.0`. Thêm `SqliteCommandReceiptRetention` (mặc định 90 ngày, chạy khi khởi động, lỗi chỉ log cảnh báo). Kết quả: build 0 lỗi, 30 cảnh báo (phần lớn có sẵn trên master); test 800/800 pass. Chưa chạy smoke test runtime app desktop sau thay đổi.
- Mất coverage có chủ đích: test `Inventory_SnapshotHashUnchangedAfterV25toV29` (hash bất biến khi migrate) gắn với công cụ inventory của server đã gỡ.

## Completed work

- Gallery PC/LAN/Cloud + retention + thermal labels (xem mục trước trong Git history/QA evidence).
- LAN Phase 1:
  - Shared composition root `AddLalabCore(options)`; desktop standalone giữ nguyên hành vi; Kestrel/SPA chuyển sang Infrastructure.
  - `LalabAutoReport.Server` (console/Windows Service-ready): `--data-dir` (mặc định `%ProgramData%\LalabAutoReport\Server`), Global mutex + `server.lock`, backup fail-closed trước migrate, migration V24 `server_identity`, health live/ready loopback với BuildId.
  - Mobile API re-host loopback-only; gate đọc-only trả 503 `WRITES_DISABLED_UNTIL_PHASE2` cho business writes; auth/login + gallery manifest/touch được phép; Cloud/AutoScan tắt.
  - `inventory --dry-run`: không tạo file/thư mục trong data dir, snapshot WAL-safe qua Backup API, fail-loud exit 8 khi thiếu schema bắt buộc, exit 7 khi thiếu DB; hash snapshot v2 đầy đủ header/line/adjustment.
  - `render-spike`: render bill JPEG + tem trong tiến trình server, hash pixel khớp desktop.
  - Headless `--toggle-printed` từ chối (exit 5) khi `server.lock` đang bị giữ.

## Active tasks

- Không có code đang dở. Working tree sẽ sạch sau commit docs/evidence.

## Important technical decisions

- Một writer host: server sở hữu DB/scan/cloud; client chỉ qua API (Phase 3).
- Phase 1 chưa mở ghi nghiệp vụ: mọi write trả 503 cho tới khi có revision/idempotency ở Phase 2.
- Inventory là công cụ cutover: đọc snapshot, không migrate, không ghi nguồn; kiểm tra hash + exit code.
- Health không lộ đường dẫn/secret; readiness phản ánh DB probe + mobile API.

## Gate / review status

- QA: PASS (evidence `docs/qa-evidence-phase1/QA_REPORT.md`, addendum #3).
- Code Reviewer: LGTM cho range chính; fixes được re-review.
- Final Reviewer: **FINAL PASS** tại `e229d5c`; 2 non-blocking deviations ghi ở `docs/phase1-inventory-contract.md` §8.

## Known limitations / follow-up

- **Session 0 Windows Service** chưa cài/kiểm chứng (Phase 5/6); renderer mới chứng minh trên console host.
- Cloud/AutoScan tắt trong server profile Phase 1 (Q-B4/Q-B7).
- Business writes vẫn 503 tới Phase 2.
- Desktop standalone vẫn dùng DB LocalAppData riêng cho tới Phase 3 client (không đồng bộ với server DB).
- Exit code 1 của inventory có thể đã in JSON trước khi phát hiện sai lệch hash → automation phải kiểm exit code; dự kiến sửa ordering ở Phase 2.
- `bill_lines` chỉ optional khi không có locked legacy bills (nếu có → exit 8).

## Commits

- `9f3bfba` gallery/labels; `5960bfa` docs gallery/LAN Phase 0.
- `08f1b3f` composition root + move transport.
- `e29b3ae` server host skeleton.
- `739ea1e` read-only loopback API.
- `425bf86` renderer spike + headless guard.
- `fdb4d7f`, `6513edf`, `e229d5c` final-review fixes.

## Recommended next step (Phase 2)

1. Auth: PIN hashed, session/device lifecycle, capability matrix (Admin/Staff; no cashier role yet), revoke/rate-limit.
2. Commands: desired-state set commands, revision checks, idempotency keys/receipts, operation lookup.
3. Mutation handlers: atomic bill-job claim, audit/outbox, conflict UX; enable writes only after concurrency/crash tests pass.


---

## Phase 2 progress

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
- **B4 (hardening/load/docs/final review): NOT STARTED.**

Phase 2 release-note behavior changes (acknowledged): PIN no longer viewable (empty = unchanged, explicit clear); PWA re-login once after upgrade; lock rejected if an included order is already in another locked/exported bill (double-bill fix); scan no longer resurrects an old printed marker after explicit un-print; notes limited to 4000 characters.

Remaining known gaps: Session 0 Windows Service install/rendering NOT VERIFIED; desktop standalone still writes its own LocalAppData DB (Phase 3 client); desktop startup credential scrubber (B1-NB3) optional; legacy toggle adapters are intentionally not retry-safe; B4 load/10-client testing pending.


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

## Runtime & Cloud deployment (27/10/2026)

- **.NET 10 LTS**: toàn bộ solution retarget `net10.0`/`net10.0-windows`; WindowsServices 10.0.12; workflow/scripts/docs cập nhật; commit `c274598`. Build Debug/Release 0 errors, full suite 858 passed trên net10; app net10 chạy LAN (PID 107796) với PWA/Tailwind local.
- **Cloud production**: worker `9226ad9a-9ef0-408e-aa4a-7c4c23ae6917` deployed; D1 migration `0008_bill_label_snapshots.sql` applied (3 cột bill snapshot còn thiếu do 0007 đã được apply bản cũ). Xem `docs/CLOUD_DEPLOY_20261027.md`; backup D1 `artifacts/cloud-d1-backup-20261027.sql` (local).
- Verify: health 200; PWA gallery/label 200; gallery request 401; sync jobs 403; D1 đủ bảng gallery và cột bill; secrets giữ nguyên.
- Lưu ý LAN PWA: Tailwind đã vendor local (commit `63d1e4b`), không phụ thuộc internet.

- **Tailscale 2 xưởng**: runbook docs/TAILSCALE_TWO_WORKSHOPS.md; máy A 	iniwin01 100.81.149.85 đã verify HTTP PWA qua Tailscale; PC B + điện thoại làm theo runbook.

- **Release v2.1.0** (tag 2.1.0, commit 79d903a): GitHub Actions success; asset LalabAutoReport.exe 217,428,437 bytes, sha256 19a3ab2a...2cb57, kèm .sha256. Auto-update versioning đã sửa (AssemblyVersion 2.1.0.0); xem docs/RELEASE_NOTES_v2.1.0.md.
