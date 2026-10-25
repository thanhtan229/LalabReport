# LAN Phase 0 — Inventory đường ghi, conflict register và baseline

Ngày lập: 25/10/2026. Phạm vi: **chỉ đọc**. Tài liệu đi kèm `docs/PLAN_LAN_SERVER_WORKSTATIONS.md`, `docs/adr/ADR-002-lan-authoritative-server.md`, `docs/LAN_PHASE0_QUESTIONS.md`.

Mọi `file:line` dưới đây đọc trên working tree hiện tại (HEAD `b9f3bb5` + thay đổi chưa commit). Đường dẫn tương đối từ `src/` trừ khi ghi khác. Khi working tree thay đổi (đặc biệt sau commit gallery), số dòng có thể lệch: Phase 1 phải grep lại tên method, không tin số dòng mù quáng.

---

## 1. Baseline

| Mục | Giá trị | Nguồn |
|---|---|---|
| HEAD | `b9f3bb5` (docs: reconcile durable project plan…) | `git rev-parse --short HEAD` |
| Working tree | 37 file `M`, 16 mục `??`; `git diff --stat`: 37 files, +4172 / −282 | `git status --short`, 25/10/2026 |
| Thay đổi chưa commit chính | Gallery PC/LAN/Cloud (`IOrderGalleryService`, `OrderGalleryService`, `SqliteThumbnailCacheRepository`, migration V23, `OrderGalleryWindow`, worker `gallery.ts`, D1 `0007_order_gallery.sql`), tem nhiệt (`label-svg.ts`, `WpfShippingLabelExporter`), billing/settings/SPA | danh sách `??`/`M` |
| Ghi chú | Snapshot đầu phiên có `?? Bills/`; lần chạy sau không còn. Không do Phase 0 tạo/xóa | `git status` |
| Tests | **629 passed**: theo kết quả orchestrator cung cấp, **chưa chạy lại** trong Phase 0 | yêu cầu nhiệm vụ |
| Build | Chưa chạy trong Phase 0 (documentation-only) | — |
| SDK có sẵn | 8.0.424, 10.0.401 | `dotnet --list-sdks` |
| Target | Core `net8.0`; Infrastructure/UI/Tests `net8.0-windows`, `UseWPF=true` | `LalabAutoReport.Infrastructure.csproj:19-20`, `LalabAutoReport.UI.csproj:29,32` |
| ASP.NET Core | `FrameworkReference Microsoft.AspNetCore.App` nằm trong **UI** project | `LalabAutoReport.UI.csproj:14` |
| Version | 2.0.0 | `LalabAutoReport.UI.csproj:35-36` |
| Schema | 23 migrations, mới nhất V23 `ThumbnailCacheMetadata` (chưa commit) | `Infrastructure/Data/DatabaseMigrator.cs:756-772` |

**Gate Phase 0 về baseline chưa đạt đầy đủ:** plan §12 yêu cầu chạy targeted + full suite + build và lưu kết quả. Phase 0 này chỉ dùng con số 629 được cung cấp. Trước khi bắt đầu Phase 1 (sau khi gallery được commit), chạy lại `dotnet test --nologo` và `dotnet build src/LalabAutoReport.UI/LalabAutoReport.UI.csproj -c Debug` trên commit đó và ghi kết quả vào đây.

---

## 2. Runtime topology hiện tại

Một process WPF `LalabAutoReport.UI.exe` sở hữu mọi thứ:

| Thành phần | Bằng chứng |
|---|---|
| Composition root duy nhất (repositories, scan, billing, cloud, Kestrel, ViewModels) | `UI/App.xaml.cs:528-612` |
| Migration lúc khởi động UI | `UI/App.xaml.cs:216-238` |
| Single-instance: `Mutex "Local\LalabAutoReport_SingleInstance_Mutex_v1"` (**theo session**, không `Global\`) | `UI/App.xaml.cs:30,67` |
| IPC: named pipe `LalabAutoReport_IpcPipe_v1`, **không ACL/không xác thực** | `UI/App.xaml.cs:31,442-447` |
| Khi pipe không trả lời trong 1200 ms: kill các process `LalabAutoReport.UI` cùng exe path rồi chiếm mutex | `UI/App.xaml.cs:84-85,116-139` |
| AutoScan (poll 15 s + idle) và startup scan | `UI/App.xaml.cs:286-301`; `Core/Services/AutoScanCoordinator.cs:86-137,92` |
| Kestrel LAN/PWA khởi động nền | `UI/App.xaml.cs:308-322` |
| Cloud sync khởi động sau 3 s + timer 15 s | `UI/App.xaml.cs:384-399`; `Infrastructure/Services/CloudSyncService.cs:84-107` |
| Dọn thumbnail cache lúc khởi động (ghi settings) | `UI/App.xaml.cs:329-377` |
| Teardown: chờ tối đa 1.5 s; Cloud timer **không** được stop tường minh | `UI/App.xaml.cs:615-672` |
| DB: `%LocalAppData%\LalabAutoReport\lalab_autoreport.db`; mỗi lần mở chạy `PRAGMA foreign_keys=ON; journal_mode=WAL`; **không có `busy_timeout`** | `Infrastructure/Data/SqliteConnectionFactory.cs:20-26,45` |
| Log: `%LocalAppData%\LalabAutoReport\logs\lalab-.log`, rolling 30 ngày | `Infrastructure/Logging/LoggingSetup.cs:16-38` |
| Thumbnail cache: `%LocalAppData%\LalabAutoReport\Thumbnails` | `Infrastructure/Services/ThumbnailService.cs:60-63` |
| Icon marker Explorer trỏ `%LocalAppData%\…\Assets\*.ico` (đường dẫn tuyệt đối theo user) | `Infrastructure/Windows/FolderVisualMarkerService.cs:25-27` |
| Dev scripts nhận diện runtime bằng **tên process** `LalabAutoReport.UI`, rồi `Stop-Process -Force` | `DEV_START.bat` (khối kiểm tra/stop đầu file), `DEV_STATUS.bat` |

Hệ quả cho LAN: mọi đường dẫn trạng thái (DB, log, cache, icon) đều theo **user profile**. Windows Service chạy dưới tài khoản khác sẽ thấy một bộ dữ liệu khác hoàn toàn nếu chỉ dùng mặc định.

---

## 3. Inventory đường ghi (writers)

Ký hiệu cột **Lớp**: `Repo` = ViewModel/View/endpoint gọi thẳng repository; `Svc` = qua application/domain service. Cột **Kiểu**: `Toggle` = đọc rồi đảo; `Set` = ghi giá trị mong muốn (last-write-wins); `Cmd` = thao tác nghiệp vụ.

**Không đường ghi nào** dưới đây có revision, ETag hay idempotency key do client cung cấp. Bảng `orders`, `customer_bills` không có cột revision/row_version; chỉ có `updated_at`. Revision duy nhất là `cloud_field_revisions` cho Cloud (`DatabaseMigrator.cs:724-739`). Không có bảng audit.

### 3.1 Đơn hàng: trạng thái vận hành

| file:line | Điểm vào | Ghi | Lớp | Kiểu | Cloud outbox |
|---|---|---|---|---|---|
| `UI/ViewModels/DashboardViewModel.cs:1261` | `ToggleDeliveredAsync`@1238 | `orders.is_delivered/...` | Repo | Toggle | Có (repo enqueue) |
| `UI/ViewModels/DashboardViewModel.cs:1393` | `TogglePrintedAsync`@1387 (cả đơn) | `orders` printed flag | Repo | Toggle | **Không** |
| `UI/ViewModels/DashboardViewModel.cs:1301` | `ToggleItemPrintedAsync`@1275 | qua `PrintStatusService` (DB + marker + icon) | Svc | Toggle | Không |
| `UI/ViewModels/DashboardViewModel.cs:1416` | `EditOrderNoteAsync`@1402 | `orders.note` | Repo | Set | Có |
| `UI/Views/ShippingLabelPreviewWindow.xaml.cs:138,147` | `Print_Click`@114 (auto-deliver) | delivered=true từng đơn | Repo (code-behind) | Set | Có |
| `UI/Web/KestrelMobileWebServer.cs:547-551` | `POST /api/orders/{id}/toggle-delivered` (Staff được) | delivered | Repo | **Toggle** đọc-đảo không atomic | Có |
| `UI/Web/KestrelMobileWebServer.cs:573-576` | `POST /api/orders/{id}/toggle-printed` (Staff được) | printed flag | Repo | Toggle | **Không** |
| `UI/Web/KestrelMobileWebServer.cs:613` | `POST /api/orders/{id}/note` | note | Repo | Set | Có |
| `UI/Web/KestrelMobileWebServer.cs:664` | `POST /api/orders/{id}/print-label` + `AutoMarkDeliveredOnPrint` | delivered=true | Repo | Set | Có |
| `UI/Web/KestrelMobileWebServer.cs:947,953` | `POST /api/bills/{id}/print` + auto-deliver, từng đơn, không chung transaction | delivered=true | Repo | Set | Có |
| `Infrastructure/Data/SqliteOrderRepository.cs:680` | `UpdateOrderDeliveredStatusAsync` | transaction; enqueue `order_delivered` (`:700`) | — | — | Có |
| `Infrastructure/Data/SqliteOrderRepository.cs:603-619` | `UpdateOrderPrintedStatusAsync` | UPDATE không transaction, không outbox | — | — | Không |
| `Infrastructure/Data/SqliteOrderRepository.cs:704,720` | `UpdateOrderNoteAsync` | transaction; enqueue `order_note` | — | — | Có |

Lưu ý: `SqliteCloudSyncStateRepository.EnqueueAsync` (`Infrastructure/Data/SqliteCloudSyncStateRepository.cs:109-116`) sinh `operation_id = Guid.NewGuid()` **ở mỗi lần ghi DB**. Client retry một toggle sẽ tạo operation mới và đảo trạng thái lần nữa.

### 3.2 Print status markers (Explorer)

| file:line | Điểm vào | Ghi |
|---|---|---|
| `UI/App.xaml.cs:150-183` | `--toggle-printed` khi app **chưa** chạy: process headless tự tạo `SqliteConnectionFactory`, **chạy migration** (không backup service), tạo repositories, gọi `ToggleStatusAsync` | DB + marker, **process ghi thứ hai** |
| `UI/App.xaml.cs:479-489` | IPC `TOGGLE_PRINTED` → `IPrintStatusService.ToggleStatusAsync` | DB + marker |
| `Core/Services/PrintStatusService.cs:133-141` | `ToggleStatusAsync`: chỉ Printed→NotPrinted; **Partial→Printed** | — |
| `Core/Services/PrintStatusService.cs:208,213-214,225,235,293-298` | Thứ tự: DB `SaveAsync` → marker `.lalab-printed` → `desktop.ini`/icon → item flag → tính lại parent; không chung transaction | DB, file |
| `Core/Services/PrintStatusService.cs:600-606` | Cascade cấp đơn: marker/icon **không try/catch**, lỗi IO dừng giữa chừng | DB, file |
| `Core/Services/PrintStatusService.cs:43-125` | `GetStatusAsync` (đọc) **ghi** khi khôi phục marker→DB (`:112`) | DB |
| `Core/Services/PrintStatusService.cs:326-360` | `ReconcileFolderStatusAsync`: DB NotPrinted + marker còn sót → **hồi sinh Printed** | DB |
| `Core/Services/PrintStatusService.cs:822-852` | Marker JSON ghi bằng `File.WriteAllText` (không atomic) | file |
| `Infrastructure/Windows/FolderVisualMarkerService.cs:185,208-213,237-258` | `SHGetSetFolderCustomSettings`, đặt ReadOnly; `RemoveVisualMarker` xóa **mọi** `desktop.ini` không kiểm tra chủ sở hữu | file |
| `Infrastructure/Windows/WindowsContextMenuIntegrationService.cs:123-180` | Ghi HKCU verb `--quick-bill` / `--toggle-printed`; gọi mỗi lần khởi động nếu bật (`UI/App.xaml.cs:245-248`) | registry |

Marker vi phạm trực tiếp plan §8.1 "Recovery từ marker không được hồi sinh trạng thái cũ sau khi DB đã gỡ đã in".

### 3.3 Scan

| file:line | Điểm vào | Ghi |
|---|---|---|
| `UI/ViewModels/DashboardViewModel.cs:489,562,616,936` | Scan ngày / ngày thiếu / đơn / quy cách | qua `IScanService` |
| `UI/ViewModels/ReportsViewModel.cs:281` | Scan ngày thiếu | qua `IScanService` |
| `Core/Services/AutoScanCoordinator.cs:60-137,161-228,260-315` | Poll 15 s, idle ≥ `IdleThresholdMinutes`, quét `IdleScanWindowDays` ngày; startup scan theo `AutoScanStartup` | `ScanOrderAsync` |
| `Core/Domain/Models.cs:438,441` | Mặc định `AutoScanStartup=true`, `EnableIdleScan=true` | — |
| `Core/Services/AutoScanCoordinator.cs:81` | `SetPaused` **không được gọi ở đâu** | — |
| `Core/Services/ScanService.cs:241` | Xóa order mồ côi (không có locked bill) khi scan ngày | `DeleteOrderAsync` |
| `Core/Services/ScanService.cs:769` | `SaveOrderAsync` (snapshot mới) | orders/items/snapshots |
| `Core/Services/ScanService.cs:794,952,958,964` | Đồng bộ **draft** customer bill theo scan | customer_bills, items, orders |
| `Core/Services/ScanService.cs:804` → `Core/Services/BillingService.cs:176` | Legacy per-order bill | bills |
| `UI/ViewModels/DashboardViewModel.cs:962` | `ResolveQuantityAsync`@957 (V1) | `UpdateOrderItemResolutionAsync`, Repo |
| `UI/ViewModels/DashboardViewModel.cs:987` | `SelectCandidatePrintFolderAsync`@969 | `UpdateOrderItemPrintFolderAsync`, Repo |
| `UI/ViewModels/DashboardViewModel.cs:725` | `DeleteOrderAsync`@701 | Repo |
| `UI/ViewModels/DashboardViewModel.cs:250,259,357` | `UpdateOrderStatusAsync` **fire-and-forget** (`:357` ghi trong luồng load) | Repo |

### 3.4 Billing, lock, reopen, split, payment

| file:line | Điểm vào | Ghi | Lớp |
|---|---|---|---|
| `UI/ViewModels/DashboardViewModel.cs:838`; `CustomersViewModel.cs:434` | Build/refresh draft khách hàng | `CustomerBillingService.cs:420` SaveBill | Svc |
| `UI/ViewModels/DashboardViewModel.cs:898`; `UI/Services/QuickBillLauncher.cs:119` | Guest draft (`persistDraft:false`) | **vẫn `SaveOrderAsync` vô điều kiện** `CustomerBillingService.cs:1266`; bill chỉ lưu khi `:1521` | Svc |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:838,856,860,885` | Xuất bill: Lock → JPEG → `RecordBillExportedAsync` → `SyncBillAsync` | Svc |
| `Core/Services/CustomerBillingService.cs:463-535` | `LockBillAsync`: verify scoped (`:500-518`) rồi `LockCustomerBillAtomicAsync` (`:531`) | Svc |
| `Infrastructure/Data/SqliteCustomerBillRepository.cs:286-322` | Lock transaction: `UPDATE order_item_scans SET customer_bill_id=@Id WHERE id IN @Ids` (`:298-303`) — **không có điều kiện `customer_bill_id IS NULL`/bill khác**; `orders.status='Billed'` | — |
| `Infrastructure/Data/SqliteCustomerBillRepository.cs:364-397` | `SaveBillInternalAsync`: kiểm tra trạng thái đã lưu trong transaction; giữ `is_paid` đã lưu (`:371-373`); chặn sửa snapshot đã chốt (`:374-394`) | — |
| `Core/Services/CustomerBillingService.cs:539-570` | `ReopenBillAsync`: lý do **chỉ log** (`:568`), không lưu | Svc |
| `UI/ViewModels/DashboardViewModel.cs:653-654,779-780` | Rescan/xem bill phát hiện thay đổi → hộp Yes/No → **Reopen + Sync** | Svc |
| `UI/ViewModels/DashboardViewModel.cs:658,785` | `SetFilesystemChangedAfterLockAsync(false)` | Repo |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:1057` | Reopen | Svc |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:1105` → `SqliteCustomerBillRepository.cs:249-283` | Split: kiểm tra `expectedUpdatedAt` trong transaction (`:263`) — **cơ chế optimistic duy nhất hiện có** | Svc |
| `Core/Services/CustomerBillingService.cs:587-640` | Detach order khỏi bill | Svc |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:645,690` | Convert guest: `SaveBillAsync` trực tiếp rồi `ConvertGuestBillToCustomerAsync` | Repo + Svc |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:1269,1330` | Lưu snapshot địa chỉ trước in/xuất tem | Repo |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:1369` ← `Views/CustomerBillReviewWindow.xaml.cs:20` | Lưu cả draft khi **đóng cửa sổ** (async void) | Repo |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:369,381` | `RecordBillExportedAsync` fire-and-forget | Svc |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:1218`; `InvoicesViewModel.cs:378` | Toggle thanh toán | Repo |
| `UI/Web/KestrelMobileWebServer.cs:796-798` | `POST /api/bills/{id}/toggle-payment` (Admin) | Repo, Toggle |
| `Infrastructure/Data/SqliteCustomerBillRepository.cs:692-711` | `SetPaymentStatusAsync`: transaction, enqueue `bill_payment` | — |
| `Core/Services/LockingService.cs:128,156` | V1 per-order lock/reopen: đăng ký DI (`UI/App.xaml.cs:561`), inject vào Dashboard nhưng **không có caller** | — |

### 3.5 Danh mục: khách hàng, alias, bảng giá, root

| file:line | Ghi | Lớp |
|---|---|---|
| `UI/ViewModels/AssignCustomerViewModel.cs:181,226,235,240,269,273` | Tạo khách, chuyển/thêm/xóa alias, `UpdateFolderCustomerIdAsync` hàng loạt | Repo |
| `UI/ViewModels/CustomersViewModel.cs:164,171,207,258,282,321,342` | Sửa/xóa/tạo khách, alias | Repo |
| `UI/ViewModels/PriceListViewModel.cs:233,258,279,316,335,362,394,399,429` | Quy cách, giá, alias, rename, delete | Repo |
| `UI/ViewModels/ManageFamilyAliasesViewModel.cs:157,200,218,252,292,327` | Product family + alias CRUD | Repo |
| `UI/ViewModels/SettingsViewModel.cs:567,602,637,657,676,722` | `root_folders` thêm/sửa path/đổi tên/bật/default/xóa | Repo |

### 3.6 Settings

`Infrastructure/Data/SqliteSettingsRepository.cs` chỉ có `GetSettingsAsync`@23 và `SaveSettingsAsync`@278. Save **upsert toàn bộ khóa** trong một transaction (`:281-458`). Ngoại lệ duy nhất là `SqliteCloudSyncStateRepository.SetLastSyncAtAsync`@133.

Các caller đọc-sửa-ghi cả object, không kiểm tra concurrency, nên có thể lost update:

| file:line | Ngữ cảnh |
|---|---|
| `UI/App.xaml.cs:228` | Isolated mode tắt auto features |
| `UI/App.xaml.cs:368` | Background cleanup ghi `LastThumbnailCleanupAt` |
| `UI/ViewModels/SettingsViewModel.cs:920,968` | Lưu settings; Sync now |
| `UI/Web/KestrelMobileWebServer.cs:466-467` | API dọn thumbnail (Admin) |
| `Core/Services/MobileAuthService.cs:41` | Tạo `MobileAuthSecret` (sync-over-async) |
| `Infrastructure/Services/CloudSyncService.cs:194` | Cleanup 24 h ngoài `_syncGate`: ghi đè snapshot cũ của **mọi khóa** gồm `CloudSyncSecret`, `EnableCloudSync`, `LastCloudSyncAt` |

Secrets lưu **plaintext** trong `app_settings`:

| Khóa | Bằng chứng |
|---|---|
| `AdminPin`, `StaffPin` | `SqliteSettingsRepository.cs:186-193,362,365`; hiển thị bằng TextBox thường `UI/Views/SettingsView.xaml:596,602` |
| `MobileAuthSecret` | `SqliteSettingsRepository.cs:196-198,368` |
| `CloudSyncSecret` | `SqliteSettingsRepository.cs:245-247,395` |

### 3.7 Cloud (inbound/outbound)

| file:line | Hành vi |
|---|---|
| `Infrastructure/Services/CloudSyncService.cs:84-107` | Timer 10 s/15 s: heartbeat → pull → print queue → `SyncAllCore` nếu outbox > 0; gallery jobs + cleanup ngoài gate |
| `Infrastructure/Services/CloudSyncService.cs:626-665` | Pull V2, `BindStreamAsync` mỗi trang, fail-closed khi `RequiresReconciliation` |
| `Infrastructure/Data/SqliteCloudSyncStateRepository.cs:31-96` | `ApplyEventAsync` một transaction: `customer_bills.is_paid`, `orders.is_delivered/note`, `cloud_applied_events`, `cloud_field_revisions`, cursor; **bỏ qua cập nhật entity nếu còn outbox pending** (`:79-92`) |
| `Infrastructure/Services/CloudSyncService.cs:672-697` | Drain outbox `POST /api/sync/v2/operations` |
| `Infrastructure/Services/CloudSyncService.cs:896-1049` | Remote print: ledger `cloud_print_command_ledger`, auto-deliver (`:998-1016`) |
| `Infrastructure/Services/CloudSyncService.cs:747-894` | Gallery jobs (chưa commit): claim, manifest, upload thumbnail |
| `Infrastructure/Services/CloudSyncService.cs:721,727` | `deviceId = Environment.MachineName`, `appVersion = "1.0.0"` hard-code; **không có device ID bền vững** |
| `Infrastructure/Services/CloudMediaSyncService.cs:26,102,138` | Dedupe upload chỉ in-memory |

Cloud inbound **đi vòng qua repository** (`ApplyEventAsync`) chứ không qua command handler: đây là producer cần adapter riêng ở Phase 2 (plan §7 mục 8).

### 3.8 Backup / restore / reset / purge / update

| file:line | Hành vi | Ghi chú LAN |
|---|---|---|
| `Infrastructure/Data/DatabaseBackupService.cs:70-75` | Backup bằng **SQLite Online Backup API**; giữ 20 bản (`:15,196-226`) | Dùng lại được cho cutover |
| `Infrastructure/Data/DatabaseBackupService.cs:119-163` | Restore: guard + `EnsureLocalOnlyAsync` cả DB hiện tại và file backup, backup trước, `ClearAllPools`, `BackupDatabase` đè live DB; không `integrity_check`, không kiểm schema version | Cần thêm generation bump |
| `Infrastructure/Data/DatabaseLifecycleGuard.cs:11-18` | `SemaphoreSlim` **trong process** theo đường dẫn DB | Không che process headless/Service |
| `Infrastructure/Data/DatabaseLifecycleGuard.cs:20-35` | `EnsureLocalOnlyAsync`: chặn nếu DB đã liên kết Cloud | Cutover không được bypass |
| `Infrastructure/Data/DatabaseResetService.cs:38-202` | Reset vận hành / factory reset, có backup bắt buộc | Admin-only ở server |
| `Infrastructure/Data/DataPurgeService.cs:64-188` | Purge theo ngày; `PRAGMA foreign_keys=OFF` bên trong transaction (vô hiệu); orders/bills cắt theo tiêu chí khác nhau | Rủi ro có sẵn, ngoài phạm vi LAN |
| `Infrastructure/Data/DatabaseMigrator.cs:55-66` | Backup trước migrate; lỗi backup **chỉ warning** | Server phải fail-closed |
| `Infrastructure/Data/DatabaseMigrator.cs:74-122` | Mỗi migration một transaction + `foreign_key_check` | Giữ nguyên |
| `Infrastructure/Services/GitHubUpdateService.cs:205-210` | Updater ghi script PowerShell rồi `Environment.Exit(0)`; không qua lifecycle guard | Không được thay binary service |
| `Infrastructure/Services/UpdateReplacement.cs:18-115` | Kiểm MZ + SHA256 tự tính; không chữ ký | — |

### 3.9 Rendering / in / clipboard

| file:line | Phụ thuộc UI |
|---|---|
| `Infrastructure/Reporting/WpfJpegBillExporter.cs:48-70` | Tự tạo STA thread khi không ở STA; `DrawingVisual` + `RenderTargetBitmap`. Ứng viên tốt cho Session 0, cần spike |
| `Infrastructure/Reporting/WpfShippingLabelExporter.cs:210` | `new PrintDialog()` trong Infrastructure: **phải tách** khỏi service |
| `Infrastructure/Reporting/WpfShippingLabelExporter.cs:357,376` | STA render tem |
| `UI/Web/KestrelMobileWebServer.cs:643,647,921,925` | Endpoint PWA gọi **in vật lý trên máy chạy server** |
| `Infrastructure/Services/ThumbnailService.cs:261,313-331` | WIC qua `System.Windows.Media.Imaging`, không Dispatcher |
| `UI/ViewModels/CustomerBillReviewViewModel.cs:773,1197`; `SettingsViewModel.cs:1017,1029` | Clipboard trong ViewModel, giữ ở client |
| Service locator `App.Current.Services` | `CustomerBillReviewViewModel.cs:220,642,687,1108,1215,1254,1266,1272-1273,1313,1327,1366`; `DashboardViewModel.cs:191,1281,1330`; `InvoicesViewModel.cs:510-511,585`; `MainViewModel.cs:308-312` |

### 3.10 LAN HTTP/PWA

| file:line | Hiện trạng |
|---|---|
| `UI/Web/KestrelMobileWebServer.cs:92` | `Listen(IPAddress.Any, port)`, **HTTP**, mặc định 5050 (`Core/Domain/Models.cs:464`), `EnableMobileServer=true` (`:463`) |
| `UI/Web/KestrelMobileWebServer.cs:97-103` | CORS `AllowAnyOrigin/Header/Method` |
| `UI/Web/KestrelMobileWebServer.cs:156-166` | `/api/status` không auth, lộ `CloudSyncApiUrl` |
| `UI/Web/KestrelMobileWebServer.cs:171-196` | Login PIN, không rate limit |
| `UI/Web/KestrelMobileWebServer.cs:1095-1126` | `CheckAuth`: Bearer hoặc `?token=`/`?auth=` trong query |
| `UI/Web/KestrelMobileWebServer.cs:350-444` | Gallery endpoints dùng `IOrderGalleryService` (đúng hướng re-host) |
| `UI/Web/KestrelMobileWebServer.cs` (toàn file) | Không dùng `Application.Current`/Dispatcher. Nằm trong UI project chỉ vì `FrameworkReference` (`LalabAutoReport.UI.csproj:14`) |
| `UI/Web/MobileSpaHtmlProvider.cs:699,725,1120,1465` | PWA gọi toggle không body/không opId |
| `UI/Web/MobileSpaHtmlProvider.cs:236,247,324` | Token trong `localStorage` và query `?auth=` |

### 3.11 Auth hiện tại

| file:line | Hiện trạng |
|---|---|
| `Core/Domain/Enums.cs:251` | `MobileUserRole { Staff=0, Admin=1 }` |
| `Core/Services/MobileAuthService.cs:12` | Token 30 ngày, không gia hạn trượt |
| `Core/Services/MobileAuthService.cs:60-72` | Token `base64url(role:ticks:salt).HMACSHA256` |
| `Core/Services/MobileAuthService.cs:131` | So chữ ký bằng `FixedTimeEquals` |
| `Core/Services/MobileAuthService.cs:152,158,163,168` | So PIN plaintext `==`; AdminPin rỗng → không ai login; PIN trùng → từ chối |
| `Core/Services/MobileAuthService.cs:14-53` | Secret HMAC trong settings; lỗi repo → key tạm trong RAM |
| — | **Không thu hồi**: đổi PIN không vô hiệu token; chỉ xoay `MobileAuthSecret` mới thu hồi |

---

## 4. Conflict register V1 (AGENTS.md) ↔ V2 (PLAN_lalab_V2 / ARCHITECTURE) ↔ code

Thẩm quyền: `docs/archive/PLAN_lalab.md:11-18` có banner "V2 UPGRADE SPECIFICATION IN EFFECT", nên V2 thắng qua thứ tự ưu tiên của AGENTS.md:41-49. AGENTS.md không tự nhắc V2.

**Quy tắc cho dự án LAN:** không đổi công thức/khóa billing (plan §3). Mọi dòng dưới đây được **đóng băng nguyên trạng** khi re-host; chỉ thay đổi khi người dùng quyết định riêng.

| # | Quy tắc | V1 | V2 | Code hiện tại | Đang giải quyết thế nào | Tác động LAN |
|---|---|---|---|---|---|---|
| C1 | Source vs Print mismatch, USE_PRINT/USE_SOURCE/CUSTOM | AGENTS.md:106-140,407-417 | PLAN_V2:29-35,588-600,981-993; ARCHITECTURE.md:151-154 | `ScanService.cs:1112-1146` luôn `BillQuantity=PrintCount`, `UsePrint`; enum còn `Core/Domain/Enums.cs:88-94`; guest path còn so mismatch `CustomerBillingService.cs:1237-1246`; fallback `PrintCount ?? SourceCount` `CustomerBillingService.cs:256,1323,1604,1612`, `ScanService.cs:741,828`; `DashboardViewModel.cs:957-966` còn sống; test V1 `tests/LalabAutoReport.Tests/BillingEngineTests.cs:154` | **V2 thắng**; mismatch không chặn; override qua `BilledQuantity` + lý do | API v1 **không** công bố lệnh USE_SOURCE/CUSTOM; dùng override dòng bill. Fallback/guest mismatch giữ nguyên, ghi vào câu hỏi không chặn |
| C2 | Physical folder = Order | AGENTS.md:59-74 | PLAN_V2:89-146,411-492; ARCHITECTURE.md:110-147 | `OrderKind` `Enums.cs:123-127`; parser `FolderStructureParser.cs:477-549`; heuristic `:495-502` coi thư mục lạ có con là explicit order (trái PLAN_V2 Case D); nhánh KHACH_LE `:580-599` không return | **V2** (implicit/explicit), alias vẫn tách order | Identity API = `OrderId` (+ `RootId`,`RelativePath`,`OrderKind` để hiển thị). Không sửa parser trong LAN |
| C3 | Missing print folder là state | AGENTS.md:96-104,385 | PLAN_V2:37-50,542-586 | `PrintFolderResolver.cs:66-73,98-131,145-177` | **V2**: product folder tự là billing folder | Chọn candidate thành server command theo `OrderItemId` + relative path đã có trong DB |
| C4 | Lock/reopen | AGENTS.md:158-164,421-429 | PLAN_V2:52-54,894-936; ARCHITECTURE.md:210 ghi `ReopenOrder` (cũ) | Live: `CustomerBillingService.cs:463-570`; V1 `LockingService.cs:128,156` không caller; reopen lý do chỉ log `:568`; UI reopen bằng Yes/No `DashboardViewModel.cs:653,779` | Customer-bill lock thay per-order lock; immutability đạt; reopen yếu | Reopen = lệnh Admin riêng, lý do bắt buộc, lưu audit (plan §8.2.8). Không đăng ký `ILockingService` ở server |
| C5 | Cấm watcher/polling | AGENTS.md:166-177,444-448 | PLAN_V2:1045-1052 | `AutoScanCoordinator.cs:86-137` (15 s + idle); `CloudSyncService.cs:84-107` (15 s); không có FileSystemWatcher | Trái chữ V1/V2; chỉ có căn cứ `docs/archive/PLAN_PRINTED_EXPLORER.md:17,517`, `FINAL_REVIEW.md:163` | Idle không có nghĩa trong Session 0 → xem Q-B7. Cloud timer chỉ chạy trên server |
| C6 | Cấm decode ảnh | AGENTS.md:248-259 ("trừ khi feature yêu cầu") | PLAN_V2:1232-1236 | `ThumbnailService.cs:259-331`; scan chỉ metadata `ScanService.cs:1096-1105` | Ngoại lệ "future feature": `docs/PLAN_ORDER_THUMBNAIL_GALLERY.md:52,55,133,253` | Decode chỉ trên server; client chỉ nhận JPEG qua API |
| C7 | Không Cloud/remote trừ khi yêu cầu | AGENTS.md:507-515 | ARCHITECTURE.md:325-368; ADR-001 | `EnableCloudSync=true`, `EnableMobileServer=true` mặc định (`Models.cs:463,480`) | Đã được chấp nhận qua ADR-001 | Cloud tùy chọn, chỉ server; client không giữ secret |
| C8 | Album, override, adjustments, guest | — | PLAN_V2:148-200,684-745; ARCHITECTURE.md:259-323 | `BillingService.cs:105-122`; `CustomerBillingService.cs:305-332,487-494,964,996` | Chỉ V2 | Server tự tính lại tổng; không tin giá/tổng client |
| C9 | Test V1 vs V2 | AGENTS.md:385,412-416 | PLAN_V2:1282-1300 | `BillingEngineTests.cs:154-204` (V1); `EffectiveBillingFolderTests.cs:544`, `FinalPrintFolderAndBillingTests.cs:199-214` (V2) | Cùng tồn tại, cùng pass | Giữ cả hai trong regression baseline LAN; không xóa |
| C10 | Physical order provenance khi gán khách | AGENTS.md:59-74 | ARCHITECTURE.md:261 | `AssignCustomerViewModel.cs:240` `UpdateFolderCustomerIdAsync` cập nhật mọi order cùng folder | Chủ ý theo alias folder | Server command phải trả danh sách order bị ảnh hưởng và audit; không mở rộng phạm vi |

---

## 5. Risk register cho LAN

Xếp theo mức độ, cao xuống thấp.

| ID | Rủi ro | Bằng chứng | Mức | Phase xử lý |
|---|---|---|---|---|
| R1 | **Hai writer host**: mutex `Local\` theo session nên service Session 0 và desktop user không thấy nhau; process headless `--toggle-printed` tự mở DB + migrate | `UI/App.xaml.cs:30,67,150-183` | Nghiêm trọng | 1 |
| R2 | **Double-claim job khi lock**: lock transaction gán `customer_bill_id` không điều kiện; hai draft khác nhau chứa cùng job có thể cùng chốt | `SqliteCustomerBillRepository.cs:298-303` | Nghiêm trọng (tiền) | 2 |
| R3 | **Toggle + operationId sinh ở DB**: retry mạng đảo trạng thái delivered/printed/paid; outbox nhận operation mới mỗi lần | `KestrelMobileWebServer.cs:547,573,796`; `SqliteCloudSyncStateRepository.cs:113-115` | Cao | 2 |
| R4 | **Marker hồi sinh Printed** sau khi DB gỡ; cascade dừng giữa chừng; `desktop.ini` của người khác bị xóa | `PrintStatusService.cs:326-360,600-606`; `FolderVisualMarkerService.cs:237-250` | Cao | 2 |
| R5 | **Lost update settings**: save toàn object từ ≥6 nơi, kể cả timer Cloud ngoài gate | `SqliteSettingsRepository.cs:278-458`; `CloudSyncService.cs:194` | Cao | 1 (server sở hữu), 2 (field-level) |
| R6 | **Không thu hồi phiên, PIN plaintext, không rate limit, token trong query** | §3.11; `KestrelMobileWebServer.cs:1110-1116` | Cao | 2 |
| R7 | **Đường dẫn theo user profile** (DB/log/cache/icon) khiến service dùng nhầm bộ dữ liệu | §2 | Cao | 1 |
| R8 | **Interactive printing trong Infrastructure/endpoint**: `PrintDialog` và in vật lý trên máy server | `WpfShippingLabelExporter.cs:210`; `KestrelMobileWebServer.cs:643,921` | Trung bình | 1 (spike), 4 |
| R9 | **Device identity = MachineName**; đổi máy chủ đổi identity heartbeat | `CloudSyncService.cs:721` | Trung bình | 1 (cutover), 5 |
| R10 | **Migration fail-open khi backup lỗi** | `DatabaseMigrator.cs:55-66` | Trung bình | 1 (server profile) |
| R11 | **Ghi trực tiếp repo từ ViewModel và fire-and-forget**: phải gom vào application handlers | §3.4–3.5 | Trung bình | 2–3 |
| R12 | **Quick Bill ghi order dù `persistDraft:false`** | `CustomerBillingService.cs:1266` | Trung bình | 4 (giữ hành vi, đưa qua command) |
| R13 | **.NET 8 hết hỗ trợ 10/11/2026** (16 ngày nữa) | target `net8.0*` | Trung bình (vận hành) | Batch riêng, xem Q-B6 |
| R14 | **Không `busy_timeout`**; transaction scan/verify ngắn nhưng lock verify scan filesystem nằm ngoài transaction (đúng) | `SqliteConnectionFactory.cs:45`; `CustomerBillingService.cs:500-531` | Thấp–TB | 1 |
| R15 | **Dev scripts kill theo tên process** | `DEV_START.bat`, `DEV_STATUS.bat` | Thấp (dev) | 1/5 |

---

## 6. Gallery: đã có, chỉ re-host

`IOrderGalleryService` (`Core/Interfaces/IOrderGalleryService.cs:8-14`) gồm 4 method: manifest, page, thumbnail, touch. `OrderGalleryService` (`Infrastructure/Services/OrderGalleryService.cs:18`) không dùng WPF, không ghi đĩa ngoài cache metadata, có guard root/reparse point (`:331-350`). Kestrel đã gọi service này (`KestrelMobileWebServer.cs:358,392,412,444`). Cloud gallery job dùng chung (`CloudSyncService.cs:747-894`).

Ở server mode: đăng ký nguyên các dịch vụ này trong composition root server. WPF client gọi cùng endpoint gallery qua HTTP. Không viết lại, không thay schema V23.

Hai điểm cần biết khi re-host (không sửa trong Phase 1):

- Manifest cache nằm trong RAM (`OrderGalleryService.cs:33`), nên restart service làm manifest stale. Hành vi này chấp nhận được vì client tạo manifest mới.
- `ThumbnailService.cs:217` lưu `SourceRelativePath` bằng **full path** và không đặt `root_folder_id`. Cache vẫn đúng trên một server, nhưng nên ghi nhận trước khi đổi root sang UNC.
