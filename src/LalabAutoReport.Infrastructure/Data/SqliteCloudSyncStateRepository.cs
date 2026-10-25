using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using LalabAutoReport.Core.Interfaces;

namespace LalabAutoReport.Infrastructure.Data;

public sealed class SqliteCloudSyncStateRepository(ISqliteConnectionFactory factory) : ICloudSyncStateRepository
{
    public async Task<CloudSyncCheckpoint> GetCheckpointAsync(CancellationToken cancellationToken = default)
    {
        using var connection = factory.CreateConnection();
        var state = await connection.QuerySingleAsync<StateRow>("SELECT * FROM cloud_sync_state WHERE id = 1");
        return new(state.cursor, state.requires_reconciliation != 0, state.stream_id, state.endpoint);
    }

    public async Task BindStreamAsync(string streamId, string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamId)) throw new InvalidOperationException("Cloud thiếu stream ID V2.");
        using var connection = factory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var state = await connection.QuerySingleAsync<StateRow>("SELECT * FROM cloud_sync_state WHERE id = 1", transaction: transaction);
        if (state.requires_reconciliation != 0) throw new InvalidOperationException("Cần đối soát cursor Cloud cũ. Xuất báo cáo đối soát trước khi tiếp tục.");
        if ((state.stream_id != null && state.stream_id != streamId) || (state.endpoint != null && state.endpoint != endpoint))
            throw new InvalidOperationException("Cloud endpoint/stream đã đổi. Cần đối soát; không tự reset checkpoint.");
        await connection.ExecuteAsync("UPDATE cloud_sync_state SET stream_id = @streamId, endpoint = @endpoint WHERE id = 1", new { streamId, endpoint }, transaction);
        transaction.Commit();
    }

    public async Task<bool> ApplyEventAsync(CloudOperationalEvent ev, bool advanceCursor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ev.Id <= 0 || ev.EntityId <= 0) throw new InvalidOperationException("Invalid Cloud event identity.");
        // Parse and validate before any write; unknown events must not be skipped.
        using var payload = JsonDocument.Parse(ev.PayloadJson);
        var root = payload.RootElement;
        string table, assignments;
        object values;
        switch (ev.EntityType)
        {
            case "bill_payment":
                table = "customer_bills"; assignments = "is_paid = @Flag, paid_at = @At";
                values = new { Id = ev.EntityId, Flag = root.GetProperty("isPaid").GetBoolean() ? 1 : 0, At = ReadTimestamp(root, "paidAt") };
                break;
            case "order_delivered":
                table = "orders"; assignments = "is_delivered = @Flag, delivered_at = @At, delivered_by = @By";
                values = new { Id = ev.EntityId, Flag = root.GetProperty("isDelivered").GetBoolean() ? 1 : 0, At = ReadTimestamp(root, "deliveredAt"), By = root.GetProperty("deliveredBy").GetString() };
                break;
            case "order_note":
                table = "orders"; assignments = "note = @Note";
                values = new { Id = ev.EntityId, Note = root.GetProperty("note").GetString() };
                break;
            default: throw new InvalidOperationException($"Unknown Cloud event type: {ev.EntityType}; checkpoint retained.");
        }
        using var connection = factory.CreateConnection();
        using var transaction = connection.BeginTransaction();
        if (await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE id = @Id", new { Id = ev.EntityId }, transaction) != 1)
            throw new InvalidOperationException($"Cloud event {ev.Id}: local entity {ev.EntityType}/{ev.EntityId} missing; checkpoint retained.");
        string eventJson = JsonSerializer.Serialize(ev);
        var existing = await connection.QuerySingleOrDefaultAsync<string>("SELECT event_json FROM cloud_applied_events WHERE id = @Id", new { ev.Id }, transaction);
        if (existing != null && existing != eventJson) throw new InvalidOperationException($"Cloud event ID {ev.Id} changed content.");
        await connection.ExecuteAsync("INSERT OR IGNORE INTO cloud_applied_events(id, event_json) VALUES (@Id, @eventJson)", new { ev.Id, eventJson }, transaction);
        await connection.ExecuteAsync(@"INSERT INTO cloud_field_revisions(entity_type, entity_id, revision, payload_json) VALUES (@EntityType, @EntityId, @Id, @PayloadJson)
            ON CONFLICT(entity_type, entity_id) DO UPDATE SET revision = excluded.revision, payload_json = excluded.payload_json WHERE excluded.revision > revision", ev, transaction);
        if (!advanceCursor && ev.OperationId != null)
        {
            var operation = await connection.QuerySingleOrDefaultAsync<PendingRow>("SELECT * FROM cloud_operational_outbox WHERE operation_id = @OperationId", ev, transaction);
            if (operation != null && (operation.entity_type != ev.EntityType || operation.entity_id != ev.EntityId))
                throw new InvalidOperationException("Cloud acknowledgement does not match the pending operation.");
            if (operation != null)
            {
                using var expected = JsonDocument.Parse(operation.value_json);
                var actual = root.GetProperty(ev.EntityType == "bill_payment" ? "isPaid" : ev.EntityType == "order_delivered" ? "isDelivered" : "note");
                if (expected.RootElement.ToString() != actual.ToString()) throw new InvalidOperationException("Cloud acknowledgement changed the operation value.");
            }
            await connection.ExecuteAsync("DELETE FROM cloud_operational_outbox WHERE operation_id = @OperationId", ev, transaction);
        }
        if (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM cloud_operational_outbox WHERE entity_type = @EntityType AND entity_id = @EntityId", ev, transaction) == 0)
        {
            // An older ACK may arrive after a newer mobile event. Materialize the
            // highest confirmed payload, never the ACK's stale value.
            string confirmed = await connection.QuerySingleAsync<string>("SELECT payload_json FROM cloud_field_revisions WHERE entity_type = @EntityType AND entity_id = @EntityId", ev, transaction);
            using var latest = JsonDocument.Parse(confirmed); var r = latest.RootElement;
            values = ev.EntityType switch
            {
                "bill_payment" => new { Id = ev.EntityId, Flag = r.GetProperty("isPaid").GetBoolean() ? 1 : 0, At = ReadTimestamp(r, "paidAt") },
                "order_delivered" => new { Id = ev.EntityId, Flag = r.GetProperty("isDelivered").GetBoolean() ? 1 : 0, At = ReadTimestamp(r, "deliveredAt"), By = r.GetProperty("deliveredBy").GetString() },
                _ => new { Id = ev.EntityId, Note = r.GetProperty("note").GetString() }
            };
            await connection.ExecuteAsync($"UPDATE {table} SET {assignments} WHERE id = @Id", values, transaction);
        }
        if (advanceCursor) await connection.ExecuteAsync("UPDATE cloud_sync_state SET cursor = MAX(cursor, @Id) WHERE id = 1", new { ev.Id }, transaction);
        transaction.Commit();
        return existing == null;
    }

    private static string? ReadTimestamp(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null) return null;
        string? text = value.GetString();
        if (!DateTimeOffset.TryParse(text, out _)) throw new InvalidOperationException($"Invalid Cloud timestamp {name}.");
        return text;
    }

    // Called inside the same transaction as the local edit. No echo from remote
    // application: ApplyEventAsync writes operational columns directly.
    internal static async Task EnqueueAsync(IDbConnection connection, IDbTransaction transaction, string type, long id, object value, string? actor = null, string? operationId = null)
    {
        var enabled = await connection.QuerySingleOrDefaultAsync<string>("SELECT value FROM app_settings WHERE key = 'EnableCloudSync'", transaction: transaction);
        if (!bool.TryParse(enabled, out bool on) || !on) return;
        await connection.ExecuteAsync(@"INSERT INTO cloud_operational_outbox(operation_id, entity_type, entity_id, value_json, delivered_by, created_at)
            VALUES (@OperationId, @type, @id, @ValueJson, @actor, @CreatedAt)",
            new { OperationId = operationId ?? Guid.NewGuid().ToString("N"), type, id, ValueJson = JsonSerializer.Serialize(value), actor, CreatedAt = DateTimeOffset.UtcNow.ToString("o") }, transaction);
    }

    public async Task<IReadOnlyList<CloudPendingOperation>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        using var connection = factory.CreateConnection();
        return (await connection.QueryAsync<PendingRow>("SELECT * FROM cloud_operational_outbox ORDER BY sequence"))
            .Select(r => new CloudPendingOperation(r.sequence, r.operation_id, r.entity_type, r.entity_id, r.value_json, r.delivered_by)).ToList();
    }

    public async Task<IReadOnlyList<object>> GetReconciliationInventoryAsync(CancellationToken cancellationToken = default)
    {
        using var connection = factory.CreateConnection();
        var orders = await connection.QueryAsync("SELECT id, root_folder_id, relative_path, is_delivered, delivered_at, delivered_by, note FROM orders ORDER BY id");
        var bills = await connection.QueryAsync("SELECT id, bill_number, status, grand_total, is_paid, paid_at FROM customer_bills ORDER BY id");
        return orders.Select(o => (object)new { entityType = "order", state = o }).Concat(bills.Select(b => (object)new { entityType = "bill", state = b })).ToList();
    }

    public async Task SetLastSyncAtAsync(string value, CancellationToken cancellationToken = default)
    {
        using var connection = factory.CreateConnection();
        await connection.ExecuteAsync("INSERT INTO app_settings(key,value) VALUES ('LastCloudSyncAt',@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", new { value });
    }

    private static readonly JsonSerializerOptions ReportOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static async Task<string> LocalHashAsync(IDbConnection connection, IDbTransaction transaction)
    {
        var orders = (await connection.QueryAsync("SELECT id,root_folder_id,relative_path,is_delivered,delivered_at,delivered_by,note FROM orders ORDER BY id", transaction: transaction)).ToList();
        var bills = (await connection.QueryAsync("SELECT id,bill_number,status,grand_total,is_paid,paid_at FROM customer_bills ORDER BY id", transaction: transaction)).ToList();
        var pending = (await connection.QueryAsync("SELECT * FROM cloud_operational_outbox ORDER BY sequence", transaction: transaction)).ToList();
        var syncState = await connection.QuerySingleAsync("SELECT * FROM cloud_sync_state WHERE id=1", transaction: transaction);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { orders, bills, pending, syncState }))));
    }

    public async Task<string> CreateReconciliationReportAsync(string endpoint, string snapshotJson, CancellationToken cancellationToken = default)
    {
        using var snapshot = JsonDocument.Parse(snapshotJson); var root = snapshot.RootElement;
        if (root.GetProperty("protocolVersion").GetInt32() != 2 || !root.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Invalid V2 reconciliation snapshot.");
        string stream = root.GetProperty("streamId").GetString() ?? throw new InvalidOperationException("Missing stream ID.");
        long cursor = root.GetProperty("cursor").GetInt64();
        if (cursor < 0) throw new InvalidOperationException("Invalid snapshot cursor.");
        using var connection = factory.CreateConnection(); using var transaction = connection.BeginTransaction();
        var fields = new List<CloudReconciliationField>();
        foreach (var field in root.GetProperty("fields").EnumerateArray())
        {
            string type = field.GetProperty("entityType").GetString()!; long id = field.GetProperty("entityId").GetInt64(); long revision = field.GetProperty("revision").GetInt64();
            string payload = field.GetProperty("payloadJson").GetString()!;
            if (id <= 0 || revision < 0 || revision > cursor || fields.Any(f => f.EntityType == type && f.EntityId == id)) throw new InvalidOperationException("Invalid reconciliation field identity.");
            string query = type switch {
                "order_note" => "SELECT COALESCE(note,'') FROM orders WHERE id=@id",
                "order_delivered" => "SELECT is_delivered FROM orders WHERE id=@id",
                "bill_payment" => "SELECT is_paid FROM customer_bills WHERE id=@id",
                _ => throw new InvalidOperationException("Unknown reconciliation field.") };
            // Dapper dynamic single scalar is awkward; use a scalar with a separate
            // existence check to preserve missing versus legitimate false/null.
            string table = type == "bill_payment" ? "customer_bills" : "orders";
            bool exists = await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE id=@id", new { id }, transaction) == 1;
            string? localJson = null;
            if (exists) localJson = type == "order_note"
                ? JsonSerializer.Serialize(await connection.ExecuteScalarAsync<string>(query, new { id }, transaction))
                : JsonSerializer.Serialize((await connection.ExecuteScalarAsync<long>(query, new { id }, transaction)) != 0);
            using var validation = JsonDocument.Parse(payload);
            string key = type == "order_note" ? "note" : type == "bill_payment" ? "isPaid" : "isDelivered";
            _ = validation.RootElement.GetProperty(key);
            fields.Add(new(type, id, revision, payload, localJson, "unreviewed"));
        }
        var report = new CloudReconciliationReport(Guid.NewGuid().ToString("N"), endpoint, stream, cursor, await LocalHashAsync(connection, transaction), fields);
        string json = JsonSerializer.Serialize(report, ReportOptions);
        await connection.ExecuteAsync("INSERT INTO cloud_reconciliation_reports(report_id,report_json,created_at) VALUES (@ReportId,@json,@now)", new { report.ReportId, json, now = DateTimeOffset.UtcNow.ToString("o") }, transaction);
        transaction.Commit(); return json;
    }

    public async Task CompleteReconciliationAsync(string reviewedReportJson, CancellationToken cancellationToken = default)
    {
        var report = JsonSerializer.Deserialize<CloudReconciliationReport>(reviewedReportJson, ReportOptions) ?? throw new InvalidOperationException("Invalid report.");
        using var connection = factory.CreateConnection(); using var transaction = connection.BeginTransaction();
        string originalJson = await connection.QuerySingleOrDefaultAsync<string>("SELECT report_json FROM cloud_reconciliation_reports WHERE report_id=@ReportId AND approved_at IS NULL", report, transaction)
            ?? throw new InvalidOperationException("Report missing or already approved.");
        var original = JsonSerializer.Deserialize<CloudReconciliationReport>(originalJson, ReportOptions)!;
        var unchanged = report with { Fields = report.Fields.Select(f => f with { Resolution = "unreviewed" }).ToList() };
        if (JsonSerializer.Serialize(unchanged, ReportOptions) != JsonSerializer.Serialize(original, ReportOptions)) throw new InvalidOperationException("Chỉ được sửa resolution; dữ liệu bằng chứng đã thay đổi.");
        if (await LocalHashAsync(connection, transaction) != report.LocalHash) throw new InvalidOperationException("Dữ liệu Desktop đã đổi. Xuất lại báo cáo đối soát.");
        await connection.ExecuteAsync("DELETE FROM cloud_field_revisions", transaction: transaction);
        foreach (var field in report.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (field.LocalValueJson == null)
            {
                if (field.Resolution != "ignore_missing") throw new InvalidOperationException($"Entity {field.EntityType}/{field.EntityId} không có local; cần chọn ignore_missing tường minh.");
                continue;
            }
            if (field.Resolution != "server" && field.Resolution != "desktop") throw new InvalidOperationException($"Chưa chọn server/desktop cho {field.EntityType}/{field.EntityId}.");
            using var payload = JsonDocument.Parse(field.PayloadJson); var root = payload.RootElement;
            string table = field.EntityType == "bill_payment" ? "customer_bills" : "orders";
            string assignments = field.EntityType switch { "bill_payment" => "is_paid=@Flag,paid_at=@At", "order_delivered" => "is_delivered=@Flag,delivered_at=@At,delivered_by=@By", _ => "note=@Note" };
            object values = field.EntityType switch {
                "bill_payment" => new { Id = field.EntityId, Flag = root.GetProperty("isPaid").GetBoolean() ? 1 : 0, At = ReadTimestamp(root,"paidAt") },
                "order_delivered" => new { Id = field.EntityId, Flag = root.GetProperty("isDelivered").GetBoolean() ? 1 : 0, At = ReadTimestamp(root,"deliveredAt"), By = root.GetProperty("deliveredBy").GetString() },
                _ => new { Id = field.EntityId, Note = root.GetProperty("note").GetString() }
            };
            await connection.ExecuteAsync("DELETE FROM cloud_operational_outbox WHERE entity_type=@EntityType AND entity_id=@EntityId",field,transaction);
            await connection.ExecuteAsync(@"INSERT INTO cloud_field_revisions(entity_type,entity_id,revision,payload_json) VALUES(@EntityType,@EntityId,@Revision,@PayloadJson)
                ON CONFLICT(entity_type,entity_id) DO UPDATE SET revision=excluded.revision,payload_json=excluded.payload_json",field,transaction);
            if (field.Resolution == "server") await connection.ExecuteAsync($"UPDATE {table} SET {assignments} WHERE id=@Id",values,transaction);
            else await connection.ExecuteAsync(@"INSERT INTO cloud_operational_outbox(operation_id,entity_type,entity_id,value_json,created_at)
                VALUES (@OperationId,@EntityType,@EntityId,@LocalValueJson,@now)",new { OperationId=Guid.NewGuid().ToString("N"),field.EntityType,field.EntityId,field.LocalValueJson,now=DateTimeOffset.UtcNow.ToString("o") },transaction);
        }
        await connection.ExecuteAsync(@"INSERT OR IGNORE INTO cloud_applied_event_archive(stream_id,id,event_json,archived_at)
            SELECT COALESCE((SELECT stream_id FROM cloud_sync_state WHERE id=1),'legacy'),id,event_json,@now FROM cloud_applied_events",
            new { now=DateTimeOffset.UtcNow.ToString("o") },transaction);
        await connection.ExecuteAsync("DELETE FROM cloud_applied_events", transaction: transaction);
        await connection.ExecuteAsync("UPDATE cloud_sync_state SET cursor=@Cursor,requires_reconciliation=0,stream_id=@StreamId,endpoint=@Endpoint WHERE id=1",report,transaction);
        await connection.ExecuteAsync("UPDATE cloud_reconciliation_reports SET approved_at=@now WHERE report_id=@ReportId",new {report.ReportId,now=DateTimeOffset.UtcNow.ToString("o")},transaction);
        transaction.Commit();
    }

    private sealed class StateRow { public long cursor { get; set; } public int requires_reconciliation { get; set; } public string? stream_id { get; set; } public string? endpoint { get; set; } }
    private sealed class PendingRow { public long sequence { get; set; } public string operation_id { get; set; } = ""; public string entity_type { get; set; } = ""; public long entity_id { get; set; } public string value_json { get; set; } = ""; public string? delivered_by { get; set; } }
}
