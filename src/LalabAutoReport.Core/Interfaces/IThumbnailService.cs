using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;

namespace LalabAutoReport.Core.Interfaces;

public enum ThumbnailStatus
{
    Success,
    UnsupportedFormat,
    FileNotFound,
    Failed
}

public class ThumbnailResult
{
    public ThumbnailStatus Status { get; set; }
    public string? CachedFilePath { get; set; }
    public string? FormatExtension { get; set; }
    public string? ErrorMessage { get; set; }

    public static ThumbnailResult SuccessResult(string path) => new() { Status = ThumbnailStatus.Success, CachedFilePath = path };
    public static ThumbnailResult Unsupported(string ext) => new() { Status = ThumbnailStatus.UnsupportedFormat, FormatExtension = ext };
    public static ThumbnailResult NotFound() => new() { Status = ThumbnailStatus.FileNotFound };
    public static ThumbnailResult FailedResult(string err) => new() { Status = ThumbnailStatus.Failed, ErrorMessage = err };
}

public interface IThumbnailService
{
    Task<ThumbnailResult> GetThumbnailAsync(string fullSourcePath, CancellationToken cancellationToken = default);
    Task TouchLastUsedAsync(string fullSourcePath, CancellationToken cancellationToken = default);
    Task TouchLastUsedBatchAsync(IEnumerable<string> fullSourcePaths, CancellationToken cancellationToken = default);
    void Pause();
    void Resume();
    void ClearMemoryCache();
    void ClearDiskCache();
    long GetDiskCacheSizeBytes();
    void CleanupExpiredDiskCache(TimeSpan maxAge);
    Task<ThumbnailCleanupSummary> CleanupExpiredDiskCacheAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
    Task<ThumbnailClearSummary> ClearAllDiskCacheAsync(CancellationToken cancellationToken = default);
}
