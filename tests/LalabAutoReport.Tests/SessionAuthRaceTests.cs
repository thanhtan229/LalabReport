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
}
