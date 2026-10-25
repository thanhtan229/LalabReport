using LalabAutoReport.Core.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Services;

public class OrderGalleryService : IOrderGalleryService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg"
    };

    private readonly IOrderRepository _orderRepository;
    private readonly IRootFolderRepository? _rootFolderRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IFileSystemAdapter _fileSystem;
    private readonly IThumbnailService _thumbnailService;
    private readonly ILogger<OrderGalleryService>? _logger;

    // Cache active manifests in memory keyed by OrderId
    private readonly ConcurrentDictionary<long, OrderGalleryManifest> _manifestCache = new();

    public OrderGalleryService(
        IOrderRepository orderRepository,
        ISettingsRepository settingsRepository,
        IFileSystemAdapter fileSystem,
        IThumbnailService thumbnailService,
        IRootFolderRepository? rootFolderRepository = null,
        ILogger<OrderGalleryService>? logger = null)
    {
        _orderRepository = orderRepository;
        _settingsRepository = settingsRepository;
        _fileSystem = fileSystem;
        _thumbnailService = thumbnailService;
        _rootFolderRepository = rootFolderRepository;
        _logger = logger;
    }

    private async Task<string?> ResolveRootPathAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.RootFolderId.HasValue)
        {
            if (_rootFolderRepository != null)
            {
                var rf = await _rootFolderRepository.GetByIdAsync(order.RootFolderId.Value, cancellationToken);
                if (rf != null && !string.IsNullOrWhiteSpace(rf.FullPath) && _fileSystem.DirectoryExists(rf.FullPath))
                {
                    return rf.FullPath;
                }
            }
            // CRITICAL: Do NOT fallback to settings.RootFolder when order is pinned to a specific RootFolderId
            return null;
        }

        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.RootFolder) && _fileSystem.DirectoryExists(settings.RootFolder))
        {
            return settings.RootFolder;
        }

        return null;
    }

    public async Task<OrderGalleryManifest> GetOrderManifestAsync(long orderId, CancellationToken cancellationToken = default)
    {
        var order = await _orderRepository.GetOrderByIdAsync(orderId, cancellationToken);
        if (order == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy đơn hàng với ID {orderId}.");
        }

        string? rootPath = await ResolveRootPathAsync(order, cancellationToken);

        var manifest = new OrderGalleryManifest
        {
            OrderId = order.Id,
            OrderCode = order.OrderCode ?? string.Empty,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };

        if (order.Status == OrderStatus.Locked || order.Status == OrderStatus.Billed)
        {
            manifest.Notice = "Đơn hàng đã lập bill/khoá: Thư viện hiển thị ảnh hiện có trên ổ đĩa tại thời điểm xem.";
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            string missingRootNotice = order.RootFolderId.HasValue
                ? "Thư mục gốc của đơn hàng không khả dụng hoặc ngoại tuyến."
                : "Thư mục kho dữ liệu của đơn hàng không khả dụng trên máy này.";

            manifest.Notice = string.IsNullOrWhiteSpace(manifest.Notice)
                ? missingRootNotice
                : $"{manifest.Notice} ({missingRootNotice})";

            manifest.ManifestVersion = GenerateVersionString(order.Id, "no_root");
            _manifestCache[order.Id] = manifest;
            return manifest;
        }

        string canonicalRoot = Path.GetFullPath(rootPath);
        string canonicalRootWithSeparator = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var comparer = NaturalStringComparer.Instance;
        var versionBuilder = new StringBuilder();
        versionBuilder.Append($"{order.Id}|{order.UpdatedAt.Ticks}|");

        int totalCount = 0;
        bool hasUnresolved = false;

        foreach (var item in order.Items)
        {
            var group = new OrderGalleryGroup
            {
                OrderItemId = item.Id,
                SpecificationName = item.PrintSpecification?.CanonicalName ?? item.SpecificationFolderName,
                FolderName = item.SpecificationFolderName,
                SelectedPrintFolderRelativePath = item.SelectedPrintFolderRelativePath
            };

            if (string.IsNullOrWhiteSpace(item.SelectedPrintFolderRelativePath))
            {
                group.IsFolderResolved = false;
                group.ResolutionMessage = "Chưa xác định thư mục in";
                hasUnresolved = true;
                manifest.Groups.Add(group);
                continue;
            }

            string fullItemFolder = Path.GetFullPath(Path.Combine(canonicalRoot, item.SelectedPrintFolderRelativePath));
            string fullItemFolderWithSeparator = fullItemFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // Prevent path traversal outside root (ensures exact folder prefix containment)
            if (!fullItemFolderWithSeparator.StartsWith(canonicalRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                group.IsFolderResolved = false;
                group.ResolutionMessage = "Đường dẫn thư mục in nằm ngoài thư mục kho dữ liệu";
                hasUnresolved = true;
                manifest.Groups.Add(group);
                continue;
            }

            if (!_fileSystem.DirectoryExists(fullItemFolder))
            {
                group.IsFolderResolved = false;
                group.ResolutionMessage = "Thư mục in không tồn tại trên ổ đĩa";
                hasUnresolved = true;
                manifest.Groups.Add(group);
                continue;
            }

            // Check ReparsePoint (symlink/junction) security
            try
            {
                var dirInfo = new DirectoryInfo(fullItemFolder);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    group.IsFolderResolved = false;
                    group.ResolutionMessage = "Thư mục in là liên kết tượng trưng (ReparsePoint) không được phép truy cập";
                    hasUnresolved = true;
                    manifest.Groups.Add(group);
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Lỗi kiểm tra thuộc tính thư mục '{Folder}'", fullItemFolder);
            }

            group.IsFolderResolved = true;

            // Enumerate files directly in the selected print folder (NO recursive scanning!)
            string[] files;
            try
            {
                files = Directory.GetFiles(fullItemFolder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => AllowedExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(Path.GetFileName, comparer)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Lỗi đọc thư mục '{Folder}'", fullItemFolder);
                group.ResolutionMessage = $"Lỗi đọc thư mục: {ex.Message}";
                manifest.Groups.Add(group);
                continue;
            }

            foreach (var filePath in files)
            {
                try
                {
                    var fi = new FileInfo(filePath);
                    string fileName = fi.Name;
                    string relPath = Path.GetRelativePath(canonicalRoot, filePath);

                    // Generate deterministic opaque fileId based on orderId, rootFolderId, specification, folderRelativePath and fileName
                    string specKey = item.SpecificationRelativePath ?? item.SpecificationFolderName ?? item.Id.ToString();
                    string fileId = GenerateOpaqueFileId(order.Id, order.RootFolderId, specKey, item.SelectedPrintFolderRelativePath, fileName);
                    string itemVersion = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}:v1";

                    versionBuilder.Append($"{fileName}:{itemVersion};");

                    group.Items.Add(new OrderGalleryItem
                    {
                        FileId = fileId,
                        OrderItemId = item.Id,
                        SpecificationName = group.SpecificationName,
                        FileName = fileName,
                        FileSizeBytes = fi.Length,
                        LastModifiedUtc = fi.LastWriteTimeUtc,
                        Status = "Ready",
                        SourceRelativePath = relPath,
                        Version = itemVersion
                    });
                    totalCount++;
                }
                catch { }
            }

            manifest.Groups.Add(group);
        }

        manifest.TotalImageCount = totalCount;
        manifest.HasUnresolvedFolders = hasUnresolved;

        using (var sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(versionBuilder.ToString()));
            manifest.ManifestVersion = Convert.ToHexString(hash).ToLowerInvariant()[..16];
        }

        _manifestCache[order.Id] = manifest;
        return manifest;
    }

    public async Task<OrderGalleryPage> GetOrderGalleryPageAsync(long orderId, string manifestVersion, int offset, int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) limit = 24;
        if (limit > 100) limit = 100;
        if (offset < 0) offset = 0;

        if (!_manifestCache.TryGetValue(orderId, out var manifest) ||
            (!string.IsNullOrWhiteSpace(manifestVersion) && !string.Equals(manifest.ManifestVersion, manifestVersion, StringComparison.OrdinalIgnoreCase)))
        {
            manifest = await GetOrderManifestAsync(orderId, cancellationToken);
        }

        var allItems = manifest.Groups.SelectMany(g => g.Items).ToList();
        int totalCount = allItems.Count;
        var pagedItems = allItems.Skip(offset).Take(limit).ToList();

        var page = new OrderGalleryPage
        {
            OrderId = orderId,
            ManifestVersion = manifest.ManifestVersion,
            Offset = offset,
            Limit = limit,
            TotalCount = totalCount,
            HasMore = (offset + limit) < totalCount,
            Items = pagedItems.Select(item => new OrderGalleryItemDto
            {
                FileId = item.FileId,
                OrderItemId = item.OrderItemId,
                SpecificationName = item.SpecificationName,
                FileName = item.FileName,
                FileSizeBytes = item.FileSizeBytes,
                Status = item.Status,
                Version = item.Version,
                ThumbnailUrl = $"/api/orders/{orderId}/gallery/images/{item.FileId}?v={Uri.EscapeDataString(item.Version)}"
            }).ToList()
        };

        return page;
    }

    public async Task<ThumbnailResult> GetGalleryThumbnailAsync(long orderId, string manifestVersion, string fileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return ThumbnailResult.NotFound();
        }

        bool needRefresh = true;
        OrderGalleryItem? item = null;
        if (_manifestCache.TryGetValue(orderId, out var manifest))
        {
            item = manifest.Groups.SelectMany(g => g.Items).FirstOrDefault(i => string.Equals(i.FileId, fileId, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                // Accept if manifestVersion matches either the manifest version or the specific item version
                if (string.IsNullOrWhiteSpace(manifestVersion) ||
                    string.Equals(manifest.ManifestVersion, manifestVersion, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Version, manifestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    needRefresh = false;
                }
            }
        }

        if (needRefresh)
        {
            manifest = await GetOrderManifestAsync(orderId, cancellationToken);
            item = manifest.Groups.SelectMany(g => g.Items).FirstOrDefault(i => string.Equals(i.FileId, fileId, StringComparison.OrdinalIgnoreCase));
        }

        if (item == null || string.IsNullOrWhiteSpace(item.SourceRelativePath))
        {
            return ThumbnailResult.NotFound();
        }

        var order = await _orderRepository.GetOrderByIdAsync(orderId, cancellationToken);
        if (order == null) return ThumbnailResult.NotFound();

        string? rootPath = await ResolveRootPathAsync(order, cancellationToken);
        if (string.IsNullOrWhiteSpace(rootPath)) return ThumbnailResult.NotFound();

        string canonicalRoot = Path.GetFullPath(rootPath);
        string canonicalRootWithSeparator = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string physicalPath = Path.GetFullPath(Path.Combine(canonicalRoot, item.SourceRelativePath));

        if (!physicalPath.StartsWith(canonicalRootWithSeparator, StringComparison.OrdinalIgnoreCase) || !_fileSystem.FileExists(physicalPath))
        {
            return ThumbnailResult.NotFound();
        }

        try
        {
            var fileInfo = new FileInfo(physicalPath);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return ThumbnailResult.NotFound();
            }
        }
        catch
        {
            return ThumbnailResult.NotFound();
        }

        var result = await _thumbnailService.GetThumbnailAsync(physicalPath, cancellationToken);
        if (result.Status == ThumbnailStatus.Success)
        {
            await _thumbnailService.TouchLastUsedAsync(physicalPath, cancellationToken);
        }
        return result;
    }

    public async Task TouchViewedBatchAsync(long orderId, string manifestVersion, IEnumerable<string> fileIds, CancellationToken cancellationToken = default)
    {
        if (fileIds == null) return;
        var fileIdSet = fileIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fileIdSet.Count == 0) return;

        if (!_manifestCache.TryGetValue(orderId, out var manifest) ||
            (!string.IsNullOrWhiteSpace(manifestVersion) && !string.Equals(manifest.ManifestVersion, manifestVersion, StringComparison.OrdinalIgnoreCase)))
        {
            manifest = await GetOrderManifestAsync(orderId, cancellationToken);
        }

        var order = await _orderRepository.GetOrderByIdAsync(orderId, cancellationToken);
        if (order == null) return;

        string? rootPath = await ResolveRootPathAsync(order, cancellationToken);
        if (string.IsNullOrWhiteSpace(rootPath)) return;

        string canonicalRoot = Path.GetFullPath(rootPath);
        var matchedPaths = new List<string>();

        foreach (var item in manifest.Groups.SelectMany(g => g.Items))
        {
            if (fileIdSet.Contains(item.FileId))
            {
                string physicalPath = Path.GetFullPath(Path.Combine(canonicalRoot, item.SourceRelativePath));
                matchedPaths.Add(physicalPath);
            }
        }

        if (matchedPaths.Count > 0)
        {
            await _thumbnailService.TouchLastUsedBatchAsync(matchedPaths, cancellationToken);
        }
    }

    private static string GenerateOpaqueFileId(long orderId, long? rootFolderId, string? specKey, string? folderRelativePath, string fileName)
    {
        string normSpec = (specKey ?? "").Replace('\\', '/').Trim('/').ToLowerInvariant();
        string normFolder = (folderRelativePath ?? "").Replace('\\', '/').Trim('/').ToLowerInvariant();
        string raw = $"{orderId}:{rootFolderId ?? 0}:{normSpec}:{normFolder}:{fileName.ToLowerInvariant()}";
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static string GenerateVersionString(long orderId, string extra)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes($"{orderId}:{extra}:{DateTime.UtcNow.Ticks}"));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }
}
