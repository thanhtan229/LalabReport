using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class MigrationBackupGateTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;

    public MigrationBackupGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LalabMigGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "gate.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch { }
    }

    private sealed class ScriptedBackupService : IDatabaseBackupService
    {
        private readonly bool _fail;
        public int Calls { get; private set; }

        public ScriptedBackupService(bool fail) => _fail = fail;

        public Task<DatabaseBackupInfo> CreateBackupAsync(string? customDestinationPath = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_fail) throw new IOException("Disk full (simulated)");
            return Task.FromResult(new DatabaseBackupInfo("simulated.db", "simulated.db", 0, DateTime.UtcNow));
        }

        public Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DatabaseBackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>(Array.Empty<DatabaseBackupInfo>());

        public string GetDatabasePath() => "simulated";
    }

    private async Task<long> MaxAppliedVersionAsync()
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations;");
    }

    [Fact]
    public async Task ExistingDatabase_WhenBackupFails_RefusesMigrationAndLeavesSchemaUnchanged()
    {
        await new DatabaseMigrator(_factory).MigrateToVersionAsync(24);
        long before = await MaxAppliedVersionAsync();
        var backup = new ScriptedBackupService(fail: true);

        var act = async () => await new DatabaseMigrator(_factory, backup).MigrateAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        backup.Calls.Should().Be(1);
        (await MaxAppliedVersionAsync()).Should().Be(before, "no pending migration may run without a restore point");
    }

    [Fact]
    public async Task ExistingDatabase_WhenBackupSucceeds_MigratesToLatest()
    {
        await new DatabaseMigrator(_factory).MigrateToVersionAsync(24);
        var backup = new ScriptedBackupService(fail: false);

        await new DatabaseMigrator(_factory, backup).MigrateAsync();

        backup.Calls.Should().Be(1);
        (await MaxAppliedVersionAsync()).Should().Be(DatabaseMigrator.LatestVersion);
    }

    [Fact]
    public async Task BrandNewDatabase_DoesNotRequireBackup_AndMigratesToLatest()
    {
        var backup = new ScriptedBackupService(fail: true);

        await new DatabaseMigrator(_factory, backup).MigrateAsync();

        backup.Calls.Should().Be(0, "a new database has no data to protect");
        (await MaxAppliedVersionAsync()).Should().Be(DatabaseMigrator.LatestVersion);
    }
}
