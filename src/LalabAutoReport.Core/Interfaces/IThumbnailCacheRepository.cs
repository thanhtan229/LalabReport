using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;

namespace LalabAutoReport.Core.Interfaces;

public interface IThumbnailCacheRepository
{
    Task<ThumbnailCacheEntry?> GetEntryAsync(string cacheKey, CancellationToken cancellationToken = default);
    Task UpsertEntryAsync(ThumbnailCacheEntry entry, CancellationToken cancellationToken = default);
    Task TouchAsync(string cacheKey, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default);
    Task TouchBatchAsync(IEnumerable<string> cacheKeys, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ThumbnailCacheEntry>> GetExpiredEntriesAsync(DateTimeOffset thresholdUtc, int limit = 500, CancellationToken cancellationToken = default);
    Task DeleteEntriesAsync(IEnumerable<string> cacheKeys, CancellationToken cancellationToken = default);
    Task<long> GetTotalDiskSizeBytesAsync(CancellationToken cancellationToken = default);
    Task<int> GetTotalCountAsync(CancellationToken cancellationToken = default);
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
