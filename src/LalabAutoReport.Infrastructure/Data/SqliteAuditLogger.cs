using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Core.Interfaces;

namespace LalabAutoReport.Infrastructure.Data;

public sealed class SqliteAuditLogger : IAuditLogger
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private static readonly ConcurrentDictionary<string, bool> _ensuredDatabases = new();

    public SqliteAuditLogger(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();
        EnsureTable(conn);

        const string sql = @"
            INSERT INTO audit_log (
                occurred_at_utc, action, outcome, operation_id, entity_type, entity_id,
                reason, actor_kind, actor_role, actor_session_id, actor_device_id, actor_label, client_ip
            ) VALUES (
                @OccurredAtUtc, @Action, @Outcome, @OperationId, @EntityType, @EntityId,
                @Reason, @ActorKind, @ActorRole, @ActorSessionId, @ActorDeviceId, @ActorLabel, @ClientIp
            );
        ";

        await conn.ExecuteAsync(sql, new
        {
            OccurredAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            entry.Action,
            entry.Outcome,
            entry.OperationId,
            entry.EntityType,
            entry.EntityId,
            entry.Reason,
            entry.ActorKind,
            entry.ActorRole,
            entry.ActorSessionId,
            entry.ActorDeviceId,
            entry.ActorLabel,
            entry.ClientIp
        });
    }

    public static void EnsureTable(System.Data.IDbConnection conn)
    {
        string key = conn.ConnectionString ?? "";
        if (_ensuredDatabases.ContainsKey(key)) return;

        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS audit_log (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at_utc   TEXT NOT NULL,
                action            TEXT NOT NULL,
                outcome           TEXT NOT NULL,
                operation_id      TEXT,
                entity_type       TEXT,
                entity_id         INTEGER,
                before_json       TEXT,
                after_json        TEXT,
                reason            TEXT,
                actor_kind        TEXT NOT NULL,
                actor_role        TEXT,
                actor_session_id  TEXT,
                actor_device_id   TEXT,
                actor_label       TEXT,
                client_ip         TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_audit_log_entity    ON audit_log(entity_type, entity_id, id);
            CREATE INDEX IF NOT EXISTS idx_audit_log_operation ON audit_log(operation_id);
            CREATE INDEX IF NOT EXISTS idx_audit_log_time      ON audit_log(occurred_at_utc);
        ");

        _ensuredDatabases.TryAdd(key, true);
    }
}
