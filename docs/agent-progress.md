# Agent Progress — Lalab Auto Report

Cập nhật: 25/10/2026. Bản ghi bàn giao ngắn; Git + test evidence là nguồn sự thật.

## Current milestone

- **Gallery v2.1: đã commit** (code `9f3bfba`, docs `5960bfa`).
- **LAN Phase 1 (server host, read-only LAN, inventory/renderer spike): FINAL PASS** tại `e229d5c`. Chưa push, chưa cài Windows Service, chưa deploy.

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
