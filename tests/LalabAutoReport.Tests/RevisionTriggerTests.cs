using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.ApplicationCore.Commands;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Commands;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class RevisionTriggerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DatabaseMigrator _migrator;

    public RevisionTriggerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabRevTrigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_rev.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _migrator = new DatabaseMigrator(_connectionFactory);
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

    private async Task<long> SeedOrderAsync(SqliteConnection conn)
    {
        string nowIso = DateTimeOffset.UtcNow.ToString("o");
        return await conn.QuerySingleAsync<long>(@"
            INSERT INTO orders (
                order_code, work_date, original_folder_name, relative_path, status,
                is_delivered, delivered_revision, is_printed, printed_revision,
                note_revision, created_at, updated_at
            ) VALUES (
                'DH-261026-001', '2026-10-26', 'Folder', '2026-10-26\\Folder', 'Scanned',
                0, 0, 0, 0, 0, @Now, @Now
            );
            SELECT last_insert_rowid();
        ", new { Now = nowIso });
    }

    [Fact]
    public async Task Trigger_DeliveredChange_BumpsDeliveredRevisionOnly_AndJournals()
    {
        await _migrator.MigrateAsync();
        using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        long orderId = await SeedOrderAsync(conn);

        // Update delivered columns
        await conn.ExecuteAsync(@"
            UPDATE orders
            SET is_delivered = 1, delivered_at = @At, delivered_by = 'Chủ tiệm'
            WHERE id = @Id;
        ", new { Id = orderId, At = DateTimeOffset.UtcNow.ToString("o") });

        // Assert delivered_revision is bumped to 1, others stay 0
        var order = await conn.QuerySingleAsync<dynamic>(
            "SELECT delivered_revision, printed_revision, note_revision FROM orders WHERE id = @Id", new { Id = orderId });
        ((long)order.delivered_revision).Should().Be(1);
        ((long)order.printed_revision).Should().Be(0);
        ((long)order.note_revision).Should().Be(0);

        // Assert change_journal row inserted
        var journal = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM change_journal WHERE entity_id = @Id AND field_group = 'delivered'", new { Id = orderId });
        Assert.NotNull(journal);
        ((string)journal.entity_type).Should().Be("order");
        ((long)journal.revision).Should().Be(1);
        ((long)journal.database_generation).Should().Be(1);
    }

    [Fact]
    public async Task Trigger_NoOpWrite_DoesNotBump()
    {
        await _migrator.MigrateAsync();
        using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        long orderId = await SeedOrderAsync(conn);

        // Initial bump
        await conn.ExecuteAsync(@"
            UPDATE orders
            SET is_delivered = 1, delivered_at = '2026-10-26T10:00:00Z', delivered_by = 'Chủ tiệm'
            WHERE id = @Id;
        ", new { Id = orderId });

        var countBefore = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM change_journal WHERE entity_id = @Id", new { Id = orderId });

        // No-op write: set the exact same values
        await conn.ExecuteAsync(@"
            UPDATE orders
            SET is_delivered = 1, delivered_at = '2026-10-26T10:00:00Z', delivered_by = 'Chủ tiệm'
            WHERE id = @Id;
        ", new { Id = orderId });

        // Revision must stay 1
        var rev = await conn.ExecuteScalarAsync<long>(
            "SELECT delivered_revision FROM orders WHERE id = @Id", new { Id = orderId });
        rev.Should().Be(1);

        // Journal row count must be unchanged
        var countAfter = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM change_journal WHERE entity_id = @Id", new { Id = orderId });
        countAfter.Should().Be(countBefore);
    }

    [Fact]
    public async Task Trigger_ScanSaveOrder_UnchangedPrinted_DoesNotBump()
    {
        await _migrator.MigrateAsync();
        var orderRepo = new SqliteOrderRepository(_connectionFactory);

        // Create an order via repository
        var order = new Order
        {
            WorkDate = "2026-10-26",
            OriginalFolderName = "CustomerA",
            RelativePath = "2026-10-26\\CustomerA",
            Status = OrderStatus.Scanned,
            IsPrinted = true,
            PrintProgress = PrintStatus.Printed,
            PrintedAt = DateTimeOffset.UtcNow
        };
        var snapshot = new ScanSnapshot { OrderId = 0, StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow };
        await orderRepo.SaveOrderAsync(order, snapshot);
        order.Id.Should().BeGreaterThan(0);

        // After initial insert, bump print once
        using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        long initialPrintedRev = await conn.ExecuteScalarAsync<long>(
            "SELECT printed_revision FROM orders WHERE id = @Id", new { Id = order.Id });

        // Rescan / re-save with unchanged printed state
        var loaded = await orderRepo.GetOrderByIdAsync(order.Id);
        Assert.NotNull(loaded);
        await orderRepo.SaveOrderAsync(loaded, snapshot);

        long afterPrintedRev = await conn.ExecuteScalarAsync<long>(
            "SELECT printed_revision FROM orders WHERE id = @Id", new { Id = order.Id });
        afterPrintedRev.Should().Be(initialPrintedRev);
    }

    [Fact]
    public async Task Trigger_CloudApplyEvent_BumpsLanRevision_LanClientGets409()
    {
        await _migrator.MigrateAsync();
        using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        long orderId = await SeedOrderAsync(conn);

        // Seed initial note
        await conn.ExecuteAsync("UPDATE orders SET note = 'Initial note' WHERE id = @Id", new { Id = orderId });
        long revAfterSeed = await conn.ExecuteScalarAsync<long>("SELECT note_revision FROM orders WHERE id = @Id", new { Id = orderId });
        revAfterSeed.Should().Be(1);

        // Simulated cloud inbound write directly updates orders.note (as ApplyEventAsync does)
        await conn.ExecuteAsync("UPDATE orders SET note = 'Cloud updated note' WHERE id = @Id", new { Id = orderId });

        long revAfterCloud = await conn.ExecuteScalarAsync<long>("SELECT note_revision FROM orders WHERE id = @Id", new { Id = orderId });
        revAfterCloud.Should().Be(2);

        // LAN client holding old revision (1) attempts to update note -> gets 409 STALE_REVISION
        var store = new SqliteOrderOperationalCommandStore(_connectionFactory);
        var inFlight = new InFlightOperationRegistry();
        var lookup = new SqliteCommandReceiptReader(_connectionFactory);
        var handler = new OrderOperationalCommandHandler(store, inFlight, lookup);
        var actor = new ActorContext("session", "Admin", new HashSet<string> { Capabilities.OrdersEditNote });

        var req = new SetOrderNoteRequest(Guid.NewGuid().ToString(), ExpectedRevision: 1, Note: "LAN overwrite attempt");
        var ex = await Assert.ThrowsAsync<StaleRevisionException>(() =>
            handler.SetNoteAsync(actor, orderId, req));
        ex.Code.Should().Be(ApiErrorCodes.StaleRevision);
        ex.CurrentRevision.Should().Be(2);
    }

    [Fact]
    public async Task Trigger_PrintStatusServiceWriter_BumpsPrintedRevision()
    {
        await _migrator.MigrateAsync();
        using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        long orderId = await SeedOrderAsync(conn);

        // PrintStatusService updates is_printed and print_progress
        await conn.ExecuteAsync(@"
            UPDATE orders
            SET is_printed = 1, print_progress = 1, printed_at = @Now
            WHERE id = @Id;
        ", new { Id = orderId, Now = DateTimeOffset.UtcNow.ToString("o") });

        long printedRev = await conn.ExecuteScalarAsync<long>(
            "SELECT printed_revision FROM orders WHERE id = @Id", new { Id = orderId });
        printedRev.Should().Be(1);

        var journal = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM change_journal WHERE entity_id = @Id AND field_group = 'printed'", new { Id = orderId });
        Assert.NotNull(journal);
        ((long)journal.revision).Should().Be(1);
    }

    [Fact]
    public async Task Trigger_CustomerBillUpdate_BumpsRevisionOnce_NoRecursion()
    {
        await _migrator.MigrateAsync();
        using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        string nowIso = DateTimeOffset.UtcNow.ToString("o");

        long billId = await conn.QuerySingleAsync<long>(@"
            INSERT INTO customer_bills (
                bill_number, customer_name_snapshot, period_start, period_end, status, product_subtotal,
                adjustments_total, grand_total, created_at, updated_at
            ) VALUES (
                'BILL-TEST-001', 'Customer A', '2026-10-26', '2026-10-26', 'Draft', 50000,
                0, 50000, @Now, @Now
            );
            SELECT last_insert_rowid();
        ", new { Now = nowIso });

        // Initial revision is 0
        long rev0 = await conn.ExecuteScalarAsync<long>("SELECT revision FROM customer_bills WHERE id = @Id", new { Id = billId });
        rev0.Should().Be(0);

        // Update customer_bills
        await conn.ExecuteAsync(@"
            UPDATE customer_bills
            SET product_subtotal = 75000, grand_total = 75000, updated_at = @Now
            WHERE id = @Id;
        ", new { Id = billId, Now = DateTimeOffset.UtcNow.ToString("o") });

        // Revision must be bumped exactly once to 1 (no recursive trigger loop)
        long rev1 = await conn.ExecuteScalarAsync<long>("SELECT revision FROM customer_bills WHERE id = @Id", new { Id = billId });
        rev1.Should().Be(1);

        // Change journal recorded exactly one row for bill
        var journalRows = (await conn.QueryAsync<dynamic>(
            "SELECT * FROM change_journal WHERE entity_id = @Id AND entity_type = 'customer_bill'", new { Id = billId })).ToList();
        journalRows.Should().HaveCount(1);
        ((long)journalRows[0].revision).Should().Be(1);
    }

    [Fact]
    public void Migrator_LatestVersionEqualsMaxMigration()
    {
        DatabaseMigrator.LatestVersion.Should().Be(29);
    }

    [Fact]
    public async Task PhaseOneBinarySchemaList_IgnoresV25toV29()
    {
        // Migrate to V29
        await _migrator.MigrateAsync();

        // Running MigrateToVersionAsync(24) on a V29 DB must succeed with no error
        var act = async () => await _migrator.MigrateToVersionAsync(24);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReopenAndPayment_BothBumpBillRevision()
    {
        await _migrator.MigrateAsync();
        var billRepo = new SqliteCustomerBillRepository(_connectionFactory);
        using var conn = (SqliteConnection)_connectionFactory.CreateConnection();
        string nowIso = DateTimeOffset.UtcNow.ToString("o");

        // 1. Create a customer and locked bill
        long custId = await conn.QuerySingleAsync<long>(@"
            INSERT INTO customers (canonical_name, price_tier, created_at, updated_at)
            VALUES ('Customer RevTest', 'Retail', @Now, @Now);
            SELECT last_insert_rowid();
        ", new { Now = nowIso });

        long billId = await conn.QuerySingleAsync<long>(@"
            INSERT INTO customer_bills (
                bill_number, customer_id, customer_name_snapshot, period_start, period_end, status, product_subtotal,
                adjustments_total, grand_total, is_paid, created_at, updated_at
            ) VALUES (
                'BILL-REV-001', @CustId, 'Customer RevTest', '2026-10-26', '2026-10-26', 'Locked', 100000,
                0, 100000, 0, @Now, @Now
            );
            SELECT last_insert_rowid();
        ", new { CustId = custId, Now = nowIso });

        long rev0 = await conn.ExecuteScalarAsync<long>(
            "SELECT revision FROM customer_bills WHERE id = @Id", new { Id = billId });
        rev0.Should().Be(0);

        // 2. Reopen bill (status becomes Draft via ReopenCustomerBillAtomicAsync)
        var billToReopen = await billRepo.GetByIdAsync(billId);
        Assert.NotNull(billToReopen);
        billToReopen.Status = CustomerBillStatus.Draft;
        await billRepo.ReopenCustomerBillAtomicAsync(billToReopen, Array.Empty<long>(), Array.Empty<long>());

        long revReopened = await conn.ExecuteScalarAsync<long>(
            "SELECT revision FROM customer_bills WHERE id = @Id", new { Id = billId });
        revReopened.Should().Be(1);

        var reopenJournal = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM change_journal WHERE entity_id = @Id AND entity_type = 'customer_bill' AND revision = 1",
            new { Id = billId });
        Assert.NotNull(reopenJournal);
        ((string)reopenJournal.field_group).Should().Be("bill");

        // 3. Toggle payment (is_paid = true via SetPaymentStatusAsync)
        await billRepo.SetPaymentStatusAsync(billId, isPaid: true, paidAt: DateTimeOffset.UtcNow);

        long revPaid = await conn.ExecuteScalarAsync<long>(
            "SELECT revision FROM customer_bills WHERE id = @Id", new { Id = billId });
        revPaid.Should().Be(2);

        var paidJournal = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM change_journal WHERE entity_id = @Id AND entity_type = 'customer_bill' AND revision = 2",
            new { Id = billId });
        Assert.NotNull(paidJournal);
        ((string)paidJournal.field_group).Should().Be("bill");
    }
}
