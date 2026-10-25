using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.Reporting;
using Xunit;

namespace LalabAutoReport.Tests;

public class ShippingLabelTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly DatabaseMigrator _migrator;
    private readonly SqliteCustomerRepository _customerRepo;
    private readonly SqliteCustomerBillRepository _customerBillRepo;
    private readonly SqliteSettingsRepository _settingsRepo;

    public ShippingLabelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Lalab_ShippingTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _dbPath = Path.Combine(_tempRoot, "test_shipping.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _migrator = new DatabaseMigrator(_connectionFactory);
        _migrator.MigrateAsync().GetAwaiter().GetResult();

        _customerRepo = new SqliteCustomerRepository(_connectionFactory);
        _customerBillRepo = new SqliteCustomerBillRepository(_connectionFactory);
        _settingsRepo = new SqliteSettingsRepository(_connectionFactory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task CustomerAddress_CanBeSavedAndRetrieved_InCustomerRepository()
    {
        // 1. Create customer with address
        var customer = new Customer
        {
            CanonicalName = "Studio Tuấn Hải",
            Phone = "0988776655",
            Address = "Số 123 Phố Huế, Hai Bà Trưng, Hà Nội",
            Note = "Giao giờ hành chính"
        };

        var created = await _customerRepo.CreateCustomerAsync(customer);
        created.Address.Should().Be("Số 123 Phố Huế, Hai Bà Trưng, Hà Nội");

        // 2. Fetch from DB
        var fetched = await _customerRepo.GetByIdAsync(created.Id);
        fetched.Should().NotBeNull();
        fetched!.Address.Should().Be("Số 123 Phố Huế, Hai Bà Trưng, Hà Nội");

        // 3. Update customer address
        fetched.Address = "Tòa Discovery Complex, 302 Cầu Giấy, Hà Nội";
        await _customerRepo.UpdateCustomerAsync(fetched);

        var updated = await _customerRepo.GetByIdAsync(created.Id);
        updated!.Address.Should().Be("Tòa Discovery Complex, 302 Cầu Giấy, Hà Nội");
    }

    [Fact]
    public async Task ShippingAddressSnapshot_CanBeSavedAndRetrieved_InCustomerBillRepository()
    {
        var bill = new CustomerBill
        {
            BillNumber = "BILL-20261001-9999",
            CustomerNameSnapshot = "Studio Kim Long",
            PhoneSnapshot = "0912345678",
            ShippingAddressSnapshot = "Số 88 Lê Duẩn, Hoàn Kiếm, Hà Nội",
            PeriodStart = "2026-10-01",
            PeriodEnd = "2026-10-01",
            Status = CustomerBillStatus.Draft,
            ProductSubtotal = 500000,
            GrandTotal = 500000,
            IsPaid = false
        };

        await _customerBillRepo.SaveBillAsync(bill);
        bill.Id.Should().BeGreaterThan(0);

        var fetched = await _customerBillRepo.GetByIdAsync(bill.Id);
        fetched.Should().NotBeNull();
        fetched!.ShippingAddressSnapshot.Should().Be("Số 88 Lê Duẩn, Hoàn Kiếm, Hà Nội");
    }

    [Fact]
    public async Task WorkshopSettings_CanBeSavedAndRetrieved_InSettingsRepository()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.WorkshopName = "XƯỞNG IN ẢNH NGHỆ THUẬT LALAB PRO";
        settings.WorkshopPhone = "0909.123.456";
        settings.WorkshopAddress = "Số 10 Tràng Thi, Hoàn Kiếm, Hà Nội";

        await _settingsRepo.SaveSettingsAsync(settings);

        var loaded = await _settingsRepo.GetSettingsAsync();
        loaded.WorkshopName.Should().Be("XƯỞNG IN ẢNH NGHỆ THUẬT LALAB PRO");
        loaded.WorkshopPhone.Should().Be("0909.123.456");
        loaded.WorkshopAddress.Should().Be("Số 10 Tràng Thi, Hoàn Kiếm, Hà Nội");
    }

    [Fact]
    public async Task WpfShippingLabelExporter_ExportShippingLabelImage_CreatesValidImageFile()
    {
        var bill = new CustomerBill
        {
            BillNumber = "BILL-20261001-0088",
            CustomerNameSnapshot = "Studio Mai Vàng",
            PhoneSnapshot = "0987654321",
            ShippingAddressSnapshot = "Số 25 Lạc Long Quân, Tây Hồ, Hà Nội",
            PeriodStart = "2026-10-01",
            PeriodEnd = "2026-10-01",
            Status = CustomerBillStatus.Locked,
            ProductSubtotal = 320000,
            GrandTotal = 320000,
            IsPaid = false,
            Note = "Hàng dễ vỡ, xin nhẹ tay"
        };

        var order = new CustomerBillOrder
        {
            OrderId = 1,
            OrderNameSnapshot = "Bộ ảnh cưới 60x90",
            Subtotal = 320000,
            IsIncluded = true
        };

        order.Lines.Add(new CustomerBillLine
        {
            ProductNameSnapshot = "Ảnh ép gỗ tráng gương",
            VariantSnapshot = "60x90",
            BilledQuantity = 1,
            BilledUnitPrice = 250000,
            LineTotal = 250000,
            IsIncluded = true
        });

        order.Lines.Add(new CustomerBillLine
        {
            ProductNameSnapshot = "Ảnh để bàn",
            VariantSnapshot = "15x21",
            BilledQuantity = 2,
            BilledUnitPrice = 35000,
            LineTotal = 70000,
            IsIncluded = true
        });

        bill.Orders.Add(order);

        var exporter = new WpfShippingLabelExporter(_settingsRepo);
        string exportDir = Path.Combine(_tempRoot, "Labels");

        string imagePath = await exporter.ExportShippingLabelImageAsync(bill, exportDir);

        File.Exists(imagePath).Should().BeTrue();
        new FileInfo(imagePath).Length.Should().BeGreaterThan(1000);
        Path.GetExtension(imagePath).Should().Be(".png");
    }

    [Fact]
    public void WpfShippingLabelExporter_PaidAndUnpaidBills_RenderDrawingVisuals()
    {
        var settings = new AppSettings
        {
            WorkshopName = "LALAB TEST WORKSHOP",
            WorkshopPhone = "0911223344",
            EnableVietQrOnBill = true,
            BankAccountNumber = "123456789"
        };

        var billUnpaid = new CustomerBill
        {
            BillNumber = "BILL-UNPAID-01",
            CustomerNameSnapshot = "Khách Chưa Trả Tiền",
            GrandTotal = 150000,
            IsPaid = false
        };

        var billPaid = new CustomerBill
        {
            BillNumber = "BILL-PAID-01",
            CustomerNameSnapshot = "Khách Đã Chuyển Khoản",
            GrandTotal = 150000,
            IsPaid = true
        };

        // Render unpaid visual
        var visualUnpaid = WpfShippingLabelExporter.CreateLabelVisual(billUnpaid, settings);
        visualUnpaid.Should().NotBeNull();

        // Render paid visual
        var visualPaid = WpfShippingLabelExporter.CreateLabelVisual(billPaid, settings);
        visualPaid.Should().NotBeNull();
    }

    [Fact]
    public async Task SettingsRepository_ThermalPrinterAndAutoDeliverySettings_CanBeSavedAndRetrieved()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.ThermalPrinterName = "Xprinter XP-350B (75x100mm)";
        settings.AutoMarkDeliveredOnPrint = true;

        await _settingsRepo.SaveSettingsAsync(settings);

        var loaded = await _settingsRepo.GetSettingsAsync();
        loaded.ThermalPrinterName.Should().Be("Xprinter XP-350B (75x100mm)");
        loaded.AutoMarkDeliveredOnPrint.Should().BeTrue();

        // Test updating to false / different printer
        loaded.ThermalPrinterName = "HPRT N41";
        loaded.AutoMarkDeliveredOnPrint = false;
        await _settingsRepo.SaveSettingsAsync(loaded);

        var loaded2 = await _settingsRepo.GetSettingsAsync();
        loaded2.ThermalPrinterName.Should().Be("HPRT N41");
        loaded2.AutoMarkDeliveredOnPrint.Should().BeFalse();
    }

    [Fact]
    public void WpfShippingLabelExporter_CreateOrderLabelVisual_And_TestSample_RenderValidVisuals()
    {
        var settings = new AppSettings
        {
            WorkshopName = "XƯỞNG IN ẢNH LALAB",
            WorkshopPhone = "0988.111.222",
            WorkshopAddress = "123 Đường Láng, Đống Đa, Hà Nội",
            EnableVietQrOnBill = true,
            BankAccountNumber = "99998888",
            BankBinOrCode = "970422"
        };

        var order = new Order
        {
            Id = 55,
            WorkDate = "2026-10-01",
            OrderCode = "ORD-20261001-0055",
            OriginalFolderName = "Studio Hoàng Phúc",
            Customer = new Customer
            {
                CanonicalName = "Studio Hoàng Phúc",
                Phone = "0933.444.555",
                Address = "456 Giải Phóng, Thanh Xuân, Hà Nội"
            },
            Note = "Giao buổi sáng, gọi trước 15 phút",
            Items = new System.Collections.Generic.List<OrderItemScan>
            {
                new OrderItemScan
                {
                    SpecificationFolderName = "13x18 Go",
                    PrintCount = 20,
                    BillQuantity = 20
                },
                new OrderItemScan
                {
                    SpecificationFolderName = "60x90 Cong",
                    PrintCount = 1,
                    BillQuantity = 1
                }
            }
        };

        // 1. Render order visual
        var orderVisual = WpfShippingLabelExporter.CreateOrderLabelVisual(order, settings);
        orderVisual.Should().NotBeNull();

        // 2. Render test sample label visual
        var sampleVisual = WpfShippingLabelExporter.CreateTestSampleLabelVisual(settings, "Máy In Mẫu");
        sampleVisual.Should().NotBeNull();
    }

    [Fact]
    public async Task WpfShippingLabelExporter_ExportOrderShippingLabelImage_CreatesValidImageFile()
    {
        var order = new Order
        {
            Id = 66,
            WorkDate = "2026-10-01",
            OrderCode = "ORD-20261001-0066",
            OriginalFolderName = "Studio Ánh Dương",
            Customer = new Customer
            {
                CanonicalName = "Studio Ánh Dương",
                Phone = "0977.888.999",
                Address = "789 Hoàng Hoa Thám, Ba Đình, Hà Nội"
            },
            Note = "Đóng gói cẩn thận chống nước"
        };

        var exporter = new WpfShippingLabelExporter(_settingsRepo);
        string exportDir = Path.Combine(_tempRoot, "OrderLabels");

        string imagePath = await exporter.ExportOrderShippingLabelImageAsync(order, exportDir);

        File.Exists(imagePath).Should().BeTrue();
        new FileInfo(imagePath).Length.Should().BeGreaterThan(1000);
        Path.GetExtension(imagePath).Should().Be(".png");
    }

    [Fact]
    public async Task ThermalLabelSize_CanBeSavedAndRetrieved_WithDefault40x100()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.ThermalLabelSize.Should().Be(ThermalLabelSizes.Size40x100);

        settings.ThermalLabelSize = ThermalLabelSizes.Size75x100;
        await _settingsRepo.SaveSettingsAsync(settings);

        var loaded = await _settingsRepo.GetSettingsAsync();
        loaded.ThermalLabelSize.Should().Be(ThermalLabelSizes.Size75x100);
    }

    [Fact]
    public async Task WpfShippingLabelExporter_ExportsBoth40x100And75x100_WithExactDimensions()
    {
        var bill = new CustomerBill
        {
            BillNumber = "BILL-DIM-TEST",
            CustomerNameSnapshot = "Studio Test Dim",
            PhoneSnapshot = "0900000000",
            ShippingAddressSnapshot = "Hà Nội",
            GrandTotal = 200000,
            IsPaid = false
        };

        var exporter = new WpfShippingLabelExporter(_settingsRepo);
        string exportDir = Path.Combine(_tempRoot, "DimTest");

        // 1. Export 40x100mm (Default)
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.ThermalLabelSize = ThermalLabelSizes.Size40x100;
        await _settingsRepo.SaveSettingsAsync(settings);

        string path40x100 = await exporter.ExportShippingLabelImageAsync(bill, exportDir);
        File.Exists(path40x100).Should().BeTrue();

        using (var fs = new FileStream(path40x100, FileMode.Open, FileAccess.Read))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            frame.PixelWidth.Should().Be(800);
            frame.PixelHeight.Should().Be(480);
        }

        // 2. Export 75x100mm
        settings.ThermalLabelSize = ThermalLabelSizes.Size75x100;
        await _settingsRepo.SaveSettingsAsync(settings);

        string path75x100 = await exporter.ExportShippingLabelImageAsync(bill, exportDir);
        File.Exists(path75x100).Should().BeTrue();

        using (var fs = new FileStream(path75x100, FileMode.Open, FileAccess.Read))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            frame.PixelWidth.Should().Be(600);
            frame.PixelHeight.Should().Be(800);
        }
    }

    [Fact]
    public async Task WpfShippingLabelExporter_LongDataAndNoAddress_RendersSafelyWithoutOverflow()
    {
        // Test edge cases: empty address (no duplicate "Địa chỉ: Địa chỉ:"), very long name, 8 items, long note
        var bill = new CustomerBill
        {
            BillNumber = "BILL-LONG-TEST-9999",
            CustomerNameSnapshot = "Studio Tiệc Cưới và Sự Kiện Nghệ Thuật Hoàng Gia Luxury VIP Hà Nội Sài Gòn",
            PhoneSnapshot = "0988.777.666",
            ShippingAddressSnapshot = null, // empty address test
            GrandTotal = 158500000, // 158.5M COD to test large amount auto-shrinking font
            IsPaid = false,
            Note = "Lưu ý đặc biệt quan trọng: Hàng tranh tráng gương kích thước lớn rất dễ vỡ. Yêu cầu bọc 3 lớp xốp khí cẩn thận, giao buổi sáng trước 11h30 và gọi điện cho quản lý studio trước khi tới 30 phút."
        };

        var order = new CustomerBillOrder
        {
            OrderId = 1,
            OrderNameSnapshot = "Đơn in triển lãm ảnh cưới",
            IsIncluded = true
        };

        for (int i = 1; i <= 8; i++)
        {
            order.Lines.Add(new CustomerBillLine
            {
                ProductNameSnapshot = $"Ảnh cổng tráng gương pha lê cao cấp mẫu số {i}",
                VariantSnapshot = $"{50 + i * 5}x{70 + i * 5}",
                BilledQuantity = i,
                BilledUnitPrice = 150000,
                LineTotal = i * 150000,
                IsIncluded = true
            });
        }
        bill.Orders.Add(order);

        var settings = await _settingsRepo.GetSettingsAsync();
        settings.ThermalLabelSize = ThermalLabelSizes.Size40x100;
        settings.WorkshopName = "XƯỞNG IN TRANH ẢNH NGHỆ THUẬT CAO CẤP CHUYÊN NGHIỆP LALAB VIỆT NAM";
        settings.WorkshopPhone = "0909.123.456";
        settings.WorkshopAddress = "Tòa Nhà Công Nghệ Số 123 Đường Cầu Giấy, Phường Quan Hoa, Quận Cầu Giấy, Hà Nội";
        await _settingsRepo.SaveSettingsAsync(settings);

        var exporter = new WpfShippingLabelExporter(_settingsRepo);
        string exportDir = Path.Combine(_tempRoot, "StressTest");

        string labelPath = await exporter.ExportShippingLabelImageAsync(bill, exportDir);
        File.Exists(labelPath).Should().BeTrue();

        using (var fs = new FileStream(labelPath, FileMode.Open, FileAccess.Read))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            frame.PixelWidth.Should().Be(800);
            frame.PixelHeight.Should().Be(480);
        }

        // Test Order Label with Note
        var testOrder = new Order
        {
            Id = 99,
            OrderCode = "ORD-TEST-99",
            Customer = new Customer { CanonicalName = "Khách Hàng Không Có Địa Chỉ", Phone = "0911223344", Address = null },
            Note = "Ghi chú đóng gói: kiểm đếm kỹ 10 tấm trước khi đóng thùng"
        };
        string orderLabelPath = await exporter.ExportOrderShippingLabelImageAsync(testOrder, exportDir);
        File.Exists(orderLabelPath).Should().BeTrue();
    }

    [Fact]
    public async Task WpfShippingLabelExporter_WhenTargetFileHasUserMappedSectionOpen_FallsBackWithoutThrowing()
    {
        var bill = new CustomerBill
        {
            BillNumber = "BILL-20261001-0022",
            CustomerNameSnapshot = "Chị Lan thêm",
            PhoneSnapshot = "0912345678",
            ShippingAddressSnapshot = "Hà Nội",
            GrandTotal = 500000,
            IsPaid = false
        };

        var exporter = new WpfShippingLabelExporter(_settingsRepo);
        string exportDir = Path.Combine(_tempRoot, "MappedLockTest");

        // 1. First export creates the deterministic file
        string firstPath = await exporter.ExportShippingLabelImageAsync(bill, exportDir);
        File.Exists(firstPath).Should().BeTrue();

        // 2. Simulate an external process (Windows Photos / WIC decoder) holding a memory-mapped section open on firstPath
        using var fs = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
            fs, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        using var accessor = mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);

        // 3. Second export while memory-mapped section is open must succeed via fallback file
        string secondPath = await exporter.ExportShippingLabelImageAsync(bill, exportDir);
        File.Exists(secondPath).Should().BeTrue();
        secondPath.Should().NotBe(firstPath);
        new FileInfo(secondPath).Length.Should().BeGreaterThan(1000);
    }
}


