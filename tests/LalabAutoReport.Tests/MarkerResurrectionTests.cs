using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.ApplicationCore.Commands;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Commands;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.FileSystem;
using LalabAutoReport.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class MarkerResurrectionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DatabaseMigrator _migrator;
    private readonly SqliteOrderRepository _orderRepo;

    public MarkerResurrectionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabMarkerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_marker.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _migrator = new DatabaseMigrator(_connectionFactory);
        _orderRepo = new SqliteOrderRepository(_connectionFactory);
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

    private async Task<Order> SeedOrderAsync(bool isPrinted = false, long printedRev = 0)
    {
        await _migrator.MigrateAsync();
        var order = new Order
        {
            WorkDate = "2026-10-26",
            OriginalFolderName = "TestFolder",
            RelativePath = "2026-10-26\\TestFolder",
            Status = OrderStatus.Scanned,
            IsPrinted = isPrinted,
            PrintProgress = isPrinted ? PrintStatus.Printed : PrintStatus.NotPrinted,
            PrintedRevision = printedRev
        };
        var snapshot = new ScanSnapshot { StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow };
        await _orderRepo.SaveOrderAsync(order, snapshot);
        return order;
    }

    [Fact]
    public async Task ScanVsSetPrinted_ScanDoesNotOverwriteConcurrentCommand()
    {
        // Order starts with is_printed = true at printed_revision 0
        var order = await SeedOrderAsync(isPrinted: true, printedRev: 0);

        // 1. Scan reads the existing order (observedPrintedRevision = 0, says printed = true)
        var scanOrder = await _orderRepo.GetOrderByIdAsync(order.Id);
        Assert.NotNull(scanOrder);
        scanOrder.PrintedRevision.Should().Be(0);

        // 2. Concurrently, a LAN command sets printed = false (bumps printed_revision to 1)
        var store = new SqliteOrderOperationalCommandStore(_connectionFactory);
        var inFlight = new InFlightOperationRegistry();
        var lookup = new SqliteCommandReceiptReader(_connectionFactory);
        var handler = new OrderOperationalCommandHandler(store, inFlight, lookup);
        var actor = new ActorContext("session", "Admin", new HashSet<string> { Capabilities.OrdersSetPrinted });

        var cmdResult = await handler.SetPrintedAsync(
            actor, order.Id, new SetOrderPrintedRequest(Guid.NewGuid().ToString(), 0, false));
        cmdResult.FieldState.Revision.Should().Be(1);

        // 3. Scan completes and saves with old observedPrintedRevision = 0 wanting is_printed = true
        var snapshot = new ScanSnapshot { StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow };
        await _orderRepo.SaveOrderAsync(scanOrder, snapshot);

        // 4. Assert: printed remains false because scan's revision guard prevented overwrite!
        var finalOrder = await _orderRepo.GetOrderByIdAsync(order.Id);
        Assert.NotNull(finalOrder);
        finalOrder.IsPrinted.Should().BeFalse();
        finalOrder.PrintedRevision.Should().Be(1);
    }

    [Fact]
    public async Task ScanSaveOrder_DoesNotTouchDeliveredOrNote()
    {
        var order = await SeedOrderAsync();

        // Update delivered and note via direct SQL
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                UPDATE orders
                SET is_delivered = 1, delivered_by = 'Chủ tiệm', note = 'Original Note'
                WHERE id = @Id
            ", new { Id = order.Id });
        }

        // Run SaveOrderAsync with scan data that does NOT have delivered or note
        var scanOrder = await _orderRepo.GetOrderByIdAsync(order.Id);
        Assert.NotNull(scanOrder);
        scanOrder.Note = "Mutated Note That Should Not Persist Via Scan";

        var snapshot = new ScanSnapshot { StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow };
        await _orderRepo.SaveOrderAsync(scanOrder, snapshot);

        // Delivered and Note must remain untouched
        var loaded = await _orderRepo.GetOrderByIdAsync(order.Id);
        Assert.NotNull(loaded);
        loaded.IsDelivered.Should().BeTrue();
        loaded.DeliveredBy.Should().Be("Chủ tiệm");
        loaded.Note.Should().Be("Original Note");
    }

    private class MockPrintStatusService : IPrintStatusService
    {
        private readonly bool _folderPrintedResult;
        public int CallCount { get; private set; }

        public MockPrintStatusService(bool folderPrintedResult)
        {
            _folderPrintedResult = folderPrintedResult;
        }

        public event Action<string, PrintStatus>? PrintStatusChanged { add { } remove { } }

        public Task<PrintStatus> GetStatusAsync(string folderPath, CancellationToken cancellationToken = default)
            => Task.FromResult(_folderPrintedResult ? PrintStatus.Printed : PrintStatus.NotPrinted);

        public Task<bool> IsFolderPrintedAsync(string folderPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_folderPrintedResult);
        }

        public Task<TogglePrintStatusResult> ToggleStatusAsync(string folderPath, string markedBy = "ExplorerContextMenu", CancellationToken cancellationToken = default)
            => Task.FromResult(new TogglePrintStatusResult { IsSuccess = true, NewStatus = _folderPrintedResult ? PrintStatus.Printed : PrintStatus.NotPrinted });

        public Task<TogglePrintStatusResult> SetStatusAsync(string folderPath, PrintStatus targetStatus, string markedBy = "ExplorerContextMenu", CancellationToken cancellationToken = default)
            => Task.FromResult(new TogglePrintStatusResult { IsSuccess = true, NewStatus = targetStatus });

        public Task<IReadOnlySet<string>> GetAllPrintedNormalizedPathsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task ReconcileFolderStatusAsync(string folderPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private class TestSettingsRepo : ISettingsRepository
    {
        private readonly string _rootFolder;
        public TestSettingsRepo(string rootFolder) => _rootFolder = rootFolder;
        public Task<AppSettings> GetSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult(new AppSettings { RootFolder = _rootFolder });
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task MarkerResurrection_ExplicitUnprintedOrder_ScanDoesNotReimportMarker()
    {
        // An order where user explicitly marked it unprinted (PrintedRevision = 1, IsPrinted = false)
        var order = await SeedOrderAsync(isPrinted: false, printedRev: 1);

        var mockPrintService = new MockPrintStatusService(folderPrintedResult: true);
        var fs = new PhysicalFileSystemAdapter();
        var settingsRepo = new TestSettingsRepo(_tempRoot);

        // Create folder on disk
        string orderDir = Path.Combine(_tempRoot, order.RelativePath);
        Directory.CreateDirectory(orderDir);

        var scanService = new ScanService(
            fs,
            new FolderStructureParser(fs),
            new PrintFolderResolver(fs),
            settingsRepo,
            orderRepository: _orderRepo,
            printStatusService: mockPrintService);

        await scanService.ScanDateAsync(order.WorkDate);

        // PrintedRevision > 0: marker must NOT be re-imported!
        var reloaded = await _orderRepo.GetOrderByIdAsync(order.Id);
        Assert.NotNull(reloaded);
        reloaded.IsPrinted.Should().BeFalse();
        mockPrintService.CallCount.Should().Be(0); // Guard skipped checking marker!
    }

    [Fact]
    public async Task MarkerImport_NeverDecidedOrder_StillImports()
    {
        // An order that has never been decided (PrintedRevision = 0, IsPrinted = false)
        var order = await SeedOrderAsync(isPrinted: false, printedRev: 0);

        var mockPrintService = new MockPrintStatusService(folderPrintedResult: true);
        var fs = new PhysicalFileSystemAdapter();
        var settingsRepo = new TestSettingsRepo(_tempRoot);

        string orderDir = Path.Combine(_tempRoot, order.RelativePath);
        Directory.CreateDirectory(orderDir);

        var scanService = new ScanService(
            fs,
            new FolderStructureParser(fs),
            new PrintFolderResolver(fs),
            settingsRepo,
            orderRepository: _orderRepo,
            printStatusService: mockPrintService);

        await scanService.ScanDateAsync(order.WorkDate);

        // PrintedRevision == 0: marker IS imported!
        var reloaded = await _orderRepo.GetOrderByIdAsync(order.Id);
        Assert.NotNull(reloaded);
        reloaded.IsPrinted.Should().BeTrue();
        mockPrintService.CallCount.Should().BeGreaterThan(0);
    }
}
