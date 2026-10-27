# Release v2.1.0 — Lalab Auto Report

Ngày phát hành: 27/10/2026. Tag: `v2.1.0`. Commit: `79d903a`.

## Tải về

- `https://github.com/thanhtan229/LalabReport/releases/download/v2.1.0/LalabAutoReport.exe`
- Checksum: `LalabAutoReport.exe.sha256`
  - SHA-256: `19a3ab2a7fa92325d8134e16343344d99b5ed33564bf97eb7a7f5e28e5c2cb57`
  - Kích thước: 217,428,437 bytes
- Self-contained single-file (`win-x64`), không cần cài .NET, không có MSI/installer.

## Nội dung chính

- Order thumbnail gallery cho PC, LAN PWA và Cloud PWA (manifest theo thư mục in đã resolve, phân trang, cache/retention 30/60/90, dọn tay local + cloud).
- Tem nhiệt 40x100 mặc định / 75x100; địa chỉ/điện thoại/ghi chú trong cloud bill và label SVG.
- Phase 2 LAN server: auth/session (`lsv2`), capability, revisions/receipts/idempotency, 3 lệnh ghi đơn (delivered/printed/note) + legacy adapter; payment/print/maintenance vẫn khóa.
- Tailwind được vendor local trong PWA LAN: không phụ thuộc internet/CDN.
- Nâng toàn bộ solution lên .NET 10 LTS (`net10.0`/`net10.0-windows`).
- Release workflow lấy version từ tag, chỉ phát hành một exe + checksum.

## Auto-update

- App kiểm tra GitHub `releases/latest`, tải exe mới, thay thế an toàn và khởi động lại minimized.
- v2.0 → v2.1.0: app v2.0 (AssemblyVersion 2.0.0.0) sẽ thấy v2.1.0 và mời cập nhật.
- Bản v2.1.0 có AssemblyVersion 2.1.0.0, nên không lặp lại việc mời cập nhật cùng phiên bản.
- Yêu cầu: internet + quyền ghi vào thư mục chứa exe (không đặt trong thư mục chỉ-đọc/Program Files nếu chạy user thường).
- Chưa ký số (code signing): Windows SmartScreen có thể cảnh báo.

## Nâng cấp / rollback

- Dữ liệu ở `%LocalAppData%\LalabAutoReport` (DB, backups, thumbnails) được giữ nguyên khi thay exe.
- Nên backup DB trước khi nâng cấp production.
- Rollback: chạy lại asset v2.0 từ release trước; DB additive tương thích, không cần xóa dữ liệu.

## Cloud

- Worker production: `9226ad9a-9ef0-408e-aa4a-7c4c23ae6917`.
- D1 migration `0008_bill_label_snapshots.sql` đã apply; xem `docs/CLOUD_DEPLOY_20261027.md`.

## Ghi chú

- `/api/status` vẫn hiển thị `2.0.0` (lấy từ assembly Infrastructure); version thực của app/FileVersion là `2.1.0`. Chỉnh hợp nhất version các assembly sẽ làm trong release sau.
- Tailscale/browser-only cho máy phụ: xem `docs/TAILSCALE_TWO_WORKSHOPS.md`.
