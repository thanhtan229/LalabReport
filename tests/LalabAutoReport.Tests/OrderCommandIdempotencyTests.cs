using System;
using System.IO;
using System.Security.Cryptography;
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

public class OrderCommandIdempotencyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseMigrator _migrator;
    private readonly InFlightOperationRegistry _inFlightRegistry;
    private readonly SqliteOrderOperationalCommandStore _store;
    private readonly SqliteCommandReceiptReader _lookupStore;
    private readonly OrderOperationalCommandHandler _handler;
    private readonly ActorContext _adminActor;
    private readonly ActorContext _staffActor;

    public OrderCommandIdempotencyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabCmdTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_cmd.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _migrator = new DatabaseMigrator(_connectionFactory);
        _inFlightRegistry = new InFlightOperationRegistry();
        _store = new SqliteOrderOperationalCommandStore(_connectionFactory);
        _lookupStore = new SqliteCommandReceiptReader(_connectionFactory);
        _handler = new OrderOperationalCommandHandler(_store, _inFlightRegistry, _lookupStore);

        _adminActor = new ActorContext("session", "Admin", new HashSet<string>
        {
            Capabilities.OrdersRead,
            Capabilities.OrdersSetDelivered,
            Capabilities.OrdersSetPrinted,
            Capabilities.OrdersEditNote
        }, sessionId: "admin-sess-1");

        _staffActor = new ActorContext("session", "Staff", new HashSet<string>
        {
            Capabilities.OrdersRead,
            Capabilities.OrdersSetDelivered,
            Capabilities.OrdersSetPrinted,
            Capabilities.OrdersEditNote
        }, sessionId: "staff-sess-1");
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

    private async Task<long> SeedOrderAsync(
        bool delivered = false, long deliveredRev = 0,
        bool printed = false, long printedRev = 0,
        string? note = null, long noteRev = 0)
    {
        await _migrator.MigrateAsync();
        using var conn = _connectionFactory.CreateConnection();
        // Enable Cloud sync in settings for outbox testing
        await conn.ExecuteAsync("INSERT OR REPLACE INTO app_settings (key, value) VALUES ('EnableCloudSync', 'true');");

        string nowIso = DateTimeOffset.UtcNow.ToString("o");
        return await conn.QuerySingleAsync<long>(@"
            INSERT INTO orders (
                order_code, work_date, original_folder_name, relative_path, status,
                is_delivered, delivered_at, delivered_by, delivered_revision,
                is_printed, printed_at, print_progress, printed_revision,
                note, note_revision, created_at, updated_at
            ) VALUES (
                'DH-261026-001', '2026-10-26', 'Folder', '2026-10-26\\Folder', 'Scanned',
                @Delivered, @DeliveredAt, @DeliveredBy, @DeliveredRev,
                @Printed, @PrintedAt, @PrintProgress, @PrintedRev,
                @Note, @NoteRev, @Now, @Now
            );
            SELECT last_insert_rowid();
        ", new
        {
            Delivered = delivered ? 1 : 0,
            DeliveredAt = delivered ? nowIso : null,
            DeliveredBy = delivered ? "Chủ tiệm" : null,
            DeliveredRev = deliveredRev,
            Printed = printed ? 1 : 0,
            PrintedAt = printed ? nowIso : null,
            PrintProgress = printed ? (int)PrintStatus.Printed : (int)PrintStatus.NotPrinted,
            PrintedRev = printedRev,
            Note = note,
            NoteRev = noteRev,
            Now = nowIso
        });
    }

    [Fact]
    public async Task SameOperationSamePayload_ReturnsStoredResult_AfterAuth()
    {
        long orderId = await SeedOrderAsync();
        string opId = Guid.NewGuid().ToString();

        var req = new SetOrderDeliveredRequest(opId, 0, true);
        var result1 = await _handler.SetDeliveredAsync(_adminActor, orderId, req);
        result1.IsReplay.Should().BeFalse();
        result1.FieldState.Revision.Should().Be(1);

        // Replay with same opId and same payload
        var result2 = await _handler.SetDeliveredAsync(_adminActor, orderId, req);
        result2.IsReplay.Should().BeTrue();
        result2.ResultJson.Should().Be(result1.ResultJson);
        result2.FieldState.Revision.Should().Be(1);

        // Capability check before replay: unauthenticated actor without capability throws CapabilityDeniedException
        var unauthorizedActor = new ActorContext("session", "Guest", new HashSet<string>());
        await Assert.ThrowsAsync<CapabilityDeniedException>(() =>
            _handler.SetDeliveredAsync(unauthorizedActor, orderId, req));
    }

    [Fact]
    public async Task SameOperationDifferentPayload_Returns409IdempotencyConflict()
    {
        long orderId = await SeedOrderAsync();
        string opId = Guid.NewGuid().ToString();

        var req1 = new SetOrderDeliveredRequest(opId, 0, true);
        await _handler.SetDeliveredAsync(_adminActor, orderId, req1);

        // Replay with different payload (Delivered=false)
        var req2 = new SetOrderDeliveredRequest(opId, 0, false);
        var ex = await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            _handler.SetDeliveredAsync(_adminActor, orderId, req2));
        ex.Code.Should().Be(ApiErrorCodes.IdempotencyConflict);
    }

    [Fact]
    public async Task SameOperationDifferentCapability_Returns409IdempotencyConflict()
    {
        long orderId = await SeedOrderAsync();
        string opId = Guid.NewGuid().ToString();

        var reqDelivered = new SetOrderDeliveredRequest(opId, 0, true);
        await _handler.SetDeliveredAsync(_adminActor, orderId, reqDelivered);

        // Replay same opId under different capability (SetPrinted)
        var reqPrinted = new SetOrderPrintedRequest(opId, 0, true);
        var ex = await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            _handler.SetPrintedAsync(_adminActor, orderId, reqPrinted));
        ex.Code.Should().Be(ApiErrorCodes.IdempotencyConflict);
    }

    [Fact]
    public async Task RetryAfterCommit_ReturnsSuccessNotStale()
    {
        long orderId = await SeedOrderAsync();
        string opA = Guid.NewGuid().ToString();
        string opB = Guid.NewGuid().ToString();

        // Client A commits delivered=true at rev 0 -> rev becomes 1
        var reqA = new SetOrderDeliveredRequest(opA, 0, true);
        var resA = await _handler.SetDeliveredAsync(_adminActor, orderId, reqA);
        resA.FieldState.Revision.Should().Be(1);

        // Client B updates delivered=false at rev 1 -> rev becomes 2
        var reqB = new SetOrderDeliveredRequest(opB, 1, false);
        var resB = await _handler.SetDeliveredAsync(_adminActor, orderId, reqB);
        resB.FieldState.Revision.Should().Be(2);

        // Client A retries with original opA and old expectedRevision 0: returns stored success, NOT stale revision!
        var resARetry = await _handler.SetDeliveredAsync(_adminActor, orderId, reqA);
        resARetry.IsReplay.Should().BeTrue();
        resARetry.ResultJson.Should().Be(resA.ResultJson);
    }

    [Fact]
    public async Task StaleRevision_Returns409WithCurrentRevisionAndValue()
    {
        long orderId = await SeedOrderAsync(note: "Hello", noteRev: 5);
        string opId = Guid.NewGuid().ToString();

        // Client expects revision 4, but current revision is 5
        var req = new SetOrderNoteRequest(opId, 4, "World");
        var ex = await Assert.ThrowsAsync<StaleRevisionException>(() =>
            _handler.SetNoteAsync(_adminActor, orderId, req));

        ex.Code.Should().Be(ApiErrorCodes.StaleRevision);
        ex.EntityId.Should().Be(orderId);
        ex.FieldGroup.Should().Be("note");
        ex.CurrentRevision.Should().Be(5);
        ex.CurrentValue.GetProperty("note").GetString().Should().Be("Hello");
    }

    [Fact]
    public async Task SetPrintedTrueTwice_NoToggle_SecondIsNoChange()
    {
        long orderId = await SeedOrderAsync(printed: false, printedRev: 0);

        string op1 = Guid.NewGuid().ToString();
        var res1 = await _handler.SetPrintedAsync(_adminActor, orderId, new SetOrderPrintedRequest(op1, 0, true));
        res1.FieldState.Changed.Should().BeTrue();
        res1.FieldState.Revision.Should().Be(1);

        string op2 = Guid.NewGuid().ToString();
        // Second command with desired printed=true (already true): outcome NoChange, does not toggle
        var res2 = await _handler.SetPrintedAsync(_adminActor, orderId, new SetOrderPrintedRequest(op2, 0, true));
        res2.FieldState.Changed.Should().BeFalse();
        res2.FieldState.Revision.Should().Be(1); // Revision unchanged
    }

    [Fact]
    public async Task SetDelivered_ConvergentNoOp_IgnoresStaleExpected()
    {
        long orderId = await SeedOrderAsync(delivered: true, deliveredRev: 3);

        // Row is already delivered=true. Client sends expectedRevision=1 (stale), but desired is true.
        string op = Guid.NewGuid().ToString();
        var res = await _handler.SetDeliveredAsync(_adminActor, orderId, new SetOrderDeliveredRequest(op, 1, true));
        res.FieldState.Changed.Should().BeFalse();
        res.FieldState.Revision.Should().Be(3);
    }

    [Fact]
    public async Task Commit_WritesReceiptAuditJournalOutbox_InOneTransaction()
    {
        long orderId = await SeedOrderAsync();
        string op = Guid.NewGuid().ToString();

        var res = await _handler.SetDeliveredAsync(_adminActor, orderId, new SetOrderDeliveredRequest(op, 0, true));
        res.FieldState.Revision.Should().Be(1);

        string normOp = Guid.Parse(op).ToString("N");
        using var conn = _connectionFactory.CreateConnection();

        // 1. Receipt row exists
        var receipt = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM command_receipts WHERE operation_id = @Op", new { Op = normOp });
        Assert.NotNull(receipt);
        ((string)receipt.capability).Should().Be(Capabilities.OrdersSetDelivered);
        ((long)receipt.entity_id).Should().Be(orderId);

        // 2. Audit log row exists
        var audit = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM audit_log WHERE operation_id = @Op", new { Op = normOp });
        Assert.NotNull(audit);
        ((string)audit.action).Should().Be(Capabilities.OrdersSetDelivered);
        ((string)audit.outcome).Should().Be("Committed");

        // 3. Change journal row exists with operation_id tagged
        var journal = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM change_journal WHERE entity_id = @Id AND operation_id = @Op",
            new { Id = orderId, Op = normOp });
        Assert.NotNull(journal);
        ((long)journal.revision).Should().Be(1);
        ((string)journal.field_group).Should().Be("delivered");

        // 4. Cloud operational outbox row exists with operation_id tagged
        var outbox = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM cloud_operational_outbox WHERE entity_id = @Id AND operation_id = @Op",
            new { Id = orderId, Op = normOp });
        Assert.NotNull(outbox);
        ((string)outbox.entity_type).Should().Be("order_delivered");

        // 5. Business row updated
        var order = await conn.QuerySingleAsync<dynamic>(
            "SELECT is_delivered, delivered_revision FROM orders WHERE id = @Id", new { Id = orderId });
        ((long)order.is_delivered).Should().Be(1);
        ((long)order.delivered_revision).Should().Be(1);
    }

    [Fact]
    public async Task Printed_DoesNotEnqueueCloudOutbox()
    {
        long orderId = await SeedOrderAsync();
        string op = Guid.NewGuid().ToString();

        await _handler.SetPrintedAsync(_adminActor, orderId, new SetOrderPrintedRequest(op, 0, true));

        string normOp = Guid.Parse(op).ToString("N");
        using var conn = _connectionFactory.CreateConnection();
        var outboxCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cloud_operational_outbox WHERE operation_id = @Op", new { Op = normOp });
        outboxCount.Should().Be(0);
    }

    [Fact]
    public async Task NoChange_WritesReceiptAndAudit_NoOutboxNoRevisionBump()
    {
        long orderId = await SeedOrderAsync(delivered: true, deliveredRev: 2);
        string op = Guid.NewGuid().ToString();

        var res = await _handler.SetDeliveredAsync(_adminActor, orderId, new SetOrderDeliveredRequest(op, 2, true));
        res.FieldState.Changed.Should().BeFalse();
        res.FieldState.Revision.Should().Be(2);

        string normOp = Guid.Parse(op).ToString("N");
        using var conn = _connectionFactory.CreateConnection();

        // Receipt written
        var receipt = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM command_receipts WHERE operation_id = @Op", new { Op = normOp });
        Assert.NotNull(receipt);

        // Audit written with outcome NoChange
        var audit = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM audit_log WHERE operation_id = @Op", new { Op = normOp });
        Assert.NotNull(audit);
        ((string)audit.outcome).Should().Be("NoChange");

        // No outbox row
        var outboxCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM cloud_operational_outbox WHERE operation_id = @Op", new { Op = normOp });
        outboxCount.Should().Be(0);

        // Revision not bumped
        var rev = await conn.ExecuteScalarAsync<long>(
            "SELECT delivered_revision FROM orders WHERE id = @Id", new { Id = orderId });
        rev.Should().Be(2);
    }

    private class FaultInjectingObserver : ICommandPipelineObserver
    {
        public Task OnBeforeInsertReceiptAsync(string operationId, string capability)
        {
            throw new InvalidOperationException("Injected fault before receipt insert.");
        }
    }

    [Fact]
    public async Task FaultBeforeReceiptInsert_RollsBackBusinessChange_NoPartialReceipt()
    {
        long orderId = await SeedOrderAsync();
        string op = Guid.NewGuid().ToString();
        var observer = new FaultInjectingObserver();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _handler.SetDeliveredAsync(_adminActor, orderId, new SetOrderDeliveredRequest(op, 0, true), observer));

        string normOp = Guid.Parse(op).ToString("N");
        using var conn = _connectionFactory.CreateConnection();

        // No receipt exists
        var receiptCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM command_receipts WHERE operation_id = @Op", new { Op = normOp });
        receiptCount.Should().Be(0);

        // Business row rolled back
        var order = await conn.QuerySingleAsync<dynamic>(
            "SELECT is_delivered, delivered_revision FROM orders WHERE id = @Id", new { Id = orderId });
        ((long)order.is_delivered).Should().Be(0);
        ((long)order.delivered_revision).Should().Be(0);
    }

    [Fact]
    public void PayloadHash_GoldenVectors()
    {
        var (s1, h1) = PayloadHasher.ComputeOrderOperationalHash(
            Capabilities.OrdersSetDelivered, 123, 4, "delivered", boolValue: true);
        s1.Should().Be("{\"v\":1,\"cap\":\"orders.setDelivered\",\"entity\":\"order\",\"id\":123,\"expectedRevision\":4,\"value\":{\"delivered\":true}}");
        h1.Should().Be("66a39272f95d4f111bd977642715bfd99f486b1eca7c91de03333d3e6c323e04");

        var (s2, h2) = PayloadHasher.ComputeOrderOperationalHash(
            Capabilities.OrdersSetPrinted, 123, 4, "printed", boolValue: true);
        s2.Should().Be("{\"v\":1,\"cap\":\"orders.setPrinted\",\"entity\":\"order\",\"id\":123,\"expectedRevision\":4,\"value\":{\"printed\":true}}");
        h2.Should().Be("e8fd577c926426d5b3d923a1715294c1c508ff5ddb3ff01a451f327a16580ef3");

        var (s3, h3) = PayloadHasher.ComputeOrderOperationalHash(
            Capabilities.OrdersEditNote, 123, 4, "note", stringValue: "test note");
        s3.Should().Be("{\"v\":1,\"cap\":\"orders.editNote\",\"entity\":\"order\",\"id\":123,\"expectedRevision\":4,\"value\":{\"note\":\"test note\"}}");
        h3.Should().Be("c1ceba4ea0d47887ad4b303f6ad3c3142de298021d56ae10ef56fbc7a59064cf");

        var (s4, h4) = PayloadHasher.ComputeOrderOperationalHash(
            Capabilities.OrdersEditNote, 123, 4, "note", stringValue: null);
        s4.Should().Be("{\"v\":1,\"cap\":\"orders.editNote\",\"entity\":\"order\",\"id\":123,\"expectedRevision\":4,\"value\":{\"note\":null}}");
        h4.Should().Be("ae37381f50b26f002320c10cfe1303e924a869f9642f124199a7bc77648f4817");

        var (s5, h5) = PayloadHasher.ComputeOrderOperationalHash(
            "orders.setNote", 123, 4, "note", stringValue: "test note");
        s5.Should().Be("{\"v\":1,\"cap\":\"orders.setNote\",\"entity\":\"order\",\"id\":123,\"expectedRevision\":4,\"value\":{\"note\":\"test note\"}}");
        h5.Should().Be("d79636fa56b2bd23f89bbe50018e5f54f56f2e73cd70312ec96677ba4657de79");
    }

    [Fact]
    public async Task OperationId_NormalisedToGuidN_InvalidReturns422()
    {
        long orderId = await SeedOrderAsync();

        // Valid GUID format with hyphens normalizes to 32 hex
        string opWithHyphens = Guid.NewGuid().ToString("D");
        var res = await _handler.SetDeliveredAsync(_adminActor, orderId, new SetOrderDeliveredRequest(opWithHyphens, 0, true));
        res.FieldState.OperationId.Should().Be(Guid.Parse(opWithHyphens).ToString("N"));

        // Invalid GUID format throws CommandValidationException (code VALIDATION_FAILED -> HTTP 422)
        var ex = await Assert.ThrowsAsync<CommandValidationException>(() =>
            _handler.SetDeliveredAsync(_adminActor, orderId, new SetOrderDeliveredRequest("invalid-guid-not-hex", 0, true)));
        ex.Code.Should().Be(ApiErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Note_TrimmedEmptyBecomesNull_Over4000Returns422()
    {
        long orderId = await SeedOrderAsync();

        // Whitespace only becomes null
        string op1 = Guid.NewGuid().ToString();
        var res1 = await _handler.SetNoteAsync(_adminActor, orderId, new SetOrderNoteRequest(op1, 0, "   "));
        res1.FieldState.Value.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);

        // Normal note trimmed
        string op2 = Guid.NewGuid().ToString();
        var res2 = await _handler.SetNoteAsync(_adminActor, orderId, new SetOrderNoteRequest(op2, 0, "  hello world  "));
        res2.FieldState.Value.GetProperty("note").GetString().Should().Be("hello world");

        // Over 4000 chars returns 422
        string op3 = Guid.NewGuid().ToString();
        string longNote = new string('x', 4001);
        var ex = await Assert.ThrowsAsync<CommandValidationException>(() =>
            _handler.SetNoteAsync(_adminActor, orderId, new SetOrderNoteRequest(op3, 1, longNote)));
        ex.Code.Should().Be(ApiErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Pipeline_UsesImmediateTransaction()
    {
        long orderId = await SeedOrderAsync();
        // Microsoft.Data.Sqlite BeginTransaction() issues BEGIN IMMEDIATE by default.
        // Prove that opening a write lock immediately holds SQLite exclusive write lock against concurrent writes.
        var barrier = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var observer = new HookObserver(
            onAfterBegin: () => { barrier.SetResult(); return release.Task; });

        var taskA = Task.Run(() => _handler.SetDeliveredAsync(_adminActor, orderId,
            new SetOrderDeliveredRequest(Guid.NewGuid().ToString(), 0, true), observer));

        await barrier.Task;

        // While task A holds transaction, another transaction attempting write on separate connection fails or waits
        using var conn2 = _connectionFactory.CreateConnection();
        using var cmd = conn2.CreateCommand();
        cmd.CommandText = "UPDATE orders SET is_delivered = 1 WHERE id = " + orderId;
        cmd.CommandTimeout = 1; // 1s timeout to fail fast

        var busyEx = await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        (busyEx.SqliteErrorCode is 5 or 6).Should().BeTrue();

        release.SetResult();
        await taskA;
    }

    [Fact]
    public async Task Busy_Returns503ServerBusy_Retryable_NoCommit()
    {
        long orderId = await SeedOrderAsync();

        // Lock the database by holding an open transaction on connection 2
        using var conn2 = _connectionFactory.CreateConnection();
        using var tx2 = conn2.BeginTransaction();
        using var cmd = conn2.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx2;
        cmd.CommandText = "UPDATE orders SET is_delivered = 1 WHERE id = " + orderId;
        await cmd.ExecuteNonQueryAsync();

        // Command attempting to execute against locked DB should catch SQLITE_BUSY and throw ServerBusyException
        string op = Guid.NewGuid().ToString();
        var ex = await Assert.ThrowsAsync<ServerBusyException>(() =>
            _handler.SetDeliveredAsync(_adminActor, orderId, new SetOrderDeliveredRequest(op, 0, true)));
        ex.Code.Should().Be(ApiErrorCodes.ServerBusy);
        ex.Retryable.Should().BeTrue();

        tx2.Rollback();
    }

    [Fact]
    public async Task OperationLookup_CommittedPendingNotFound()
    {
        long orderId = await SeedOrderAsync();
        string committedOp = Guid.NewGuid().ToString();
        await _handler.SetDeliveredAsync(_adminActor, orderId, new SetOrderDeliveredRequest(committedOp, 0, true));

        // 1. Committed
        var lookupCommitted = await _handler.LookupOperationAsync(_adminActor, committedOp);
        lookupCommitted.Status.Should().Be(OperationLookupStatus.Committed);
        lookupCommitted.Capability.Should().Be(Capabilities.OrdersSetDelivered);
        lookupCommitted.EntityId.Should().Be(orderId);

        // 2. Pending
        string pendingOp = Guid.NewGuid().ToString();
        string normPendingOp = Guid.Parse(pendingOp).ToString("N");
        _inFlightRegistry.TryBegin(normPendingOp);
        try
        {
            var lookupPending = await _handler.LookupOperationAsync(_adminActor, pendingOp);
            lookupPending.Status.Should().Be(OperationLookupStatus.Pending);
        }
        finally
        {
            _inFlightRegistry.End(normPendingOp);
        }

        // 3. Not found
        string unknownOp = Guid.NewGuid().ToString();
        var lookupNotFound = await _handler.LookupOperationAsync(_adminActor, unknownOp);
        lookupNotFound.Status.Should().Be(OperationLookupStatus.NotFound);

        // 4. Committed but caller lacks capability -> hidden as NotFound (§3.9)
        var restrictedActor = new ActorContext("session", "Staff", new HashSet<string> { Capabilities.OrdersRead });
        var lookupHidden = await _handler.LookupOperationAsync(restrictedActor, committedOp);
        lookupHidden.Status.Should().Be(OperationLookupStatus.NotFound);
    }

    private class HookObserver : ICommandPipelineObserver
    {
        private readonly Func<Task>? _onAfterBegin;
        public HookObserver(Func<Task>? onAfterBegin = null)
        {
            _onAfterBegin = onAfterBegin;
        }

        public async Task OnAfterBeginTransactionAsync(string operationId, string capability)
        {
            if (_onAfterBegin != null) await _onAfterBegin();
        }
    }
}
