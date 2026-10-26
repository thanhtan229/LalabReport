# ADR 002: Máy chủ LAN có thẩm quyền và máy trạm (LAN Authoritative Server)

> **Trạng thái:** Proposed (Phase 0, chờ duyệt)
> **Ngày:** 2026-10-25
> **Phạm vi:** Lalab Auto Report Desktop (.NET 8 WPF, SQLite), Kestrel LAN/PWA, Cloud Sync V2
> **Tham chiếu:** `docs/PLAN_LAN_SERVER_WORKSTATIONS.md`, `docs/LAN_PHASE0_INVENTORY.md` (bằng chứng `file:line`), `docs/LAN_PHASE0_QUESTIONS.md`, `docs/adr/ADR-001-cloud-sync-v2-protocol.md`, `docs/PLAN_ORDER_THUMBNAIL_GALLERY.md`, `ARCHITECTURE.md`

---

## 1. Bối cảnh

Hiện tại một process WPF giữ composition root duy nhất (`UI/App.xaml.cs:528-612`). Process đó vừa migrate DB, quét filesystem, tính bill, chạy Cloud timer và Kestrel LAN. DB nằm trong `%LocalAppData%` của user đăng nhập (`SqliteConnectionFactory.cs:20-26`).

Ngoài process UI còn hai đường ghi khác:

- Process headless `--toggle-printed` (`UI/App.xaml.cs:150-183`).
- PWA LAN, qua toggle không idempotent (`KestrelMobileWebServer.cs:547,573,796`).

Không có revision trên aggregate, không có audit, không có thu hồi phiên. Inventory đầy đủ ở `docs/LAN_PHASE0_INVENTORY.md` §3 và §5.

Xưởng cần một máy chủ chạy Windows Service, nhiều máy trạm desktop dùng chung dữ liệu, PIN theo vai trò, và in tại máy trạm (plan §1).

## 2. Quyết định

### D1. Một server có thẩm quyền, một writer host

- **Lalab Server** (Windows Service) là process **duy nhất** mở DB nghiệp vụ, chạy migration, quét filesystem, chạy Cloud sync và scheduled maintenance.
- Mọi UI, kể cả desktop trên chính máy chủ, là **client HTTP** (loopback hoặc LAN).
- Bảo đảm single-writer bằng hai lớp, kiểm tra trước khi migrate:
  1. **File lock độc quyền** `server.lock` (`FileShare.None`) trong data directory. Lock giữ suốt vòng đời process, chứa `ServerInstanceId`, PID và BuildId để chẩn đoán. Lock theo **đường dẫn DB**, không theo session, nên phủ được Session 0 lẫn desktop user. Mutex `Local\` hiện tại không phủ được (`UI/App.xaml.cs:30`).
  2. `Global\` mutex dẫn xuất từ hash đường dẫn DB, để báo lỗi sớm và rõ.
- Không lấy được lock thì process **không** mở DB, không migrate và trả trạng thái `NotOwner` cho người gọi.
- Client profile không đăng ký `ISqliteConnectionFactory`. Bất biến này được kiểm bằng test DI, không chỉ bằng quy ước.
- Đường headless `--toggle-printed` và IPC `TOGGLE_PRINTED` ở server mode chuyển thành lời gọi HTTP tới server (Phase 5). Từ Phase 1, khi phát hiện server profile đang hoạt động, đường headless **từ chối mở DB** và báo lỗi rõ.
- Bỏ hành vi "kill process cùng tên khi pipe không trả lời" (`UI/App.xaml.cs:116-126`) trong server/client profile. Ownership xác định bằng lock và identity, không bằng tên process.

### D2. Hợp đồng application trung lập với transport; tách composition root

- Tạo `LalabAutoReport.Application`. Project này chứa use-case handlers mỏng, gọi các service Core hiện có (`CustomerBillingService`, `ScanService`, `PrintStatusService`, `ReportService`, `IOrderGalleryService`…) **mà không đổi quy tắc billing**. Handlers là nơi đặt authorization theo capability, idempotency, revision check và audit.
- Tạo `LalabAutoReport.Contracts`: DTO, command, error code, `ApiVersion`, capability names. Không chứa connection string hay secret.
- Tách việc đăng ký DI thành một extension method dùng chung (ví dụ `AddLalabServerCore(dataDirectory)`):
  - **Server host** dùng extension này cộng Kestrel endpoints.
  - **Standalone WPF** (giai đoạn chuyển tiếp) dùng cùng extension trong process, nên không duy trì hai bản đăng ký. Hiện chỉ có một bản tại `UI/App.xaml.cs:534-595`.
  - **Client WPF** chỉ đăng ký typed HTTP client, ViewModels và renderer/printing cục bộ.
- Chuyển endpoint mapping của `KestrelMobileWebServer` và `MobileSpaHtmlProvider` sang `LalabAutoReport.Server`. File này đã không dùng `Application.Current`/Dispatcher (inventory §3.10). Nó nằm trong UI project chỉ vì `FrameworkReference` (`LalabAutoReport.UI.csproj:14`).
  - Legacy `/api/...` giữ nguyên contract cho PWA.
  - API desktop mới nằm dưới `/api/v1/...`.
- API được thiết kế theo **thao tác nghiệp vụ** (load dashboard, build draft, lock bill…), không theo từng query repository.
- **Gallery** là application service có sẵn (`IOrderGalleryService`, `Core/Interfaces/IOrderGalleryService.cs:8-14`) và chỉ được **re-host**. Server đăng ký nguyên `OrderGalleryService`, `ThumbnailService`, `SqliteThumbnailCacheRepository`, và giữ endpoint gallery hiện có (`KestrelMobileWebServer.cs:350-444`). WPF `OrderGalleryWindow` chuyển sang gọi cùng endpoint qua client ở Phase 3. Không viết lại, không đổi schema V23.

### D3. Danh tính: PIN theo vai trò, phiên, capability

- Giữ mô hình **PIN dùng chung theo vai trò**, không có tài khoản cá nhân. Giữ các ràng buộc của `MobileAuthService`:
  - không PIN mặc định: AdminPin rỗng thì không ai đăng nhập (`MobileAuthService.cs:152`);
  - PIN các vai trò không được trùng nhau (`:158`, `KestrelMobileWebServer.cs:185`);
  - so chữ ký constant-time (`:131`).
- Bổ sung ở Phase 2 (additive):
  - **Lưu PIN dạng hash** (PBKDF2/Argon2 có salt) thay cho plaintext (`SqliteSettingsRepository.cs:362,365`). Migration chuyển đổi một lần.
  - **Session có trạng thái** trong DB: `SessionId`, `Role`, `DeviceId`, `IssuedAt`, `ExpiresAt`, `RevokedAt`, `CredentialEpoch`. Token mang `SessionId` và `CredentialEpoch`. Đổi PIN hoặc khóa thiết bị sẽ tăng epoch, làm mọi phiên cũ hết hiệu lực. Hiện tại token HMAC stateless không thu hồi được (`MobileAuthService.cs:60-72`).
  - **Device** đăng ký bằng mã ghép nối dùng một lần do Admin cấp. Device nhận secret riêng, được lưu ở máy trạm bằng DPAPI (CurrentUser). `DeviceId` tự khai không phải bằng chứng xác thực.
  - **Rate limit** đăng nhập theo IP+device: backoff tăng dần và khóa tạm.
  - Desktop/API mới **không** nhận token qua query string. Legacy PWA vẫn được dùng `?token=` cho `<img>` (`KestrelMobileWebServer.cs:1110-1116`) trong compatibility adapter, giới hạn ở các GET ảnh.
- **Capability** là đơn vị kiểm quyền ở mỗi handler. Vai trò chỉ là tập capability (ví dụ `orders.read`, `orders.setDelivered`, `orders.setPrinted`, `orders.editNote`, `finance.read`, `bills.draft`, `bills.lock`, `bills.overrideQuantityPrice`, `bills.reopen`, `payments.confirm`, `payments.revoke`, `catalog.edit`, `scan.request`, `admin.maintenance`).
  - Ma trận mặc định là plan §6. Riêng **Thu ngân** chờ Q-B1.
  - Staff giữ **chính xác** quyền hiện có trên PWA: xem đơn, delivered/printed/note, in tem đơn. Không cấp thêm capability tài chính.
  - DTO, artifact và event được lọc theo capability ở server.
- **Vai trò thiết bị** (Server/Workstation) tách khỏi **vai trò phiên**. Desktop chạy trên máy chủ không mặc nhiên có quyền Admin.
- Audit không thể xác định cá nhân thao tác (PIN dùng chung). Audit ghi role, device, session và tên nhập tay nếu có.

### D4. Concurrency và idempotency

Áp dụng trước khi mở bất kỳ write LAN nào từ client mới (Phase 2). Mọi thay đổi schema đều additive.

1. **Revision theo aggregate/field.** Thêm `revision INTEGER NOT NULL DEFAULT 0`:
   - `orders` tách theo nhóm field vận hành: delivered, printed, note. Mục đích là tránh conflict chéo không liên quan.
   - `customer_bills` có một revision chung cho draft, membership, lock, reopen và payment.
   - Các bảng danh mục (customers, aliases, specifications, root_folders).
   - Cập nhật luôn có điều kiện `WHERE id=@Id AND revision=@Expected` trong transaction. `affected=0` trả 409 kèm revision hiện hành.
   - Cơ chế `expectedUpdatedAt` của Split (`SqliteCustomerBillRepository.cs:263`) được chuyển sang revision.
2. **Set desired state** thay cho toggle: `printed=true|false`, `delivered=true|false`, `paid=true|false`.
   - Legacy toggle của PWA đi qua adapter. Adapter đọc trạng thái, tính đích, rồi gọi set-command với operation do adapter sinh. Adapter được ghi rõ là **không retry-safe**.
   - PWA nâng lên set-command trong cùng release tương thích.
3. **Command receipts (idempotency ledger).** Bảng `command_receipts(operation_id PK, actor_session, capability, payload_hash, status, result_json, created_at)` được ghi **cùng transaction** với thay đổi nghiệp vụ. Thứ tự xử lý:
   - Cùng `OperationId`, cùng payload: trả kết quả cũ, **sau khi** kiểm quyền phiên hiện tại.
   - Cùng `OperationId`, khác payload: trả 409 `IDEMPOTENCY_CONFLICT`.
   - Kiểm receipt **trước** kiểm revision, để retry của lệnh đã commit nhận thành công cũ chứ không nhận stale giả.
4. **Operation lookup.** `GET /api/v1/operations/{operationId}` trả `committed | failed | unknown`. Client timeout phải tra lookup trước khi tạo lệnh mới.
5. **Cloud outbox dùng `OperationId` của command.** `EnqueueAsync` (`SqliteCloudSyncStateRepository.cs:109-116`) nhận `operationId` từ handler thay vì tự sinh GUID. Một lệnh LAN tương ứng đúng một operation Cloud, và retry không nhân bản.
6. **Claim job khi lock.** Thêm điều kiện claim vào transaction lock: `UPDATE order_item_scans SET customer_bill_id=@Id WHERE id IN @Ids AND (customer_bill_id IS NULL OR customer_bill_id=@Id)`. So số dòng ảnh hưởng với số job; lệch thì rollback và trả 409. Đây là sửa lỗ hổng R2 (`SqliteCustomerBillRepository.cs:298-303`). Thay đổi **không đổi công thức** và vẫn cho phép split hợp lệ, vì split chuyển job giữa các draft trong transaction riêng (`:249-283`).
7. **Draft ownership** (`owner_session`, `owner_device`, `claimed_at`) chỉ dùng để hiển thị và tiếp quản tường minh, không khóa DB. Draft không giữ transaction trong lúc review.
8. **Audit/outbox/change journal.**
   - `audit_log` ghi trong cùng transaction.
   - `change_journal(seq, entity_type, entity_id, revision, generation)` phục vụ catch-up khi client reconnect.
   - `side_effect_jobs` (marker/icon) là outbox bền vững cho tác vụ filesystem, có retry giới hạn.
9. **Marker là side effect, DB là nguồn chính.** Ở server mode, recovery marker→DB **không** được hồi sinh trạng thái khi DB đã có bản ghi NotPrinted. Hành vi hiện tại vi phạm điều này (`PrintStatusService.cs:326-360`). Marker chỉ được nhập vào DB khi DB chưa có bản ghi nào.
10. **SQLite.** Đặt `busy_timeout` có giới hạn. Write transaction ngắn. Không mở transaction trong lúc quét đĩa: verify scan trước, sau đó commit ngắn có revalidate (giữ mẫu `CustomerBillingService.cs:500-531`). Semaphore trong process chỉ dùng để điều phối, không phải bảo đảm.
11. **Settings** chuyển sang ghi theo field hoặc nhóm field có revision. Không còn upsert toàn object từ nhiều nơi (`SqliteSettingsRepository.cs:278-458`, `CloudSyncService.cs:194`).

### D5. Tách renderer: tạo artifact ở server, in ở máy trạm

- Server tạo **immutable artifact** (JPEG bill, PNG/tem) từ snapshot bill đã khóa và template có version. Mỗi artifact có `ArtifactId`, checksum, content type, `BillRevision` và `TemplateVersion`. Render lỗi không rollback bill đã khóa. Retry render theo cùng bill revision và template version.
- Máy trạm tải artifact (có kiểm quyền), preview, rồi in bằng máy in **cục bộ**, kèm clipboard và Save As. Không gọi `PrintDialog` trong service. Lời gọi `new PrintDialog()` trong Infrastructure (`WpfShippingLabelExporter.cs:210`) được tách thành adapter chỉ có ở client.
- Client WPF giữ khả năng render tem từ artifact/DTO cho máy in nhiệt khi cần layout theo driver. Dữ liệu và tiền vẫn lấy từ server; client không tự tính.
- Endpoint PWA in vật lý trên máy chạy server (`KestrelMobileWebServer.cs:619-664,890-953`) giữ nguyên hành vi trong compatibility adapter. Nó vẫn in tại máy chủ, giống hôm nay. Không mở rộng sang việc gửi lệnh in từ máy trạm tới máy chủ (non-goal của plan §4).
- **Spike Phase 1:** chạy `WpfJpegBillExporter` trong Windows Service (Session 0, STA thread riêng, đã có ở `WpfJpegBillExporter.cs:48-70`) trên fixture snapshot. So sánh pixel/hash với output desktop, đo thời gian, kiểm font và VietQR. Nếu không ổn định, thay adapter renderer. **Không** chuyển việc tính tiền xuống client.
- Phân biệt các trạng thái "đã xuất artifact", "đã gửi spooler" và "đã in vật lý". Client không tự retry thao tác in khi timeout.

### D6. Vị trí DB, ACL, service account, quy tắc WAL một host

- Data directory của server là `%ProgramData%\LalabAutoReport\Server\`, gồm `db\`, `backups\`, `logs\`, `artifacts\`, `thumbnails\`, `server.lock` và `server.json`. Đường dẫn cấu hình tường minh, không dùng `LocalAppData` của tài khoản service.
- ACL:
  - Service account: Modify.
  - Administrators: Full.
  - Users: không có quyền truy cập DB/secrets. Tùy chọn: quyền đọc `logs\` cho chẩn đoán.
- Service account mặc định là **virtual account** `NT SERVICE\LalabServer`, đủ khi root nằm trên ổ cục bộ. Khi root ở NAS/SMB, cần tài khoản có quyền share (gMSA hoặc tài khoản dịch vụ chuyên dụng), dùng UNC, không dùng mapped drive. Xem Q-B3.
- **Quy tắc WAL:** DB chỉ được mở bởi process giữ `server.lock`, trên đĩa cục bộ của máy chủ. Cấm đặt DB trên SMB/NAS, cấm share file `.db` và cấm máy trạm mở trực tiếp. Lý do: WAL cần shared memory cùng host.
- Migration chỉ do server chạy, sau khi giữ lock và tạo backup thành công. Ở server profile, **backup trước migrate lỗi thì dừng (fail-closed)**, khác hiện tại chỉ ghi warning (`DatabaseMigrator.cs:55-66`).
- Secrets được bảo vệ bằng DPAPI LocalMachine, ACL theo service, hoặc file chỉ service đọc được:
  - `CloudSyncSecret` (đang plaintext, `SqliteSettingsRepository.cs:395`);
  - `MobileAuthSecret`;
  - private key TLS.
- Client không giữ cloud secret.

### D7. Migration, cutover, rollback và inventory toàn vẹn

- **Công cụ cutover có dry-run** (Phase 1, chỉ chạy trên fixture). Lệnh `inventory` mở DB nguồn **chỉ đọc** qua SQLite Backup API sang bản tạm, rồi xuất báo cáo:
  - số orders/items/bills theo trạng thái;
  - tổng `grand_total` của bill Locked/Exported;
  - công nợ chưa thanh toán;
  - hash nội dung snapshot từng bill khóa;
  - danh sách `root_folders` (id, path);
  - bộ đếm customers/aliases/giá;
  - `schema_migrations` max;
  - Cloud: `stream_id`, endpoint, cursor, `requires_reconciliation`, số outbox pending, print ledger chưa ack.
- Cutover thật (Phase 5/6), theo thứ tự:
  1. Maintenance.
  2. Dừng đúng writer cũ, xác minh bằng lock/PID/path chứ không bằng tên process.
  3. Backup API.
  4. Inventory trước.
  5. Đặt DB vào data directory server, giữ nguyên ID, provenance và Cloud state.
  6. Server migrate.
  7. Inventory sau. Mọi bộ đếm, tổng và hash phải khớp.
  8. Readiness.
  9. Chuyển desktop cũ sang client profile.
- Máy trạm **không** import DB local của mình.
- **Guard Cloud:** `EnsureLocalOnlyAsync` (`DatabaseLifecycleGuard.cs:20-35`) chặn restore DB đã liên kết Cloud. Cutover **không phải restore** và không bypass guard này. Đây là lệnh offline riêng, có các tiền điều kiện:
  - outbox rỗng, hoặc được mang nguyên;
  - không có print ledger STARTED;
  - stream/endpoint không đổi.
  Sau cutover, server tiếp tục cùng `stream_id` và cursor, không seed lại.
- **Rollback:**
  - Trước khi mở write: dừng service và dùng lại DB gốc (đã giữ nguyên) với binary cũ.
  - Sau khi đã có write mới: không chép đè. Chuyển sang maintenance, backup hiện trạng, rồi forward-fix hoặc recovery có đối soát.
- **Generation:** bảng `server_identity(server_instance_id, database_generation, created_at)` (additive, Phase 1). Restore hoặc reset sẽ tăng `database_generation`, làm client hủy cache, draft và cursor.

### D8. Cloud sync chuyển về service

- Chỉ server đăng ký và chạy `CloudSyncService` và `CloudMediaSyncService`, gồm timer 15 s, pull/push, remote print và gallery jobs (`CloudSyncService.cs:84-107,747-894`). Client không đăng ký các service này.
- Giữ nguyên protocol ADR-001: cursor, stream binding, fail-closed, per-field revision, projection không ghi field vận hành.
- Cloud inbound (`ApplyEventAsync`, `SqliteCloudSyncStateRepository.cs:31-96`) là một producer. Ở Phase 2 nó đi qua adapter cùng ghi `change_journal`/`audit_log`. Phải tăng revision LAN tương ứng, để client LAN đang giữ revision cũ nhận 409 thay vì ghi đè. Quy tắc reconcile hai loại revision (cloud event id và LAN revision) được tài liệu hóa và test riêng. Không tuyên bố hai loại đã đồng nhất.
- `deviceId = Environment.MachineName` (`CloudSyncService.cs:721`) giữ nguyên nếu service chạy trên cùng máy. Nếu đổi máy, cần quyết định tường minh (Q-B4).
- Mapping Thu ngân sang Cloud chỉ thực hiện khi API Cloud hỗ trợ. Mặc định Cloud giữ ma trận quyền cũ.

### D9. Health, readiness, BuildId, handshake API version

- `GET /api/v1/health/live`: process sống, không auth, không lộ path hay secret.
- `GET /api/v1/health/ready` trả các trường sau:
  - `status`: `Starting | Migrating | Ready | Maintenance | NotOwner | Failed`;
  - `buildId` (git SHA + build time, nhúng lúc build), `productVersion`;
  - `apiVersion` dạng `major.minor`, `minClientApiVersion`;
  - `schemaVersion`, `serverInstanceId`, `databaseGeneration`;
  - danh sách capabilities;
  - `cloud`: `disabled | ok | failClosed`.
  Chi tiết khác chỉ dành cho Admin.
- Handshake: client gửi `X-Lalab-Client-Api` và BuildId. Server từ chối **command** (409 hoặc 426 kèm code `CLIENT_TOO_OLD`/`SERVER_TOO_OLD`) khi major không khớp hoặc client dưới `minClientApiVersion`. Read có thể vẫn được phục vụ nếu tương thích.
- Startup không trả `Ready` cho đến khi đủ cả ba điều kiện: giữ lock, migration và recovery hoàn tất, data directory ghi được. Startup **không** tự kích hoạt full scan.
- Dev scripts và verification dùng `ready.buildId` + PID + path làm bằng chứng current build, thay cho chỉ tên process (`DEV_START.bat`).

### D10. Non-goals (đợt này)

- Nhiều server cùng ghi, replication, HA hoặc failover tự động.
- Ghi offline ở máy trạm rồi merge. Mất kết nối thì không lock bill và không xác nhận thanh toán.
- Tài khoản cá nhân, sổ cái kế toán, thanh toán từng phần, thay đổi công thức giá/số lượng.
- **Thay đổi bất kỳ quy tắc billing V1/V2 nào**: conflict register C1–C10 được đóng băng nguyên trạng.
- Truy cập server từ Internet, mở port router.
- FileSystemWatcher, polling ổ đĩa, mở rộng scan policy, full scan lúc khởi động service.
- Gửi lệnh in từ máy trạm tới máy in của server.
- Viết lại gallery, Cloud protocol hoặc renderer khi spike thành công.
- Nâng runtime .NET trong cùng batch refactor (Q-B6).

## 3. Lát cắt Phase 1 đề xuất (nhỏ nhất, coherent)

Điều kiện tiên quyết: gallery đã commit; baseline test và build đã chạy lại trên commit đó.

1. **Tách DI:** extension `AddLalabServerCore(dataDir)` trong Infrastructure, nhận đúng các đăng ký `UI/App.xaml.cs:534-595` trừ ViewModels, tray và context menu. WPF standalone gọi extension này nên hành vi không đổi. Test DI so sánh tập service trước và sau.
2. **Project `LalabAutoReport.Server`** (`net8.0-windows`, `Microsoft.Extensions.Hosting.WindowsServices`) có các thành phần:
   - console khi dev, Windows Service khi cài;
   - `--data-dir`, mặc định `%ProgramData%\LalabAutoReport\Server`;
   - `server.lock` và `Global\` mutex;
   - backup fail-closed rồi migrate;
   - migration additive **V24 `server_identity`**;
   - health live/ready với BuildId.
3. **Re-host Kestrel:** chuyển `KestrelMobileWebServer` và `MobileSpaHtmlProvider` (kèm gallery endpoints) sang Server.
   - Bind lấy theo settings hiện có, mặc định giữ hành vi. Server dev chỉ nghe loopback.
   - Legacy **read** endpoints và gallery phục vụ fixture.
   - Legacy **write** endpoints ở server profile trả 503 `WRITES_DISABLED_UNTIL_PHASE2`, vì concurrency của Phase 2 chưa có.
   - Standalone không đổi.
4. **Không khởi động** AutoScan và Cloud ở server profile Phase 1 (fixture DB). Gỡ bỏ chờ Q-B7 và Q-B4.
5. **Renderer spike:** harness chạy trong service render bill JPEG và tem từ fixture snapshot, so hash/pixel với desktop. Kết quả ghi vào evidence.
6. **Cutover `inventory --dry-run`** trên bản copy fixture, theo D7.
7. **Guard headless:** `--toggle-printed` từ chối mở DB khi phát hiện `server.lock` đang bị giữ ở đường dẫn server.

Gate (plan §12, Phase 1):

- Service chạy khi chưa có ai đăng nhập.
- Đóng UI không làm dừng service.
- Process thứ hai không mở được DB: kiểm bằng test process riêng.
- PWA đọc được fixture.
- Toàn bộ regression core pass.
- Spike renderer có kết quả.

## 4. Hệ quả

**Tích cực:**

- Một nguồn sự thật.
- Giải quyết R1 (hai writer) ngay ở Phase 1.
- Billing core tái sử dụng nguyên vẹn.
- Gallery và Cloud chỉ đổi host, không đổi logic.
- Standalone vẫn chạy trong lúc chuyển đổi.

**Chi phí/rủi ro:**

- Tăng số project và độ phức tạp vận hành (service, ACL, TLS).
- Phase 2 có nhiều migration additive (revision, receipts, sessions, audit, journal).
- PWA cần một release chuyển sang set-command.
- Renderer trong Session 0 chưa được chứng minh.
- `net8.0` hết hỗ trợ ngày 10/11/2026 (Q-B6).

**Thay thế đã cân nhắc và loại:**

- Share `.db` qua SMB: WAL không hỗ trợ.
- Mỗi máy một DB đồng bộ qua Cloud: tạo merge tiền tệ, trái non-goal offline.
- Remote repository API theo từng query: round trip nhiều, mất ranh giới transaction.
- Chuyển hẳn sang server DB (PostgreSQL): chưa cần ở quy mô đề xuất, chi phí migration lớn.


---

## 5. Phase 1 gate result (25/10/2026)

- Code: commits `08f1b3f` (shared composition root + transport moved to Infrastructure), `e29b3ae` (Server host skeleton), `739ea1e` (read-only loopback mobile API), `425bf86` (renderer spike + headless guard), `fdb4d7f`, `6513edf`, `e229d5c` (review fixes: non-mutating/WAL-safe inventory, dynamic readiness, schema contract, snapshot hash v2).
- Validation: 692 automated tests passed; builds 0 warnings/0 errors.
- Independent QA: PASS (gate lines 2–6; Session 0 service install NOT VERIFIED). Evidence: `docs/qa-evidence-phase1/`.
- Code Reviewer: LGTM (`5960bfa..425bf86`), fixes re-reviewed.
- Final Reviewer: **FINAL PASS** at `e229d5c` (0 blocking findings, 2 non-blocking contract deviations recorded in `docs/phase1-inventory-contract.md` §8).
- Accepted limitations: Windows Service Session 0 execution/rendering not exercised; Cloud/AutoScan disabled in server profile; business writes return 503 `WRITES_DISABLED_UNTIL_PHASE2`; standalone desktop keeps its own LocalAppData DB until the Phase 3 client.
- Phase 2 must address first: revocable sessions/capabilities with hashed PINs; desired-state commands with revisions and idempotency receipts; shared mutation handlers with atomic bill-job claims, audit/outbox and concurrency tests before enabling writes.

---

## 6. Phase 2 gate result (26/10/2026)

- Code: commits `0f2a7b9` (hashed PINs, revocable sessions, capability auth), `5ec3e21` (revisions, command receipts, idempotent order pipeline), `209914c` (v1 order commands, legacy adapters, order writes gate), `ebf61a3` (record B2/B3 gate PASS and enabled order writes).
- Validation: **810 automated tests passed**; builds 0 warnings/0 errors.
- Independent QA: **PASS** (evidence in `docs/qa-evidence-phase2/`).
- Code Reviewer: **LGTM** across all Phase 2 batches (`B1` through `B4`).
- Default write mode: `LalabAutoReport.Server --lan-writes` now defaults to `orders` (enabling `orders.setDelivered`, `orders.setPrinted`, `orders.editNote`).
- Instant rollback: `--lan-writes none` immediately restores Phase 1 read-only surface without schema rollback.
- B4 Load & Contention Evidence: 10 concurrent clients, 60s bounded run with parallel scan-shaped SQLite contention:
  - Total requests: 11,838 completed (197.3 req/s throughput); 0 fatal errors.
  - Read latency: p50 = 1.8 ms, p95 = 6.9 ms (target < 1,000 ms).
  - State command latency: p50 = 4.7 ms, p95 = 464.7 ms (target < 2,000 ms).
  - 100% idempotency replay success (365/365 identical responses with zero duplicate effects).
  - Optimistic concurrency conflicts (409 STALE_REVISION) cleanly handled with re-read and retry.
  - Database integrity: `PRAGMA integrity_check = ok`, 0 FK violations, 0 partial/uncommitted receipts, 0 inconsistent audit entries, Server `inventory --dry-run` PASS.
  - Detailed report: [`docs/qa-evidence-phase2/B4_LOAD_REPORT.md`](../qa-evidence-phase2/B4_LOAD_REPORT.md).
- Accepted limitations:
  - Standalone desktop ViewModel writers remain last-write-wins until Phase 3 client facade (they participate via DB triggers causing 409 on stale LAN clients).
  - Payment toggle (`/api/bills/{id}/toggle-payment`), server bill print, and thumbnail maintenance return `503 WRITES_DISABLED_UNTIL_PHASE2` until Phase 4/5.
  - Windows Service Session 0 execution/rendering remains unverified on live service.
  - Offline brute-force risk of low-entropy PIN hashes is documented and mitigated primarily by OS database ACL.
- Next Phase: Phase 3 (Desktop Client Mode & LAN Pairing / Discovery).
