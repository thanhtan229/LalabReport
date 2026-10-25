using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.Services;
using Xunit;

namespace LalabAutoReport.Tests;

public partial class CloudSyncServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteSettingsRepository _settingsRepo;
    private readonly SqliteOrderRepository _orderRepo;
    private readonly SqliteCustomerBillRepository _customerBillRepo;
    private readonly SqliteBillRepository _billRepo;
    private readonly SqliteCustomerRepository _customerRepo;
    private readonly SqliteProductRepository _productRepo;
    private readonly BillingService _billingService;
    private readonly ReportService _reportService;

    public CloudSyncServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "CloudSyncTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        string dbPath = Path.Combine(_tempRoot, "test_cloud_sync.db");
        _connectionFactory = new SqliteConnectionFactory(dbPath);

        var migrator = new DatabaseMigrator(_connectionFactory);
        migrator.MigrateAsync().GetAwaiter().GetResult();

        _settingsRepo = new SqliteSettingsRepository(_connectionFactory);
        _orderRepo = new SqliteOrderRepository(_connectionFactory);
        _customerBillRepo = new SqliteCustomerBillRepository(_connectionFactory);
        _billRepo = new SqliteBillRepository(_connectionFactory);
        _customerRepo = new SqliteCustomerRepository(_connectionFactory);
        _productRepo = new SqliteProductRepository(_connectionFactory);

        _billingService = new BillingService(_orderRepo, _billRepo, _productRepo, _customerRepo);
        _reportService = new ReportService(_orderRepo, _billRepo, _billingService, _productRepo, _customerRepo, null, _customerBillRepo);
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

    private class TestHttpMessageHandler : HttpMessageHandler
    {
        public Action<HttpRequestMessage>? OnSend { get; set; }
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedBody { get; private set; }
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true}")
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OnSend?.Invoke(request);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/pull")) CapturedRequest = request;
            if (request.Content != null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            string content = request.RequestUri!.AbsolutePath.EndsWith("/claim") ? "{\"success\":true}" : await ResponseToReturn.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/pull") && ResponseToReturn.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(content);
                if (!doc.RootElement.TryGetProperty("events", out _))
                    content = "{\"success\":true,\"protocolVersion\":2,\"streamId\":\"test-stream\",\"events\":[],\"nextCursor\":0,\"hasMore\":false}";
            }
            return new HttpResponseMessage(ResponseToReturn.StatusCode) { Content = new StringContent(content) };
        }
    }

    [Fact]
    public async Task SyncAllAsync_WhenDisabled_ReturnsFailureWithoutCallingHttp()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = false;
        await _settingsRepo.SaveSettingsAsync(settings);

        var handler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient, enablePeriodicSync: false);

        var result = await service.SyncAllAsync();

        Assert.False(result.Success);
        Assert.Contains("tắt", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.CapturedRequest);
    }

    [Fact]
    public async Task SyncAllAsync_WhenUrlMissing_ReturnsFailure()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "";
        await _settingsRepo.SaveSettingsAsync(settings);

        var handler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient, enablePeriodicSync: false);

        var result = await service.SyncAllAsync();

        Assert.False(result.Success);
        Assert.Contains("Chưa cấu hình", result.Message);
        Assert.Null(handler.CapturedRequest);
    }

    [Fact]
    public async Task SyncAllAsync_WhenSuccessful_PostsBatchAndUpdatesLastSyncAt()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "https://lalab.tinix.io.vn";
        settings.CloudSyncSecret = "my-secret-123";
        await _settingsRepo.SaveSettingsAsync(settings);

        // Seed an order directly in SQLite
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO orders (work_date, original_folder_name, relative_path, status, created_at, updated_at)
                VALUES (@WorkDate, @FolderName, @RelPath, @Status, datetime('now'), datetime('now'));
            ", new
            {
                WorkDate = DateTime.Today.ToString("yyyy-MM-dd"),
                FolderName = "Studio ABC",
                RelPath = "2026-10-02/Studio ABC",
                Status = "Ready"
            });
        }

        var handler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient, enablePeriodicSync: false);

        var result = await service.SyncAllAsync();

        Assert.True(result.Success);
        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal("https://lalab.tinix.io.vn/api/sync/batch", handler.CapturedRequest!.RequestUri!.ToString());
        Assert.True(handler.CapturedRequest.Headers.Contains("X-Sync-Secret"));

        var values = handler.CapturedRequest.Headers.GetValues("X-Sync-Secret");
        Assert.Contains("my-secret-123", values);

        var updatedSettings = await _settingsRepo.GetSettingsAsync();
        Assert.False(string.IsNullOrWhiteSpace(updatedSettings.LastCloudSyncAt));
    }

    [Fact]
    public async Task SyncAllAsync_WhenServerReturns500_HandlesGracefullyWithoutThrowing()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "https://lalab.tinix.io.vn";
        settings.CloudSyncSecret = "my-secret-123";
        await _settingsRepo.SaveSettingsAsync(settings);

        var handler = new TestHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("D1 DB error")
            }
        };
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient, enablePeriodicSync: false);

        var result = await service.SyncAllAsync();

        Assert.False(result.Success);
        Assert.Contains("500", result.Message);
        Assert.Contains("500", result.Error);
    }

    [Fact]
    public async Task PullRemoteChangesAsync_WithDeliveredPaymentAndNoteEvents_UpdatesSqlite()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "https://lalab.tinix.io.vn";
        settings.CloudSyncSecret = "my-secret-123";
        settings.LastCloudPullAt = null;
        await _settingsRepo.SaveSettingsAsync(settings);

        long orderId;
        long billId;

        using (var conn = _connectionFactory.CreateConnection())
        {
            orderId = await conn.QuerySingleAsync<long>(@"
                INSERT INTO orders (work_date, original_folder_name, relative_path, status, is_delivered, created_at, updated_at)
                VALUES ('2026-10-02', 'Studio VIP', '2026-10-02/Studio VIP', 'Ready', 0, datetime('now'), datetime('now'))
                RETURNING id;
            ");

            billId = await conn.QuerySingleAsync<long>(@"
                INSERT INTO customer_bills (bill_number, bill_type, customer_name_snapshot, period_start, period_end, status, product_subtotal, grand_total, is_paid, created_at, updated_at)
                VALUES ('BILL-202610-999', 'Customer', 'Studio VIP', '2026-10-02', '2026-10-02', 'Locked', 500000, 500000, 0, datetime('now'), datetime('now'))
                RETURNING id;
            ");
        }

        string eventsJson = $@"{{
            ""success"": true,
            ""protocolVersion"": 2, ""streamId"": ""test-stream"", ""nextCursor"": 3, ""hasMore"": false,
            ""events"": [
                {{
                    ""id"": 1,
                    ""entityType"": ""order_delivered"",
                    ""entityId"": {orderId},
                    ""payloadJson"": ""{{\""isDelivered\"":true,\""deliveredAt\"":\""2026-10-02T15:00:00Z\"",\""deliveredBy\"":\""Shipper An\""}}"",
                    ""createdAt"": ""2026-10-02T15:00:00Z""
                }},
                {{
                    ""id"": 2,
                    ""entityType"": ""bill_payment"",
                    ""entityId"": {billId},
                    ""payloadJson"": ""{{\""isPaid\"":true,\""paidAt\"":\""2026-10-02T15:05:00Z\""}}"",
                    ""createdAt"": ""2026-10-02T15:05:00Z""
                }},
                {{
                    ""id"": 3,
                    ""entityType"": ""order_note"",
                    ""entityId"": {orderId},
                    ""payloadJson"": ""{{\""note\"":\""Đã kiểm hàng đầy đủ\""}}"",
                    ""createdAt"": ""2026-10-02T15:10:00Z""
                }}
            ],
            ""serverTime"": ""2026-10-02T15:10:00Z""
        }}";

        var handler = new TestHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(eventsJson)
            }
        };
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient, enablePeriodicSync: false);

        int applied = await service.PullRemoteChangesAsync();
        Assert.Equal(3, applied);

        // Verify SQLite state
        var updatedOrder = await _orderRepo.GetOrderByIdAsync(orderId);
        Assert.NotNull(updatedOrder);
        Assert.True(updatedOrder!.IsDelivered);
        Assert.Equal("Shipper An", updatedOrder.DeliveredBy);
        Assert.Equal("Đã kiểm hàng đầy đủ", updatedOrder.Note);

        var updatedBill = await _customerBillRepo.GetByIdAsync(billId);
        Assert.NotNull(updatedBill);
        Assert.True(updatedBill!.IsPaid);

        var updatedSettings = await _settingsRepo.GetSettingsAsync();
        Assert.Null(updatedSettings.LastCloudPullAt);
        Assert.Equal(3, (await new SqliteCloudSyncStateRepository(_connectionFactory).GetCheckpointAsync()).Cursor);
    }

    [Fact]
    public async Task SendHeartbeatAsync_WhenEnabled_SendsValidPayloadToWorker()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "https://lalab.tinix.io.vn";
        settings.CloudSyncSecret = "test-secret";
        settings.MobileServerPort = 5050;
        await _settingsRepo.SaveSettingsAsync(settings);

        var handler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient, enablePeriodicSync: false);

        bool success = await service.SendHeartbeatAsync();
        Assert.True(success);

        Assert.NotNull(handler.CapturedRequest);
        Assert.Equal(HttpMethod.Post, handler.CapturedRequest!.Method);
        Assert.Equal("https://lalab.tinix.io.vn/api/sync/heartbeat", handler.CapturedRequest.RequestUri!.ToString());
        Assert.True(handler.CapturedRequest.Headers.Contains("X-Sync-Secret"));

        Assert.NotNull(handler.CapturedBody);
        Assert.Contains("deviceId", handler.CapturedBody);
        Assert.Contains("lanUrl", handler.CapturedBody);
        Assert.Contains(":5050", handler.CapturedBody);
    }

    [Fact]
    public async Task SendHeartbeatAsync_WhenDisabled_ReturnsFalseWithoutCallingHttp()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = false;
        await _settingsRepo.SaveSettingsAsync(settings);

        var handler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient, enablePeriodicSync: false);

        bool success = await service.SendHeartbeatAsync();
        Assert.False(success);
        Assert.Null(handler.CapturedRequest);
    }

    private class TestShippingLabelExporter : IShippingLabelExporter
    {
        public List<long> PrintedOrders { get; } = new();
        public List<long> PrintedBills { get; } = new();

        public Task<string> ExportShippingLabelImageAsync(CustomerBill bill, string? destinationPath = null, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task PrintShippingLabelAsync(CustomerBill bill, bool silent = false, CancellationToken cancellationToken = default)
        {
            PrintedBills.Add(bill.Id);
            return Task.CompletedTask;
        }
        public Task<string> ExportOrderShippingLabelImageAsync(Order order, string? destinationPath = null, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task PrintOrderShippingLabelAsync(Order order, bool silent = false, CancellationToken cancellationToken = default)
        {
            PrintedOrders.Add(order.Id);
            return Task.CompletedTask;
        }
        public Task PrintTestSampleLabelAsync(string? printerName = null, string? labelSizeOverride = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ProcessPendingPrintCommandsAsync_ExecutesPrintAndAcknowledgesWorker()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "https://lalab.tinix.io.vn";
        settings.CloudSyncSecret = "test-secret";
        settings.AutoMarkDeliveredOnPrint = true;
        await _settingsRepo.SaveSettingsAsync(settings);

        long orderId;
        using (var conn = _connectionFactory.CreateConnection())
        {
            orderId = await conn.ExecuteScalarAsync<long>(@"
                INSERT INTO orders (order_code, work_date, original_folder_name, relative_path, status, is_delivered, created_at, updated_at)
                VALUES ('DH-PRINT-01', '2026-10-02', 'Khach A', '2026-10-02/Khach A', 'Ready', 0, datetime('now'), datetime('now'));
                SELECT last_insert_rowid();
            ");
        }

        await new SqliteCloudSyncStateRepository(_connectionFactory).BindStreamAsync("print-stream", settings.CloudSyncApiUrl);

        string pendingCmdsJson = $@"{{
            ""commands"": [
                {{
                    ""id"": 12,
                    ""orderId"": {orderId},
                    ""commandType"": ""print_order_label"",
                    ""requestedBy"": ""Staff""
                }}
            ]
        }}";

        var handler = new TestHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(pendingCmdsJson)
            }
        };
        var httpClient = new HttpClient(handler);
        var exporter = new TestShippingLabelExporter();

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient,
            null,
            exporter, enablePeriodicSync: false);

        int processed = await service.ProcessPendingPrintCommandsAsync();
        Assert.Equal(1, processed);
        Assert.Contains(orderId, exporter.PrintedOrders);

        // Verify order delivery marked
        var order = await _orderRepo.GetOrderByIdAsync(orderId);
        Assert.NotNull(order);
        Assert.True(order!.IsDelivered);
    }

    [Fact]
    public async Task ProcessPendingPrintCommandsAsync_ExecutesBillPrintAndMarksIncludedOrdersDelivered()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "https://lalab.tinix.io.vn";
        settings.CloudSyncSecret = "test-secret";
        settings.AutoMarkDeliveredOnPrint = true;
        await _settingsRepo.SaveSettingsAsync(settings);

        long includedOrderId;
        long excludedOrderId;
        long billId;
        using (var conn = _connectionFactory.CreateConnection())
        {
            includedOrderId = await conn.ExecuteScalarAsync<long>(@"
                INSERT INTO orders (order_code, work_date, original_folder_name, relative_path, status, is_delivered, created_at, updated_at)
                VALUES ('DH-PRINT-BILL-01', '2026-10-02', 'Khach Bill 1', '2026-10-02/Khach Bill 1', 'Ready', 0, datetime('now'), datetime('now'));
                SELECT last_insert_rowid();
            ");

            excludedOrderId = await conn.ExecuteScalarAsync<long>(@"
                INSERT INTO orders (order_code, work_date, original_folder_name, relative_path, status, is_delivered, created_at, updated_at)
                VALUES ('DH-PRINT-BILL-02', '2026-10-02', 'Khach Bill 2', '2026-10-02/Khach Bill 2', 'Ready', 0, datetime('now'), datetime('now'));
                SELECT last_insert_rowid();
            ");

            billId = await conn.ExecuteScalarAsync<long>(@"
                INSERT INTO customer_bills (bill_number, bill_type, customer_name_snapshot, period_start, period_end, status, product_subtotal, adjustments_total, grand_total, is_paid, created_at, updated_at)
                VALUES ('BILL-20261002-0099', 'Customer', 'Khach Bill Test', '2026-10-02', '2026-10-02', 'Draft', 100000, 0, 100000, 0, datetime('now'), datetime('now'));
                SELECT last_insert_rowid();
            ");

            await conn.ExecuteAsync(@"
                INSERT INTO customer_bill_orders (bill_id, order_id, order_name_snapshot, order_date_snapshot, original_folder_name_snapshot, subtotal, is_included)
                VALUES (@BillId, @OrderId, 'DH 1', '2026-10-02', 'Khach Bill 1', 50000, 1);
            ", new { BillId = billId, OrderId = includedOrderId });

            await conn.ExecuteAsync(@"
                INSERT INTO customer_bill_orders (bill_id, order_id, order_name_snapshot, order_date_snapshot, original_folder_name_snapshot, subtotal, is_included)
                VALUES (@BillId, @OrderId, 'DH 2', '2026-10-02', 'Khach Bill 2', 50000, 0);
            ", new { BillId = billId, OrderId = excludedOrderId });
        }

        await new SqliteCloudSyncStateRepository(_connectionFactory).BindStreamAsync("print-stream", settings.CloudSyncApiUrl);

        string pendingCmdsJson = $@"{{
            ""commands"": [
                {{
                    ""id"": 99,
                    ""orderId"": 0,
                    ""billId"": {billId},
                    ""commandType"": ""print_bill_label"",
                    ""requestedBy"": ""Admin""
                }}
            ]
        }}";

        var handler = new TestHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(pendingCmdsJson)
            }
        };
        var httpClient = new HttpClient(handler);
        var exporter = new TestShippingLabelExporter();

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient,
            null,
            exporter, enablePeriodicSync: false);

        int processed = await service.ProcessPendingPrintCommandsAsync();
        Assert.Equal(1, processed);
        Assert.Contains(billId, exporter.PrintedBills);

        // Verify included order delivery marked, excluded order untouched
        var incOrder = await _orderRepo.GetOrderByIdAsync(includedOrderId);
        Assert.NotNull(incOrder);
        Assert.True(incOrder!.IsDelivered);

        var excOrder = await _orderRepo.GetOrderByIdAsync(excludedOrderId);
        Assert.NotNull(excOrder);
        Assert.False(excOrder!.IsDelivered);
    }

    [Fact]
    public async Task ProcessPendingGalleryJobsAsync_ClaimsAndProcessesManifestJob_Successfully()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "https://example.com/api";
        settings.CloudSyncSecret = "test-secret";
        await _settingsRepo.SaveSettingsAsync(settings);

        long orderId = 101;
        var mockGallery = new MockOrderGalleryService();
        mockGallery.ManifestToReturn = new OrderGalleryManifest
        {
            OrderId = orderId,
            ManifestVersion = "v1_test",
            TotalImageCount = 1,
            Groups = new List<OrderGalleryGroup>()
        };

        string jobsJson = $@"{{
            ""jobs"": [
                {{
                    ""id"": ""job-101-manifest"",
                    ""orderId"": {orderId},
                    ""manifestVersion"": """",
                    ""status"": ""PENDING""
                }}
            ]
        }}";

        var handler = new TestHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jobsJson)
            }
        };
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient,
            enablePeriodicSync: false,
            orderGalleryService: mockGallery);

        int processed = await service.ProcessPendingGalleryJobsAsync();
        Assert.Equal(1, processed);
        Assert.True(mockGallery.GetOrderManifestCalled);
    }

    private class MockOrderGalleryService : IOrderGalleryService
    {
        public bool GetOrderManifestCalled { get; set; }
        public OrderGalleryManifest? ManifestToReturn { get; set; }

        public Task<OrderGalleryManifest> GetOrderManifestAsync(long orderId, CancellationToken cancellationToken = default)
        {
            GetOrderManifestCalled = true;
            return Task.FromResult(ManifestToReturn ?? new OrderGalleryManifest { OrderId = orderId });
        }

        public Task<OrderGalleryPage> GetOrderGalleryPageAsync(long orderId, string manifestVersion, int offset, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderGalleryPage());

        public string? ThumbnailPathToReturn { get; set; }

        public Task<ThumbnailResult> GetGalleryThumbnailAsync(long orderId, string manifestVersion, string fileId, CancellationToken cancellationToken = default)
        {
            if (ThumbnailPathToReturn != null)
                return Task.FromResult(ThumbnailResult.SuccessResult(ThumbnailPathToReturn));
            return Task.FromResult(ThumbnailResult.NotFound());
        }

        public Task TouchViewedBatchAsync(long orderId, string manifestVersion, IEnumerable<string> fileIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task ProcessPendingGalleryJobsAsync_WhenJobManifestVersionDiffersFromLocal_PushesUpdatedManifestBeforeReady()
    {
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.EnableCloudSync = true;
        settings.CloudSyncApiUrl = "https://app.tinix.io.vn";
        settings.CloudSyncSecret = "test-secret";
        await _settingsRepo.SaveSettingsAsync(settings);

        long orderId = 202;
        string tempThumbFile = Path.Combine(_tempRoot, "test_thumb.jpg");
        await File.WriteAllBytesAsync(tempThumbFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01 });

        var mockGallery = new MockOrderGalleryService
        {
            ManifestToReturn = new OrderGalleryManifest
            {
                OrderId = orderId,
                ManifestVersion = "v2_new_manifest",
                TotalImageCount = 1,
                Groups = new List<OrderGalleryGroup>
                {
                    new()
                    {
                        Items = new List<OrderGalleryItem>
                        {
                            new()
                            {
                                FileId = "file_999",
                                Version = "v2_item_ver"
                            }
                        }
                    }
                }
            },
            ThumbnailPathToReturn = tempThumbFile
        };

        string jobsJson = $@"{{
            ""jobs"": [
                {{
                    ""id"": ""job-batch-202"",
                    ""orderId"": {orderId},
                    ""manifestVersion"": ""v1_old_manifest"",
                    ""status"": ""PENDING"",
                    ""fileIds"": [""file_999""]
                }}
            ]
        }}";

        var recordedRequests = new List<(HttpMethod Method, string Path)>();
        var handler = new TestHttpMessageHandler
        {
            OnSend = req => recordedRequests.Add((req.Method, req.RequestUri!.AbsolutePath)),
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jobsJson)
            }
        };
        var httpClient = new HttpClient(handler);

        var service = new CloudSyncService(
            _settingsRepo,
            _orderRepo,
            _customerBillRepo,
            _reportService,
            httpClient,
            enablePeriodicSync: false,
            orderGalleryService: mockGallery);

        int processed = await service.ProcessPendingGalleryJobsAsync();
        Assert.Equal(1, processed);

        var manifestPostIdx = recordedRequests.FindIndex(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/api/sync/gallery/manifests"));
        var readyPostIdx = recordedRequests.FindIndex(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/api/sync/gallery/jobs/job-batch-202/status"));

        Assert.True(manifestPostIdx >= 0, "Updated manifest was not pushed to /api/sync/gallery/manifests");
        Assert.True(readyPostIdx >= 0, "Job status READY was not reported");
        Assert.True(manifestPostIdx < readyPostIdx, $"Updated manifest POST (index {manifestPostIdx}) must happen before READY status POST (index {readyPostIdx})");
    }

    [Fact]
    public void MapBillToCloudDto_IncludesPhoneShippingAddressAndNote()
    {
        var bill = new CustomerBill
        {
            Id = 42,
            BillNumber = "BILL-42",
            CustomerNameSnapshot = "Khách Hàng Test",
            PhoneSnapshot = "0987654321",
            ShippingAddressSnapshot = "Số 10 Tràng Tiền, Hoàn Kiếm, Hà Nội",
            Note = "Giao buổi sáng",
            PeriodEnd = "2026-10-25",
            GrandTotal = 250000,
            ProductSubtotal = 250000,
            AdjustmentsTotal = 0,
            IsPaid = true
        };

        var mapper = typeof(CloudSyncService).GetMethod("MapBillToCloudDto", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var dtoObj = mapper.Invoke(null, new object[] { bill });
        using var jsonDoc = JsonDocument.Parse(JsonSerializer.Serialize(dtoObj));
        var root = jsonDoc.RootElement;

        Assert.Equal("0987654321", root.GetProperty("phoneSnapshot").GetString());
        Assert.Equal("Số 10 Tràng Tiền, Hoàn Kiếm, Hà Nội", root.GetProperty("shippingAddressSnapshot").GetString());
        Assert.Equal("Giao buổi sáng", root.GetProperty("note").GetString());
    }

}
