using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LalabAutoReport.Tests;

public class MobileWebServerReadOnlyTests
{
    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Theory]
    [InlineData("GET", "/api/status", true)]
    [InlineData("HEAD", "/api/status", true)]
    [InlineData("OPTIONS", "/api/orders/7/note", true)]
    [InlineData("POST", "/api/auth/login", true)]
    [InlineData("POST", "/api/auth/login/", true)]
    [InlineData("POST", "/API/AUTH/LOGIN", true)]
    [InlineData("POST", "/api/orders/7/gallery/requests", true)]
    [InlineData("POST", "/api/orders/7/gallery/requests/", true)]
    [InlineData("POST", "/api/orders/7/gallery/requests?v=1", true)]
    [InlineData("POST", "/api/orders/123/gallery/touch", true)]
    [InlineData("POST", "/api/orders/123/gallery/touch/", true)]
    [InlineData("POST", "/api/orders/7/note", false)]
    [InlineData("POST", "/api/orders/7/toggle-delivered", false)]
    [InlineData("POST", "/api/orders/7/toggle-printed", false)]
    [InlineData("POST", "/api/orders/7/print-label", false)]
    [InlineData("POST", "/api/bills/1/toggle-payment", false)]
    [InlineData("POST", "/api/settings/thumbnails/cleanup", false)]
    [InlineData("POST", "/api/settings/thumbnails/clear", false)]
    [InlineData("PUT", "/api/orders/7", false)]
    [InlineData("DELETE", "/api/orders/7", false)]
    [InlineData("PATCH", "/api/orders/7", false)]
    public void IsAllowedWritePath_EvaluatesExpectedAllowlist(string method, string path, bool expected)
    {
        KestrelMobileWebServer.IsAllowedWritePath(method, path).Should().Be(expected);
    }

    [Fact]
    public async Task KestrelMobileWebServer_InReadOnlyMode_GuardsWritesAndAllowsReads()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_ro_kestrel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        int port = GetAvailablePort();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLalabCore(options =>
        {
            options.DataDirectory = tempDir;
            options.ThumbnailsDirectory = Path.Combine(tempDir, "Thumbnails");
            options.EnableCloudSync = false;
            options.EnableMobileWebServer = true;
            options.MobileWebServerLoopbackOnly = true;
            options.MobileWebServerReadOnlyMode = true;
            options.MobileWebServerPort = port;
        });

        using var provider = services.BuildServiceProvider();
        var migrator = provider.GetRequiredService<IDatabaseMigrator>();
        await migrator.MigrateAsync();

        var settingsRepo = provider.GetRequiredService<ISettingsRepository>();
        var settings = await settingsRepo.GetSettingsAsync();
        settings.AdminPin = "888888";
        settings.StaffPin = "111111";
        await settingsRepo.SaveSettingsAsync(settings);

        var server = provider.GetRequiredService<IMobileWebServer>();
        await server.StartAsync();

        try
        {
            server.IsRunning.Should().BeTrue();
            server.LocalUrl.Should().Be($"http://127.0.0.1:{port}");

            using var client = new HttpClient { BaseAddress = new Uri(server.LocalUrl!) };

            // 1. GET /api/status -> 200 OK
            var statusResp = await client.GetAsync("/api/status");
            statusResp.StatusCode.Should().Be(HttpStatusCode.OK);
            string statusJson = await statusResp.Content.ReadAsStringAsync();
            statusJson.Should().Contain("Lalab Auto Report");

            // 2. POST /api/orders/{id}/note -> 503 WRITES_DISABLED_UNTIL_PHASE2
            var noteContent = new StringContent(
                JsonSerializer.Serialize(new { note = "test note" }),
                Encoding.UTF8,
                "application/json");
            var noteResp = await client.PostAsync("/api/orders/7/note", noteContent);
            noteResp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            string noteBody = await noteResp.Content.ReadAsStringAsync();
            noteBody.Should().Contain("WRITES_DISABLED_UNTIL_PHASE2");

            // 3. POST /api/auth/login -> Not 503 (401 Unauthorized for bad PIN, 200 OK for correct PIN)
            var badLoginContent = new StringContent(
                JsonSerializer.Serialize(new { pin = "000000" }),
                Encoding.UTF8,
                "application/json");
            var badLoginResp = await client.PostAsync("/api/auth/login", badLoginContent);
            badLoginResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var goodLoginContent = new StringContent(
                JsonSerializer.Serialize(new { pin = "888888" }),
                Encoding.UTF8,
                "application/json");
            var goodLoginResp = await client.PostAsync("/api/auth/login", goodLoginContent);
            goodLoginResp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var loginDoc = JsonDocument.Parse(await goodLoginResp.Content.ReadAsStringAsync());
            string token = loginDoc.RootElement.GetProperty("token").GetString()!;

            // 4. POST /api/orders/{id}/gallery/requests -> Not 503 (404 Not Found for non-existent order)
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var galleryReqResp = await client.PostAsync("/api/orders/7/gallery/requests", new StringContent(""));
            galleryReqResp.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
            galleryReqResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await server.StopAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch { }
        }
    }

    [Fact]
    public async Task KestrelMobileWebServer_InStandaloneMode_AllowsWriteEndpointsToProceed()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_standalone_kestrel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        int port = GetAvailablePort();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLalabCore(options =>
        {
            options.DataDirectory = tempDir;
            options.ThumbnailsDirectory = Path.Combine(tempDir, "Thumbnails");
            options.EnableCloudSync = false;
            options.EnableMobileWebServer = true;
            options.MobileWebServerLoopbackOnly = true;
            options.MobileWebServerReadOnlyMode = false; // Standalone: writes allowed
            options.MobileWebServerPort = port;
        });

        using var provider = services.BuildServiceProvider();
        var migrator = provider.GetRequiredService<IDatabaseMigrator>();
        await migrator.MigrateAsync();

        var settingsRepo = provider.GetRequiredService<ISettingsRepository>();
        var settings = await settingsRepo.GetSettingsAsync();
        settings.AdminPin = "888888";
        settings.StaffPin = "111111";
        await settingsRepo.SaveSettingsAsync(settings);

        var server = provider.GetRequiredService<IMobileWebServer>();
        await server.StartAsync();

        try
        {
            server.IsRunning.Should().BeTrue();
            using var client = new HttpClient { BaseAddress = new Uri(server.LocalUrl!) };

            // In standalone mode, POST /api/orders/7/note is NOT intercepted by read-only guard (not 503)
            // It reaches the handler and returns 401 Unauthorized because caller has no auth token
            var noteContent = new StringContent(
                JsonSerializer.Serialize(new { note = "standalone note" }),
                Encoding.UTF8,
                "application/json");
            var noteResp = await client.PostAsync("/api/orders/7/note", noteContent);
            noteResp.StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
            noteResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await server.StopAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch { }
        }
    }
}
