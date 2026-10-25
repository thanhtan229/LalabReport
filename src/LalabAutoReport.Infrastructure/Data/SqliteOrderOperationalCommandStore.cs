using System;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.ApplicationCore.Commands;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Commands;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Data.Sqlite;

namespace LalabAutoReport.Infrastructure.Data;

public class SqliteOrderOperationalCommandStore : IOrderOperationalCommandStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteOrderOperationalCommandStore(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<OrderCommandResult> ExecuteOrderOperationalCommandAsync(
        ActorContext actor,
        string capability,
        long orderId,
        string operationId,
        long expectedRevision,
        string payloadHash,
        object? desiredValue,
        ICommandPipelineObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        observer ??= NullCommandPipelineObserver.Instance;
        await observer.OnBeforeTransactionAsync(operationId, capability);

        try
        {
            return await ExecuteInternalAsync(
                actor, capability, orderId, operationId, expectedRevision, payloadHash, desiredValue, observer, cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // Constraint violation on operation_id
        {
            // Concurrent duplicate backstop: retry reading receipt once
            return await ReadReceiptOnFreshConnectionAsync(operationId, payloadHash, capability, cancellationToken);
        }
    }

    private async Task<OrderCommandResult> ExecuteInternalAsync(
        ActorContext actor,
        string capability,
        long orderId,
        string operationId,
        long expectedRevision,
        string payloadHash,
        object? desiredValue,
        ICommandPipelineObserver observer,
        CancellationToken cancellationToken)
    {
        using var connection = _connectionFactory.CreateConnection();
        if (connection is SqliteConnection sqliteConn)
        {
            sqliteConn.DefaultTimeout = 5;
        }

        IDbTransaction? tx = null;
        try
        {
            tx = connection.BeginTransaction();
            await observer.OnAfterBeginTransactionAsync(operationId, capability);
            // 1. Receipt check
            var existingReceipt = await connection.QuerySingleOrDefaultAsync<ReceiptDto>(@"
                SELECT operation_id, capability, entity_type, entity_id, payload_hash, status, result_json
                FROM command_receipts
                WHERE operation_id = @Op;
            ", new { Op = operationId }, transaction: tx);

            if (existingReceipt != null)
            {
                if (string.Equals(existingReceipt.payload_hash, payloadHash, StringComparison.Ordinal) &&
                    string.Equals(existingReceipt.capability, capability, StringComparison.Ordinal))
                {
                    tx.Commit();
                    var storedDto = JsonSerializer.Deserialize<OrderFieldStateDto>(existingReceipt.result_json, JsonOptions)!;
                    return new OrderCommandResult
                    {
                        IsReplay = true,
                        ResultJson = existingReceipt.result_json,
                        FieldState = storedDto
                    };
                }

                tx.Rollback();
                throw new IdempotencyConflictException();
            }

            // 2. Read order current state
            var cur = await connection.QuerySingleOrDefaultAsync<OrderRowDto>(@"
                SELECT id, is_delivered, delivered_at, delivered_by, delivered_revision,
                       is_printed, printed_at, print_progress, printed_revision,
                       note, note_revision
                FROM orders
                WHERE id = @Id;
            ", new { Id = orderId }, transaction: tx);

            if (cur == null)
            {
                tx.Rollback();
                throw new OrderNotFoundException();
            }

            // 3. Convergent check & Revision check
            string fieldGroup;
            long groupRevision;
            bool isNoChange;
            JsonElement beforeValueElement;
            JsonElement targetValueElement;
            string nowIso = DateTimeOffset.UtcNow.ToString("o");

            if (capability == Capabilities.OrdersSetDelivered)
            {
                fieldGroup = "delivered";
                groupRevision = cur.delivered_revision;
                bool desired = (bool)desiredValue!;
                bool current = cur.is_delivered == 1;
                isNoChange = (desired == current);

                beforeValueElement = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    delivered = current,
                    deliveredAt = cur.delivered_at,
                    deliveredBy = cur.delivered_by
                })).RootElement.Clone();

                string? targetDeliveredAt = desired ? (isNoChange ? cur.delivered_at : nowIso) : null;
                string? targetDeliveredBy = desired ? (isNoChange ? cur.delivered_by : (actor.Role == "Admin" ? "Chủ tiệm" : "Nhân viên")) : null;

                targetValueElement = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    delivered = desired,
                    deliveredAt = targetDeliveredAt,
                    deliveredBy = targetDeliveredBy
                })).RootElement.Clone();
            }
            else if (capability == Capabilities.OrdersSetPrinted)
            {
                fieldGroup = "printed";
                groupRevision = cur.printed_revision;
                bool desired = (bool)desiredValue!;
                bool current = cur.is_printed == 1;
                isNoChange = (desired == current);

                beforeValueElement = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    printed = current,
                    printedAt = cur.printed_at,
                    printProgress = cur.print_progress
                })).RootElement.Clone();

                string? targetPrintedAt = desired ? (isNoChange ? cur.printed_at : nowIso) : null;
                int targetPrintProgress = desired ? (isNoChange ? cur.print_progress : (int)PrintStatus.Printed) : (int)PrintStatus.NotPrinted;

                targetValueElement = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    printed = desired,
                    printedAt = targetPrintedAt,
                    printProgress = targetPrintProgress
                })).RootElement.Clone();
            }
            else if (capability == Capabilities.OrdersEditNote)
            {
                fieldGroup = "note";
                groupRevision = cur.note_revision;
                string? desired = (string?)desiredValue;
                string? current = cur.note;
                isNoChange = string.Equals(desired, current, StringComparison.Ordinal);

                beforeValueElement = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    note = current
                })).RootElement.Clone();

                targetValueElement = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    note = desired
                })).RootElement.Clone();
            }
            else
            {
                tx.Rollback();
                throw new NotSupportedException($"Capability {capability} is not supported by order operational command store.");
            }

            string outcome;
            long newRevision;

            if (isNoChange)
            {
                outcome = "NoChange";
                newRevision = groupRevision;
            }
            else
            {
                if (groupRevision != expectedRevision)
                {
                    tx.Rollback();
                    throw new StaleRevisionException("order", orderId, fieldGroup, groupRevision, beforeValueElement);
                }

                outcome = "Committed";

                // 4. Conditional UPDATE on orders
                int affected;
                if (capability == Capabilities.OrdersSetDelivered)
                {
                    bool desired = (bool)desiredValue!;
                    string? deliveredAt = desired ? nowIso : null;
                    string? deliveredBy = desired ? (actor.Role == "Admin" ? "Chủ tiệm" : "Nhân viên") : null;

                    affected = await connection.ExecuteAsync(@"
                        UPDATE orders
                        SET is_delivered = @Delivered,
                            delivered_at = @DeliveredAt,
                            delivered_by = @DeliveredBy,
                            updated_at = @UpdatedAt
                        WHERE id = @Id AND delivered_revision = @ExpectedRevision;
                    ", new
                    {
                        Id = orderId,
                        Delivered = desired ? 1 : 0,
                        DeliveredAt = deliveredAt,
                        DeliveredBy = deliveredBy,
                        UpdatedAt = nowIso,
                        ExpectedRevision = expectedRevision
                    }, transaction: tx);
                }
                else if (capability == Capabilities.OrdersSetPrinted)
                {
                    bool desired = (bool)desiredValue!;
                    string? printedAt = desired ? nowIso : null;
                    int printProgress = desired ? (int)PrintStatus.Printed : (int)PrintStatus.NotPrinted;

                    affected = await connection.ExecuteAsync(@"
                        UPDATE orders
                        SET is_printed = @Printed,
                            printed_at = @PrintedAt,
                            print_progress = @PrintProgress,
                            updated_at = @UpdatedAt
                        WHERE id = @Id AND printed_revision = @ExpectedRevision;
                    ", new
                    {
                        Id = orderId,
                        Printed = desired ? 1 : 0,
                        PrintedAt = printedAt,
                        PrintProgress = printProgress,
                        UpdatedAt = nowIso,
                        ExpectedRevision = expectedRevision
                    }, transaction: tx);
                }
                else // OrdersEditNote
                {
                    string? note = (string?)desiredValue;

                    affected = await connection.ExecuteAsync(@"
                        UPDATE orders
                        SET note = @Note,
                            updated_at = @UpdatedAt
                        WHERE id = @Id AND note_revision = @ExpectedRevision;
                    ", new
                    {
                        Id = orderId,
                        Note = note,
                        UpdatedAt = nowIso,
                        ExpectedRevision = expectedRevision
                    }, transaction: tx);
                }

                if (affected != 1)
                {
                    tx.Rollback();
                    throw new StaleRevisionException("order", orderId, fieldGroup, groupRevision, beforeValueElement);
                }

                await observer.OnAfterBusinessUpdateAsync(operationId, capability);

                // 5. Read bumped revision and tag change_journal row
                if (capability == Capabilities.OrdersSetDelivered)
                {
                    newRevision = await connection.QuerySingleAsync<long>(
                        "SELECT delivered_revision FROM orders WHERE id = @Id", new { Id = orderId }, transaction: tx);
                }
                else if (capability == Capabilities.OrdersSetPrinted)
                {
                    newRevision = await connection.QuerySingleAsync<long>(
                        "SELECT printed_revision FROM orders WHERE id = @Id", new { Id = orderId }, transaction: tx);
                }
                else
                {
                    newRevision = await connection.QuerySingleAsync<long>(
                        "SELECT note_revision FROM orders WHERE id = @Id", new { Id = orderId }, transaction: tx);
                }

                await connection.ExecuteAsync(@"
                    UPDATE change_journal
                    SET operation_id = @Op
                    WHERE entity_type = 'order'
                      AND entity_id = @Id
                      AND field_group = @Group
                      AND revision = @NewRev
                      AND operation_id IS NULL;
                ", new
                {
                    Op = operationId,
                    Id = orderId,
                    Group = fieldGroup,
                    NewRev = newRevision
                }, transaction: tx);

                // 6. Cloud outbox (delivered and note only)
                if (capability == Capabilities.OrdersSetDelivered)
                {
                    await SqliteCloudSyncStateRepository.EnqueueAsync(
                        connection, tx, "order_delivered", orderId, (bool)desiredValue!,
                        actor: (actor.Role == "Admin" ? "Chủ tiệm" : "Nhân viên"),
                        operationId: operationId);
                }
                else if (capability == Capabilities.OrdersEditNote)
                {
                    await SqliteCloudSyncStateRepository.EnqueueAsync(
                        connection, tx, "order_note", orderId, (string?)desiredValue ?? "",
                        actor: (actor.Role == "Admin" ? "Chủ tiệm" : "Nhân viên"),
                        operationId: operationId);
                }
            }

            // 7. Insert audit_log
            string nowAuditIso = DateTimeOffset.UtcNow.ToString("o");
            string actorLabel = actor.Role == "Admin" ? "Chủ tiệm" : "Nhân viên";
            await connection.ExecuteAsync(@"
                INSERT INTO audit_log (
                    occurred_at_utc, action, outcome, operation_id, entity_type, entity_id,
                    before_json, after_json, reason, actor_kind, actor_role,
                    actor_session_id, actor_device_id, actor_label, client_ip
                ) VALUES (
                    @OccurredAt, @Action, @Outcome, @Op, 'order', @Id,
                    @BeforeJson, @AfterJson, NULL, @ActorKind, @ActorRole,
                    @ActorSessionId, @ActorDeviceId, @ActorLabel, @ClientIp
                );
            ", new
            {
                OccurredAt = nowAuditIso,
                Action = capability,
                Outcome = outcome,
                Op = operationId,
                Id = orderId,
                BeforeJson = JsonSerializer.Serialize(beforeValueElement),
                AfterJson = JsonSerializer.Serialize(targetValueElement),
                ActorKind = actor.Kind,
                ActorRole = actor.Role,
                ActorSessionId = actor.SessionId,
                ActorDeviceId = actor.DeviceId,
                ActorLabel = actorLabel,
                ClientIp = actor.ClientIp
            }, transaction: tx);

            // 8. Insert command_receipts
            await observer.OnBeforeInsertReceiptAsync(operationId, capability);

            var stateDto = new OrderFieldStateDto(
                orderId,
                fieldGroup,
                newRevision,
                targetValueElement,
                outcome == "Committed",
                operationId,
                null);
            string resultJson = JsonSerializer.Serialize(stateDto);

            await connection.ExecuteAsync(@"
                INSERT INTO command_receipts (
                    operation_id, capability, entity_type, entity_id, payload_hash,
                    status, result_json, actor_kind, actor_session_id, actor_role,
                    created_at_utc, completed_at_utc
                ) VALUES (
                    @Op, @Cap, 'order', @Id, @Hash,
                    'Committed', @ResultJson, @ActorKind, @ActorSessionId, @ActorRole,
                    @CreatedAt, @CompletedAt
                );
            ", new
            {
                Op = operationId,
                Cap = capability,
                Id = orderId,
                Hash = payloadHash,
                ResultJson = resultJson,
                ActorKind = actor.Kind,
                ActorSessionId = actor.SessionId,
                ActorRole = actor.Role,
                CreatedAt = nowAuditIso,
                CompletedAt = DateTimeOffset.UtcNow.ToString("o")
            }, transaction: tx);

            // 9. Commit
            await observer.OnBeforeCommitAsync(operationId, capability);
            tx.Commit();
            await observer.OnAfterCommitAsync(operationId, capability);

            return new OrderCommandResult
            {
                IsReplay = false,
                ResultJson = resultJson,
                FieldState = stateDto
            };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // SQLITE_BUSY or SQLITE_LOCKED
        {
            try { tx?.Rollback(); } catch { }
            throw new ServerBusyException();
        }
        catch
        {
            try { tx?.Rollback(); } catch { }
            throw;
        }
        finally
        {
            tx?.Dispose();
        }
    }

    private async Task<OrderCommandResult> ReadReceiptOnFreshConnectionAsync(
        string operationId,
        string payloadHash,
        string capability,
        CancellationToken cancellationToken)
    {
        using var connection = _connectionFactory.CreateConnection();
        var receipt = await connection.QuerySingleOrDefaultAsync<ReceiptDto>(@"
            SELECT operation_id, capability, entity_type, entity_id, payload_hash, status, result_json
            FROM command_receipts
            WHERE operation_id = @Op;
        ", new { Op = operationId });

        if (receipt != null &&
            string.Equals(receipt.payload_hash, payloadHash, StringComparison.Ordinal) &&
            string.Equals(receipt.capability, capability, StringComparison.Ordinal))
        {
            var storedDto = JsonSerializer.Deserialize<OrderFieldStateDto>(receipt.result_json, JsonOptions)!;
            return new OrderCommandResult
            {
                IsReplay = true,
                ResultJson = receipt.result_json,
                FieldState = storedDto
            };
        }

        throw new IdempotencyConflictException();
    }

    private class ReceiptDto
    {
        public string operation_id { get; set; } = string.Empty;
        public string capability { get; set; } = string.Empty;
        public string entity_type { get; set; } = string.Empty;
        public long entity_id { get; set; }
        public string payload_hash { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
        public string result_json { get; set; } = string.Empty;
    }

    private class OrderRowDto
    {
        public long id { get; set; }
        public long is_delivered { get; set; }
        public string? delivered_at { get; set; }
        public string? delivered_by { get; set; }
        public long delivered_revision { get; set; }
        public long is_printed { get; set; }
        public string? printed_at { get; set; }
        public int print_progress { get; set; }
        public long printed_revision { get; set; }
        public string? note { get; set; }
        public long note_revision { get; set; }
    }
}
