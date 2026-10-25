using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Auth;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LalabAutoReport.Tests;

public class LegacyPwaAdapterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<IMobileWebServer> _servers = new();

    public LegacyPwaAdapterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lalab_legacy_tests_" + Guid.NewGuid().ToString("N"));
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

    private async Task<(ServiceProvider Provider, IMobileWebServer Server, HttpClient Client, string Token, Order SeedOrder)> CreateServerAsync()
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
            // Standalone profile: EnabledWriteCapabilities is null (all allowed)
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
            OriginalFolderName = "LegacyCustomer",
            RelativePath = "2026-10-26\\LegacyCustomer",
            Status = OrderStatus.Scanned,
            IsDelivered = false,
            IsPrinted = false,
            Note = "Initial legacy note"
        };
        await orderRepo.SaveOrderAsync(order, new ScanSnapshot { StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow });

        var server = provider.GetRequiredService<IMobileWebServer>();
        await server.StartAsync();
        _servers.Add(server);

        var client = new HttpClient { BaseAddress = new Uri(server.LocalUrl!) };

        // Login via legacy endpoint
        var loginResp = await client.PostAsync("/api/auth/login", new StringContent(
            JsonSerializer.Serialize(new { pin = "888888" }), Encoding.UTF8, "application/json"));
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
        string token = loginDoc.RootElement.GetProperty("token").GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return (provider, server, client, token, order);
    }

    [Fact]
    public async Task LegacyToggle_RoutesThroughHandler_WritesReceipt()
    {
        var (provider, _, client, _, order) = await CreateServerAsync();

        // 1. Toggle Delivered
        var respDelivered = await client.PostAsync($"/api/orders/{order.Id}/toggle-delivered", new StringContent(""));
        respDelivered.StatusCode.Should().Be(HttpStatusCode.OK);
        using var delDoc = JsonDocument.Parse(await respDelivered.Content.ReadAsStringAsync());
        delDoc.RootElement.GetProperty("id").GetInt64().Should().Be(order.Id);
        delDoc.RootElement.GetProperty("isDelivered").GetBoolean().Should().BeTrue();
        delDoc.RootElement.GetProperty("deliveredBy").GetString().Should().Be("Chủ tiệm");

        // 2. Toggle Printed
        var respPrinted = await client.PostAsync($"/api/orders/{order.Id}/toggle-printed", new StringContent(""));
        respPrinted.StatusCode.Should().Be(HttpStatusCode.OK);
        using var printDoc = JsonDocument.Parse(await respPrinted.Content.ReadAsStringAsync());
        printDoc.RootElement.GetProperty("id").GetInt64().Should().Be(order.Id);
        printDoc.RootElement.GetProperty("isPrinted").GetBoolean().Should().BeTrue();

        // 3. Verify DB writes: command_receipts, audit_log, change_journal
        var connectionFactory = provider.GetRequiredService<ISqliteConnectionFactory>();
        using var conn = connectionFactory.CreateConnection();

        int receiptsCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM command_receipts WHERE entity_id = @Id;", new { Id = order.Id });
        receiptsCount.Should().Be(2);

        int auditCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM audit_log WHERE entity_id = @Id;", new { Id = order.Id });
        auditCount.Should().Be(2);

        int journalCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM change_journal WHERE entity_id = @Id;", new { Id = order.Id });
        journalCount.Should().Be(2);
    }

    [Fact]
    public async Task LegacyNote_SameShapeAsPhase1()
    {
        var (provider, _, client, _, order) = await CreateServerAsync();

        var noteContent = new StringContent(
            JsonSerializer.Serialize(new { note = "Legacy note content" }),
            Encoding.UTF8, "application/json");

        var resp = await client.PostAsync($"/api/orders/{order.Id}/note", noteContent);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetInt64().Should().Be(order.Id);
        doc.RootElement.GetProperty("note").GetString().Should().Be("Legacy note content");
        doc.RootElement.GetProperty("syncState").GetString().Should().Be("pending");

        // Verify receipt written
        var connectionFactory = provider.GetRequiredService<ISqliteConnectionFactory>();
        using var conn = connectionFactory.CreateConnection();
        int receiptsCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM command_receipts WHERE entity_id = @Id AND capability = @Cap;",
            new { Id = order.Id, Cap = Capabilities.OrdersEditNote });
        receiptsCount.Should().Be(1);
    }

    [Fact]
    public async Task LegacyRead_IncludesRevisionFields()
    {
        var (_, _, client, _, order) = await CreateServerAsync();

        // 1. GET /api/orders
        var listResp = await client.GetAsync($"/api/orders?date={order.WorkDate}");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var firstOrder = listDoc.RootElement.EnumerateArray().First();
        firstOrder.TryGetProperty("deliveredRevision", out var delRevElem).Should().BeTrue();
        delRevElem.GetInt64().Should().Be(0);
        firstOrder.TryGetProperty("printedRevision", out var printRevElem).Should().BeTrue();
        printRevElem.GetInt64().Should().Be(0);
        firstOrder.TryGetProperty("noteRevision", out var noteRevElem).Should().BeTrue();
        noteRevElem.GetInt64().Should().Be(0);

        // 2. GET /api/orders/{id}
        var getResp = await client.GetAsync($"/api/orders/{order.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync());
        getDoc.RootElement.TryGetProperty("deliveredRevision", out var dRev).Should().BeTrue();
        dRev.GetInt64().Should().Be(0);
        getDoc.RootElement.TryGetProperty("printedRevision", out var pRev).Should().BeTrue();
        pRev.GetInt64().Should().Be(0);
        getDoc.RootElement.TryGetProperty("noteRevision", out var nRev).Should().BeTrue();
        nRev.GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task LegacyToggle_StaleReturns409LegacyShape()
    {
        var (_, _, client, _, order) = await CreateServerAsync();

        // Launch concurrent note updates with different content
        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 15; i++)
        {
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                var content = new StringContent(
                    JsonSerializer.Serialize(new { note = $"Concurrent note {index}" }),
                    Encoding.UTF8, "application/json");
                return await client.PostAsync($"/api/orders/{order.Id}/note", content);
            }));
        }

        var responses = await Task.WhenAll(tasks);
        // Under 15 concurrent different note requests, at least some succeed and at least some get 409 STALE_REVISION
        var staleResponses = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        staleResponses.Should().NotBeEmpty("concurrent distinct note edits must produce stale revision conflicts");

        var firstStale = staleResponses.First();
        string body = await firstStale.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("code").GetString().Should().Be(ApiErrorCodes.StaleRevision);
        doc.RootElement.GetProperty("message").GetString().Should().Be("Đơn vừa được cập nhật trên thiết bị khác. Vui lòng tải lại.");
    }
}
