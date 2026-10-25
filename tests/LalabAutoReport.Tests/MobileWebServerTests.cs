using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LalabAutoReport.Tests;

public class MobileWebServerTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly KestrelMobileWebServer _webServer;
    private readonly HttpClient _httpClient;
    private readonly AppSettings _settings;
    private readonly MockOrderRepository _orderRepo = new();
    private readonly MockCustomerBillRepository _billRepo = new();
    private readonly MockShippingLabelExporter _labelExporter = new();
    private readonly int _testPort = GetAvailablePort();

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private readonly string _tempDb;
    private readonly ISessionService _sessionService;

    private string GetToken(MobileUserRole role)
    {
        var result = _sessionService.LoginAsync(role == MobileUserRole.Admin ? "888888" : "111111", clientKind: "pwa", ip: "127.0.0.1").GetAwaiter().GetResult();
        return result.Token!;
    }

    public MobileWebServerTests()
    {
        var services = new ServiceCollection();

        _settings = new AppSettings
        {
            EnableMobileServer = true,
            MobileServerPort = _testPort,
            AdminPin = "888888",
            StaffPin = "111111",
            WorkshopName = "Test Photo Workshop",
            AutoMarkDeliveredOnPrint = true
        };

        var settingsRepo = new InMemorySettingsRepository(_settings);
        services.AddSingleton<ISettingsRepository>(settingsRepo);

        string tempDb = Path.Combine(Path.GetTempPath(), "lalab_mws_" + Guid.NewGuid().ToString("N") + ".db");
        _tempDb = tempDb;
        var connFactory = new SqliteConnectionFactory(tempDb);
        var migrator = new DatabaseMigrator(connFactory);
        migrator.MigrateAsync().GetAwaiter().GetResult();

        var credStore = new SqliteRoleCredentialStore(connFactory);
        credStore.SetPinAsync("Admin", "888888").GetAwaiter().GetResult();
        credStore.SetPinAsync("Staff", "111111").GetAwaiter().GetResult();

        var sessionStore = new SqliteSessionStore(connFactory);
        var rateLimiter = new LoginRateLimiter(TimeProvider.System);
        var auditLogger = new SqliteAuditLogger(connFactory);
        _sessionService = new SessionService(credStore, sessionStore, rateLimiter, auditLogger);

        services.AddSingleton<ISqliteConnectionFactory>(connFactory);
        services.AddSingleton<IRoleCredentialStore>(credStore);
        services.AddSingleton<ISessionStore>(sessionStore);
        services.AddSingleton<LoginRateLimiter>(rateLimiter);
        services.AddSingleton<IAuditLogger>(auditLogger);
        services.AddSingleton<ISessionService>(_sessionService);
        var authService = new MobileAuthService();
        services.AddSingleton<IMobileAuthService>(authService);

        // Mock repositories
        services.AddSingleton<IOrderRepository>(_orderRepo);
        services.AddSingleton<ICustomerBillRepository>(_billRepo);
        services.AddSingleton<ICustomerRepository>(new MockCustomerRepository());
        services.AddSingleton<IReportService>(new MockReportService());
        services.AddSingleton<IJpegBillExporter>(new MockJpegBillExporter());
        services.AddSingleton<IShippingLabelExporter>(_labelExporter);
        services.AddSingleton<IThumbnailService>(new MockThumbnailService());
        services.AddSingleton<IOrderGalleryService>(new MockOrderGalleryService());

        _serviceProvider = services.BuildServiceProvider();

        _webServer = new KestrelMobileWebServer(
            _serviceProvider,
            settingsRepo,
            authService
        );

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_testPort}")
        };
    }

    [Theory]
    [InlineData("image", false)]
    [InlineData("label", false)]
    [InlineData("qr", false)]
    [InlineData("print", true)]
    [InlineData("toggle-payment", true)]
    public async Task ProductionPermission_StaffCannotAccessFinancialMediaOrMutations(string endpoint, bool post)
    {
        await _webServer.StartAsync();
        try
        {
            var token = GetToken(MobileUserRole.Staff);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = post ? await _httpClient.PostAsync($"/api/bills/1/{endpoint}", null)
                : await _httpClient.GetAsync($"/api/bills/1/{endpoint}");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await _webServer.StopAsync(); }
    }

    [Theory]
    [InlineData(MobileUserRole.Admin)]
    [InlineData(MobileUserRole.Staff)]
    public async Task ProductionContract_BillDetailMatchesPwaAndRedactsAmounts(MobileUserRole role)
    {
        _billRepo.Bills[1] = new CustomerBill { Id = 1, BillNumber = "AUDIT-1", CustomerNameSnapshot = "Audit Customer",
            PeriodEnd = "2026-10-02", GrandTotal = 75000, ProductSubtotal = 75000,
            Lines = new() { new() { Id = 1, ProductNameSnapshot = "Photo", BilledQuantity = 15, BilledUnitPrice = 5000, LineTotal = 75000 } } };
        await _webServer.StartAsync();
        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                GetToken(role));
            var response = await _httpClient.GetAsync("/api/bills/1");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var bill = json.RootElement;
            bill.GetProperty("id").GetInt64().Should().Be(1);
            bill.GetProperty("billNumber").GetString().Should().Be("AUDIT-1");
            bill.GetProperty("billType").GetString().Should().Be("Customer");
            bill.GetProperty("customerName").GetString().Should().Be("Audit Customer");
            bill.GetProperty("periodEnd").GetString().Should().Be("2026-10-02");
            bill.GetProperty("status").GetString().Should().Be("Draft");
            bill.GetProperty("isPaid").GetBoolean().Should().BeFalse();
            bill.GetProperty("lines")[0].GetProperty("description").GetString().Should().Be("Photo");
            bill.GetProperty("lines")[0].GetProperty("quantity").GetInt32().Should().Be(15);

            if (role == MobileUserRole.Admin)
            {
                bill.GetProperty("productSubtotal").GetInt64().Should().Be(75000);
                bill.GetProperty("adjustmentsTotal").GetInt64().Should().Be(0);
                bill.GetProperty("grandTotal").GetInt64().Should().Be(75000);
                bill.GetProperty("lines")[0].GetProperty("unitPrice").GetInt64().Should().Be(5000);
                bill.GetProperty("lines")[0].GetProperty("lineTotal").GetInt64().Should().Be(75000);
                bill.GetProperty("adjustments").GetArrayLength().Should().Be(0);
            }
            else
            {
                bill.GetProperty("productSubtotal").ValueKind.Should().Be(JsonValueKind.Null);
                bill.GetProperty("adjustmentsTotal").ValueKind.Should().Be(JsonValueKind.Null);
                bill.GetProperty("grandTotal").ValueKind.Should().Be(JsonValueKind.Null);
                bill.GetProperty("lines")[0].GetProperty("unitPrice").ValueKind.Should().Be(JsonValueKind.Null);
                bill.GetProperty("lines")[0].GetProperty("lineTotal").ValueKind.Should().Be(JsonValueKind.Null);
                bill.GetProperty("adjustments").ValueKind.Should().Be(JsonValueKind.Null);
                bill.GetProperty("hasExportFile").GetBoolean().Should().BeFalse();
                bill.GetProperty("hasPaymentQr").GetBoolean().Should().BeFalse();
            }
        }
        finally { await _webServer.StopAsync(); }
    }


    [Theory]
    [InlineData(MobileUserRole.Staff, true)]
    [InlineData(MobileUserRole.Staff, false)]
    [InlineData(MobileUserRole.Admin, true)]
    [InlineData(MobileUserRole.Admin, false)]
    public async Task AttachedOrderLabel_RespectsFinancialRoleForPreviewAndPrint(MobileUserRole role, bool attached)
    {
        _orderRepo.Orders[1] = new Order { Id=1, Items=new List<OrderItemScan> { new() { CustomerBillId=attached ? 1 : null } } };
        _billRepo.Bills[1] = new CustomerBill { Id=1, GrandTotal=75000 };
        string file = Path.GetTempFileName();
        _labelExporter.MockLabelPath = file;
        await _webServer.StartAsync();
        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetToken(role));
            (await _httpClient.GetAsync("/api/orders/1/label")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await _httpClient.PostAsync("/api/orders/1/print-label", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            bool financial = attached && role == MobileUserRole.Admin;
            _labelExporter.ExportBillCalls.Should().Be(financial ? 1 : 0);
            _labelExporter.PrintShippingLabelCalls.Should().Be(financial ? 1 : 0);
            _labelExporter.ExportOrderCalls.Should().Be(financial ? 0 : 1);
            _labelExporter.PrintOrderShippingLabelCalls.Should().Be(financial ? 0 : 1);
        }
        finally { await _webServer.StopAsync(); File.Delete(file); }
    }

    [Theory]
    [InlineData(MobileUserRole.Admin)]
    [InlineData(MobileUserRole.Staff)]
    public async Task MobileBill_ExcludesRejectedLinesAndWholeOrders(MobileUserRole role)
    {
        _billRepo.Bills[1] = new CustomerBill { Id=1, ProductSubtotal=75000, AdjustmentsTotal=-5000, GrandTotal=70000,
            Orders=new() { new() { OrderId=10 }, new() { OrderId=20,IsIncluded=false } },
            Lines=new() { new() { Id=1,OrderId=10,LineTotal=75000 }, new() { Id=2,OrderId=10,LineTotal=25000,IsIncluded=false }, new() { Id=3,OrderId=20,LineTotal=90000 } } };
        await _webServer.StartAsync();
        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetToken(role));
            using var json = JsonDocument.Parse(await _httpClient.GetStringAsync("/api/bills/1"));
            var body = json.RootElement;
            body.GetProperty("lines").GetArrayLength().Should().Be(1);
            body.GetProperty("lines")[0].GetProperty("id").GetInt64().Should().Be(1);
            if (role == MobileUserRole.Admin) { body.GetProperty("productSubtotal").GetInt64().Should().Be(75000); body.GetProperty("grandTotal").GetInt64().Should().Be(70000); }
            else body.GetProperty("productSubtotal").ValueKind.Should().Be(JsonValueKind.Null);
            _billRepo.Bills[1].Lines.Count.Should().Be(3);
        }
        finally { await _webServer.StopAsync(); }
    }

    [Fact]
    public async Task StatusEndpoint_ShouldReturnRunningInfo()
    {
        await _webServer.StartAsync();

        try
        {
            var response = await _httpClient.GetAsync("/api/status");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("Lalab Auto Report");
            content.Should().Contain("Test Photo Workshop");
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task Login_ValidAdminPin_ShouldReturnAdminToken()
    {
        await _webServer.StartAsync();

        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { pin = "888888" }),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync("/api/auth/login", content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("role").GetString().Should().Be("Admin");
            doc.RootElement.GetProperty("token").GetString().Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task Login_ValidStaffPin_ShouldReturnStaffToken()
    {
        await _webServer.StartAsync();

        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { pin = "111111" }),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync("/api/auth/login", content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("role").GetString().Should().Be("Staff");
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task Login_InvalidPin_ShouldReturn401()
    {
        await _webServer.StartAsync();

        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { pin = "000999" }),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync("/api/auth/login", content);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task Login_WhenAdminPinNotConfigured_ShouldReturn503ServiceUnavailable()
    {
        string originalAdminPin = _settings.AdminPin;
        _settings.AdminPin = string.Empty;
        await _webServer.StartAsync();

        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { pin = "888888" }),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync("/api/auth/login", content);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("message").GetString().Should().Contain("Chưa cấu hình mã PIN");
        }
        finally
        {
            _settings.AdminPin = originalAdminPin;
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task Login_WhenAdminAndStaffPinsAreIdentical_ShouldReturn503ServiceUnavailable()
    {
        string originalStaffPin = _settings.StaffPin;
        _settings.StaffPin = _settings.AdminPin;
        await _webServer.StartAsync();

        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { pin = _settings.AdminPin }),
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync("/api/auth/login", content);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("message").GetString().Should().Contain("không được trùng nhau");
        }
        finally
        {
            _settings.StaffPin = originalStaffPin;
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task RoleAuthorization_StaffAccessingDailyReport_ShouldBeForbidden()
    {
        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string staffToken = GetToken(MobileUserRole.Staff);
            string adminToken = GetToken(MobileUserRole.Admin);

            // 1. Staff access daily report -> 403 Forbidden
            var staffReq = new HttpRequestMessage(HttpMethod.Get, "/api/reports/daily?date=2026-10-01");
            staffReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
            var staffRes = await _httpClient.SendAsync(staffReq);
            staffRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // 1b. Staff access monthly report -> 403 Forbidden
            var staffMonthlyReq = new HttpRequestMessage(HttpMethod.Get, "/api/reports/monthly?year=2026&month=10");
            staffMonthlyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
            var staffMonthlyRes = await _httpClient.SendAsync(staffMonthlyReq);
            staffMonthlyRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // 2. Staff access unpaid debts -> 403 Forbidden
            var staffDebtsReq = new HttpRequestMessage(HttpMethod.Get, "/api/customers/unpaid");
            staffDebtsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
            var staffDebtsRes = await _httpClient.SendAsync(staffDebtsReq);
            staffDebtsRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // 3. Admin access daily report -> 200 OK
            var adminReq = new HttpRequestMessage(HttpMethod.Get, "/api/reports/daily?date=2026-10-01");
            adminReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var adminRes = await _httpClient.SendAsync(adminReq);
            adminRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3b. Admin access monthly report -> 200 OK
            var adminMonthlyReq = new HttpRequestMessage(HttpMethod.Get, "/api/reports/monthly?year=2026&month=10");
            adminMonthlyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var adminMonthlyRes = await _httpClient.SendAsync(adminMonthlyReq);
            adminMonthlyRes.StatusCode.Should().Be(HttpStatusCode.OK);

            // 3c. Admin access unpaid debts -> 200 OK
            var adminDebtsReq = new HttpRequestMessage(HttpMethod.Get, "/api/customers/unpaid");
            adminDebtsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var adminDebtsRes = await _httpClient.SendAsync(adminDebtsReq);
            adminDebtsRes.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task Fallback_ShouldServeHtmlSinglePageApp()
    {
        await _webServer.StartAsync();

        try
        {
            var response = await _httpClient.GetAsync("/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");

            string html = await response.Content.ReadAsStringAsync();
            html.Should().Contain("Lalab Mobile");
            html.Should().Contain("manifest.json");
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _webServer.Dispose();
        _serviceProvider.Dispose();
    }

    #region Mock Implementations for Test

    private class InMemorySettingsRepository : ISettingsRepository
    {
        private AppSettings _settings;
        public InMemorySettingsRepository(AppSettings settings) => _settings = settings;
        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }



    private class MockOrderRepository : IOrderRepository
    {
        public readonly Dictionary<long, Order> Orders = new();
        public Task DeleteOrderAsync(long orderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GetMaxOrderSequenceForDateAsync(string workDate, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task<Order?> GetOrderByCodeAsync(string orderCode, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetOrderByIdAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(Orders.TryGetValue(id, out var o) ? o : null);
        public Task<Order?> GetOrderByRelativePathAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<Order?> GetOrderByRootAndRelativePathAsync(long rootFolderId, string relativePath, CancellationToken cancellationToken = default) => Task.FromResult<Order?>(null);
        public Task<int> GetOrderCountByRootFolderIdAsync(long rootFolderId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Order>> GetOrdersByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyList<Order>> GetOrdersByDateAndRootAsync(string date, long rootFolderId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyList<Order>> GetOrdersByDateAsync(string date, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyList<Order>> GetOrdersByDateRangeAsync(string startDate, string endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());
        public Task<IReadOnlyList<ScanSnapshot>> GetScanSnapshotsForOrderAsync(long orderId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScanSnapshot>>(Array.Empty<ScanSnapshot>());
        public Task<IReadOnlyList<string>> GetScannedDatesInMonthAsync(string yearMonth, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task SaveOrderAsync(Order order, ScanSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetCustomerBillIdForItemsAsync(IEnumerable<long> orderItemIds, long? customerBillId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFilesystemChangedAfterLockAsync(long orderId, bool changed, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateFolderCustomerIdAsync(string workDate, string originalFolderName, long? customerId, string? orderName = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderCustomerIdAsync(long orderId, long customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderItemPrintFolderAsync(long orderItemId, string printFolderRelativePath, int printCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderItemResolutionAsync(long orderItemId, int billQuantity, QuantityResolutionMode mode, string? note, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderPrintedStatusAsync(long orderId, bool isPrinted, DateTimeOffset? printedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderPrintedStatusByRelativePathAsync(string relativePath, bool isPrinted, DateTimeOffset? printedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderPrintProgressAsync(long orderId, PrintStatus progress, bool isPrinted, DateTimeOffset? printedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderItemPrintedStatusAsync(long orderItemId, bool isPrinted, DateTimeOffset? printedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateOrderDeliveredStatusAsync(long orderId, bool isDelivered, DateTimeOffset? deliveredAt, string? deliveredBy = null, CancellationToken cancellationToken = default)
        {
            if (Orders.TryGetValue(orderId, out var o))
            {
                o.IsDelivered = isDelivered;
                o.DeliveredAt = deliveredAt;
                o.DeliveredBy = deliveredBy;
            }
            return Task.CompletedTask;
        }
        public Task UpdateOrderNoteAsync(long orderId, string? note, CancellationToken cancellationToken = default)
        {
            if (Orders.TryGetValue(orderId, out var o))
            {
                o.Note = note;
            }
            return Task.CompletedTask;
        }
        public Task UpdateOrderStatusAsync(long orderId, OrderStatus status, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class MockCustomerBillRepository : ICustomerBillRepository
    {
        public Dictionary<long, CustomerBill> Bills { get; } = new();

        public Task DeleteBillAsync(long billId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteDraftBillAsync(long billId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GenerateNextBillNumberAsync(string date, CancellationToken cancellationToken = default) => Task.FromResult("BILL-001");
        public Task<CustomerBill?> GetActiveDraftByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default) => Task.FromResult<CustomerBill?>(null);
        public Task<IReadOnlyList<CustomerBill>> GetAllBillsAsync(BillType? typeFilter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerBill>>(Array.Empty<CustomerBill>());
        public Task<IReadOnlyList<CustomerBill>> GetBillsByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerBill>>(Array.Empty<CustomerBill>());
        public Task<IReadOnlyList<CustomerBill>> GetBillsByDateAsync(string date, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerBill>>(Array.Empty<CustomerBill>());
        public Task<IReadOnlyList<CustomerBill>> GetBillsByDateRangeAsync(string startDate, string endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerBill>>(Array.Empty<CustomerBill>());
        public Task<IReadOnlyList<CustomerBill>> GetBillsByMonthAsync(string yearMonth, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerBill>>(Array.Empty<CustomerBill>());
        public Task<IReadOnlyList<CustomerBill>> GetBillsBySourceFolderPathAsync(string normalizedFolderPath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerBill>>(Array.Empty<CustomerBill>());
        public Task<CustomerBill?> GetByBillNumberAsync(string billNumber, CancellationToken cancellationToken = default) => Task.FromResult<CustomerBill?>(null);
        public Task<CustomerBill?> GetByIdAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(Bills.GetValueOrDefault(id));
        public Task<long> GetCustomerTotalDebtAsync(long customerId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<CustomerBill?> GetLastLockedOrExportedBillByCustomerIdAsync(long customerId, CancellationToken cancellationToken = default) => Task.FromResult<CustomerBill?>(null);
        public Task<CustomerBill?> GetLockedBillByOrderIdAsync(long orderId, CancellationToken cancellationToken = default) => Task.FromResult<CustomerBill?>(null);
        public Task<IReadOnlyList<GuestBillSourceFolder>> GetSourceFoldersByBillIdAsync(long billId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuestBillSourceFolder>>(Array.Empty<GuestBillSourceFolder>());
        public Task<IReadOnlyList<CustomerBill>> GetUnpaidBillsAsync(long? customerId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerBill>>(Array.Empty<CustomerBill>());
        public Task LockCustomerBillAtomicAsync(CustomerBill bill, IReadOnlyList<long> productJobIds, IReadOnlyList<long> orderIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReopenCustomerBillAtomicAsync(CustomerBill bill, IReadOnlyList<long> productJobIds, IReadOnlyList<long> orderIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveBillAsync(CustomerBill bill, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetPaymentStatusAsync(long billId, bool isPaid, DateTimeOffset? paidAt = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class MockCustomerRepository : ICustomerRepository
    {
        public Task AddAliasAsync(long customerId, string aliasText, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Customer> CreateCustomerAsync(Customer customer, string? initialAlias = null, CancellationToken cancellationToken = default) => Task.FromResult(customer);
        public Task DeleteCustomerAsync(long customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CustomerAlias?> FindAliasByTextAsync(string aliasText, CancellationToken cancellationToken = default) => Task.FromResult<CustomerAlias?>(null);
        public Task<Customer?> FindExactMatchAsync(string normalizedName, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task<IReadOnlyList<CustomerAlias>> GetAllAliasesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerAlias>>(Array.Empty<CustomerAlias>());
        public Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Customer>>(Array.Empty<Customer>());
        public Task<Customer?> GetByIdAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(null);
        public Task<bool> HasCustomerHistoryAsync(long customerId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ReassignAliasAsync(long aliasId, long newCustomerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAliasAsync(long aliasId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAliasAsync(long aliasId, string newAliasText, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateCustomerAsync(Customer customer, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class MockReportService : IReportService
    {
        public Task<DailyReport> GetDailyReportAsync(string date, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DailyReport(date, 5, 2, 50, 1500000, 0, 5, Array.Empty<CustomerDailyAggregation>(), Array.Empty<SpecificationSummary>()));
        }

        public Task<DateRangeReport> GetDateRangeReportAsync(string startDate, string endDate, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DateRangeReport(startDate, endDate, 10, 4, 100, 3000000, 0, 10, Array.Empty<DailySummary>(), Array.Empty<CustomerMonthlySummary>(), Array.Empty<SpecificationSummary>()));
        }

        public Task<MonthlyReport> GetMonthlyReportAsync(int year, int month, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new MonthlyReport(year, month, 15, 6, 200, 6000000, 0, 15, Array.Empty<string>(), Array.Empty<DailySummary>(), Array.Empty<CustomerMonthlySummary>(), Array.Empty<SpecificationSummary>()));
        }
    }

    private class MockJpegBillExporter : IJpegBillExporter
    {
        public Task<string> ExportBillToJpegAsync(CustomerBill bill, string? destinationDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult("mock_bill.jpg");
    }

    private class MockShippingLabelExporter : IShippingLabelExporter
    {
        public int ExportBillCalls { get; set; }
        public int ExportOrderCalls { get; set; }
        public int PrintShippingLabelCalls { get; set; }
        public int PrintOrderShippingLabelCalls { get; set; }
        public bool LastSilent { get; set; }
        public CustomerBill? LastPrintedBill { get; set; }
        public Order? LastPrintedOrder { get; set; }
        public string? MockLabelPath { get; set; }

        public Task<string> ExportShippingLabelImageAsync(CustomerBill bill, string? destinationPath = null, CancellationToken cancellationToken = default)
        { ExportBillCalls++; return Task.FromResult(MockLabelPath ?? "mock_label.png"); }

        public Task PrintShippingLabelAsync(CustomerBill bill, bool silent = false, CancellationToken cancellationToken = default)
        {
            PrintShippingLabelCalls++;
            LastSilent = silent;
            LastPrintedBill = bill;
            return Task.CompletedTask;
        }

        public Task<string> ExportOrderShippingLabelImageAsync(Order order, string? destinationPath = null, CancellationToken cancellationToken = default)
        { ExportOrderCalls++; return Task.FromResult(MockLabelPath ?? "mock_order_label.png"); }

        public Task PrintOrderShippingLabelAsync(Order order, bool silent = false, CancellationToken cancellationToken = default)
        {
            PrintOrderShippingLabelCalls++;
            LastSilent = silent;
            LastPrintedOrder = order;
            return Task.CompletedTask;
        }

        public Task PrintTestSampleLabelAsync(string? printerName = null, string? labelSizeOverride = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class MockThumbnailService : IThumbnailService
    {
        public Task<ThumbnailResult> GetThumbnailAsync(string fullSourcePath, CancellationToken cancellationToken = default)
            => Task.FromResult(ThumbnailResult.SuccessResult(fullSourcePath));
        public void Pause() { }
        public void Resume() { }
        public void ClearMemoryCache() { }
        public void ClearDiskCache() { }
        public long GetDiskCacheSizeBytes() => 1024;
        public void CleanupExpiredDiskCache(TimeSpan maxAge) { }
        public Task TouchLastUsedAsync(string fullSourcePath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task TouchLastUsedBatchAsync(IEnumerable<string> fullSourcePaths, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ThumbnailCleanupSummary> CleanupExpiredDiskCacheAsync(TimeSpan maxAge, CancellationToken cancellationToken = default) => Task.FromResult(new ThumbnailCleanupSummary());
        public Task<ThumbnailClearSummary> ClearAllDiskCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ThumbnailClearSummary());
    }

    private class MockOrderGalleryService : IOrderGalleryService
    {
        public Task<OrderGalleryManifest> GetOrderManifestAsync(long orderId, CancellationToken cancellationToken = default)
        {
            var manifest = new OrderGalleryManifest
            {
                OrderId = orderId,
                OrderCode = "DH-TEST-001",
                ManifestVersion = "vtest123",
                TotalImageCount = 2,
                Groups = new()
                {
                    new OrderGalleryGroup
                    {
                        OrderItemId = 1,
                        SpecificationName = "13x18",
                        FolderName = "13x18",
                        IsFolderResolved = true,
                        Items = new()
                        {
                            new OrderGalleryItem { FileId = "f1", FileName = "01.jpg", FileSizeBytes = 1000, Status = "Ready" },
                            new OrderGalleryItem { FileId = "f2", FileName = "02.jpg", FileSizeBytes = 2000, Status = "Ready" }
                        }
                    }
                }
            };
            return Task.FromResult(manifest);
        }

        public Task<OrderGalleryPage> GetOrderGalleryPageAsync(long orderId, string manifestVersion, int offset, int limit, CancellationToken cancellationToken = default)
        {
            var page = new OrderGalleryPage
            {
                OrderId = orderId,
                ManifestVersion = manifestVersion,
                Offset = offset,
                Limit = limit,
                TotalCount = 2,
                HasMore = false,
                Items = new()
                {
                    new OrderGalleryItemDto { FileId = "f1", FileName = "01.jpg", FileSizeBytes = 1000, OrderItemId = 1, SpecificationName = "13x18", Status = "Ready" },
                    new OrderGalleryItemDto { FileId = "f2", FileName = "02.jpg", FileSizeBytes = 2000, OrderItemId = 1, SpecificationName = "13x18", Status = "Ready" }
                }
            };
            return Task.FromResult(page);
        }

        public Task<ThumbnailResult> GetGalleryThumbnailAsync(long orderId, string manifestVersion, string fileId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ThumbnailResult.SuccessResult("dummy_cached.jpg"));
        }

        public Task TouchViewedBatchAsync(long orderId, string manifestVersion, IEnumerable<string> fileIds, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    #endregion

    [Fact]
    public async Task GetOrderThumbnail_Unauthorized_ShouldReturn401()
    {
        await _webServer.StartAsync();

        try
        {
            var response = await _httpClient.GetAsync("/api/orders/1/thumbnail");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task GetOrderThumbnail_Authorized_ValidImage_ShouldReturn200AndCacheHeader()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MobileThumbTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string imageFile = Path.Combine(tempDir, "sample.jpg");
        await File.WriteAllBytesAsync(imageFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 });

        _settings.RootFolder = tempDir;

        var order = new Order
        {
            Id = 99,
            WorkDate = "2026-10-01",
            RelativePath = "Order_99",
            ThumbnailCandidateRelativePath = "sample.jpg"
        };
        _orderRepo.Orders[99] = order;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string staffToken = GetToken(MobileUserRole.Staff);

            var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders/99/thumbnail");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);

            var response = await _httpClient.SendAsync(req);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
            response.Headers.CacheControl?.Public.Should().BeTrue();
            response.Headers.CacheControl?.MaxAge.Should().Be(TimeSpan.FromSeconds(86400));

            // Test with ?token= query parameter (img src pattern)
            var resToken = await _httpClient.GetAsync($"/api/orders/99/thumbnail?token={staffToken}");
            resToken.StatusCode.Should().Be(HttpStatusCode.OK);

            // Test with ?auth= query parameter (backwards compatibility)
            var resAuth = await _httpClient.GetAsync($"/api/orders/99/thumbnail?auth={staffToken}");
            resAuth.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await _webServer.StopAsync();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task GetOrderThumbnail_AutoHealing_WhenCandidatePathIsNull_ShouldResolveFromFolder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MobileAutoHealTest_" + Guid.NewGuid().ToString("N"));
        string orderFolder = Path.Combine(tempDir, "2026-10-01", "Order_AutoHeal", "13x18");
        Directory.CreateDirectory(orderFolder);
        string imageFile = Path.Combine(orderFolder, "heal_001.jpg");
        await File.WriteAllBytesAsync(imageFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 });

        _settings.RootFolder = tempDir;

        var order = new Order
        {
            Id = 100,
            WorkDate = "2026-10-01",
            RelativePath = Path.Combine("2026-10-01", "Order_AutoHeal"),
            ThumbnailCandidateRelativePath = null
        };
        _orderRepo.Orders[100] = order;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string staffToken = GetToken(MobileUserRole.Staff);

            var response = await _httpClient.GetAsync($"/api/orders/100/thumbnail?token={staffToken}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        }
        finally
        {
            await _webServer.StopAsync();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ToggleDelivered_Staff_ShouldMarkDeliveredWithStaffRole()
    {
        var order = new Order
        {
            Id = 201,
            WorkDate = "2026-10-01",
            RelativePath = "Order_201",
            IsDelivered = false
        };
        _orderRepo.Orders[201] = order;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string staffToken = GetToken(MobileUserRole.Staff);

            var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders/201/toggle-delivered");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);

            var response = await _httpClient.SendAsync(req);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("isDelivered").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("deliveredBy").GetString().Should().Be("Nhân viên");
            doc.RootElement.GetProperty("deliveredAt").GetString().Should().NotBeNullOrWhiteSpace();

            order.IsDelivered.Should().BeTrue();
            order.DeliveredBy.Should().Be("Nhân viên");
            order.DeliveredAt.Should().NotBeNull();
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task ToggleDelivered_Admin_ShouldMarkDeliveredWithAdminRole()
    {
        var order = new Order
        {
            Id = 202,
            WorkDate = "2026-10-01",
            RelativePath = "Order_202",
            IsDelivered = false
        };
        _orderRepo.Orders[202] = order;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string adminToken = GetToken(MobileUserRole.Admin);

            var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders/202/toggle-delivered");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

            var response = await _httpClient.SendAsync(req);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("isDelivered").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("deliveredBy").GetString().Should().Be("Chủ tiệm");

            // Toggling again should revert to false and clear deliveredBy / deliveredAt
            var reqUndo = new HttpRequestMessage(HttpMethod.Post, "/api/orders/202/toggle-delivered");
            reqUndo.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

            var resUndo = await _httpClient.SendAsync(reqUndo);
            resUndo.StatusCode.Should().Be(HttpStatusCode.OK);

            string jsonUndo = await resUndo.Content.ReadAsStringAsync();
            using var docUndo = JsonDocument.Parse(jsonUndo);
            docUndo.RootElement.GetProperty("isDelivered").GetBoolean().Should().BeFalse();
            docUndo.RootElement.GetProperty("deliveredBy").ValueKind.Should().Be(JsonValueKind.Null);
            docUndo.RootElement.GetProperty("deliveredAt").ValueKind.Should().Be(JsonValueKind.Null);

            order.IsDelivered.Should().BeFalse();
            order.DeliveredBy.Should().BeNull();
            order.DeliveredAt.Should().BeNull();
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task UpdateOrderNote_ShouldSaveAndRetrieveNote()
    {
        var order = new Order
        {
            Id = 203,
            WorkDate = "2026-10-01",
            RelativePath = "Order_203"
        };
        _orderRepo.Orders[203] = order;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string staffToken = GetToken(MobileUserRole.Staff);

            // 1. Add note
            var content = new StringContent(
                JsonSerializer.Serialize(new { note = "thiếu 1 tấm 13x18, giao gấp buổi chiều" }),
                Encoding.UTF8,
                "application/json"
            );
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders/203/note") { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);

            var response = await _httpClient.SendAsync(req);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("note").GetString().Should().Be("thiếu 1 tấm 13x18, giao gấp buổi chiều");
            order.Note.Should().Be("thiếu 1 tấm 13x18, giao gấp buổi chiều");

            // 2. Clear note by sending whitespace
            var emptyContent = new StringContent(
                JsonSerializer.Serialize(new { note = "   " }),
                Encoding.UTF8,
                "application/json"
            );
            var reqClear = new HttpRequestMessage(HttpMethod.Post, "/api/orders/203/note") { Content = emptyContent };
            reqClear.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);

            var resClear = await _httpClient.SendAsync(reqClear);
            resClear.StatusCode.Should().Be(HttpStatusCode.OK);

            string jsonClear = await resClear.Content.ReadAsStringAsync();
            using var docClear = JsonDocument.Parse(jsonClear);
            docClear.RootElement.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
            order.Note.Should().BeNull();
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task PrintOrderLabel_UnbilledOrder_ShouldCallPrinterAndAutoMarkDelivered()
    {
        var order = new Order
        {
            Id = 301,
            WorkDate = "2026-10-01",
            OrderCode = "ORD-301",
            OriginalFolderName = "Studio_Phong",
            IsDelivered = false
        };
        _orderRepo.Orders[301] = order;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string staffToken = GetToken(MobileUserRole.Staff);

            var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders/301/print-label");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);

            var response = await _httpClient.SendAsync(req);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("isDelivered").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("deliveredBy").GetString().Should().Be("Nhân viên (Mobile)");

            // Verify printer called
            _labelExporter.PrintOrderShippingLabelCalls.Should().Be(1);
            _labelExporter.LastSilent.Should().BeTrue();
            _labelExporter.LastPrintedOrder?.Id.Should().Be(301);

            // Verify order state in repo updated
            order.IsDelivered.Should().BeTrue();
            order.DeliveredBy.Should().Be("Nhân viên (Mobile)");
            order.DeliveredAt.Should().NotBeNull();
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task PrintOrderLabel_BilledOrder_ShouldCallPrinterForBillAndAutoMarkDelivered()
    {
        var bill = new CustomerBill
        {
            Id = 401,
            BillNumber = "BILL-401",
            CustomerNameSnapshot = "Studio Kim",
            Status = CustomerBillStatus.Draft
        };
        _billRepo.Bills[401] = bill;

        var order = new Order
        {
            Id = 302,
            WorkDate = "2026-10-01",
            OrderCode = "ORD-302",
            OriginalFolderName = "Studio_Kim",
            IsDelivered = false,
            Items = new System.Collections.Generic.List<OrderItemScan>
            {
                new OrderItemScan
                {
                    Id = 1,
                    OrderId = 302,
                    CustomerBillId = 401
                }
            }
        };
        _orderRepo.Orders[302] = order;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string adminToken = GetToken(MobileUserRole.Admin);

            var req = new HttpRequestMessage(HttpMethod.Post, "/api/orders/302/print-label");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

            var response = await _httpClient.SendAsync(req);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("isDelivered").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("deliveredBy").GetString().Should().Be("Chủ tiệm (Mobile)");

            // Verify printer called for the bill
            _labelExporter.PrintShippingLabelCalls.Should().Be(1);
            _labelExporter.LastSilent.Should().BeTrue();
            _labelExporter.LastPrintedBill?.Id.Should().Be(401);

            // Verify order state updated
            order.IsDelivered.Should().BeTrue();
            order.DeliveredBy.Should().Be("Chủ tiệm (Mobile)");
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task PrintBillLabel_ShouldCallPrinterAndAutoMarkIncludedOrdersDelivered()
    {
        var orderA = new Order { Id = 501, WorkDate = "2026-10-01", IsDelivered = false };
        var orderB = new Order { Id = 502, WorkDate = "2026-10-01", IsDelivered = false };
        _orderRepo.Orders[501] = orderA;
        _orderRepo.Orders[502] = orderB;

        var bill = new CustomerBill
        {
            Id = 601,
            BillNumber = "BILL-601",
            CustomerNameSnapshot = "Khách Hàng Vip",
            Orders = new System.Collections.Generic.List<CustomerBillOrder>
            {
                new CustomerBillOrder { OrderId = 501, IsIncluded = true },
                new CustomerBillOrder { OrderId = 502, IsIncluded = true },
                new CustomerBillOrder { OrderId = 503, IsIncluded = false } // Not included
            }
        };
        _billRepo.Bills[601] = bill;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string staffToken = GetToken(MobileUserRole.Admin);

            var req = new HttpRequestMessage(HttpMethod.Post, "/api/bills/601/print");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);

            var response = await _httpClient.SendAsync(req);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            // Printer should be called silently
            _labelExporter.PrintShippingLabelCalls.Should().Be(1);
            _labelExporter.LastSilent.Should().BeTrue();
            _labelExporter.LastPrintedBill?.Id.Should().Be(601);

            // Both included orders should now be delivered
            orderA.IsDelivered.Should().BeTrue();
            orderA.DeliveredBy.Should().Be("Chủ tiệm (Mobile)");
            orderB.IsDelivered.Should().BeTrue();
            orderB.DeliveredBy.Should().Be("Chủ tiệm (Mobile)");
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task GetOrderLabel_ShouldReturnOrderLabelPng()
    {
        string tempPng = Path.Combine(Path.GetTempPath(), $"order_label_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempPng, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        _labelExporter.MockLabelPath = tempPng;

        var order = new Order
        {
            Id = 701,
            WorkDate = "2026-10-01",
            OrderCode = "ORD-701",
            OriginalFolderName = "Test_Label_Preview"
        };
        _orderRepo.Orders[701] = order;

        await _webServer.StartAsync();

        try
        {
            var authService = _serviceProvider.GetRequiredService<IMobileAuthService>();
            string staffToken = GetToken(MobileUserRole.Staff);

            var req = new HttpRequestMessage(HttpMethod.Get, "/api/orders/701/label");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);

            var response = await _httpClient.SendAsync(req);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");

            byte[] bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Length.Should().Be(8);
        }
        finally
        {
            await _webServer.StopAsync();
            try { File.Delete(tempPng); } catch { }
        }
    }

    [Fact]
    public async Task GalleryRequests_Unauthorized_Returns401()
    {
        await _webServer.StartAsync();
        try
        {
            var response = await _httpClient.PostAsync("/api/orders/1/gallery/requests", null);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task GalleryRequests_Authorized_ReturnsManifest200()
    {
        await _webServer.StartAsync();
        try
        {
            var token = GetToken(MobileUserRole.Staff);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.PostAsync("/api/orders/1/gallery/requests", null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = await response.Content.ReadAsStringAsync();
            json.Should().Contain("vtest123");
            json.Should().Contain("DH-TEST-001");
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task GalleryPage_Authorized_ReturnsPage200()
    {
        await _webServer.StartAsync();
        try
        {
            var token = GetToken(MobileUserRole.Staff);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.GetAsync("/api/orders/1/gallery?version=vtest123&offset=0&limit=24");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = await response.Content.ReadAsStringAsync();
            json.Should().Contain("01.jpg");
            json.Should().Contain("02.jpg");
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task GalleryTouch_Authorized_ReturnsSuccess200()
    {
        await _webServer.StartAsync();
        try
        {
            var token = GetToken(MobileUserRole.Staff);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payload = new StringContent(
                JsonSerializer.Serialize(new { version = "vtest123", fileIds = new[] { "f1", "f2" } }),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("/api/orders/1/gallery/touch", payload);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }

    [Fact]
    public async Task SettingsThumbnailsCleanup_StaffForbidden_AdminAllowed()
    {
        await _webServer.StartAsync();
        try
        {
            var staffToken = GetToken(MobileUserRole.Staff);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);

            var staffRes = await _httpClient.PostAsync("/api/settings/thumbnails/cleanup", null);
            staffRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var adminToken = GetToken(MobileUserRole.Admin);
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

            var adminRes = await _httpClient.PostAsync("/api/settings/thumbnails/cleanup", null);
            adminRes.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await _webServer.StopAsync();
        }
    }
}
