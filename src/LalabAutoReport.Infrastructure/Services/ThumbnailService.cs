using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Services;

public class ThumbnailService : IThumbnailService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp"
    };

    private static readonly HashSet<string> UnsupportedHeavyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".psd", ".psb", ".raw", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".tif", ".tiff"
    };

    private readonly IFileSystemAdapter _fileSystem;
    private readonly IThumbnailCacheRepository? _cacheRepository;
    private readonly ILogger<ThumbnailService>? _logger;
    private readonly string _cacheDirectory;

    // Single worker concurrency: only 1 image decoded at a time to prevent disk I/O thrashing
    private readonly SemaphoreSlim _workerLock = new(1, 1);

    // Pause/Resume coordination
    private readonly AsyncManualResetEvent _pauseGate = new(initialState: true);

    // Tier 1: In-memory cache for fast lookup (max ~100 items)
    private readonly ConcurrentDictionary<string, ThumbnailResult> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    // Inflight deduplication: coalesce concurrent requests for the same source file
    private readonly ConcurrentDictionary<string, Task<ThumbnailResult>> _inflightTasks = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<DateTimeOffset> _utcNowProvider;

    public ThumbnailService(
        IFileSystemAdapter fileSystem,
        string? cacheDirectory = null,
        IThumbnailCacheRepository? cacheRepository = null,
        ILogger<ThumbnailService>? logger = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _fileSystem = fileSystem;
        _cacheRepository = cacheRepository;
        _logger = logger;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LalabAutoReport",
            "Thumbnails");

        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create thumbnail cache directory '{Dir}'", _cacheDirectory);
        }
    }

    public async Task<ThumbnailResult> GetThumbnailAsync(string fullSourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullSourcePath))
        {
            return ThumbnailResult.NotFound();
        }

        // Fast-fail check
        string ext = Path.GetExtension(fullSourcePath);
        if (UnsupportedHeavyExtensions.Contains(ext))
        {
            return ThumbnailResult.Unsupported(ext.TrimStart('.').ToUpperInvariant());
        }

        if (!SupportedExtensions.Contains(ext))
        {
            return ThumbnailResult.Unsupported(ext.TrimStart('.').ToUpperInvariant());
        }

        if (!_fileSystem.FileExists(fullSourcePath))
        {
            return ThumbnailResult.NotFound();
        }

        // Calculate cache key based on file metadata (path, length, last write time)
        string cacheKey;
        string cachedFilePath;
        long sourceLength;
        long sourceTicks;
        try
        {
            var fi = new FileInfo(fullSourcePath);
            if (!fi.Exists)
            {
                return ThumbnailResult.NotFound();
            }

            sourceLength = fi.Length;
            sourceTicks = fi.LastWriteTimeUtc.Ticks;
            string rawKey = $"{fullSourcePath}|{sourceLength}|{sourceTicks}|v1";
            using var md5 = MD5.Create();
            string hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
            cacheKey = hash;
            cachedFilePath = Path.Combine(_cacheDirectory, $"{hash}.jpg");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to inspect file '{Path}' for thumbnail", fullSourcePath);
            return ThumbnailResult.FailedResult(ex.Message);
        }

        // Check Tier 1 (RAM)
        if (_memoryCache.TryGetValue(cacheKey, out var memCached))
        {
            if (memCached.CachedFilePath != null && File.Exists(memCached.CachedFilePath))
            {
                TouchCacheKeyBackground(cacheKey);
                return memCached;
            }
        }

        // Check Tier 2 (Disk)
        if (File.Exists(cachedFilePath))
        {
            var diskResult = ThumbnailResult.SuccessResult(cachedFilePath);
            AddToMemoryCache(cacheKey, diskResult);
            RecordOrTouchDiskCacheHitBackground(cacheKey, fullSourcePath, sourceLength, sourceTicks, cachedFilePath);
            return diskResult;
        }

        // Coalesce concurrent requests for the exact same cacheKey
        Task<ThumbnailResult>? inflightTask;
        bool isTaskOwner = false;
        var tcs = new TaskCompletionSource<ThumbnailResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        while (true)
        {
            if (_inflightTasks.TryGetValue(cacheKey, out inflightTask))
            {
                break;
            }
            if (_inflightTasks.TryAdd(cacheKey, tcs.Task))
            {
                inflightTask = tcs.Task;
                isTaskOwner = true;
                break;
            }
        }

        if (!isTaskOwner)
        {
            try
            {
                return await inflightTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        try
        {
            // Wait if paused (e.g. active scan or order lock in progress)
            await _pauseGate.WaitAsync(cancellationToken);

            // Queue to single worker to generate thumbnail
            await _workerLock.WaitAsync(cancellationToken);
            try
            {
                // Re-check disk cache inside lock in case another task generated it
                if (File.Exists(cachedFilePath))
                {
                    var diskResult = ThumbnailResult.SuccessResult(cachedFilePath);
                    AddToMemoryCache(cacheKey, diskResult);
                    RecordOrTouchDiskCacheHitBackground(cacheKey, fullSourcePath, sourceLength, sourceTicks, cachedFilePath);
                    tcs.TrySetResult(diskResult);
                    return diskResult;
                }

                // Also check pause state inside lock
                await _pauseGate.WaitAsync(cancellationToken);

                // Decode image using WIC with downsampling and EXIF orientation
                var result = await Task.Run(() => GenerateThumbnailCore(fullSourcePath, cachedFilePath), cancellationToken);
                if (result.Status == ThumbnailStatus.Success && result.CachedFilePath != null && File.Exists(result.CachedFilePath))
                {
                    AddToMemoryCache(cacheKey, result);

                    // Record to repository
                    if (_cacheRepository != null)
                    {
                        try
                        {
                            var cachedFi = new FileInfo(result.CachedFilePath);
                            var nowStr = _utcNowProvider().ToString("o");
                            await _cacheRepository.UpsertEntryAsync(new ThumbnailCacheEntry
                            {
                                CacheKey = cacheKey,
                                SourceRelativePath = fullSourcePath,
                                SourceFileSize = sourceLength,
                                SourceLastWriteTicks = sourceTicks,
                                CodecVersion = "v1",
                                CachedFilePath = result.CachedFilePath,
                                CachedFileSize = cachedFi.Exists ? cachedFi.Length : 0,
                                CreatedAtUtc = nowStr,
                                LastUsedAtUtc = nowStr,
                                Status = "Ready"
                            }, cancellationToken);
                        }
                        catch (Exception repoEx)
                        {
                            _logger?.LogDebug(repoEx, "Could not record thumbnail cache entry in repository");
                        }
                    }
                }

                tcs.TrySetResult(result);
                return result;
            }
            finally
            {
                _workerLock.Release();
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _inflightTasks.TryRemove(cacheKey, out _);
        }
    }

    private ThumbnailResult GenerateThumbnailCore(string sourcePath, string targetCachedPath)
    {
        string tempPath = Path.Combine(_cacheDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fileStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var decoder = BitmapDecoder.Create(
                    fileStream,
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.None);

                if (decoder.Frames.Count == 0)
                {
                    return ThumbnailResult.FailedResult("No frames found in image");
                }

                var frame = decoder.Frames[0];
                double origW = frame.PixelWidth;
                double origH = frame.PixelHeight;

                if (origW <= 0 || origH <= 0)
                {
                    return ThumbnailResult.FailedResult("Invalid image dimensions");
                }

                var rotation = GetRotationFromExif(frame);
                double effectiveW = (rotation == Rotation.Rotate90 || rotation == Rotation.Rotate270) ? origH : origW;
                double effectiveH = (rotation == Rotation.Rotate90 || rotation == Rotation.Rotate270) ? origW : origH;

                const double maxDim = 320.0;
                double scale = Math.Min(maxDim / effectiveW, maxDim / effectiveH);
                if (scale > 1.0)
                {
                    scale = 1.0; // Do not upscale smaller images
                }

                BitmapSource outputSource;
                bool needsTransform = (scale < 0.999) || (rotation != Rotation.Rotate0);

                if (needsTransform)
                {
                    var group = new TransformGroup();
                    if (scale < 0.999)
                    {
                        group.Children.Add(new ScaleTransform(scale, scale));
                    }
                    if (rotation != Rotation.Rotate0)
                    {
                        double angle = rotation switch
                        {
                            Rotation.Rotate90 => 90.0,
                            Rotation.Rotate180 => 180.0,
                            Rotation.Rotate270 => 270.0,
                            _ => 0.0
                        };
                        group.Children.Add(new RotateTransform(angle));
                    }

                    var transformed = new TransformedBitmap(frame, group);
                    transformed.Freeze();
                    outputSource = transformed;
                }
                else
                {
                    outputSource = frame;
                }

                var encoder = new JpegBitmapEncoder
                {
                    QualityLevel = 80
                };
                encoder.Frames.Add(BitmapFrame.Create(outputSource));

                using (var outStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(outStream);
                }
            }

            if (File.Exists(targetCachedPath))
            {
                File.Delete(targetCachedPath);
            }
            File.Move(tempPath, targetCachedPath);

            return ThumbnailResult.SuccessResult(targetCachedPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error decoding thumbnail for '{Path}'", sourcePath);
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }

            return ThumbnailResult.FailedResult(ex.Message);
        }
    }

    private static Rotation GetRotationFromExif(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                var val = metadata.GetQuery("/app1/ifd/{ushort=274}");
                if (val is ushort orientation)
                {
                    return orientation switch
                    {
                        3 => Rotation.Rotate180,
                        6 => Rotation.Rotate90,
                        8 => Rotation.Rotate270,
                        _ => Rotation.Rotate0
                    };
                }
            }
        }
        catch { }
        return Rotation.Rotate0;
    }

    public async Task TouchLastUsedAsync(string fullSourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullSourcePath) || _cacheRepository == null) return;
        try
        {
            var fi = new FileInfo(fullSourcePath);
            if (!fi.Exists) return;
            string rawKey = $"{fullSourcePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|v1";
            using var md5 = MD5.Create();
            string hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
            await _cacheRepository.TouchAsync(hash, _utcNowProvider(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error touching thumbnail last used for '{Path}'", fullSourcePath);
        }
    }

    public async Task TouchLastUsedBatchAsync(IEnumerable<string> fullSourcePaths, CancellationToken cancellationToken = default)
    {
        if (_cacheRepository == null) return;
        var hashes = new List<string>();
        using var md5 = MD5.Create();
        foreach (var path in fullSourcePaths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var fi = new FileInfo(path);
                if (!fi.Exists) continue;
                string rawKey = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|v1";
                string hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
                hashes.Add(hash);
            }
            catch { }
        }

        if (hashes.Count > 0)
        {
            await _cacheRepository.TouchBatchAsync(hashes, _utcNowProvider(), cancellationToken);
        }
    }

    private void TouchCacheKeyBackground(string cacheKey)
    {
        if (_cacheRepository == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _cacheRepository.TouchAsync(cacheKey, _utcNowProvider());
            }
            catch { }
        });
    }

    private void RecordOrTouchDiskCacheHitBackground(string cacheKey, string fullSourcePath, long sourceLength, long sourceTicks, string cachedFilePath)
    {
        if (_cacheRepository == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var entry = await _cacheRepository.GetEntryAsync(cacheKey);
                var now = _utcNowProvider();
                if (entry == null)
                {
                    var cachedFi = new FileInfo(cachedFilePath);
                    await _cacheRepository.UpsertEntryAsync(new ThumbnailCacheEntry
                    {
                        CacheKey = cacheKey,
                        SourceRelativePath = fullSourcePath,
                        SourceFileSize = sourceLength,
                        SourceLastWriteTicks = sourceTicks,
                        CodecVersion = "v1",
                        CachedFilePath = cachedFilePath,
                        CachedFileSize = cachedFi.Exists ? cachedFi.Length : 0,
                        CreatedAtUtc = now.ToString("o"),
                        LastUsedAtUtc = now.ToString("o"),
                        Status = "Ready"
                    });
                }
                else
                {
                    await _cacheRepository.TouchAsync(cacheKey, now);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to record or touch disk cache hit for '{Path}'", fullSourcePath);
            }
        });
    }

    private void AddToMemoryCache(string key, ThumbnailResult result)
    {
        if (_memoryCache.Count > 100)
        {
            _memoryCache.Clear();
        }
        _memoryCache[key] = result;
    }

    public void Pause()
    {
        _pauseGate.Reset();
    }

    public void Resume()
    {
        _pauseGate.Set();
    }

    public void ClearMemoryCache()
    {
        _memoryCache.Clear();
    }

    public void ClearDiskCache()
    {
        ClearMemoryCache();
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                var files = Directory.GetFiles(_cacheDirectory, "*.*");
                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            _ = _cacheRepository?.ClearAllAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error clearing disk cache in '{Dir}'", _cacheDirectory);
        }
    }

    public async Task<ThumbnailClearSummary> ClearAllDiskCacheAsync(CancellationToken cancellationToken = default)
    {
        ClearMemoryCache();
        var summary = new ThumbnailClearSummary();
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                var files = Directory.GetFiles(_cacheDirectory, "*.*");
                foreach (var f in files)
                {
                    try
                    {
                        var len = new FileInfo(f).Length;
                        File.Delete(f);
                        summary.FilesDeleted++;
                        summary.BytesFreed += len;
                    }
                    catch { }
                }
            }

            if (_cacheRepository != null)
            {
                await _cacheRepository.ClearAllAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error in ClearAllDiskCacheAsync");
        }

        return summary;
    }

    public long GetDiskCacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory)) return 0;
            var files = Directory.GetFiles(_cacheDirectory, "*.*");
            long total = 0;
            foreach (var f in files)
            {
                try
                {
                    total += new FileInfo(f).Length;
                }
                catch { }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    public void CleanupExpiredDiskCache(TimeSpan maxAge)
    {
        _ = CleanupExpiredDiskCacheAsync(maxAge).GetAwaiter().GetResult();
    }

    public async Task<ThumbnailCleanupSummary> CleanupExpiredDiskCacheAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var summary = new ThumbnailCleanupSummary();
        try
        {
            if (!Directory.Exists(_cacheDirectory)) return summary;
            var thresholdUtc = _utcNowProvider() - maxAge;

            if (_cacheRepository != null)
            {
                var expired = await _cacheRepository.GetExpiredEntriesAsync(thresholdUtc, limit: 1000, cancellationToken);
                var deletedKeys = new List<string>();

                foreach (var entry in expired)
                {
                    try
                    {
                        if (File.Exists(entry.CachedFilePath))
                        {
                            var fi = new FileInfo(entry.CachedFilePath);
                            long len = fi.Length;
                            fi.Delete();
                            summary.FilesDeleted++;
                            summary.BytesFreed += len;
                        }
                        deletedKeys.Add(entry.CacheKey);
                    }
                    catch (Exception ex)
                    {
                        summary.ErrorsCount++;
                        _logger?.LogDebug(ex, "Failed deleting expired thumbnail '{Path}'", entry.CachedFilePath);
                    }
                }

                if (deletedKeys.Count > 0)
                {
                    await _cacheRepository.DeleteEntriesAsync(deletedKeys, cancellationToken);
                }
            }
            else
            {
                // Fallback for legacy disk cache cleanup without DB repository
                var files = Directory.GetFiles(_cacheDirectory, "*.*");
                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        var lastUsedUtc = fi.LastWriteTimeUtc > fi.LastAccessTimeUtc ? fi.LastWriteTimeUtc : fi.LastAccessTimeUtc;
                        if (lastUsedUtc < thresholdUtc)
                        {
                            long len = fi.Length;
                            fi.Delete();
                            summary.FilesDeleted++;
                            summary.BytesFreed += len;
                        }
                    }
                    catch (Exception ex)
                    {
                        summary.ErrorsCount++;
                        _logger?.LogDebug(ex, "Error deleting legacy file '{Path}'", f);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            summary.ErrorsCount++;
            _logger?.LogWarning(ex, "Error cleaning expired disk cache");
        }

        return summary;
    }

    private class AsyncManualResetEvent
    {
        private volatile TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsyncManualResetEvent(bool initialState)
        {
            if (initialState)
            {
                _tcs.TrySetResult(true);
            }
        }

        public Task WaitAsync(CancellationToken cancellationToken = default)
        {
            return _tcs.Task.WaitAsync(cancellationToken);
        }

        public void Set()
        {
            _tcs.TrySetResult(true);
        }

        public void Reset()
        {
            while (true)
            {
                var current = _tcs;
                if (!current.Task.IsCompleted) return;
                var next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (Interlocked.CompareExchange(ref _tcs, next, current) == current) return;
            }
        }
    }
}
