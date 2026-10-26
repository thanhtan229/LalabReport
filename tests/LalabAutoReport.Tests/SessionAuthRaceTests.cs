using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Auth;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.FileSystem;
using LalabAutoReport.Infrastructure.Services;
using LalabAutoReport.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LalabAutoReport.Tests;

public class SessionAuthRaceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseBackupService _backupService;
    private readonly SqliteRoleCredentialStore _credStore;
    private readonly SqliteSessionStore _sessionStore;
    private readonly SqliteAuditLogger _auditLogger;

    public SessionAuthRaceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabRaceTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_race.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _backupService = new DatabaseBackupService(_connectionFactory);

        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        migrator.MigrateAsync().GetAwaiter().GetResult();

        _credStore = new SqliteRoleCredentialStore(_connectionFactory);
        _sessionStore = new SqliteSessionStore(_connectionFactory);
        _auditLogger = new SqliteAuditLogger(_connectionFactory);

        // Seed baseline credentials
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

    private sealed class DelegateLoginPipelineObserver : ILoginPipelineObserver
    {
        private readonly Func<string, long, CancellationToken, Task> _callback;

        public DelegateLoginPipelineObserver(Func<string, long, CancellationToken, Task> callback)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public Task OnAfterVerifyBeforeCreateSessionAsync(string role, long verifiedEpoch, CancellationToken cancellationToken = default)
        {
            return _callback(role, verifiedEpoch, cancellationToken);
        }
    }

    [Fact]
    public async Task LoginAsync_WhenPinChangedDuringVerification_FailsAtomicallyAndDoesNotIssueSession()
    {
        // Arrange: hook rotates the Admin PIN right after verification, bumping the credential epoch
        var observer = new DelegateLoginPipelineObserver(async (role, epoch, ct) =>
        {
            if (role == "Admin")
            {
                await _credStore.SetPinAsync("Admin", "999888", null, ct);
            }
        });

        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(
            _credStore,
            _sessionStore,
            limiter,
            _auditLogger,
            pipelineObserver: observer);

        // Act: attempt login with old PIN
        var result = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "127.0.0.1");

        // Assert: login rejected, no token, no session created
        result.Status.Should().Be(LoginResultStatus.InvalidCredentials);
        result.Token.Should().BeNull();
        result.SessionId.Should().BeNull();

        using (var conn = _connectionFactory.CreateConnection())
        {
            int sessionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth_sessions;");
            sessionCount.Should().Be(0, "atomic check must prevent inserting session for stale credential epoch");
        }

        // Retry with old PIN without hook -> rejected
        var defaultSessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger);
        var oldRetry = await defaultSessionService.LoginAsync("123456", clientKind: "pwa", ip: "127.0.0.1");
        oldRetry.Status.Should().Be(LoginResultStatus.InvalidCredentials);

        // Login with new PIN -> succeeds
        var newLogin = await defaultSessionService.LoginAsync("999888", clientKind: "pwa", ip: "127.0.0.1");
        newLogin.Status.Should().Be(LoginResultStatus.Success);
        newLogin.Token.Should().NotBeNullOrEmpty();
        newLogin.Role.Should().Be("Admin");

        using (var conn = _connectionFactory.CreateConnection())
        {
            int sessionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth_sessions;");
            sessionCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task LoginAsync_WhenPinClearedDuringVerification_FailsAtomicallyAndDoesNotIssueSession()
    {
        // Arrange: hook clears the Staff PIN right after verification
        var observer = new DelegateLoginPipelineObserver(async (role, epoch, ct) =>
        {
            if (role == "Staff")
            {
                await _credStore.ClearPinAsync("Staff", null, ct);
            }
        });

        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(
            _credStore,
            _sessionStore,
            limiter,
            _auditLogger,
            pipelineObserver: observer);

        // Act: attempt login with Staff PIN
        var result = await sessionService.LoginAsync("654321", clientKind: "pwa", ip: "127.0.0.1");

        // Assert: login rejected, no session created
        result.Status.Should().Be(LoginResultStatus.InvalidCredentials);
        result.Token.Should().BeNull();

        using var conn = _connectionFactory.CreateConnection();
        int sessionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth_sessions WHERE role = 'Staff';");
        sessionCount.Should().Be(0, "session must not be issued when credential hash is cleared");
    }

    [Fact]
    public async Task LoginAsync_WhenRevokeAllOccursDuringVerification_FailsAtomicallyAndDoesNotIssueSession()
    {
        // Arrange: hook triggers BumpEpochAsync (revoke-all) right after verification
        var observer = new DelegateLoginPipelineObserver(async (role, epoch, ct) =>
        {
            await _credStore.BumpEpochAsync(null, "admin_action", null, ct);
        });

        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(
            _credStore,
            _sessionStore,
            limiter,
            _auditLogger,
            pipelineObserver: observer);

        // Act: attempt login
        var result = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "127.0.0.1");

        // Assert: login rejected, no session created
        result.Status.Should().Be(LoginResultStatus.InvalidCredentials);
        result.Token.Should().BeNull();

        using var conn = _connectionFactory.CreateConnection();
        int sessionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth_sessions;");
        sessionCount.Should().Be(0, "session must not be issued when epoch is bumped concurrently");
    }

    [Fact]
    public async Task LoginAsync_NormalFlow_IssuesValidSession_AndSubsequentPinChangeInvalidatesToken()
    {
        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger);

        // Act 1: normal login
        var result = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: "127.0.0.1");
        result.Status.Should().Be(LoginResultStatus.Success);
        result.Token.Should().NotBeNullOrEmpty();

        // Validate token
        var (valid, session, reason) = await _sessionStore.ValidateSessionAsync(result.Token!);
        valid.Should().BeTrue();
        session.Should().NotBeNull();
        reason.Should().BeNull();

        // Act 2: rotate PIN
        await _credStore.SetPinAsync("Admin", "777888");

        // Assert: old token is invalidated immediately via epoch mismatch
        var (validAfter, _, reasonAfter) = await _sessionStore.ValidateSessionAsync(result.Token!);
        validAfter.Should().BeFalse();
        reasonAfter.Should().BeOneOf("revoked", "epoch_mismatch");
    }

    [Fact]
    public async Task HttpApi_Login_InterleavedWithPinChange_Returns401Unauthorized()
    {
        int port = GetAvailablePort();
        var observer = new DelegateLoginPipelineObserver(async (role, epoch, ct) =>
        {
            if (role == "Admin")
            {
                await _credStore.SetPinAsync("Admin", "888999", null, ct);
            }
        });

        var services = new ServiceCollection();
        var settingsRepo = new SqliteSettingsRepository(_connectionFactory, _credStore);
        var settings = await settingsRepo.GetSettingsAsync();
        settings.MobileServerPort = port;
        settings.WorkshopName = "Race Test Lab";
        await settingsRepo.SaveSettingsAsync(settings);

        var limiter = new LoginRateLimiter();
        var sessionService = new SessionService(_credStore, _sessionStore, limiter, _auditLogger, pipelineObserver: observer);

        var authService = new MobileAuthService();
        services.AddSingleton<ISettingsRepository>(settingsRepo);
        services.AddSingleton<IMobileAuthService>(authService);
        services.AddSingleton<ISqliteConnectionFactory>(_connectionFactory);
        services.AddSingleton<IRoleCredentialStore>(_credStore);
        services.AddSingleton<ISessionStore>(_sessionStore);
        services.AddSingleton<LoginRateLimiter>(limiter);
        services.AddSingleton<IAuditLogger>(_auditLogger);
        services.AddSingleton<ILoginPipelineObserver>(observer);
        services.AddSingleton<ISessionService>(sessionService);

        var server = new KestrelMobileWebServer(
            services.BuildServiceProvider(),
            settingsRepo,
            authService);

        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        try
        {
            // Act: POST /api/v1/auth/sessions with old PIN while observer rotates it
            var loginBody = JsonSerializer.Serialize(new { pin = "123456", clientKind = "pwa" });
            var response = await client.PostAsync("/api/v1/auth/sessions", new StringContent(loginBody, Encoding.UTF8, "application/json"));

            // Assert: Returns HTTP 401 Unauthorized
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            string responseText = await response.Content.ReadAsStringAsync();
            responseText.Should().Contain(ApiErrorCodes.Unauthorized);

            using var conn = _connectionFactory.CreateConnection();
            int sessionCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth_sessions;");
            sessionCount.Should().Be(0, "no session should exist after race rejection");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task LiveRuntimeSmoke_ServerProcess_WithLanWritesOrders_NormalLogin_PinRotation_WriteGate()
    {
        string serverExe = FindServerExePath();
        string smokeDataDir = Path.Combine(Path.GetTempPath(), "LalabLiveSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(smokeDataDir);

        try
        {
            // 1. Initialize SQLite database and seed credentials & order
            var connFactory = new SqliteConnectionFactory(Path.Combine(smokeDataDir, "lalab_autoreport.db"));
            var backup = new DatabaseBackupService(connFactory);
            var migrator = new DatabaseMigrator(connFactory, backup);
            await migrator.MigrateAsync();

            var credStore = new SqliteRoleCredentialStore(connFactory);
            await credStore.SetPinAsync("Admin", "123456");
            await credStore.SetPinAsync("Staff", "654321");

            using (var conn = connFactory.CreateConnection())
            {
                await conn.ExecuteAsync("INSERT OR IGNORE INTO customers (id, canonical_name, created_at, updated_at) VALUES (1, 'Test Customer', '2026-10-26', '2026-10-26');");
                await conn.ExecuteAsync("INSERT INTO orders (id, work_date, customer_id, original_folder_name, relative_path, status, created_at, updated_at) VALUES (101, '2026-10-26', 1, 'ORD-101', '2026-10-26/ORD-101', 'Scanned', '2026-10-26', '2026-10-26');");
            }

            int healthPort = GetAvailablePort();
            int mobilePort = GetAvailablePort();

            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"--data-dir \"{smokeDataDir}\" --port {mobilePort} --health-port {healthPort} --lan-writes orders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull("Server process should launch");

            try
            {
                using var http = new HttpClient();

                // 2. Poll health endpoint until Ready
                var sw = Stopwatch.StartNew();
                bool isReady = false;
                JsonElement healthJson = default;
                while (sw.Elapsed < TimeSpan.FromSeconds(20))
                {
                    try
                    {
                        var resp = await http.GetAsync($"http://127.0.0.1:{healthPort}/health/ready");
                        if (resp.IsSuccessStatusCode)
                        {
                            string json = await resp.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "Ready")
                            {
                                healthJson = doc.RootElement.Clone();
                                isReady = true;
                                break;
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(250);
                }

                isReady.Should().BeTrue("Server should report ready on /health/ready within 20s");
                var caps = healthJson.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()).ToList();
                caps.Should().Contain("orders.editNote");

                // 3. Normal login (POST /api/v1/auth/sessions) -> returns 200 OK
                var loginBody = JsonSerializer.Serialize(new { pin = "123456", clientKind = "pwa" });
                var loginResp = await http.PostAsync($"http://127.0.0.1:{mobilePort}/api/v1/auth/sessions",
                    new StringContent(loginBody, Encoding.UTF8, "application/json"));
                loginResp.StatusCode.Should().Be(HttpStatusCode.OK);

                string loginJson = await loginResp.Content.ReadAsStringAsync();
                using var loginDoc = JsonDocument.Parse(loginJson);
                string token = loginDoc.RootElement.GetProperty("token").GetString()!;
                string role = loginDoc.RootElement.GetProperty("role").GetString()!;
                role.Should().Be("Admin");
                token.Should().StartWith("lsv2.");

                // 4. Validate session via GET /api/v1/auth/sessions/current -> returns 200 OK
                using (var currentReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{mobilePort}/api/v1/auth/sessions/current"))
                {
                    currentReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var currentResp = await http.SendAsync(currentReq);
                    currentResp.StatusCode.Should().Be(HttpStatusCode.OK);
                }

                // 5. Verify write gate allowed under --lan-writes orders: POST note command
                using (var cmdReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{mobilePort}/api/v1/orders/101/note"))
                {
                    cmdReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    cmdReq.Headers.Add("X-Lalab-Client-Api", "1.0");
                    cmdReq.Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        operationId = Guid.NewGuid().ToString(),
                        expectedRevision = 0,
                        note = "Live smoke note"
                    }), Encoding.UTF8, "application/json");

                    var cmdResp = await http.SendAsync(cmdReq);
                    cmdResp.StatusCode.Should().Be(HttpStatusCode.OK, "orders write capability must be enabled under --lan-writes orders");
                }

                // 6. Rotate Admin PIN -> bumps epoch, revokes active session
                await credStore.SetPinAsync("Admin", "777888");

                // Old token is immediately rejected on /auth/sessions/current
                using (var oldReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{mobilePort}/api/v1/auth/sessions/current"))
                {
                    oldReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var oldResp = await http.SendAsync(oldReq);
                    oldResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                }

                // Old PIN fails login
                var failLoginResp = await http.PostAsync($"http://127.0.0.1:{mobilePort}/api/v1/auth/sessions",
                    new StringContent(loginBody, Encoding.UTF8, "application/json"));
                failLoginResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

                // New PIN succeeds login
                var newLoginBody = JsonSerializer.Serialize(new { pin = "777888", clientKind = "pwa" });
                var newLoginResp = await http.PostAsync($"http://127.0.0.1:{mobilePort}/api/v1/auth/sessions",
                    new StringContent(newLoginBody, Encoding.UTF8, "application/json"));
                newLoginResp.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(smokeDataDir))
                {
                    Directory.Delete(smokeDataDir, recursive: true);
                }
            }
            catch { }
        }
    }

    private static string FindServerExePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] relativePaths = new[]
        {
            "LalabAutoReport.Server.exe",
            @"..\..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe",
            @"..\..\..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe",
            @"..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe"
        };

        foreach (var rel in relativePaths)
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (File.Exists(full))
            {
                return full;
            }
        }

        throw new FileNotFoundException($"Không tìm thấy LalabAutoReport.Server.exe từ thư mục: {baseDir}");
    }
}
