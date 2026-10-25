using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Services;

public class CloudSyncService : ICloudSyncService, IDisposable
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly ICustomerBillRepository _billRepository;
    private readonly IReportService _reportService;
    private readonly ICloudMediaSyncService? _cloudMediaSyncService;
    private readonly IShippingLabelExporter? _shippingLabelExporter;
    private readonly IOrderGalleryService? _orderGalleryService;
    private readonly IThumbnailService? _thumbnailService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CloudSyncService>? _logger;
    private readonly Timer? _periodicPullTimer;

    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly SemaphoreSlim _galleryGate = new(1, 1);
    private readonly ICloudSyncStateRepository? _syncState;
    private readonly SqliteRemotePrintLedger? _printLedger;
    private bool _isSyncing;
    private DateTimeOffset? _lastSyncTime;
    private string? _lastSyncStatus;
    private string? _remotePrintWarning;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public bool IsSyncing => _isSyncing;
    public DateTimeOffset? LastSyncTime => _lastSyncTime;
    public string? LastSyncStatus => _lastSyncStatus;

    public event EventHandler? SyncStatusChanged;

    public CloudSyncService(
        ISettingsRepository settingsRepository,
        IOrderRepository orderRepository,
        ICustomerBillRepository billRepository,
        IReportService reportService,
        HttpClient? httpClient = null,
        ICloudMediaSyncService? cloudMediaSyncService = null,
        IShippingLabelExporter? shippingLabelExporter = null,
        ILogger<CloudSyncService>? logger = null,
        ICloudSyncStateRepository? syncState = null,
        bool enablePeriodicSync = true,
        IOrderGalleryService? orderGalleryService = null,
        IThumbnailService? thumbnailService = null)
    {
        _settingsRepository = settingsRepository;
        _orderRepository = orderRepository;
        _billRepository = billRepository;
        _reportService = reportService;
        _cloudMediaSyncService = cloudMediaSyncService;
        _shippingLabelExporter = shippingLabelExporter;
        _orderGalleryService = orderGalleryService;
        _thumbnailService = thumbnailService;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _logger = logger;
        _syncState = syncState ?? (settingsRepository is SqliteSettingsRepository sqlite ? new SqliteCloudSyncStateRepository(sqlite.ConnectionFactory) : null);

        if (settingsRepository is SqliteSettingsRepository printSqlite)
            _printLedger = new SqliteRemotePrintLedger(printSqlite.ConnectionFactory);

        // Periodic background pull, heartbeat and remote print queue (every 15s after 10s initial delay)
        if (enablePeriodicSync) _periodicPullTimer = new Timer(async _ =>
        {
            if (!await _syncGate.WaitAsync(0)) return;
            try
            {
                using var lifecycle = await EnterDatabaseLifecycleAsync(CancellationToken.None);
                if (!(await _settingsRepository.GetSettingsAsync()).EnableCloudSync) return;
                SetSyncing(true, "Đang kiểm tra các thay đổi Cloud...");
                await SendHeartbeatCoreAsync();
                await PullRemoteChangesCoreAsync();
                await ProcessPendingPrintCommandsCoreAsync();
                var pending = _syncState == null ? 0 : (await _syncState.GetPendingAsync()).Count;
                if (pending > 0) await SyncAllCoreAsync();
                else SetSyncing(false, "Đã xác nhận với Cloud; không có thao tác chờ gửi.");
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Periodic Cloud sync failed"); SetSyncing(false, "Đồng bộ chưa hoàn tất: " + ex.Message); }
            finally
            {
                _syncGate.Release();
                // Process gallery jobs asynchronously outside _syncGate so heartbeat and remote print aren't blocked
                _ = ProcessPendingGalleryJobsAsync(CancellationToken.None);
                _ = CheckAutoThumbnailCleanupAsync(CancellationToken.None);
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
    }

    public void Dispose()
    {
        _periodicPullTimer?.Dispose();

        _galleryGate.Dispose();
    }

    public Task<CloudSyncResult> SyncAllAsync(CancellationToken cancellationToken = default) => WithGateAsync(() => SyncAllCoreAsync(cancellationToken), cancellationToken);
    public Task<CloudSyncResult> SyncDateAsync(string date, CancellationToken cancellationToken = default) => WithGateAsync(() => SyncDateCoreAsync(date, cancellationToken), cancellationToken);
    public Task<CloudSyncResult> SyncBillAsync(long billId, CancellationToken cancellationToken = default) => WithGateAsync(() => SyncBillCoreAsync(billId, cancellationToken), cancellationToken);
    public Task<CloudSyncResult> UploadBillJpegAsync(long billId, CancellationToken cancellationToken = default) => WithGateAsync(() => UploadBillJpegCoreAsync(billId, cancellationToken), cancellationToken);
    public Task<int> PullRemoteChangesAsync(CancellationToken cancellationToken = default) => WithGateAsync(() => PullRemoteChangesCoreAsync(cancellationToken), cancellationToken);
    public Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default) => WithGateAsync(() => SendHeartbeatCoreAsync(cancellationToken), cancellationToken);
    public Task<int> ProcessPendingPrintCommandsAsync(CancellationToken cancellationToken = default) => WithGateAsync(() => ProcessPendingPrintCommandsCoreAsync(cancellationToken), cancellationToken);
    public async Task<int> ProcessPendingGalleryJobsAsync(CancellationToken cancellationToken = default)
    {
        if (_orderGalleryService == null) return 0;
        if (!await _galleryGate.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            using var lifecycle = await EnterDatabaseLifecycleAsync(cancellationToken);
            return await ProcessPendingGalleryJobsCoreAsync(cancellationToken);
        }
        finally
        {
            _galleryGate.Release();
        }
    }

    public async Task<bool> CleanupCloudGalleryCacheAsync(int retentionDays, bool clearAll = false, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!settings.EnableCloudSync) return false;
        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        string secret = settings.CloudSyncSecret ?? "";
        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(secret)) return false;

        try
        {
            var payload = clearAll ? new { clearAll = true } : (object)new { retentionDays };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/cleanup")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Sync-Secret", secret);
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lỗi gọi dọn dẹp thumbnail cache trên Cloud");
            return false;
        }
    }

    private async Task CheckAutoThumbnailCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
            if (!settings.EnableThumbnailAutoCleanup) return;

            bool shouldSweep = true;
            if (!string.IsNullOrWhiteSpace(settings.LastThumbnailCleanupAt) &&
                DateTimeOffset.TryParse(settings.LastThumbnailCleanupAt, out var lastSweep))
            {
                if (DateTimeOffset.UtcNow - lastSweep < TimeSpan.FromHours(24))
                {
                    shouldSweep = false;
                }
            }

            if (shouldSweep)
            {
                int days = (settings.ThumbnailRetentionDays == 30 || settings.ThumbnailRetentionDays == 90) ? settings.ThumbnailRetentionDays : 60;
                if (_thumbnailService != null)
                {
                    await _thumbnailService.CleanupExpiredDiskCacheAsync(TimeSpan.FromDays(days), cancellationToken);
                }
                if (settings.EnableCloudSync)
                {
                    await CleanupCloudGalleryCacheAsync(days, clearAll: false, cancellationToken);
                }
                settings.LastThumbnailCleanupAt = DateTimeOffset.UtcNow.ToString("o");
                await _settingsRepository.SaveSettingsAsync(settings, cancellationToken);
                _logger?.LogInformation("Periodic 24h thumbnail cleanup sweep completed (retention {Days} days).", days);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lỗi kiểm tra chu kỳ 24h dọn thumbnail cache");
        }
    }

    private async Task<T> WithGateAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _syncGate.WaitAsync(ct);
        try { using var lifecycle = await EnterDatabaseLifecycleAsync(ct); return await action(); }
        catch (Exception ex) { SetSyncing(false, "Đồng bộ chưa hoàn tất: " + ex.Message); throw; }
        finally { _syncGate.Release(); }
    }

    private async Task<IDisposable?> EnterDatabaseLifecycleAsync(CancellationToken ct) =>
        _settingsRepository is SqliteSettingsRepository sqlite
            ? await DatabaseLifecycleGuard.EnterAsync(sqlite.ConnectionFactory.DatabasePath, ct) : null;

    private async Task<CloudSyncResult> SyncAllCoreAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!settings.EnableCloudSync)
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = "Tính năng đồng bộ đám mây đang tắt trong phần Cài đặt."
            };
        }

        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        string secret = settings.CloudSyncSecret ?? "";

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return new CloudSyncResult
            {
                Success = false,
                Message = "Chưa cấu hình URL Cloudflare Worker (CloudSyncApiUrl)."
            };
        }

        SetSyncing(true, "Đang chuẩn bị dữ liệu đồng bộ...");

        try
        {
            await PullRemoteChangesCoreAsync(cancellationToken, settings);
            var now = DateTime.Today;
            string todayStr = now.ToString("yyyy-MM-dd");
            string yesterdayStr = now.AddDays(-1).ToString("yyyy-MM-dd");

            string sixtyDaysAgoStr = now.AddDays(-60).ToString("yyyy-MM-dd");

            // 1. Collect Orders (Last 60 days covering current and previous month)
            var recentOrders = await _orderRepository.GetOrdersByDateRangeAsync(sixtyDaysAgoStr, todayStr, cancellationToken);
            var ordersToday = await _orderRepository.GetOrdersByDateAsync(todayStr, cancellationToken);
            var ordersYesterday = await _orderRepository.GetOrdersByDateAsync(yesterdayStr, cancellationToken);
            var allOrders = recentOrders.Concat(ordersToday).Concat(ordersYesterday).DistinctBy(o => o.Id).ToList();

            // 2. Collect Bills (Unpaid bills + recent bills in last 60 days)
            var unpaidBills = await _billRepository.GetUnpaidBillsAsync();
            var recentBills = await _billRepository.GetBillsByDateRangeAsync(sixtyDaysAgoStr, todayStr);
            var allBills = unpaidBills.Concat(recentBills).DistinctBy(b => b.Id).ToList();

            // 3. Collect Reports
            var monthlyReport = await _reportService.GetMonthlyReportAsync(now.Year, now.Month);
            var prevMonth = now.AddMonths(-1);
            var prevMonthlyReport = await _reportService.GetMonthlyReportAsync(prevMonth.Year, prevMonth.Month);

            // Group unpaid for report
            var debtsGrouped = unpaidBills
                .GroupBy(b => new { Id = b.CustomerId ?? 0, Name = b.CustomerNameSnapshot })
                .Select(g => new
                {
                    customerId = g.Key.Id,
                    customerName = g.Key.Name,
                    unpaidBillCount = g.Count(),
                    totalDebt = g.Sum(b => b.GrandTotal),
                    bills = g.Select(b => new
                    {
                        id = b.Id,
                        billNumber = b.BillNumber,
                        date = b.PeriodEnd,
                        grandTotal = b.GrandTotal
                    })
                })
                .OrderByDescending(c => c.totalDebt)
                .ToList();

            // 4. Media Sync (Thumbnails for Orders)
            Dictionary<long, OrderThumbnailInfo> thumbInfos = new();
            int orderThumbnailsUploaded = 0;
            if (_cloudMediaSyncService != null)
            {
                SetSyncing(true, "Đang xử lý và tải ảnh thumbnail lên R2...");
                try
                {
                    thumbInfos = await _cloudMediaSyncService.EnsureThumbnailsUploadedForOrdersAsync(allOrders, apiUrl, secret, cancellationToken);
                    orderThumbnailsUploaded = thumbInfos.Values.Count(t => t.Uploaded);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Lỗi tải ảnh thumbnail khi đồng bộ");
                }
            }

            // 5. Construct Payload
            var payload = new
            {
                orders = allOrders.Select(o => MapOrderToCloudDto(o, thumbInfos.GetValueOrDefault(o.Id))),
                bills = allBills.Select(MapBillToCloudDto),
                reports = new[]
                {
                    new
                    {
                        key = $"month_{now.Year}_{now.Month:D2}",
                        dataJson = JsonSerializer.Serialize(monthlyReport, JsonOptions)
                    },
                    new
                    {
                        key = $"month_{prevMonth.Year}_{prevMonth.Month:D2}",
                        dataJson = JsonSerializer.Serialize(prevMonthlyReport, JsonOptions)
                    },
                    new
                    {
                        key = "unpaid_customers",
                        dataJson = JsonSerializer.Serialize(debtsGrouped, JsonOptions)
                    }
                }
            };

            // 6. Send POST /api/sync/batch
            SetSyncing(true, "Đang gửi dữ liệu lên Cloudflare D1...");
            var jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/batch")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Sync-Secret", secret);

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                string errContent = await resp.Content.ReadAsStringAsync(cancellationToken);
                string msg = $"Lỗi đồng bộ Cloudflare D1: HTTP {(int)resp.StatusCode} - {errContent}";
                _logger?.LogWarning(msg);
                SetSyncing(false, msg);
                return new CloudSyncResult { Success = false, Message = msg, Error = errContent };
            }

            // 7. Upload JPEGs for bills that have exported images
            await DrainOperationsAsync(settings, cancellationToken);
            await PullRemoteChangesCoreAsync(cancellationToken, settings);
            int mediaCount = orderThumbnailsUploaded;
            foreach (var bill in allBills)
            {
                if (!string.IsNullOrWhiteSpace(bill.ExportFilePath) && File.Exists(bill.ExportFilePath))
                {
                    bool uploaded = await UploadJpegInternalAsync(apiUrl, secret, bill.BillNumber, bill.ExportFilePath, cancellationToken);
                    if (uploaded) mediaCount++;
                }
            }

            // 7. Update LastCloudSyncAt
            string syncTimeStr = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await RequireSyncState().SetLastSyncAtAsync(syncTimeStr, cancellationToken);

            _lastSyncTime = DateTimeOffset.Now;
            string successMsg = $"Đã đồng bộ thành công ({allOrders.Count} đơn, {allBills.Count} bill, {mediaCount} ảnh bill).";
            SetSyncing(false, successMsg);

            return new CloudSyncResult
            {
                Success = true,
                Message = successMsg,
                OrdersCount = allOrders.Count,
                BillsCount = allBills.Count,
                ReportsCount = 3,
                MediaCount = mediaCount
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Lỗi xảy ra trong quá trình đồng bộ Cloudflare");
            string errMsg = $"Lỗi đồng bộ: {ex.Message}";
            SetSyncing(false, errMsg);
            return new CloudSyncResult { Success = false, Message = errMsg, Error = ex.ToString() };
        }
    }

    private async Task<CloudSyncResult> SyncDateCoreAsync(string date, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!settings.EnableCloudSync) return new CloudSyncResult { Success = false, Message = "Cloud sync disabled" };

        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        string secret = settings.CloudSyncSecret ?? "";
        if (string.IsNullOrWhiteSpace(apiUrl)) return new CloudSyncResult { Success = false, Message = "Missing CloudSyncApiUrl" };

        SetSyncing(true, $"Đang đồng bộ ngày {date}...");
        try
        {
            await PullRemoteChangesCoreAsync(cancellationToken, settings);
            var orders = await _orderRepository.GetOrdersByDateAsync(date);
            var bills = await _billRepository.GetBillsByDateRangeAsync(date, date);

            var parsedDate = DateTime.TryParse(date, out var parsed) ? parsed : DateTime.Today;
            var report = await _reportService.GetMonthlyReportAsync(parsedDate.Year, parsedDate.Month);

            Dictionary<long, OrderThumbnailInfo> thumbInfos = new();
            int orderThumbnailsUploaded = 0;
            if (_cloudMediaSyncService != null)
            {
                try
                {
                    thumbInfos = await _cloudMediaSyncService.EnsureThumbnailsUploadedForOrdersAsync(orders, apiUrl, secret, cancellationToken);
                    orderThumbnailsUploaded = thumbInfos.Values.Count(t => t.Uploaded);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Lỗi tải ảnh thumbnail khi đồng bộ ngày {Date}", date);
                }
            }

            var payload = new
            {
                orders = orders.Select(o => MapOrderToCloudDto(o, thumbInfos.GetValueOrDefault(o.Id))),
                bills = bills.Select(MapBillToCloudDto),
                reports = new[]
                {
                    new
                    {
                        key = $"month_{parsedDate.Year}_{parsedDate.Month:D2}",
                        dataJson = JsonSerializer.Serialize(report, JsonOptions)
                    }
                }
            };

            var jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/batch")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Sync-Secret", secret);

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                string errContent = await resp.Content.ReadAsStringAsync(cancellationToken);
                SetSyncing(false, $"Lỗi sync ngày {date}: HTTP {(int)resp.StatusCode}");
                return new CloudSyncResult { Success = false, Message = $"HTTP {(int)resp.StatusCode}", Error = errContent };
            }

            await DrainOperationsAsync(settings, cancellationToken);
            await PullRemoteChangesCoreAsync(cancellationToken, settings);
            int mediaCount = orderThumbnailsUploaded;
            foreach (var bill in bills)
            {
                if (!string.IsNullOrWhiteSpace(bill.ExportFilePath) && File.Exists(bill.ExportFilePath))
                {
                    if (await UploadJpegInternalAsync(apiUrl, secret, bill.BillNumber, bill.ExportFilePath, cancellationToken))
                    {
                        mediaCount++;
                    }
                }
            }

            SetSyncing(false, $"Đã đồng bộ ngày {date} ({orders.Count} đơn, {bills.Count} bill).");
            return new CloudSyncResult
            {
                Success = true,
                Message = "Thành công",
                OrdersCount = orders.Count,
                BillsCount = bills.Count,
                ReportsCount = 1,
                MediaCount = mediaCount
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Lỗi đồng bộ ngày {Date}", date);
            SetSyncing(false, $"Lỗi: {ex.Message}");
            return new CloudSyncResult { Success = false, Message = ex.Message, Error = ex.ToString() };
        }
    }

    private async Task<CloudSyncResult> SyncBillCoreAsync(long billId, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!settings.EnableCloudSync) return new CloudSyncResult { Success = false, Message = "Cloud sync disabled" };

        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        string secret = settings.CloudSyncSecret ?? "";
        if (string.IsNullOrWhiteSpace(apiUrl)) return new CloudSyncResult { Success = false, Message = "Missing CloudSyncApiUrl" };

        var bill = await _billRepository.GetByIdAsync(billId);
        if (bill == null) return new CloudSyncResult { Success = false, Message = "Bill not found" };

        SetSyncing(true, $"Đang đồng bộ hóa đơn {bill.BillNumber}...");
        try
        {
            await PullRemoteChangesCoreAsync(cancellationToken, settings);
            bill = await _billRepository.GetByIdAsync(billId) ?? throw new InvalidOperationException("Bill no longer exists.");
            var payload = new
            {
                bills = new[] { MapBillToCloudDto(bill) }
            };

            var jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/batch")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Sync-Secret", secret);

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                string errContent = await resp.Content.ReadAsStringAsync(cancellationToken);
                SetSyncing(false, $"Lỗi sync bill {bill.BillNumber}: HTTP {(int)resp.StatusCode}");
                return new CloudSyncResult { Success = false, Message = $"HTTP {(int)resp.StatusCode}", Error = errContent };
            }

            await DrainOperationsAsync(settings, cancellationToken);
            await PullRemoteChangesCoreAsync(cancellationToken, settings);
            int mediaCount = 0;
            if (!string.IsNullOrWhiteSpace(bill.ExportFilePath) && File.Exists(bill.ExportFilePath))
            {
                if (await UploadJpegInternalAsync(apiUrl, secret, bill.BillNumber, bill.ExportFilePath, cancellationToken))
                {
                    mediaCount++;
                }
            }

            SetSyncing(false, $"Đã đồng bộ hóa đơn {bill.BillNumber}.");
            return new CloudSyncResult
            {
                Success = true,
                Message = "Thành công",
                BillsCount = 1,
                MediaCount = mediaCount
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Lỗi đồng bộ bill {BillId}", billId);
            SetSyncing(false, $"Lỗi: {ex.Message}");
            return new CloudSyncResult { Success = false, Message = ex.Message, Error = ex.ToString() };
        }
    }

    private async Task<CloudSyncResult> UploadBillJpegCoreAsync(long billId, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        string secret = settings.CloudSyncSecret ?? "";
        if (string.IsNullOrWhiteSpace(apiUrl)) return new CloudSyncResult { Success = false, Message = "Missing CloudSyncApiUrl" };

        var bill = await _billRepository.GetByIdAsync(billId);
        if (bill == null) return new CloudSyncResult { Success = false, Message = "Bill not found" };

        if (string.IsNullOrWhiteSpace(bill.ExportFilePath) || !File.Exists(bill.ExportFilePath))
        {
            return new CloudSyncResult { Success = false, Message = "Chưa xuất file ảnh hóa đơn trên máy tính." };
        }

        bool uploaded = await UploadJpegInternalAsync(apiUrl, secret, bill.BillNumber, bill.ExportFilePath, cancellationToken);
        return new CloudSyncResult
        {
            Success = uploaded,
            Message = uploaded ? "Đã tải ảnh hóa đơn lên Cloudflare R2" : "Tải ảnh hóa đơn thất bại",
            MediaCount = uploaded ? 1 : 0
        };
    }

    private async Task<bool> UploadJpegInternalAsync(string apiUrl, string secret, string billNumber, string filePath, CancellationToken ct)
    {
        try
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(filePath, ct);
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{apiUrl}/api/sync/media/bills/{billNumber}.jpg")
            {
                Content = new ByteArrayContent(fileBytes)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            req.Headers.Add("X-Sync-Secret", secret);

            var resp = await _httpClient.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to upload bill JPEG {BillNumber} to R2", billNumber);
            return false;
        }
    }

    private ICloudSyncStateRepository RequireSyncState() => _syncState ?? throw new InvalidOperationException("Durable Cloud sync repository is required.");

    public Task ExportReconciliationReportAsync(string path, CancellationToken cancellationToken = default) => WithGateAsync(async () =>
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string endpoint = NormalizeApiUrl(settings.CloudSyncApiUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/api/sync/v2/reconciliation");
        request.Headers.Add("X-Sync-Secret", settings.CloudSyncSecret);
        using var response = await _httpClient.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
        string report = await RequireSyncState().CreateReconciliationReportAsync(endpoint, await response.Content.ReadAsStringAsync(cancellationToken), cancellationToken);
        await File.WriteAllTextAsync(path, report, cancellationToken);
        SetSyncing(false, "Đã xuất báo cáo chỉ đọc. Chọn resolution từng field trước khi áp dụng."); return true;
    }, cancellationToken);

    public Task ApplyReviewedReconciliationAsync(string path, CancellationToken cancellationToken = default) => WithGateAsync(async () =>
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        using var report = JsonDocument.Parse(json);
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string endpoint = NormalizeApiUrl(settings.CloudSyncApiUrl);
        if (endpoint != report.RootElement.GetProperty("endpoint").GetString()) throw new InvalidOperationException("Endpoint không khớp báo cáo đã duyệt.");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/api/sync/v2/reconciliation");
        request.Headers.Add("X-Sync-Secret", settings.CloudSyncSecret);
        using var response = await _httpClient.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
        using var live = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (live.RootElement.GetProperty("streamId").GetString() != report.RootElement.GetProperty("streamId").GetString()
            || live.RootElement.GetProperty("cursor").GetInt64() < report.RootElement.GetProperty("cursor").GetInt64()) throw new InvalidOperationException("Cloud stream đã đổi; xuất lại báo cáo.");
        await RequireSyncState().CompleteReconciliationAsync(json, cancellationToken);
        SetSyncing(false, "Đã áp dụng lựa chọn đối soát. Thao tác Desktop được chọn đang pending; bấm Đồng bộ ngay."); return true;
    }, cancellationToken);

    private async Task<int> PullRemoteChangesCoreAsync(CancellationToken cancellationToken = default, AppSettings? frozenSettings = null)
    {
        var settings = frozenSettings ?? await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!settings.EnableCloudSync) return 0;
        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(settings.CloudSyncSecret))
            throw new InvalidOperationException("Cloud URL/secret chưa được cấu hình.");
        var repository = RequireSyncState();
        var state = await repository.GetCheckpointAsync(cancellationToken);
        if (state.RequiresReconciliation) throw new InvalidOperationException("Cần đối soát cursor Cloud cũ. Xuất báo cáo đối soát trong Cài đặt.");
        int count = 0;
        long cursor = state.Cursor;
        for (int pageNumber = 0; pageNumber < 10000; pageNumber++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}/api/sync/v2/pull?cursor={cursor}");
            request.Headers.Add("X-Sync-Secret", settings.CloudSyncSecret);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode(); // A failed pull blocks projection push.
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var page = doc.RootElement;
            if (page.GetProperty("protocolVersion").GetInt32() != 2 || !page.GetProperty("success").GetBoolean())
                throw new InvalidOperationException("Cloud không hỗ trợ protocol V2; chặn push.");
            await repository.BindStreamAsync(page.GetProperty("streamId").GetString()!, apiUrl, cancellationToken);
            var events = page.GetProperty("events").EnumerateArray().Select(ReadEvent).ToList();
            long previous = cursor;
            foreach (var ev in events)
            {
                if (ev.Id <= previous) throw new InvalidOperationException("Cloud page event IDs are not strictly increasing.");
                previous = ev.Id;
            }
            long next = page.GetProperty("nextCursor").GetInt64();
            bool more = page.GetProperty("hasMore").GetBoolean();
            if (events.Count > 500 || next != previous || (more && events.Count == 0))
                throw new InvalidOperationException("Cloud page cursor exceeds the received events.");
            foreach (var ev in events) if (await repository.ApplyEventAsync(ev, true, cancellationToken)) count++;
            cursor = next;
            if (!more) return count;
        }
        throw new InvalidOperationException("Cloud pagination limit exceeded; retry from committed cursor.");
    }

    private static CloudOperationalEvent ReadEvent(JsonElement ev) => new(ev.GetProperty("id").GetInt64(),
        ev.GetProperty("entityType").GetString()!, ev.GetProperty("entityId").GetInt64(),
        ev.GetProperty("payloadJson").GetString()!, ev.GetProperty("createdAt").GetString()!,
        ev.TryGetProperty("operationId", out var op) && op.ValueKind == JsonValueKind.String ? op.GetString() : null);

    private async Task DrainOperationsAsync(AppSettings settings, CancellationToken ct)
    {
        var repository = RequireSyncState();
        var pending = await repository.GetPendingAsync(ct);
        SetSyncing(true, $"Đang gửi {pending.Count} thao tác pending để server xác nhận...");
        foreach (var operation in pending)
        {
            using var value = JsonDocument.Parse(operation.ValueJson);
            using var request = new HttpRequestMessage(HttpMethod.Post, NormalizeApiUrl(settings.CloudSyncApiUrl) + "/api/sync/v2/operations")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { operation.OperationId, operation.EntityType, operation.EntityId,
                    value = value.RootElement, operation.DeliveredBy }, JsonOptions), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Sync-Secret", settings.CloudSyncSecret);
            using var response = await _httpClient.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.GetProperty("protocolVersion").GetInt32() != 2 || !doc.RootElement.GetProperty("success").GetBoolean())
                throw new InvalidOperationException("Invalid Cloud mutation acknowledgement.");
            var ev = ReadEvent(doc.RootElement.GetProperty("event"));
            if (ev.OperationId != operation.OperationId || ev.EntityType != operation.EntityType || ev.EntityId != operation.EntityId)
                throw new InvalidOperationException("Cloud acknowledgement identity mismatch.");
            await repository.ApplyEventAsync(ev, false, ct);
        }
        int remaining = (await repository.GetPendingAsync(ct)).Count;
        if (remaining != 0) throw new InvalidOperationException($"Còn {remaining} thao tác pending; cần đồng bộ tiếp.");
    }

    private async Task<bool> SendHeartbeatCoreAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        try
        {
            settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        }
        catch
        {
            return false;
        }

        if (!settings.EnableCloudSync) return false;

        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        string secret = settings.CloudSyncSecret ?? "";
        if (string.IsNullOrWhiteSpace(apiUrl)) return false;

        try
        {
            string localIp = NetworkAddressHelper.GetLocalIpAddress();
            string lanUrl = $"http://{localIp}:{settings.MobileServerPort}";
            string deviceId = Environment.MachineName;

            var payload = new
            {
                deviceId,
                lanUrl,
                appVersion = "1.0.0"
            };

            var jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/heartbeat")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Sync-Secret", secret);

            var resp = await _httpClient.SendAsync(req, cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Lỗi gửi heartbeat tới Cloud");
            return false;
        }
    }

    private async Task<int> ProcessPendingGalleryJobsCoreAsync(CancellationToken cancellationToken = default)
    {
        if (_orderGalleryService == null) return 0;
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!settings.EnableCloudSync) return 0;
        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        string secret = settings.CloudSyncSecret ?? "";
        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(secret)) return 0;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}/api/sync/gallery/jobs");
            req.Headers.Add("X-Sync-Secret", secret);
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode) return 0;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array) return 0;

            int processed = 0;
            foreach (var job in jobs.EnumerateArray())
            {
                string id = job.GetProperty("id").GetString()!;
                long orderId = job.GetProperty("orderId").GetInt64();
                string manifestVersion = job.GetProperty("manifestVersion").GetString() ?? "";
                string claimToken = Guid.NewGuid().ToString("N");

                using var claimReq = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/jobs/{id}/claim")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { claimToken }, JsonOptions), Encoding.UTF8, "application/json")
                };
                claimReq.Headers.Add("X-Sync-Secret", secret);
                using var claimResp = await _httpClient.SendAsync(claimReq, cancellationToken);
                if (claimResp.StatusCode == System.Net.HttpStatusCode.Conflict || !claimResp.IsSuccessStatusCode)
                    continue;

                try
                {
                    if (string.IsNullOrWhiteSpace(manifestVersion))
                    {
                        var manifest = await _orderGalleryService.GetOrderManifestAsync(orderId, cancellationToken);
                        if (manifest != null)
                        {
                            using var postManifest = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/manifests")
                            {
                                Content = new StringContent(JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, "application/json")
                            };
                            postManifest.Headers.Add("X-Sync-Secret", secret);
                            using var postResp = await _httpClient.SendAsync(postManifest, cancellationToken);
                            postResp.EnsureSuccessStatusCode();

                            using var readyReq = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/jobs/{id}/status")
                            {
                                Content = new StringContent(JsonSerializer.Serialize(new { claimToken, status = "READY" }, JsonOptions), Encoding.UTF8, "application/json")
                            };
                            readyReq.Headers.Add("X-Sync-Secret", secret);
                            await _httpClient.SendAsync(readyReq, cancellationToken);
                            processed++;
                        }
                        else
                        {
                            using var failReq = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/jobs/{id}/status")
                            {
                                Content = new StringContent(JsonSerializer.Serialize(new { claimToken, status = "FAILED", errorMessage = "Không tìm thấy thư mục hoặc ảnh cho đơn hàng này." }, JsonOptions), Encoding.UTF8, "application/json")
                            };
                            failReq.Headers.Add("X-Sync-Secret", secret);
                            await _httpClient.SendAsync(failReq, cancellationToken);
                        }
                    }
                    else if (job.TryGetProperty("fileIds", out var fileIdsProp) && fileIdsProp.ValueKind == JsonValueKind.Array)
                    {
                        var manifest = await _orderGalleryService.GetOrderManifestAsync(orderId, cancellationToken);
                        var manifestItems = manifest?.Groups.SelectMany(g => g.Items).ToList() ?? new List<OrderGalleryItem>();
                        var itemLookup = manifestItems.ToDictionary(i => i.FileId, StringComparer.OrdinalIgnoreCase);

                        int uploadedCount = 0;
                        int totalRequested = 0;

                        foreach (var fElement in fileIdsProp.EnumerateArray())
                        {
                            totalRequested++;
                            string fileId = fElement.GetString()!;
                            var thumbResult = await _orderGalleryService.GetGalleryThumbnailAsync(orderId, manifestVersion, fileId, cancellationToken);
                            if (thumbResult.Status == ThumbnailStatus.Success && !string.IsNullOrEmpty(thumbResult.CachedFilePath) && File.Exists(thumbResult.CachedFilePath))
                            {
                                itemLookup.TryGetValue(fileId, out var matchedItem);
                                string versionParam = !string.IsNullOrWhiteSpace(matchedItem?.Version) ? Uri.EscapeDataString(matchedItem.Version) : "1";

                                byte[] bytes = await File.ReadAllBytesAsync(thumbResult.CachedFilePath, cancellationToken);
                                using var uploadReq = new HttpRequestMessage(HttpMethod.Put, $"{apiUrl}/api/sync/gallery/images/{fileId}?v={versionParam}")
                                {
                                    Content = new ByteArrayContent(bytes)
                                };
                                uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                                uploadReq.Headers.Add("X-Sync-Secret", secret);
                                using var uploadResp = await _httpClient.SendAsync(uploadReq, cancellationToken);
                                if (uploadResp.IsSuccessStatusCode)
                                {
                                    uploadedCount++;
                                }
                            }
                        }

                        if (uploadedCount == 0 && totalRequested > 0)
                        {
                            using var failReq = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/jobs/{id}/status")
                            {
                                Content = new StringContent(JsonSerializer.Serialize(new { claimToken, status = "FAILED", errorMessage = "Không tạo hoặc tải được ảnh lên Cloud." }, JsonOptions), Encoding.UTF8, "application/json")
                            };
                            failReq.Headers.Add("X-Sync-Secret", secret);
                            await _httpClient.SendAsync(failReq, cancellationToken);
                        }
                        else
                        {
                            if (manifest != null && !string.Equals(manifest.ManifestVersion, manifestVersion, StringComparison.Ordinal))
                            {
                                using var postManifest = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/manifests")
                                {
                                    Content = new StringContent(JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, "application/json")
                                };
                                postManifest.Headers.Add("X-Sync-Secret", secret);
                                using var postResp = await _httpClient.SendAsync(postManifest, cancellationToken);
                                postResp.EnsureSuccessStatusCode();
                            }

                            using var readyReq = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/jobs/{id}/status")
                            {
                                Content = new StringContent(JsonSerializer.Serialize(new { claimToken, status = "READY" }, JsonOptions), Encoding.UTF8, "application/json")
                            };
                            readyReq.Headers.Add("X-Sync-Secret", secret);
                            await _httpClient.SendAsync(readyReq, cancellationToken);
                            processed++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error processing gallery job {JobId}", id);
                    try
                    {
                        using var failReq = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/gallery/jobs/{id}/status")
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new { claimToken, status = "FAILED", errorMessage = ex.Message }, JsonOptions), Encoding.UTF8, "application/json")
                        };
                        failReq.Headers.Add("X-Sync-Secret", secret);
                        await _httpClient.SendAsync(failReq, cancellationToken);
                    }
                    catch { }
                }
            }

            return processed;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Lỗi kiểm tra gallery jobs từ Cloud");
            return 0;
        }
    }

    private async Task<int> ProcessPendingPrintCommandsCoreAsync(CancellationToken cancellationToken = default)
    {
        if (_shippingLabelExporter == null || _printLedger == null || _syncState == null) return 0;
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        if (!settings.EnableCloudSync) return 0;
        string apiUrl = NormalizeApiUrl(settings.CloudSyncApiUrl);
        if (string.IsNullOrWhiteSpace(apiUrl)) return 0;
        var checkpoint = await _syncState.GetCheckpointAsync(cancellationToken);
        if (checkpoint.RequiresReconciliation || string.IsNullOrWhiteSpace(checkpoint.StreamId) || checkpoint.Endpoint != apiUrl) return 0;
        string stream = checkpoint.StreamId;
        _remotePrintWarning = await _printLedger.ReviewWarningAsync(apiUrl, stream);

        async Task<bool> AckAsync(long id, string token, string status, string? error)
        {
            try
            {
                using var ack = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/print-commands/{id}/status")
                { Content = new StringContent(JsonSerializer.Serialize(new { status, errorMessage = error, claimToken = token }, JsonOptions), Encoding.UTF8, "application/json") };
                ack.Headers.Add("X-Sync-Secret", settings.CloudSyncSecret);
                using var response = await _httpClient.SendAsync(ack, cancellationToken);
                response.EnsureSuccessStatusCode();
                await _printLedger.AcknowledgeAsync(apiUrl, stream, id);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Remote print {CommandId}: ACK pending; will not print again", id);
                SetSyncing(false, $"Lệnh in #{id}: chờ Cloud xác nhận, không tự in lại.");
                return false;
            }
        }

        // Retry durable acknowledgements even when the Worker no longer lists PROCESSING jobs.
        foreach (var entry in await _printLedger.PendingAsync(apiUrl, stream))
        {
            if (entry.Status == "STARTED")
            {
                entry.Status = "FAILED";
                entry.ErrorMessage = "Kết quả in không rõ sau gián đoạn. Kiểm tra máy in/tem trước khi tạo lệnh mới; không tự in lại.";
                await _printLedger.FinishAsync(apiUrl, stream, entry.CommandId, entry.Status, entry.ErrorMessage);
                SetSyncing(false, entry.ErrorMessage);
            }
            await AckAsync(entry.CommandId, entry.ClaimToken, entry.Status, entry.ErrorMessage);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}/api/sync/print-commands");
        request.Headers.Add("X-Sync-Secret", settings.CloudSyncSecret);
        using var queue = await _httpClient.SendAsync(request, cancellationToken);
        if (!queue.IsSuccessStatusCode) return 0;
        using var doc = JsonDocument.Parse(await queue.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array) return 0;
        int processed = 0;
        foreach (var command in commands.EnumerateArray())
        {
            long id = command.GetProperty("id").GetInt64();
            string token = Guid.NewGuid().ToString("N");
            if (!await _printLedger.StartAsync(apiUrl, stream, id, token)) continue;
            string status = "FAILED";
            string? error = null;
            bool printed = false;
            try
            {
                using var claim = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/sync/print-commands/{id}/claim")
                { Content = new StringContent(JsonSerializer.Serialize(new { claimToken = token }, JsonOptions), Encoding.UTF8, "application/json") };
                claim.Headers.Add("X-Sync-Secret", settings.CloudSyncSecret);
                using var claimed = await _httpClient.SendAsync(claim, cancellationToken);
                if (claimed.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    await _printLedger.FinishAsync(apiUrl, stream, id, "FAILED", "Lệnh đã được Desktop khác nhận.");
                    await _printLedger.AcknowledgeAsync(apiUrl, stream, id);
                    continue;
                }
                claimed.EnsureSuccessStatusCode();
                using var claimBody = JsonDocument.Parse(await claimed.Content.ReadAsStringAsync(cancellationToken));
                if (!claimBody.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("Cloud chưa xác nhận quyền nhận lệnh in.");
                long orderId = command.TryGetProperty("orderId", out var oProp) && oProp.ValueKind == JsonValueKind.Number ? oProp.GetInt64() : 0;
                Order? order = orderId > 0 ? await _orderRepository.GetOrderByIdAsync(orderId, cancellationToken) : null;
                string type = command.TryGetProperty("commandType", out var t) ? t.GetString() ?? "" : "";
                if (type == "print_bill_label")
                {
                    if (command.TryGetProperty("requestedBy", out var role) && role.GetString() == "Staff")
                        throw new InvalidOperationException("Nhân viên không được in tem hóa đơn có tiền.");
                    long billId = command.GetProperty("billId").GetInt64();
                    var bill = await _billRepository.GetByIdAsync(billId, cancellationToken);
                    if (bill != null)
                    {
                        await _shippingLabelExporter.PrintShippingLabelAsync(bill, silent: true, cancellationToken);
                    }
                    else if (order != null)
                    {
                        await _shippingLabelExporter.PrintOrderShippingLabelAsync(order, silent: true, cancellationToken);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Không tìm thấy hóa đơn #{billId}");
                    }
                    printed = true;
                    // Persist the effect before any delivery mutation or HTTP ACK can fail.
                    await _printLedger.FinishAsync(apiUrl, stream, id, "COMPLETED", null);
                    status = "COMPLETED";
                    processed++;
                    if (settings.AutoMarkDeliveredOnPrint)
                    {
                        if (order != null && !order.IsDelivered)
                        {
                            await _orderRepository.UpdateOrderDeliveredStatusAsync(order.Id, true, DateTimeOffset.Now, "Mobile (In từ xa)", cancellationToken);
                        }
                        if (bill?.Orders != null)
                        {
                            foreach (var bo in bill.Orders.Where(o => o.IsIncluded && o.OrderId > 0))
                            {
                                if (order != null && bo.OrderId == order.Id) continue;
                                var linkedOrder = await _orderRepository.GetOrderByIdAsync(bo.OrderId, cancellationToken);
                                if (linkedOrder != null && !linkedOrder.IsDelivered)
                                {
                                    await _orderRepository.UpdateOrderDeliveredStatusAsync(linkedOrder.Id, true, DateTimeOffset.Now, "Mobile (In từ xa)", cancellationToken);
                                }
                            }
                        }
                    }
                }
                else if (type == "print_order_label")
                {
                    if (order == null)
                        throw new InvalidOperationException($"Không tìm thấy đơn hàng #{orderId}");
                    await _shippingLabelExporter.PrintOrderShippingLabelAsync(order, silent: true, cancellationToken);
                    printed = true;
                    // Persist the effect before any delivery mutation or HTTP ACK can fail.
                    await _printLedger.FinishAsync(apiUrl, stream, id, "COMPLETED", null);
                    status = "COMPLETED";
                    processed++;
                    if (settings.AutoMarkDeliveredOnPrint && !order.IsDelivered)
                        await _orderRepository.UpdateOrderDeliveredStatusAsync(order.Id, true, DateTimeOffset.Now, "Mobile (In từ xa)", cancellationToken);
                }
                else throw new InvalidOperationException("Loại lệnh in không được hỗ trợ.");
            }
            catch (Exception ex)
            {
                error = printed ? "Tem đã in; cập nhật trạng thái giao hàng thất bại: " + ex.Message
                    : "Kết quả in có thể không rõ. Kiểm tra máy in trước khi tạo lệnh mới: " + ex.Message;
                _logger?.LogError(ex, "Remote print {CommandId} interrupted", id);
                SetSyncing(false, error);
                // A failed ledger write after spooling leaves STARTED, never an automatic retry.
                if (!printed) await _printLedger.FinishAsync(apiUrl, stream, id, "FAILED", error);
                else if (status != "COMPLETED") continue;
                else await _printLedger.FinishAsync(apiUrl, stream, id, "COMPLETED", error);
            }
            await AckAsync(id, token, status, error);
        }
        _remotePrintWarning = await _printLedger.ReviewWarningAsync(apiUrl, stream);
        SetSyncing(false, "Đã kiểm tra hàng đợi in Cloud.");
        return processed;
    }

    private void SetSyncing(bool syncing, string? status = null)
    {
        lock (_lock)
        {
            _isSyncing = syncing;
            if (status != null) _lastSyncStatus = !syncing && _remotePrintWarning != null
                ? status + " · " + _remotePrintWarning : status;
        }
        SyncStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static object MapOrderToCloudDto(Order o, OrderThumbnailInfo? thumbInfo = null)
    {
        string? thumbRel = o.ThumbnailCandidateRelativePath;
        if (string.IsNullOrWhiteSpace(thumbRel) && o.Items != null && o.Items.Count > 0)
        {
            thumbRel = o.Items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.ThumbnailCandidateRelativePath))?.ThumbnailCandidateRelativePath;
        }

        string thumbStatus = thumbInfo?.Status ?? (!string.IsNullOrEmpty(thumbRel) ? "READY" : "NONE");
        string? thumbKey = thumbInfo?.Key;
        string? thumbVer = thumbInfo?.Version;

        var itemsList = (o.Items ?? (IReadOnlyList<OrderItemScan>)Array.Empty<OrderItemScan>()).Select(i => new
        {
            id = i.Id,
            specName = i.SpecificationFolderName,
            variantName = i.PrintSpecification?.CanonicalName ?? i.SpecificationFolderName,
            size = i.PrintSpecification?.CanonicalSize,
            billQuantity = i.BillQuantity ?? i.PrintCount ?? 0,
            printCount = i.PrintCount,
            isMissingPrintFolder = i.PrintFolderStatus == PrintFolderResolutionStatus.NoPrintFolder,
            isAmbiguous = i.PrintFolderStatus == PrintFolderResolutionStatus.AmbiguousPrintFolder
        }).ToList();

        return new
        {
            id = o.Id,
            orderCode = o.OrderCode ?? $"ORD-{o.Id}",
            customerName = o.Customer?.CanonicalName ?? o.OriginalFolderName,
            folderName = o.OriginalFolderName,
            workDate = o.WorkDate,
            status = o.Status.ToString(),
            isPrinted = o.IsPrinted,
            printedAt = o.PrintedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            isDelivered = o.IsDelivered,
            deliveredAt = o.DeliveredAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            deliveredBy = o.DeliveredBy,
            note = o.Note,
            hasIssues = o.Status == OrderStatus.NeedsReview || o.Status == OrderStatus.Error,
            isLocked = o.Status == OrderStatus.Locked,
            hasThumbnail = !string.IsNullOrEmpty(thumbRel) || !string.IsNullOrEmpty(o.RelativePath),
            thumbnailFileName = !string.IsNullOrEmpty(thumbRel) ? Path.GetFileName(thumbRel) : null,
            thumbnailStatus = thumbStatus,
            thumbnailKey = thumbKey,
            thumbnailVersion = thumbVer,
            customerBillId = o.Items?.FirstOrDefault(i => i.CustomerBillId != null)?.CustomerBillId,
            totalQuantity = o.Items?.Sum(i => i.BillQuantity ?? i.PrintCount ?? 0) ?? 0,
            itemsJson = JsonSerializer.Serialize(itemsList, JsonOptions)
        };
    }

    private static object MapBillToCloudDto(CustomerBill b)
    {
        var linesList = BillSnapshotLines.Included(b).Select(l => new
        {
            id = l.Id,
            description = l.ProductNameSnapshot,
            size = l.VariantSnapshot,
            quantity = l.BilledQuantity,
            unitPrice = l.BilledUnitPrice,
            lineTotal = l.LineTotal
        }).ToList();

        var adjustmentsList = (b.Adjustments ?? new List<BillAdjustment>()).Select(a => new
        {
            id = a.Id,
            description = a.Label,
            amount = a.Amount,
            type = a.Type.ToString(),
            direction = a.Direction.ToString()
        }).ToList();

        return new
        {
            id = b.Id,
            billNumber = b.BillNumber,
            billType = b.BillType.ToString(),
            customerId = b.CustomerId,
            customerNameSnapshot = b.CustomerNameSnapshot,
            phoneSnapshot = b.PhoneSnapshot,
            shippingAddressSnapshot = b.ShippingAddressSnapshot,
            note = b.Note,
            periodDate = b.PeriodEnd,
            totalAmount = b.GrandTotal,
            productSubtotal = b.ProductSubtotal,
            adjustmentsTotal = b.AdjustmentsTotal,
            isPaid = b.IsPaid,
            paidAt = b.PaidAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            linesJson = JsonSerializer.Serialize(linesList, JsonOptions),
            adjustmentsJson = JsonSerializer.Serialize(adjustmentsList, JsonOptions),
            hasJpeg = !string.IsNullOrWhiteSpace(b.ExportFilePath) && File.Exists(b.ExportFilePath),
            lockedAt = b.LockedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            exportedAt = b.ExportedAt?.ToString("yyyy-MM-dd HH:mm:ss")
        };
    }

    private static string NormalizeApiUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
        string url = rawUrl.Trim().TrimEnd('/');
        if (string.Equals(url, "https://app.tinix.io.vn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(url, "http://app.tinix.io.vn", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://lalab.tinix.io.vn";
        }
        else if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }
}
