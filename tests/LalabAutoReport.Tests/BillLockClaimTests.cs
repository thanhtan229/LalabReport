using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class BillLockClaimTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DatabaseMigrator _migrator;
    private readonly SqliteCustomerBillRepository _billRepo;

    public BillLockClaimTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabBillClaimTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_claim.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _migrator = new DatabaseMigrator(_connectionFactory);
        _billRepo = new SqliteCustomerBillRepository(_connectionFactory);
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

    private async Task<(long CustomerId, long OrderId, long JobId)> SeedBaseDataAsync()
    {
        await _migrator.MigrateAsync();
        using var conn = _connectionFactory.CreateConnection();
        string nowIso = DateTimeOffset.UtcNow.ToString("o");

        long rootId = await conn.QuerySingleAsync<long>(@"
            INSERT INTO root_folders (name, full_path, is_active, is_default, created_at, updated_at)
            VALUES ('Root', 'C:\Root', 1, 1, @Now, @Now);
            SELECT last_insert_rowid();
        ", new { Now = nowIso });

        long custId = await conn.QuerySingleAsync<long>(@"
            INSERT INTO customers (canonical_name, price_tier, created_at, updated_at)
            VALUES ('Khách A', 'Retail', @Now, @Now);
            SELECT last_insert_rowid();
        ", new { Now = nowIso });

        long orderId = await conn.QuerySingleAsync<long>(@"
            INSERT INTO orders (
                order_code, work_date, customer_id, original_folder_name, relative_path, status,
                is_printed, print_progress, is_delivered, root_folder_id, created_at, updated_at
            ) VALUES (
                'DH-261026-001', '2026-10-26', @CustId, 'Khách A', '2026-10-26\Khách A', 'Scanned',
                1, 1, 0, @RootId, @Now, @Now
            );
            SELECT last_insert_rowid();
        ", new { CustId = custId, RootId = rootId, Now = nowIso });

        long scanSnapshotId = await conn.QuerySingleAsync<long>(@"
            INSERT INTO scan_snapshots (order_id, started_at, scan_scope, status, app_version)
            VALUES (@OrderId, @Now, 'Order', 'Completed', '2.0.0');
            SELECT last_insert_rowid();
        ", new { OrderId = orderId, Now = nowIso });

        long jobId = await conn.QuerySingleAsync<long>(@"
            INSERT INTO order_item_scans (
                scan_snapshot_id, order_id, specification_folder_name, specification_relative_path,
                source_count, print_folder_status, scan_status
            ) VALUES (
                @SnapshotId, @OrderId, '13x18', '13x18', 10, 'AutoResolved', 'Normal'
            );
            SELECT last_insert_rowid();
        ", new { SnapshotId = scanSnapshotId, OrderId = orderId });

        return (custId, orderId, jobId);
    }

    private CustomerBill CreateDraftBill(string billNumber, long custId, long orderId, long jobId, bool isIncluded = true)
    {
        var bill = new CustomerBill
        {
            BillNumber = billNumber,
            CustomerId = custId,
            CustomerNameSnapshot = "Khách A",
            PeriodStart = "2026-10-26",
            PeriodEnd = "2026-10-26",
            Status = CustomerBillStatus.Draft,
            ProductSubtotal = 100000,
            GrandTotal = 100000
        };

        bill.Orders.Add(new CustomerBillOrder
        {
            OrderId = orderId,
            OrderNameSnapshot = "Đơn 1",
            OrderDateSnapshot = "2026-10-26",
            OriginalFolderNameSnapshot = "Khách A",
            IsIncluded = isIncluded,
            Subtotal = 100000
        });

        bill.Lines.Add(new CustomerBillLine
        {
            OrderId = orderId,
            ProductJobId = jobId,
            ProductNameSnapshot = "13x18",
            ScannedQuantity = 10,
            BilledQuantity = 10,
            BilledUnitPrice = 10000,
            LineTotal = 100000,
            IsIncluded = isIncluded,
            SortOrder = 1
        });

        return bill;
    }

    [Fact]
    public async Task LockClaimRace_TwoDraftsSameOrder_OnlyOneLocks()
    {
        var (custId, orderId, jobId) = await SeedBaseDataAsync();

        var bill1 = CreateDraftBill("BILL-001", custId, orderId, jobId);
        await _billRepo.SaveBillAsync(bill1);

        var bill2 = CreateDraftBill("BILL-002", custId, orderId, jobId);
        await _billRepo.SaveBillAsync(bill2);

        // Lock bill 1
        bill1.Status = CustomerBillStatus.Locked;
        bill1.LockedAt = DateTimeOffset.UtcNow;
        await _billRepo.LockCustomerBillAtomicAsync(bill1, new[] { jobId }, new[] { orderId });

        // Lock bill 2 attempting to claim same order and job: fails with BillClaimConflictException!
        bill2.Status = CustomerBillStatus.Locked;
        bill2.LockedAt = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<BillClaimConflictException>(() =>
            _billRepo.LockCustomerBillAtomicAsync(bill2, new[] { jobId }, new[] { orderId }));

        ex.Message.Should().Contain("đã được chốt trong hóa đơn khác");

        // Losing bill 2 remains Draft in database
        var reloadedBill2 = await _billRepo.GetByIdAsync(bill2.Id);
        Assert.NotNull(reloadedBill2);
        reloadedBill2.Status.Should().Be(CustomerBillStatus.Draft);
    }

    [Fact]
    public async Task LockClaim_JobOwnedByLockedBill_Rejected()
    {
        var (custId, orderId, jobId) = await SeedBaseDataAsync();

        // Seed a second order
        using var conn = _connectionFactory.CreateConnection();
        string nowIso = DateTimeOffset.UtcNow.ToString("o");
        long order2Id = await conn.QuerySingleAsync<long>(@"
            INSERT INTO orders (
                order_code, work_date, customer_id, original_folder_name, relative_path, status,
                is_printed, print_progress, is_delivered, root_folder_id, created_at, updated_at
            ) VALUES (
                'DH-261026-002', '2026-10-26', @CustId, 'Khách A', '2026-10-26\Khách A\02', 'Scanned',
                1, 1, 0, 1, @Now, @Now
            );
            SELECT last_insert_rowid();
        ", new { CustId = custId, Now = nowIso });

        // Bill 1 locks jobId
        var bill1 = CreateDraftBill("BILL-001", custId, orderId, jobId);
        await _billRepo.SaveBillAsync(bill1);
        bill1.Status = CustomerBillStatus.Locked;
        bill1.LockedAt = DateTimeOffset.UtcNow;
        await _billRepo.LockCustomerBillAtomicAsync(bill1, new[] { jobId }, new[] { orderId });

        // Bill 2 has order2, but tries to claim jobId which is already owned by Locked bill 1
        var bill2 = CreateDraftBill("BILL-002", custId, order2Id, jobId);
        await _billRepo.SaveBillAsync(bill2);
        bill2.Status = CustomerBillStatus.Locked;
        bill2.LockedAt = DateTimeOffset.UtcNow;

        var ex = await Assert.ThrowsAsync<BillClaimConflictException>(() =>
            _billRepo.LockCustomerBillAtomicAsync(bill2, new[] { jobId }, new[] { order2Id }));

        ex.Message.Should().Contain("hạng mục in đã thuộc hóa đơn đã khóa/xuất");
    }

    [Fact]
    public async Task LockClaim_JobOwnedByOtherDraft_TakenOver()
    {
        var (custId, orderId, jobId) = await SeedBaseDataAsync();

        // Bill 1 is Draft and claims jobId
        var bill1 = CreateDraftBill("BILL-001", custId, orderId, jobId);
        await _billRepo.SaveBillAsync(bill1);
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE order_item_scans SET customer_bill_id = @BillId WHERE id = @JobId",
                new { BillId = bill1.Id, JobId = jobId });
        }

        // Bill 2 locks and takes over jobId from Draft bill 1
        var bill2 = CreateDraftBill("BILL-002", custId, orderId, jobId);
        await _billRepo.SaveBillAsync(bill2);
        bill2.Status = CustomerBillStatus.Locked;
        bill2.LockedAt = DateTimeOffset.UtcNow;

        // Must succeed without throwing
        await _billRepo.LockCustomerBillAtomicAsync(bill2, new[] { jobId }, new[] { orderId });

        // Job now points to Bill 2
        using (var conn = _connectionFactory.CreateConnection())
        {
            long owner = await conn.ExecuteScalarAsync<long>(
                "SELECT customer_bill_id FROM order_item_scans WHERE id = @JobId", new { JobId = jobId });
            owner.Should().Be(bill2.Id);
        }
    }

    [Fact]
    public async Task LockClaim_ExcludedOrderInLockedBill_CanBeBilledElsewhere()
    {
        var (custId, orderId, jobId) = await SeedBaseDataAsync();

        // Bill 1 has orderId EXCLUDED (is_included = 0) and is locked
        var bill1 = CreateDraftBill("BILL-001", custId, orderId, jobId, isIncluded: false);
        await _billRepo.SaveBillAsync(bill1);
        bill1.Status = CustomerBillStatus.Locked;
        bill1.LockedAt = DateTimeOffset.UtcNow;
        // Lock bill 1 without claiming the excluded order or job
        await _billRepo.LockCustomerBillAtomicAsync(bill1, Array.Empty<long>(), Array.Empty<long>());

        // Bill 2 INCLUDES orderId and locks it -> succeeds because bill 1 excluded it!
        var bill2 = CreateDraftBill("BILL-002", custId, orderId, jobId, isIncluded: true);
        await _billRepo.SaveBillAsync(bill2);
        bill2.Status = CustomerBillStatus.Locked;
        bill2.LockedAt = DateTimeOffset.UtcNow;

        await _billRepo.LockCustomerBillAtomicAsync(bill2, new[] { jobId }, new[] { orderId });

        var reloaded = await _billRepo.GetByIdAsync(bill2.Id);
        Assert.NotNull(reloaded);
        reloaded.Status.Should().Be(CustomerBillStatus.Locked);
    }

    [Fact]
    public async Task SplitThenLock_ValidSplitStillLocks()
    {
        var (custId, order1Id, job1Id) = await SeedBaseDataAsync();

        using var conn = _connectionFactory.CreateConnection();
        string nowIso = DateTimeOffset.UtcNow.ToString("o");

        long order2Id = await conn.QuerySingleAsync<long>(@"
            INSERT INTO orders (
                order_code, work_date, customer_id, original_folder_name, relative_path, status,
                is_printed, print_progress, is_delivered, root_folder_id, created_at, updated_at
            ) VALUES (
                'DH-261026-002', '2026-10-26', @CustId, 'Khách A', '2026-10-26\Khách A\02', 'Scanned',
                1, 1, 0, 1, @Now, @Now
            );
            SELECT last_insert_rowid();
        ", new { CustId = custId, Now = nowIso });

        long scan2Id = await conn.QuerySingleAsync<long>(@"
            INSERT INTO scan_snapshots (order_id, started_at, scan_scope, status, app_version)
            VALUES (@OrderId, @Now, 'Order', 'Completed', '2.0.0');
            SELECT last_insert_rowid();
        ", new { OrderId = order2Id, Now = nowIso });

        long job2Id = await conn.QuerySingleAsync<long>(@"
            INSERT INTO order_item_scans (
                scan_snapshot_id, order_id, specification_folder_name, specification_relative_path,
                source_count, print_folder_status, scan_status
            ) VALUES (
                @SnapshotId, @OrderId, '15x21', '15x21', 5, 'AutoResolved', 'Normal'
            );
            SELECT last_insert_rowid();
        ", new { SnapshotId = scan2Id, OrderId = order2Id });

        // Original bill with 2 orders
        var originalBill = CreateDraftBill("BILL-SPLIT-ORIG", custId, order1Id, job1Id);
        originalBill.Orders.Add(new CustomerBillOrder
        {
            OrderId = order2Id,
            OrderNameSnapshot = "Đơn 2",
            OrderDateSnapshot = "2026-10-26",
            OriginalFolderNameSnapshot = "Khách A",
            IsIncluded = true,
            Subtotal = 50000
        });
        originalBill.Lines.Add(new CustomerBillLine
        {
            OrderId = order2Id,
            ProductJobId = job2Id,
            ProductNameSnapshot = "15x21",
            ScannedQuantity = 5,
            BilledQuantity = 5,
            BilledUnitPrice = 10000,
            LineTotal = 50000,
            IsIncluded = true,
            SortOrder = 2
        });
        originalBill.ProductSubtotal = 150000;
        originalBill.GrandTotal = 150000;
        await _billRepo.SaveBillAsync(originalBill);

        // Load fresh
        var loadedOriginal = await _billRepo.GetByIdAsync(originalBill.Id);
        Assert.NotNull(loadedOriginal);

        // Created bill for split
        var createdBill = new CustomerBill
        {
            BillNumber = "BILL-SPLIT-NEW",
            CustomerId = custId,
            CustomerNameSnapshot = "Khách A",
            PeriodStart = "2026-10-26",
            PeriodEnd = "2026-10-26",
            Status = CustomerBillStatus.Draft,
            ProductSubtotal = 50000,
            GrandTotal = 50000
        };
        createdBill.Orders.Add(new CustomerBillOrder
        {
            OrderId = order2Id,
            OrderNameSnapshot = "Đơn 2",
            OrderDateSnapshot = "2026-10-26",
            OriginalFolderNameSnapshot = "Khách A",
            IsIncluded = true,
            Subtotal = 50000
        });
        createdBill.Lines.Add(new CustomerBillLine
        {
            OrderId = order2Id,
            ProductJobId = job2Id,
            ProductNameSnapshot = "15x21",
            ScannedQuantity = 5,
            BilledQuantity = 5,
            BilledUnitPrice = 10000,
            LineTotal = 50000,
            IsIncluded = true,
            SortOrder = 1
        });

        // Mutate original in-memory representation for split
        loadedOriginal.Orders.RemoveAll(o => o.OrderId == order2Id);
        loadedOriginal.Lines.RemoveAll(l => l.OrderId == order2Id);
        loadedOriginal.ProductSubtotal = 100000;
        loadedOriginal.GrandTotal = 100000;

        // Perform split using new revision signature
        await _billRepo.SplitBillAtomicAsync(loadedOriginal, createdBill, new[] { order2Id });

        createdBill.Id.Should().BeGreaterThan(0);

        // Lock created bill
        createdBill.Status = CustomerBillStatus.Locked;
        createdBill.LockedAt = DateTimeOffset.UtcNow;
        await _billRepo.LockCustomerBillAtomicAsync(createdBill, new[] { job2Id }, new[] { order2Id });

        var reloadedCreated = await _billRepo.GetByIdAsync(createdBill.Id);
        Assert.NotNull(reloadedCreated);
        reloadedCreated.Status.Should().Be(CustomerBillStatus.Locked);
    }

    [Fact]
    public async Task SplitVsLock_ConcurrentSplitAfterLock_Rejected()
    {
        var (custId, orderId, jobId) = await SeedBaseDataAsync();

        var bill = CreateDraftBill("BILL-001", custId, orderId, jobId);
        await _billRepo.SaveBillAsync(bill);

        var loadedBill = await _billRepo.GetByIdAsync(bill.Id);
        Assert.NotNull(loadedBill);

        // Lock the bill
        loadedBill.Status = CustomerBillStatus.Locked;
        loadedBill.LockedAt = DateTimeOffset.UtcNow;
        await _billRepo.LockCustomerBillAtomicAsync(loadedBill, new[] { jobId }, new[] { orderId });

        // Attempting to split a locked bill throws InvalidOperationException
        var created = new CustomerBill { BillNumber = "NEW", CustomerId = custId, Status = CustomerBillStatus.Draft };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _billRepo.SplitBillAtomicAsync(loadedBill, created, new[] { orderId }));
    }

    [Fact]
    public async Task Split_StaleRevision_Rejected()
    {
        var (custId, orderId, jobId) = await SeedBaseDataAsync();

        var bill = CreateDraftBill("BILL-001", custId, orderId, jobId);
        await _billRepo.SaveBillAsync(bill);

        var loadedBill = await _billRepo.GetByIdAsync(bill.Id);
        Assert.NotNull(loadedBill);

        // Artificially change the expected revision (stale)
        loadedBill.Revision = loadedBill.Revision + 1;

        var created = new CustomerBill { BillNumber = "NEW", CustomerId = custId, Status = CustomerBillStatus.Draft };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _billRepo.SplitBillAtomicAsync(loadedBill, created, new[] { orderId }));

        ex.Message.Should().Contain("Hóa đơn đã thay đổi");
    }
}
