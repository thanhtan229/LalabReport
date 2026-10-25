using System;
using System.Collections.Generic;

namespace LalabAutoReport.Core.Domain;

/// <summary>
/// Gallery Manifest representing all printable images of an order, grouped by specification/folder.
/// </summary>
public class OrderGalleryManifest
{
    public long OrderId { get; set; }
    public string OrderCode { get; set; } = string.Empty;
    public string ManifestVersion { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int TotalImageCount { get; set; }
    public bool HasUnresolvedFolders { get; set; }
    public string? Notice { get; set; }
    public List<OrderGalleryGroup> Groups { get; set; } = new();
}

/// <summary>
/// A product group / specification within the order gallery
/// </summary>
public class OrderGalleryGroup
{
    public long OrderItemId { get; set; }
    public string SpecificationName { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string? SelectedPrintFolderRelativePath { get; set; }
    public bool IsFolderResolved { get; set; }
    public string? ResolutionMessage { get; set; }
    public int ImageCount => Items.Count;
    public List<OrderGalleryItem> Items { get; set; } = new();
}

/// <summary>
/// Single image item within an order gallery manifest
/// </summary>
public class OrderGalleryItem
{
    /// <summary>
    /// Opaque stable file ID (derived from relative path / item info, does not expose filesystem path)
    /// </summary>
    public string FileId { get; set; } = string.Empty;
    public long OrderItemId { get; set; }
    public string SpecificationName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }
    public string Status { get; set; } = "Ready"; // "Ready", "Missing", "Unreadable", "Failed"
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Relative path inside root folder (internal only, never serialized to clients)
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceRelativePath { get; set; } = string.Empty;
}

/// <summary>
/// Paginated page of gallery items for UI viewport rendering
/// </summary>
public class OrderGalleryPage
{
    public long OrderId { get; set; }
    public string ManifestVersion { get; set; } = string.Empty;
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int TotalCount { get; set; }
    public bool HasMore { get; set; }
    public List<OrderGalleryItemDto> Items { get; set; } = new();
}

/// <summary>
/// Client DTO for a single gallery thumbnail item
/// </summary>
public class OrderGalleryItemDto
{
    public string FileId { get; set; } = string.Empty;
    public long OrderItemId { get; set; }
    public string SpecificationName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string Status { get; set; } = "Ready";
    public string Version { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// Database cache entry tracking thumbnail metadata and usage
/// </summary>
public class ThumbnailCacheEntry
{
    public string CacheKey { get; set; } = string.Empty;
    public long? RootFolderId { get; set; }
    public string SourceRelativePath { get; set; } = string.Empty;
    public long SourceFileSize { get; set; }
    public long SourceLastWriteTicks { get; set; }
    public string CodecVersion { get; set; } = "v1";
    public string CachedFilePath { get; set; } = string.Empty;
    public long CachedFileSize { get; set; }
    public string CreatedAtUtc { get; set; } = string.Empty;
    public string LastUsedAtUtc { get; set; } = string.Empty;
    public string Status { get; set; } = "Ready"; // "Ready", "Deleting", "Pinned"
}

/// <summary>
/// Summary of thumbnail cleanup execution
/// </summary>
public class ThumbnailCleanupSummary
{
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public int ErrorsCount { get; set; }
    public DateTimeOffset ExecutedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ThumbnailCleanupSummary() { }
    public ThumbnailCleanupSummary(int filesDeleted, long bytesFreed, int errorsCount = 0)
    {
        FilesDeleted = filesDeleted;
        BytesFreed = bytesFreed;
        ErrorsCount = errorsCount;
    }
}

/// <summary>
/// Summary of clear-all cache operation
/// </summary>
public class ThumbnailClearSummary
{
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTimeOffset ExecutedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ThumbnailClearSummary() { }
    public ThumbnailClearSummary(int filesDeleted, long bytesFreed)
    {
        FilesDeleted = filesDeleted;
        BytesFreed = bytesFreed;
    }
}

public class TouchGalleryRequest
{
    public string? Version { get; set; }
    public List<string>? FileIds { get; set; }
}

