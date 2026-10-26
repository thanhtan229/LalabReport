# Lalab Auto Report

Ứng dụng Windows Desktop (.NET 10 LTS, WPF, SQLite WAL) dành cho xưởng in ảnh chuyên nghiệp. Ứng dụng tự động hóa quy trình rà soát đơn hàng, đối soát số lượng file ảnh in, tính tiền và lập báo cáo tài chính/sản lượng từ cấu trúc thư mục thực tế của xưởng mà không can thiệp hay thay đổi file của khách hàng.

---

## 1. Nguyên Tắc Cốt Lõi V2 (Core Principles V2)

1. **Tính chính xác của hóa đơn đặt lên hàng đầu**: Không bao giờ tự động suy đoán khi cấu trúc thư mục mơ hồ.
2. **Bảo toàn nguồn gốc đơn hàng (Provenance)**: Mỗi thư mục khách hàng vật lý dưới một ngày là **một Đơn hàng riêng biệt**. Nếu một khách hàng có nhiều thư mục con trong ngày (`Văn An`, `Anh An`, `A.An`), cả 3 vẫn là 3 Đơn hàng riêng biệt, chỉ được tổng hợp định danh trên báo cáo.
3. **Thư mục in cuối cùng là nguồn số lượng duy nhất (V2 Rule)**: Số lượng tính tiền cho mọi sản phẩm được lấy trực tiếp từ thư mục in cuối cùng (Final Print Folder). Không đối soát số lượng gốc (Source), không cảnh báo lệch gốc/in. 1 file ảnh in = 1 bản in ảnh hoặc 1 tờ album (không trừ file bìa).
4. **Hỗ trợ đa dạng danh mục sản phẩm**: Hỗ trợ Ảnh in (`PhotoPrint` tính theo số file in) và Album (`Album` tính theo giá gói chuẩn + tờ phát sinh thêm).
5. **Smart Scan thủ công - Không quét ngầm liên tục**: Ứng dụng ở trạng thái tĩnh hoàn toàn (0% CPU/Disk) khi không có thao tác quét của người dùng. Mở báo cáo tháng đọc trực tiếp từ SQLite chứ không quét lại toàn bộ cây thư mục ảnh.
6. **Lịch sử bất biến khi đã Khóa**: Hóa đơn sau khi khóa trở thành một bản snapshot lịch sử. Quét lại thư mục sau này nếu có thay đổi file trên ổ cứng sẽ cảnh báo `FilesystemChangedAfterLock` chứ không âm thầm làm sai lệch số tiền đã khóa.
7. **Tiền tệ VND là số nguyên**: Toàn bộ số tiền và đơn giá được lưu trữ và tính toán dưới dạng số nguyên (long), tuyệt đối không dùng số thực dấu phẩy động.

---

## 2. Quy Ước Cấu Trúc Thư Mục V2 (Folder Structure V2)

```text
RootFolder/
└── 2026-09-29/                         <-- Thư mục ngày (YYYY-MM-DD)
    ├── Văn An/                         <-- Khách hàng A (Legacy: sản phẩm trực tiếp)
    │   ├── 13x18 in/                   <-- Sản phẩm ảnh in
    │   │   ├── photo01.jpg             <-- Nếu không có thư mục con, tính trực tiếp
    │   │   └── in/                     <-- Hoặc thư mục lá sâu nhất chứa ảnh
    │   │       ├── p01.jpg
    │   │       └── p02.jpg
    │   └── Album 20x20/                <-- Sản phẩm Album (1 thư mục = 1 album)
    │       └── in/                     <-- 12 file ảnh = 12 tờ (10 tờ chuẩn + 2 tờ thêm)
    │           ├── sheet_01.jpg
    │           └── ...
    └── Anh An/                         <-- Khách hàng B (Explicit: có các đơn con)
        ├── Don 01/                     <-- Đơn hàng 1
        │   └── 13x18 in/
        └── Don 02/                     <-- Đơn hàng 2
            └── Album 25x25/
```

- **Thư mục in cuối cùng (Final Print Folder)**: Thư mục lá sâu nhất chứa ảnh in hợp lệ, hoặc chính thư mục sản phẩm nếu nó chứa file ảnh trực tiếp và không có thư mục con chứa ảnh.
- **AmbiguousPrintFolder**: Nếu phân nhánh tạo ra nhiều thư mục lá chứa ảnh (`edit-a`, `edit-b`), ứng dụng gắn cờ yêu cầu người dùng chọn thư mục in chính thức.
- **Tính tiền V2**: Toàn bộ số lượng tính tiền được lấy từ thư mục in cuối cùng. PhotoPrint tính theo số file x đơn giá; Album tính theo gói chuẩn + số tờ vượt. Không so sánh với file gốc.

---

## 3. Kiến Trúc Ứng Dụng (.NET 10 Clean Architecture)

```text
src/
├── LalabAutoReport.Core/            # Domain models, Enums, Interfaces, và Business Services
│   ├── Domain/                     # Models (Order, Bill, Customer, PrintSpecification, v.v.)
│   ├── Interfaces/                 # Hợp đồng trừu tượng (Scanner, Billing, Locking, v.v.)
│   └── Services/                   # Logic nghiệp vụ độc lập, kiểm thử 100% tự động
├── LalabAutoReport.Infrastructure/  # Triển khai tầng hạ tầng
│   ├── Data/                       # SQLite WAL mode, Dapper, Migrations, BackupService
│   ├── FileSystem/                 # PhysicalFileSystemAdapter (chống crash truy cập, path dài)
│   └── Logging/                    # Serilog rolling file logger
└── LalabAutoReport.UI/              # Giao diện WPF MVVM (CommunityToolkit.Mvvm)
    ├── ViewModels/                 # Dashboard, Reports, Customers, PriceList, Settings
    └── Views/                      # XAML Controls, bảng kê, modal đối soát, xuất bản
tests/
└── LalabAutoReport.Tests/          # 52 Automated Unit, Integration & Hardening Tests
```

---

## 4. Hướng Dẫn Vận Hành & Tính Năng

### 4.1 Cài Đặt Ban Đầu (Settings)
1. Mở tab **Cài đặt**.
2. Chọn **Thư mục gốc chứa ảnh (Root Folder)** (ví dụ: `D:\WORK_2026\ORDERS`).
3. Khai báo danh sách đuôi ảnh hỗ trợ (mặc định: `.jpg, .jpeg, .png, .tif, .tiff, .psd, .bmp, .webp`).
4. Nhấn **Lưu Cài Đặt**.

### 4.2 Bảng Giá Quy Cách In (Price List)
1. Mở tab **Bảng giá**.
2. Nhập các quy cách in chuẩn của xưởng (ví dụ: `13x18 in`, `20x30 ép gỗ`, `Album 30x30`) và đơn giá tương ứng (VND).
3. Thêm các biệt danh (Aliases) thường gặp trong tên thư mục thợ đặt (ví dụ: `13x18`, `13x18in`, `13-18`).

### 4.3 Quản Lý Khách Hàng (Customers & Aliases)
1. Mở tab **Khách hàng**.
2. Thêm khách hàng chính thức và số điện thoại.
3. Khi quét thư mục nếu gặp tên mới, ứng dụng sẽ đề xuất gợi ý nhưng **không bao giờ tự ý gộp khách hàng**. Người dùng xác nhận lưu alias để tự động nhận diện vào các lần quét sau.

### 4.4 Quét & Kiểm Tra Đơn Hàng Hàng Ngày (Dashboard)
1. Chọn ngày cần làm việc (ví dụ: `2026-09-28`).
2. Nhấn **⚡ Quét Ngày**.
3. Xem danh sách các đơn hàng theo khách hàng.
4. Đối với các đơn hàng có cảnh báo:
   - **Lệch số lượng**: Nhấp để chọn `Dùng số in`, `Dùng số gốc`, hoặc `Nhập số lượng khác`.
   - **Thư mục in mơ hồ**: Nhấp chọn thư mục in chính xác trong danh sách thư mục lá được tìm thấy.
5. Sau khi kiểm tra xong:
   - Nhấn **Khóa đơn**: Hệ thống thực hiện một lượt **Quét & Xác Thực tươi (Scan & Verify Before Lock)** ngay trên ổ đĩa. Nếu không có lỗi, hóa đơn được chốt vĩnh viễn vào SQLite.

### 4.5 Báo Cáo Tài Chính & Sản Lượng (Reports)
1. Mở tab **Báo cáo**.
2. Hỗ trợ xem theo:
   - **Báo cáo Tháng**: Xem doanh thu, tổng số lượng in, biểu đồ bảng theo từng ngày và từng khách hàng.
   - **Báo cáo Ngày**: Chi tiết từng đơn và từng khách hàng trong ngày.
   - **Khoảng Ngày (Tùy chỉnh)**: Lọc theo kỳ thanh toán bất kỳ.
3. **Phát hiện ngày chưa quét (Missing Days)**:
   - Báo cáo tháng tự động kiểm tra danh sách thư mục ngày trên ổ cứng so với database.
   - Nếu có ngày chưa quét, nút **⚡ Quét các ngày còn thiếu** cho phép quét bù nhanh chóng mà không cần quét lại cả tháng.

### 4.6 Sao Lưu & Phục Hồi Cơ Sở Dữ Liệu (Backup & Restore)
- **Vị trí file dữ liệu**: `%LocalAppData%\LalabAutoReport\lalab_autoreport.db`.
- **Tự động sao lưu trước migration**: Mỗi khi hệ thống cập nhật cấu trúc database, một bản sao lưu timestamped được tự động tạo.
- **Sao lưu thủ công**: Vào tab **Cài đặt** -> bấm **⚡ Sao Lưu Ngay**. File sao lưu được lưu tại `%LocalAppData%\LalabAutoReport\backups\`. Hệ thống tự động duy trì 20 bản sao lưu gần nhất.
- **Khôi phục**: Bấm **📂 Khôi Phục Từ File...** hoặc nút **Khôi phục** ở danh sách bản sao lưu. Trước khi ghi đè, hệ thống luôn tự động tạo thêm một bản pre-restore backup để đảm bảo an toàn tuyệt đối.

---

## 5. Quy Trình Phát Triển & Các Script Tự Động (Development Workflow)

Theo quy định tại `DEVELOPMENT_WORKFLOW.md`:
* **Vòng lặp nhanh**: `Understand → Code → Validate → Build when needed → Restart → Health Check → Ready for User Test`.
* **Không package `.exe` sau mỗi lần code**: Trong quá trình phát triển, chỉ build Debug và chạy từ source để vòng lặp test diễn ra trong 1-2 giây. Chỉ đóng gói `.exe` khi cần phát hành (release).

Dự án cung cấp sẵn các script Windows tiện lợi:

| Script | Chức năng | Mô tả chi tiết |
|:---|:---|:---|
| **`RESTART.bat`** / **`DEV_RESTART.bat`** | **Khởi động lại nhanh** | Dừng instance cũ đang chạy, build Debug, khởi động lại app và tự động Health Check để sẵn sàng kiểm thử. |
| **`DEV_START.bat`** | **Bắt đầu làm việc** | Kiểm tra trùng lặp instance, build và mở ứng dụng. |
| **`DEV_STOP.bat`** | **Dừng ứng dụng** | Dừng sạch sẽ toàn bộ tiến trình app/dotnet dev đang chạy để giải phóng tài nguyên. |
| **`DEV_STATUS.bat`** | **Kiểm tra trạng thái** | Health check hiển thị PID, RAM, thời gian chạy, database SQLite và log mới nhất. |
| **`TEST.bat`** | **Chạy kiểm thử** | Chạy toàn bộ 52 test cases tự động và báo cáo kết quả tức thì. |
| **`PACKAGE.bat`** | **Đóng gói phát hành** | Tự động chạy test và đóng gói ứng dụng thành file exe độc lập (Single-File Release). |
| **`tao_thu_muc_test_mau.ps1`** | **Tạo dữ liệu test** | Tự động sinh trọn bộ cây thư mục mẫu cho 17 kịch bản kiểm thử thực hành. |

> 📖 **Xem hướng dẫn chi tiết kiểm thử thực hành:** Đọc file [HUONG_DAN_THUC_HANH_TEST_APP.md](HUONG_DAN_THUC_HANH_TEST_APP.md) để thực hành từng bước từ Happy Path, Nhận diện Alias, Album V2, Thư mục mơ hồ, Khóa đơn bất biến, đến Báo cáo.

---

## 6. Hướng Dẫn Biên Dịch & Đóng Gói Phát Hành (Release Packaging)

### Yêu Cầu Môi Trường
- Windows 10/11 x64
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Chạy Kiểm Thử Tự Động (Automated Tests)
Toàn bộ 52 test cases (từ bộ nhận diện thư mục, giải quyết xung đột alias, tính giá, khóa đơn, báo cáo database-only, đến kiểm thử chịu lỗi và backup):

```powershell
dotnet test
# Hoặc nhấp đúp file TEST.bat
```

### Đóng Gói Bản Thực Thi Độc Lập (Self-Contained Single File)
Chỉ thực hiện khi chuẩn bị phát hành phiên bản mới (Release). Chạy lệnh:

```powershell
dotnet publish src/LalabAutoReport.UI/LalabAutoReport.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

File thực thi sau khi hoàn tất:
```text
src/LalabAutoReport.UI/bin/Release/net10.0-windows/win-x64/publish/LalabAutoReport.UI.exe
```
Người dùng chỉ cần copy file `LalabAutoReport.UI.exe` vào máy tính là có thể sử dụng ngay lập tức.
