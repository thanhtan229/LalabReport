using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.ApplicationCore.Commands;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Commands;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class CommandCrashRecoveryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DatabaseMigrator _migrator;
    private readonly string _harnessDllPath;

    public CommandCrashRecoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabCrashTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_crash.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _migrator = new DatabaseMigrator(_connectionFactory);

        _harnessDllPath = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "LalabAutoReport.CrashHarness", "bin",
            new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory).Parent?.Name ?? "Debug",
            new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory).Name,
            "LalabAutoReport.CrashHarness.dll"));
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

    private async Task<long> SeedOrderAsync()
    {
        await _migrator.MigrateAsync();
        using var conn = _connectionFactory.CreateConnection();
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

    private int RunCrashHarness(string crashPoint, long orderId, string opId, long expectedRev, bool delivered)
    {
        File.Exists(_harnessDllPath).Should().BeTrue("CrashHarness.dll must be built.");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{_harnessDllPath}\" --db \"{_tempDbPath}\" --crash-point {crashPoint} --order-id {orderId} --op-id \"{opId}\" --expected-rev {expectedRev} --delivered {delivered}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(psi)!;
        process.WaitForExit(15000);
        return process.ExitCode;
    }

    [Fact]
    public async Task CrashAfterBusinessUpdate_BeforeCommit_NoChangeNoReceipt()
    {
        long orderId = await SeedOrderAsync();
        string opId = Guid.NewGuid().ToString();

        // Run child process which will crash before commit
        int exitCode = RunCrashHarness("before_commit", orderId, opId, 0, true);
        exitCode.Should().NotBe(0, "Process must terminate abnormally on FailFast.");

        // Clear pools and inspect database
        SqliteConnection.ClearAllPools();
        using var conn = _connectionFactory.CreateConnection();

        // 1. Business update was rolled back
        var order = await conn.QuerySingleAsync<dynamic>(
            "SELECT is_delivered, delivered_revision FROM orders WHERE id = @Id", new { Id = orderId });
        ((long)order.is_delivered).Should().Be(0);
        ((long)order.delivered_revision).Should().Be(0);

        // 2. No receipt exists
        string normOp = Guid.Parse(opId).ToString("N");
        var receiptCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM command_receipts WHERE operation_id = @Op", new { Op = normOp });
        receiptCount.Should().Be(0);
    }

    [Fact]
    public async Task CrashAfterCommit_BeforeResponse_RetryReturnsStoredResult()
    {
        long orderId = await SeedOrderAsync();
        string opId = Guid.NewGuid().ToString();

        // Run child process which will crash after commit but before returning HTTP response
        int exitCode = RunCrashHarness("after_commit", orderId, opId, 0, true);
        exitCode.Should().NotBe(0, "Process must terminate abnormally on FailFast.");

        // Clear pools and inspect database
        SqliteConnection.ClearAllPools();
        using (var conn = _connectionFactory.CreateConnection())
        {
            // 1. Business update WAS committed
            var order = await conn.QuerySingleAsync<dynamic>(
                "SELECT is_delivered, delivered_revision FROM orders WHERE id = @Id", new { Id = orderId });
            ((long)order.is_delivered).Should().Be(1);
            ((long)order.delivered_revision).Should().Be(1);

            // 2. Receipt exists
            string normOp = Guid.Parse(opId).ToString("N");
            var receiptCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM command_receipts WHERE operation_id = @Op", new { Op = normOp });
            receiptCount.Should().Be(1);
        }

        // 3. Client retries with the same operationId -> gets stored result without error!
        var store = new SqliteOrderOperationalCommandStore(_connectionFactory);
        var inFlight = new InFlightOperationRegistry();
        var lookup = new SqliteCommandReceiptReader(_connectionFactory);
        var handler = new OrderOperationalCommandHandler(store, inFlight, lookup);
        var actor = new ActorContext("session", "Admin", new HashSet<string> { Capabilities.OrdersSetDelivered });

        var retryResult = await handler.SetDeliveredAsync(actor, orderId, new SetOrderDeliveredRequest(opId, 0, true));
        retryResult.IsReplay.Should().BeTrue();
        retryResult.FieldState.Revision.Should().Be(1);
        retryResult.FieldState.Changed.Should().BeTrue();
    }

    [Fact]
    public async Task RestartBetweenCommitAndRetry_ReceiptSurvives()
    {
        long orderId = await SeedOrderAsync();
        string opId = Guid.NewGuid().ToString();

        var store = new SqliteOrderOperationalCommandStore(_connectionFactory);
        var inFlight = new InFlightOperationRegistry();
        var lookup = new SqliteCommandReceiptReader(_connectionFactory);
        var handler = new OrderOperationalCommandHandler(store, inFlight, lookup);
        var actor = new ActorContext("session", "Admin", new HashSet<string> { Capabilities.OrdersSetDelivered });

        // Commit command
        var res1 = await handler.SetDeliveredAsync(actor, orderId, new SetOrderDeliveredRequest(opId, 0, true));
        res1.IsReplay.Should().BeFalse();

        // Simulate server restart: clear pools, instantiate fresh services
        SqliteConnection.ClearAllPools();
        var freshConnFactory = new SqliteConnectionFactory(_tempDbPath);
        var freshStore = new SqliteOrderOperationalCommandStore(freshConnFactory);
        var freshInFlight = new InFlightOperationRegistry();
        var freshLookup = new SqliteCommandReceiptReader(freshConnFactory);
        var freshHandler = new OrderOperationalCommandHandler(freshStore, freshInFlight, freshLookup);

        // Retry returns stored receipt surviving restart
        var res2 = await freshHandler.SetDeliveredAsync(actor, orderId, new SetOrderDeliveredRequest(opId, 0, true));
        res2.IsReplay.Should().BeTrue();
        res2.ResultJson.Should().Be(res1.ResultJson);
    }
}
