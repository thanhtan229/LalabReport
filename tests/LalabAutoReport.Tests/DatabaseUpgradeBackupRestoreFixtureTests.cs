using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class DatabaseUpgradeBackupRestoreFixtureTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DatabaseBackupService _backupService;
    private readonly DatabaseMigrator _migrator;

    public DatabaseUpgradeBackupRestoreFixtureTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabDbLifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _tempDbPath = Path.Combine(_tempRoot, "test_lifecycle.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _backupService = new DatabaseBackupService(_connectionFactory);
        _migrator = new DatabaseMigrator(_connectionFactory, _backupService);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors on Windows
        }
    }

    [Fact]
    public async Task ComprehensiveDatabaseLifecycle_Upgrade_Idempotency_Integrity_BackupRestore_PreservesAllStates()
    {
        // 1. Initialize schema and seed fixture data
        await _migrator.MigrateAsync();

        using (var conn = _connectionFactory.CreateConnection())
        {
            // Seed Roots
            await conn.ExecuteAsync(@"
                INSERT INTO root_folders (id, name, full_path, is_active, is_default, created_at, updated_at)
                VALUES (1, 'Kho Chinh', 'C:\Root1', 1, 1, datetime('now'), datetime('now')),
                       (2, 'Kho Phu', 'C:\Root2', 1, 0, datetime('now'), datetime('now'));
            ");

            // Seed Customer
            await conn.ExecuteAsync(@"
                INSERT INTO customers (id, canonical_name, phone, note, created_at, updated_at)
                VALUES (1, 'Studio Hoa Mai', '0912345678', 'Khách VIP', datetime('now'), datetime('now'));
            ");

            // Seed Orders with multi-root and operational states
            await conn.ExecuteAsync(@"
                INSERT INTO orders (id, customer_id, work_date, relative_path, original_folder_name, status, root_folder_id,
                                    is_delivered, delivered_at, delivered_by, note, created_at, updated_at)
                VALUES (101, 1, '2026-09-28', '2026-09-28\Customer\13x18 in', '13x18 in', 'Scanned', 1,
                        1, '2026-09-28 10:00:00', 'StaffA', 'Giao gấp buổi sáng', datetime('now'), datetime('now')),
                       (102, 1, '2026-09-28', '2026-09-28\Customer\13x18 in', '13x18 in', 'Scanned', 2,
                        0, NULL, NULL, 'Đơn từ kho 2', datetime('now'), datetime('now'));
            ");

            // Seed Customer Bills: one Locked, one Exported
            await conn.ExecuteAsync(@"
                INSERT INTO customer_bills (id, bill_number, customer_id, customer_name_snapshot, period_start, period_end,
                                           product_subtotal, adjustments_total, grand_total, status, bill_type,
                                           is_paid, paid_at, locked_at, exported_at, created_at, updated_at)
                VALUES (201, 'BILL-20260928-001', 1, 'Studio Hoa Mai', '2026-09-28', '2026-09-28',
                        500000, 0, 500000, 'Locked', 'Customer',
                        0, NULL, datetime('now'), NULL, datetime('now'), datetime('now')),
                       (202, 'BILL-20260928-002', 1, 'Studio Hoa Mai', '2026-09-28', '2026-09-28',
                        1200000, 0, 1200000, 'Exported', 'Customer',
                        1, '2026-09-28 15:30:00', datetime('now'), datetime('now'), datetime('now'), datetime('now'));
            ");

            // Seed cloud sync state
            await conn.ExecuteAsync(@"
                UPDATE cloud_sync_state
                SET stream_id = 'test-stream-fixture',
                    cursor = 42,
                    endpoint = 'https://fixture.tinix.io.vn'
                WHERE id = 1;
            ");
        }

        // 2. Perform Backup
        string backupFile = Path.Combine(_tempRoot, "pre_lifecycle_backup.db");
        var backupResult = await _backupService.CreateBackupAsync(backupFile);
        File.Exists(backupResult.BackupPath).Should().BeTrue();
        new FileInfo(backupResult.BackupPath).Length.Should().BeGreaterThan(0);

        // 3. Repeated Migration / Idempotency Check
        // Running MigrateAsync again must be completely idempotent and not alter schema or data
        await _migrator.MigrateAsync();
        await _migrator.MigrateAsync();

        // 4. PRAGMA integrity_check
        using (var conn = _connectionFactory.CreateConnection())
        {
            string integrity = (await conn.ExecuteScalarAsync<string>("PRAGMA integrity_check;"))!;
            integrity.Should().Be("ok");

            // 5. PRAGMA foreign_key_check
            var fkViolations = (await conn.QueryAsync("PRAGMA foreign_key_check;")).AsList();
            fkViolations.Should().BeEmpty();

            // 6. Financial snapshot / totals unchanged
            var bill1 = await conn.QuerySingleAsync<dynamic>("SELECT product_subtotal, adjustments_total, grand_total, status, is_paid FROM customer_bills WHERE id = 201");
            ((long)bill1.product_subtotal).Should().Be(500000);
            ((long)bill1.adjustments_total).Should().Be(0);
            ((long)bill1.grand_total).Should().Be(500000);
            ((string)bill1.status).Should().Be("Locked");
            ((long)bill1.is_paid).Should().Be(0);

            var bill2 = await conn.QuerySingleAsync<dynamic>("SELECT product_subtotal, adjustments_total, grand_total, status, is_paid, paid_at FROM customer_bills WHERE id = 202");
            ((long)bill2.product_subtotal).Should().Be(1200000);
            ((long)bill2.adjustments_total).Should().Be(0);
            ((long)bill2.grand_total).Should().Be(1200000);
            ((string)bill2.status).Should().Be("Exported");
            ((long)bill2.is_paid).Should().Be(1);
            ((string)bill2.paid_at).Should().Be("2026-09-28 15:30:00");

            // 7. Operational fields preserved
            var order1 = await conn.QuerySingleAsync<dynamic>("SELECT root_folder_id, is_delivered, delivered_by, note FROM orders WHERE id = 101");
            ((long)order1.root_folder_id).Should().Be(1);
            ((long)order1.is_delivered).Should().Be(1);
            ((string)order1.delivered_by).Should().Be("StaffA");
            ((string)order1.note).Should().Be("Giao gấp buổi sáng");

            var order2 = await conn.QuerySingleAsync<dynamic>("SELECT root_folder_id, is_delivered, note FROM orders WHERE id = 102");
            ((long)order2.root_folder_id).Should().Be(2);
            ((long)order2.is_delivered).Should().Be(0);
            ((string)order2.note).Should().Be("Đơn từ kho 2");

            // Multi-root separation preserved
            long root1Count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM orders WHERE root_folder_id = 1");
            long root2Count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM orders WHERE root_folder_id = 2");
            root1Count.Should().Be(1);
            root2Count.Should().Be(1);

            // Cloud state preserved
            var syncState = await conn.QuerySingleAsync<dynamic>("SELECT stream_id, cursor, endpoint FROM cloud_sync_state WHERE id = 1");
            ((string)syncState.stream_id).Should().Be("test-stream-fixture");
            ((long)syncState.cursor).Should().Be(42);
        }

        // 8. Test Restore rejection when cloud is bound
        var rejectEx = await Assert.ThrowsAsync<InvalidOperationException>(() => _backupService.RestoreBackupAsync(backupResult.BackupPath));
        rejectEx.Message.Should().Contain("Cloud");

        // Clear cloud sync state on both current and backup to allow authorized local restore
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync("DELETE FROM customer_bills WHERE id = 201");
            await conn.ExecuteAsync("INSERT INTO orders (id, customer_id, work_date, relative_path, original_folder_name, status, created_at, updated_at) VALUES (999, 1, '2026-09-28', 'temp', 'temp', 'Scanned', datetime('now'), datetime('now'))");
            await conn.ExecuteAsync("UPDATE cloud_sync_state SET stream_id = NULL, endpoint = NULL, cursor = 0, requires_reconciliation = 0 WHERE id = 1");
        }

        using (var backupConn = new SqliteConnection($"Data Source={backupResult.BackupPath}"))
        {
            await backupConn.OpenAsync();
            await backupConn.ExecuteAsync("UPDATE cloud_sync_state SET stream_id = NULL, endpoint = NULL, cursor = 0, requires_reconciliation = 0 WHERE id = 1");
        }

        // 9. Restore backup
        await _backupService.RestoreBackupAsync(backupResult.BackupPath);

        // 10. Verify restored state
        using (var restoredConn = _connectionFactory.CreateConnection())
        {
            // Integrity & FK check on restored DB
            string restoredIntegrity = (await restoredConn.ExecuteScalarAsync<string>("PRAGMA integrity_check;"))!;
            restoredIntegrity.Should().Be("ok");

            var restoredFkViolations = (await restoredConn.QueryAsync("PRAGMA foreign_key_check;")).AsList();
            restoredFkViolations.Should().BeEmpty();

            var (grandTotal1, status1) = await restoredConn.QuerySingleAsync<(long, string)>("SELECT grand_total, status FROM customer_bills WHERE id = 201");
            grandTotal1.Should().Be(500000);
            status1.Should().Be("Locked");

            // Mutated order 999 is gone
            long order999Count = await restoredConn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM orders WHERE id = 999");
            order999Count.Should().Be(0);

            // Original orders restored
            long originalOrderCount = await restoredConn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM orders WHERE id IN (101, 102)");
            originalOrderCount.Should().Be(2);
        }
    }
}
