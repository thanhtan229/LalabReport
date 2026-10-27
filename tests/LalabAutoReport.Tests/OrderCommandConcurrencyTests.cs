using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

public class OrderCommandConcurrencyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DatabaseMigrator _migrator;
    private readonly InFlightOperationRegistry _inFlightRegistry;
    private readonly SqliteOrderOperationalCommandStore _store;
    private readonly SqliteCommandReceiptReader _lookupStore;
    private readonly OrderOperationalCommandHandler _handler;
    private readonly ActorContext _actorA;
    private readonly ActorContext _actorB;

    public OrderCommandConcurrencyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabCmdConcTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_conc.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _migrator = new DatabaseMigrator(_connectionFactory);
        _inFlightRegistry = new InFlightOperationRegistry();
        _store = new SqliteOrderOperationalCommandStore(_connectionFactory);
        _lookupStore = new SqliteCommandReceiptReader(_connectionFactory);
        _handler = new OrderOperationalCommandHandler(_store, _inFlightRegistry, _lookupStore);

        _actorA = new ActorContext("session", "Admin", new HashSet<string>
        {
            Capabilities.OrdersRead,
            Capabilities.OrdersSetDelivered,
            Capabilities.OrdersSetPrinted,
            Capabilities.OrdersEditNote
        }, sessionId: "session-a");

        _actorB = new ActorContext("session", "Staff", new HashSet<string>
        {
            Capabilities.OrdersRead,
            Capabilities.OrdersSetDelivered,
            Capabilities.OrdersSetPrinted,
            Capabilities.OrdersEditNote
        }, sessionId: "session-b");
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

    [Fact]
    public async Task TwoClients_SetDifferentDeliveredStates_OneWinsOther409()
    {
        long orderId = await SeedOrderAsync();

        var barrierAInTx = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();

        var observerA = new BarrierObserver(onAfterBusinessUpdate: async () =>
        {
            barrierAInTx.SetResult();
            await releaseA.Task;
        });

        // Client A sets delivered = true at expectedRevision = 0
        var taskA = Task.Run(() => _handler.SetDeliveredAsync(
            _actorA, orderId, new SetOrderDeliveredRequest(Guid.NewGuid().ToString(), 0, true), observerA));

        await barrierAInTx.Task;

        // Client B tries to set delivered = false at expectedRevision = 0
        // Because of BEGIN IMMEDIATE, B will wait for A's transaction, then see rev 1 -> 409 STALE_REVISION
        var taskB = Task.Run(() => _handler.SetDeliveredAsync(
            _actorB, orderId, new SetOrderDeliveredRequest(Guid.NewGuid().ToString(), 0, false)));

        releaseA.SetResult();

        var resA = await taskA;
        resA.FieldState.Revision.Should().Be(1);

        var exB = await Assert.ThrowsAsync<StaleRevisionException>(() => taskB);
        exB.Code.Should().Be(ApiErrorCodes.StaleRevision);
        exB.CurrentRevision.Should().Be(1);
    }

    [Fact]
    public async Task TwoClients_SameDesiredState_BothSucceed_OneNoChange()
    {
        long orderId = await SeedOrderAsync();

        var barrierAInTx = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();

        var observerA = new BarrierObserver(onAfterBusinessUpdate: async () =>
        {
            barrierAInTx.SetResult();
            await releaseA.Task;
        });

        // Client A sets delivered = true at rev 0
        var taskA = Task.Run(() => _handler.SetDeliveredAsync(
            _actorA, orderId, new SetOrderDeliveredRequest(Guid.NewGuid().ToString(), 0, true), observerA));

        await barrierAInTx.Task;

        // Client B ALSO wants delivered = true with old expected revision 0
        var taskB = Task.Run(() => _handler.SetDeliveredAsync(
            _actorB, orderId, new SetOrderDeliveredRequest(Guid.NewGuid().ToString(), 0, true)));

        releaseA.SetResult();

        var resA = await taskA;
        var resB = await taskB;

        resA.FieldState.Changed.Should().BeTrue();
        resA.FieldState.Revision.Should().Be(1);

        // Client B converges on same desired state, succeeds without error, Changed=false
        resB.FieldState.Changed.Should().BeFalse();
        resB.FieldState.Revision.Should().Be(1);
    }

    [Fact]
    public async Task TwoClients_DifferentFieldGroups_NoCrossConflict()
    {
        long orderId = await SeedOrderAsync();

        // Client A sets note at note_revision 0
        var resA = await _handler.SetNoteAsync(
            _actorA, orderId, new SetOrderNoteRequest(Guid.NewGuid().ToString(), 0, "Client A note"));

        // Client B sets delivered at delivered_revision 0
        var resB = await _handler.SetDeliveredAsync(
            _actorB, orderId, new SetOrderDeliveredRequest(Guid.NewGuid().ToString(), 0, true));

        resA.FieldState.Revision.Should().Be(1);
        resB.FieldState.Revision.Should().Be(1);

        using var conn = _connectionFactory.CreateConnection();
        var order = await conn.QuerySingleAsync<dynamic>(
            "SELECT note, note_revision, is_delivered, delivered_revision FROM orders WHERE id = @Id", new { Id = orderId });

        ((string)order.note).Should().Be("Client A note");
        ((long)order.note_revision).Should().Be(1);
        ((long)order.is_delivered).Should().Be(1);
        ((long)order.delivered_revision).Should().Be(1);
    }

    [Fact]
    public async Task NoteConflict_SecondGets409WithFirstNote_NoOverwrite()
    {
        long orderId = await SeedOrderAsync();

        // Client A sets note at rev 0 -> commits "Note from A"
        await _handler.SetNoteAsync(
            _actorA, orderId, new SetOrderNoteRequest(Guid.NewGuid().ToString(), 0, "Note from A"));

        // Client B tries to set note with old expected rev 0
        var ex = await Assert.ThrowsAsync<StaleRevisionException>(() =>
            _handler.SetNoteAsync(_actorB, orderId, new SetOrderNoteRequest(Guid.NewGuid().ToString(), 0, "Note from B")));

        ex.Code.Should().Be(ApiErrorCodes.StaleRevision);
        ex.CurrentRevision.Should().Be(1);
        ex.CurrentValue.GetProperty("note").GetString().Should().Be("Note from A");

        // Verify Note from A was NOT overwritten
        using var conn = _connectionFactory.CreateConnection();
        string currentNote = (await conn.ExecuteScalarAsync<string>(
            "SELECT note FROM orders WHERE id = @Id", new { Id = orderId }))!;
        currentNote.Should().Be("Note from A");
    }

    [Fact]
    public async Task DuplicateInFlightOperation_Returns409OperationInProgress_ThenReplay()
    {
        long orderId = await SeedOrderAsync();
        string sharedOpId = Guid.NewGuid().ToString();

        var barrierInTx = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var observer = new BarrierObserver(onAfterBusinessUpdate: async () =>
        {
            barrierInTx.SetResult();
            await release.Task;
        });

        // Request 1 starts with sharedOpId
        var task1 = Task.Run(() => _handler.SetDeliveredAsync(
            _actorA, orderId, new SetOrderDeliveredRequest(sharedOpId, 0, true), observer));

        await barrierInTx.Task;

        // Concurrent Request 2 with same sharedOpId while Request 1 is still in flight
        var ex = await Assert.ThrowsAsync<OperationInProgressException>(() =>
            _handler.SetDeliveredAsync(_actorA, orderId, new SetOrderDeliveredRequest(sharedOpId, 0, true)));
        ex.Code.Should().Be(ApiErrorCodes.OperationInProgress);

        release.SetResult();
        var res1 = await task1;
        res1.IsReplay.Should().BeFalse();

        // After Request 1 completes, Request 3 with sharedOpId replays stored result
        var res3 = await _handler.SetDeliveredAsync(_actorA, orderId, new SetOrderDeliveredRequest(sharedOpId, 0, true));
        res3.IsReplay.Should().BeTrue();
        res3.ResultJson.Should().Be(res1.ResultJson);
    }

    private class BarrierObserver : ICommandPipelineObserver
    {
        private readonly Func<Task>? _onAfterBusinessUpdate;
        public BarrierObserver(Func<Task>? onAfterBusinessUpdate = null)
        {
            _onAfterBusinessUpdate = onAfterBusinessUpdate;
        }

        public async Task OnAfterBusinessUpdateAsync(string operationId, string capability)
        {
            if (_onAfterBusinessUpdate != null) await _onAfterBusinessUpdate();
        }
    }
}
