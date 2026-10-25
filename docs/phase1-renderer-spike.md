# Báo cáo Thử nghiệm Spike: Renderer trong Service Process (LAN Phase 1)

> **Mục tiêu:** Kiểm chứng tính khả thi của việc chạy bộ máy xuất ảnh hóa đơn (`WpfJpegBillExporter`) và nhãn in nhiệt (`WpfShippingLabelExporter`) trực tiếp bên trong tiến trình máy chủ `LalabAutoReport.Server` (chế độ headless/console host) mà không cần giao diện đồ họa tương tác (interactive UI window).
>
> **Tài liệu tham chiếu:** `docs/adr/ADR-002-lan-authoritative-server.md` (§2 D5, §3 Mục 5)
> **Ngày thực hiện:** 2026-10-25

---

## 1. Mục đích thử nghiệm (Purpose)

Trong kiến trúc máy chủ có thẩm quyền (LAN Authoritative Server), máy chủ chịu trách nhiệm tạo các tệp artifact bất biến (immutable artifacts) gồm ảnh JPEG hóa đơn và ảnh tem nhãn in nhiệt giao hàng từ snapshot hóa đơn đã khóa. Máy trạm (desktop client) hoặc thiết bị cầm tay (mobile web) chỉ việc tải artifact về để in cục bộ.

Thử nghiệm này kiểm chứng:
1. Tiến trình `LalabAutoReport.Server` có thể khởi tạo container DI, thực thi chu trình dữ liệu fail-closed, nạp hóa đơn từ SQLite và xuất thành công cả file JPEG hóa đơn lẫn tem nhãn PNG.
2. Các thành phần kết xuất đồ họa WPF (`RenderTargetBitmap`, `DrawingVisual`, `BitmapDecoder`) hoạt động ổn định trên luồng STA nền mà không phụ thuộc vào `Application.Current` hay cửa sổ WPF.
3. So sánh kích thước và mã băm pixel giải mã (decoded pixel hash) giữa kết quả xuất từ tiến trình Server với kết quả xuất in-process trên máy trạm desktop, khẳng định tính toàn vẹn và đồng nhất 100%.

---

## 2. Lệnh thực thi chi tiết (Exact Commands)

### 2.1. Lệnh Server Subcommand `render-spike`

```powershell
# Chạy trực tiếp qua binary đã build
.\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe `
  render-spike `
  --bill-id 1 `
  --out "%TEMP%\lalab_render_runtime\out" `
  --data-dir "%TEMP%\lalab_render_runtime"
```

### 2.2. Kiểm thử tự động so sánh mã băm pixel (Pixel-Hash Comparison Test)

```powershell
dotnet test tests/LalabAutoReport.Tests/LalabAutoReport.Tests.csproj --filter "ServerRendererSpikeTests" --nologo
```

---

## 3. Dữ liệu Fixture sử dụng (Bill Fixture Used)

Thử nghiệm được thực hiện trên hai nguồn fixture:

### Fixture A (Runtime Snapshot từ DB thực tế)
- **Cơ sở dữ liệu:** Bản sao độc lập từ `%LOCALAPPDATA%\LalabAutoReport\lalab_autoreport.db` vào thư mục tạm.
- **Hóa đơn ID:** `1`
- **Số hóa đơn:** `BILL-20260929-0001`
- **Khách hàng:** `Văn An`
- **Trạng thái:** `Locked`
- **Tổng tiền:** Có chiết khấu, điều chỉnh và các dòng sản phẩm in ấn.

### Fixture B (Automated Integration Test Fixture)
- **Hóa đơn:** `BILL-SPIKE-001`
- **Khách hàng:** `Khách Hàng Spike` (ID 1)
- **Thời điểm tạo:** `2026-10-25 08:30:00 +07:00`
- **Tổng tiền:** 150,000 VND
- **Dòng sản phẩm:** `Ảnh tráng gương 20x30`, số lượng 3, đơn giá 50,000 VND.

---

## 4. Kết quả JSON quan sát được (Observed JSON)

Kết quả trả về từ lệnh `render-spike --bill-id 1`:

```json
{
  "billId": 1,
  "jpegPath": "C:\\Users\\Admin\\AppData\\Local\\Temp\\lalab_render_runtime\\out\\BILL-20260929-0001_Van-An.jpg",
  "jpegSha256": "49caea17e448fdede704024ba7cd69cb59421b15958e42a9149ff1f3805adb89",
  "jpegBytes": 148361,
  "jpegWidth": 1080,
  "jpegHeight": 930,
  "labelPath": "C:\\Users\\Admin\\AppData\\Local\\Temp\\lalab_render_runtime\\out\\Label_BILL-20260929-0001_Văn_An.png",
  "labelSha256": "58950385618e4f4b7845a0a6bd142db4de3011b948c9e66247399c0133683576",
  "labelBytes": 44276,
  "labelWidth": 800,
  "labelHeight": 480,
  "processId": 66096,
  "sessionId": 1,
  "buildId": "2.0.0+739ea1ed6c8c82940bcb606247e4f50b421ea451+2.0.0"
}
```

- **Mã thoát (Exit Code):** `0`
- **Tập tin JPEG:** 148,361 bytes, kích thước 1080 x 930 px.
- **Tập tin tem nhãn:** 44,276 bytes, kích thước 800 x 480 px (khổ in nhiệt cảnh quan 40x100mm).
- **SessionId:** `1` (Console host chạy trong user session hiện hành).

---

## 5. Kết quả so sánh mã băm pixel (Pixel-Hash Comparison Result)

Trong bài test tự động `ServerRendererSpikeTests`:
- Ảnh JPEG hóa đơn do tiến trình Server tạo ra và do Desktop renderer in-process tạo ra có:
  - **Kích thước:** Trùng khớp tuyệt đối (`1080 x 930` hoặc theo khổ hóa đơn).
  - **Decoded Pixel Hash (Bgra32 raw buffer SHA-256):** Khớp tuyệt đối `serverJpgPixelHash == desktopJpgPixelHash`.
- Ảnh tem nhãn PNG do tiến trình Server tạo ra và do Desktop renderer in-process tạo ra có:
  - **Kích thước:** Trùng khớp tuyệt đối (`800 x 480`).
  - **Decoded Pixel Hash (Bgra32 raw buffer SHA-256):** Khớp tuyệt đối `serverLblPixelHash == desktopLblPixelHash`.

Điều này chứng minh việc đóng gói và thực thi pipeline render WPF trong một tiến trình console/server không làm sai lệch bất kỳ pixel nào so với máy trạm desktop.

---

## 6. Giới hạn đã ghi nhận (Known Limitations)

> [!WARNING]
> **Thử nghiệm hiện tại được thực hiện trên Console Host (Session 1+), CHƯA chạy dưới Session 0 của một Windows Service thực thụ được cài đặt qua Service Control Manager.**

- **Hạn chế của Session 0:** Trên Windows, Windows Service chạy dưới Session 0 không có giao diện người dùng tương tác và không có desktop station chuẩn. Mặc dù WPF software rendering (`WpfJpegBillExporter` dùng `RenderTargetBitmap` kết xuất phần mềm trên bộ nhớ) thường không đòi hỏi GPU, một số API font hoặc GDI/DirectWrite trong Session 0 trên các bản Windows Server/Enterprise nhất định có thể cần cấu hình Desktop heap hoặc fallback.
- **Biện pháp giảm thiểu:** Nếu sau khi cài đặt Windows Service ở Phase 5 phát hiện Session 0 giới hạn đồ họa WPF, kiến trúc cho phép cắm adapter SkiaSharp/ImageSharp hoặc gọi helper tiến trình mỏng trong phiên người dùng.

---

## 7. Các bước còn lại cho Phase 5 và Phase 6 (Remaining for Phase 5/6)

1. **Phase 5 (Windows Service Integration):**
   - Đăng ký và kiểm thử dịch vụ Windows Service chính thức (`sc.exe create LalabServer binPath=...`).
   - Kiểm tra trực tiếp lệnh xuất hóa đơn khi tiến trình thực sự chạy ở `SessionId = 0`.
   - Nếu Session 0 chạy trơn tru, cố định `WpfJpegBillExporter` làm engine kết xuất artifact mặc định trên Server.
2. **Phase 6 (Artifact Delivery API & Caching):**
   - Mở API HTTP:
     - `GET /api/v1/bills/{id}/artifact/jpeg` (trả về Content-Type: `image/jpeg`)
     - `GET /api/v1/bills/{id}/artifact/label` (trả về Content-Type: `image/png`)
   - Bổ sung bộ đệm (cache) lưu các file artifact theo `BillRevision` và `TemplateVersion` trong `%ProgramData%\LalabAutoReport\Server\artifacts\`.
   - Máy trạm tải artifact về và gọi `PrintDialog` hoặc máy in nhiệt cục bộ tại máy trạm theo D5.
