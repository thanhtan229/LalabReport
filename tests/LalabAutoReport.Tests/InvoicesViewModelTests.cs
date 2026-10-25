using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.UI.ViewModels;
using Xunit;

namespace LalabAutoReport.Tests;

public class InvoicesViewModelTests
{
    private class FakeCustomerBillRepo : ICustomerBillRepository
    {
        public List<CustomerBill> Store { get; set; } = new();

        public Task<CustomerBill?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
            => Task.FromResult(Store.FirstOrDefault(b => b.Id == id));

        public Task<CustomerBill?> GetByBillNumberAsync(string billNumber, CancellationToken cancellationToken = default)
            => Task.FromResult(Store.FirstOrDefault(b => b.BillNumber == billNumber));

        public Task<IReadOnlyList<CustomerBill>> GetAllBillsAsync(BillType? typeFilter = null, CancellationToken cancellationToken = default)
        {
            var res = Store.AsEnumerable();
            if (typeFilter.HasValue) res = res.Where(b => b.BillType == typeFilter.Value);
            return Task.FromResult<IReadOnlyList<CustomerBill>>(res.ToList());
        }

        public Task<IReadOnlyList<CustomerBill>> GetBillsByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomerBill>>(Store.Where(b => b.CustomerId == customerId).ToList());

        public Task<CustomerBill?> GetLastLockedOrExportedBillByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Store.LastOrDefault(b => b.CustomerId == customerId && b.Status != CustomerBillStatus.Draft));

        public Task<CustomerBill?> GetLockedBillByOrderIdAsync(long orderId, CancellationToken cancellationToken = default)
            => Task.FromResult<CustomerBill?>(null);

        public Task<CustomerBill?> GetActiveDraftByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default)
            => Task.FromResult<CustomerBill?>(null);

        public Task<IReadOnlyList<CustomerBill>> GetBillsBySourceFolderPathAsync(string normalizedFolderPath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomerBill>>(new List<CustomerBill>());

        public Task<IReadOnlyList<GuestBillSourceFolder>> GetSourceFoldersByBillIdAsync(long billId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GuestBillSourceFolder>>(new List<GuestBillSourceFolder>());

        public Task SaveBillAsync(CustomerBill bill, CancellationToken cancellationToken = default)
        {
            int idx = Store.FindIndex(b => b.Id == bill.Id);
            if (idx >= 0) Store[idx] = bill;
            else Store.Add(bill);
            return Task.CompletedTask;
        }

        public Task LockCustomerBillAtomicAsync(CustomerBill bill, IReadOnlyList<long> productJobIds, IReadOnlyList<long> orderIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReopenCustomerBillAtomicAsync(CustomerBill bill, IReadOnlyList<long> productJobIds, IReadOnlyList<long> orderIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteDraftBillAsync(long billId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteBillAsync(long billId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CustomerBill>> GetBillsByDateAsync(string date, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomerBill>>(Store.Where(b => b.PeriodStart.StartsWith(date) || b.PeriodEnd.StartsWith(date)).ToList());

        public Task<IReadOnlyList<CustomerBill>> GetBillsByMonthAsync(string yearMonth, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomerBill>>(Store.Where(b => b.PeriodStart.StartsWith(yearMonth) || b.PeriodEnd.StartsWith(yearMonth)).ToList());

        public Task<IReadOnlyList<CustomerBill>> GetBillsByDateRangeAsync(string startDate, string endDate, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomerBill>>(Store.ToList());

        public Task<string> GenerateNextBillNumberAsync(string date, CancellationToken cancellationToken = default)
            => Task.FromResult("BILL-TEST-001");

        public Task SetPaymentStatusAsync(long billId, bool isPaid, DateTimeOffset? paidAt = null, CancellationToken cancellationToken = default)
        {
            var b = Store.FirstOrDefault(x => x.Id == billId);
            if (b != null)
            {
                b.IsPaid = isPaid;
                b.PaidAt = paidAt;
            }
            return Task.CompletedTask;
        }

        public Task<long> GetCustomerTotalDebtAsync(long customerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Store.Where(b => b.CustomerId == customerId && !b.IsPaid).Sum(b => b.GrandTotal));

        public Task<IReadOnlyList<CustomerBill>> GetUnpaidBillsAsync(long? customerId = null, CancellationToken cancellationToken = default)
        {
            var res = Store.Where(b => !b.IsPaid);
            if (customerId.HasValue) res = res.Where(b => b.CustomerId == customerId.Value);
            return Task.FromResult<IReadOnlyList<CustomerBill>>(res.ToList());
        }
    }

    private class FakeCustomerBillingService : ICustomerBillingService
    {
        public Task<CustomerUnbilledSummary> GetCustomerUnbilledSummaryAsync(long customerId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerUnbilledSummary(customerId, "Test", 0, 0, 0, null, null, false));
        public Task<CustomerBillDraftResult> BuildOrRefreshDraftAsync(long customerId, bool forceRescan = true, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerBillDraftResult(new CustomerBill(), new List<string>(), new List<string>()));
        public Task<IReadOnlyList<DuplicateFolderWarning>> CheckDuplicateSourceFoldersAsync(IEnumerable<string> folderPaths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DuplicateFolderWarning>>(new List<DuplicateFolderWarning>());
        public Task<CustomerBillDraftResult> BuildGuestBillDraftAsync(IReadOnlyList<string> sourceFolderPaths, string? customGuestName = null, long? existingDraftId = null, bool persistDraft = false, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerBillDraftResult(new CustomerBill { BillType = BillType.Guest }, new List<string>(), new List<string>()));
        public Task<Customer> ConvertGuestBillToCustomerAsync(long billId, string customerCanonicalName, CancellationToken cancellationToken = default)
            => Task.FromResult(new Customer { Id = 1, CanonicalName = customerCanonicalName });
        public CustomerBill RecalculateTotals(CustomerBill bill) => bill;
        public Task<CustomerBill> LockBillAsync(CustomerBill draft, CancellationToken cancellationToken = default) => Task.FromResult(draft);
        public bool ReopenCalled { get; private set; }
        public string? LastReopenReason { get; private set; }
        public Task ReopenBillAsync(long billId, string reason, CancellationToken cancellationToken = default)
        {
            ReopenCalled = true;
            LastReopenReason = reason;
            return Task.CompletedTask;
        }
        public Task RecordBillExportedAsync(long billId, string exportFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DetachOrderFromCustomerBillAsync(long orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SyncBillWithScannedOrderAsync(Order scannedOrder, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CustomerBill> SplitOrdersToNewBillAsync(long currentBillId, IReadOnlyList<long> orderIdsToMove, CancellationToken cancellationToken = default) => Task.FromResult(new CustomerBill());
    }

    private class FakeJpegExporter : IJpegBillExporter
    {
        public Task<string> ExportBillToJpegAsync(CustomerBill bill, string? destinationDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult(@"C:\FakePath\Bill.jpg");
    }

    private class FakeSettingsRepo : ISettingsRepository
    {
        public AppSettings CurrentSettings { get; set; } = new() { RootFolder = @"C:\FakeRoot" };
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentSettings);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            CurrentSettings = settings;
            return Task.CompletedTask;
        }
    }

private class FakeThumbnailService : IThumbnailService
    {
        public Task<ThumbnailResult> GetThumbnailAsync(string fullSourcePath, CancellationToken cancellationToken = default) => Task.FromResult(ThumbnailResult.NotFound());
        public Task TouchLastUsedAsync(string fullSourcePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TouchLastUsedBatchAsync(IEnumerable<string> fullSourcePaths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Pause() { }
        public void Resume() { }
        public void ClearMemoryCache() { }
        public void ClearDiskCache() { }
        public long GetDiskCacheSizeBytes() => 1024 * 1024;
        public void CleanupExpiredDiskCache(TimeSpan maxAge) { }
        public Task<ThumbnailCleanupSummary> CleanupExpiredDiskCacheAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
            => Task.FromResult(new ThumbnailCleanupSummary { FilesDeleted = 5, BytesFreed = 5 * 1024 * 1024 });
        public Task<ThumbnailClearSummary> ClearAllDiskCacheAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ThumbnailClearSummary { FilesDeleted = 10, BytesFreed = 10 * 1024 * 1024 });
    }

    private class FakeCloudSyncService : ICloudSyncService
    {
        public bool IsSyncing => false;
        public DateTimeOffset? LastSyncTime => null;
        public string? LastSyncStatus => null;
        public event EventHandler? SyncStatusChanged { add { } remove { } }

        public bool CleanupCalled { get; private set; }
        public int LastRetentionDays { get; private set; }
        public bool LastClearAll { get; private set; }
        public bool ThrowOnCleanup { get; set; }

        public Task<CloudSyncResult> SyncAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CloudSyncResult { Success = true });
        public Task<CloudSyncResult> SyncDateAsync(string date, CancellationToken cancellationToken = default) => Task.FromResult(new CloudSyncResult { Success = true });
        public Task<CloudSyncResult> SyncBillAsync(long billId, CancellationToken cancellationToken = default) => Task.FromResult(new CloudSyncResult { Success = true });
        public Task<CloudSyncResult> UploadBillJpegAsync(long billId, CancellationToken cancellationToken = default) => Task.FromResult(new CloudSyncResult { Success = true });
        public Task<int> PullRemoteChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<int> ProcessPendingPrintCommandsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> CleanupCloudGalleryCacheAsync(int retentionDays, bool clearAll = false, CancellationToken cancellationToken = default)
        {
            if (ThrowOnCleanup) throw new InvalidOperationException("Simulated cloud failure");
            CleanupCalled = true;
            LastRetentionDays = retentionDays;
            LastClearAll = clearAll;
            return Task.FromResult(true);
        }
    }

    private class FakeCustomerRepo : ICustomerRepository
    {
        public List<Customer> Customers { get; set; } = new();

        public Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Customer>>(Customers);

        public Task<Customer?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
            => Task.FromResult(Customers.FirstOrDefault(c => c.Id == id));

        public Task<Customer?> FindExactMatchAsync(string normalizedName, CancellationToken cancellationToken = default)
            => Task.FromResult(Customers.FirstOrDefault(c => c.CanonicalName.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)));

        public Task<Customer> CreateCustomerAsync(Customer customer, string? initialAlias = null, CancellationToken cancellationToken = default)
        {
            customer.Id = Customers.Count + 1;
            Customers.Add(customer);
            return Task.FromResult(customer);
        }

        public Task UpdateCustomerAsync(Customer customer, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteCustomerAsync(long id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AddAliasAsync(long customerId, string aliasText, CancellationToken cancellationToken = default)
        {
            var cust = Customers.FirstOrDefault(c => c.Id == customerId);
            if (cust != null)
            {
                cust.Aliases.Add(new CustomerAlias { Id = cust.Aliases.Count + 1, CustomerId = customerId, AliasText = aliasText, NormalizedAlias = aliasText.ToLowerInvariant() });
            }
            return Task.CompletedTask;
        }

        public Task UpdateAliasAsync(long aliasId, string newAliasText, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAliasAsync(long aliasId, CancellationToken cancellationToken = default)
        {
            foreach (var c in Customers) c.Aliases.RemoveAll(a => a.Id == aliasId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CustomerAlias>> GetAllAliasesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomerAlias>>(Customers.SelectMany(c => c.Aliases).ToList());

        public Task<CustomerAlias?> FindAliasByTextAsync(string aliasText, CancellationToken cancellationToken = default)
            => Task.FromResult(Customers.SelectMany(c => c.Aliases).FirstOrDefault(a => a.AliasText.Equals(aliasText, StringComparison.OrdinalIgnoreCase)));

        public Task ReassignAliasAsync(long aliasId, long newCustomerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> HasCustomerHistoryAsync(long customerId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    [Fact]
    public async Task InvoicesViewModel_LoadsInvoices_CalculatesFinancialMetricsAccurately()
    {
        var billRepo = new FakeCustomerBillRepo
        {
            Store = new List<CustomerBill>
            {
                new CustomerBill
                {
                    Id = 1,
                    BillNumber = "BILL-001",
                    CustomerNameSnapshot = "Studio Mai",
                    BillType = BillType.Customer,
                    GrandTotal = 500000,
                    IsPaid = true,
                    PeriodStart = "2026-10-01",
                    PeriodEnd = "2026-10-01"
                },
                new CustomerBill
                {
                    Id = 2,
                    BillNumber = "BILL-002",
                    CustomerNameSnapshot = "Anh Nam Khách Lẻ",
                    BillType = BillType.Guest,
                    GrandTotal = 300000,
                    IsPaid = false,
                    PeriodStart = "2026-10-01",
                    PeriodEnd = "2026-10-01"
                },
                new CustomerBill
                {
                    Id = 3,
                    BillNumber = "BILL-003",
                    CustomerNameSnapshot = "Studio Hoàng",
                    BillType = BillType.Customer,
                    GrandTotal = 1200000,
                    IsPaid = false,
                    PeriodStart = "2026-10-01",
                    PeriodEnd = "2026-10-01"
                }
            }
        };

        var vm = new InvoicesViewModel(billRepo, new FakeCustomerBillingService(), new FakeJpegExporter(), new FakeSettingsRepo());

        await vm.LoadInvoicesAsync();

        vm.TotalBillsCount.Should().Be(3);
        vm.TotalRevenue.Should().Be(2000000);
        vm.TotalPaidAmount.Should().Be(500000);
        vm.PaidBillsCount.Should().Be(1);
        vm.TotalDebtAmount.Should().Be(1500000);
        vm.UnpaidBillsCount.Should().Be(2);
        vm.HasNoInvoicesFound.Should().BeFalse();
    }

    [Fact]
    public async Task InvoicesViewModel_FiltersByPaymentStatus_ShowsOnlyUnpaidOrPaid()
    {
        var billRepo = new FakeCustomerBillRepo
        {
            Store = new List<CustomerBill>
            {
                new CustomerBill { Id = 1, BillNumber = "BILL-001", GrandTotal = 100000, IsPaid = true, PeriodStart = "2026-10-01", PeriodEnd = "2026-10-01" },
                new CustomerBill { Id = 2, BillNumber = "BILL-002", GrandTotal = 200000, IsPaid = false, PeriodStart = "2026-10-01", PeriodEnd = "2026-10-01" }
            }
        };

        var vm = new InvoicesViewModel(billRepo, new FakeCustomerBillingService(), new FakeJpegExporter(), new FakeSettingsRepo());
        await vm.LoadInvoicesAsync();

        // Filter: Unpaid (Chưa thanh toán)
        vm.PaymentFilter = "Unpaid";
        vm.FilteredInvoices.Should().HaveCount(1);
        vm.FilteredInvoices.First().BillNumber.Should().Be("BILL-002");
        vm.TotalRevenue.Should().Be(200000);
        vm.TotalDebtAmount.Should().Be(200000);

        // Filter: Paid (Đã thanh toán)
        vm.PaymentFilter = "Paid";
        vm.FilteredInvoices.Should().HaveCount(1);
        vm.FilteredInvoices.First().BillNumber.Should().Be("BILL-001");
        vm.TotalRevenue.Should().Be(100000);
        vm.TotalPaidAmount.Should().Be(100000);
    }

    [Fact]
    public async Task InvoicesViewModel_FiltersByBillTypeAndSearchText()
    {
        var billRepo = new FakeCustomerBillRepo
        {
            Store = new List<CustomerBill>
            {
                new CustomerBill { Id = 1, BillNumber = "BILL-001", CustomerNameSnapshot = "Studio Mimosa", PhoneSnapshot = "0901234567", BillType = BillType.Customer, GrandTotal = 400000, PeriodStart = "2026-10-01", PeriodEnd = "2026-10-01" },
                new CustomerBill { Id = 2, BillNumber = "BILL-002", CustomerNameSnapshot = "Khách Lẻ - Nam", PhoneSnapshot = "0987654321", BillType = BillType.Guest, GrandTotal = 150000, PeriodStart = "2026-10-01", PeriodEnd = "2026-10-01" }
            }
        };

        var vm = new InvoicesViewModel(billRepo, new FakeCustomerBillingService(), new FakeJpegExporter(), new FakeSettingsRepo());
        await vm.LoadInvoicesAsync();

        // 1. Filter by BillType: Guest
        vm.BillTypeFilter = "Guest";
        vm.FilteredInvoices.Should().HaveCount(1);
        vm.FilteredInvoices.First().BillNumber.Should().Be("BILL-002");

        // 2. Filter by BillType: Customer
        vm.BillTypeFilter = "Customer";
        vm.FilteredInvoices.Should().HaveCount(1);
        vm.FilteredInvoices.First().BillNumber.Should().Be("BILL-001");

        // 3. Reset BillType and Search
        vm.BillTypeFilter = "All";
        vm.SearchText = "0987";
        vm.FilteredInvoices.Should().HaveCount(1);
        vm.FilteredInvoices.First().CustomerNameSnapshot.Should().Be("Khách Lẻ - Nam");
    }

    [Fact]
    public async Task InvoicesViewModel_TogglePaymentStatus_TogglesStateAndRecalculatesDebt()
    {
        var targetBill = new CustomerBill
        {
            Id = 10,
            BillNumber = "BILL-010",
            CustomerNameSnapshot = "Anh Tuấn",
            GrandTotal = 750000,
            IsPaid = false,
            PeriodStart = "2026-10-01",
            PeriodEnd = "2026-10-01"
        };

        var billRepo = new FakeCustomerBillRepo
        {
            Store = new List<CustomerBill> { targetBill }
        };

        var vm = new InvoicesViewModel(billRepo, new FakeCustomerBillingService(), new FakeJpegExporter(), new FakeSettingsRepo());
        await vm.LoadInvoicesAsync();

        vm.TotalDebtAmount.Should().Be(750000);
        vm.TotalPaidAmount.Should().Be(0);

        // Click to pay
        await vm.TogglePaymentStatusAsync(targetBill);

        targetBill.IsPaid.Should().BeTrue();
        targetBill.PaidAt.Should().NotBeNull();
        vm.TotalDebtAmount.Should().Be(0);
        vm.TotalPaidAmount.Should().Be(750000);
        vm.PaidBillsCount.Should().Be(1);
        vm.UnpaidBillsCount.Should().Be(0);

        // Click again to un-pay
        await vm.TogglePaymentStatusAsync(targetBill);

        targetBill.IsPaid.Should().BeFalse();
        targetBill.PaidAt.Should().BeNull();
        vm.TotalDebtAmount.Should().Be(750000);
        vm.TotalPaidAmount.Should().Be(0);
    }

    [Fact]
    public async Task InvoicesViewModel_FilterByCustomer_UpdatesListAndKpiMetrics()
    {
        var custRepo = new FakeCustomerRepo
        {
            Customers = new List<Customer>
            {
                new Customer { Id = 10, CanonicalName = "Studio Mai" },
                new Customer { Id = 20, CanonicalName = "Studio Hoàng" }
            }
        };

        var billRepo = new FakeCustomerBillRepo
        {
            Store = new List<CustomerBill>
            {
                new CustomerBill
                {
                    Id = 1,
                    BillNumber = "BILL-001",
                    CustomerId = 10,
                    CustomerNameSnapshot = "Studio Mai",
                    BillType = BillType.Customer,
                    GrandTotal = 500000,
                    IsPaid = true,
                    PeriodStart = "2026-10-01",
                    PeriodEnd = "2026-10-01"
                },
                new CustomerBill
                {
                    Id = 2,
                    BillNumber = "BILL-002",
                    CustomerId = 20,
                    CustomerNameSnapshot = "Studio Hoàng",
                    BillType = BillType.Customer,
                    GrandTotal = 1200000,
                    IsPaid = false,
                    PeriodStart = "2026-10-01",
                    PeriodEnd = "2026-10-01"
                },
                new CustomerBill
                {
                    Id = 3,
                    BillNumber = "BILL-003",
                    CustomerId = null,
                    CustomerNameSnapshot = "Khách Lẻ - Anh Hùng",
                    BillType = BillType.Guest,
                    GrandTotal = 300000,
                    IsPaid = false,
                    PeriodStart = "2026-10-01",
                    PeriodEnd = "2026-10-01"
                }
            }
        };

        var vm = new InvoicesViewModel(billRepo, new FakeCustomerBillingService(), new FakeJpegExporter(), new FakeSettingsRepo(), custRepo);
        await vm.LoadInvoicesAsync();

        // 1. Initial State: All customers loaded
        vm.CustomerFilterOptions.Should().HaveCount(4); // "Tất cả khách hàng", "⚡ Tất cả khách lẻ", "Studio Hoàng", "Studio Mai"
        vm.FilteredInvoices.Should().HaveCount(3);
        vm.TotalRevenue.Should().Be(2000000);
        vm.TotalDebtAmount.Should().Be(1500000);

        // 2. Filter by Customer 10 (Studio Mai)
        vm.FilterByCustomer(10);
        vm.FilteredInvoices.Should().HaveCount(1);
        vm.FilteredInvoices.First().BillNumber.Should().Be("BILL-001");
        vm.TotalRevenue.Should().Be(500000);
        vm.TotalPaidAmount.Should().Be(500000);
        vm.TotalDebtAmount.Should().Be(0);

        // 3. Filter by Customer 20 (Studio Hoàng)
        vm.FilterByCustomer(20);
        vm.FilteredInvoices.Should().HaveCount(1);
        vm.FilteredInvoices.First().BillNumber.Should().Be("BILL-002");
        vm.TotalRevenue.Should().Be(1200000);
        vm.TotalPaidAmount.Should().Be(0);
        vm.TotalDebtAmount.Should().Be(1200000);

        // 4. Filter by Guest
        vm.FilterByCustomer(-1, isGuest: true);
        vm.FilteredInvoices.Should().HaveCount(1);
        vm.FilteredInvoices.First().BillNumber.Should().Be("BILL-003");
        vm.TotalRevenue.Should().Be(300000);
        vm.TotalDebtAmount.Should().Be(300000);

        // 5. Clear filter
        vm.ClearCustomerFilterCommand.Execute(null);
        vm.FilteredInvoices.Should().HaveCount(3);
        vm.TotalRevenue.Should().Be(2000000);
    }

    [Fact]
    public async Task InvoicesViewModel_QuickFilterByCustomer_SetsCustomerFilterImmediately()
    {
        var custRepo = new FakeCustomerRepo
        {
            Customers = new List<Customer>
            {
                new Customer { Id = 10, CanonicalName = "Studio Mai" }
            }
        };

        var bill1 = new CustomerBill
        {
            Id = 1,
            BillNumber = "BILL-001",
            CustomerId = 10,
            CustomerNameSnapshot = "Studio Mai",
            BillType = BillType.Customer,
            GrandTotal = 500000
        };
        var bill2 = new CustomerBill
        {
            Id = 2,
            BillNumber = "BILL-002",
            CustomerId = null,
            CustomerNameSnapshot = "Khách Lẻ - Anh An",
            BillType = BillType.Guest,
            GrandTotal = 250000
        };

        var billRepo = new FakeCustomerBillRepo
        {
            Store = new List<CustomerBill> { bill1, bill2 }
        };

        var vm = new InvoicesViewModel(billRepo, new FakeCustomerBillingService(), new FakeJpegExporter(), new FakeSettingsRepo(), custRepo);
        await vm.LoadInvoicesAsync();

        // Click customer 10 bill
        vm.QuickFilterByCustomerCommand.Execute(bill1);
        vm.SelectedCustomerFilter.Should().NotBeNull();
        vm.SelectedCustomerFilter!.CustomerId.Should().Be(10);
        vm.FilteredInvoices.Should().HaveCount(1);

        // Click guest bill
        vm.QuickFilterByCustomerCommand.Execute(bill2);
        vm.SelectedCustomerFilter.Should().NotBeNull();
        vm.SelectedCustomerFilter!.IsGuest.Should().BeTrue();
        vm.FilteredInvoices.Should().HaveCount(1);
    }

    [Fact]
    public async Task CustomersViewModel_TransactionSummaryAndNavigationEvent_WorksProperly()
    {
        var custRepo = new FakeCustomerRepo
        {
            Customers = new List<Customer>
            {
                new Customer { Id = 10, CanonicalName = "Studio Bình Minh" }
            }
        };

        var billRepo = new FakeCustomerBillRepo
        {
            Store = new List<CustomerBill>
            {
                new CustomerBill
                {
                    Id = 1,
                    BillNumber = "BILL-01",
                    CustomerId = 10,
                    GrandTotal = 600000,
                    IsPaid = true
                },
                new CustomerBill
                {
                    Id = 2,
                    BillNumber = "BILL-02",
                    CustomerId = 10,
                    GrandTotal = 400000,
                    IsPaid = false
                }
            }
        };

        var settingsRepo = new FakeSettingsRepo();
        var vm = new CustomersViewModel(custRepo, new FakeCustomerBillingService(), billRepo, new FakeJpegExporter(), settingsRepo);
        await vm.LoadCustomersAsync();

        vm.SelectedCustomer = vm.AllCustomers.First(c => c.Id == 10);
        await vm.RefreshBillsAsync();

        vm.CustomerTotalBillsCount.Should().Be(2);
        vm.CustomerTotalSpentAmount.Should().Be(1000000);
        vm.CustomerTotalDebtAmount.Should().Be(400000);
        vm.HasCustomerHistory.Should().BeTrue();

        // Test navigation event
        long receivedCustomerId = 0;
        vm.ViewCustomerInvoicesRequested += id => receivedCustomerId = id;

        vm.ViewCustomerInvoicesCommand.Execute(null);
        receivedCustomerId.Should().Be(10);
    }

    [Fact]
    public async Task MainViewModel_OnViewCustomerInvoicesRequested_SwitchesTabAndFilters()
    {
        var custRepo = new FakeCustomerRepo
        {
            Customers = new List<Customer>
            {
                new Customer { Id = 10, CanonicalName = "Studio Bình Minh" }
            }
        };

        var billRepo = new FakeCustomerBillRepo
        {
            Store = new List<CustomerBill>
            {
                new CustomerBill { Id = 1, BillNumber = "BILL-01", CustomerId = 10, GrandTotal = 500000 }
            }
        };

        var settingsRepo = new FakeSettingsRepo();
        var billingService = new FakeCustomerBillingService();
        var jpegExporter = new FakeJpegExporter();

        var customersVm = new CustomersViewModel(custRepo, billingService, billRepo, jpegExporter, settingsRepo);
        var invoicesVm = new InvoicesViewModel(billRepo, billingService, jpegExporter, settingsRepo, custRepo);

        var mainVm = new MainViewModel(
            null!,
            invoicesVm,
            null!,
            null!,
            customersVm,
            null!,
            settingsRepo
        );

        mainVm.ActiveTab.Should().Be("Dashboard");

        await customersVm.LoadCustomersAsync();
        customersVm.SelectedCustomer = customersVm.AllCustomers.First(c => c.Id == 10);

        // Execute ViewCustomerInvoicesCommand on CustomersViewModel
        customersVm.ViewCustomerInvoicesCommand.Execute(null);

        // Give async void event handler a moment to execute
        await Task.Delay(50);

        mainVm.ActiveTab.Should().Be("Invoices");
        mainVm.CurrentView.Should().Be(invoicesVm);
        invoicesVm.SelectedCustomerFilter.Should().NotBeNull();
        invoicesVm.SelectedCustomerFilter!.CustomerId.Should().Be(10);
        invoicesVm.FilteredInvoices.Should().HaveCount(1);
    }

    [Fact]
    public async Task CustomerBillReviewViewModel_ReopenBill_RestoresDraftAndResetsExportButtonState()
    {
        var bill = new CustomerBill
        {
            Id = 99,
            BillNumber = "BILL-REOPEN-TEST",
            BillType = BillType.Customer,
            CustomerId = 1,
            CustomerNameSnapshot = "Test Customer",
            Status = CustomerBillStatus.Exported,
            ExportFilePath = string.Empty,
            LockedAt = DateTimeOffset.UtcNow,
            ExportedAt = DateTimeOffset.UtcNow,
            GrandTotal = 100000,
            ProductSubtotal = 100000
        };

        var billRepo = new FakeCustomerBillRepo();
        var billingService = new FakeCustomerBillingService();
        var jpegExporter = new FakeJpegExporter();
        var settingsRepo = new FakeSettingsRepo();

        var vm = new CustomerBillReviewViewModel(
            bill,
            billingService,
            jpegExporter,
            null,
            null,
            null,
            settingsRepo,
            null,
            billRepo
        );

        // Initially exported / locked
        vm.IsLocked.Should().BeTrue();
        vm.IsEditable.Should().BeFalse();
        vm.CanViewExportedBill.Should().BeTrue();
        vm.IsOpenOnlyState.Should().BeTrue();
        vm.ExportButtonText.Should().Be("XEM BILL");
        vm.ExportButtonIcon.Should().Be("🖼️ ");

        // Action: Reopen bill with reason and Yes confirmation
        vm.ReopenReasonFunc = (_, _) => "   Sửa giá theo thỏa thuận   ";
        vm.ConfirmFunc = (_, _, _, _) => System.Windows.MessageBoxResult.Yes;
        await vm.ReopenBillCommand.ExecuteAsync(null);

        // Assert: Billing service called with trimmed reason
        billingService.ReopenCalled.Should().BeTrue();
        billingService.LastReopenReason.Should().Be("Sửa giá theo thỏa thuận");

        // Assert: Restored to editable draft state
        vm.IsLocked.Should().BeFalse();
        vm.IsEditable.Should().BeTrue();
        vm.Bill.Status.Should().Be(CustomerBillStatus.Draft);
        vm.Bill.LockedAt.Should().BeNull();
        vm.Bill.ExportedAt.Should().BeNull();

        // Assert: Export button properly reset to primary orange export mode
        vm.CanViewExportedBill.Should().BeFalse();
        vm.IsOpenOnlyState.Should().BeFalse();
        vm.ExportButtonText.Should().Be("XUẤT BILL");
        vm.ExportButtonIcon.Should().Be("⚡ ");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CustomerBillReviewViewModel_ReopenBill_CanceledBecauseNoReason_RemainsLocked(string? emptyReason)
    {
        var bill = new CustomerBill
        {
            Id = 101,
            BillNumber = "BILL-NO-REASON",
            BillType = BillType.Customer,
            CustomerId = 1,
            CustomerNameSnapshot = "Test Customer",
            Status = CustomerBillStatus.Locked,
            LockedAt = DateTimeOffset.UtcNow,
            GrandTotal = 50000,
            ProductSubtotal = 50000
        };

        var billRepo = new FakeCustomerBillRepo();
        var billingService = new FakeCustomerBillingService();
        var jpegExporter = new FakeJpegExporter();
        var settingsRepo = new FakeSettingsRepo();

        var vm = new CustomerBillReviewViewModel(
            bill,
            billingService,
            jpegExporter,
            null,
            null,
            null,
            settingsRepo,
            null,
            billRepo
        );

        vm.IsLocked.Should().BeTrue();
        vm.ReopenReasonFunc = (_, _) => emptyReason;
        vm.ConfirmFunc = (_, _, _, _) => System.Windows.MessageBoxResult.Yes;

        await vm.ReopenBillCommand.ExecuteAsync(null);

        // Assert: Billing service was never called, remains locked
        billingService.ReopenCalled.Should().BeFalse();
        vm.IsLocked.Should().BeTrue();
        vm.IsEditable.Should().BeFalse();
        vm.Bill.Status.Should().Be(CustomerBillStatus.Locked);
        vm.Bill.LockedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CustomerBillReviewViewModel_ReopenBill_CanceledBecauseDeclinedConfirmation_RemainsLocked()
    {
        var bill = new CustomerBill
        {
            Id = 100,
            BillNumber = "BILL-REOPEN-CANCEL",
            BillType = BillType.Customer,
            CustomerId = 1,
            CustomerNameSnapshot = "Test Customer",
            Status = CustomerBillStatus.Locked,
            LockedAt = DateTimeOffset.UtcNow,
            GrandTotal = 50000,
            ProductSubtotal = 50000
        };

        var billRepo = new FakeCustomerBillRepo();
        var billingService = new FakeCustomerBillingService();
        var jpegExporter = new FakeJpegExporter();
        var settingsRepo = new FakeSettingsRepo();

        var vm = new CustomerBillReviewViewModel(
            bill,
            billingService,
            jpegExporter,
            null,
            null,
            null,
            settingsRepo,
            null,
            billRepo
        );

        vm.IsLocked.Should().BeTrue();
        // Action: User enters reason but declines confirmation
        vm.ReopenReasonFunc = (_, _) => "Sửa số lượng thực in";
        vm.ConfirmFunc = (_, _, _, _) => System.Windows.MessageBoxResult.No;
        await vm.ReopenBillCommand.ExecuteAsync(null);

        // Assert: Billing service never called, remains locked
        billingService.ReopenCalled.Should().BeFalse();
        vm.IsLocked.Should().BeTrue();
        vm.IsEditable.Should().BeFalse();
        vm.Bill.Status.Should().Be(CustomerBillStatus.Locked);
        vm.Bill.LockedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SettingsViewModel_CleanupExpiredThumbnailsNow_WhenCloudSyncEnabled_CallsCloudCleanupAndPersistsTimestamp()
    {
        var settingsRepo = new FakeSettingsRepo
        {
            CurrentSettings = new AppSettings
            {
                RootFolder = @"C:\FakeRoot",
                EnableCloudSync = true,
                ThumbnailRetentionDays = 60
            }
        };

        var fakeThumbnails = new FakeThumbnailService();
        var fakeCloudSync = new FakeCloudSyncService();

        var vm = new SettingsViewModel(
            settingsRepo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            thumbnailService: fakeThumbnails,
            cloudSyncService: fakeCloudSync
        );

        await vm.CleanupExpiredThumbnailsNowCommand.ExecuteAsync(null);

        fakeCloudSync.CleanupCalled.Should().BeTrue();
        fakeCloudSync.LastRetentionDays.Should().Be(60);
        fakeCloudSync.LastClearAll.Should().BeFalse();
        settingsRepo.CurrentSettings.LastThumbnailCleanupAt.Should().NotBeNullOrWhiteSpace();
        vm.ThumbnailCacheStatusMessage.Should().Contain("Đã dọn dẹp xong: Đã xóa 5 ảnh thumbnail quá hạn")
            .And.Contain("Đồng thời đã dọn ảnh quá hạn trên Cloud");
    }

    [Fact]
    public async Task SettingsViewModel_CleanupExpiredThumbnailsNow_WhenCloudFails_DoesNotFailLocalCleanup()
    {
        var settingsRepo = new FakeSettingsRepo
        {
            CurrentSettings = new AppSettings
            {
                RootFolder = @"C:\FakeRoot",
                EnableCloudSync = true,
                ThumbnailRetentionDays = 30
            }
        };

        var fakeThumbnails = new FakeThumbnailService();
        var fakeCloudSync = new FakeCloudSyncService { ThrowOnCleanup = true };

        var vm = new SettingsViewModel(
            settingsRepo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            thumbnailService: fakeThumbnails,
            cloudSyncService: fakeCloudSync
        );
        vm.ThumbnailRetentionDays = 30;

        await vm.CleanupExpiredThumbnailsNowCommand.ExecuteAsync(null);

        settingsRepo.CurrentSettings.LastThumbnailCleanupAt.Should().NotBeNullOrWhiteSpace();
        vm.ThumbnailCacheStatusMessage.Should().Contain("Đã dọn dẹp xong: Đã xóa 5 ảnh thumbnail quá hạn")
            .And.Contain("Dọn Cloud chưa hoàn tất: Simulated cloud failure");
    }

    [Fact]
    public async Task SettingsViewModel_ClearThumbnailCache_WhenUserSelectsYes_CallsCloudCleanupWithClearAllTrue()
    {
        var settingsRepo = new FakeSettingsRepo
        {
            CurrentSettings = new AppSettings
            {
                RootFolder = @"C:\FakeRoot",
                EnableCloudSync = true,
                ThumbnailRetentionDays = 60
            }
        };

        var fakeThumbnails = new FakeThumbnailService();
        var fakeCloudSync = new FakeCloudSyncService();

        var vm = new SettingsViewModel(
            settingsRepo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            thumbnailService: fakeThumbnails,
            cloudSyncService: fakeCloudSync
        );
        vm.ClearCacheConfirmFunc = (_, _, _, _) => System.Windows.MessageBoxResult.Yes;

        await vm.ClearThumbnailCacheCommand.ExecuteAsync(null);

        fakeCloudSync.CleanupCalled.Should().BeTrue();
        fakeCloudSync.LastClearAll.Should().BeTrue();
        vm.ThumbnailCacheStatusMessage.Should().Contain("Đã xóa sạch bộ nhớ đệm máy tính: 10 file")
            .And.Contain("Đồng thời đã dọn sạch ảnh trên Cloud");
    }

    [Fact]
    public async Task SettingsViewModel_ClearThumbnailCache_WhenUserSelectsNo_OnlyClearsLocal()
    {
        var settingsRepo = new FakeSettingsRepo
        {
            CurrentSettings = new AppSettings
            {
                RootFolder = @"C:\FakeRoot",
                EnableCloudSync = true,
                ThumbnailRetentionDays = 60
            }
        };

        var fakeThumbnails = new FakeThumbnailService();
        var fakeCloudSync = new FakeCloudSyncService();

        var vm = new SettingsViewModel(
            settingsRepo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            thumbnailService: fakeThumbnails,
            cloudSyncService: fakeCloudSync
        );
        vm.ClearCacheConfirmFunc = (_, _, _, _) => System.Windows.MessageBoxResult.No;

        await vm.ClearThumbnailCacheCommand.ExecuteAsync(null);

        fakeCloudSync.CleanupCalled.Should().BeFalse();
        vm.ThumbnailCacheStatusMessage.Should().Contain("Đã xóa sạch bộ nhớ đệm máy tính: 10 file");
        vm.ThumbnailCacheStatusMessage.Should().NotContain("Cloud");
    }

    [Fact]
    public async Task SettingsViewModel_ClearThumbnailCache_WhenUserCancels_DoesNothing()
    {
        var settingsRepo = new FakeSettingsRepo
        {
            CurrentSettings = new AppSettings
            {
                RootFolder = @"C:\FakeRoot",
                EnableCloudSync = true
            }
        };

        var fakeThumbnails = new FakeThumbnailService();
        var fakeCloudSync = new FakeCloudSyncService();

        var vm = new SettingsViewModel(
            settingsRepo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            thumbnailService: fakeThumbnails,
            cloudSyncService: fakeCloudSync
        );
        vm.ClearCacheConfirmFunc = (_, _, _, _) => System.Windows.MessageBoxResult.Cancel;

        await vm.ClearThumbnailCacheCommand.ExecuteAsync(null);

        fakeCloudSync.CleanupCalled.Should().BeFalse();
        vm.ThumbnailCacheStatusMessage.Should().BeEmpty();
    }
}
