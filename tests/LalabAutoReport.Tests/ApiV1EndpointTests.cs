using System.Net.Http.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Auth;
using LalabAutoReport.Contracts.Commands;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.Web;
using LalabAutoReport.Server.Health;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LalabAutoReport.Tests;

public class ApiV1EndpointTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<IMobileWebServer> _servers = new();

    public ApiV1EndpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lalab_apiv1_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var server in _servers)
        {
            try { server.StopAsync().GetAwaiter().GetResult(); } catch { }
        }
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<(ServiceProvider Provider, IMobileWebServer Server, HttpClient Client, string AdminToken, string StaffToken, Order SeedOrder)> CreateServerAsync(
        IReadOnlySet<string>? enabledCapabilities = null,
        bool readOnlyMode = false)
    {
        int port = GetAvailablePort();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLalabCore(options =>
        {
            options.DataDirectory = _tempDir;
            options.ThumbnailsDirectory = Path.Combine(_tempDir, "Thumbnails");
            options.EnableCloudSync = false;
            options.EnableMobileWebServer = true;
            options.MobileWebServerLoopbackOnly = true;
            options.MobileWebServerPort = port;
            if (enabledCapabilities != null)
            {
                options.MobileWebServerEnabledWriteCapabilities = enabledCapabilities;
            }
            else if (readOnlyMode)
            {
                options.MobileWebServerReadOnlyMode = true;
            }
        });

        var provider = services.BuildServiceProvider();
        var migrator = provider.GetRequiredService<IDatabaseMigrator>();
        await migrator.MigrateAsync();

        var settingsRepo = provider.GetRequiredService<ISettingsRepository>();
        var settings = await settingsRepo.GetSettingsAsync();
        settings.AdminPin = "888888";
        settings.StaffPin = "111111";
        settings.WorkshopName = "Lalab Test Workshop";
        await settingsRepo.SaveSettingsAsync(settings);

        var orderRepo = provider.GetRequiredService<IOrderRepository>();
        var order = new Order
        {
            WorkDate = "2026-10-26",
            OriginalFolderName = "TestCustomer",
            RelativePath = "2026-10-26\\TestCustomer",
            Status = OrderStatus.Scanned,
            IsDelivered = false,
            IsPrinted = false,
            Note = "Initial note"
        };
        await orderRepo.SaveOrderAsync(order, new ScanSnapshot { StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow });

        var server = provider.GetRequiredService<IMobileWebServer>();
        await server.StartAsync();
        _servers.Add(server);

        var client = new HttpClient { BaseAddress = new Uri(server.LocalUrl!) };

        // Login as Admin
        var adminLoginResp = await client.PostAsync("/api/v1/auth/sessions", new StringContent(
            JsonSerializer.Serialize(new { pin = "888888", clientKind = "pwa" }), Encoding.UTF8, "application/json"));
        adminLoginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminLogin = await adminLoginResp.Content.ReadFromJsonAsync<LoginResponseDto>();

        // Login as Staff
        var staffLoginResp = await client.PostAsync("/api/v1/auth/sessions", new StringContent(
            JsonSerializer.Serialize(new { pin = "111111", clientKind = "pwa" }), Encoding.UTF8, "application/json"));
        staffLoginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var staffLogin = await staffLoginResp.Content.ReadFromJsonAsync<LoginResponseDto>();

        return (provider, server, client, adminLogin!.Token!, staffLogin!.Token!, order);
    }

    [Fact]
    public async Task ApiV1_CommandWithoutClientApiHeader_Returns426()
    {
        var (_, _, client, adminToken, _, order) = await CreateServerAsync(enabledCapabilities: null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var content = new StringContent(JsonSerializer.Serialize(new
        {
            operationId = Guid.NewGuid().ToString("N"),
            expectedRevision = 0,
            delivered = true
        }), Encoding.UTF8, "application/json");

        // Missing X-Lalab-Client-Api
        var resp = await client.PostAsync($"/api/v1/orders/{order.Id}/delivered", content);
        resp.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);

        var body = await resp.Content.ReadFromJsonAsync<ApiErrorResponse>();
        body!.Code.Should().Be(ApiErrorCodes.ClientTooOld);
    }

    [Fact]
    public async Task ApiV1_CommandWithOlderOrNewerClientApiHeader_Returns426()
    {
        var (_, _, client, adminToken, _, order) = await CreateServerAsync(enabledCapabilities: null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Older major version: 0.9 -> CLIENT_TOO_OLD
        var reqOlder = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Id}/delivered")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = 0,
                delivered = true
            }), Encoding.UTF8, "application/json")
        };
        reqOlder.Headers.Add("X-Lalab-Client-Api", "0.9");
        var respOlder = await client.SendAsync(reqOlder);
        respOlder.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);
        var bodyOlder = await respOlder.Content.ReadFromJsonAsync<ApiErrorResponse>();
        bodyOlder!.Code.Should().Be(ApiErrorCodes.ClientTooOld);

        // Newer major version: 2.0 -> SERVER_TOO_OLD
        var reqNewer = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Id}/delivered")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = 0,
                delivered = true
            }), Encoding.UTF8, "application/json")
        };
        reqNewer.Headers.Add("X-Lalab-Client-Api", "2.0");
        var respNewer = await client.SendAsync(reqNewer);
        respNewer.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);
        var bodyNewer = await respNewer.Content.ReadFromJsonAsync<ApiErrorResponse>();
        bodyNewer!.Code.Should().Be(ApiErrorCodes.ServerTooOld);
    }

    [Fact]
    public async Task ApiV1_NoCorsHeaders()
    {
        var (_, _, client, _, _, _) = await CreateServerAsync(enabledCapabilities: null);

        // 1. /api/v1/auth/sessions with Origin header -> must NOT have Access-Control-Allow-Origin
        var v1Req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/sessions")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { pin = "888888" }), Encoding.UTF8, "application/json")
        };
        v1Req.Headers.Add("Origin", "http://example.com");
        var v1Resp = await client.SendAsync(v1Req);
        v1Resp.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();

        // 2. Legacy /api/status with Origin header -> DOES have Access-Control-Allow-Origin
        var legacyReq = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        legacyReq.Headers.Add("Origin", "http://example.com");
        var legacyResp = await client.SendAsync(legacyReq);
        legacyResp.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
    }

    [Fact]
    public async Task ServerProfile_LanWritesNone_All503()
    {
        // empty set = none enabled
        var (_, _, client, adminToken, _, order) = await CreateServerAsync(
            enabledCapabilities: new HashSet<string>(StringComparer.Ordinal));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // /api/v1/orders/{id}/delivered -> 503
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Id}/delivered")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = 0,
                delivered = true
            }), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Lalab-Client-Api", "1.0");
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var err = await resp.Content.ReadFromJsonAsync<ApiErrorResponse>();
        err!.Code.Should().Be(ApiErrorCodes.WritesDisabledUntilPhase2);

        // Legacy /api/orders/{id}/toggle-delivered -> 503
        var legResp = await client.PostAsync($"/api/orders/{order.Id}/toggle-delivered", new StringContent(""));
        legResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var legErr = await legResp.Content.ReadFromJsonAsync<ApiErrorResponse>();
        legErr!.Code.Should().Be(ApiErrorCodes.WritesDisabledUntilPhase2);
    }

    [Fact]
    public async Task ServerProfile_LanWritesOrders_OnlyThreeEnabled_PaymentPrintMaintenanceStill503()
    {
        var ordersCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            Capabilities.OrdersSetDelivered,
            Capabilities.OrdersSetPrinted,
            Capabilities.OrdersEditNote
        };
        var (_, _, client, adminToken, _, order) = await CreateServerAsync(enabledCapabilities: ordersCapabilities);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // 1. delivered -> 200 OK
        var reqDelivered = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Id}/delivered")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = 0,
                delivered = true
            }), Encoding.UTF8, "application/json")
        };
        reqDelivered.Headers.Add("X-Lalab-Client-Api", "1.0");
        var respDelivered = await client.SendAsync(reqDelivered);
        respDelivered.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. printed -> 200 OK
        var reqPrinted = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Id}/printed")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = 0,
                printed = true
            }), Encoding.UTF8, "application/json")
        };
        reqPrinted.Headers.Add("X-Lalab-Client-Api", "1.0");
        var respPrinted = await client.SendAsync(reqPrinted);
        respPrinted.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. note -> 200 OK
        var reqNote = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Id}/note")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = Guid.NewGuid().ToString("N"),
                expectedRevision = 0,
                note = "Updated note via v1"
            }), Encoding.UTF8, "application/json")
        };
        reqNote.Headers.Add("X-Lalab-Client-Api", "1.0");
        var respNote = await client.SendAsync(reqNote);
        respNote.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Payment toggle -> 503
        var paymentResp = await client.PostAsync("/api/bills/1/toggle-payment", new StringContent(""));
        paymentResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var paymentErr = await paymentResp.Content.ReadFromJsonAsync<ApiErrorResponse>();
        paymentErr!.Code.Should().Be(ApiErrorCodes.WritesDisabledUntilPhase2);

        // 5. Bill print -> 503
        var billPrintResp = await client.PostAsync("/api/bills/1/print", new StringContent(""));
        billPrintResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // 6. Thumbnails maintenance -> 503
        var thumbResp = await client.PostAsync("/api/settings/thumbnails/cleanup", new StringContent(""));
        thumbResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task ServerProfile_UnmappedPost_FailsClosed503()
    {
        var ordersCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            Capabilities.OrdersSetDelivered
        };
        var (_, _, client, adminToken, _, _) = await CreateServerAsync(enabledCapabilities: ordersCapabilities);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Unmapped POST path
        var unmappedResp = await client.PostAsync("/api/v1/unknown-endpoint", new StringContent(""));
        unmappedResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var err = await unmappedResp.Content.ReadFromJsonAsync<ApiErrorResponse>();
        err!.Code.Should().Be(ApiErrorCodes.WritesDisabledUntilPhase2);
    }

    [Fact]
    public void Ready_ReportsLatestSchemaAndCapabilities()
    {
        var caps = new List<string> { Capabilities.OrdersSetDelivered, Capabilities.OrdersSetPrinted, Capabilities.OrdersEditNote };
        var provider = new ServerHealthInfoProvider(
            startedAtUtc: DateTimeOffset.UtcNow,
            schemaVersionProvider: () => DatabaseMigrator.LatestVersion,
            isReadyProvider: () => true,
            componentsProvider: () => new Dictionary<string, string> { ["database"] = "ready" },
            capabilitiesProvider: () => caps);

        var health = provider.GetReadyHealth();
        health.Status.Should().Be("Ready");
        health.SchemaVersion.Should().Be(DatabaseMigrator.LatestVersion);
        health.Capabilities.Should().NotBeNull();
        health.Capabilities.Should().BeEquivalentTo(caps);
    }

    [Fact]
    public async Task ApiV1_OrderCommands_OperationalState_And_OperationLookup()
    {
        var ordersCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            Capabilities.OrdersSetDelivered,
            Capabilities.OrdersSetPrinted,
            Capabilities.OrdersEditNote
        };
        var (_, _, client, adminToken, _, order) = await CreateServerAsync(enabledCapabilities: ordersCapabilities);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // 1. Initial operational state (Reads don't need X-Lalab-Client-Api)
        var stateResp = await client.GetAsync($"/api/v1/orders/{order.Id}/operational-state");
        stateResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var stateDoc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync());
        stateDoc.RootElement.GetProperty("delivered").GetProperty("value").GetBoolean().Should().BeFalse();
        stateDoc.RootElement.GetProperty("delivered").GetProperty("revision").GetInt64().Should().Be(0);
        stateDoc.RootElement.GetProperty("printed").GetProperty("value").GetBoolean().Should().BeFalse();
        stateDoc.RootElement.GetProperty("note").GetProperty("value").GetString().Should().Be("Initial note");

        // 2. Execute SetDelivered
        string opId = Guid.NewGuid().ToString("N");
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Id}/delivered")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = opId,
                expectedRevision = 0,
                delivered = true
            }), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Lalab-Client-Api", "1.0");
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var fieldState = await resp.Content.ReadFromJsonAsync<OrderFieldStateDto>();
        fieldState!.Changed.Should().BeTrue();
        fieldState.Revision.Should().Be(1);
        fieldState.OperationId.Should().Be(opId);

        // 3. Retry same operation (Idempotency replay)
        var reqRetry = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Id}/delivered")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                operationId = opId,
                expectedRevision = 0,
                delivered = true
            }), Encoding.UTF8, "application/json")
        };
        reqRetry.Headers.Add("X-Lalab-Client-Api", "1.0");
        var respRetry = await client.SendAsync(reqRetry);
        respRetry.StatusCode.Should().Be(HttpStatusCode.OK);
        var fieldStateRetry = await respRetry.Content.ReadFromJsonAsync<OrderFieldStateDto>();
        fieldStateRetry!.OperationId.Should().Be(opId);
        fieldStateRetry.Revision.Should().Be(1);

        // 4. Lookup operation by ID
        var lookupResp = await client.GetAsync($"/api/v1/operations/{opId}");
        lookupResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var lookupDoc = JsonDocument.Parse(await lookupResp.Content.ReadAsStringAsync());
        lookupDoc.RootElement.GetProperty("status").GetString().Should().Be("committed");
        lookupDoc.RootElement.GetProperty("capability").GetString().Should().Be(Capabilities.OrdersSetDelivered);
        lookupDoc.RootElement.GetProperty("entityId").GetInt64().Should().Be(order.Id);

        // 5. Lookup unknown operation ID
        var unknownResp = await client.GetAsync($"/api/v1/operations/{Guid.NewGuid():N}");
        unknownResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var unknownDoc = JsonDocument.Parse(await unknownResp.Content.ReadAsStringAsync());
        unknownDoc.RootElement.GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task LanWriteGate_RevokeAllSessions_WorksInReadOnlyAndOrders_And_DisabledRouteWithLegacyTokenReturns503()
    {
        var ordersCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            Capabilities.OrdersSetDelivered,
            Capabilities.OrdersSetPrinted,
            Capabilities.OrdersEditNote
        };

        var (provider, _, client, adminToken, staffToken, order) = await CreateServerAsync(enabledCapabilities: ordersCapabilities);
        var authService = provider.GetRequiredService<IMobileAuthService>();
        string legacyToken = authService.GenerateToken(MobileUserRole.Admin);

        // 1. Staff calling POST /api/v1/admin/sessions/revoke-all -> 403 Forbidden (not 503)
        var staffRevokeReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/sessions/revoke-all")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        staffRevokeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        var staffRevokeResp = await client.SendAsync(staffRevokeReq);
        staffRevokeResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 2. Admin calling POST /api/v1/admin/sessions/revoke-all -> 200 OK (not 503)
        var adminRevokeReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/sessions/revoke-all")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        adminRevokeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var adminRevokeResp = await client.SendAsync(adminRevokeReq);
        adminRevokeResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. Disabled route with legacy token -> 503 Service Unavailable (ordering check)
        var disabledReq = new HttpRequestMessage(HttpMethod.Post, "/api/bills/1/toggle-payment")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        disabledReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
        var disabledResp = await client.SendAsync(disabledReq);
        disabledResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var disabledBody = await disabledResp.Content.ReadAsStringAsync();
        disabledBody.Should().Contain(ApiErrorCodes.WritesDisabledUntilPhase2);

        // 4. Enabled route with legacy token -> 401 Session Upgrade Required
        var enabledReq = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{order.Id}/toggle-delivered")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        enabledReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
        var enabledResp = await client.SendAsync(enabledReq);
        enabledResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var enabledBody = await enabledResp.Content.ReadAsStringAsync();
        enabledBody.Should().Contain(ApiErrorCodes.SessionUpgradeRequired);
    }

    [Fact]
    public async Task LanWriteGate_InReadOnlyMode_RevokeAllWorksForAdminAndStaff()
    {
        var (_, _, client, adminToken, staffToken, _) = await CreateServerAsync(readOnlyMode: true);

        // 1. Staff calling POST /api/v1/admin/sessions/revoke-all -> 403 Forbidden (not 503)
        var staffRevokeReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/sessions/revoke-all")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        staffRevokeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", staffToken);
        var staffRevokeResp = await client.SendAsync(staffRevokeReq);
        staffRevokeResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 2. Admin calling POST /api/v1/admin/sessions/revoke-all -> 200 OK (not 503)
        var adminRevokeReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/sessions/revoke-all")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        adminRevokeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var adminRevokeResp = await client.SendAsync(adminRevokeReq);
        adminRevokeResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
