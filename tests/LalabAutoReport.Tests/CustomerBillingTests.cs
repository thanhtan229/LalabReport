using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.FileSystem;
using LalabAutoReport.Infrastructure.Reporting;
using LalabAutoReport.UI.ViewModels;
using Xunit;

namespace LalabAutoReport.Tests;

public class CustomerBillingTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dbPath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly DatabaseMigrator _migrator;
    private readonly SqliteOrderRepository _orderRepo;
    private readonly SqliteBillRepository _billRepo;
    private readonly SqliteCustomerBillRepository _customerBillRepo;
    private readonly SqliteCustomerRepository _customerRepo;
    private readonly SqliteProductRepository _productRepo;
    private readonly SqliteSettingsRepository _settingsRepo;
    private readonly PhysicalFileSystemAdapter _fileSystem;
    private readonly FolderStructureParser _structureParser;
    private readonly PrintFolderResolver _folderResolver;
    private readonly CustomerResolver _customerResolver;
    private readonly PrintSpecificationResolver _specResolver;
    private readonly ScanService _scanService;
    private readonly CustomerBillingService _customerBillingService;
    private readonly WpfJpegBillExporter _jpegExporter;

    public CustomerBillingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Lalab_CustBillTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _dbPath = Path.Combine(_tempRoot, "test_customer_billing.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
        _migrator = new DatabaseMigrator(_connectionFactory);
        _migrator.MigrateAsync().GetAwaiter().GetResult();

        _orderRepo = new SqliteOrderRepository(_connectionFactory);
        _billRepo = new SqliteBillRepository(_connectionFactory);
        _customerBillRepo = new SqliteCustomerBillRepository(_connectionFactory);
        _customerRepo = new SqliteCustomerRepository(_connectionFactory);
        _productRepo = new SqliteProductRepository(_connectionFactory);
        _settingsRepo = new SqliteSettingsRepository(_connectionFactory);

        _settingsRepo.SaveSettingsAsync(new AppSettings
        {
            RootFolder = _tempRoot,
            BillExportFolder = Path.Combine(_tempRoot, "_BILLS"),
            SupportedExtensions = new() { ".jpg", ".jpeg", ".png", ".tif" }
        }).GetAwaiter().GetResult();

        _fileSystem = new PhysicalFileSystemAdapter();
        _structureParser = new FolderStructureParser(_fileSystem);
        _folderResolver = new PrintFolderResolver(_fileSystem);
        _customerResolver = new CustomerResolver(_customerRepo);
        _specResolver = new PrintSpecificationResolver(_productRepo);

        _scanService = new ScanService(
            _fileSystem,
            _structureParser,
            _folderResolver,
            _settingsRepo,
            _orderRepo,
            _customerResolver,
            _specResolver,
            _billRepo,
            customerBillRepository: _customerBillRepo
        );

        _customerBillingService = new CustomerBillingService(
            _customerRepo,
            _orderRepo,
            _customerBillRepo,
            _productRepo,
            _scanService
        );

        _jpegExporter = new WpfJpegBillExporter(_settingsRepo);
    }

    [Theory]
    [InlineData(BillType.Customer, CustomerBillStatus.Locked)]
    [InlineData(BillType.Guest, CustomerBillStatus.Exported)]
    public async Task ProductionHistory_CannotRewriteOrDeleteWithoutReopen(BillType type, CustomerBillStatus status)
    {
        var bill = new CustomerBill { BillNumber = "HISTORY", BillType = type, Status = status,
            CustomerNameSnapshot = "Snapshot", GrandTotal = 5000, ProductSubtotal = 5000 };
        await _customerBillRepo.SaveBillAsync(bill);
        var stale = (await _customerBillRepo.GetByIdAsync(bill.Id))!;
        stale.GrandTotal = 9999;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _customerBillRepo.SaveBillAsync(stale));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _customerBillRepo.DeleteBillAsync(bill.Id));
        await _customerBillRepo.DeleteDraftBillAsync(bill.Id);
        (await _customerBillRepo.GetByIdAsync(bill.Id))!.GrandTotal.Should().Be(5000);
        await _customerBillingService.ReopenBillAsync(bill.Id, "Correct approved amount");
        var reopened = (await _customerBillRepo.GetByIdAsync(bill.Id))!;
        reopened.GrandTotal = 6000;
        await _customerBillRepo.SaveBillAsync(reopened);
        (await _customerBillRepo.GetByIdAsync(bill.Id))!.GrandTotal.Should().Be(6000);
    }

    [Fact]
    public async Task ProductionHistory_ExportMetadataDoesNotUndoConcurrentPayment()
    {
        var bill = new CustomerBill { BillNumber = "EXPORT", Status = CustomerBillStatus.Locked,
            CustomerNameSnapshot = "Snapshot", GrandTotal = 5000 };
        await _customerBillRepo.SaveBillAsync(bill);
        await _customerBillRepo.SetPaymentStatusAsync(bill.Id, true);
        bill.Status = CustomerBillStatus.Exported;
        bill.ExportFilePath = "test.jpg";
        await _customerBillRepo.SaveBillAsync(bill);
        var saved = (await _customerBillRepo.GetByIdAsync(bill.Id))!;
        saved.IsPaid.Should().BeTrue();
        saved.GrandTotal.Should().Be(5000);
        saved.Status.Should().Be(CustomerBillStatus.Exported);
    }

    [Fact]
    public async Task ProductionVerification_FilesChangedAfterReviewCannotLockStaleDraft()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Fresh Verify" });
        string folder = Path.Combine(_tempRoot, "2026-10-02", "Fresh Verify", "13x18 in");
        CreateDummyFiles(folder, 2);
        await _scanService.ScanDateAsync("2026-10-02");
        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        File.WriteAllText(Path.Combine(folder, "new.jpg"), "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _customerBillingService.LockBillAsync(draft));
        (await _customerBillRepo.GetByIdAsync(draft.Id))!.Status.Should().Be(CustomerBillStatus.Draft);
        var reviewed = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        reviewed.Lines[0].BilledQuantity = 4;
        reviewed.Lines[0].QuantityOverrideReason = "Approved adjustment";
        await _customerBillingService.LockBillAsync(reviewed);
        (await _customerBillRepo.GetByIdAsync(reviewed.Id))!.Lines[0].BilledQuantity.Should().Be(4);
    }

    [Fact]
    public void ProductionCloudLink_DoesNotExposeLanToken()
    {
        MobileAccessLink.Create("https://cloud.example", "private-lan-token", true).Should().Be("https://cloud.example/");
        MobileAccessLink.Create("http://lan:5050", "private-lan-token", false).Should().Contain("?auth=private-lan-token");
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

    private void CreateDummyFiles(string folderPath, int count)
    {
        Directory.CreateDirectory(folderPath);
        for (int i = 1; i <= count; i++)
        {
            File.WriteAllText(Path.Combine(folderPath, $"img_{i:D3}.jpg"), "dummy image content");
        }
    }

    [Fact]
    public async Task CandidateSelection_CustomerWithNoPreviousBill_IncludesAllUnbilledOrdersAndJobs()
    {
        // Setup: Create customer
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Nguyễn Văn An" });
        await _customerRepo.AddAliasAsync(customer.Id, "Van An");

        // Create 2 date folders with orders for Van An
        string spec1 = Path.Combine(_tempRoot, "2026-09-28", "Van An", "In Lụa 13x18");
        string spec2 = Path.Combine(_tempRoot, "2026-09-29", "Van An", "In Lụa 15x21");
        CreateDummyFiles(spec1, 10);
        CreateDummyFiles(spec2, 20);

        // Scan both dates
        await _scanService.ScanDateAsync("2026-09-28");
        await _scanService.ScanDateAsync("2026-09-29");

        // Action: Get Unbilled Summary
        var summary = await _customerBillingService.GetCustomerUnbilledSummaryAsync(customer.Id);

        // Assert
        summary.CustomerId.Should().Be(customer.Id);
        summary.CustomerName.Should().Be("Nguyễn Văn An");
        summary.UnbilledOrderCount.Should().Be(2);
        summary.UnbilledJobCount.Should().Be(2);
        summary.LastBillNumber.Should().BeNull();
    }

    [Fact]
    public async Task CandidateSelection_CustomerWithLockedBill_ExcludesBilledJobs()
    {
        // Setup: Customer with 2 orders on 2026-09-28 and 2026-09-29
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Studio Ánh Sáng" });
        await _customerRepo.AddAliasAsync(customer.Id, "Anh Sang");

        string spec1 = Path.Combine(_tempRoot, "2026-09-28", "Anh Sang", "In Lụa 13x18");
        string spec2 = Path.Combine(_tempRoot, "2026-09-29", "Anh Sang", "In Lụa 15x21");
        CreateDummyFiles(spec1, 15);
        CreateDummyFiles(spec2, 25);

        await _scanService.ScanDateAsync("2026-09-28");
        await _scanService.ScanDateAsync("2026-09-29");

        // Build first draft and lock only the first order (2026-09-28)
        var draftRes = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var order28 = draftRes.Draft.Orders.First(o => o.OrderDateSnapshot == "2026-09-28");
        order28.IsIncluded = true;
        order28.Lines.ForEach(l => l.BilledUnitPrice = 5000);
        draftRes.Draft.Orders.First(o => o.OrderDateSnapshot == "2026-09-29").IsIncluded = false;

        _customerBillingService.RecalculateTotals(draftRes.Draft);
        var lockedBill = await _customerBillingService.LockBillAsync(draftRes.Draft);
        lockedBill.Status.Should().Be(CustomerBillStatus.Locked);

        // Action: Get Unbilled Summary again
        var summary = await _customerBillingService.GetCustomerUnbilledSummaryAsync(customer.Id);

        // Assert: Only 2026-09-29 order remains unbilled!
        summary.UnbilledOrderCount.Should().Be(1);
        summary.UnbilledJobCount.Should().Be(1);
        summary.LastBillNumber.Should().Be(lockedBill.BillNumber);
    }

    [Fact]
    public async Task CandidateSelection_UnbilledOrderFromPreviousPeriod_FlaggedCorrectly()
    {
        // Customer has locked bill for 2026-09-25, but 2026-09-22 was never billed
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Lê Thị Mai" });
        await _customerRepo.AddAliasAsync(customer.Id, "Mai Le");

        string folder25 = Path.Combine(_tempRoot, "2026-09-25", "Mai Le", "In Lụa 13x18");
        CreateDummyFiles(folder25, 10);
        await _scanService.ScanDateAsync("2026-09-25");

        // Bill and lock 2026-09-25
        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        draft.Orders.First().Lines.ForEach(l => l.BilledUnitPrice = 5000);
        _customerBillingService.RecalculateTotals(draft);
        await _customerBillingService.LockBillAsync(draft);

        // Now a backlog order from 2026-09-22 is scanned later
        string folder22 = Path.Combine(_tempRoot, "2026-09-22", "Mai Le", "In Lụa 13x18");
        CreateDummyFiles(folder22, 5);
        await _scanService.ScanDateAsync("2026-09-22");

        // Action
        var summary = await _customerBillingService.GetCustomerUnbilledSummaryAsync(customer.Id);

        // Assert: Detected unbilled from previous period!
        summary.HasOrdersFromPreviousPeriod.Should().BeTrue();
        summary.UnbilledOrderCount.Should().Be(1);
    }

    [Fact]
    public async Task MultipleAliases_MappingToSameCustomer_RetrievedTogetherInDraft()
    {
        // 3 physical folder names for the same customer: "Van An", "Anh An", "A.An"
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Trần Văn An" });
        await _customerRepo.AddAliasAsync(customer.Id, "Van An");
        await _customerRepo.AddAliasAsync(customer.Id, "Anh An");
        await _customerRepo.AddAliasAsync(customer.Id, "A.An");

        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "Van An", "In Lụa 13x18"), 10);
        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "Anh An", "In Lụa 15x21"), 12);
        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "A.An", "In Lụa 20x30"), 15);

        await _scanService.ScanDateAsync("2026-09-28");

        // Action: Build draft
        var result = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);

        // Assert: 3 separate physical orders, but unified in one customer draft
        result.Draft.Orders.Should().HaveCount(3);
        result.Draft.Orders.Select(o => o.OriginalFolderNameSnapshot).Should().BeEquivalentTo(new[] { "Van An", "Anh An", "A.An" });
        result.Draft.Lines.Should().HaveCount(3);
    }

    [Fact]
    public async Task DraftGrouping_CalculatesOrderSubtotals_AndProductSubtotal()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Hàng Test 1" });
        await _customerRepo.AddAliasAsync(customer.Id, "Test1");

        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "Test1", "In Lụa 13x18"), 10);
        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-29", "Test1", "In Lụa 15x21"), 5);

        await _scanService.ScanDateAsync("2026-09-28");
        await _scanService.ScanDateAsync("2026-09-29");

        var result = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var draft = result.Draft;

        draft.Orders.Should().HaveCount(2);

        // Manually set prices for predictable test assertion
        var order1 = draft.Orders.First(o => o.OrderDateSnapshot == "2026-09-28");
        order1.Lines.First().BilledUnitPrice = 5000;
        order1.Lines.First().LineTotal = 10 * 5000;

        var order2 = draft.Orders.First(o => o.OrderDateSnapshot == "2026-09-29");
        order2.Lines.First().BilledUnitPrice = 10000;
        order2.Lines.First().LineTotal = 5 * 10000;

        // Recalculate
        _customerBillingService.RecalculateTotals(draft);

        // Assert
        order1.Subtotal.Should().Be(50000);
        order2.Subtotal.Should().Be(50000);

        draft.ProductSubtotal.Should().Be(100000);
        draft.GrandTotal.Should().Be(100000);
    }

    [Fact]
    public async Task UserQuantityOverride_PreservesScannedQuantity()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Override Qty" });
        await _customerRepo.AddAliasAsync(customer.Id, "OverrideQty");

        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "OverrideQty", "In Lụa 13x18"), 20);
        await _scanService.ScanDateAsync("2026-09-28");

        var result = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var line = result.Draft.Orders.First().Lines.First();

        line.ScannedQuantity.Should().Be(20);
        line.BilledQuantity.Should().Be(20);

        // User overrides quantity to 18 with reason
        line.BilledQuantity = 18;
        line.QuantityOverrideReason = "Khách in hỏng 2 tấm bỏ đi";
        line.BilledUnitPrice = 4000;
        line.LineTotal = 18 * 4000;

        _customerBillingService.RecalculateTotals(result.Draft);

        // Assert
        line.ScannedQuantity.Should().Be(20); // NOT overwritten
        line.BilledQuantity.Should().Be(18);
        line.QuantityOverrideReason.Should().Be("Khách in hỏng 2 tấm bỏ đi");
        line.LineTotal.Should().Be(18 * 4000);
    }

    [Fact]
    public async Task UserUnitPriceOverride_PreservesConfiguredPrice()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Override Price" });
        await _customerRepo.AddAliasAsync(customer.Id, "OverridePrice");

        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "OverridePrice", "In Lụa 13x18"), 10);
        await _scanService.ScanDateAsync("2026-09-28");

        var result = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var line = result.Draft.Orders.First().Lines.First();

        long configured = line.ConfiguredUnitPrice;

        // User gives a discount rate
        line.BilledUnitPrice = 2500;
        line.PriceOverrideReason = "Chiết khấu thân thiết";
        line.LineTotal = 10 * 2500;

        _customerBillingService.RecalculateTotals(result.Draft);

        // Assert
        line.ConfiguredUnitPrice.Should().Be(configured);
        line.BilledUnitPrice.Should().Be(2500);
        line.PriceOverrideReason.Should().Be("Chiết khấu thân thiết");
        line.LineTotal.Should().Be(10 * 2500);
    }

    [Fact]
    public async Task Adjustments_AddAndDeduct_CalculatesGrandTotalCorrectly()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Adjustments" });
        await _customerRepo.AddAliasAsync(customer.Id, "Adjustments");

        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "Adjustments", "In Lụa 13x18"), 10);
        await _scanService.ScanDateAsync("2026-09-28");

        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        var line = draft.Orders.First().Lines.First();
        line.BilledUnitPrice = 10000;
        line.LineTotal = 10 * 10000; // 100,000

        // Add default adjustments:
        // Shipping: +30,000
        var ship = draft.Adjustments.First(a => a.Type == AdjustmentType.Shipping);
        ship.Amount = 30000;

        // Surcharge: +15,000
        var sur = draft.Adjustments.First(a => a.Type == AdjustmentType.Surcharge);
        sur.Amount = 15000;

        // Discount: -20,000
        var disc = draft.Adjustments.First(a => a.Type == AdjustmentType.Discount);
        disc.Amount = 20000;

        // Custom Deduct: -5,000 (Đã ứng cọc)
        draft.Adjustments.Add(new BillAdjustment
        {
            BillId = draft.Id,
            Type = AdjustmentType.Custom,
            Label = "Đã ứng cọc",
            Amount = 5000,
            Direction = AdjustmentDirection.Deduct,
            SortOrder = 10
        });

        _customerBillingService.RecalculateTotals(draft);

        // Assert:
        // ProductSubtotal = 100,000
        // AdjustmentsTotal = +30,000 + 15,000 - 20,000 - 5,000 = +20,000
        // GrandTotal = 120,000
        draft.ProductSubtotal.Should().Be(100000);
        draft.AdjustmentsTotal.Should().Be(20000);
        draft.GrandTotal.Should().Be(120000);

        // Test non-negative clamping
        disc.Amount = 200000; // Deduct exceeds product subtotal
        _customerBillingService.RecalculateTotals(draft);
        draft.GrandTotal.Should().Be(0); // Clamped to non-negative
    }

    [Fact]
    public async Task AlbumCalculation_BasePlusExtraSheets_Breakdown()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Album" });
        await _customerRepo.AddAliasAsync(customer.Id, "AlbumKhach");

        // Create an album order: 25 sheets total
        string albumFolder = Path.Combine(_tempRoot, "2026-09-28", "AlbumKhach", "Album Phẳng 20x30", "Ruột");
        CreateDummyFiles(albumFolder, 25);
        await _scanService.ScanDateAsync("2026-09-28");

        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        var line = draft.Orders.First().Lines.First();

        // Simulate album pricing: Base 10 sheets = 300,000; Extra 15 sheets * 15,000 = 225,000
        line.BillingMethodSnapshot = BillingMethod.AlbumBasePlusExtra;
        line.IncludedSheetsSnapshot = 10;
        line.BasePriceSnapshot = 300000;
        line.ExtraSheetPriceSnapshot = 15000;
        line.ExtraSheetCount = 15;
        line.SheetCount = 25;
        line.BilledUnitPrice = 300000 + (15 * 15000); // 525,000
        line.LineTotal = line.BilledUnitPrice;

        _customerBillingService.RecalculateTotals(draft);

        // LineTotal = 300,000 + (15 * 15,000) = 525,000
        line.LineTotal.Should().Be(525000);
        draft.ProductSubtotal.Should().Be(525000);
        draft.GrandTotal.Should().Be(525000);
    }

    [Fact]
    public async Task BillLocking_MakesSnapshotImmutable_AttachesJobs_SubsequentBillExcludesThem()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Lock Bill" });
        await _customerRepo.AddAliasAsync(customer.Id, "LockBill");

        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "LockBill", "In Lụa 13x18"), 10);
        await _scanService.ScanDateAsync("2026-09-28");

        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        var line = draft.Orders.First().Lines.First();
        line.BilledUnitPrice = 3000;
        line.LineTotal = 10 * 3000;
        _customerBillingService.RecalculateTotals(draft);

        // Action: Lock Bill
        var lockedBill = await _customerBillingService.LockBillAsync(draft);

        // Assert: Locked properties
        lockedBill.Status.Should().Be(CustomerBillStatus.Locked);
        lockedBill.LockedAt.Should().NotBeNull();
        lockedBill.BillNumber.Should().StartWith("BILL-");

        // Verify that order item in database now has customer_bill_id set
        var orders = await _orderRepo.GetOrdersByCustomerIdAsync(customer.Id);
        var itemScan = orders.First().Items.First();
        itemScan.CustomerBillId.Should().Be(lockedBill.Id);

        // Subsequent draft build has 0 unbilled items
        var summary = await _customerBillingService.GetCustomerUnbilledSummaryAsync(customer.Id);
        summary.UnbilledOrderCount.Should().Be(0);
        summary.UnbilledJobCount.Should().Be(0);
    }

    [Fact]
    public void DeterministicFilename_AndDiacriticsSanitization()
    {
        var bill = new CustomerBill
        {
            BillNumber = "BILL-20260929-0001",
            CustomerNameSnapshot = "Nguyễn Văn An — Studio Ánh Dương #1"
        };

        string fileName = WpfJpegBillExporter.GenerateDeterministicFileName(bill);

        // Expect: BILL-20260929-0001_Nguyen-Van-An-Studio-Anh-Duong-1.jpg
        fileName.Should().StartWith("BILL-20260929-0001_");
        fileName.Should().EndWith(".jpg");
        fileName.Should().NotContain("—");
        fileName.Should().NotContain("#");
        fileName.Should().NotContain("ễ");
        fileName.Should().NotContain("ă");
        fileName.Should().NotContain("Á");
        fileName.Should().NotContain(" ");
    }

    [Fact]
    public async Task ReopenBill_UnlinksJobs_AllowsReEditing()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Reopen" });
        await _customerRepo.AddAliasAsync(customer.Id, "Reopen");

        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "Reopen", "In Lụa 13x18"), 10);
        await _scanService.ScanDateAsync("2026-09-28");

        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        var line = draft.Orders.First().Lines.First();
        line.BilledUnitPrice = 5000;
        line.LineTotal = 10 * 5000;
        _customerBillingService.RecalculateTotals(draft);

        var lockedBill = await _customerBillingService.LockBillAsync(draft);
        lockedBill.Status.Should().Be(CustomerBillStatus.Locked);

        // Action: Reopen bill
        await _customerBillingService.ReopenBillAsync(lockedBill.Id, "Cần sửa lại đơn giá theo thỏa thuận");

        // Assert
        var reopened = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        reopened!.Status.Should().Be(CustomerBillStatus.Draft);
        reopened.LockedAt.Should().BeNull();

        // Verify order items unlinked
        var orders = await _orderRepo.GetOrdersByCustomerIdAsync(customer.Id);
        orders.First().Items.First().CustomerBillId.Should().BeNull();

        // Now items are unbilled again
        var summary = await _customerBillingService.GetCustomerUnbilledSummaryAsync(customer.Id);
        summary.UnbilledOrderCount.Should().Be(1);
    }

    [Fact]
    public async Task WpfJpegBillExporter_RendersJpegSuccessfully()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Studio Hạnh Phúc" });
        await _customerRepo.AddAliasAsync(customer.Id, "Hanh Phuc");

        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "Hanh Phuc", "In Lụa 13x18"), 15);
        await _scanService.ScanDateAsync("2026-09-28");

        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        var line = draft.Orders.First().Lines.First();
        line.BilledUnitPrice = 3500;
        line.LineTotal = 15 * 3500;
        draft.Adjustments.First(a => a.Type == AdjustmentType.Shipping).Amount = 25000;

        _customerBillingService.RecalculateTotals(draft);
        var lockedBill = await _customerBillingService.LockBillAsync(draft);

        // Action: Render JPEG
        string exportedPath = await _jpegExporter.ExportBillToJpegAsync(lockedBill);
        await _customerBillRepo.SaveBillAsync(lockedBill);

        // Assert: File exists, length > 0, valid JPEG header
        File.Exists(exportedPath).Should().BeTrue();
        var bytes = File.ReadAllBytes(exportedPath);
        bytes.Length.Should().BeGreaterThan(1000);

        // JPEG Magic Bytes: 0xFF, 0xD8
        bytes[0].Should().Be(0xFF);
        bytes[1].Should().Be(0xD8);

        // Database record updated with path and status Exported
        var updatedBill = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        updatedBill!.Status.Should().Be(CustomerBillStatus.Exported);
        updatedBill.ExportFilePath.Should().Be(exportedPath);
    }

    [Fact]
    public async Task ExcludedOrderOrJob_RemainsUnbilled_ForSubsequentBills()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Exclude Test" });
        await _customerRepo.AddAliasAsync(customer.Id, "ExcludeTest");

        // Create 2 jobs under order: Spec 1 and Spec 2
        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "ExcludeTest", "In Lụa 13x18"), 10);
        CreateDummyFiles(Path.Combine(_tempRoot, "2026-09-28", "ExcludeTest", "In Lụa 15x21"), 12);
        await _scanService.ScanDateAsync("2026-09-28");

        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        var order = draft.Orders.First();
        order.Lines.Should().HaveCount(2);

        // Include job 1, Exclude job 2
        order.Lines[0].IsIncluded = true;
        order.Lines[0].BilledUnitPrice = 4000;
        order.Lines[1].IsIncluded = false; // Excluded!

        _customerBillingService.RecalculateTotals(draft);
        var lockedBill = await _customerBillingService.LockBillAsync(draft);

        // Assert: Job 1 attached to locked bill, Job 2 remains customer_bill_id == null
        var ordersInDb = await _orderRepo.GetOrdersByCustomerIdAsync(customer.Id);
        var item1 = ordersInDb.First().Items.First(i => i.SpecificationFolderName == "In Lụa 13x18");
        var item2 = ordersInDb.First().Items.First(i => i.SpecificationFolderName == "In Lụa 15x21");

        item1.CustomerBillId.Should().Be(lockedBill.Id);
        item2.CustomerBillId.Should().BeNull(); // Unbilled!

        // Subsequent summary should still report 1 unbilled job
        var summary = await _customerBillingService.GetCustomerUnbilledSummaryAsync(customer.Id);
        summary.UnbilledJobCount.Should().Be(1);
    }

    [Fact]
    public async Task ReExport_UsesSnapshot_WithoutRescanning()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách ReExport Snapshot" });
        await _customerRepo.AddAliasAsync(customer.Id, "ReExportCust");

        string folder = Path.Combine(_tempRoot, "2026-09-28", "ReExportCust", "In Lụa 13x18");
        CreateDummyFiles(folder, 8);
        await _scanService.ScanDateAsync("2026-09-28");

        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        draft.Orders.First().Lines.First().BilledUnitPrice = 6000;
        _customerBillingService.RecalculateTotals(draft);
        var lockedBill = await _customerBillingService.LockBillAsync(draft);

        // Now completely delete the order folder from disk
        Directory.Delete(Path.Combine(_tempRoot, "2026-09-28", "ReExportCust"), true);

        // Action: Re-export JPEG using only the persisted snapshot
        string reExportedPath = await _jpegExporter.ExportBillToJpegAsync(lockedBill);

        // Assert: Re-export succeeds cleanly without disk error
        File.Exists(reExportedPath).Should().BeTrue();
        new FileInfo(reExportedPath).Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public async Task CustomerBillLine_Note_PersistsAndLoadsFromDatabase()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Hàng Ghi Chú Line" });
        await _customerRepo.AddAliasAsync(customer.Id, "KhachGhiChuLine");

        string folder = Path.Combine(_tempRoot, "2026-09-28", "KhachGhiChuLine", "13x18 in");
        CreateDummyFiles(folder, 5);
        await _scanService.ScanDateAsync("2026-09-28");

        var draft = (await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id)).Draft;
        var firstLine = draft.Orders.First().Lines.First();
        firstLine.BilledUnitPrice = 5000;
        firstLine.Note = "Ghi chú test: In giấy lụa cán mờ, giao trước 17h";

        _customerBillingService.RecalculateTotals(draft);
        var lockedBill = await _customerBillingService.LockBillAsync(draft);

        // Load back from DB directly
        var loadedBill = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        loadedBill.Should().NotBeNull();
        var loadedLine = loadedBill!.Orders.First().Lines.First();
        loadedLine.Note.Should().Be("Ghi chú test: In giấy lụa cán mờ, giao trước 17h");

        // Also check exporter includes note and works cleanly
        string jpegPath = await _jpegExporter.ExportBillToJpegAsync(loadedBill);
        File.Exists(jpegPath).Should().BeTrue();
    }

    [Fact]
    public void CustomerBillLineDisplayModel_Note_UpdatesDomainAndTriggersCallback()
    {
        var line = new CustomerBillLine
        {
            ProductNameSnapshot = "Ảnh Lụa 13x18",
            ScannedQuantity = 10,
            BilledQuantity = 10,
            ConfiguredUnitPrice = 5000,
            BilledUnitPrice = 5000,
            LineTotal = 50000,
            Note = null
        };

        bool callbackFired = false;
        var displayModel = new LalabAutoReport.UI.ViewModels.CustomerBillLineDisplayModel(line, () => callbackFired = true);

        displayModel.Note.Should().BeNull();
        line.Note.Should().BeNull();

        // Update note via ViewModel (simulate user typing in UI)
        displayModel.Note = "Khách yêu cầu căn màu ấm";

        displayModel.Note.Should().Be("Khách yêu cầu căn màu ấm");
        line.Note.Should().Be("Khách yêu cầu căn màu ấm");
        callbackFired.Should().BeTrue();
    }

    [Fact]
    public async Task CustomerBillingService_PreservesLineNoteOnDraftRefresh()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Hàng Refresh Note" });
        await _customerRepo.AddAliasAsync(customer.Id, "KhachRefreshNote");

        string folder = Path.Combine(_tempRoot, "2026-09-28", "KhachRefreshNote", "In Lụa 13x18");
        CreateDummyFiles(folder, 4);
        await _scanService.ScanDateAsync("2026-09-28");

        // Draft 1: user enters a line note
        var draftResult1 = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var draft1 = draftResult1.Draft;
        draft1.Orders.First().Lines.First().Note = "Ghi chú đã nhập trên draft";

        // Save draft to DB
        await _customerBillRepo.SaveBillAsync(draft1);

        // Draft 2: refresh draft (forceRescan: false or true)
        var draftResult2 = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id, forceRescan: false);
        var draft2 = draftResult2.Draft;

        draft2.Orders.First().Lines.First().Note.Should().Be("Ghi chú đã nhập trên draft");
    }

    [Fact]
    public void ShouldShowOrderSubtotal_ReturnsFalseForDefaultOrders_AndTrueForExplicitOrders()
    {
        var defaultOrder1 = new CustomerBillOrder { OrderNameSnapshot = "Đơn mặc định" };
        var defaultOrder2 = new CustomerBillOrder { OrderNameSnapshot = " Don mac dinh " };
        var emptyOrder = new CustomerBillOrder { OrderNameSnapshot = "" };
        var nullOrder = new CustomerBillOrder { OrderNameSnapshot = null! };
        var explicitOrder = new CustomerBillOrder { OrderNameSnapshot = "Đơn Album cưới" };

        WpfJpegBillExporter.ShouldShowOrderSubtotal(defaultOrder1).Should().BeFalse();
        WpfJpegBillExporter.ShouldShowOrderSubtotal(defaultOrder2).Should().BeFalse();
        WpfJpegBillExporter.ShouldShowOrderSubtotal(emptyOrder).Should().BeFalse();
        WpfJpegBillExporter.ShouldShowOrderSubtotal(nullOrder).Should().BeFalse();
        WpfJpegBillExporter.ShouldShowOrderSubtotal(explicitOrder).Should().BeTrue();
    }

    [Fact]
    public async Task ExportBillToJpeg_WithDefaultOrders_RendersSuccessfully()
    {
        string artifactDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "brain", "a000c280-7e3c-4300-a127-b366304360e5");
        var bill = new CustomerBill
        {
            BillNumber = "BILL-20260929-0002",
            CustomerId = 1,
            CustomerNameSnapshot = "Văn An",
            PhoneSnapshot = "0901234567",
            PeriodStart = "2026-09-25",
            PeriodEnd = "2026-09-30",
            CreatedAt = DateTimeOffset.UtcNow,
            ProductSubtotal = 445000,
            GrandTotal = 445000,
            Adjustments = new List<BillAdjustment>
            {
                new() { Label = "Phí vận chuyển", Amount = 30000, Direction = AdjustmentDirection.Add },
                new() { Label = "Phụ thu", Amount = 20000, Direction = AdjustmentDirection.Add },
                new() { Label = "Giảm giá", Amount = 50000, Direction = AdjustmentDirection.Deduct }
            },
            Orders = new List<CustomerBillOrder>
            {
                new()
                {
                    OrderId = 1,
                    OrderNameSnapshot = "Đơn mặc định",
                    OrderDateSnapshot = "2026-09-25",
                    Subtotal = 50000,
                    IsIncluded = true,
                    Lines = new List<CustomerBillLine>
                    {
                        new() { ProductNameSnapshot = "13x18 in", BilledQuantity = 10, BilledUnitPrice = 5000, LineTotal = 50000, IsIncluded = true }
                    }
                },
                new()
                {
                    OrderId = 2,
                    OrderNameSnapshot = "Đơn mặc định",
                    OrderDateSnapshot = "2026-09-29",
                    Subtotal = 90000,
                    IsIncluded = true,
                    Lines = new List<CustomerBillLine>
                    {
                        new() { ProductNameSnapshot = "20x30", BilledQuantity = 6, BilledUnitPrice = 15000, LineTotal = 90000, IsIncluded = true }
                    }
                },
                new()
                {
                    OrderId = 3,
                    OrderNameSnapshot = "Đơn mặc định",
                    OrderDateSnapshot = "2026-09-29",
                    Subtotal = 80000,
                    IsIncluded = true,
                    Lines = new List<CustomerBillLine>
                    {
                        new() { ProductNameSnapshot = "13x18 in", BilledQuantity = 16, BilledUnitPrice = 5000, LineTotal = 80000, IsIncluded = true }
                    }
                },
                new()
                {
                    OrderId = 4,
                    OrderNameSnapshot = "Đơn mặc định",
                    OrderDateSnapshot = "2026-09-29",
                    Subtotal = 75000,
                    IsIncluded = true,
                    Lines = new List<CustomerBillLine>
                    {
                        new() { ProductNameSnapshot = "13x18 in", BilledQuantity = 15, BilledUnitPrice = 5000, LineTotal = 75000, IsIncluded = true }
                    }
                },
                new()
                {
                    OrderId = 5,
                    OrderNameSnapshot = "Đơn mặc định",
                    OrderDateSnapshot = "2026-09-30",
                    Subtotal = 150000,
                    IsIncluded = true,
                    Lines = new List<CustomerBillLine>
                    {
                        new() { ProductNameSnapshot = "20x30", BilledQuantity = 10, BilledUnitPrice = 15000, LineTotal = 150000, IsIncluded = true }
                    }
                }
            }
        };

        string exportedPath = await _jpegExporter.ExportBillToJpegAsync(bill, artifactDir);
        File.Exists(exportedPath).Should().BeTrue();
        new FileInfo(exportedPath).Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void CustomerBillOrderDisplayModel_EditingOrderName_UpdatesDomainOrderSnapshot()
    {
        var domainOrder = new CustomerBillOrder
        {
            OrderId = 1,
            OrderNameSnapshot = "Anh An",
            OrderDateSnapshot = "2026-09-29",
            Subtotal = 50000
        };

        bool dataChangedTriggered = false;
        var displayModel = new LalabAutoReport.UI.ViewModels.CustomerBillOrderDisplayModel(domainOrder, () => dataChangedTriggered = true);

        displayModel.OrderName.Should().Be("Anh An");

        // User edits the order name on UI
        displayModel.OrderName = "Ảnh tiệc cưới";

        domainOrder.OrderNameSnapshot.Should().Be("Ảnh tiệc cưới");
        dataChangedTriggered.Should().BeTrue();
    }

    [Fact]
    public async Task Rescan_After_CustomerBill_Locked_Detects_FilesystemChangedAfterLock()
    {
        // 1. Setup customer
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Chị Lan" }, "Chị Lan");

        // 2. Create filesystem order: 2026-09-30 / Chị Lan / 13x18 in / 3 files
        string date = "2026-09-30";
        string orderDir = Path.Combine(_tempRoot, date, "Chị Lan", "13x18 in");
        Directory.CreateDirectory(orderDir);
        File.WriteAllText(Path.Combine(orderDir, "01.jpg"), "fake");
        File.WriteAllText(Path.Combine(orderDir, "02.jpg"), "fake");
        File.WriteAllText(Path.Combine(orderDir, "03.jpg"), "fake");

        // 3. Scan order
        var order = await _scanService.ScanOrderAsync($"{date}\\Chị Lan");
        order.Should().NotBeNull();
        order!.Items.Should().HaveCount(1);
        order.Items[0].PrintCount.Should().Be(3);

        // 4. Create and Lock CustomerBill
        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        draftResult.Draft.Lines.Should().HaveCount(1);
        draftResult.Draft.Lines[0].BilledQuantity.Should().Be(3);

        var lockedBill = await _customerBillingService.LockBillAsync(draftResult.Draft);
        lockedBill.Status.Should().Be(CustomerBillStatus.Locked);

        var dbOrder = await _orderRepo.GetOrderByIdAsync(order.Id);
        dbOrder!.Status.Should().Be(OrderStatus.Billed);

        // 5. User adds a new file to the folder on disk: 04.jpg
        File.WriteAllText(Path.Combine(orderDir, "04.jpg"), "fake");

        // 6. Rescan order: Must detect FilesystemChangedAfterLock and preserve the locked snapshot!
        var rescanned = await _scanService.ScanOrderAsync($"{date}\\Chị Lan");
        rescanned.Should().NotBeNull();
        rescanned!.Items[0].PrintCount.Should().Be(4);
        rescanned.FilesystemChangedAfterLock.Should().BeTrue();

        var billAfterRescan = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        billAfterRescan.Should().NotBeNull();
        billAfterRescan!.Lines[0].BilledQuantity.Should().Be(3); // Preserved snapshot!
        billAfterRescan.Status.Should().Be(CustomerBillStatus.Locked);

        var dbOrderAfterRescan = await _orderRepo.GetOrderByIdAsync(order.Id);
        dbOrderAfterRescan!.FilesystemChangedAfterLock.Should().BeTrue();

        // 7. When user explicitly chooses to update (reopening and syncing):
        await _customerBillingService.ReopenBillAsync(lockedBill.Id, "User confirmed update");
        await _customerBillingService.SyncBillWithScannedOrderAsync(rescanned);
        await _orderRepo.SetFilesystemChangedAfterLockAsync(order.Id, false);

        var updatedBill = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        updatedBill.Should().NotBeNull();
        updatedBill!.Lines[0].BilledQuantity.Should().Be(4);

        var dbOrderAfterUpdate = await _orderRepo.GetOrderByIdAsync(order.Id);
        dbOrderAfterUpdate!.FilesystemChangedAfterLock.Should().BeFalse();
    }

    [Fact]
    public async Task QuangStudio_EmptySpecificationFolder_DoesNotBlockBilling_WhenUncheckedByDefault()
    {
        // 1. Setup Customer 'Quang Studio'
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Quang Studio" }, "Quang Studio");

        // 2. Add 'Tranh Mica 50x75'
        var spec = await _productRepo.CreateSpecificationAsync(new PrintSpecification
        {
            CanonicalName = "Tranh Mica 50x75",
            UnitPrice = 120000,
            Category = ProductCategory.PhotoPrint,
            BillingMethod = BillingMethod.FileCount
        });

        // 3. Create filesystem folders:
        // - Quang Studio\Tranh Mica 50x75 (4 files)
        // - Quang Studio\50x75 in (0 files, only readme.txt)
        string date = "2026-09-29";
        string validDir = Path.Combine(_tempRoot, date, "Quang Studio", "Tranh Mica 50x75");
        Directory.CreateDirectory(validDir);
        for (int i = 1; i <= 4; i++)
        {
            File.WriteAllText(Path.Combine(validDir, $"mica_{i:000}.jpg"), "fake");
        }

        string emptyDir = Path.Combine(_tempRoot, date, "Quang Studio", "50x75 in");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, "readme.txt"), "Chưa chốt in");

        // 4. Scan order
        var order = await _scanService.ScanOrderAsync($"{date}\\Quang Studio");
        order.Should().NotBeNull();
        order!.Items.Should().HaveCount(2);

        // 5. Build draft bill
        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var draft = draftResult.Draft;
        draft.Lines.Should().HaveCount(2);

        // Find line for 50x75 in (empty)
        var emptyLine = draft.Lines.FirstOrDefault(l => l.SpecificationFolderName == "50x75 in");
        emptyLine.Should().NotBeNull();
        emptyLine!.IsIncluded.Should().BeFalse("Empty folder with no print files should be unchecked by default");
        emptyLine.ScannedQuantity.Should().Be(0);
        emptyLine.IssueMessage.Should().Contain("Chưa có thư mục in hợp lệ");

        // Find line for Tranh Mica 50x75 (valid 4 files)
        var validLine = draft.Lines.FirstOrDefault(l => l.SpecificationFolderName == "Tranh Mica 50x75");
        validLine.Should().NotBeNull();
        validLine!.IsIncluded.Should().BeTrue();
        validLine.ScannedQuantity.Should().Be(4);
        validLine.BilledQuantity.Should().Be(4);
        validLine.LineTotal.Should().Be(480000);

        // Warnings should mention the unchecked line, but BlockingIssues must be empty because the invalid line was excluded
        draftResult.BlockingIssues.Should().BeEmpty();
        draftResult.Warnings.Should().Contain(w => w.Contains("50x75 in") && w.Contains("Đã tạm bỏ chọn"));

        // 6. Lock and Export succeed without error!
        var lockedBill = await _customerBillingService.LockBillAsync(draft);
        lockedBill.Status.Should().Be(CustomerBillStatus.Locked);
        lockedBill.GrandTotal.Should().Be(480000);
    }

    [Fact]
    public async Task CustomerBillReviewViewModel_DynamicallyUpdatesBlockingIssues_WhenLinesToggled()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Quang Studio Review Test" }, "Quang Studio Review Test");

        string date = "2026-09-29";
        string validDir = Path.Combine(_tempRoot, date, "Quang Studio Review Test", "13x18 in");
        Directory.CreateDirectory(validDir);
        File.WriteAllText(Path.Combine(validDir, "p1.jpg"), "fake");

        string emptyDir = Path.Combine(_tempRoot, date, "Quang Studio Review Test", "EmptySpec");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, "notes.txt"), "empty");

        var order = await _scanService.ScanOrderAsync($"{date}\\Quang Studio Review Test");
        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);

        var vm = new LalabAutoReport.UI.ViewModels.CustomerBillReviewViewModel(
            draftResult.Draft,
            _customerBillingService,
            _jpegExporter,
            draftResult.Warnings,
            draftResult.BlockingIssues
        );

        // Initially: empty line is unchecked, so BlockingIssues is empty
        vm.HasBlockingIssues.Should().BeFalse();
        vm.BlockingIssues.Should().BeEmpty();

        // User manually checks the empty/invalid line
        var emptyLineDisplay = vm.Orders.SelectMany(o => o.Lines).First(l => l.SpecificationFolderName == "EmptySpec");
        emptyLineDisplay.IsIncluded = true;

        // Now HasBlockingIssues must dynamically become true!
        vm.HasBlockingIssues.Should().BeTrue();
        vm.BlockingIssues.Should().HaveCount(1);
        vm.BlockingIssues[0].Should().Contain("EmptySpec");

        // User unchecks the invalid line again
        emptyLineDisplay.IsIncluded = false;

        // HasBlockingIssues dynamically returns to false!
        vm.HasBlockingIssues.Should().BeFalse();
        vm.BlockingIssues.Should().BeEmpty();
    }

    [Fact]
    public void Album8Sheets_CustomerBillLineDisplayModel_AlbumDetails_OmitsStandardPackageNote()
    {
        // 1. Case: Album 8 sheets (under included 10 sheets)
        var line8 = new CustomerBillLine
        {
            BillingMethodSnapshot = BillingMethod.AlbumBasePlusExtra,
            SheetCount = 8,
            IncludedSheetsSnapshot = 10,
            ExtraSheetCount = 0,
            BasePriceSnapshot = 400000,
            ExtraSheetPriceSnapshot = 20000,
            LineTotal = 400000,
            BilledUnitPrice = 400000
        };
        var vm8 = new CustomerBillLineDisplayModel(line8);
        vm8.AlbumDetails.Should().Be("8 tờ"); // Must NOT contain "(chuẩn 10 tờ)"

        // 2. Case: Album 10 sheets (exact included sheets)
        var line10 = new CustomerBillLine
        {
            BillingMethodSnapshot = BillingMethod.AlbumBasePlusExtra,
            SheetCount = 10,
            IncludedSheetsSnapshot = 10,
            ExtraSheetCount = 0,
            BasePriceSnapshot = 400000,
            ExtraSheetPriceSnapshot = 20000,
            LineTotal = 400000,
            BilledUnitPrice = 400000
        };
        var vm10 = new CustomerBillLineDisplayModel(line10);
        vm10.AlbumDetails.Should().Be("10 tờ (chuẩn 10 tờ)");

        // 3. Case: Album 14 sheets (over included sheets)
        var line14 = new CustomerBillLine
        {
            BillingMethodSnapshot = BillingMethod.AlbumBasePlusExtra,
            SheetCount = 14,
            IncludedSheetsSnapshot = 10,
            ExtraSheetCount = 4,
            BasePriceSnapshot = 400000,
            ExtraSheetPriceSnapshot = 20000,
            LineTotal = 480000,
            BilledUnitPrice = 480000
        };
        var vm14 = new CustomerBillLineDisplayModel(line14);
        vm14.AlbumDetails.Should().Be("14 tờ (chuẩn 10 tờ + 4 tờ thêm × 20,000 đ)");
    }

    [Fact]
    public async Task Album8Sheets_JpegBillExport_RendersSuccessfully_At10SheetBasePrice()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Studio Kim" });
        await _customerRepo.AddAliasAsync(customer.Id, "Kim Studio");

        // Seed an Album with 8 sheets
        string albumFolder = Path.Combine(_tempRoot, "2026-09-29", "Kim Studio", "Album 20x20", "final");
        CreateDummyFiles(albumFolder, 8);
        await _scanService.ScanDateAsync("2026-09-29");

        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var draft = draftResult.Draft;

        var line = draft.Orders.First().Lines.First();
        line.BillingMethodSnapshot.Should().Be(BillingMethod.AlbumBasePlusExtra);
        line.SheetCount.Should().Be(8);
        line.IncludedSheetsSnapshot.Should().Be(10);
        line.ExtraSheetCount.Should().Be(0);
        // Still charged 400k (the 10-sheet base price)
        line.LineTotal.Should().Be(400000);
        draft.GrandTotal.Should().Be(400000);

        var lockedBill = await _customerBillingService.LockBillAsync(draft);
        string exportedPath = await _jpegExporter.ExportBillToJpegAsync(lockedBill);

        File.Exists(exportedPath).Should().BeTrue();
        var bytes = File.ReadAllBytes(exportedPath);
        bytes.Length.Should().BeGreaterThan(1000);
        bytes[0].Should().Be(0xFF);
        bytes[1].Should().Be(0xD8);
    }

    [Fact]
    public async Task RecordBillExportedAsync_PersistsExportPathAndExportedStatusToDatabase()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Studio Export Test" });
        string folder = Path.Combine(_tempRoot, "2026-09-29", "Studio Export Test", "In 13x18", "final");
        CreateDummyFiles(folder, 5);
        await _scanService.ScanDateAsync("2026-09-29");

        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var lockedBill = await _customerBillingService.LockBillAsync(draftResult.Draft);

        lockedBill.Status.Should().Be(CustomerBillStatus.Locked);
        lockedBill.ExportFilePath.Should().BeNull();

        string fakeExportPath = Path.Combine(_tempRoot, "dummy_bill_export.jpg");
        await File.WriteAllTextAsync(fakeExportPath, "fake image content");

        await _customerBillingService.RecordBillExportedAsync(lockedBill.Id, fakeExportPath);

        var reloaded = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(CustomerBillStatus.Exported);
        reloaded.ExportFilePath.Should().Be(fakeExportPath);
        reloaded.ExportedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CustomerBillReviewViewModel_CanViewExportedBill_AndFallbackResolution()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Studio UI Test" });
        string folder = Path.Combine(_tempRoot, "2026-09-29", "Studio UI Test", "In 13x18", "final");
        CreateDummyFiles(folder, 5);
        await _scanService.ScanDateAsync("2026-09-29");

        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var draft = draftResult.Draft;

        // 1. When draft is not exported
        var vm = new CustomerBillReviewViewModel(draft, _customerBillingService, _jpegExporter, null, null, null, _settingsRepo);
        vm.CanViewExportedBill.Should().BeFalse();
        vm.ExportButtonText.Should().Be("XUẤT BILL");
        vm.ViewBillToolTip.Should().Contain("chưa được xuất");

        // 2. Lock and export
        var locked = await _customerBillingService.LockBillAsync(draft);
        string exportedPath = await _jpegExporter.ExportBillToJpegAsync(locked);
        await _customerBillingService.RecordBillExportedAsync(locked.Id, exportedPath);

        // 3. Open locked/exported bill in ViewModel (no changes -> XEM BILL)
        var vmExported = new CustomerBillReviewViewModel(locked, _customerBillingService, _jpegExporter, null, null, null, _settingsRepo);
        vmExported.CanViewExportedBill.Should().BeTrue();
        vmExported.IsOpenOnlyState.Should().BeTrue();
        vmExported.ExportButtonText.Should().Be("XEM BILL");
        vmExported.ExportButtonIcon.Should().Be("🖼️ ");
        vmExported.ViewBillToolTip.Should().ContainEquivalentOf("mở xem");

        // 3b. Modify quantity -> transitions to XUẤT LẠI BILL
        vmExported.Orders[0].Lines[0].BilledQuantity = 99;
        vmExported.HasChanges.Should().BeTrue();
        vmExported.IsOpenOnlyState.Should().BeFalse();
        vmExported.ExportButtonText.Should().Be("XUẤT LẠI BILL");
        vmExported.ExportButtonIcon.Should().Be("⚡ ");

        // 3c. Revert quantity back to 5 -> transitions back to XEM BILL
        vmExported.Orders[0].Lines[0].BilledQuantity = 5;
        vmExported.HasChanges.Should().BeFalse();
        vmExported.IsOpenOnlyState.Should().BeTrue();
        vmExported.ExportButtonText.Should().Be("XEM BILL");

        // 4. Test fallback resolution: simulate old bill without export_file_path in DB, but JPEG exists on disk
        locked.ExportFilePath = null;
        var vmFallback = new CustomerBillReviewViewModel(locked, _customerBillingService, _jpegExporter, null, null, null, _settingsRepo);
        vmFallback.CanViewExportedBill.Should().BeTrue();
        vmFallback.ExportedFilePath.Should().Be(exportedPath);
    }

    [Fact]
    public async Task DetachOrderFromCustomerBillAsync_WhenSingleOrderBill_DeletesBillCompletely()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Studio Detach 1" });
        string folder = Path.Combine(_tempRoot, "2026-09-29", "Studio Detach 1", "In 13x18", "final");
        CreateDummyFiles(folder, 5);
        var orders = await _scanService.ScanDateAsync("2026-09-29");
        var order = orders.Single();

        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var lockedBill = await _customerBillingService.LockBillAsync(draftResult.Draft);

        var billBefore = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        billBefore.Should().NotBeNull();
        billBefore!.Orders.Should().HaveCount(1);

        // Historical snapshot cannot detach until explicitly reopened.
        await Assert.ThrowsAsync<InvalidOperationException>(() => _customerBillingService.DetachOrderFromCustomerBillAsync(order.Id));
        await _customerBillingService.ReopenBillAsync(lockedBill.Id, "Approved detach");
        await _customerBillingService.DetachOrderFromCustomerBillAsync(order.Id);

        // Assert: Since this bill only had 1 order, detaching it leaves 0 orders -> bill is completely deleted
        var billAfter = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        billAfter.Should().BeNull();
    }

    [Fact]
    public async Task DetachOrderFromCustomerBillAsync_WhenMultiOrderBill_RemovesOrderAndRecalculatesTotals()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Studio Detach Multi" });
        await _customerRepo.AddAliasAsync(customer.Id, "Studio Detach Multi 2");

        string folder1 = Path.Combine(_tempRoot, "2026-09-29", "Studio Detach Multi", "In 13x18", "final");
        CreateDummyFiles(folder1, 3); // 3 * 5,000 = 15,000

        string folder2 = Path.Combine(_tempRoot, "2026-09-29", "Studio Detach Multi 2", "In 13x18", "final");
        CreateDummyFiles(folder2, 4); // 4 * 5,000 = 20,000

        var orders = await _scanService.ScanDateAsync("2026-09-29");
        orders.Should().HaveCount(2);

        var order1 = orders.First(o => o.OriginalFolderName == "Studio Detach Multi");
        var order2 = orders.First(o => o.OriginalFolderName == "Studio Detach Multi 2");

        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        draftResult.Draft.Orders.Should().HaveCount(2);
        draftResult.Draft.GrandTotal.Should().Be(35000);

        var lockedBill = await _customerBillingService.LockBillAsync(draftResult.Draft);
        lockedBill.GrandTotal.Should().Be(35000);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _customerBillingService.DetachOrderFromCustomerBillAsync(order2.Id));
        await _customerBillingService.ReopenBillAsync(lockedBill.Id, "Approved detach");
        await _customerBillingService.DetachOrderFromCustomerBillAsync(order2.Id);

        // Assert: Bill remains with order1 only, total is 15,000
        var billAfter = await _customerBillRepo.GetByIdAsync(lockedBill.Id);
        billAfter.Should().NotBeNull();
        billAfter!.Orders.Should().HaveCount(1);
        billAfter.Orders[0].OrderId.Should().Be(order1.Id);
        billAfter.Lines.Should().HaveCount(1);
        billAfter.Lines[0].OrderId.Should().Be(order1.Id);
        billAfter.Lines[0].BilledQuantity.Should().Be(3);
        billAfter.ProductSubtotal.Should().Be(15000);
        billAfter.GrandTotal.Should().Be(15000);
    }

    [Fact]
    public void PaginateBill_SmallBill_ReturnsSinglePage()
    {
        var bill = new CustomerBill
        {
            BillNumber = "BILL-20260930-0001",
            CustomerNameSnapshot = "Khách Hàng Nhỏ",
            Orders = new List<CustomerBillOrder>
            {
                new()
                {
                    OrderNameSnapshot = "Đơn 1",
                    IsIncluded = true,
                    Lines = Enumerable.Range(1, 10).Select(i => new CustomerBillLine
                    {
                        ProductNameSnapshot = $"Ảnh {i}",
                        BilledQuantity = 1,
                        BilledUnitPrice = 5000,
                        IsIncluded = true
                    }).ToList()
                }
            }
        };

        var pages = WpfJpegBillExporter.PaginateBill(bill, 45);
        pages.Should().HaveCount(1);
        pages[0].PageNumber.Should().Be(1);
        pages[0].TotalPages.Should().Be(1);
        pages[0].IsFirstPage.Should().BeTrue();
        pages[0].IsLastPage.Should().BeTrue();

        var fileNames = WpfJpegBillExporter.GetExportPageFileNames(bill, 45);
        fileNames.Should().HaveCount(1);
        fileNames[0].Should().Be("BILL-20260930-0001_Khach-Hang-Nho.jpg");
    }

    [Fact]
    public void PaginateBill_LargeBill_PaginatesCleanlyWithoutOverflow()
    {
        var bill = new CustomerBill
        {
            BillNumber = "BILL-20260930-0002",
            CustomerNameSnapshot = "Studio Lớn Nhiều Đơn",
            Orders = new List<CustomerBillOrder>
            {
                new()
                {
                    OrderNameSnapshot = "Đơn 1",
                    IsIncluded = true,
                    Lines = Enumerable.Range(1, 30).Select(i => new CustomerBillLine
                    {
                        ProductNameSnapshot = $"Ảnh D1-{i}",
                        BilledQuantity = 1,
                        BilledUnitPrice = 5000,
                        IsIncluded = true
                    }).ToList()
                },
                new()
                {
                    OrderNameSnapshot = "Đơn 2",
                    IsIncluded = true,
                    Lines = Enumerable.Range(1, 30).Select(i => new CustomerBillLine
                    {
                        ProductNameSnapshot = $"Ảnh D2-{i}",
                        BilledQuantity = 1,
                        BilledUnitPrice = 5000,
                        IsIncluded = true
                    }).ToList()
                },
                new()
                {
                    OrderNameSnapshot = "Đơn 3",
                    IsIncluded = true,
                    Lines = Enumerable.Range(1, 30).Select(i => new CustomerBillLine
                    {
                        ProductNameSnapshot = $"Ảnh D3-{i}",
                        BilledQuantity = 1,
                        BilledUnitPrice = 5000,
                        IsIncluded = true
                    }).ToList()
                }
            }
        };

        // 90 lines total with max 45 per page -> 2 pages
        var pages = WpfJpegBillExporter.PaginateBill(bill, 45);
        pages.Should().HaveCount(2);
        pages[0].PageNumber.Should().Be(1);
        pages[0].TotalPages.Should().Be(2);
        pages[0].IsFirstPage.Should().BeTrue();
        pages[0].IsLastPage.Should().BeFalse();

        pages[1].PageNumber.Should().Be(2);
        pages[1].TotalPages.Should().Be(2);
        pages[1].IsFirstPage.Should().BeFalse();
        pages[1].IsLastPage.Should().BeTrue();

        var fileNames = WpfJpegBillExporter.GetExportPageFileNames(bill, 45);
        fileNames.Should().HaveCount(2);
        fileNames[0].Should().Be("BILL-20260930-0002_Studio-Lon-Nhieu-Don_Trang1.jpg");
        fileNames[1].Should().Be("BILL-20260930-0002_Studio-Lon-Nhieu-Don_Trang2.jpg");
    }

    [Fact]
    public async Task ExportBillToJpegAsync_LargeBill_RendersMultipleJpegFilesCleanly()
    {
        string artifactDir = Path.Combine(Path.GetTempPath(), "LalabExportTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);

        try
        {
            var bill = new CustomerBill
            {
                BillNumber = "BILL-20260930-0003",
                CustomerNameSnapshot = "Studio Khủng 100 Món",
                Orders = new List<CustomerBillOrder>
                {
                    new()
                    {
                        OrderNameSnapshot = "Đơn Đợt 1",
                        IsIncluded = true,
                        Lines = Enumerable.Range(1, 40).Select(i => new CustomerBillLine
                        {
                            ProductNameSnapshot = $"Ảnh Lụa 13x18 số {i}",
                            BilledQuantity = 2,
                            BilledUnitPrice = 5000,
                            IsIncluded = true
                        }).ToList()
                    },
                    new()
                    {
                        OrderNameSnapshot = "Đơn Đợt 2",
                        IsIncluded = true,
                        Lines = Enumerable.Range(1, 40).Select(i => new CustomerBillLine
                        {
                            ProductNameSnapshot = $"Ảnh Ép Gỗ 20x30 số {i}",
                            BilledQuantity = 1,
                            BilledUnitPrice = 35000,
                            IsIncluded = true
                        }).ToList()
                    },
                    new()
                    {
                        OrderNameSnapshot = "Đơn Đợt 3",
                        IsIncluded = true,
                        Lines = Enumerable.Range(1, 20).Select(i => new CustomerBillLine
                        {
                            ProductNameSnapshot = $"Album Mini {i}",
                            BilledQuantity = 1,
                            BilledUnitPrice = 150000,
                            IsIncluded = true
                        }).ToList()
                    }
                }
            };

            string primaryPath = await _jpegExporter.ExportBillToJpegAsync(bill, artifactDir);

            // Primary path should point to Trang 1
            primaryPath.Should().EndWith("BILL-20260930-0003_Studio-Khung-100-Mon_Trang1.jpg");
            File.Exists(primaryPath).Should().BeTrue();

            // Total 100 items with max 45 per page -> 3 pages
            string page2Path = Path.Combine(artifactDir, "BILL-20260930-0003_Studio-Khung-100-Mon_Trang2.jpg");
            string page3Path = Path.Combine(artifactDir, "BILL-20260930-0003_Studio-Khung-100-Mon_Trang3.jpg");

            File.Exists(page2Path).Should().BeTrue();
            File.Exists(page3Path).Should().BeTrue();

            // Verify each file is a valid JPEG
            foreach (var p in new[] { primaryPath, page2Path, page3Path })
            {
                var bytes = File.ReadAllBytes(p);
                bytes.Length.Should().BeGreaterThan(1000);
                bytes[0].Should().Be(0xFF);
                bytes[1].Should().Be(0xD8);
            }
        }
        finally
        {
            if (Directory.Exists(artifactDir))
            {
                try { Directory.Delete(artifactDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExportBillToJpegAsync_WithCustomerNamedOrder_RendersOrderSubtotalCorrectly()
    {
        string artifactDir = Path.Combine(Path.GetTempPath(), "LalabExportTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDir);

        try
        {
            var bill = new CustomerBill
            {
                BillNumber = "DH-260929-007",
                CustomerNameSnapshot = "Quang Studio",
                PeriodStart = "2026-09-29",
                PeriodEnd = "2026-09-29",
                ProductSubtotal = 1140000,
                GrandTotal = 1140000,
                Orders = new List<CustomerBillOrder>
                {
                    new()
                    {
                        OrderId = 1,
                        OrderCodeSnapshot = "DH-260929-007",
                        OrderNameSnapshot = "Quang Studio",
                        OrderDateSnapshot = "2026-09-29",
                        IsIncluded = true,
                        Subtotal = 1140000,
                        Lines = new List<CustomerBillLine>
                        {
                            new() { ProductNameSnapshot = "13x18 in", BilledQuantity = 10, BilledUnitPrice = 5000, LineTotal = 50000, IsIncluded = true },
                            new() { ProductNameSnapshot = "15x21 in", BilledQuantity = 3, BilledUnitPrice = 10000, LineTotal = 30000, IsIncluded = true },
                            new() { ProductNameSnapshot = "20x30", BilledQuantity = 12, BilledUnitPrice = 15000, LineTotal = 180000, IsIncluded = true },
                            new() { ProductNameSnapshot = "40x60 TG", BilledQuantity = 5, BilledUnitPrice = 80000, LineTotal = 400000, IsIncluded = true },
                            new() { ProductNameSnapshot = "Tranh Mica 50x75", BilledQuantity = 4, BilledUnitPrice = 120000, LineTotal = 480000, IsIncluded = true },
                        }
                    }
                }
            };

            var pages = WpfJpegBillExporter.PaginateBill(bill);
            pages.Should().HaveCount(1);
            pages[0].Orders[0].ShowSubtotalOnThisPage.Should().BeTrue();

            string primaryPath = await _jpegExporter.ExportBillToJpegAsync(bill, artifactDir);
            File.Exists(primaryPath).Should().BeTrue();

            var bytes = File.ReadAllBytes(primaryPath);
            bytes.Length.Should().BeGreaterThan(5000);
            bytes[0].Should().Be(0xFF);
            bytes[1].Should().Be(0xD8);
        }
        finally
        {
            if (Directory.Exists(artifactDir))
            {
                try { Directory.Delete(artifactDir, true); } catch { }
            }
        }
    }


    [Fact]
    public async Task CustomerBillReviewViewModel_ExportShippingLabelAsync_ExportsAndAutoOpensWithoutModal()
    {
        string fakeLabelPath = Path.Combine(_tempRoot, "test_label.png");
        File.WriteAllText(fakeLabelPath, "fake image");

        var dummyExporter = new DummyShippingLabelExporter { ResultPath = fakeLabelPath };
        var draft = new CustomerBill
        {
            BillNumber = "BILL-20261001-0001",
            CustomerNameSnapshot = "Khách Hàng Test Tem",
            ShippingAddressSnapshot = "123 Đường Test"
        };

        await _customerBillRepo.SaveBillAsync(draft);
        var vm = new CustomerBillReviewViewModel(
            draft,
            _customerBillingService,
            _jpegExporter,
            null,
            null,
            null,
            _settingsRepo,
            null,
            _customerBillRepo,
            dummyExporter
        );

        // Act - should complete smoothly without modal confirmation dialog
        await vm.ExportShippingLabelAsync();

        // Assert
        dummyExporter.ExportCalled.Should().BeTrue();
        vm.StatusMessage.Should().Contain("Đã xuất tem dán");
        vm.StatusMessage.Should().Contain("Clipboard");
        vm.IsBusy.Should().BeFalse();
    }

    private class DummyShippingLabelExporter : IShippingLabelExporter
    {
        public bool ExportCalled { get; private set; }
        public string ResultPath { get; set; } = string.Empty;

        public Task<string> ExportShippingLabelImageAsync(CustomerBill bill, string? destinationPath = null, CancellationToken cancellationToken = default)
        {
            ExportCalled = true;
            return Task.FromResult(ResultPath);
        }

        public Task PrintShippingLabelAsync(CustomerBill bill, bool silent = false, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string> ExportOrderShippingLabelImageAsync(Order order, string? destinationPath = null, CancellationToken cancellationToken = default)
        {
            ExportCalled = true;
            return Task.FromResult(ResultPath);
        }

        public Task PrintOrderShippingLabelAsync(Order order, bool silent = false, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PrintTestSampleLabelAsync(string? printerName = null, string? labelSizeOverride = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CustomerBillReviewViewModel_SmartExportButton_OpensOldBillWhenNoChanges_AndReexportsWhenChangedOrForced()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Smart Button Test" });
        string folder = Path.Combine(_tempRoot, "2026-09-29", "Smart Button Test", "In 13x18", "final");
        CreateDummyFiles(folder, 4);
        await _scanService.ScanDateAsync("2026-09-29");

        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var locked = await _customerBillingService.LockBillAsync(draftResult.Draft);
        string exportedPath = await _jpegExporter.ExportBillToJpegAsync(locked);
        await _customerBillingService.RecordBillExportedAsync(locked.Id, exportedPath);

        int exportCount = 0;
        var spyExporter = new SpyJpegExporter(_jpegExporter, () => exportCount++);

        var vm = new CustomerBillReviewViewModel(locked, _customerBillingService, spyExporter, null, null, null, _settingsRepo);
        vm.CustomFileOpener = _ => { };
        vm.CustomPreviewOpener = (_, _) => { };
        vm.IsOpenOnlyState.Should().BeTrue();
        vm.ExportButtonText.Should().Be("XEM BILL");

        // 1. Calling ExportBillAsync when no changes -> should NOT re-export
        await vm.ExportBillCommand.ExecuteAsync(null);
        exportCount.Should().Be(0);

        // 2. Calling ForceReexportBillCommand -> SHOULD re-export even if no changes
        await vm.ForceReexportBillCommand.ExecuteAsync(null);
        exportCount.Should().Be(1);
        vm.IsOpenOnlyState.Should().BeTrue();

        // 3. Make change -> HasChanges becomes true, ExportButtonText becomes "XUẤT LẠI BILL"
        vm.Orders[0].Lines[0].BilledQuantity = 10;
        vm.IsOpenOnlyState.Should().BeFalse();
        vm.ExportButtonText.Should().Be("XUẤT LẠI BILL");

        // Calling ExportBillCommand with changes -> SHOULD re-export
        await vm.ExportBillCommand.ExecuteAsync(null);
        // Re-export always uses the persisted historical snapshot.
        exportCount.Should().Be(2);
        vm.Bill.Lines[0].BilledQuantity.Should().Be(4);
        vm.IsEditable.Should().BeFalse();
        vm.IsOpenOnlyState.Should().BeTrue();
        vm.ExportButtonText.Should().Be("XEM BILL");
    }

    [Fact]
    public async Task CustomerBillReviewViewModel_ExportBillAsync_RendersInMemoryAndOpensPreviewWindowWithoutDiskWrites()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "In Memory Preview Test" });
        string folder = Path.Combine(_tempRoot, "2026-09-29", "In Memory Preview Test", "In 13x18", "final");
        CreateDummyFiles(folder, 3);
        await _scanService.ScanDateAsync("2026-09-29");

        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var draft = draftResult.Draft;

        var vm = new CustomerBillReviewViewModel(draft, _customerBillingService, _jpegExporter, null, null, null, _settingsRepo);
        
        bool previewOpened = false;
        IReadOnlyList<BitmapSource>? capturedBitmaps = null;
        vm.CustomPreviewOpener = (bill, bitmaps) =>
        {
            previewOpened = true;
            capturedBitmaps = bitmaps;
        };

        // Act: Execute Export
        await vm.ExportBillCommand.ExecuteAsync(null);

        // Assert
        previewOpened.Should().BeTrue("Should open preview in memory");
        capturedBitmaps.Should().NotBeNull();
        capturedBitmaps!.Count.Should().Be(1);
        capturedBitmaps[0].PixelWidth.Should().Be(1080);
        capturedBitmaps[0].PixelHeight.Should().BeGreaterThan(500);

        // Verify Bill is marked Exported in DB
        var savedBill = await _customerBillRepo.GetByIdAsync(draft.Id);
        savedBill.Should().NotBeNull();
        savedBill!.Status.Should().Be(CustomerBillStatus.Exported);
        savedBill.ExportedAt.Should().NotBeNull();

        // Verify no unexpected JPEG or Excel files were automatically written to disk
        string defaultExportFolder = (await _settingsRepo.GetSettingsAsync()).BillExportFolder;
        if (Directory.Exists(defaultExportFolder))
        {
            var files = Directory.GetFiles(defaultExportFolder, $"{savedBill.BillNumber}*");
            files.Should().BeEmpty("No JPEG or Excel files should be auto-written to disk");
        }
    }

    [Fact]
    public async Task BillVisualRenderer_StitchBitmapsVertically_ProducesSingleContinuousBitmap()
    {
        var customer = await _customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Stitch Test Customer" });
        string folder = Path.Combine(_tempRoot, "2026-09-29", "Stitch Test Customer", "In 13x18", "final");
        CreateDummyFiles(folder, 2);
        await _scanService.ScanDateAsync("2026-09-29");

        var draftResult = await _customerBillingService.BuildOrRefreshDraftAsync(customer.Id);
        var bitmaps = await _jpegExporter.RenderBillBitmapsAsync(draftResult.Draft);
        bitmaps.Count.Should().BeGreaterThan(0);

        // Simulate 2 pages by passing bitmaps twice
        var pagesToStitch = new List<BitmapSource> { bitmaps[0], bitmaps[0] };
        var stitched = _jpegExporter.StitchBitmapsVertically(pagesToStitch);

        stitched.Should().NotBeNull();
        stitched.PixelWidth.Should().Be(bitmaps[0].PixelWidth);
        stitched.PixelHeight.Should().Be(bitmaps[0].PixelHeight * 2);
    }

    private class SpyJpegExporter : IJpegBillExporter, IBillVisualRenderer
    {
        private readonly IJpegBillExporter _inner;
        private readonly Action _onExport;

        public SpyJpegExporter(IJpegBillExporter inner, Action onExport)
        {
            _inner = inner;
            _onExport = onExport;
        }

        public Task<string> ExportBillToJpegAsync(CustomerBill bill, string? destinationDirectory = null, System.Threading.CancellationToken cancellationToken = default)
        {
            _onExport();
            return _inner.ExportBillToJpegAsync(bill, destinationDirectory, cancellationToken);
        }

        public Task<IReadOnlyList<BitmapSource>> RenderBillBitmapsAsync(CustomerBill bill, System.Threading.CancellationToken cancellationToken = default)
        {
            _onExport();
            if (_inner is IBillVisualRenderer renderer)
            {
                return renderer.RenderBillBitmapsAsync(bill, cancellationToken);
            }
            return Task.FromResult<IReadOnlyList<BitmapSource>>(Array.Empty<BitmapSource>());
        }

        public Task<string> SaveBitmapToJpegAsync(BitmapSource bitmap, string destinationFilePath, int quality = 95, System.Threading.CancellationToken cancellationToken = default)
        {
            if (_inner is IBillVisualRenderer renderer)
            {
                return renderer.SaveBitmapToJpegAsync(bitmap, destinationFilePath, quality, cancellationToken);
            }
            return Task.FromResult(destinationFilePath);
        }

        public BitmapSource StitchBitmapsVertically(IReadOnlyList<BitmapSource> pages)
        {
            if (_inner is IBillVisualRenderer renderer)
            {
                return renderer.StitchBitmapsVertically(pages);
            }
            return pages.First();
        }
    }
}



