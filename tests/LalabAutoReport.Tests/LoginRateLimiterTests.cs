using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts;
using LalabAutoReport.Contracts.Auth;
using Xunit;

namespace LalabAutoReport.Tests;

public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TestTimeProvider(DateTimeOffset? initial = null)
    {
        _utcNow = initial ?? new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration)
    {
        _utcNow = _utcNow.Add(duration);
    }
}

public class LoginRateLimiterTests
{
    [Fact]
    public void RateLimit_LocksIpAfter10Failures_RetryAfterHeader()
    {
        var fakeTime = new TestTimeProvider();
        var limiter = new LoginRateLimiter(fakeTime);
        string ip = "192.168.1.50";

        for (int i = 0; i < 9; i++)
        {
            var (isLimited, _) = limiter.CheckRateLimit(ip, null);
            isLimited.Should().BeFalse();
            limiter.RecordFailure(ip, null);
        }

        // 10th failure
        limiter.RecordFailure(ip, null);

        var (limited, retryAfter) = limiter.CheckRateLimit(ip, null);
        limited.Should().BeTrue();
        retryAfter.Should().BeCloseTo(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

        // Advance 30 seconds: still locked
        fakeTime.Advance(TimeSpan.FromSeconds(30));
        var (stillLimited, remaining) = limiter.CheckRateLimit(ip, null);
        stillLimited.Should().BeTrue();
        remaining.Should().BeCloseTo(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        // Advance another 31 seconds: unlocked
        fakeTime.Advance(TimeSpan.FromSeconds(31));
        var (unlocked, _) = limiter.CheckRateLimit(ip, null);
        unlocked.Should().BeFalse();
    }

    [Fact]
    public void RateLimit_LockoutDoublesUpTo15Minutes()
    {
        var fakeTime = new TestTimeProvider();
        var limiter = new LoginRateLimiter(fakeTime);
        string ip = "192.168.1.51";

        // Lockout 1: 10 failures -> 1 min
        for (int i = 0; i < 10; i++) limiter.RecordFailure(ip, null);
        var (_, retry1) = limiter.CheckRateLimit(ip, null);
        retry1.Should().BeCloseTo(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

        // Expire lockout 1 and trigger lockout 2: 10 failures -> 2 min
        fakeTime.Advance(TimeSpan.FromMinutes(1.1));
        for (int i = 0; i < 10; i++) limiter.RecordFailure(ip, null);
        var (_, retry2) = limiter.CheckRateLimit(ip, null);
        retry2.Should().BeCloseTo(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(1));

        // Expire and trigger lockout 3: 4 min
        fakeTime.Advance(TimeSpan.FromMinutes(2.1));
        for (int i = 0; i < 10; i++) limiter.RecordFailure(ip, null);
        var (_, retry3) = limiter.CheckRateLimit(ip, null);
        retry3.Should().BeCloseTo(TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(1));

        // Expire and trigger lockout 4: 8 min
        fakeTime.Advance(TimeSpan.FromMinutes(4.1));
        for (int i = 0; i < 10; i++) limiter.RecordFailure(ip, null);
        var (_, retry4) = limiter.CheckRateLimit(ip, null);
        retry4.Should().BeCloseTo(TimeSpan.FromMinutes(8), TimeSpan.FromSeconds(1));

        // Expire and trigger lockout 5: max 15 min
        fakeTime.Advance(TimeSpan.FromMinutes(8.1));
        for (int i = 0; i < 10; i++) limiter.RecordFailure(ip, null);
        var (_, retry5) = limiter.CheckRateLimit(ip, null);
        retry5.Should().BeCloseTo(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RateLimit_SuccessResetsDeviceKey()
    {
        var fakeTime = new TestTimeProvider();
        var limiter = new LoginRateLimiter(fakeTime);
        string ip = "192.168.1.52";
        string device = "device-xyz";

        // 4 failures on device (threshold is 5)
        for (int i = 0; i < 4; i++)
        {
            limiter.RecordFailure(ip, device);
        }

        // Success resets device failures
        limiter.RecordSuccess(ip, device);

        // 4 more failures - should NOT lock since count was reset
        for (int i = 0; i < 4; i++)
        {
            limiter.RecordFailure(ip, device);
        }

        var (limited, _) = limiter.CheckRateLimit(ip, device);
        limited.Should().BeFalse();
    }

    private class SpyingCredStore : IRoleCredentialStore
    {
        public int VerifyCount { get; private set; }

        public Task<RoleCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RoleCredentialStatus(true, true, 1, 1, DateTimeOffset.UtcNow.AddDays(7)));

        public Task<CredentialVerificationResult?> VerifyWithEpochAsync(string pin, CancellationToken cancellationToken = default)
        {
            VerifyCount++;
            return Task.FromResult<CredentialVerificationResult?>(null);
        }

        public Task<string?> VerifyAsync(string pin, CancellationToken cancellationToken = default)
        {
            VerifyCount++;
            return Task.FromResult<string?>(null);
        }

        public Task SetPinAsync(string role, string newPin, ActorContext? actor = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearPinAsync(string role, ActorContext? actor = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task BumpEpochAsync(string? role = null, string reason = "admin", ActorContext? actor = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class DummySessionStore : ISessionStore
    {
        public Task<(string? Token, SessionDto? Session)> CreateSessionAsync(
            string role, long expectedCredentialEpoch, string clientKind, string? deviceId = null, string? deviceLabel = null, string? issuedIp = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<(string?, SessionDto?)>(("dummy-token", new LalabAutoReport.Contracts.Auth.SessionDto("dummy-id", role, clientKind, deviceId, deviceLabel, issuedIp, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), null, null, null)));

        public Task<(string Token, SessionDto Session)> CreateSessionAsync(
            string role, string clientKind, string? deviceId = null, string? deviceLabel = null, string? issuedIp = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(("dummy-token", new LalabAutoReport.Contracts.Auth.SessionDto("dummy-id", role, clientKind, deviceId, deviceLabel, issuedIp, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), null, null, null)));

        public Task<(bool Success, LalabAutoReport.Contracts.Auth.SessionDto? Session, string? FailureReason)> ValidateSessionAsync(string token, CancellationToken cancellationToken = default) =>
            Task.FromResult<(bool Success, LalabAutoReport.Contracts.Auth.SessionDto? Session, string? FailureReason)>((false, null, "dummy"));

        public Task RevokeSessionAsync(string sessionId, string reason = "logout", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RevokeSessionsByRoleAsync(string role, string reason = "credential_changed", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RevokeAllSessionsAsync(string reason = "revoke_all", CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> PurgeExpiredAndRevokedSessionsAsync(TimeSpan olderThan, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<System.Collections.Generic.IReadOnlyList<LalabAutoReport.Contracts.Auth.SessionDto>> ListActiveSessionsAsync(string? role = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<LalabAutoReport.Contracts.Auth.SessionDto>>(Array.Empty<LalabAutoReport.Contracts.Auth.SessionDto>());
    }

    private class DummyAuditLogger : IAuditLogger
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task RateLimit_LockedRequestDoesNotRunPbkdf2()
    {
        var fakeTime = new TestTimeProvider();
        var limiter = new LoginRateLimiter(fakeTime);
        var credStore = new SpyingCredStore();
        var sessionStore = new DummySessionStore();
        var audit = new DummyAuditLogger();

        var sessionService = new SessionService(
            credStore,
            sessionStore,
            limiter,
            audit);

        string ip = "192.168.1.99";

        // Trigger rate limit
        for (int i = 0; i < 10; i++)
        {
            limiter.RecordFailure(ip, null);
        }

        // Attempt login while rate limited
        var result = await sessionService.LoginAsync("123456", clientKind: "pwa", ip: ip);

        result.Status.Should().Be(LoginResultStatus.RateLimited);
        // Verify credStore.VerifyAsync was NEVER called because request was rate limited before hashing
        credStore.VerifyCount.Should().Be(0);
    }
}
