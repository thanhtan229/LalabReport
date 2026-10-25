# Agent Progress — Lalab Auto Report

Cập nhật: 25/10/2026. Tài liệu này chỉ là bản ghi bàn giao ngắn; Git + test evidence là nguồn sự thật.

## Current milestone

- **Gallery v2.1: ĐÃ COMMIT** (`9f3bfba`, branch `master`, chưa push).
- **LAN Server Workstations: Phase 0 (thiết kế) hoàn tất**, chưa triển khai code.

## Completed work

- Order Thumbnail Gallery cho PC (WPF), LAN PWA và Cloud PWA/Worker: manifest theo thư mục in đã resolve, file id opaque ổn định, phân trang 24, thumbnail 320px cache + last-used, retention 30/60/90 + dọn tay (local + cloud).
- WPF: cửa sổ bitmap bounded theo viewport, 2 decode worker, prefetch 1 màn hình (tối đa 12), unload ngoài 2 màn hình, hủy khi đóng/refresh, retry từng ảnh, mở file gốc/thư mục có kiểm tra path traversal + reparse point.
- LAN: API gallery + SPA 3 cột, `AbortController` khi đóng modal, chống response cũ, lazy image, trạng thái lỗi/retry, observer touch đúng order.
- Cloud: job queue claim/status, manifest TTL 24h, upload R2, D1 media theo `(file_id, version)`, clamp limit 1..50, fallback version khi ảnh cũ, R2-404 self-heal + retry UI, cleanup admin-only.
- Retention/cleanup: settings UI 30/60/90/tắt/dọn-ngay/xóa-tất-cả; dọn tay cập nhật `LastThumbnailCleanupAt` và gọi cloud cleanup qua `ICloudSyncService`.
- Thermal label 40x100 (mặc định) / 75x100 + địa chỉ/điện thoại/ghi chú trong cloud bill DTO và SVG label; reopen hóa đơn khóa giữ lý do + xác nhận.
- Tests: .NET 640 pass; worker 94 pass; `tsc` sạch. QA runtime PASS (trừ WPF trực quan).

## Active tasks

- Không có task code đang dở. Working tree sạch sau commit docs.

## Important technical decisions

- Một physical folder = một Order; gallery chỉ đọc file JPG/JPEG trực tiếp trong print folder đã resolve, không fallback sang thư mục khác, không đụng counts/billing/hóa đơn khóa.
- Gallery application service (`IOrderGalleryService`) là transport-neutral để LAN Phase 1 chỉ re-host, không viết lại.
- WPF không dùng virtualization thật (layout nhóm + wrap); thay bằng cửa sổ bitmap bounded — chấp nhận element nhẹ, giải phóng bitmap ngoài viewport.
- Cloud gallery bị freeze mở rộng cho tới sau LAN Phase 1 vì CloudSyncService sẽ chuyển sang service host.

## Known blockers / limitations

- WPF gallery **chưa nghiệm thu bằng mắt** (QA chỉ xác minh cache/log/DB); cần một lần mở thử trên máy thật.
- Chưa in tem trên máy in nhiệt thật (chỉ test render/dimension).
- Cloud production chưa deploy: cần chạy migration 0007 remote **trước**, set `SYNC_SECRET`/`JWT_SECRET` (đã có trên production, không ghi vào repo), deploy Worker, rồi phát hành PC.
- Local D1 dev state đã apply 0007 bản cũ trước khi bổ sung 3 cột `cloud_bills`; cần xóa/backup `.wrangler/state/v3/d1` (dev-only) hoặc dùng DB mới trước khi test local lần tới. Vitest luôn apply migration mới từ đầu.
- PC↔Cloud gallery sync mới kiểm chứng bằng unit/integration tests + mô phỏng local, chưa chạy với production.

## References

- Code commit: `9f3bfba`
- QA: `docs/qa-evidence-gallery/QA_REPORT.md`
- LAN Phase 0: `docs/adr/ADR-002-lan-authoritative-server.md`, `docs/LAN_PHASE0_INVENTORY.md`, `docs/LAN_PHASE0_QUESTIONS.md`
- Plans: `docs/PLAN_ORDER_THUMBNAIL_GALLERY.md`, `docs/PLAN_LAN_SERVER_WORKSTATIONS.md`

## Recommended next step

1. Chốt 7 quyết định trong `docs/LAN_PHASE0_QUESTIONS.md`.
2. LAN Phase 1 slice 1–3 (shared composition root → `LalabAutoReport.Server` console/Windows Service → re-host Kestrel/PWA/gallery read-only), giữ Cloud/AutoScan tắt trong server mode.
3. Trước release Cloud: deploy migration 0007 → Worker → PC; xác minh gallery cloud thật.
