using System;
using System.IO;
using System.Text;
using System.Threading;

namespace LalabAutoReport.Server.Hosting;

public sealed class ServerInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Global\LalabAutoReport.Server";

    private readonly string _dataDir;
    private readonly string _lockFilePath;
    private readonly string _mutexName;
    private Mutex? _mutex;
    private FileStream? _fileLock;
    private bool _hasMutexOwnership;

    public string DataDirectory => _dataDir;
    public string LockFilePath => _lockFilePath;
    public bool IsAcquired => _fileLock != null && _hasMutexOwnership;

    public ServerInstanceGuard(string dataDir, string? mutexName = null)
    {
        _dataDir = dataDir;
        _lockFilePath = Path.Combine(dataDir, "server.lock");
        _mutexName = mutexName ?? DefaultMutexName;
    }

    public bool TryAcquire(out string? failureReason)
    {
        failureReason = null;
        try
        {
            // 1. Acquire Named Mutex
            try
            {
                _mutex = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
                if (!createdNew)
                {
                    if (!_mutex.WaitOne(0, false))
                    {
                        failureReason = $"Named mutex '{_mutexName}' đang được nắm giữ bởi một tiến trình server khác.";
                        _mutex.Dispose();
                        _mutex = null;
                        return false;
                    }
                }
                _hasMutexOwnership = true;
            }
            catch (AbandonedMutexException)
            {
                _hasMutexOwnership = true;
            }
            catch (Exception ex)
            {
                failureReason = $"Không thể khởi tạo hoặc chiếm quyền mutex '{_mutexName}': {ex.Message}";
                ReleaseMutexInternal();
                return false;
            }

            // 2. Ensure data directory exists
            try
            {
                Directory.CreateDirectory(_dataDir);
            }
            catch (Exception ex)
            {
                failureReason = $"Không thể tạo hoặc truy cập thư mục dữ liệu '{_dataDir}': {ex.Message}";
                ReleaseMutexInternal();
                return false;
            }

            // 3. Acquire exclusive file lock on server.lock (FileShare.None)
            try
            {
                _fileLock = new FileStream(
                    _lockFilePath,
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    FileOptions.None);

                string content = $"pid={Environment.ProcessId}\nstarted_at_utc={DateTimeOffset.UtcNow:o}\n";
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                _fileLock.Write(bytes, 0, bytes.Length);
                _fileLock.Flush();
            }
            catch (IOException ex)
            {
                failureReason = $"Tập tin khóa '{_lockFilePath}' đang bị khóa bởi tiến trình khác (FileShare.None): {ex.Message}";
                ReleaseMutexInternal();
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                failureReason = $"Không có quyền ghi vào tập tin khóa '{_lockFilePath}': {ex.Message}";
                ReleaseMutexInternal();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"Lỗi không xác định khi acquire guard: {ex.Message}";
            Dispose();
            return false;
        }
    }

    private void ReleaseMutexInternal()
    {
        if (_mutex != null)
        {
            if (_hasMutexOwnership)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
                _hasMutexOwnership = false;
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    public void Dispose()
    {
        if (_fileLock != null)
        {
            try
            {
                _fileLock.Dispose();
            }
            catch { }
            _fileLock = null;

            try
            {
                if (File.Exists(_lockFilePath))
                {
                    File.Delete(_lockFilePath);
                }
            }
            catch { }
        }

        ReleaseMutexInternal();
    }
}
