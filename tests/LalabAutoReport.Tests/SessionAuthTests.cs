using System;
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
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.Services;
using LalabAutoReport.Infrastructure.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LalabAutoReport.Tests;

public class SessionAuthTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseBackupService _backupService;
    private readonly SqliteRoleCredentialStore _credStore;
    private readonly SqliteSessionStore _sessionStore;
    private readonly SqliteAuditLogger _auditLogger;

    public SessionAuthTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabSessionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_session.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _backupService = new DatabaseBackupService(_connectionFactory);

        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        migrator.MigrateAsync().GetAwaiter().GetResult();

        _credStore = new SqliteRoleCredentialStore(_connectionFactory);
        _sessionStore = new SqliteSessionStore(_connectionFactory);
        _auditLogger = new SqliteAuditLogger(_connectionFactory);

        // Seed default credentials
        _credStore.SetPinAsync("Admin", "123456").GetAwaiter().GetResult();
        _credStore.SetPinAsync("Staff", "654321").GetAwaiter().GetResult();
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

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private (KestrelMobileWebServer Server, HttpClient Client, IServiceProvider Sp) CreateTestServer()
    {
        int port = GetAvailablePort();
        var services = new ServiceCollection();
        var settingsRepo = new SqliteSettingsRepository(_connectionFactory, _credStore);
        var settings = settingsRepo.GetSettingsAsync().GetAwaiter().GetResult();
        settings.MobileServerPort = port;
        settings.WorkshopName = "Test Lab";
        settingsRepo.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        var authService = new MobileAuthService();
        var rateLimiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, rateLimiter, _auditLogger);

        services.AddSingleton<ISettingsRepository>(settingsRepo);
        services.AddSingleton<IMobileAuthService>(authService);
        services.AddSingleton<ISqliteConnectionFactory>(_connectionFactory);
        services.AddSingleton<IRoleCredentialStore>(_credStore);
        services.AddSingleton<ISessionStore>(_sessionStore);
        services.AddSingleton<LoginRateLimiter>(rateLimiter);
        services.AddSingleton<IAuditLogger>(_auditLogger);
        services.AddSingleton<ISessionService>(sessionService);

        var sp = services.BuildServiceProvider();
        var server = new KestrelMobileWebServer(sp, settingsRepo, authService);
        server.StartAsync().GetAwaiter().GetResult();

        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        return (server, client, sp);
    }

    [Fact]
    public async Task Login_IssuesV2Token_SessionRowPersisted()
    {
        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger);

        var result = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "192.168.1.100");

        result.Status.Should().Be(LoginResultStatus.Success);
        result.Token.Should().NotBeNullOrEmpty();
        result.Token!.Should().StartWith("lsv2.");
        result.Role.Should().Be("Admin");

        using var conn = _connectionFactory.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth_sessions WHERE session_id = @sid", new { sid = result.SessionId });
        count.Should().Be(1);
    }

    [Fact]
    public async Task Login_LegacyEndpoint_ReturnsV2TokenSameShape()
    {
        var (server, client, _) = CreateTestServer();
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(new { pin = "123456" }), Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/auth/login", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("role").GetString().Should().Be("Admin");
            var token = doc.RootElement.GetProperty("token").GetString();
            token.Should().NotBeNullOrEmpty();
            token!.Should().StartWith("lsv2.");
            doc.RootElement.GetProperty("workshopName").GetString().Should().Be("Test Lab");
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Session_Expired_Returns401SessionExpired()
    {
        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger);

        var result = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "192.168.1.101");
        result.Token.Should().NotBeNullOrEmpty();

        // Expire session in database
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE auth_sessions SET expires_at_utc = @past WHERE session_id = @sid",
                new { past = DateTimeOffset.UtcNow.AddDays(-1).ToString("o"), sid = result.SessionId });
        }

        var actor = await sessionService.ValidateTokenAsync(result.Token!);
        actor.Should().BeNull();
    }

    [Fact]
    public async Task Session_Logout_RevokesImmediately()
    {
        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger);

        var result = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "192.168.1.102");
        
        var actor = await sessionService.ValidateTokenAsync(result.Token!);
        actor.Should().NotBeNull();

        await sessionService.RevokeSessionAsync(result.SessionId!, "user_logout");

        var actorAfter = await sessionService.ValidateTokenAsync(result.Token!);
        actorAfter.Should().BeNull();
    }

    [Fact]
    public async Task PinChange_BumpsEpoch_RevokesRoleSessions_OtherRoleUnaffected()
    {
        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger);

        var adminLogin = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "192.168.1.103");
        var staffLogin = await sessionService.LoginAsync("654321", clientKind: "pwa", ip: "192.168.1.104");

        // Change Admin PIN via credential store (which bumps epoch and revokes Admin sessions)
        await _credStore.SetPinAsync("Admin", "112233");

        var adminActor = await sessionService.ValidateTokenAsync(adminLogin.Token!);
        adminActor.Should().BeNull();

        var staffActor = await sessionService.ValidateTokenAsync(staffLogin.Token!);
        staffActor.Should().NotBeNull();
        staffActor!.Role.Should().Be("Staff");
    }

    [Fact]
    public async Task RevokeAll_RevokesEverySession_AndEndsLegacyWindow()
    {
        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger);

        var adminLogin = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "192.168.1.105");
        var staffLogin = await sessionService.LoginAsync("654321", clientKind: "pwa", ip: "192.168.1.106");

        await sessionService.RevokeAllAsync(reason: "admin_revoke_all");
        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE auth_state SET legacy_tokens_accepted_until_utc = @past WHERE id = 1",
                new { past = DateTimeOffset.UtcNow.ToString("o") });
        }

        (await sessionService.ValidateTokenAsync(adminLogin.Token!)).Should().BeNull();
        (await sessionService.ValidateTokenAsync(staffLogin.Token!)).Should().BeNull();

        var credStatus = await _credStore.GetStatusAsync();
        credStatus.LegacyTokensValidUntilUtc.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task LegacyToken_AcceptedForGetWithinWindow()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var credStore = sp.GetRequiredService<IRoleCredentialStore>();
            var credStatus = await credStore.GetStatusAsync();

            using (var conn = _connectionFactory.CreateConnection())
            {
                await conn.ExecuteAsync("UPDATE auth_state SET legacy_tokens_accepted_until_utc = @future WHERE id = 1",
                    new { future = DateTimeOffset.UtcNow.AddDays(7).ToString("o") });
            }

            var authService = sp.GetRequiredService<IMobileAuthService>();
            // Pre-cutoff legacy token (issued 1h before cutoff)
            string legacyToken = authService.GenerateToken(MobileUserRole.Staff, credStatus.LegacyTokensIssuedBeforeUtc.AddHours(-1));

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
            var response = await client.GetAsync("/api/auth/me");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("role").GetString().Should().Be("Staff");
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task LegacyToken_PostCutoff_RejectedForGet()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var credStore = sp.GetRequiredService<IRoleCredentialStore>();
            var credStatus = await credStore.GetStatusAsync();

            using (var conn = _connectionFactory.CreateConnection())
            {
                await conn.ExecuteAsync("UPDATE auth_state SET legacy_tokens_accepted_until_utc = @future WHERE id = 1",
                    new { future = DateTimeOffset.UtcNow.AddDays(7).ToString("o") });
            }

            var authService = sp.GetRequiredService<IMobileAuthService>();
            // Post-cutoff legacy token (issued 5m after cutoff)
            string postCutoffToken = authService.GenerateToken(MobileUserRole.Staff, credStatus.LegacyTokensIssuedBeforeUtc.AddMinutes(5));

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", postCutoffToken);
            var response = await client.GetAsync("/api/auth/me");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Also test a token generated NOW (which is after cutoff)
            string nowToken = authService.GenerateToken(MobileUserRole.Staff);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", nowToken);
            var responseNow = await client.GetAsync("/api/auth/me");
            responseNow.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task LegacyToken_RejectedAfterWindow()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var credStore = sp.GetRequiredService<IRoleCredentialStore>();
            var credStatus = await credStore.GetStatusAsync();

            // Expire legacy window in DB
            using (var conn = _connectionFactory.CreateConnection())
            {
                await conn.ExecuteAsync("UPDATE auth_state SET legacy_tokens_accepted_until_utc = @past WHERE id = 1",
                    new { past = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("o") });
            }

            var authService = sp.GetRequiredService<IMobileAuthService>();
            // Pre-cutoff token, but grace window has expired
            string legacyToken = authService.GenerateToken(MobileUserRole.Staff, credStatus.LegacyTokensIssuedBeforeUtc.AddHours(-1));

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
            var response = await client.GetAsync("/api/auth/me");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task LegacyToken_RejectedAfterPinChange()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var credStore = sp.GetRequiredService<IRoleCredentialStore>();
            var credStatus = await credStore.GetStatusAsync();

            var authService = sp.GetRequiredService<IMobileAuthService>();
            // Pre-cutoff token
            string legacyToken = authService.GenerateToken(MobileUserRole.Staff, credStatus.LegacyTokensIssuedBeforeUtc.AddHours(-1));

            // Change PIN resets legacy grace window
            await _credStore.SetPinAsync("Staff", "998877");

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
            var response = await client.GetAsync("/api/auth/me");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task LegacyToken_WriteReturns401SessionUpgradeRequired()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var credStore = sp.GetRequiredService<IRoleCredentialStore>();
            var credStatus = await credStore.GetStatusAsync();

            var authService = sp.GetRequiredService<IMobileAuthService>();
            string legacyToken = authService.GenerateToken(MobileUserRole.Admin, credStatus.LegacyTokensIssuedBeforeUtc.AddHours(-1));

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/orders/1/toggle-delivered", content);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.GetProperty("code").GetString().Should().Be(ApiErrorCodes.SessionUpgradeRequired);
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task LegacyToken_RejectedOnApiV1()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var credStore = sp.GetRequiredService<IRoleCredentialStore>();
            var credStatus = await credStore.GetStatusAsync();

            var authService = sp.GetRequiredService<IMobileAuthService>();
            string legacyToken = authService.GenerateToken(MobileUserRole.Admin, credStatus.LegacyTokensIssuedBeforeUtc.AddHours(-1));

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", legacyToken);
            var response = await client.GetAsync("/api/v1/auth/sessions/current");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task V2Token_UnaffectedByLegacyCutoff()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var sessionService = sp.GetRequiredService<ISessionService>();
            var loginResult = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "127.0.0.1");
            loginResult.Status.Should().Be(LoginResultStatus.Success);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Token);
            var response = await client.GetAsync("/api/auth/me");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("role").GetString().Should().Be("Admin");
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task QueryToken_AcceptedOnlyForImageGets()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var sessionService = sp.GetRequiredService<ISessionService>();
            var login = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "127.0.0.1");

            // Query token allowed on image route (returns 200 or 404 depending on order, but NOT 401)
            var response = await client.GetAsync($"/api/orders/999/thumbnail?token={login.Token}");
            response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

            // Query token NOT allowed on non-image route (returns 401)
            var forbiddenQuery = await client.GetAsync($"/api/orders?token={login.Token}");
            forbiddenQuery.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task QueryToken_RejectedForWritesAndApiV1()
    {
        var (server, client, sp) = CreateTestServer();
        try
        {
            var sessionService = sp.GetRequiredService<ISessionService>();
            var login = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "127.0.0.1");

            // Write with query token should be 401
            var writeResp = await client.PostAsync($"/api/orders/1/toggle-delivered?token={login.Token}", new StringContent("{}", Encoding.UTF8, "application/json"));
            writeResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Api v1 with query token should be 401
            var v1Resp = await client.GetAsync($"/api/v1/auth/sessions/current?token={login.Token}");
            v1Resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await server.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task NoSecrets_InAuditLogOrLogs()
    {
        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger);

        string secretPin = "741852";
        await _credStore.SetPinAsync("Admin", secretPin);
        var loginResult = await sessionService.LoginAsync(secretPin, clientKind: "pwa", ip: "192.168.1.107");

        using var conn = _connectionFactory.CreateConnection();
        var auditRows = (await conn.QueryAsync<(string? before_json, string? after_json, string? reason)>(
            "SELECT before_json, after_json, reason FROM audit_log")).AsList();

        auditRows.Should().NotBeEmpty();
        foreach (var row in auditRows)
        {
            string combined = $"{row.before_json} {row.after_json} {row.reason}";
            combined.Should().NotContain(secretPin);
            if (!string.IsNullOrEmpty(loginResult.Token))
            {
                var parts = loginResult.Token.Split('.');
                if (parts.Length == 3)
                {
                    combined.Should().NotContain(parts[2]);
                }
            }
        }
    }
}
