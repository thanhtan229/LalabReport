using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LalabAutoReport.Tests;

public class BackupSecondaryCopyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly string _dbPath;

    public BackupSecondaryCopyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabBackupSecondary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Combine(_tempRoot, "lalab_autoreport.db");
        _connectionFactory = new SqliteConnectionFactory(_dbPath);
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

    private sealed class CapturingLogger : ILogger<DatabaseBackupService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private async Task CreateUnmigratedDatabaseAsync()
    {
        // A database file that exists but has never been migrated (no app_settings table).
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync("CREATE TABLE dummy (x INTEGER);");
    }

    [Fact]
    public async Task Backup_OnUnmigratedDatabase_DoesNotLogFalseSecondaryFailure()
    {
        await CreateUnmigratedDatabaseAsync();
        var logger = new CapturingLogger();
        var settingsRepo = new SqliteSettingsRepository(_connectionFactory);
        var service = new DatabaseBackupService(_connectionFactory, settingsRepo, logger);
        string destination = Path.Combine(_tempRoot, "pre_migration.db");

        var info = await service.CreateBackupAsync(destination);

        File.Exists(info.BackupPath).Should().BeTrue();
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Backup_WithConfiguredSecondaryFolder_CopiesBackup()
    {
        await new DatabaseMigrator(_connectionFactory).MigrateAsync();
        var settingsRepo = new SqliteSettingsRepository(_connectionFactory);
        string secondaryDir = Path.Combine(_tempRoot, "secondary");
        var settings = await settingsRepo.GetSettingsAsync();
        settings.SecondaryBackupFolder = secondaryDir;
        await settingsRepo.SaveSettingsAsync(settings);

        var service = new DatabaseBackupService(_connectionFactory, settingsRepo, new CapturingLogger());
        string destination = Path.Combine(_tempRoot, "backup_main.db");

        await service.CreateBackupAsync(destination);

        File.Exists(Path.Combine(secondaryDir, "backup_main.db")).Should().BeTrue();
    }

    [Fact]
    public async Task Backup_WhenSecondaryCopyFails_StillLogsWarning()
    {
        await new DatabaseMigrator(_connectionFactory).MigrateAsync();
        var settingsRepo = new SqliteSettingsRepository(_connectionFactory);
        // A regular file where the secondary directory should be makes the copy fail for real.
        string blocker = Path.Combine(_tempRoot, "not_a_directory");
        File.WriteAllText(blocker, "blocker");
        var settings = await settingsRepo.GetSettingsAsync();
        settings.SecondaryBackupFolder = Path.Combine(blocker, "nested");
        await settingsRepo.SaveSettingsAsync(settings);

        var logger = new CapturingLogger();
        var service = new DatabaseBackupService(_connectionFactory, settingsRepo, logger);

        var info = await service.CreateBackupAsync(Path.Combine(_tempRoot, "backup_fail.db"));

        File.Exists(info.BackupPath).Should().BeTrue("primary backup must still succeed");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }
}
