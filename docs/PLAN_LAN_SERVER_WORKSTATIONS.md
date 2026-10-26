# Kế hoạch triển khai máy chủ và máy trạm trong xưởng

Ngày lập: 25/10/2026. Trạng thái: **Phase 0 hoàn tất (tài liệu) — chưa triển khai code**.

## 1. Mục tiêu và yêu cầu đã chốt

Một xưởng dùng một máy chủ Windows chứa dữ liệu đơn hàng. Chỉ máy chủ quét filesystem và giữ database nghiệp vụ chính. Các máy khác cài app desktop, kết nối qua LAN và dùng cùng dữ liệu.

Người dùng đã xác nhận:

- Máy trạm được đánh dấu đã in, giao hàng, sửa ghi chú, tính bill và xác nhận thanh toán theo quyền.
- Máy chủ chạy bằng **Windows Service**, phục vụ ngay sau khi Windows khởi động, không phụ thuộc đăng nhập hoặc cửa sổ desktop.
- Tiếp tục **PIN dùng chung theo vai trò**, chưa triển khai tài khoản riêng từng nhân viên.
- In bill và tem bằng **máy in của máy trạm đang thao tác**.
- Hiện tại chỉ lập kế hoạch `.md`, chưa viết code, chưa cài dịch vụ hoặc thay đổi dữ liệu.

Các lựa chọn kỹ thuật và ma trận quyền bên dưới là đề xuất triển khai. Không diễn giải chúng thành yêu cầu người dùng đã xác nhận thêm.

## 2. Kết luận kiến trúc

Chọn **một backend có thẩm quyền, nhiều desktop client**, chạy hoàn toàn trong LAN. Tái sử dụng Core, các quy tắc billing, SQLite và WPF hiện tại; bổ sung application services, API và HTTP client. Không viết lại sản phẩm từ đầu.

```text
Máy chủ xưởng
  Lalab Server — Windows Service
    API + xác thực/phân quyền + điều phối lệnh
    Scan / Billing / Reporting / Cloud Sync nếu đang bật
    SQLite chính trên ổ cục bộ + backup + audit
    Thư mục đơn hàng do server truy cập
              ↑ HTTPS LAN
       ┌──────┴─────────┐
Desktop máy chủ     Desktop máy trạm A/B/...
  Cùng là client      Cùng dữ liệu và API
  Kết nối loopback    Không quét, không ghi DB nghiệp vụ riêng
  In/lưu tại máy      In/lưu tại máy
```

Ngay cả desktop trên máy chủ cũng gọi API. Không duy trì đường ghi trực tiếp vào SQLite từ UI, menu Explorer hoặc PWA trong chế độ máy chủ: mọi đường ghi phải đi qua cùng application command handlers.

Giữ SQLite trong giai đoạn này. Database nằm trên đĩa cục bộ của máy chủ, chỉ service sở hữu kết nối. Không share file `.db` qua SMB, không để mỗi máy tự đồng bộ database độc lập. SQLite WAL không hỗ trợ cách nhiều máy cùng mở DB qua network filesystem; xem [SQLite WAL](https://www.sqlite.org/wal.html).

Không yêu cầu Cloud để xưởng vận hành. Cloud hiện hữu là kênh tùy chọn của server, không phải trung gian giữa máy trạm và máy chủ.

## 3. Cơ sở khảo sát repository

Đánh giá theo working tree ngày lập kế hoạch, không coi tài liệu nghiệm thu cũ là bằng chứng build hiện tại đã đạt.

| Khu vực | Hiện trạng quan sát được | Hướng xử lý |
|---|---|---|
| `src/LalabAutoReport.UI/App.xaml.cs` | Desktop đăng ký SQLite repositories, scan, billing, cloud và Kestrel trong cùng process | Tách composition root server; desktop dùng HTTP application client |
| `src/LalabAutoReport.Infrastructure/Data/SqliteConnectionFactory.cs` | DB mặc định trong LocalAppData; bật WAL | Chuyển DB có kiểm soát sang thư mục service trong ProgramData |
| `src/LalabAutoReport.UI/Web/KestrelMobileWebServer.cs` | Đã có LAN API/PWA, ghi chú, giao hàng, đã in, payment, reports; host phụ thuộc UI | Tách API host; giữ adapter tương thích PWA, bổ sung API desktop có version |
| `src/LalabAutoReport.Core/Services/CustomerBillingService.cs` | Có verify scoped trước lock, snapshot và atomic repository calls | Tái sử dụng quy tắc; thêm kiểm soát stale draft và tranh chấp giữa nhiều client |
| `src/LalabAutoReport.Core/Services/PrintStatusService.cs` | DB status kết hợp marker filesystem và visual marker Windows | Server thực thi; tách trạng thái nghiệp vụ và side effect marker |
| `src/LalabAutoReport.Infrastructure/Reporting/` | Export JPEG/tem có phụ thuộc WPF; in tem có PrintDialog | Tách tạo artifact và in tương tác, không gọi dialog trong service |
| `src/LalabAutoReport.Core/Services/MobileAuthService.cs` | Token chủ yếu đại diện vai trò, không phải tài khoản cá nhân | Giữ PIN vai trò, bổ sung phiên, thu hồi và định danh thiết bị |
| `src/LalabAutoReport.Infrastructure/Data/DatabaseLifecycleGuard.cs` | Có guard reset/restore và bảo vệ dữ liệu liên kết Cloud | Giữ nguyên tính an toàn; không bypass để chuyển DB sang service |
| `DEV_START.bat`, `DEVELOPMENT_WORKFLOW.md` | Runtime hiện là WPF in-process; health chủ yếu kiểm tra process | Bổ sung lifecycle riêng cho server, readiness và build identity |

Working tree đang có nhiều thay đổi, gồm billing, gallery, cloud, UI và tests. Phase 0 phải lập baseline để tránh ghi đè hoặc quy lỗi nhầm. Không tự commit, stash hay revert thay đổi của người dùng.

Tham chiếu nghiệp vụ: `docs/archive/PLAN_lalab.md`, `ARCHITECTURE.md`, `ux_ui.md`, `AGENTS.md`, `PLAN_PRODUCTION_HARDENING.md`, `FINAL_REVIEW_VERIFIED.md`. Đầu PLAN đã ghi nhận V2 thay V1 ở một số quy tắc đếm/billing; AGENTS vẫn chứa quy tắc V1. Khi đụng vùng mâu thuẫn phải lập bảng đối chiếu quyết định/acceptance hiện hành, không tự đổi công thức tính bill trong dự án LAN này. Nếu không có bằng chứng yêu cầu mới hơn giải quyết được mâu thuẫn, hỏi người dùng trước khi thay đổi hành vi đó.

## 4. Phạm vi và những điều chưa làm ở đợt đầu

### Trong phạm vi

- Windows Service độc lập UI, sở hữu scan/database và nghiệp vụ.
- App desktop kết nối server: dashboard, tìm kiếm, khách hàng, bảng giá, reports, bill review và các thao tác được cấp quyền.
- Xử lý concurrency, retry, audit, bảo toàn snapshot và chống tính trùng đơn.
- Chọn/lưu/in bill và tem tại máy trạm; hỗ trợ Explorer commands theo phạm vi có thể xác định trên server.
- Di chuyển dữ liệu hiện có, đóng gói, cập nhật, backup/restore và kiểm thử nhiều máy thật.
- PWA LAN và Cloud hiện hữu tiếp tục hoạt động, không mở rộng quyền Staff cũ ngoài ý muốn.

### Ngoài phạm vi đợt đầu

- Nhiều server cùng ghi một xưởng; replication DB hoặc tự bầu server dự phòng.
- Ghi nghiệp vụ offline rồi tự merge khi có mạng. Khi mất kết nối, không cho chốt bill/ghi thanh toán mới.
- Tài khoản riêng theo nhân viên, kế toán sổ cái, thanh toán từng phần hoặc thay đổi công thức giá.
- Truy cập trực tiếp server từ Internet hoặc tự mở port router.
- Tự thêm filesystem watcher, polling ổ đĩa hoặc mở rộng chính sách scan. Việc chạy service không đồng nghĩa cho phép quét liên tục.
- Gửi lệnh in tới máy in của server từ máy trạm.

## 5. Cấu trúc code đề xuất

Tên project dưới đây là dự kiến; chỉ tạo khi đến phase tương ứng.

| Thành phần | Trách nhiệm |
|---|---|
| `LalabAutoReport.Core` | Domain và business rules, không phụ thuộc HTTP/WPF |
| `LalabAutoReport.Application` | Use cases, authorization theo capability, orchestration, concurrency/idempotency boundaries |
| `LalabAutoReport.Contracts` | DTO, command, error codes, API version, capabilities, event envelopes; không chứa connection string hoặc secret |
| `LalabAutoReport.Infrastructure` | SQLite, filesystem, migrations, cloud adapters và triển khai hạ tầng dùng trên server |
| `LalabAutoReport.Server` | ASP.NET Core/Kestrel, Windows Service lifecycle, auth middleware, endpoints, background jobs |
| `LalabAutoReport.Client` | Typed HTTP client, sessions, reconnect, error mapping, event subscriptions |
| `LalabAutoReport.UI` | WPF, ViewModels, nhập liệu/review, in/clipboard/lưu file tại máy đang sử dụng |
| Rendering Windows dùng chung nếu cần | Tạo artifact JPEG/tem trong môi trường Windows không có UI tương tác |

Không mô phỏng repository SQL từ xa bằng một API cho từng query nhỏ. Thiết kế application-level operations như load dashboard, create draft, review draft, lock bill. Giảm round trips và giữ ranh giới transaction trên server.

Tách dần phụ thuộc Windows/WPF của Infrastructure khi host yêu cầu; không refactor toàn bộ chỉ để đẹp kiến trúc. Server Windows có thể dùng renderer STA riêng nếu spike chứng minh chạy được trong service. Không để business operations cần `Application.Current`, interactive desktop, Clipboard hay PrintDialog.

## 6. Quyền và danh tính

### Ma trận quyền đề xuất

Máy chủ/máy trạm là **vai trò thiết bị**, còn Admin/Thu ngân/Nhân viên là **quyền phiên đăng nhập**. Cài ở máy chủ không tự có quyền Admin.

| Thao tác | Nhân viên | Thu ngân (đề xuất mới) | Admin |
|---|---|---|---|
| Xem đơn và trạng thái phi tài chính | Có | Có | Có |
| Đánh dấu/gỡ đã in, giao hàng; sửa ghi chú | Có | Có | Có |
| Xem tiền, công nợ và bill tài chính | Không | Có | Có |
| Tạo/review/tính/chốt bill và xuất/in | Không | Có | Có |
| Xác nhận đã thanh toán toàn bộ bill | Không | Có | Có |
| Hủy xác nhận thanh toán, reopen bill | Không | Không mặc định | Có, cần lý do |
| Sửa bảng giá, aliases, cấu hình root | Không | Không | Có |
| Yêu cầu scan có phạm vi trên server | Không mặc định | Theo capability được cấp | Có |
| Backup/restore, PIN, thiết bị, update service | Không | Không | Có |

Nếu xưởng muốn chỉ giữ hai vai trò hiện tại, dùng Admin trên máy trạm làm thu ngân. Không âm thầm cấp tài chính cho Staff cũ. Việc thêm Thu ngân và quyền override quantity/price/adjustments phải được chốt trước phase billing client; mặc định giữ các guard hiện hành, quyền override là capability riêng.

Mỗi vai trò dùng PIN riêng, không có PIN mặc định; không cho PIN vai trò trùng nhau. Server kiểm quyền ở từng command và từng dữ liệu trả về. UI ẩn nút không thay thế authorization.

Thiết bị có `DeviceId`, tên hiển thị và đăng ký qua mã ghép nối dùng một lần do Admin cấp. Phiên có `SessionId`, role, expiry và cơ chế thu hồi khi đổi PIN/khóa thiết bị. Không dùng DeviceId tự khai là bằng chứng xác thực. Không log PIN/token; lưu credential an toàn trên server, bảo vệ token máy trạm bằng Windows credential protection. Có rate limit đăng nhập.

Audit ghi thời gian server, role, device, session, action, entity, before/after, reason, operation/correlation ID. Vì PIN dùng chung, **không thể xác định chắc chắn cá nhân nào thao tác**; tên người nhập tay chỉ là thông tin bổ sung.

## 7. Hợp đồng API và an toàn nhiều người dùng

### Read models và command

- Namespace mới `/api/v1/...`; giữ legacy `/api/...` cho PWA qua compatibility adapters.
- Read APIs có phân trang, tìm kiếm/lọc trên server, timezone rõ ràng và DTO ổn định.
- Server trả `ServerInstanceId`, `DatabaseGeneration`, `BuildId`, API version và capabilities. Restore tạo generation mới để client vô hiệu cache/draft cũ.
- Commands dùng entity ID + revision + giá trị mong muốn. Không cho client gửi absolute path để server thao tác tùy ý.
- Ví dụ nhóm API: session/device; orders/printed/delivery/note; billing/drafts/review/lock/reopen/payment; reports; artifacts; scoped scan jobs; change feed; admin maintenance.
- Error envelope thống nhất: code, message tiếng Việt, correlation ID, revision hiện hành và cách khắc phục. Phân biệt 401/403, 409 stale/idempotency conflict, 422 cần review, 503 maintenance/unready.

### Concurrency bắt buộc trước khi mở quyền ghi LAN

1. Thêm revision cho các aggregate được sửa; update có điều kiện trên revision trong transaction, không chỉ kiểm tra trước ở application code.
2. Thay command mạng kiểu toggle bằng **set desired state**: `printed=true`, `delivered=true`, `paid=true`. Retry không đảo trạng thái lần hai. Legacy toggle phải được adapter xử lý theo contract cũ, không quảng bá là retry-safe; nâng PWA lên set commands trong cùng release tương thích.
3. Mỗi write command có `OperationId`/idempotency key. Lưu actor, payload hash và kết quả cùng transaction với nghiệp vụ. Cùng key/cùng payload trả kết quả cũ sau khi kiểm tra quyền; cùng key/khác payload trả conflict.
4. Idempotency check precedes stale-revision rejection khi command đã commit: request retry phải nhận thành công cũ, không nhận stale giả.
5. In-memory semaphore chỉ hỗ trợ điều phối; transaction, conditional updates và constraints mới bảo vệ dữ liệu qua restart. SQLite chỉ giữ write transaction ngắn, có bounded busy handling; không mở transaction suốt lúc quét đĩa.
6. Hai người sửa ghi chú cùng revision: một thắng, một nhận conflict và được xem nội dung hiện tại, giữ bản nhập để sửa lại. Không tự ghi đè.
7. Trạng thái in/delivery/payment có revision phù hợp theo aggregate/field để giảm conflict không liên quan; không dùng merge last-write-wins cho tiền hoặc membership bill.
8. Mọi producer, gồm server desktop, PWA, Explorer và Cloud inbound, phải dùng command pipeline hoặc adapter có bảo đảm tương đương. Nếu Cloud hiện chưa truyền revision phù hợp, định nghĩa riêng quy tắc reconcile và test, không tuyên bố đã đồng nhất chỉ vì cùng dùng repository.

Ledger idempotency có retention được tài liệu hóa; không tự xóa bản ghi chống trùng của các lệnh tài chính khi còn thời gian client có thể replay. Retry đọc được tự động; retry ghi dùng cùng key và chỉ trong chính sách an toàn. Timeout không đồng nghĩa command đã thất bại: client tra kết quả operation trước khi tạo lệnh mới.

## 8. Luồng nghiệp vụ cần hoàn thiện

### 8.1 Đánh dấu đã in và giao hàng

Máy trạm gửi ID + target status + revision. Server kiểm quyền, kiểm scope rồi commit status/audit. Marker `.lalab-printed`, `desktop.ini` và icon là side effect filesystem có kết quả riêng, không thể atomic với SQLite.

Sử dụng outbox/job bền vững cho marker và retry có giới hạn; DB là nguồn chính. Nếu ghi marker lỗi, UI hiển thị “Đã lưu trạng thái; chưa cập nhật biểu tượng thư mục”, có retry. Không báo toàn bộ lệnh thất bại để người dùng bấm đảo trạng thái. Recovery từ marker không được hồi sinh trạng thái cũ sau khi DB đã gỡ đã in.

Không gộp trạng thái in, giao hàng, khóa bill và thanh toán thành một cờ. Không cập nhật hàng loạt order của cùng khách nếu người dùng chỉ thao tác một physical order.

### 8.2 Tính, review và chốt bill

1. Client gửi customer/order IDs hoặc selection; server tạo draft có ID, revision, provenance, snapshot đầu vào và phạm vi đơn rõ ràng.
2. Việc refresh/verify cần scan đều chạy trên server, đúng phạm vi. Client nhận progress/cancel và không treo UI.
3. Client review các dòng/đơn, quantity/price override có lý do, adjustments và split theo capability. Server tự tính lại tổng; không tin total hoặc đơn giá do client tự khẳng định.
4. Draft có `Revision` và owner session/device để hiển thị ai đang làm. Không tự merge hai draft; cần cơ chế tiếp quản rõ ràng cho draft đã bỏ dở.
5. Chốt bill yêu cầu revision và dấu vết review mới nhất. Server điều phối scan/lock trên tập order có thứ tự ổn định, verify filesystem và bind vào scan snapshot xác định. Nếu observation thay đổi so với bản đã review, dừng để người dùng review lại; không âm thầm chốt số lượng mới.
6. Commit lock trong transaction ngắn: kiểm tra revision/order membership hiện tại, revalidate trạng thái, claim jobs/physical orders theo invariant hiện hành, lưu bill/lines/adjustments/snapshot/audit/idempotency result. Bảo đảm hai máy không cùng chốt bill chứa đơn/dòng trùng nhau; unique constraints thiết kế theo nghiệp vụ split hiện có, không cấm split hợp lệ.
7. Có verification token/scan revision để phát hiện scan mới xen giữa verify và commit. Không thể khóa filesystem khi thợ vẫn sửa ảnh: bill lưu observation đã xác minh; thay đổi về sau chỉ tạo cảnh báo và không sửa snapshot đã khóa.
8. Reopen là command riêng của Admin, có lý do và revision, không bị gọi ngầm khi stale draft hoặc export lại. Split/reopen/lock/payment cùng tham gia kiểm soát concurrency.

Draft không giữ DB transaction/lock trong nhiều phút người dùng review. Export artifact là bước sau commit; renderer lỗi không rollback bill đã khóa hoặc tạo bill mới. Retry export theo cùng bill revision và template version.

### 8.3 Xác nhận thanh toán

Giữ mô hình hiện tại: xác nhận bill đã thanh toán toàn bộ, chưa thêm trả từng phần hoặc payment gateway. Server lấy số tiền từ snapshot bill, chỉ chấp nhận trạng thái hợp lệ. Xác nhận/hủy xác nhận là lệnh rõ ràng, audit riêng, không thay đổi tổng tiền bill.

Hai máy xác nhận đồng thời không ghi hai sự kiện thu tiền. Retry cùng operation trả kết quả cũ. Đụng reopen/sửa revision phải trả conflict thích hợp. Trạng thái pending Cloud không được trình bày thành Cloud đã xác nhận; LAN commit và Cloud replication có trạng thái riêng.

### 8.4 In, xuất file và clipboard

- Server tạo immutable artifact từ snapshot bill và template có version, trả ID/checksum/content type. Artifact không dựa vào đường dẫn file của máy trạm.
- Desktop tải artifact có kiểm quyền, xem preview, chọn máy in và in tại chính máy đang thao tác; clipboard và Save As cũng tại máy đó.
- Service không bật PrintDialog hay dùng default printer của tài khoản service. Phân biệt “đã xuất”, “đã gửi vào spooler” và “đã in vật lý”; không cam kết nhận biết giấy in thành công nếu thiết bị không hỗ trợ.
- Không tự retry thao tác in khi timeout để tránh in trùng. Cho phép người dùng chủ động in lại từ cùng snapshot.
- WPF renderer cần spike trong Session 0/STA không tương tác. Nếu không ổn định, thay adapter renderer phù hợp trong phase này, không chuyển tính tiền xuống client để né lỗi.
- Giữ layout bill/tem, VietQR, font, thermal sizes và nội dung snapshot hiện tại; render-and-compare bằng fixtures và thử máy in thật.

### 8.5 Explorer, đường dẫn và Quick Bill

- Client không quét ổ cục bộ. Menu Explorer trên máy trạm chỉ resolve đường dẫn share đã đăng ký sang `RootId + RelativePath` rồi yêu cầu server xử lý.
- Mapping server path/UNC/client mapped drive là cấu hình có kiểm tra. Không coi `D:\...` trên hai máy là cùng dữ liệu. Kiểm tra traversal, junction/symlink thoát root và scope theo root phía server.
- Với thư mục chưa có order, cho phép server resolve/scan đúng folder trong root nếu được cấp quyền. Ngoài root: báo “Thư mục này chưa thuộc dữ liệu máy chủ”; không tự upload ảnh hoặc tự quét tại máy trạm.
- Quick Bill khách lẻ từ thư mục ngoài root của máy trạm chưa được hỗ trợ trong đợt đầu. Giữ luồng hiện có trên máy chủ nếu được cấu hình như thao tác explicit có quyền; cần use case riêng với scope cho phép, không tạo endpoint arbitrary-path.
- Khi Explorer chạy lúc app đóng, helper đọc profile, xác thực an toàn và gửi lệnh cùng pipeline. Không khởi tạo SQLite riêng hoặc service thứ hai.

## 9. Cập nhật dữ liệu và mất kết nối

Đề xuất SignalR cho thông báo entity changed, scan progress và operation completion; API vẫn là nguồn dữ liệu chính. Event chỉ chứa ID/revision theo quyền, không phát thông tin tiền tới Staff.

Ghi change journal/outbox cùng transaction nghiệp vụ, có cursor để client bắt kịp khi reconnect. Nếu cursor đã hết hạn thì yêu cầu refresh các màn hình/phạm vi đang dùng, không scan đĩa và không tải toàn bộ archive mặc định. Restore/generation đổi làm mất hiệu lực cursor cũ.

Khi mất mạng:

- Hiển thị rõ offline, thời điểm cập nhật cuối, trạng thái thao tác đang chờ xác minh.
- Có thể giữ dữ liệu màn hình hiện tại để xem, nhưng không giả vờ còn online và không lưu pending write để tự merge.
- Giữ bản ghi chú chưa gửi ở dạng bản nhập theo phiên nếu cần; đăng nhập lại/đổi role phải xóa dữ liệu không còn được phép xem.
- Billing/lock/payment bị vô hiệu khi không thể xác nhận trạng thái server. Kết nối lại phải tra OperationId cũ trước khi cho gửi thao tác thay thế.
- Không fallback sang DB local hay quét ổ đĩa trên máy trạm.

## 10. Windows Service, cấu hình và bảo mật LAN

- Service identity có quyền tối thiểu trên DB, logs, artifacts và scan roots. Dữ liệu dùng `ProgramData\LalabAutoReport\Server`, ACL rõ ràng; không ngầm dùng LocalAppData của LocalSystem.
- Không dùng mapped drive theo phiên đăng nhập cho service. Root cục bộ dùng absolute path server; root mạng nếu có dùng UNC và tài khoản service được cấp quyền phù hợp. Phải test thực tế nếu đưa SMB/NAS vào scope.
- Service auto-start, recovery có giới hạn, graceful shutdown, maintenance mode và readiness khi migration/recovery hoàn tất. Startup service không tự trigger full scan.
- Chống hai server/process cùng sở hữu một DB; desktop client và Explorer không migrate DB. Migration chỉ do server thực hiện, có backup và kiểm tra version trước khi ready.
- HTTPS với chứng chỉ/cơ chế trust được provision cho máy trạm. Không dùng bypass certificate validation hoặc ASP.NET development certificate cho service production. Private key có ACL cho service. Health metadata công khai phải tối thiểu, không lộ secret hoặc đường dẫn.
- Firewall chỉ profile/subnet cần thiết. Không bật CORS wildcard không cần thiết; PWA cùng origin. Kiểm tra Origin/CSRF phù hợp với loại auth dùng cho browser; auth desktop không được làm yếu auth PWA.
- Không hardcode IP. Admin nhập hostname/IP, test connection và xác nhận server identity khi ghép nối; đổi server là thao tác rõ ràng, xóa cache/phiên cũ và không tự trộn dữ liệu hai xưởng.
- Chọn target framework/package version sau khi kiểm tra SDK, Windows mục tiêu, vòng hỗ trợ và dependencies hiện tại. Không gộp nâng runtime lớn vào refactor LAN nếu không cần; nếu runtime cần nâng để đủ vòng đời hỗ trợ thì tách batch và chạy regression riêng trước release.

Tham khảo triển khai service và chứng chỉ: [Microsoft — Host ASP.NET Core in a Windows Service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service).

## 11. Dữ liệu, Cloud và migration

Migration dự kiến, với tên/schema quyết định sau khi khảo sát đầy đủ:

- Aggregate/field revisions; draft version và ownership metadata.
- Device/session/role credentials và capability mapping.
- Command receipts/idempotency ledger; operation jobs và outbox/change journal.
- Audit trail; server identity/database generation và deployment metadata.
- Bill membership/claims hoặc constraints phù hợp invariant split hiện hành.

Tất cả migration additive khi có thể, repeatable theo hệ thống hiện tại; có fixture DB cũ và rollback khi migration lỗi. Không xóa DB để nâng cấp.

Chuyển từ app local hiện tại sang service:

1. Inventory đúng DB nguồn, root IDs, khách hàng, giá, orders, bills, membership, ảnh bill, cloud identity/cursor/outbox và secrets.
2. Backup nhất quán bằng SQLite Backup API, không copy riêng file `.db` đang chạy WAL; kiểm tra restore trên bản cô lập.
3. Bật maintenance, dừng đúng process ghi DB cũ, xác minh không còn writer; không copy DB khi cloud/scan đang commit.
4. Chuyển DB và artifacts cần thiết sang server data directory, giữ IDs/provenance, quyền file và bí mật; migrate một lần, giữ bản gốc để phục hồi.
5. Giữ cloud stream identity/cursor và pending operation theo protocol hiện hành. Không tạo desktop identity mới gây seed lại/trùng dữ liệu. Nếu guard hiện tại chặn restore vì đã liên kết Cloud, phải thiết kế migration offline có kiểm tra riêng; không bỏ guard để đi tiếp.
6. Khởi động service, kiểm tra readiness/build/schema, kiểm tổng bills/công nợ/locked snapshots và cloud queue trước khi mở client.
7. Chuyển desktop cũ sang client profile, chặn đường local-write cũ. Các máy trạm không được tự import DB local của chúng vào server.

Chỉ server chạy CloudSync/CloudMediaSync, scheduled maintenance và policy scan đã được chấp thuận. Client không có cloud secret. Giữ permission matrix và contract Cloud cũ; mapping vai trò Thu ngân nếu thêm phải explicit. Không tự cấp Thu ngân quyền qua Cloud khi API Cloud chưa hỗ trợ.

## 12. Thứ tự triển khai và tiêu chí từng phase

Triển khai tuần tự theo các vertical slice có thể test; không mở multi-client writes trước khi concurrency/idempotency hoàn thành.

### Phase 0 — Baseline và chốt hợp đồng

- Đọc đầy đủ phần nghiệp vụ/UX liên quan, ADR và các thay đổi chưa commit; lập conflict register V1/V2.
- Chạy targeted baseline, toàn bộ suite liên quan, build; lưu kết quả và failing tests có sẵn.
- Lập inventory mọi writer: ViewModels, repositories, scan, billing, Explorer, PWA, Cloud, backup/restore, markers.
- Chốt ma trận quyền, phạm vi Quick Bill, số máy/dữ liệu dự kiến và mapping root thực tế. Khảo sát Windows/SDK/cert/service account.
- Tạo ADR cho authoritative server, auth PIN, concurrency, rendering và migration; spike service renderer không cần đăng nhập.

**Gate:** baseline tái lập được; không còn câu hỏi chặn kiến trúc; spike tạo đúng bill/tem không gọi interactive UI. Không dùng kết quả nghiệm thu cũ thay baseline mới.

### Phase 1 — Tách backend chạy service và dữ liệu

- Tách application contracts/composition root; dựng Server chạy console ở dev và Windows Service khi cài.
- Tách API/PWA khỏi UI, nối lại dependencies mà không đổi quy tắc billing.
- DB local server, instance ownership, health/readiness/build/schema, startup/recovery, service config/ACL.
- Chuẩn bị migration/cutover tool có dry-run; chưa đụng DB thật.

**Gate:** service chạy khi chưa đăng nhập, đóng UI không dừng service, chỉ một writer host; PWA đọc được fixture, core regression pass.

### Phase 2 — Command safety, auth và dữ liệu đồng thời

- Revisions, idempotency transaction, audit, outbox, authorization/capabilities và device/session/PIN lifecycle.
- Set-state commands cho note/delivery/printed/payment; marker retry và operation lookup.
- Route legacy PWA và Cloud writes qua adapter có kiểm soát; loại bỏ direct UI/Explorer writer trong server mode.
- Hoàn thiện race protection cho draft/lock/reopen/split/membership ở DB và application layer, dù UI client sẽ triển khai ở phase sau.

**Gate:** test đồng thời và crash/retry chứng minh không lost update, không đảo trạng thái do retry, không bill trùng và không mở rộng quyền Staff cũ.

### Phase 3 — Desktop client đọc và thao tác đơn

- Profile server/client, ghép nối/đăng nhập, typed clients và application facade cho ViewModels.
- Dashboard, orders/details, search, customers, price lists, reports; pagination và loading/cancel.
- Note/printed/delivery bằng set commands, conflict UX, offline và reconnect.
- SignalR/change catch-up; reset cache đúng server generation/role.

**Gate:** hai máy trạm thấy cùng dữ liệu và thay đổi của nhau; client không mở DB nghiệp vụ, không quét filesystem, không tự khởi động Kestrel/Cloud/AutoScan.

### Phase 4 — Billing, thanh toán và in tại máy trạm

- Remote draft/create/review/refresh/lock, overrides, adjustments, split/reopen theo quyền.
- Scoped verification trên server, progress/cancel, stale review và operation recovery UX.
- Xác nhận/hủy payment có quyền, audit và conflict rõ ràng.
- Artifact generation/re-export, preview/download, clipboard/Save As, in JPEG/tem tại máy trạm.
- Guard toàn bộ cửa vào: dashboard, customer billing, guest/Quick Bill trong root, invoices và shortcuts.

**Gate:** hai máy cùng tính/chốt các đơn giao nhau chỉ một kết quả hợp lệ; tiền/snapshot/provenance đúng; printer acceptance trên máy trạm thật.

### Phase 5 — Explorer, vận hành và tương thích đầy đủ

- UNC/root mapping, Explorer helper không local-write, lỗi ngoài scope rõ ràng.
- Cloud/PWA regression, role mapping và pending/conflict UI.
- Backup/restore/maintenance, generation reset, TLS/cert renewal và firewall provisioning.
- Cập nhật launcher/dev scripts để khởi động/dừng đúng server riêng; không dùng process name/port đơn lẻ làm bằng chứng ownership.
- Installer phân biệt Server + Desktop và Desktop Client; workstation installer không cài service hoặc migrate DB.
- Updater service/client riêng: API compatibility handshake, maintenance/drain, backup, schema checks, rollback rules và build verification. Không để updater desktop cũ tự thay binary service đang chạy.

**Gate:** migration rehearsal giữ nguyên dữ liệu và Cloud identity; update/client version mismatch được xử lý an toàn; reboot/reconnect/restore không tạo writer thứ hai.

### Phase 6 — Pilot thực tế và rollout

- Cutover có lịch, backup kiểm chứng, một server + tối thiểu hai máy trạm, máy in thật.
- Chạy một ca làm việc đại diện với billing/payment thật theo quy trình được chủ xưởng duyệt; đối chiếu số lượng, tiền, provenance và công nợ.
- Test mất mạng, restart server, đóng UI máy chủ, client cũ, mất điện mô phỏng trên fixture; không cố tình crash DB thật.
- Rollout thêm máy khi pilot đạt, ghi nhận issues theo mức độ, có runbook phục hồi.

**Gate:** chủ xưởng nghiệm thu checklist; không còn lỗi integrity/phân quyền/billing; có bằng chứng hardware/network áp dụng cho topology thực tế.

## 13. Kiểm thử bắt buộc

| Nhóm | Kịch bản tối thiểu và kỳ vọng |
|---|---|
| Domain regression | Scanner/alias/physical order/giá integer VND/album/locked snapshot/split/guest bill theo yêu cầu đã được chốt |
| Transaction concurrency | Hai lock trùng jobs; draft stale; reopen vs payment; split vs lock; scan vs verify; note conflict; unique constraints vẫn cho split hợp lệ |
| Retry và crash | Commit xong mất response; retry trước/sau restart; cùng key payload khác; request hủy nhưng server đã commit; ledger và business data không tách nhau |
| Filesystem | Path mất/inaccessible, marker fail/retry, root disabled, traversal/junction, mapped drive sai, multi-root không trùng identity |
| Service | Boot chưa login, logout, UI đóng, crash recovery, certificate/private-key ACL, startup migration fail và disk full: không báo ready giả |
| Client isolation | Không có business SQLite, filesystem scan hay cloud secrets; menu/helper không tạo local writer; role đổi không lộ cache tài chính |
| Permissions | Direct HTTP bypass UI; hết hạn/thu hồi session; sai PIN/rate limit; Staff đọc financial DTO/artifact/event; client giả DeviceId; Cloud/PWA giữ quyền cũ |
| Billing/printing | Verify changed data yêu cầu review lại; lock snapshot immutable; render fail sau lock không tạo bill mới; print tại đúng máy; cancel PrintDialog không thay payment |
| Reconnect/events | Mất event, duplicate/out-of-order event, cursor hết hạn, server generation đổi, offline timeout pending operation, không full filesystem scan |
| Migration/restore | DB trước nâng cấp, schema migration fail rollback, restored generation, giữ IDs/snapshots/aliases/giá/Cloud cursor; downgrade bị chặn khi schema không tương thích |
| Performance | Dữ liệu đại diện xưởng, nhiều client, scan cùng đọc report, memory/CPU idle, queue pressure và reconnect storm; không crawl ổ đĩa lúc idle |

Chạy integration tests bằng DB tạm thật và HTTP host thật; concurrency tests phải có barrier để ép race, không chỉ chạy tuần tự. Crash tests dùng process riêng để chứng minh receipt/transaction tồn tại qua restart. UI/printer acceptance cần máy thật và evidence, không suy ra từ unit tests.

Mục tiêu ban đầu cần đo và xác nhận ở Phase 0: pilot 1 server + 2 client, sau đó load test 10 client đồng thời trên dữ liệu đại diện. Đề xuất p95 thao tác đọc phổ biến dưới 1 giây, command trạng thái dưới 2 giây khi không tranh chấp; scan/render/lock có progress và thời gian đo riêng. Đây là target, chưa phải benchmark đã đạt. Giữ idle thấp, không dùng ping ứng dụng hoặc polling dày để thay thế thiết kế reconnect.

## 14. Đóng gói, rollout và rollback

- Gói Server cài Windows Service, cấu hình ProgramData/ACL/cert/firewall và desktop client nếu cần. Gói Client chỉ có UI/native printing và connection profile.
- Có manifest version/API/schema/BuildId/checksum; client/server handshake chặn command khi không tương thích. Không đổi tên release/version chính thức khi chưa có yêu cầu phê duyệt phiên bản tương ứng.
- Sao lưu DB bằng cơ chế nhất quán, kèm artifacts/template/cấu hình cần thiết và hướng dẫn bảo vệ secret. Backup ở thiết bị khác có kiểm tra restore; không nhầm backup với live DB share.
- Trước cutover lập thống kê số bills, orders, tổng locked totals, công nợ, hash snapshot đại diện và liên kết filesystem. Sau cutover đối chiếu cùng tập dữ liệu.
- Rollback trước khi mở writes: dừng service mới và dùng lại bản backup/binary tương thích. Sau khi đã có writes mới: không chép đè DB cũ làm mất giao dịch; maintenance, backup hiện tại, ưu tiên forward fix hoặc recovery có đối soát được duyệt.
- Update/restart không tự hủy lệnh tiền đang commit: ngừng nhận lệnh mới, drain trong timeout, lưu job state và cho client tra operation. Recovery sau crash phân biệt committed/failed/needs review.
- Không hứa high availability: một server dừng thì các máy trạm tạm ngừng ghi. Runbook phải chỉ rõ cách khôi phục và cách kiểm tra dữ liệu trước khi hoạt động lại.

## 15. Quy trình thực hiện mỗi coding batch

1. Nêu phase, acceptance criteria và danh sách file dự kiến; kiểm tra git diff trước chỉnh sửa.
2. Hoàn thành một behavior end-to-end; thêm migration/tests cho thay đổi persistence hoặc nghiệp vụ.
3. Chạy targeted tests, broader relevant suite và build; ghi tên lỗi mới/có sẵn bằng evidence.
4. Xem diff, bảo đảm không sửa unrelated changes hoặc làm yếu business guards.
5. Sau thay đổi cuối và build, restart **đúng local project server** theo AGENTS; xác định bằng command line/path/service identity/PID sở hữu, không kill mọi Node/.NET hoặc chỉ dựa vào port.
6. Xác minh PID/start time cũ được thay, health/readiness và BuildId mới; reload/rebuild frontend khi cần. Report tác vụ bị gián đoạn nếu có. Server không chạy thì start bằng local config; helper Windows chạy hidden.
7. Báo cáo theo format AGENTS: STATUS, IMPLEMENTED, KEY FILES CHANGED, VALIDATION, KNOWN LIMITATIONS/FOLLOW-UP, NEXT RECOMMENDED PHASE; kèm restart verification. Không báo ready khi restart/check thất bại.

Các lệnh hiện có để tham khảo baseline: `dotnet test --filter <TestName>`, `dotnet test --nologo`, `dotnet build src/LalabAutoReport.UI/LalabAutoReport.UI.csproj -c Debug`, `TEST.bat`, `DEV_START.bat`, `DEV_STATUS.bat`, `PACKAGE.bat`. Phase 0 kiểm tra file/lệnh thực sự tồn tại và ownership safety trước khi dùng; không giả định các restart scripts được nhắc trong tài liệu đã có sẵn. Thêm lệnh server khi project Server đã tồn tại, không gọi lệnh tưởng tượng ở giai đoạn lập kế hoạch.

## 16. Các điểm cần xác nhận trước khi code phần phụ thuộc

Không còn câu hỏi chặn việc lập kế hoạch. Các điểm dưới đây cần chốt ở Phase 0 hoặc trước phase tương ứng; mặc định dùng đề xuất trong tài liệu để chuẩn bị thiết kế, không coi là người dùng đã phê duyệt:

1. Có thêm PIN vai trò Thu ngân hay chỉ dùng Admin cho người tính bill; ai được override giá/số lượng, reopen và hủy thanh toán?
2. Số máy đồng thời, dung lượng/số đơn thực tế, Windows máy chủ, root cục bộ hay có NAS/SMB; service account và quyền quản trị cài đặt.
3. Máy trạm có cần Quick Bill từ folder ngoài dữ liệu máy chủ không? Đợt đầu chỉ hỗ trợ folder đã map vào root server.
4. Cloud và PWA đang dùng thực tế ở xưởng nào, cần giữ tương thích với những phiên bản nào; inventory trước migration.
5. Có cần giữ chế độ standalone cho xưởng khác không? Đề xuất bảo toàn bản standalone hiện có trong quá trình chuyển đổi, nhưng xưởng đã chuyển server mode phải chặn mọi local writer. Nếu tiếp tục hỗ trợ standalone lâu dài, test như deployment profile riêng dùng cùng application handlers.

Không mở rộng thành multi-server, kế toán mới hoặc offline synchronization chỉ để giải quyết các câu hỏi này.

## 17. Định nghĩa hoàn thành

Chỉ gọi **sẵn sàng triển khai mô hình xưởng nhiều máy** khi:

- Service chạy độc lập đăng nhập/UI; chỉ server quét và sở hữu DB; các client kể cả desktop server dùng cùng command API.
- Tất cả thao tác người dùng yêu cầu chạy từ client theo quyền; không có đường vòng ghi trực tiếp qua Explorer/PWA/Cloud.
- Không double bill, lost update hoặc toggle do retry; thanh toán, split và reopen có guard/audit.
- Bill snapshot, physical order provenance và integer VND giữ đúng; report đọc DB, không scan archive.
- In/clipboard/Save As diễn ra tại đúng máy trạm; service render ổn định; đã thử phần cứng áp dụng.
- Mất mạng, restart, update và restore có trạng thái rõ ràng, không báo thành công giả hoặc tự ghi lại dữ liệu cũ.
- Migration/backup/restore được diễn tập; dữ liệu cũ và Cloud identity được đối chiếu.
- Tests/build pass thực tế, local server restart/current-build verified sau coding, pilot nhiều máy đạt và giới hạn còn lại được ghi rõ.

Ưu tiên coding đầu tiên sau khi duyệt kế hoạch: **Phase 0**, tiếp theo **Phase 1 tách service + renderer spike**. Chưa triển khai client ghi đầy đủ trước khi hoàn thành Phase 2 về concurrency, idempotency và quyền.

---

Trạng thái của batch lập kế hoạch: chỉ thêm tài liệu này. Chưa chạy test/build ứng dụng vì không thay code; không restart server vì đây là documentation-only task.


---

## 18. Trạng thái thực thi các Phase

- **Phase 0:** Hoàn tất (25/10/2026) — Tài liệu kiến trúc, inventory, câu hỏi (ADR-002, LAN_PHASE0_INVENTORY.md, LAN_PHASE0_QUESTIONS.md).
- **Phase 1:** Hoàn tất (25/10/2026) — Shared DI, Server host, read-only loopback Kestrel API, renderer spike, single-instance lock.
- **Phase 2:**
  - **B1–B3 hoàn tất (Gate PASS):** Hashed PINs, stateful sessions, aggregate revisions, triggers, command pipeline, v1 order commands, --lan-writes orders default mode.
  - **B4 hoàn tất (Evidence recorded):** Load harness 10 clients, 60s bounded run, parallel scan-shaped SQLite contention, zero partials, p95 reads 6.9 ms, p95 commands 464.7 ms (bằng chứng docs/qa-evidence-phase2/B4_LOAD_REPORT.md + JSON).
- **Trạng thái hiện tại:** Phase 2 B1–B3 done / B4 evidence recorded. **Phase 3 tiếp theo** (Desktop Client Mode & LAN Pairing / Discovery).

Tài liệu Phase 0 tham chiếu:
- docs/adr/ADR-002-lan-authoritative-server.md
- docs/LAN_PHASE0_INVENTORY.md
- docs/LAN_PHASE0_QUESTIONS.md
