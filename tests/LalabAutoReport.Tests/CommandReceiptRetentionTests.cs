using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class CommandReceiptRetentionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqliteConnectionFactory _connectionFactory;

    public CommandReceiptRetentionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabReceiptRetention_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _connectionFactory = new SqliteConnectionFactory(Path.Combine(_tempRoot, "receipts.db"));
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
        catch { }
    }

    private async Task SeedReceiptAsync(string operationId, DateTimeOffset createdAtUtc)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO command_receipts (
                operation_id, capability, entity_type, entity_id, payload_hash, status, result_json,
                actor_kind, actor_session_id, actor_role, created_at_utc, completed_at_utc
            ) VALUES (
                @OperationId, 'orders.set_delivered', 'order', 1, 'hash', 'committed', '{}',
                'session', NULL, 'Admin', @CreatedAt, @CreatedAt
            );", new { OperationId = operationId, CreatedAt = createdAtUtc.ToString("o") });
    }

    private async Task<long> CountReceiptsAsync()
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QuerySingleAsync<long>("SELECT COUNT(*) FROM command_receipts;");
    }

    [Fact]
    public async Task Prune_RemovesReceiptsPastWindow_AndKeepsRecentOnes()
    {
        await new DatabaseMigrator(_connectionFactory).MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedReceiptAsync("old-op", now.AddDays(-100));
        await SeedReceiptAsync("recent-op", now.AddDays(-1));

        var retention = new SqliteCommandReceiptRetention(_connectionFactory);
        int removed = await retention.PruneAsync(now, retentionDays: 90);

        removed.Should().Be(1);
        (await CountReceiptsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Prune_WithNoExpiredReceipts_RemovesNothing()
    {
        await new DatabaseMigrator(_connectionFactory).MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedReceiptAsync("recent-op", now.AddDays(-5));

        int removed = await new SqliteCommandReceiptRetention(_connectionFactory).PruneAsync(now, retentionDays: 90);

        removed.Should().Be(0);
        (await CountReceiptsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Prune_RejectsNonPositiveWindow()
    {
        await new DatabaseMigrator(_connectionFactory).MigrateAsync();
        var retention = new SqliteCommandReceiptRetention(_connectionFactory);

        var act = async () => await retention.PruneAsync(DateTimeOffset.UtcNow, retentionDays: 0);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
