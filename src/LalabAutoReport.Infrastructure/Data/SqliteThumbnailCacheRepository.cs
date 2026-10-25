using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;

namespace LalabAutoReport.Infrastructure.Data;

public class SqliteThumbnailCacheRepository : IThumbnailCacheRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteThumbnailCacheRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<ThumbnailCacheEntry?> GetEntryAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey)) return null;

        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT cache_key AS CacheKey,
                   root_folder_id AS RootFolderId,
                   source_relative_path AS SourceRelativePath,
                   source_file_size AS SourceFileSize,
                   source_last_write_ticks AS SourceLastWriteTicks,
                   codec_version AS CodecVersion,
                   cached_file_path AS CachedFilePath,
                   cached_file_size AS CachedFileSize,
                   created_at_utc AS CreatedAtUtc,
                   last_used_at_utc AS LastUsedAtUtc,
                   status AS Status
            FROM thumbnail_cache_entries
            WHERE cache_key = @CacheKey;
        ";

        return await connection.QuerySingleOrDefaultAsync<ThumbnailCacheEntry>(sql, new { CacheKey = cacheKey });
    }

    public async Task UpsertEntryAsync(ThumbnailCacheEntry entry, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            INSERT INTO thumbnail_cache_entries (
                cache_key, root_folder_id, source_relative_path, source_file_size,
                source_last_write_ticks, codec_version, cached_file_path, cached_file_size,
                created_at_utc, last_used_at_utc, status
            ) VALUES (
                @CacheKey, @RootFolderId, @SourceRelativePath, @SourceFileSize,
                @SourceLastWriteTicks, @CodecVersion, @CachedFilePath, @CachedFileSize,
                @CreatedAtUtc, @LastUsedAtUtc, @Status
            )
            ON CONFLICT(cache_key) DO UPDATE SET
                cached_file_path = excluded.cached_file_path,
                cached_file_size = excluded.cached_file_size,
                last_used_at_utc = excluded.last_used_at_utc,
                status = excluded.status;
        ";

        await connection.ExecuteAsync(sql, new
        {
            entry.CacheKey,
            entry.RootFolderId,
            entry.SourceRelativePath,
            entry.SourceFileSize,
            entry.SourceLastWriteTicks,
            entry.CodecVersion,
            entry.CachedFilePath,
            entry.CachedFileSize,
            CreatedAtUtc = !string.IsNullOrWhiteSpace(entry.CreatedAtUtc) ? entry.CreatedAtUtc : DateTimeOffset.UtcNow.ToString("o"),
            LastUsedAtUtc = !string.IsNullOrWhiteSpace(entry.LastUsedAtUtc) ? entry.LastUsedAtUtc : DateTimeOffset.UtcNow.ToString("o"),
            entry.Status
        });
    }

    public async Task TouchAsync(string cacheKey, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey)) return;

        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            UPDATE thumbnail_cache_entries
            SET last_used_at_utc = @UsedAtUtc
            WHERE cache_key = @CacheKey AND last_used_at_utc < @UsedAtUtc;
        ";

        await connection.ExecuteAsync(sql, new
        {
            CacheKey = cacheKey,
            UsedAtUtc = usedAtUtc.ToString("o")
        });
    }

    public async Task TouchBatchAsync(IEnumerable<string> cacheKeys, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default)
    {
        var keys = cacheKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();
        if (keys.Count == 0) return;

        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            UPDATE thumbnail_cache_entries
            SET last_used_at_utc = @UsedAtUtc
            WHERE cache_key = @CacheKey AND last_used_at_utc < @UsedAtUtc;
        ";

        var list = keys.Select(k => new { CacheKey = k, UsedAtUtc = usedAtUtc.ToString("o") });
        await connection.ExecuteAsync(sql, list);
    }

    public async Task<IReadOnlyList<ThumbnailCacheEntry>> GetExpiredEntriesAsync(DateTimeOffset thresholdUtc, int limit = 500, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = @"
            SELECT cache_key AS CacheKey,
                   root_folder_id AS RootFolderId,
                   source_relative_path AS SourceRelativePath,
                   source_file_size AS SourceFileSize,
                   source_last_write_ticks AS SourceLastWriteTicks,
                   codec_version AS CodecVersion,
                   cached_file_path AS CachedFilePath,
                   cached_file_size AS CachedFileSize,
                   created_at_utc AS CreatedAtUtc,
                   last_used_at_utc AS LastUsedAtUtc,
                   status AS Status
            FROM thumbnail_cache_entries
            WHERE last_used_at_utc < @ThresholdUtc
              AND status != 'Pinned'
            ORDER BY last_used_at_utc ASC
            LIMIT @Limit;
        ";

        var rows = await connection.QueryAsync<ThumbnailCacheEntry>(sql, new
        {
            ThresholdUtc = thresholdUtc.ToString("o"),
            Limit = limit
        });

        return rows.ToList();
    }

    public async Task DeleteEntriesAsync(IEnumerable<string> cacheKeys, CancellationToken cancellationToken = default)
    {
        var keys = cacheKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        if (keys.Count == 0) return;

        using var connection = _connectionFactory.CreateConnection();
        const string sql = "DELETE FROM thumbnail_cache_entries WHERE cache_key = @Key;";
        await connection.ExecuteAsync(sql, keys.Select(k => new { Key = k }));
    }

    public async Task<long> GetTotalDiskSizeBytesAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = "SELECT COALESCE(SUM(cached_file_size), 0) FROM thumbnail_cache_entries;";
        return await connection.ExecuteScalarAsync<long>(sql);
    }

    public async Task<int> GetTotalCountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        const string sql = "SELECT COUNT(*) FROM thumbnail_cache_entries;";
        return await connection.ExecuteScalarAsync<int>(sql);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync("DELETE FROM thumbnail_cache_entries;");
    }
}
