using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Server.Lifecycle;

public class ServerDataLifecycle
{
    private readonly string _databasePath;
    private readonly IDatabaseBackupService _backupService;
    private readonly IDatabaseMigrator _migrator;
    private readonly ILogger<ServerDataLifecycle>? _logger;

    public string DatabasePath => _databasePath;

    public ServerDataLifecycle(
        string databasePath,
        IDatabaseBackupService backupService,
        IDatabaseMigrator migrator,
        ILogger<ServerDataLifecycle>? logger = null)
    {
        _databasePath = databasePath;
        _backupService = backupService;
        _migrator = migrator;
        _logger = logger;
    }

    public async Task<bool> InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        bool dbFileExists = File.Exists(_databasePath);

        if (dbFileExists)
        {
            _logger?.LogInformation("Cơ sở dữ liệu đã tồn tại tại '{DbPath}'. Đang tạo bản sao lưu an toàn trước khi migrate...", _databasePath);
            DatabaseBackupInfo? backupInfo = null;
            try
            {
                backupInfo = await _backupService.CreateBackupAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Sao lưu thất bại trước khi migrate. Chế độ Fail-Closed kích hoạt, hủy quá trình khởi động.");
                return false;
            }

            if (backupInfo == null || string.IsNullOrWhiteSpace(backupInfo.BackupPath) || !File.Exists(backupInfo.BackupPath))
            {
                _logger?.LogError("Bản sao lưu không tồn tại trên đĩa ({BackupPath}) sau khi gọi backup. Fail-Closed kích hoạt, hủy migrate.", backupInfo?.BackupPath);
                return false;
            }

            _logger?.LogInformation("Sao lưu trước migrate thành công: '{BackupPath}'. Tiến hành migrate schema...", backupInfo.BackupPath);
        }
        else
        {
            _logger?.LogInformation("Cơ sở dữ liệu mới (chưa tồn tại file tại '{DbPath}'). Bỏ qua bước sao lưu, tiến hành khởi tạo schema...", _databasePath);
        }

        try
        {
            await _migrator.MigrateAsync(cancellationToken);
            _logger?.LogInformation("Migrate cơ sở dữ liệu hoàn tất thành công.");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Lỗi xảy ra trong quá trình thực hiện migrate cơ sở dữ liệu.");
            throw;
        }
    }
}
