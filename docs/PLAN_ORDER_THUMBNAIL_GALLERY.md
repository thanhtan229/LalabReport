# Kế hoạch triển khai thư viện thumbnail đơn hàng

Ngày lập: 25/10/2026. Trạng thái: **ĐÃ TRIỂN KHAI code (commit 9f3bfba) — chờ nghiệm thu WPF trực quan, máy in thực tế và deploy Cloud**.

## 1. Mục tiêu và phạm vi đã duyệt

Cho phép xem thumbnail của toàn bộ ảnh in trong một đơn trên WPF PC, web điện thoại LAN và web điện thoại cloud qua 4G/5G hoặc Wi-Fi bên ngoài.

- Quy mô thường gặp: vài ảnh đến hơn 100 ảnh/đơn; dữ liệu hiện tại là JPG.
- Danh sách đơn giữ một thumbnail đại diện. Bấm ảnh hoặc nút “Xem ảnh” để mở thư viện.
- Thư viện nhóm theo sản phẩm/quy cách, giữ tỷ lệ ảnh, có tên file; không cần ảnh lớn.
- Chỉ liệt kê file trực tiếp trong thư mục in đã được xác định/chọn của từng sản phẩm. Không gom các tầng ảnh gốc/chỉnh sửa; không chọn thay người dùng khi mơ hồ.
- Thumbnail cạnh dài tối đa 320 px, không phóng to ảnh nguồn nhỏ hơn.
- Tải theo nhu cầu, lô mặc định 24 ảnh, lưu cache; không tạo trước cả đơn hay kho ảnh.
- Điện thoại bên ngoài dùng cloud hiện có; không mở cổng PC hoặc tự chuyển sang URL LAN.
- Không yêu cầu xem khi PC tắt. Cache cloud sẵn có có thể xem được, nhưng không cam kết chế độ offline; ảnh chưa có cần PC bật, app chạy và cloud sync được bật.
- Dọn tự động mặc định sau **60 ngày không sử dụng**, tùy chọn 30/60/90 ngày, có bật/tắt và thao tác dọn thủ công.
- Không sửa ảnh gốc, nghiệp vụ tính số lượng, giá, khóa đơn/hóa đơn, trạng thái giao/in hoặc quy tắc Smart Scan.

Ngoài phạm vi: ảnh độ phân giải lớn, chỉnh sửa/xóa/chọn ảnh để in, tải ảnh gốc, tìm kiếm bằng hình ảnh, thư viện toàn kho, đồng bộ toàn bộ thumbnail để xem offline, thêm hỗ trợ TIFF/PSD/RAW, viết lại hệ thống đồng bộ.

## 2. Căn cứ từ repository và các điểm phải giữ

Đã đối chiếu `docs/archive/PLAN_lalab.md` phần hiệu suất/scoped scan, `docs/archive/PLAN_lalab_V2.md` phần Effective Billing Folder, `ux_ui.md` phần mobile và `DEVELOPMENT_WORKFLOW.md`.

| Thành phần hiện có | Nhận xét và hướng sử dụng |
| --- | --- |
| `Infrastructure/Services/ThumbnailService.cs` | JPEG 320 px, chất lượng 80, cache key từ đường dẫn/kích thước/mtime, một decoder đồng thời, có pause/resume. Tận dụng và bổ sung quản lý vòng đời, không viết engine độc lập thứ hai. |
| `UI/App.xaml.cs` | Đang dọn cache 30 ngày khi khởi động. Thay bằng chính sách lưu trong settings; loại bỏ luồng dọn cũ để không có hai chính sách cạnh tranh. |
| `ThumbnailService.CleanupExpiredDiskCache` | Đang dựa vào thời gian file của Windows. Cần metadata `LastUsedAtUtc` rõ ràng để thực hiện đúng “không sử dụng”. |
| `Core/Domain/Models.cs` | Có `RootFolderId`, `OrderItemScan.Id`, `SelectedPrintFolderRelativePath`, `EffectiveBillingFolderRelativePath`. Dùng đúng root và item/snapshot; không coi ID item scan luôn ổn định qua rescan. |
| `Infrastructure/Services/CloudMediaSyncService.cs` | Hiện tải một ảnh đại diện/đơn lên R2. Gallery là luồng riêng, không nhét toàn bộ ảnh vào sync đơn/bill thông thường. |
| `Infrastructure/Services/CloudSyncService.cs` | Đã có heartbeat/pull/remote-print theo chu kỳ khoảng 15 giây, có gate đồng bộ chung. Tận dụng để nhận việc gallery, nhưng không xử lý giải mã/upload lâu bên trong gate này. |
| `UI/Web/KestrelMobileWebServer.cs` | LAN đã có xác thực và API thumbnail đại diện. Bổ sung gallery bằng cùng application service với WPF. |
| `UI/Web/MobileSpaHtmlProvider.cs` | SPA LAN riêng, phải cập nhật và kiểm thử. |
| `cloud/worker/public/index.html` | SPA cloud riêng, phải cập nhật và kiểm thử tương đương LAN. |
| `cloud/worker/src/index.ts`, `db.ts`, `types.ts` | Có auth, heartbeat, R2, queue in và các API sync. Queue gallery tách khỏi queue in và operational mutations. |
| `Infrastructure/Data/DatabaseMigrator.cs`, `cloud/worker/migrations` | Hai nơi migration phải được triển khai, nâng cấp lặp lại an toàn; không reset DB. |

Các đường dẫn trong bảng tính từ `src/LalabAutoReport.*` tương ứng, trừ `cloud/`.

Working tree đang có nhiều thay đổi chưa commit ở billing, settings, SPA LAN/cloud và tests. Khi bắt đầu coding phải chụp baseline/diff lại, bảo toàn các thay đổi đó; không checkout/reset hoặc ghi đè nguyên file theo bản cũ.

Không có yêu cầu nghiệp vụ quan trọng nào còn chặn lập kế hoạch. Các thông số hàng đợi, timeout và giới hạn bên dưới là giá trị khởi đầu để đo/tinh chỉnh, không phải cam kết tốc độ đã kiểm chứng.

## 3. Các quyết định thiết kế

### 3.1. Nguồn ảnh và tính nhất quán

1. Đọc order, root và snapshot mới nhất bằng repository, kiểm tra quyền đọc.
2. Với từng item đã resolve, lấy thư mục in được lưu; không tự chạy resolver/scanner để tìm một thư mục khác khi mở thư viện.
3. Enumerate metadata trực tiếp trong các thư mục đó, lọc JPG/JPEG không phân biệt hoa/thường, sắp xếp tự nhiên có thứ tự phụ ổn định. Không mở pixel trong bước lập danh sách.
4. Tạo manifest có phiên bản, bao gồm nhóm sản phẩm, file ID opaque, tên file, trạng thái và metadata phục vụ versioning. Không trả đường dẫn tuyệt đối ra web.
5. Phân trang theo manifest ổn định: cursor gắn với manifest version, không paginate trên một danh sách filesystem đang thay đổi từng request.
6. Chỉ tạo ảnh cho batch được yêu cầu. Không đồng nhất `gallery image count` với số lượng tính tiền.

Thư mục mơ hồ/chưa resolve: nhóm hiện “Chưa xác định thư mục in”, hướng dẫn xử lý ở luồng hiện có. Không dùng fallback đệ quy tìm ảnh đầu tiên của thumbnail đại diện cho gallery.

Folder/file biến mất, thiếu quyền, root offline: trả lỗi đúng phạm vi, các nhóm khác vẫn hoạt động. Nếu thư mục được chọn không còn hợp lệ thì yêu cầu quét lại có chủ đích; không âm thầm đổi nguồn ảnh.

Rescan/thay lựa chọn thư mục làm manifest cũ stale; mở lại hoặc “Làm mới danh sách” tạo manifest mới. Trong phiên đang mở, file đổi metadata trả trạng thái stale và làm mới batch theo version mới, không trả nhầm ảnh cũ dưới cùng ID/version.

Với đơn có hóa đơn khóa, thư viện mô tả **ảnh hiện có trên ổ đĩa tại lúc lấy danh sách**, không phải bản lưu nội dung ảnh tại lúc khóa. Hiển thị chú thích này; không sửa snapshot/tổng tiền khóa.

### 3.2. Thư viện PC và LAN

- Application service dùng chung xử lý manifest, phân trang, version và validation; ViewModel/API không chứa logic chọn thư mục.
- WPF mở cửa sổ/panel thư viện, có tên đơn, nhóm sản phẩm, tên file, tải tiếp, lỗi và retry theo nhóm/ảnh.
- Bắt đầu tối đa 24 ảnh; ưu tiên ảnh trong viewport, prefetch tối đa một lô nhỏ phía trước.
- Dùng virtualization/recycling thực sự hoặc giới hạn cửa sổ các trang đang giữ. Không dùng một `WrapPanel` chứa mọi bitmap đã tải và coi đó là virtualization.
- Đóng/đổi đơn hủy yêu cầu đang chờ, giải phóng bitmap/subscription; decoder đang thực thi có thể hoàn tất một ảnh rồi dừng, không hứa hủy tức thời giữa native decode.
- SPA LAN mở màn hình gallery thích hợp cảm ứng, 3 cột tại chiều rộng điện thoại thông thường; co giãn ở màn hình hẹp, giữ tỷ lệ bằng contain, không crop.
- `loading=lazy` là bổ trợ; cần giới hạn request/prefetch bằng viewport và hủy fetch thực sự.
- Nút xem gallery vẫn hoạt động khi ảnh đại diện lỗi/chưa có; không phụ thuộc duy nhất `hasThumbnail`.

### 3.3. Điện thoại cloud: request theo batch, PC xử lý theo nhu cầu

```text
SPA cloud -> Worker: mở gallery / yêu cầu batch chưa có
Worker -> D1: ghi job có scope và thời hạn
PC -> nhịp pull hiện có: claim job đúng thiết bị/nguồn dữ liệu
PC -> gallery worker riêng: kiểm tra manifest, tạo ảnh cần thiết
PC -> Worker -> R2: upload thumbnail có version
PC -> Worker: cập nhật tiến độ/kết quả bằng claim hợp lệ
SPA cloud -> Worker: lấy trạng thái và ảnh đã sẵn sàng
```

- Lần đầu tạo manifest cũng là job theo order; sau đó yêu cầu tối đa 24 file ID thuộc manifest đó. Không nhận tùy ý đường dẫn từ điện thoại.
- D1 giữ manifest metadata, job/claim và metadata cache. R2 chỉ giữ JPEG thumbnail; không upload ảnh gốc.
- Có thể gộp thông tin job chờ vào phản hồi heartbeat/pull hiện có theo cách tương thích ngược, thay vì tạo timer network thường trực mới. Việc claim và xử lý tách khỏi business sync gate; worker gallery có hàng đợi giới hạn riêng.
- Trả kết quả tăng dần sau mỗi ảnh hoặc nhóm nhỏ, không chờ đủ 24 ảnh mới hiện.
- Một decoder toàn app để tránh tranh chấp I/O; upload tối đa 2 đồng thời ban đầu; hàng đợi bounded, deduplicate theo source version + kích thước/codec version.
- Ưu tiên scan/verification/locking, in từ xa và đồng bộ nghiệp vụ hơn gallery. Không làm nghẽn heartbeat để cloud hiểu nhầm PC offline.
- Manifest version, dataset/device identity và root identity phải đi cùng job. Đổi database/restore/reset/root/config phải vô hiệu hóa job cũ hoặc từ chối rõ; không xử lý theo order ID trùng ở dữ liệu khác.
- Claim có lease, retry hữu hạn/backoff; batch request idempotent; worker cũ không được ack sau khi lease bị thay thế. Job hết hạn khi người dùng không còn chờ; hủy là best effort, không hủy việc dùng chung của người xem khác.
- SPA chỉ theo dõi trạng thái khi gallery đang mở và có việc pending; backoff, dừng khi đóng/chuyển trang, hết timeout hoặc tab ẩn. Khi trở lại, kiểm tra rồi resume.
- Request khởi đầu đặt timeout quan sát khoảng 60 giây, job TTL khoảng 2 phút; không tạo job mới sau mỗi lượt kiểm tra trạng thái. Retry thủ công sau timeout kiểm tra job cũ trước để tránh nhân đôi.

**Độ trễ cần công khai:** nhịp pull hiện tại khoảng 15 giây, cộng thời gian chờ sync, decode và upload; có thể gặp riêng thời gian chờ cho manifest và batch đầu. Cache nóng trả ngay qua cloud; cache lạnh không cam kết hiện ảnh tức thời. Trạng thái “Đang chờ PC / Đang chuẩn bị x/y ảnh” phải rõ ràng.

Tối ưu tiếp trong cùng thiết kế: job mở gallery có thể kèm yêu cầu batch đầu 24 ảnh để tránh hai lượt chờ; không kèm lệnh tạo toàn bộ đơn. Nếu số đo thực tế cho thấy chờ không chấp nhận được, ghi kết quả để quyết định riêng cơ chế đánh thức/push. Không đưa WebSocket/Durable Object vào nền tảng trước khi có bằng chứng cần thiết.

### 3.4. Hợp đồng API đề xuất

Tên route là đề xuất; chốt khi implementation sau khi so với conventions hiện có.

| API | Vai trò |
| --- | --- |
| `POST /api/orders/{id}/gallery/requests` | Mở/làm mới gallery; LAN có thể xử lý trực tiếp, cloud trả 202 + request ID khi cần PC. |
| `GET /api/orders/{id}/gallery?version=...&cursor=...&limit=24` | Metadata đã có: nhóm, file ID, trạng thái, version, next cursor. |
| `POST /api/orders/{id}/gallery/batches` | Yêu cầu danh sách file ID thuộc một version, có giới hạn; tạo hoặc dùng lại job. |
| `GET /api/gallery/requests/{requestId}` | Trạng thái/tiến độ/lỗi; chỉ người có quyền scope tương ứng được xem. |
| `GET /api/orders/{id}/gallery/images/{fileId}?v=...` | Trả JPEG từ cache hoặc trạng thái chưa sẵn sàng; kiểm tra quyền mỗi lần. |
| Nhóm `/api/sync/gallery/...` | PC claim/ack, gửi manifest và upload ảnh với sync secret, lease và dataset identity. |

Trạng thái job: `Pending / Claimed / Processing / Ready / Partial / Failed / Expired / Cancelled`. Trạng thái ảnh: `Pending / Ready / Missing / Unreadable / Stale / Failed`. Offline là tình trạng kết nối từ heartbeat hiện có, không gán mọi lỗi thành offline. HTTP 202/409/410/404/403/503 và error code được chuẩn hóa cho LAN/cloud, không rò rỉ exception/path nội bộ.

### 3.5. Cache và lưu trữ

Đề xuất các trách nhiệm tách biệt, tên lớp có thể điều chỉnh theo codebase:

- `IOrderGalleryService`: manifest, phân trang, truy xuất đúng phạm vi order/item/root.
- `IThumbnailService`: tạo và lấy thumbnail; nâng cấp hiện có thay vì thay toàn bộ.
- `IThumbnailCacheRepository`: metadata, last-used, state/pin, thống kê và cleanup.
- Gallery cloud coordinator/worker: claim/retry/upload, không mang quy tắc tính tiền.

SQLite bổ sung bảng metadata cache: cache key, root/source relative identity, length, last-write UTC, codec/size version, cache-relative path, byte size, created/last-used UTC, trạng thái. Index trên last-used và key; schema cụ thể chốt trước migration.

D1 bổ sung bảng manifest/file metadata, job + claim/lease/expiry, cloud media metadata và chính sách gallery cache. Không ghi image bytes vào SQLite/D1. ID/version tránh lộ tên khách/đường dẫn trong R2 object key; dùng namespace riêng, ví dụ `gallery/<dataset>/<opaque-id>/<version>.jpg`.

Giữ endpoint và dữ liệu ảnh đại diện cũ tương thích. Có thể dùng chung file cache PC nếu cùng nguồn/version, nhưng phải quản lý reference/pin; xóa gallery không được để ảnh đại diện trỏ vĩnh viễn vào object đã mất. Không dùng dictionary “đã upload trong phiên” làm bằng chứng duy nhất rằng R2 object còn tồn tại.

Decode: đo peak RAM của cách WIC/TransformedBitmap hiện có, ưu tiên giải mã JPG xuống kích thước mục tiêu bằng cơ chế decoder hỗ trợ khi cần. Không chỉ đổi kích thước sau full decode rồi kết luận RAM đã thấp. Chuẩn hóa EXIF orientation để ảnh đứng đúng chiều chỉ trong luồng tạo thumbnail; không đọc EXIF trong scan đếm. Giới hạn kích thước input/pixel bất thường, xử lý JPG lỗi theo ảnh.

Ghi cache atomically bằng temp + rename, không phục vụ file ghi dở. Metadata/cache lệch nhau sau crash được tự phục hồi. Coalesce các yêu cầu trùng; một người đóng thư viện không được hủy công việc người khác còn sử dụng.

## 4. Dọn thumbnail: chính sách và hành vi chính xác

### 4.1. Settings và giao diện

- `EnableThumbnailAutoCleanup`: mặc định true.
- `ThumbnailRetentionDays`: mặc định 60; chỉ nhận 30/60/90.
- Settings PC là nguồn chính sách; đồng bộ version của chính sách đến cloud. Cloud chưa nhận thì dùng mặc định 60 và hiển thị trạng thái áp dụng/chưa áp dụng khi có thay đổi chờ.
- “Dọn ngay”: chỉ dọn thumbnail quá hạn theo lựa chọn hiện tại, vẫn dùng được khi tắt dọn tự động.
- “Xóa toàn bộ thumbnail”: xác nhận rõ phạm vi cache PC/cloud. Cho phép chọn PC, cloud hoặc cả hai; cloud chưa kết nối thì báo chưa hoàn tất, không báo thành công chung.
- Hiển thị dung lượng cache PC; cloud hiển thị riêng khi lấy được thống kê, không cộng số không biết thành 0.
- Hiển thị thời điểm dọn thành công gần nhất và kết quả số file/byte đã xóa, lỗi còn lại.

### 4.2. Định nghĩa “sử dụng”

- Tính từ lần thumbnail được phục vụ thành công để hiển thị, không từ tuổi đơn, mtime JPG gốc hoặc lúc mở danh sách metadata.
- PC cập nhật last-used khi cung cấp ảnh cho WPF/LAN; cloud cập nhật khi phục vụ ảnh cho điện thoại. Ghi timestamp được gộp/throttle, lấy max để tránh timestamp lùi khi concurrent.
- Khi tạo mới chưa từng xem, dùng thời điểm tạo làm mốc ban đầu. Cập nhật cache-hit cũng phải touch, không chỉ cache-miss.
- GET trả 304 hợp lệ vẫn được tính là xem. Browser giữ ảnh không gọi server thì dùng touch theo batch các ảnh thực sự vào viewport; không touch toàn manifest. Không dùng public edge cache có thể bỏ qua auth/touch.
- Với ảnh PC đã upload sang cloud, cloud trả last-used aggregate cho PC qua nhịp sync để PC nhận biết ảnh còn được xem từ xa. Mỗi tầng vẫn chỉ xóa cache của mình; cache PC/cloud có thể tạm lệch do mất kết nối và sẽ tái tạo an toàn khi cần.
- Điều kiện hết hạn: `LastUsedAtUtc < UtcNow - RetentionDays`. Dùng clock inject được cho tests; không dựa vào `LastAccessTimeUtc` của Windows.

### 4.3. Lịch và tính an toàn

- PC kiểm tra khi khởi động và khi có thao tác gallery/bảo trì phù hợp; automatic sweep tối đa một lần mỗi 24 giờ, lưu mốc qua restart. Không timer quét filesystem thường trực. App mở liên tục vẫn được dọn vào lượt sử dụng gallery kế tiếp khi đã đến hạn.
- Cloud thực hiện sweep theo batch từ nhịp heartbeat PC hiện có, có lease/mốc bền vững tối đa một sweep tự động mỗi 24 giờ. Không cần thêm cron hoặc network loop trong phiên bản đầu. PC tắt thì cleanup cloud có thể hoãn, phù hợp phạm vi đã duyệt.
- Sweep chỉ đọc metadata và cache thuộc ứng dụng, không duyệt root ảnh để tìm việc dọn.
- File đang hiển thị/đọc/upload được pin/lease hoặc được recheck nguyên tử trước xóa. Cleanup và touch đồng thời không được xóa nhầm ảnh vừa được sử dụng.
- Xóa vật lý và cập nhật DB dùng trạng thái `Deleting`/retry để chịu crash; không giả định có transaction chung giữa DB và filesystem/R2.
- Xóa theo batch có giới hạn, cancellation và nhường scan/in. Lỗi từng file được ghi log và retry ở lượt sau; không nuốt exception hay làm hỏng gallery.
- Cache legacy chưa có metadata: đăng ký dần khi dùng, bảo toàn lúc nâng cấp bằng mốc migration nếu không xác định được last-used; dọn orphan theo grace period đã kiểm thử. Không xóa hàng loạt ngay lần nâng cấp.
- Chỉ dọn namespace gallery/thumbnail được quản lý. Tuyệt đối không xóa `bills/`, tem giao hàng, JPEG nguồn, backup, manifest lịch sử nghiệp vụ hoặc dữ liệu hóa đơn.
- “Xóa toàn bộ” tăng cache generation và vô hiệu hóa upload cũ để job đang chạy không lập tức tái tạo cache vừa xóa. Không phục vụ file chưa đầy đủ sau thao tác này.
- Thời hạn này là cache do app/server quản lý, không bảo đảm xóa bản ảnh người dùng đã tự tải/lưu bên ngoài ứng dụng. Không xây offline service-worker cache thumbnail trong phạm vi này; làm sạch object URL/DOM khi đóng/đăng xuất.

## 5. Bảo mật, quyền và quan sát

- Gallery kế thừa quyền xem đơn của Admin/Staff trên từng transport; settings/dọn toàn cục chỉ Admin theo quyền hiện có.
- Kiểm tra auth/scope ở manifest, status, ảnh, touch, retry và upload; không chỉ ở trang gallery. Bearer/PIN token và sync secret tách biệt.
- Resolve file ID tại PC theo manifest đã tạo. Validate canonical path trong đúng root và thư mục item; chống `..`, absolute path, URL encoding và junction/symlink thoát root. Root offline không fallback root mặc định.
- Giới hạn batch, pending job, body/upload size, TTL và retry; gộp request cùng ảnh để hai điện thoại không tạo bão job.
- R2 private, Worker kiểm tra quyền trước khi trả ảnh; không dùng bucket public hoặc nhúng sync secret vào SPA/URL. Ưu tiên fetch có auth và object URL theo cơ chế phù hợp hiện có.
- Log cấu trúc: request/job ID, order/root identity nội bộ, batch size, cache hit/miss, duration, số byte, retry, cleanup summary và lỗi phạm vi. Không log pixel, secret/token, từng file thành công ở mức thông thường.
- Đo queue delay riêng với decode/upload time để biết độ chậm đến từ nhịp pull hay xử lý ảnh.

## 6. Các bước coding và điều kiện kết thúc từng bước

### Bước 0 — Chốt baseline và hợp đồng

- Đọc lại diff hiện tại, tài liệu nghiệp vụ liên quan và scripts thực sự tồn tại; ghi baseline tests/build.
- Xác định mapping dataset/device/root, item qua rescan, quyền người dùng và semantics lỗi.
- Chốt DTO/state machine/schema với sample 1 đơn nhiều sản phẩm. Dùng version/capability để client cũ bỏ qua gallery và client mới báo “Cần cập nhật PC” nếu backend cũ.
- Không thay quy tắc V1/V2 hoặc sửa billing để phục vụ gallery.

Hoàn thành khi có API/data contract thống nhất và baseline có bằng chứng; lỗi nền được phân biệt rõ.

### Bước 1 — Manifest và cache PC có kiểm thử

- Thêm application service, repository/migration metadata và settings cleanup.
- Nâng cấp thumbnail hiện có: last-used, coalescing, bounded queue/cancel, atomic cache và cleanup an toàn.
- Lập manifest từ đúng selected folder, không decode khi liệt kê.
- Di chuyển cleanup 30 ngày cũ sang chính sách mới; kiểm tra nâng cấp dữ liệu thật bằng bản sao.

Hoàn thành khi tests scope/version/cleanup pass, không gọi scanner/billing từ gallery, cache cũ vẫn dùng được.

### Bước 2 — WPF và web LAN hoàn chỉnh

- Thêm GalleryViewModel/View và DI; nối nút mở gallery, giữ ảnh đại diện.
- Thêm API LAN và gallery SPA LAN bằng service chung; viewport load, partial failure, retry, cancel.
- Hoàn thiện settings 30/60/90, dung lượng và thao tác dọn, xác nhận xóa toàn bộ.

Hoàn thành khi PC/LAN xem được >100 JPG, cuộn không giữ tăng bitmap vô hạn, đóng cửa sổ dừng việc chờ; API từ chối truy cập ngoài phạm vi.

### Bước 3 — Cloud theo nhu cầu và dọn cloud

- Migration D1 cho manifest/job/media metadata/policy; Worker handlers + claim/lease/idempotency.
- Bổ sung nhận job vào sync đang có và gallery background worker riêng, không làm chậm lệnh in/heartbeat.
- Upload R2 có version, ack kiểm tra claim, namespace gallery và cleanup batch có checkpoint.
- SPA cloud dùng cùng hành vi hiển thị LAN; pending/progress/offline/timeout, capability fallback.
- Đồng bộ policy và last-used; thống kê/xóa cloud có kết quả riêng.

Hoàn thành khi đường đi cloud cold-cache end-to-end hoạt động, hai client không nhân đôi decode, restart/mất mạng không để queue chạy vô hạn và cleanup không chạm bill media.

### Bước 4 — Hiệu suất, hồi quy, tài liệu và áp dụng runtime

- Chạy ma trận test bên dưới; đo dữ liệu thật hoặc bản sao được phép, không sửa file nguồn.
- Tinh chỉnh batch/prefetch/decoder trên bằng chứng, không thêm framework push khi chưa cần.
- Cập nhật README/hướng dẫn, error states, phiên bản API và release notes; giữ feature flag để có thể tắt gallery mà không ảnh hưởng đơn/bill.
- Build/test xong mới restart đúng runtime local và kiểm tra API/frontend mới theo mục 9.
- Chuẩn bị kế hoạch migration/deploy cloud tương thích ngược. Không tự deploy production khi chỉ được giao coding/local test.

Hoàn thành khi checklist nghiệm thu pass; nếu chưa thử mạng ngoài hoặc production cloud thì ghi PARTIAL và nêu phần chưa kiểm chứng.

## 7. Các file dự kiến thay đổi

| Khu vực | File hiện có / file mới dự kiến |
| --- | --- |
| Core | `Domain/Models.cs`, DTO/state gallery riêng, `Interfaces/IThumbnailService.cs`, interface gallery/cache mới |
| Infrastructure | `Services/ThumbnailService.cs`, gallery/cache/coordinator services mới, `Services/CloudMediaSyncService.cs`, `Services/CloudSyncService.cs` |
| Persistence | `Data/DatabaseMigrator.cs`, `Data/SqliteSettingsRepository.cs`, repository metadata cache mới |
| Desktop | `UI/App.xaml.cs`, `UI/ViewModels/DashboardViewModel.cs`, `UI/Views/DashboardView.xaml` và code-behind, Gallery View/ViewModel mới, Settings View/ViewModel |
| LAN | `UI/Web/KestrelMobileWebServer.cs`, `UI/Web/MobileSpaHtmlProvider.cs` |
| Cloud | `cloud/worker/src/index.ts`, `db.ts`, `types.ts`, module gallery/media/jobs riêng khi cần, migration số tiếp theo còn trống, `public/index.html` |
| Tests/docs | .NET tests mới/cập nhật, Worker tests mới/cập nhật, README/hướng dẫn/release notes |

Đường dẫn Core/Infrastructure/UI bắt đầu bằng `src/LalabAutoReport.*`. Không ấn định số migration từ hiện trạng hôm nay; tính số kế tiếp tại thời điểm coding, tránh đụng thay đổi song song.

## 8. Ma trận kiểm thử và nghiệm thu

### 8.1. Automated tests bắt buộc

**Manifest/domain boundaries**

- 0/1/24/25/120+ JPG; nhiều sản phẩm; natural sort có `1, 2, 10`; JPG/JPEG hoa thường; tên tiếng Việt/ký tự đặc biệt.
- Chỉ lấy file trực tiếp ở folder đã chọn, không lấy ảnh nguồn/tầng trung gian, không tự chọn nhánh mơ hồ.
- Hai root có cùng relative path; root thiếu/offline; thư mục/file biến mất hoặc đổi quyền trong khi enumerate.
- Pagination/version không trùng/mất ảnh khi file thay đổi; rescan thay snapshot/item/folder làm manifest stale.
- Mở gallery không gọi scanner, không cập nhật counts, không thay locked bill; ba folder cùng khách vẫn là ba order.

**Thumbnail/cache/cleanup**

- Giới hạn 320 px, không upscale, giữ tỷ lệ, EXIF rotation; JPG lỗi/đang copy/ảnh kích thước lớn bất thường.
- Hot cache không decode lại; đổi length/mtime/codec version tạo cache mới; request trùng chỉ decode một lần.
- Hủy waiter không phá waiter khác; queue đầy trả trạng thái rõ; scan pause/resume vẫn hoạt động.
- Fake clock: mặc định 60, lựa chọn 30/90, cận đúng cutoff, cache hit gia hạn, timestamp UTC.
- Auto off, dọn thủ công, mốc sweep qua restart, không sweep tự động lần hai trong 24 giờ.
- Cleanup đồng thời với view/touch/upload; xóa thất bại; orphan/temp/metadata lệch; migration legacy không xóa nhầm.
- Xóa toàn bộ theo scope, cache generation chặn upload cũ; bill images/source/backup không bị ảnh hưởng.

**LAN/cloud/API**

- Unauthorized/wrong role/order/file/request ID; path traversal, junction ngoài root, root mismatch; payload/batch/rate limit.
- LAN cold/hot cache, status/error contract giống cloud ở mức UI.
- Cloud manifest + batch đầu, progressive results, hot cache, R2 404 dù metadata READY, re-upload sau cleanup.
- Duplicate requests, lease expiry/claim conflict, PC restart, mất mạng, retry hữu hạn, client đóng/tab ẩn, job hết hạn.
- PC offline, cloud sync disabled, client/backend phiên bản cũ, dataset thay sau restore/reset, nhiều client đồng thời.
- Cloud GET/304/viewport touch gia hạn đúng; policy thay đổi/tắt cleanup/clear khi cloud mất kết nối.
- Không ảnh hưởng tests đồng bộ phiên bản, remote-print recovery, bill media và xác thực hiện có.

### 8.2. Kiểm thử thủ công và hiệu suất

Fixture tối thiểu: đơn 5 ảnh, 30 ảnh, 120–150 ảnh; thêm một đơn 500 ảnh để thử giới hạn dù ngoài quy mô thường gặp. Dùng JPG có kích thước tương tự ảnh xưởng, cả portrait/landscape/album.

Chạy PC, điện thoại LAN, điện thoại mở cloud trên cùng Wi-Fi và trên 4G/Wi-Fi ngoài. Mỗi môi trường thử cache lạnh/nóng, cuộn nhanh, đóng/mở/đổi đơn, PC offline rồi trở lại, mạng chậm và một vài file lỗi.

Ghi kết quả: phần cứng/loại ổ lưu nguồn, kích thước JPG, thời gian thấy metadata/ảnh đầu/24 ảnh, queue delay/decode/upload, tổng byte tải, peak RAM/CPU và RAM sau đóng/mở lặp lại. Không đưa số ước lượng 20 KB/ảnh thành cam kết.

Tiêu chí kỹ thuật trước nghiệm thu:

- Không tạo/upload cả đơn khi chỉ xem trang đầu; mặc định tối đa 24 ảnh đầu, prefetch không quá một batch tiếp theo.
- Tối đa một decode và hai upload đồng thời ban đầu; số waiter và pending job có giới hạn kiểm thử được.
- UI vẫn phản hồi khi tải; heartbeat, in từ xa và nghiệp vụ scan/lock không bị gallery giữ gate lâu.
- Đóng gallery không còn polling riêng của gallery; sau job đang chạy hoàn tất không tiếp tục đọc ảnh nguồn. Nhịp sync hiện hữu được báo riêng, không tuyên bố app hoàn toàn không network.
- Sau nhiều lần mở/đóng không tăng RAM đơn điệu do bitmap/subscription leak; bitmap sống được giới hạn theo cửa sổ hiển thị.
- Khi không có yêu cầu, không enumerate root ảnh hoặc decode tự phát. Cleanup chỉ chạm namespace/cache metadata.
- Ngân sách thời gian cụ thể chốt từ baseline trên máy thật ở bước 0/4; báo cold/hot riêng và ghi rõ độ trễ nhịp pull. Không đánh dấu hiệu suất đạt khi chưa đo.

### 8.3. Checklist nghiệm thu sản phẩm

- [ ] PC, LAN và cloud đều mở được gallery từ đơn có/không có ảnh đại diện.
- [ ] Đủ ảnh của đúng thư mục in đã chọn, nhóm sản phẩm rõ; không trộn order/root.
- [ ] Ảnh nhỏ nhưng đủ nhận diện, không crop, có tên file; >100 ảnh cuộn/tải tiếp được.
- [ ] Điện thoại qua 4G/Wi-Fi ngoài hoạt động khi PC/app/cloud sync đang chạy.
- [ ] Pending/offline/partial/error/retry rõ và không quay chờ vô hạn.
- [ ] Dọn 60 ngày không sử dụng đúng trên PC/cloud; 30/90/tắt/dọn ngay/xóa toàn bộ hoạt động.
- [ ] JPG gốc, counts, lịch sử và locked bills giữ nguyên.
- [ ] Tests, build, local runtime restart và health/API/frontend verification có bằng chứng.

## 9. Lệnh validation và quy trình runtime khi coding

Các lệnh sau thuộc **giai đoạn triển khai sau này**, chưa chạy cho việc lập tài liệu này:

```powershell
# Từ repo root: targeted trước, sau đó suite liên quan/toàn bộ.
dotnet test tests/LalabAutoReport.Tests/LalabAutoReport.Tests.csproj --filter "FullyQualifiedName~Thumbnail|FullyQualifiedName~Gallery|FullyQualifiedName~CloudMediaSync|FullyQualifiedName~MobileWebServer"
dotnet test tests/LalabAutoReport.Tests/LalabAutoReport.Tests.csproj --nologo
dotnet build src/LalabAutoReport.UI/LalabAutoReport.UI.csproj -c Debug --nologo

# Từ cloud/worker, dùng dependencies/lockfile của repo.
npm test
npx tsc --noEmit
npm run d1:local:migrate
npm run dev
```

Không dùng `TEST.bat` một cách máy móc: script hiện có lệnh kill toàn bộ `testhost.exe`. Ưu tiên `dotnet test` trực tiếp để không ngắt test của dự án khác. README có nêu restart scripts nhưng danh sách file hiện tại không có `RESTART.bat`/`DEV_RESTART.bat`; xác minh lại trước khi dùng.

Sau mỗi batch coding hoàn chỉnh: targeted tests → broader relevant suite → build → diff review → restart local sau thay đổi cuối → verify. Không restart sau từng edit nhỏ.

Backend LAN nằm trong process WPF: nếu không hỗ trợ restart riêng, phải khởi động lại instance app đúng project. `DEV_START.bat` hiện vừa build/start vừa dừng theo tên process, nên phải xác minh executable path/command line thuộc checkout này trước khi áp dụng; không dùng nó để dừng mọi instance tên giống nhau ở nơi khác. Nếu cần, thao tác PID chính xác rồi dùng start tương đương đã đọc từ script với working directory đúng.

Helper/Worker local chạy nền bằng cửa sổ ẩn. Xác minh process cũ đã được thay, process mới bắt đầu sau build cuối và API LAN `/api/status` cùng gallery request xác thực trả code mới. `DEV_STATUS.bat` chỉ là bằng chứng phụ; thấy process chưa chứng minh backend đã nạp code mới.

Nếu sửa Worker, restart đúng Wrangler local theo command line/cwd, xác minh `/api/health` và gallery API; refresh/reload cả SPA LAN lẫn cloud local để tránh HTML/service-worker cache cũ. Có build ID thì đối chiếu; nếu chưa có dùng thời điểm build/process và API mới. Không báo “sẵn sàng” khi restart/API verification thất bại. Báo mọi tác vụ biết đã bị ngắt bởi restart.

Cloud production là bước phát hành riêng: migration tương thích trước, backend capability trước client, kiểm tra auth/API và có rollback ứng dụng an toàn. Không rollback bằng xóa DB/bucket hoặc reset schema. Chỉ code/local test chưa đủ để tuyên bố tính năng đã chạy trên domain thật.

## 10. Rủi ro và cách giảm thiểu

| Rủi ro | Biện pháp |
| --- | --- |
| Lần mở cloud lạnh chờ lâu do nhịp 15 giây | Gộp manifest + batch đầu, progress, đo queue delay; quyết định push sau nếu có bằng chứng. |
| JPG gốc lớn dù thumbnail nhỏ | Một decode, downsample phù hợp, đo peak RAM, giới hạn input, lỗi theo ảnh. |
| Cloud sync/in bị chậm do gallery | Claim nhẹ trong nhịp hiện có, xử lý ngoài gate, queue/ưu tiên riêng. |
| Xem nhầm ảnh sau rescan/restore hoặc đa root | Manifest/dataset/root/source version và revalidation trước decode/upload. |
| Dọn nhầm ảnh đang dùng hoặc ảnh bill | Metadata UTC, touch/pin/lease, generation, namespace tách, tests cạnh tranh và whitelist. |
| Cache cloud báo READY nhưng object đã mất | Đối chiếu 404, mark missing, yêu cầu PC tạo/upload lại; không tin cache in-memory đơn thuần. |
| LAN/cloud UI lệch nhau | Một API contract + ma trận acceptance chung, test cả hai SPA. |
| Ghi đè thay đổi đang làm khác | Chụp baseline, chỉnh cục bộ theo diff hiện tại, review từng file; không reset. |

## 11. Báo cáo hoàn thành từng batch

Theo format AGENTS: `STATUS`, `IMPLEMENTED`, `KEY FILES CHANGED`, `VALIDATION` (tests/build/restart và API), `KNOWN LIMITATIONS / FOLLOW-UP`, `NEXT RECOMMENDED PHASE`.

Không gọi hoàn thành nếu chỉ có lưới UI, thiếu luồng cloud theo yêu cầu, cleanup theo last-used chưa đúng, thiếu migration hoặc chưa kiểm chứng runtime. Không báo test/build pass nếu chưa thực chạy.

**Bước coding đầu tiên được khuyến nghị:** bước 0 rồi bước 1 (manifest + cache/cleanup có tests), sau đó hoàn thiện PC/LAN trước khi nối cloud. Việc lập kế hoạch này không cấp lệnh bắt đầu coding hay deploy.


---

## 12. Ghi nhận triển khai (25/10/2026)

- Code: commit `9f3bfba` trên `master` (chưa push, chưa deploy Cloud).
- Đã có: manifest/cache PC, WPF gallery, LAN API + SPA, Cloud job queue + R2 + D1, retention 30/60/90 (local + cloud), tests.
- Khác kế hoạch ban đầu (có lý do):
  - WPF dùng **cửa sổ bitmap theo viewport** (bounded bitmap window, 2 decode worker, prefetch 1 màn hình tối đa 12, unload ngoài 2 màn hình) thay vì virtualization WPF, vì layout nhóm + WrapPanel không virtualize được nếu không viết panel tùy biến; cách này giới hạn RAM chặt hơn "giữ mọi bitmap".
  - Cloud gallery **không defer** vì code đã nằm trong working tree; chỉ freeze mở rộng cloud cho tới sau LAN Phase 1 (re-host `IOrderGalleryService`).
- Bằng chứng QA: `docs/qa-evidence-gallery/QA_REPORT.md` (QA PASS; WPF trực quan = NOT VERIFIED).
- Review: vòng 1 NOT LGTM (4 blocking: version collision cloud, manifest skew PC↔cloud, WPF bitmap leak, cloud SPA self-heal) → đã sửa + test hồi quy → vòng 2 **LGTM**.
- Validation cuối: .NET 640 passed, worker 94 passed, `tsc --noEmit` sạch, build Debug 0 warning/0 error.
- Còn lại trước khi phát hành: nghiệm thu WPF bằng mắt, in tem trên máy in thật, deploy D1 migration 0007 + Worker + cập nhật client theo thứ tự (migration → worker → PC).
