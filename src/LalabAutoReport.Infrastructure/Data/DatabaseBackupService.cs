using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Data;

public class DatabaseBackupService : IDatabaseBackupService
{
    private const int MaxDefaultBackupsToKeep = 20;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly ILogger<DatabaseBackupService>? _logger;

    public DatabaseBackupService(
        ISqliteConnectionFactory connectionFactory, 
        ISettingsRepository? settingsRepository = null,
        ILogger<DatabaseBackupService>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _settingsRepository = settingsRepository;
        _logger = logger;
    }

    public string GetDatabasePath() => _connectionFactory.DatabasePath;

    public async Task<DatabaseBackupInfo> CreateBackupAsync(string? customDestinationPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string dbPath = _connectionFactory.DatabasePath;
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException($"Cơ sở dữ liệu không tồn tại tại đường dẫn: {dbPath}");
        }

        string destinationPath;
        bool isDefaultLocation = false;

        if (string.IsNullOrWhiteSpace(customDestinationPath))
        {
            string backupDir = GetDefaultBackupDirectory();
            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            string fileName = $"lalab_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db";
            destinationPath = Path.Combine(backupDir, fileName);
            isDefaultLocation = true;
        }
        else
        {
            destinationPath = customDestinationPath;
            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        _logger?.LogInformation("Creating SQLite database backup to '{Destination}'", destinationPath);

        // Perform online backup via SQLite Backup API
        using (var sourceConnection = _connectionFactory.CreateConnection())
        {
            using var destinationConnection = new SqliteConnection($"Data Source={destinationPath};");
            destinationConnection.Open();
            sourceConnection.BackupDatabase(destinationConnection);
        }

        // Clean up older default backups if needed
        if (isDefaultLocation)
        {
            CleanupOldBackups();
        }

        // Secondary backup copy if configured (Google Drive / OneDrive / NAS).
        // A database that has not been migrated yet has no app_settings table, so no secondary
        // folder can be configured there; skip the lookup instead of logging a false failure.
        if (_settingsRepository != null && await AppSettingsTableExistsAsync(cancellationToken))
        {
            try
            {
                var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(settings.SecondaryBackupFolder))
                {
                    if (!Directory.Exists(settings.SecondaryBackupFolder))
                    {
                        Directory.CreateDirectory(settings.SecondaryBackupFolder);
                    }
                    string secondaryDest = Path.Combine(settings.SecondaryBackupFolder, Path.GetFileName(destinationPath));
                    File.Copy(destinationPath, secondaryDest, overwrite: true);
                    _logger?.LogInformation("Secondary database backup successfully copied to '{SecondaryDestination}'", secondaryDest);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to copy backup to secondary location: {Message}", ex.Message);
            }
        }

        var fileInfo = new FileInfo(destinationPath);
        _logger?.LogInformation("Database backup successfully created. Size: {Size} bytes", fileInfo.Length);

        var result = new DatabaseBackupInfo(
            BackupPath: destinationPath,
            FileName: fileInfo.Name,
            FileSizeBytes: fileInfo.Length,
            CreatedAtUtc: fileInfo.CreationTimeUtc
        );

        return result;
    }

    private async Task<bool> AppSettingsTableExistsAsync(CancellationToken cancellationToken)
    {
        using var connection = _connectionFactory.CreateConnection();
        long count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_settings';",
            cancellationToken: cancellationToken));
        return count > 0;
    }

    public async Task RestoreBackupAsync(string backupFilePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(backupFilePath))
        {
            throw new FileNotFoundException($"File sao lưu không tồn tại: {backupFilePath}");
        }

        using var lifecycle = await DatabaseLifecycleGuard.EnterAsync(_connectionFactory.DatabasePath, cancellationToken);
        using (var current = _connectionFactory.CreateConnection())
            await DatabaseLifecycleGuard.EnsureLocalOnlyAsync(current);
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupFilePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            source.Open();
            await DatabaseLifecycleGuard.EnsureLocalOnlyAsync(source);
        }

        _logger?.LogInformation("Initiating database restore from '{BackupPath}'", backupFilePath);

        // 1. Create a pre-restore safety backup of current state if database exists
        string dbPath = _connectionFactory.DatabasePath;
        if (File.Exists(dbPath))
        {
            string preRestoreBackup = Path.Combine(
                GetDefaultBackupDirectory(),
                $"lalab_pre_restore_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db"
            );
            await CreateBackupAsync(preRestoreBackup, cancellationToken);
        }

        // 2. Clear all connection pools to ensure no lingering locks
        SqliteConnection.ClearAllPools();

        // 3. Online restore from backup file to main database
        using (var backupConnection = new SqliteConnection($"Data Source={backupFilePath};"))
        {
            backupConnection.Open();
            using var targetConnection = _connectionFactory.CreateConnection();
            backupConnection.BackupDatabase(targetConnection);
        }

        SqliteConnection.ClearAllPools();
        _logger?.LogInformation("Database restore completed successfully from '{BackupPath}'", backupFilePath);
    }

    public Task<IReadOnlyList<DatabaseBackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string backupDir = GetDefaultBackupDirectory();
        if (!Directory.Exists(backupDir))
        {
            return Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>(Array.Empty<DatabaseBackupInfo>());
        }

        var dirInfo = new DirectoryInfo(backupDir);
        var files = dirInfo.GetFiles("lalab_*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new DatabaseBackupInfo(
                BackupPath: f.FullName,
                FileName: f.Name,
                FileSizeBytes: f.Length,
                CreatedAtUtc: f.CreationTimeUtc
            ))
            .ToList();

        return Task.FromResult<IReadOnlyList<DatabaseBackupInfo>>(files);
    }

    private string GetDefaultBackupDirectory()
    {
        string dbDir = Path.GetDirectoryName(_connectionFactory.DatabasePath)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LalabAutoReport");
        return Path.Combine(dbDir, "backups");
    }

    private void CleanupOldBackups()
    {
        try
        {
            string backupDir = GetDefaultBackupDirectory();
            if (!Directory.Exists(backupDir)) return;

            var dirInfo = new DirectoryInfo(backupDir);
            var files = dirInfo.GetFiles("lalab_backup_*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(MaxDefaultBackupsToKeep)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                    _logger?.LogInformation("Cleaned up old backup file: {FileName}", file.Name);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete old backup file '{File}'", file.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed during backup cleanup");
        }
    }
}
