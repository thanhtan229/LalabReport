using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace LalabAutoReport.Infrastructure.Data;

// command_receipts is the idempotency store for order commands. Receipts older than the window
// are pruned so the table does not grow without bound. Safety relies on the command contract:
// every command carries ExpectedRevision and a desired value, so a replay of a pruned operation
// either fails with a stale-revision conflict or re-applies the same desired state (no-op).
// audit_log and change_journal are NOT touched; they keep their own history.
public sealed class SqliteCommandReceiptRetention
{
    public const int DefaultRetentionDays = 90;

    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteCommandReceiptRetention(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> PruneAsync(
        DateTimeOffset nowUtc,
        int retentionDays = DefaultRetentionDays,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), "Retention window must be at least one day.");
        }

        // created_at_utc is written with DateTimeOffset "o" in UTC (fixed-width, +00:00),
        // so lexical comparison matches chronological order.
        string cutoffIso = nowUtc.ToUniversalTime().AddDays(-retentionDays).ToString("o");

        using var connection = _connectionFactory.CreateConnection();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM command_receipts WHERE created_at_utc < @Cutoff;",
            new { Cutoff = cutoffIso },
            cancellationToken: cancellationToken));
    }
}
