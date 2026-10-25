using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Server.Health;
using LalabAutoReport.Server.Hosting;
using LalabAutoReport.Server.Inventory;
using LalabAutoReport.Server.Lifecycle;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class ServerTests
{
    #region 1. ServerInstanceGuard Tests

    [Fact]
    public void ServerInstanceGuard_RejectsSecondHolderOnSameDataDir_AndReleasesCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_guard_" + Guid.NewGuid().ToString("N"));
        string mutexName = @"Global\LalabTestGuard_" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(tempDir);

            using var guard1 = new ServerInstanceGuard(tempDir, mutexName);
            bool acquired1 = guard1.TryAcquire(out var error1);
            acquired1.Should().BeTrue(error1);
            guard1.IsAcquired.Should().BeTrue();
            File.Exists(guard1.LockFilePath).Should().BeTrue();

            using var guard2 = new ServerInstanceGuard(tempDir, mutexName);
            bool acquired2 = guard2.TryAcquire(out var error2);
            acquired2.Should().BeFalse();
            error2.Should().NotBeNullOrWhiteSpace();

            // Release guard 1
            guard1.Dispose();

            // Guard 2 should now acquire cleanly
            bool acquiredAgain = guard2.TryAcquire(out var error3);
            acquiredAgain.Should().BeTrue(error3);
            guard2.IsAcquired.Should().BeTrue();

            guard2.Dispose();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch { }
        }
    }

    #endregion

    #region 2. Migration V24 ServerIdentity Tests

    [Fact]
    public async Task MigrationV24_CreatesServerIdentityWithOneRow_AndIsIdempotent()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), "lalab_v24_" + Guid.NewGuid().ToString("N") + ".db");

        try
        {
            var factory = new SqliteConnectionFactory(tempDb);
            var migrator = new DatabaseMigrator(factory);

            // Run migration up to V24
            await migrator.MigrateToVersionAsync(24);

            using (var connection = factory.CreateConnection())
            {
                int count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM server_identity;");
                count.Should().Be(1);

                var row = await connection.QuerySingleAsync<(long id, string server_id, string created_at_utc)>(
                    "SELECT id, server_id, created_at_utc FROM server_identity WHERE id = 1;");

                row.id.Should().Be(1);
                Guid.TryParse(row.server_id, out _).Should().BeTrue("server_id must be a valid Guid string");
                DateTimeOffset.TryParse(row.created_at_utc, out _).Should().BeTrue("created_at_utc must be valid ISO timestamp");

                string originalServerId = row.server_id;

                // Re-run migration: must be completely idempotent
                await migrator.MigrateAsync();

                int countAfter = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM server_identity;");
                countAfter.Should().Be(1);

                string? serverIdAfter = await connection.ExecuteScalarAsync<string>("SELECT server_id FROM server_identity WHERE id = 1;");
                serverIdAfter.Should().Be(originalServerId);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempDb)) File.Delete(tempDb);
            }
            catch { }
        }
    }

    #endregion

    #region 3. Fail-Closed Data Lifecycle Tests

    [Fact]
    public async Task ServerDataLifecycle_WhenDatabaseFileExists_AndBackupThrows_AbortsWithoutCallingMigrator()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), "lalab_lifecycle_" + Guid.NewGuid().ToString("N") + ".db");
        await File.WriteAllTextAsync(tempDb, "fake existing db content");

        try
        {
            var fakeBackup = new FakeDatabaseBackupService(shouldThrow: true);
            var spyMigrator = new SpyDatabaseMigrator();
            var lifecycle = new ServerDataLifecycle(tempDb, fakeBackup, spyMigrator);

            bool result = await lifecycle.InitializeDatabaseAsync();

            result.Should().BeFalse("Lifecycle must fail-closed when backup fails");
            spyMigrator.MigratorCalled.Should().BeFalse("Migrator must NEVER be called if pre-migration backup fails");
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public async Task ServerDataLifecycle_WhenDatabaseDoesNotExist_MigratesWithoutCallingBackup()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), "lalab_lifecycle_fresh_" + Guid.NewGuid().ToString("N") + ".db");

        try
        {
            var fakeBackup = new FakeDatabaseBackupService(shouldThrow: true); // will throw IF called
            var spyMigrator = new SpyDatabaseMigrator();
            var lifecycle = new ServerDataLifecycle(tempDb, fakeBackup, spyMigrator);

            bool result = await lifecycle.InitializeDatabaseAsync();

            result.Should().BeTrue();
            fakeBackup.BackupCalled.Should().BeFalse("Backup should not be called for fresh DB");
            spyMigrator.MigratorCalled.Should().BeTrue("Migrator must be called for fresh DB");
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public async Task ServerDataLifecycle_WhenDatabaseFileExists_AndBackupSucceeds_ProceedsToMigrate()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), "lalab_lifecycle_ok_" + Guid.NewGuid().ToString("N") + ".db");
        await File.WriteAllTextAsync(tempDb, "fake existing db content");

        try
        {
            var fakeBackup = new FakeDatabaseBackupService(shouldThrow: false);
            var spyMigrator = new SpyDatabaseMigrator();
            var lifecycle = new ServerDataLifecycle(tempDb, fakeBackup, spyMigrator);

            bool result = await lifecycle.InitializeDatabaseAsync();

            result.Should().BeTrue();
            fakeBackup.BackupCalled.Should().BeTrue();
            spyMigrator.MigratorCalled.Should().BeTrue();
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    #endregion

    #region 4. Inventory Dry-Run Tests

    [Fact]
    public async Task InventoryDryRun_ReturnsExpectedCounts_AndDoesNotModifyData()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), "lalab_inv_" + Guid.NewGuid().ToString("N") + ".db");

        try
        {
            var factory = new SqliteConnectionFactory(tempDb);
            var migrator = new DatabaseMigrator(factory);
            await migrator.MigrateAsync();

            // Populate test fixture data
            using (var conn = factory.CreateConnection())
            {
                // Customer
                await conn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Khách Test', '2026-10-25', '2026-10-25');");

                // Root folder
                await conn.ExecuteAsync("INSERT INTO root_folders (id, name, full_path, is_default, is_active, created_at, updated_at) VALUES (1, 'Root 1', 'C:\\Data', 1, 1, '2026-10-25', '2026-10-25');");

                // Orders
                await conn.ExecuteAsync(@"
                    INSERT INTO orders (id, order_code, work_date, customer_id, order_kind, order_name, original_folder_name, relative_path, status, is_printed, created_at, updated_at, root_folder_id)
                    VALUES (1, 'ORD001', '2026-10-25', 1, 'Standard', 'Order 1', 'Order 1', '2026-10-25/Khách Test/Order 1', 'Scanned', 0, '2026-10-25', '2026-10-25', 1),
                           (2, 'ORD002', '2026-10-25', 1, 'Standard', 'Order 2', 'Order 2', '2026-10-25/Khách Test/Order 2', 'Scanned', 0, '2026-10-25', '2026-10-25', 1);
                ");

                // Scan snapshot
                await conn.ExecuteAsync("INSERT INTO scan_snapshots (id, order_id, started_at, scan_scope, status, app_version) VALUES (1, 1, '2026-10-25', 'Order', 'Completed', '2.0.0');");

                // Order items
                await conn.ExecuteAsync(@"
                    INSERT INTO order_item_scans (id, scan_snapshot_id, order_id, specification_folder_name, specification_relative_path, source_count, print_folder_status, scan_status)
                    VALUES (1, 1, 1, '13x18', '13x18', 10, 'AutoResolved', 'Normal'),
                           (2, 1, 1, '15x21', '15x21', 5, 'AutoResolved', 'Normal'),
                           (3, 1, 2, '20x30', '20x30', 2, 'AutoResolved', 'Normal');
                ");

                // Customer bills (1 locked)
                await conn.ExecuteAsync(@"
                    INSERT INTO customer_bills (id, bill_number, customer_id, customer_name_snapshot, period_start, period_end, status, created_at, updated_at)
                    VALUES (1, 'BILL-001', 1, 'Khách Test', '2026-10-25', '2026-10-25', 'Locked', '2026-10-25', '2026-10-25');
                ");

                // V1 bills table (1 locked)
                await conn.ExecuteAsync(@"
                    INSERT INTO bills (id, order_id, customer_id, status, subtotal, created_at, updated_at)
                    VALUES (1, 1, 1, 'LOCKED', 100000, '2026-10-25', '2026-10-25');
                ");

                // Cloud sync queue (1 pending)
                await conn.ExecuteAsync(@"
                    INSERT INTO cloud_sync_queue (id, entity_type, entity_id, action, status, created_at)
                    VALUES (1, 'Order', 1, 'upsert', 'Pending', '2026-10-25');
                ");

                // Cloud operational outbox (1 pending item)
                await conn.ExecuteAsync(@"
                    INSERT INTO cloud_operational_outbox (sequence, operation_id, entity_type, entity_id, value_json, created_at)
                    VALUES (1, 'OP-001', 'Order', 1, '{}', '2026-10-25');
                ");

                // Cloud print command ledger (1 unacked)
                await conn.ExecuteAsync(@"
                    INSERT INTO cloud_print_command_ledger (endpoint, stream_id, command_id, claim_token, status, acknowledged)
                    VALUES ('ep1', 'stream1', 1, 'token1', 'STARTED', 0);
                ");
            }

            // Clear pool so file handles are released before reading raw bytes
            SqliteConnection.ClearAllPools();

            // Snapshot content hash before dry-run
            byte[] hashBefore = SHA256.HashData(await File.ReadAllBytesAsync(tempDb));

            var inventoryService = new ServerInventoryService(tempDb);
            var report = await inventoryService.GenerateReportAsync();

            // Verify report numbers match
            report.Orders.Should().Be(2);
            report.OrderItems.Should().Be(3);
            report.CustomerBills.Should().Be(1);
            report.Bills.Should().Be(1);
            report.LockedBills.Should().Be(2); // 1 V1 bill + 1 V2 customer_bill
            report.LockedBillsCount.Should().Be(2);
            report.LockedBillsTotalAmountVnd.Should().Be(100000);
            report.LockedBillsSha256.Should().NotBeNullOrWhiteSpace();
            report.Customers.Should().Be(1);
            report.CloudSyncQueuePending.Should().Be(1);
            report.CloudOperationalOutboxPending.Should().Be(1);
            report.CloudPrintCommandLedgerUnacked.Should().Be(1);
            report.RootFolders.Should().Be(1);
            report.SchemaMigrationsMaxVersion.Should().Be(24);

            // Clear pool again to read bytes safely
            SqliteConnection.ClearAllPools();

            // Verify database was untouched
            byte[] hashAfter = SHA256.HashData(await File.ReadAllBytesAsync(tempDb));
            hashAfter.Should().Equal(hashBefore, "inventory dry-run must not mutate the database file");
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Fact]
    public async Task InventoryDryRun_OnV23Database_LeavesSchemaAtV23_AndDoesNotCreateFilesOrMutateHash()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_inv_v23_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "lalab_autoreport.db");

        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new DatabaseMigrator(factory);

            // 1. Migrate only up to V23 (prior to V24 ServerIdentity)
            await migrator.MigrateToVersionAsync(23);

            // Populate fixture rows
            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Khách V23', '2026-10-25', '2026-10-25');");
                await conn.ExecuteAsync("INSERT INTO customer_aliases (id, customer_id, alias_text, normalized_alias, created_at) VALUES (1, 1, 'K. V23', 'k v23', '2026-10-25');");
                await conn.ExecuteAsync("INSERT INTO root_folders (id, name, full_path, is_default, is_active, created_at, updated_at) VALUES (1, 'Root 1', 'C:\\Data', 1, 1, '2026-10-25', '2026-10-25');");
                await conn.ExecuteAsync(@"
                    INSERT INTO customer_bills (id, bill_number, customer_id, customer_name_snapshot, period_start, period_end, status, grand_total, is_paid, created_at, updated_at)
                    VALUES (1, 'BILL-V23-01', 1, 'Khách V23', '2026-10-25', '2026-10-25', 'Locked', 250000, 0, '2026-10-25', '2026-10-25');
                ");
            }

            SqliteConnection.ClearAllPools();

            // Snapshot directory files and DB hash
            var dirInfo = new DirectoryInfo(tempDir);
            var filesBefore = dirInfo.GetFiles().ToDictionary(f => f.Name, f => (f.Length, f.LastWriteTimeUtc));
            string hashBefore;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                hashBefore = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }

            // 2. Run actual Server process with inventory --dry-run
            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"inventory --dry-run --data-dir \"{tempDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();
            string stdout = await process!.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, $"inventory --dry-run failed with stderr: {stderr}");

            // 3. Assert schema_migrations max version is STILL 23
            using (var conn = factory.CreateConnection())
            {
                int maxVersion = await conn.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;");
                maxVersion.Should().Be(23, "inventory dry-run must NOT migrate the database");
            }
            SqliteConnection.ClearAllPools();

            // 4. Assert DB SHA-256 hash is unchanged
            string hashAfter;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                hashAfter = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
            hashAfter.Should().Be(hashBefore, "Source DB hash must remain identical");

            // 5. Assert no logs/, backups/, server.lock, or other files were created in source directory
            var filesAfter = dirInfo.GetFiles().ToDictionary(f => f.Name, f => (f.Length, f.LastWriteTimeUtc));
            filesAfter.Keys.Should().BeEquivalentTo(filesBefore.Keys, "No new files must be created in the source directory");
            Directory.Exists(Path.Combine(tempDir, "logs")).Should().BeFalse("logs dir must not be created");
            Directory.Exists(Path.Combine(tempDir, "backups")).Should().BeFalse("backups dir must not be created");
            File.Exists(Path.Combine(tempDir, "server.lock")).Should().BeFalse("server.lock must not be created");

            // 6. Assert new report fields exist in stdout JSON
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            root.GetProperty("schema_migrations_max_version").GetInt32().Should().Be(23);
            root.GetProperty("locked_bills_count").GetInt32().Should().Be(1);
            root.GetProperty("locked_bills_total_amount_vnd").GetInt64().Should().Be(250000);
            root.GetProperty("locked_bills_sha256").GetString().Should().NotBeNullOrWhiteSpace();
            root.GetProperty("outstanding_debt_vnd").GetInt64().Should().Be(250000);
            root.GetProperty("customers").GetInt32().Should().Be(1);
            root.GetProperty("customer_aliases").GetInt32().Should().Be(1);
            root.TryGetProperty("cloud_endpoint", out _).Should().BeTrue();
            root.TryGetProperty("cloud_stream_id", out _).Should().BeTrue();
            root.TryGetProperty("cloud_last_cursor", out _).Should().BeTrue();
            root.TryGetProperty("cloud_requires_reconciliation", out _).Should().BeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InventoryDryRun_WhenDatabaseDoesNotExist_ReturnsExit7_AndDoesNotCreateDirectory()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "lalab_inv_missing_" + Guid.NewGuid().ToString("N"));
        string missingDir = Path.Combine(baseDir, "nonexistent_subfolder");

        try
        {
            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"inventory --dry-run --data-dir \"{missingDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();
            string stdout = await process!.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(7, $"Expected exit code 7 for nonexistent database file, got {process.ExitCode}. Stderr: {stderr}");
            Directory.Exists(missingDir).Should().BeFalse("Directory must not be created when DB file does not exist");
        }
        finally
        {
            try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InventoryDryRun_WithUncheckpointedWal_CapturesCommittedRows_AndLeavesSourceUntouched()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_inv_wal_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "lalab_autoreport.db");

        SqliteConnection? writerConn = null;
        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new DatabaseMigrator(factory);
            await migrator.MigrateAsync();

            // 1. Initial seeded data
            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Khách 1', '2026-10-25', '2026-10-25');");
                await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            SqliteConnection.ClearAllPools();

            // 2. Open writer connection with wal_autocheckpoint=0 and insert a new customer into WAL
            writerConn = new SqliteConnection($"Data Source={dbPath};");
            await writerConn.OpenAsync();
            await writerConn.ExecuteAsync("PRAGMA wal_autocheckpoint = 0;");
            await writerConn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (2, 'Khách WAL Uncheckpointed', '2026-10-25', '2026-10-25');");

            // Verify WAL file exists and has content
            string walPath = dbPath + "-wal";
            File.Exists(walPath).Should().BeTrue("WAL file must exist after uncheckpointed commit");
            new FileInfo(walPath).Length.Should().BeGreaterThan(0);

            // Baseline hash of main db file and directory listing
            var dirInfo = new DirectoryInfo(tempDir);
            var filesBefore = dirInfo.GetFiles().ToDictionary(f => f.Name, f => (f.Length, f.LastWriteTimeUtc));
            string hashBefore;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                hashBefore = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }

            // 3. Run Server.exe inventory --dry-run
            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"inventory --dry-run --data-dir \"{tempDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();
            string stdout = await process!.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, $"inventory dry-run failed with stderr: {stderr}");

            // 4. Assert new customer from uncheckpointed WAL is included in customers count
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            root.GetProperty("customers").GetInt32().Should().Be(2, "Uncheckpointed customer in WAL must be captured in inventory report");

            // 5. Assert source DB hash and directory listing are completely unchanged
            string hashAfter;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                hashAfter = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
            hashAfter.Should().Be(hashBefore, "Source DB file hash must remain unchanged");

            var filesAfter = dirInfo.GetFiles().ToDictionary(f => f.Name, f => (f.Length, f.LastWriteTimeUtc));
            filesAfter.Keys.Should().BeEquivalentTo(filesBefore.Keys, "No files should be added or removed from source dir");
            filesAfter[Path.GetFileName(dbPath)].Length.Should().Be(filesBefore[Path.GetFileName(dbPath)].Length);
        }
        finally
        {
            if (writerConn != null)
            {
                try { writerConn.Close(); writerConn.Dispose(); } catch { }
            }
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InventoryDryRun_WhenGrandTotalColumnMissing_Exits8_AndOutputsNoJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_inv_broken_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "lalab_autoreport.db");

        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new DatabaseMigrator(factory);
            await migrator.MigrateAsync();

            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Khách 1', '2026-10-25', '2026-10-25');");
                await conn.ExecuteAsync(@"
                    INSERT INTO customer_bills (id, bill_number, customer_id, customer_name_snapshot, period_start, period_end, status, grand_total, is_paid, created_at, updated_at)
                    VALUES (1, 'BILL-01', 1, 'Khách 1', '2026-10-25', '2026-10-25', 'Locked', 100000, 0, '2026-10-25', '2026-10-25');
                ");

                // Drop grand_total column
                await conn.ExecuteAsync("ALTER TABLE customer_bills DROP COLUMN grand_total;");
            }
            SqliteConnection.ClearAllPools();

            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"inventory --dry-run --data-dir \"{tempDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();
            string stdout = await process!.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(8, $"Expected exit code 8 on broken required schema, got {process.ExitCode}. Stderr: {stderr}");
            stdout.Trim().Should().BeEmpty("No JSON report must be emitted to stdout on exit 8");
            stderr.Should().Contain("INVENTORY");
            stderr.Should().Contain("grand_total");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InventoryDryRun_WhenCustomerBillsTableDropped_OnV24_Exits8_AndOutputsNoJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_inv_cb_drop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "lalab_autoreport.db");

        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new DatabaseMigrator(factory);
            await migrator.MigrateAsync();

            using (var conn = new SqliteConnection($"Data Source={dbPath};Foreign Keys=False;"))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Khách 1', '2026-10-25', '2026-10-25');");
                // Insert legacy bills row as well to verify it does NOT fall back to legacy bills on modern V24 schema
                await conn.ExecuteAsync("INSERT INTO bills (id, order_id, customer_id, status, subtotal, created_at, updated_at) VALUES (1, 1, 1, 'LOCKED', 100000, '2026-10-25', '2026-10-25');");

                // Drop required V2 billing table
                await conn.ExecuteAsync("DROP TABLE customer_bills;");
            }
            SqliteConnection.ClearAllPools();

            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"inventory --dry-run --data-dir \"{tempDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();
            string stdout = await process!.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(8, $"Expected exit code 8 when dropping required customer_bills, got {process.ExitCode}. Stderr: {stderr}");
            stdout.Trim().Should().BeEmpty("No JSON report must be emitted to stdout on exit 8");
            stderr.Should().Contain("INVENTORY");
            stderr.Should().Contain("customer_bills");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("customer_bills", "phone_snapshot")]
    [InlineData("customer_bill_lines", "quantity_override_reason")]
    public async Task InventoryDryRun_WhenRequiredColumnDropped_Exits8_AndOutputsNoJson(string tableName, string columnName)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_inv_coldrop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "lalab_autoreport.db");

        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new DatabaseMigrator(factory);
            await migrator.MigrateAsync();

            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Khách 1', '2026-10-25', '2026-10-25');");
                // Drop required column
                await conn.ExecuteAsync($"ALTER TABLE {tableName} DROP COLUMN {columnName};");
            }
            SqliteConnection.ClearAllPools();

            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"inventory --dry-run --data-dir \"{tempDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();
            string stdout = await process!.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(8, $"Expected exit code 8 when dropping required column {columnName}, got {process.ExitCode}. Stderr: {stderr}");
            stdout.Trim().Should().BeEmpty("No JSON report must be emitted to stdout on exit 8");
            stderr.Should().Contain("INVENTORY");
            stderr.Should().Contain(columnName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InventoryDryRun_WhenOptionalLegacyTableDropped_Exits0_WithNullField()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_inv_opt_drop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "lalab_autoreport.db");

        try
        {
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new DatabaseMigrator(factory);
            await migrator.MigrateAsync();

            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Khách 1', '2026-10-25', '2026-10-25');");
                // Drop optional legacy table cloud_sync_queue
                await conn.ExecuteAsync("DROP TABLE cloud_sync_queue;");
            }
            SqliteConnection.ClearAllPools();

            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"inventory --dry-run --data-dir \"{tempDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();
            string stdout = await process!.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, $"Expected exit code 0 when dropping optional legacy table, got {process.ExitCode}. Stderr: {stderr}");
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            root.GetProperty("cloud_sync_queue_pending").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task InventoryDryRun_LockedBillsSnapshotSha256_SensitivityAndDeterminism()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), "lalab_hash_test_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(tempDb);
            var migrator = new DatabaseMigrator(factory);
            await migrator.MigrateAsync();

            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("INSERT INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Khách A', '2026-10-25', '2026-10-25');");
                await conn.ExecuteAsync("INSERT INTO orders (id, work_date, customer_id, original_folder_name, relative_path, status, created_at, updated_at) VALUES (1, '2026-10-25', 1, 'ORD-1', '2026-10-25/ORD-1', 'Completed', '2026-10-25', '2026-10-25');");
                await conn.ExecuteAsync("INSERT INTO scan_snapshots (id, order_id, started_at, scan_scope, status, app_version) VALUES (1, 1, '2026-10-25', 'Full', 'Completed', '1.0.0');");
                await conn.ExecuteAsync("INSERT INTO order_item_scans (id, scan_snapshot_id, order_id, specification_folder_name, specification_relative_path, source_count, print_folder_status, scan_status) VALUES (1, 1, 1, 'Item 1', 'Item 1', 10, 'Resolved', 'Success');");

                // Locked customer bill with lines
                await conn.ExecuteAsync(@"
                    INSERT INTO customer_bills (id, bill_number, bill_type, customer_id, customer_name_snapshot, period_start, period_end, status, grand_total, product_subtotal, adjustments_total, is_paid, locked_at, note, created_at, updated_at)
                    VALUES (1, 'BILL-LOCKED-01', 'Retail', 1, 'Khách A', '2026-10-25', '2026-10-25', 'Locked', 100000, 100000, 0, 0, '2026-10-25T10:00:00Z', 'Note 1', '2026-10-25', '2026-10-25');
                ");
                await conn.ExecuteAsync(@"
                    INSERT INTO customer_bill_lines (id, bill_id, order_id, product_job_id, product_specification_id, product_name_snapshot, billing_method_snapshot, scanned_quantity, billed_quantity, configured_unit_price, billed_unit_price, line_total, is_included, sort_order, folder_resolution_mode_snapshot)
                    VALUES (1, 1, 1, 1, 1, 'In Ảnh 10x15', 'FileCount', 10, 10, 10000, 10000, 100000, 1, 1, 'AutoResolved');
                ");

                // Unlocked (draft) bill
                await conn.ExecuteAsync(@"
                    INSERT INTO customer_bills (id, bill_number, bill_type, customer_id, customer_name_snapshot, period_start, period_end, status, grand_total, product_subtotal, adjustments_total, is_paid, created_at, updated_at)
                    VALUES (2, 'BILL-DRAFT-02', 'Retail', 1, 'Khách A', '2026-10-25', '2026-10-25', 'Draft', 50000, 50000, 0, 0, '2026-10-25', '2026-10-25');
                ");
            }
            SqliteConnection.ClearAllPools();

            var service = new ServerInventoryService(tempDb);

            // 1. Baseline report & Determinism check
            var reportBaseline1 = await service.GenerateReportAsync();
            var reportBaseline2 = await service.GenerateReportAsync();
            string baselineHash = reportBaseline1.LockedBillsSnapshotSha256;
            baselineHash.Should().NotBeNullOrWhiteSpace();
            reportBaseline2.LockedBillsSnapshotSha256.Should().Be(baselineHash, "Snapshot hash must be deterministic across consecutive runs");

            // 2. Table-driven sensitivity tests across locked bill headers, lines, and adjustments
            var mutationCases = new (string Name, string MutateSql, string RevertSql)[]
            {
                ("customer_name_snapshot", "UPDATE customer_bills SET customer_name_snapshot = 'Khách B' WHERE id = 1;", "UPDATE customer_bills SET customer_name_snapshot = 'Khách A' WHERE id = 1;"),
                ("phone_snapshot", "UPDATE customer_bills SET phone_snapshot = '0901234567' WHERE id = 1;", "UPDATE customer_bills SET phone_snapshot = NULL WHERE id = 1;"),
                ("shipping_address_snapshot", "UPDATE customer_bills SET shipping_address_snapshot = '123 Nguyen Hue' WHERE id = 1;", "UPDATE customer_bills SET shipping_address_snapshot = NULL WHERE id = 1;"),
                ("export_file_path", "UPDATE customer_bills SET export_file_path = 'exports/bill1.xlsx' WHERE id = 1;", "UPDATE customer_bills SET export_file_path = NULL WHERE id = 1;"),
                ("header note", "UPDATE customer_bills SET note = 'Altered Note' WHERE id = 1;", "UPDATE customer_bills SET note = 'Note 1' WHERE id = 1;"),
                ("line billed_quantity", "UPDATE customer_bill_lines SET billed_quantity = 15 WHERE id = 1;", "UPDATE customer_bill_lines SET billed_quantity = 10 WHERE id = 1;"),
                ("line quantity_override_reason", "UPDATE customer_bill_lines SET quantity_override_reason = 'Khách xin thêm' WHERE id = 1;", "UPDATE customer_bill_lines SET quantity_override_reason = NULL WHERE id = 1;"),
                ("line price_override_reason", "UPDATE customer_bill_lines SET price_override_reason = 'Chiết khấu VIP' WHERE id = 1;", "UPDATE customer_bill_lines SET price_override_reason = NULL WHERE id = 1;"),
                ("line final_print_folder_path", "UPDATE customer_bill_lines SET final_print_folder_path = 'retouch/leaf_final' WHERE id = 1;", "UPDATE customer_bill_lines SET final_print_folder_path = NULL WHERE id = 1;"),
                ("line note", "UPDATE customer_bill_lines SET note = 'Line note altered' WHERE id = 1;", "UPDATE customer_bill_lines SET note = NULL WHERE id = 1;"),
                ("adding adjustment", "INSERT INTO customer_bill_adjustments (id, bill_id, type, label, direction, amount, note, sort_order) VALUES (1, 1, 'Discount', 'Giảm giá', 'Subtract', 5000, 'Ghi chú phụ thu', 1);", "DELETE FROM customer_bill_adjustments WHERE id = 1;")
            };

            foreach (var (name, mutateSql, revertSql) in mutationCases)
            {
                using (var conn = factory.CreateConnection())
                {
                    await conn.ExecuteAsync(mutateSql);
                }
                SqliteConnection.ClearAllPools();
                var mutReport = await service.GenerateReportAsync();
                mutReport.LockedBillsSnapshotSha256.Should().NotBe(
                    baselineHash,
                    $"Altering {name} on locked bill must alter snapshot hash");

                // Revert
                using (var conn = factory.CreateConnection())
                {
                    await conn.ExecuteAsync(revertSql);
                }
                SqliteConnection.ClearAllPools();
                var revReport = await service.GenerateReportAsync();
                revReport.LockedBillsSnapshotSha256.Should().Be(
                    baselineHash,
                    $"Reverting {name} on locked bill must restore baseline snapshot hash");
            }

            // 3. Adjustment property mutations (direction, note)
            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("INSERT INTO customer_bill_adjustments (id, bill_id, type, label, direction, amount, note, sort_order) VALUES (1, 1, 'Discount', 'Giảm giá', 'Subtract', 5000, 'Ban đầu', 1);");
            }
            SqliteConnection.ClearAllPools();
            var adjBaselineReport = await service.GenerateReportAsync();
            string adjBaselineHash = adjBaselineReport.LockedBillsSnapshotSha256;
            adjBaselineHash.Should().NotBe(baselineHash);

            // Mutate adjustment direction
            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("UPDATE customer_bill_adjustments SET direction = 'Add' WHERE id = 1;");
            }
            SqliteConnection.ClearAllPools();
            var mutAdjDirReport = await service.GenerateReportAsync();
            mutAdjDirReport.LockedBillsSnapshotSha256.Should().NotBe(adjBaselineHash, "Altering adjustment direction must alter snapshot hash");

            // Mutate adjustment note
            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("UPDATE customer_bill_adjustments SET direction = 'Subtract', note = 'Ghi chú mới' WHERE id = 1;");
            }
            SqliteConnection.ClearAllPools();
            var mutAdjNoteReport = await service.GenerateReportAsync();
            mutAdjNoteReport.LockedBillsSnapshotSha256.Should().NotBe(adjBaselineHash, "Altering adjustment note must alter snapshot hash");

            // Cleanup adjustment
            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("DELETE FROM customer_bill_adjustments WHERE id = 1;");
            }
            SqliteConnection.ClearAllPools();

            // 4. Mutate an UNLOCKED (Draft) bill -> Hash MUST NOT change!
            using (var conn = factory.CreateConnection())
            {
                await conn.ExecuteAsync("UPDATE customer_bills SET grand_total = 999999, note = 'Draft modified' WHERE id = 2;");
            }
            SqliteConnection.ClearAllPools();
            var reportMutDraft = await service.GenerateReportAsync();
            reportMutDraft.LockedBillsSnapshotSha256.Should().Be(baselineHash, "Modifying an unlocked draft bill must NOT alter locked bills snapshot hash");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    #endregion

    #region 5. Health Info Provider & Endpoint Tests

    [Fact]
    public void HealthInfoProvider_ReturnsExpectedState_BeforeAndAfterReady()
    {
        bool isReady = false;
        var provider = new ServerHealthInfoProvider(
            startedAtUtc: new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero),
            schemaVersionProvider: () => 24,
            isReadyProvider: () => isReady,
            componentsProvider: () => new Dictionary<string, string>
            {
                ["database"] = "ready",
                ["mobileApi"] = isReady ? "ready" : "unavailable"
            });

        // Before ready
        var liveHealth = provider.GetLiveHealth();
        liveHealth.Status.Should().Be("Live");
        liveHealth.BuildId.Should().NotBeNullOrWhiteSpace();
        liveHealth.AssemblyVersion.Should().NotBeNullOrWhiteSpace();
        liveHealth.SchemaVersion.Should().Be(24);
        liveHealth.Cloud.Should().Be("disabled");
        liveHealth.AutoScan.Should().Be("disabled");

        var readyBefore = provider.GetReadyHealth();
        readyBefore.Status.Should().Be("NotReady");
        readyBefore.Components.Should().NotBeNull();
        readyBefore.Components!["database"].Should().Be("ready");
        readyBefore.Components["mobileApi"].Should().Be("unavailable");
        provider.IsReady.Should().BeFalse();

        // After ready
        isReady = true;
        var readyAfter = provider.GetReadyHealth();
        readyAfter.Status.Should().Be("Ready");
        readyAfter.Components!["mobileApi"].Should().Be("ready");
        provider.IsReady.Should().BeTrue();
        readyAfter.Cloud.Should().Be("disabled");
        readyAfter.AutoScan.Should().Be("disabled");
    }

    [Fact]
    public async Task Readiness_WhenMobilePortOccupied_Returns503_WithComponentStatusUnavailable_AndLiveRemains200()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_port_conflict_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        int occupiedPort = GetAvailablePort();
        int healthPort = GetAvailablePort();

        // Occupy the port with a TcpListener
        var occupyingListener = new TcpListener(IPAddress.Loopback, occupiedPort);
        occupyingListener.Start();

        Process? serverProcess = null;
        try
        {
            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"--data-dir \"{tempDir}\" --port {occupiedPort} --health-port {healthPort}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            serverProcess = Process.Start(startInfo);
            serverProcess.Should().NotBeNull();

            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{healthPort}") };

            // Wait for health endpoint to become responsive
            HttpResponseMessage? liveResp = null;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    liveResp = await httpClient.GetAsync("/health/live");
                    if (liveResp.IsSuccessStatusCode) break;
                }
                catch
                {
                    await Task.Delay(250);
                }
            }

            liveResp.Should().NotBeNull("Server health endpoint must become responsive");
            liveResp!.StatusCode.Should().Be(HttpStatusCode.OK);
            string liveJson = await liveResp.Content.ReadAsStringAsync();
            using var liveDoc = JsonDocument.Parse(liveJson);
            liveDoc.RootElement.GetProperty("status").GetString().Should().Be("Live");

            // GET /health/ready MUST return 503 because mobileApi failed to start
            var readyResp = await httpClient.GetAsync("/health/ready");
            readyResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            string readyJson = await readyResp.Content.ReadAsStringAsync();
            using var readyDoc = JsonDocument.Parse(readyJson);
            var readyRoot = readyDoc.RootElement;
            readyRoot.GetProperty("status").GetString().Should().Be("NotReady");

            var components = readyRoot.GetProperty("components");
            components.GetProperty("database").GetString().Should().Be("ready");
            components.GetProperty("mobileApi").GetString().Should().Be("unavailable");
        }
        finally
        {
            occupyingListener.Stop();
            if (serverProcess != null && !serverProcess.HasExited)
            {
                try { serverProcess.Kill(entireProcessTree: true); } catch { }
            }
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HealthEndpoints_DoNotExposeAbsolutePaths_OrSecrets()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_health_privacy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        int mobilePort = GetAvailablePort();
        int healthPort = GetAvailablePort();

        Process? serverProcess = null;
        try
        {
            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"--data-dir \"{tempDir}\" --port {mobilePort} --health-port {healthPort}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            serverProcess = Process.Start(startInfo);
            serverProcess.Should().NotBeNull();

            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{healthPort}") };

            // Wait for health endpoint to become responsive
            HttpResponseMessage? readyResp = null;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    readyResp = await httpClient.GetAsync("/health/ready");
                    if (readyResp.IsSuccessStatusCode) break;
                }
                catch
                {
                    await Task.Delay(250);
                }
            }

            readyResp.Should().NotBeNull();
            readyResp!.StatusCode.Should().Be(HttpStatusCode.OK);
            string readyJson = await readyResp.Content.ReadAsStringAsync();

            var liveResp = await httpClient.GetAsync("/health/live");
            liveResp.StatusCode.Should().Be(HttpStatusCode.OK);
            string liveJson = await liveResp.Content.ReadAsStringAsync();

            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            // Assert NO absolute paths or secrets in live or ready JSON
            foreach (var json in new[] { liveJson, readyJson })
            {
                json.Should().NotContain("dataDir", "dataDir property must not be present in health responses per ADR D9");
                json.Should().NotContain(tempDir, "Temporary or real dataDir path must not be leaked");
                json.Should().NotContain(programData, "ProgramData path must not be leaked");
                json.Should().NotMatchRegex(@"[A-Za-z]:\\", "Drive letters with absolute paths must not be present");
                json.Should().NotContain(@"\\", "UNC network paths must not be present");
                json.Should().NotContainEquivalentOf("pin");
                json.Should().NotContainEquivalentOf("secret");
                json.Should().NotContainEquivalentOf("token");
            }
        }
        finally
        {
            if (serverProcess != null && !serverProcess.HasExited)
            {
                try { serverProcess.Kill(entireProcessTree: true); } catch { }
            }
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Readiness_WhenSchemaMigrationsDropped_Returns503_DatabaseUnavailable_AndRecoversWhenRestored()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_db_probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        int mobilePort = GetAvailablePort();
        int healthPort = GetAvailablePort();

        Process? serverProcess = null;
        try
        {
            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"--data-dir \"{tempDir}\" --port {mobilePort} --health-port {healthPort}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            serverProcess = Process.Start(startInfo);
            serverProcess.Should().NotBeNull();

            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{healthPort}") };

            // 1. Wait for server to become Ready
            HttpResponseMessage? readyResp = null;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    readyResp = await httpClient.GetAsync("/health/ready");
                    if (readyResp.IsSuccessStatusCode) break;
                }
                catch
                {
                    await Task.Delay(250);
                }
            }

            readyResp.Should().NotBeNull();
            readyResp!.StatusCode.Should().Be(HttpStatusCode.OK);
            string readyJson = await readyResp.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(readyJson))
            {
                doc.RootElement.GetProperty("status").GetString().Should().Be("Ready");
                doc.RootElement.GetProperty("components").GetProperty("database").GetString().Should().Be("ready");
            }

            // 2. Drop schema_migrations on the running database
            string dbPath = Path.Combine(tempDir, "lalab_autoreport.db");
            using (var conn = new SqliteConnection($"Data Source={dbPath};"))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DROP TABLE schema_migrations;";
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            // 3. /health/ready must immediately return 503 NotReady with database unavailable
            var notReadyResp = await httpClient.GetAsync("/health/ready");
            notReadyResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            string notReadyJson = await notReadyResp.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(notReadyJson))
            {
                var root = doc.RootElement;
                root.GetProperty("status").GetString().Should().Be("NotReady");
                root.GetProperty("components").GetProperty("database").GetString().Should().Contain("unavailable");
            }

            // /health/live must STILL return 200 Live
            var liveResp = await httpClient.GetAsync("/health/live");
            liveResp.StatusCode.Should().Be(HttpStatusCode.OK);
            string liveJson = await liveResp.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(liveJson))
            {
                doc.RootElement.GetProperty("status").GetString().Should().Be("Live");
            }

            // 4. Restore valid schema_migrations table with version 24
            using (var conn = new SqliteConnection($"Data Source={dbPath};"))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY); INSERT INTO schema_migrations VALUES (24);";
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            // 5. Next request to /health/ready must recover to 200 Ready!
            var recoveredResp = await httpClient.GetAsync("/health/ready");
            recoveredResp.StatusCode.Should().Be(HttpStatusCode.OK);
            string recoveredJson = await recoveredResp.Content.ReadAsStringAsync();
            using (var doc = JsonDocument.Parse(recoveredJson))
            {
                var root = doc.RootElement;
                root.GetProperty("status").GetString().Should().Be("Ready");
                root.GetProperty("components").GetProperty("database").GetString().Should().Be("ready");
            }
        }
        finally
        {
            if (serverProcess != null && !serverProcess.HasExited)
            {
                try { serverProcess.Kill(entireProcessTree: true); } catch { }
            }
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    #endregion

    #region Helpers / Fakes

    private class FakeDatabaseBackupService : IDatabaseBackupService
    {
        private readonly bool _shouldThrow;
        public bool BackupCalled { get; private set; }

        public FakeDatabaseBackupService(bool shouldThrow)
        {
            _shouldThrow = shouldThrow;
        }

        public async Task<DatabaseBackupInfo> CreateBackupAsync(string? customDestinationPath = null, CancellationToken cancellationToken = default)
        {
            BackupCalled = true;
            if (_shouldThrow)
            {
                throw new IOException("Simulated backup failure: disk full or I/O error");
            }

            string backupFile = Path.Combine(Path.GetTempPath(), "fake_backup_" + Guid.NewGuid().ToString("N") + ".db");
            await File.WriteAllTextAsync(backupFile, "backup content", cancellationToken);

            return new DatabaseBackupInfo(backupFile, Path.GetFileName(backupFile), 14, DateTime.UtcNow);
        }

        public Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DatabaseBackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>(Array.Empty<DatabaseBackupInfo>());
        public string GetDatabasePath() => "fake.db";
    }

    private class SpyDatabaseMigrator : IDatabaseMigrator
    {
        public bool MigratorCalled { get; private set; }

        public Task MigrateAsync(CancellationToken cancellationToken = default)
        {
            MigratorCalled = true;
            return Task.CompletedTask;
        }

        public Task MigrateToVersionAsync(int targetVersion, CancellationToken cancellationToken = default)
        {
            MigratorCalled = true;
            return Task.CompletedTask;
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindServerExePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] relativePaths = new[]
        {
            "LalabAutoReport.Server.exe",
            @"..\..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe",
            @"..\..\..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe",
            @"..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe"
        };

        foreach (var rel in relativePaths)
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (File.Exists(full))
            {
                return full;
            }
        }

        throw new FileNotFoundException($"Không tìm thấy LalabAutoReport.Server.exe từ thư mục: {baseDir}");
    }

    #endregion
}
