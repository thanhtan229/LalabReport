using Dapper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.FileSystem;
using LalabAutoReport.Infrastructure.Services;
using Xunit;

namespace LalabAutoReport.Tests;

public class OrderGalleryServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _cacheDir;
    private readonly PhysicalFileSystemAdapter _fileSystem;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SqliteThumbnailCacheRepository _cacheRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SqliteOrderRepository _orderRepo;
    private readonly SqliteSettingsRepository _settingsRepo;
    private readonly SqliteRootFolderRepository _rootFolderRepo;
    private readonly SqliteCustomerRepository _customerRepo;
    private readonly SqliteProductRepository _productRepo;
    private readonly OrderGalleryService _galleryService;

    public OrderGalleryServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "Lalab_GalleryTest_" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_tempRoot, "ThumbnailCache");
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_cacheDir);

        _fileSystem = new PhysicalFileSystemAdapter();
        string dbPath = Path.Combine(_tempRoot, "gallery_test.db");
        _connectionFactory = new SqliteConnectionFactory(dbPath);

        var migrator = new DatabaseMigrator(_connectionFactory);
        migrator.MigrateAsync().GetAwaiter().GetResult();

        _cacheRepo = new SqliteThumbnailCacheRepository(_connectionFactory);
        _thumbnailService = new ThumbnailService(_fileSystem, _cacheDir, _cacheRepo);
        _orderRepo = new SqliteOrderRepository(_connectionFactory);
        _settingsRepo = new SqliteSettingsRepository(_connectionFactory);
        _customerRepo = new SqliteCustomerRepository(_connectionFactory);
        _productRepo = new SqliteProductRepository(_connectionFactory);
        _rootFolderRepo = new SqliteRootFolderRepository(_connectionFactory);

        _settingsRepo.SaveSettingsAsync(new AppSettings
        {
            RootFolder = _tempRoot,
            EnableThumbnailAutoCleanup = true,
            ThumbnailRetentionDays = 60
        }).GetAwaiter().GetResult();

        _galleryService = new OrderGalleryService(
            _orderRepo,
            _settingsRepo,
            _fileSystem,
            _thumbnailService,
            _rootFolderRepo);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    private string CreateTestJpeg(string relativePath, int width = 100, int height = 100)
    {
        string fullPath = Path.Combine(_tempRoot, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        using var stream = File.Create(fullPath);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(wb));
        encoder.Save(stream);
        return fullPath;
    }

    [Fact]
    public async Task GetOrderManifestAsync_ListsOnlyDirectJpgFiles_WithNaturalSort()
    {
        // Setup folder structure: 2026-10-25 / TestOrder / 13x18 / In
        string relOrderDir = Path.Combine("2026-10-25", "TestOrder");
        string relSpecDir = Path.Combine(relOrderDir, "13x18");
        string relPrintDir = Path.Combine(relSpecDir, "In");

        CreateTestJpeg(Path.Combine(relPrintDir, "DSC_10.JPG"));
        CreateTestJpeg(Path.Combine(relPrintDir, "DSC_2.jpg"));
        CreateTestJpeg(Path.Combine(relPrintDir, "DSC_1.jpg"));
        CreateTestJpeg(Path.Combine(relPrintDir, "DSC_20.jpeg"));
        // Create non-jpg and subfolder file (should NOT be included)
        string nonJpg = Path.Combine(_tempRoot, relPrintDir, "note.txt");
        await File.WriteAllTextAsync(nonJpg, "hello");
        string subDir = Path.Combine(_tempRoot, relPrintDir, "Nested");
        Directory.CreateDirectory(subDir);
        CreateTestJpeg(Path.Combine(relPrintDir, "Nested", "nested.jpg"));

        // Save order and item to DB
        var order = new Order
        {
            OrderCode = "DH-261025-001",
            WorkDate = "2026-10-25",
            OriginalFolderName = "TestOrder",
            RelativePath = relOrderDir,
            Status = OrderStatus.Ready,
            Items = new List<OrderItemScan>
            {
                new OrderItemScan
                {
                    SpecificationFolderName = "13x18",
                    SpecificationRelativePath = relSpecDir,
                    SelectedPrintFolderRelativePath = relPrintDir,
                    PrintFolderStatus = PrintFolderResolutionStatus.Resolved,
                    ScanStatus = ScanStatus.Success
                }
            }
        };

        var snapshot = new ScanSnapshot { Scope = ScanScope.Order, Status = ScanStatus.Success };
        await _orderRepo.SaveOrderAsync(order, snapshot);
        var savedOrder = await _orderRepo.GetOrderByCodeAsync("DH-261025-001");
        savedOrder.Should().NotBeNull();

        // Act
        var manifest = await _galleryService.GetOrderManifestAsync(savedOrder!.Id);

        // Assert
        manifest.Should().NotBeNull();
        manifest.OrderId.Should().Be(savedOrder.Id);
        manifest.Groups.Should().HaveCount(1);

        var group = manifest.Groups[0];
        group.IsFolderResolved.Should().BeTrue();
        group.Items.Should().HaveCount(4); // DSC_1, DSC_2, DSC_10, DSC_20

        // Natural sort order verified: 1 before 2, 2 before 10, 10 before 20
        group.Items.Select(i => i.FileName).Should().Equal(
            "DSC_1.jpg", "DSC_2.jpg", "DSC_10.JPG", "DSC_20.jpeg");

        // Opaque file IDs (not full paths)
        group.Items[0].FileId.Should().NotBeNullOrWhiteSpace();
        group.Items[0].FileId.Should().NotContain(_tempRoot);

        manifest.TotalImageCount.Should().Be(4);
        manifest.ManifestVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetOrderGalleryPageAsync_PaginatesProperly()
    {
        string relOrderDir = Path.Combine("2026-10-25", "OrderPaging");
        string relPrintDir = Path.Combine(relOrderDir, "In");

        // Create 30 test images
        for (int i = 1; i <= 30; i++)
        {
            CreateTestJpeg(Path.Combine(relPrintDir, $"photo_{i:D2}.jpg"), 20, 20);
        }

        var order = new Order
        {
            OrderCode = "DH-261025-002",
            WorkDate = "2026-10-25",
            OriginalFolderName = "OrderPaging",
            RelativePath = relOrderDir,
            Status = OrderStatus.Ready,
            Items = new List<OrderItemScan>
            {
                new OrderItemScan
                {
                    SpecificationFolderName = "In",
                    SelectedPrintFolderRelativePath = relPrintDir,
                    PrintFolderStatus = PrintFolderResolutionStatus.Resolved,
                    ScanStatus = ScanStatus.Success
                }
            }
        };

        await _orderRepo.SaveOrderAsync(order, new ScanSnapshot());
        var savedOrder = await _orderRepo.GetOrderByCodeAsync("DH-261025-002");

        // Page 1: limit 24
        var page1 = await _galleryService.GetOrderGalleryPageAsync(savedOrder!.Id, "", offset: 0, limit: 24);
        page1.TotalCount.Should().Be(30);
        page1.Items.Should().HaveCount(24);
        page1.HasMore.Should().BeTrue();
        page1.Items[0].FileName.Should().Be("photo_01.jpg");
        page1.Items[23].FileName.Should().Be("photo_24.jpg");

        // Page 2: limit 24, offset 24
        var page2 = await _galleryService.GetOrderGalleryPageAsync(savedOrder.Id, page1.ManifestVersion, offset: 24, limit: 24);
        page2.TotalCount.Should().Be(30);
        page2.Items.Should().HaveCount(6);
        page2.HasMore.Should().BeFalse();
        page2.Items[0].FileName.Should().Be("photo_25.jpg");
        page2.Items[5].FileName.Should().Be("photo_30.jpg");
    }

    [Fact]
    public async Task ThumbnailCache_UpsertTouchAndCleanup_WorksCorrectly()
    {
        string fakeSourcePath = Path.Combine(_tempRoot, "sample.jpg");
        CreateTestJpeg("sample.jpg", 100, 100);

        var thumbResult = await _thumbnailService.GetThumbnailAsync(fakeSourcePath);
        thumbResult.Status.Should().Be(ThumbnailStatus.Success);
        thumbResult.CachedFilePath.Should().NotBeNull();
        File.Exists(thumbResult.CachedFilePath).Should().BeTrue();

        // Verify DB entry was saved
        int count = await _cacheRepo.GetTotalCountAsync();
        count.Should().Be(1);

        long sizeBytes = await _cacheRepo.GetTotalDiskSizeBytesAsync();
        sizeBytes.Should().BeGreaterThan(0);

        // Touch batch
        await _thumbnailService.TouchLastUsedAsync(fakeSourcePath);

        // Test cleanup with 60 days retention: entry should NOT be expired
        var summaryNotExpired = await _thumbnailService.CleanupExpiredDiskCacheAsync(TimeSpan.FromDays(60));
        summaryNotExpired.FilesDeleted.Should().Be(0);
        File.Exists(thumbResult.CachedFilePath).Should().BeTrue();

        // Expire entry manually in DB
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE thumbnail_cache_entries SET last_used_at_utc = @OldTime",
                new { OldTime = DateTimeOffset.UtcNow.AddDays(-65).ToString("o") });
        }

        // Run cleanup with 60 days retention: entry should be cleaned up!
        var summaryExpired = await _thumbnailService.CleanupExpiredDiskCacheAsync(TimeSpan.FromDays(60));
        summaryExpired.FilesDeleted.Should().Be(1);
        File.Exists(thumbResult.CachedFilePath).Should().BeFalse();

        int countAfter = await _cacheRepo.GetTotalCountAsync();
        countAfter.Should().Be(0);
    }

    [Fact]
    public async Task GetOrderManifestAsync_WhenRootFolderOffline_DoesNotFallbackToSettingsRoot()
    {
        long rootId = await _rootFolderRepo.InsertAsync(new RootFolder
        {
            Name = "Offline Storage",
            FullPath = "Z:\\NonExistent_Offline_Root_12345",
            IsActive = true,
            IsDefault = false
        });

        var order = new Order
        {
            OrderCode = "DH-261025-OFFLINE",
            WorkDate = "2026-10-25",
            OriginalFolderName = "TestOffline",
            RootFolderId = rootId,
            Status = OrderStatus.Ready,
            Items = new List<OrderItemScan>()
        };

        await _orderRepo.SaveOrderAsync(order, new ScanSnapshot());
        var savedOrder = await _orderRepo.GetOrderByCodeAsync("DH-261025-OFFLINE");

        var manifest = await _galleryService.GetOrderManifestAsync(savedOrder!.Id);

        manifest.Should().NotBeNull();
        manifest.Notice.Should().Contain("ngoại tuyến");
        manifest.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrderManifestAsync_PathTraversal_RejectedWhenTargetingSiblingDirectory()
    {
        // Create an order attempting sibling path escape like "..\..\other"
        var order = new Order
        {
            OrderCode = "DH-261025-TRAVERSAL",
            WorkDate = "2026-10-25",
            OriginalFolderName = "TestTraversal",
            Status = OrderStatus.Ready,
            Items = new List<OrderItemScan>
            {
                new OrderItemScan
                {
                    SpecificationFolderName = "Sneaky",
                    SelectedPrintFolderRelativePath = Path.Combine("..", "OutsideFolder"),
                    PrintFolderStatus = PrintFolderResolutionStatus.Resolved,
                    ScanStatus = ScanStatus.Success
                }
            }
        };

        await _orderRepo.SaveOrderAsync(order, new ScanSnapshot());
        var savedOrder = await _orderRepo.GetOrderByCodeAsync("DH-261025-TRAVERSAL");

        var manifest = await _galleryService.GetOrderManifestAsync(savedOrder!.Id);

        manifest.Groups.Should().HaveCount(1);
        manifest.Groups[0].IsFolderResolved.Should().BeFalse();
        manifest.Groups[0].ResolutionMessage.Should().Contain("ngoài thư mục kho");
    }

    [Fact]
    public async Task GetOrderManifestAsync_FileIdIsStableAcrossRescans()
    {
        string relOrderDir = Path.Combine("2026-10-25", "OrderStableId");
        string relPrintDir = Path.Combine(relOrderDir, "In");
        CreateTestJpeg(Path.Combine(relPrintDir, "photo.jpg"));

        var order = new Order
        {
            OrderCode = "DH-261025-STABLE",
            WorkDate = "2026-10-25",
            OriginalFolderName = "OrderStableId",
            RelativePath = relOrderDir,
            Status = OrderStatus.Ready,
            Items = new List<OrderItemScan>
            {
                new OrderItemScan
                {
                    SpecificationFolderName = "In",
                    SelectedPrintFolderRelativePath = relPrintDir,
                    PrintFolderStatus = PrintFolderResolutionStatus.Resolved,
                    ScanStatus = ScanStatus.Success
                }
            }
        };

        await _orderRepo.SaveOrderAsync(order, new ScanSnapshot());
        var saved1 = await _orderRepo.GetOrderByCodeAsync("DH-261025-STABLE");
        var manifest1 = await _galleryService.GetOrderManifestAsync(saved1!.Id);
        string fileId1 = manifest1.Groups[0].Items[0].FileId;
        string version1 = manifest1.Groups[0].Items[0].Version;

        // Clear cache and request manifest again
        var manifest2 = await _galleryService.GetOrderManifestAsync(saved1.Id);
        string fileId2 = manifest2.Groups[0].Items[0].FileId;
        string version2 = manifest2.Groups[0].Items[0].Version;

        fileId1.Should().Be(fileId2);
        version1.Should().Be(version2);
        version1.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetOrderManifestAsync_WhenOrderLocked_AddsNotice()
    {
        var order = new Order
        {
            OrderCode = "DH-261025-LOCKED",
            WorkDate = "2026-10-25",
            OriginalFolderName = "OrderLocked",
            Status = OrderStatus.Locked,
            Items = new List<OrderItemScan>()
        };

        await _orderRepo.SaveOrderAsync(order, new ScanSnapshot());
        var savedOrder = await _orderRepo.GetOrderByCodeAsync("DH-261025-LOCKED");

        var manifest = await _galleryService.GetOrderManifestAsync(savedOrder!.Id);

        manifest.Notice.Should().Contain("hiển thị ảnh hiện có");
    }

    [Fact]
    public async Task ThumbnailService_LegacyDiskCacheHit_UpsertsIntoDatabase()
    {
        string fakeSourcePath = Path.Combine(_tempRoot, "legacy_source.jpg");
        CreateTestJpeg("legacy_source.jpg", 100, 100);

        // Pre-create the cached thumbnail directly on disk without DB entry
        var fi = new FileInfo(fakeSourcePath);
        string rawKey = $"{fakeSourcePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|v1";
        using var md5 = System.Security.Cryptography.MD5.Create();
        string hash = Convert.ToHexString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
        string cachedFile = Path.Combine(_cacheDir, $"{hash}.jpg");
        CreateTestJpeg("ThumbnailCache/" + $"{hash}.jpg", 80, 80);

        // Verify DB is empty for this key
        var beforeEntry = await _cacheRepo.GetEntryAsync(hash);
        beforeEntry.Should().BeNull();

        // Act
        var result = await _thumbnailService.GetThumbnailAsync(fakeSourcePath);

        // Assert
        result.Status.Should().Be(ThumbnailStatus.Success);
        result.CachedFilePath.Should().Be(cachedFile);

        // Allow background upsert task to complete
        await Task.Delay(150);

        var afterEntry = await _cacheRepo.GetEntryAsync(hash);
        afterEntry.Should().NotBeNull();
        afterEntry!.CacheKey.Should().Be(hash);
    }

    [Fact]
    public async Task GetOrderManifestAsync_WhenMultipleItemsSharePrintFolder_GeneratesUniqueFileIdsAcrossGroups()
    {
        CreateTestJpeg("OrderMulti/CommonPrint/photo.jpg", 100, 100);

        var order = new Order
        {
            OrderCode = "DH-261025-UNIQUE",
            WorkDate = "2026-10-25",
            OriginalFolderName = "OrderMulti",
            RelativePath = "OrderMulti",
            Items = new List<OrderItemScan>
            {
                new OrderItemScan
                {
                    SpecificationFolderName = "QuyCach_A",
                    SpecificationRelativePath = "OrderMulti/QuyCach_A",
                    SelectedPrintFolderRelativePath = "OrderMulti/CommonPrint"
                },
                new OrderItemScan
                {
                    SpecificationFolderName = "QuyCach_B",
                    SpecificationRelativePath = "OrderMulti/QuyCach_B",
                    SelectedPrintFolderRelativePath = "OrderMulti/CommonPrint"
                }
            }
        };

        await _orderRepo.SaveOrderAsync(order, new ScanSnapshot());
        var savedOrder = await _orderRepo.GetOrderByCodeAsync("DH-261025-UNIQUE");

        var manifest = await _galleryService.GetOrderManifestAsync(savedOrder!.Id);

        manifest.Groups.Should().HaveCount(2);
        manifest.Groups[0].Items.Should().HaveCount(1);
        manifest.Groups[1].Items.Should().HaveCount(1);

        string fileId1 = manifest.Groups[0].Items[0].FileId;
        string fileId2 = manifest.Groups[1].Items[0].FileId;

        fileId1.Should().NotBeNullOrWhiteSpace();
        fileId2.Should().NotBeNullOrWhiteSpace();
        fileId1.Should().NotBe(fileId2);
    }

    [Fact]
    public async Task TouchViewedBatchAsync_UpdatesLastUsedAtInDatabase()
    {
        CreateTestJpeg("OrderTouch/Print/img01.jpg", 100, 100);

        var order = new Order
        {
            OrderCode = "DH-261025-TOUCH",
            WorkDate = "2026-10-25",
            OriginalFolderName = "OrderTouch",
            RelativePath = "OrderTouch",
            Items = new List<OrderItemScan>
            {
                new OrderItemScan
                {
                    SpecificationFolderName = "13x18",
                    SpecificationRelativePath = "OrderTouch/13x18",
                    SelectedPrintFolderRelativePath = "OrderTouch/Print"
                }
            }
        };

        await _orderRepo.SaveOrderAsync(order, new ScanSnapshot());
        var savedOrder = await _orderRepo.GetOrderByCodeAsync("DH-261025-TOUCH");

        var manifest = await _galleryService.GetOrderManifestAsync(savedOrder!.Id);
        string fileId = manifest.Groups[0].Items[0].FileId;

        // Generate thumbnail first
        var thumbResult = await _galleryService.GetGalleryThumbnailAsync(savedOrder.Id, manifest.ManifestVersion, fileId);
        thumbResult.Status.Should().Be(ThumbnailStatus.Success);

        // Update DB entry to an older last_used timestamp
        using var conn = _connectionFactory.CreateConnection();
        var oldTimeIso = DateTimeOffset.UtcNow.AddDays(-5).ToString("o");
        await conn.ExecuteAsync("UPDATE thumbnail_cache_entries SET last_used_at_utc = @OldTime", new { OldTime = oldTimeIso });

        var beforeEntry = await conn.QueryFirstOrDefaultAsync<ThumbnailCacheEntry>("SELECT last_used_at_utc AS LastUsedAtUtc FROM thumbnail_cache_entries LIMIT 1");
        beforeEntry.Should().NotBeNull();
        beforeEntry!.LastUsedAtUtc.Should().Be(oldTimeIso);

        // Act: Touch viewed batch
        await _galleryService.TouchViewedBatchAsync(savedOrder.Id, manifest.ManifestVersion, new[] { fileId });

        // Assert: last_used_at_utc is refreshed to near now
        var afterEntry = await conn.QueryFirstOrDefaultAsync<ThumbnailCacheEntry>("SELECT last_used_at_utc AS LastUsedAtUtc FROM thumbnail_cache_entries LIMIT 1");
        afterEntry.Should().NotBeNull();
        string afterTimeIso = afterEntry!.LastUsedAtUtc;
        DateTimeOffset.Parse(afterTimeIso).Should().BeAfter(DateTimeOffset.Parse(oldTimeIso));
    }

    [Fact]
    public async Task GetGalleryThumbnailAsync_WhenPassedItemVersion_UsesCachedManifestWithoutRebuilding()
    {
        CreateTestJpeg("OrderVersionHit/Print/img01.jpg", 100, 100);

        var order = new Order
        {
            OrderCode = "DH-261025-VER",
            WorkDate = "2026-10-25",
            OriginalFolderName = "OrderVersionHit",
            RelativePath = "OrderVersionHit",
            Items = new List<OrderItemScan>
            {
                new OrderItemScan
                {
                    SpecificationFolderName = "13x18",
                    SpecificationRelativePath = "OrderVersionHit/13x18",
                    SelectedPrintFolderRelativePath = "OrderVersionHit/Print"
                }
            }
        };

        await _orderRepo.SaveOrderAsync(order, new ScanSnapshot());
        var savedOrder = await _orderRepo.GetOrderByCodeAsync("DH-261025-VER");

        var manifest = await _galleryService.GetOrderManifestAsync(savedOrder!.Id);
        var item = manifest.Groups[0].Items[0];

        // Calling with item.Version (from ThumbnailUrl query parameter)
        var result = await _galleryService.GetGalleryThumbnailAsync(savedOrder.Id, item.Version, item.FileId);
        result.Status.Should().Be(ThumbnailStatus.Success);
    }
}
