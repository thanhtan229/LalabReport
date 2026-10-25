# LAN Phase 0 — Câu hỏi chặn Phase 1/2

Ngày lập: 25/10/2026. Chỉ liệt kê các quyết định **thực sự chặn** Phase 1 hoặc Phase 2. Mỗi câu có mặc định đề xuất. Phase 1/2 sẽ chuẩn bị theo mặc định đó, nhưng **không coi đó là người dùng đã phê duyệt**.

Bằng chứng xem `docs/LAN_PHASE0_INVENTORY.md`; quyết định kiến trúc xem `docs/adr/ADR-002-lan-authoritative-server.md`.

| ID | Chặn | Câu hỏi | Mặc định đề xuất |
|---|---|---|---|
| Q-B1 | Phase 2 (schema capability/session), Phase 4 (UI billing) | Có thêm vai trò **Thu ngân** với PIN riêng không? Ai được override số lượng/giá, reopen bill, hủy xác nhận thanh toán? | Xem chi tiết bên dưới |
| Q-B2 | Phase 2 (thiết kế lock/busy, load test) | Số máy trạm đồng thời, số đơn/ngày, số ảnh/đơn, dung lượng archive? | Pilot 1 server + 2 máy trạm; thiết kế và load test cho **10** client; giả định < 500 đơn/ngày. Nếu > 20 client, mở lại quyết định SQLite |
| Q-B3 | Phase 1 (service account, ACL, cấu hình root) | Root đơn hàng nằm trên **ổ cục bộ** của máy chủ hay **NAS/SMB**? Windows của máy chủ là bản nào? Ai có quyền admin để cài service? | Root trên ổ NTFS cục bộ của máy chủ; service chạy dưới `NT SERVICE\LalabServer`; NAS/SMB **ngoài phạm vi** đợt đầu |
| Q-B4 | Phase 1 (cutover dry-run), Phase 2 (Cloud adapter) | Xưởng nào đang bật Cloud/PWA, dùng worker/D1 nào, `stream_id` nào? Máy chủ mới có phải là **chính máy** đang chạy app hiện tại không? | Một xưởng, một stream; service chạy trên chính máy hiện tại; Cloud tắt ở server profile Phase 1 |
| Q-B5 | Phase 1 (composition root), Phase 5 (installer) | Có giữ chế độ **standalone** (một máy, không service) lâu dài không? | Giữ standalone đến hết pilot; sau đó standalone = server + client loopback trên một máy |
| Q-B6 | Phase 1 (target framework của project mới), Phase 5 (packaging) | **.NET 8 hết hỗ trợ ngày 10/11/2026.** Nâng runtime khi nào? | Project mới target `net8.0-windows` cho khớp code hiện có; nâng .NET 10 LTS trong **batch riêng** có regression đầy đủ, trước Phase 5 |
| Q-B7 | Phase 1 (service có khởi động AutoScan không) | Khi chuyển sang service, giữ **idle scan** và **startup scan** không? | Server profile **tắt** cả idle scan và startup scan; scan chỉ chạy theo yêu cầu có phạm vi |

Chi tiết từng câu hỏi ở mục 1–7 bên dưới. Mục 8 liệt kê các điểm không chặn.

---

## 1. Q-B1: Vai trò Thu ngân và quyền override

**Hiện trạng:**

- Có hai vai trò: `Staff`, `Admin` (`Core/Domain/Enums.cs:251`).
- Override số lượng/giá, reopen, split và toggle thanh toán trên desktop **không** kiểm vai trò. Desktop hiện là một người dùng tin cậy.
- Trên PWA, toggle thanh toán yêu cầu Admin (`UI/Web/KestrelMobileWebServer.cs:790`).

**Mặc định đề xuất:** chưa thêm Thu ngân ở đợt đầu. Admin trên máy trạm làm thu ngân. Schema capability (ADR-002 D3) cho phép thêm vai trò sau mà không cần migrate lại. Cụ thể:

| Đối tượng | Quyền |
|---|---|
| Staff | Giữ đúng quyền PWA hiện có: xem đơn, delivered/printed/note, in tem đơn. Không có quyền tài chính |
| Admin | Draft/lock/xuất bill, xác nhận thanh toán |
| Admin, **bắt buộc lý do** | Override số lượng/giá, reopen, hủy xác nhận thanh toán, split |

**Nếu chọn thêm Thu ngân:**

- Thu ngân có `finance.read`, `bills.draft`, `bills.lock` và `payments.confirm`.
- Override, reopen và hủy thanh toán vẫn chỉ Admin, trừ khi người dùng chỉ định khác.
- Cloud chưa hỗ trợ vai trò Thu ngân. Trên PWA cloud, Thu ngân không có quyền thanh toán cho đến khi worker được cập nhật.

## 2. Q-B2: Quy mô

Mặc định chỉ dùng để đặt `busy_timeout`, page size và mục tiêu load test (plan §13: p95 đọc < 1 s, lệnh trạng thái < 2 s). Mặc định này không thay đổi kiến trúc trừ khi quy mô vượt xa.

## 3. Q-B3: Root, máy chủ, service account

**Nếu root là NAS/SMB:**

- Cần UNC và tài khoản service có quyền trên share (gMSA hoặc tài khoản chuyên dụng).
- Phải test thực tế thông tin enumerate, quyền và độ trễ trên share đó.
- Icon marker `desktop.ini` hiện trỏ tới `%LocalAppData%` của user (`Infrastructure/Windows/FolderVisualMarkerService.cs:25-27`). Máy trạm duyệt qua share sẽ không thấy icon này. Việc này cần một quyết định riêng ở Phase 5.

DB **luôn** ở đĩa cục bộ của máy chủ, bất kể root nằm ở đâu (ADR-002 D6).

## 4. Q-B4: Cloud/PWA và danh tính máy

Cần inventory thực tế trước cutover. Inventory dry-run (ADR-002 D7) sẽ in ra:

- `stream_id`, endpoint, cursor;
- số outbox pending;
- số print ledger chưa ack.

Lý do mặc định chạy service trên chính máy hiện tại:

- Heartbeat dùng `Environment.MachineName` làm `deviceId` (`Infrastructure/Services/CloudSyncService.cs:721`). Giữ cùng máy thì Cloud vẫn thấy cùng thiết bị.
- DB được chuyển cục bộ, không qua mạng.

**Nếu đổi sang máy khác:** cần quyết định tường minh. Hoặc giữ `deviceId` cũ bằng cấu hình, hoặc chấp nhận thiết bị mới trên Cloud. Không được tự tạo identity mới gây seed lại dữ liệu.

## 5. Q-B5: Standalone

**Nếu cần standalone lâu dài cho xưởng khác:**

- Standalone là một **deployment profile** dùng cùng application handlers.
- Profile này có test riêng.
- Không duy trì đường ghi trực tiếp từ ViewModel song song.

**Với xưởng đã chuyển sang server mode:** chặn mọi writer local (ADR-002 D1).

## 6. Q-B6: Runtime .NET

`Microsoft.Data.Sqlite` và `Microsoft.Extensions.*` đang ở 10.0.12 trên target `net8.0`. Có hai lựa chọn:

- **Nâng runtime trước Phase 1:** giảm rủi ro hỗ trợ, nhưng trộn hai thay đổi lớn vào cùng một đợt.
- **Mặc định:** giữ `net8.0` cho Phase 1–2 và nâng trong một batch riêng trước khi đóng gói. Phần mềm vẫn chạy sau ngày hết hỗ trợ, nhưng không còn bản vá bảo mật.

## 7. Q-B7: AutoScan trong service

**Hiện trạng:**

- Mặc định `AutoScanStartup=true`, `EnableIdleScan=true` (`Core/Domain/Models.cs:438,441`).
- Idle được đo bằng `GetLastInputInfo` của **phiên desktop** (`Infrastructure/Windows/WindowsIdleDetector.cs:17-26`), nên vô nghĩa trong Session 0.
- Plan §10 cấm service tự full scan khi khởi động.

**Mặc định đề xuất:** server profile không chạy `AutoScanCoordinator`. Đây là **thay đổi hành vi** so với app hiện tại, nên cần người dùng xác nhận.

**Phương án thay thế** nếu cần giữ quét tự động: lịch quét có phạm vi do Admin cấu hình, ví dụ quét ngày hôm nay lúc 22:00. Lịch này phải được tài liệu hóa như một chính sách scan mới.

---

## 8. Không chặn: ghi nhận để quyết định sau

Các điểm sau **không** chặn Phase 1/2. LAN re-host giữ nguyên hành vi hiện có; không sửa trong dự án LAN nếu chưa có quyết định riêng.

- Guest/Quick Bill còn logic so Source/Print (`Core/Services/CustomerBillingService.cs:1237-1246`). Các fallback `PrintCount ?? SourceCount` vẫn tồn tại (inventory C1).
- Heuristic explicit order và nhánh KHACH_LE fall-through (`Core/Services/FolderStructureParser.cs:495-502,580-599`) (inventory C2).
- Quick Bill ghi order dù `persistDraft:false` (`Core/Services/CustomerBillingService.cs:1266`).
- `DataPurgeService` đặt `foreign_keys=OFF` bên trong transaction (`Infrastructure/Data/DataPurgeService.cs:94-159`).
- Quick Bill từ thư mục ngoài root server: đợt đầu không hỗ trợ (plan §8.5). Không cần quyết định thêm.
- TLS/chứng chỉ cho máy trạm: chặn Phase 3 (pairing), không chặn Phase 1/2. Phase 1 server dev chỉ nghe loopback.
