using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.ApplicationCore.Auth;

public sealed class SessionService : ISessionService
{
    private readonly IRoleCredentialStore _credentialStore;
    private readonly ISessionStore _sessionStore;
    private readonly LoginRateLimiter _rateLimiter;
    private readonly IAuditLogger? _auditLogger;
    private readonly ILogger<SessionService>? _logger;
    private readonly ILoginPipelineObserver _pipelineObserver;

    public SessionService(
        IRoleCredentialStore credentialStore,
        ISessionStore sessionStore,
        LoginRateLimiter rateLimiter,
        IAuditLogger? auditLogger = null,
        ILogger<SessionService>? logger = null,
        ILoginPipelineObserver? pipelineObserver = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _auditLogger = auditLogger;
        _logger = logger;
        _pipelineObserver = pipelineObserver ?? NoOpLoginPipelineObserver.Instance;
    }

    public async Task<LoginResult> LoginAsync(
        string pin,
        string? deviceId = null,
        string? deviceLabel = null,
        string? clientKind = null,
        string? ip = null,
        CancellationToken cancellationToken = default)
    {
        string effectiveIp = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip;
        var (isLimited, retryAfter) = _rateLimiter.CheckRateLimit(effectiveIp, deviceId);
        if (isLimited)
        {
            _logger?.LogWarning("Login rate limited for IP {Ip}, device {DeviceId}. Retry after {Seconds}s",
                effectiveIp, deviceId, retryAfter.TotalSeconds);
            return new LoginResult(LoginResultStatus.RateLimited, RetryAfter: retryAfter, ErrorMessage: "Quá nhiều lần thử sai. Vui lòng thử lại sau.");
        }

        var status = await _credentialStore.GetStatusAsync(cancellationToken);
        if (!status.AdminPinConfigured)
        {
            return new LoginResult(LoginResultStatus.NotConfigured, ErrorMessage: "Chưa cấu hình mã PIN Admin trên ứng dụng Desktop. Vui lòng vào Cài đặt để thiết lập mã PIN.");
        }

        var verification = await _credentialStore.VerifyWithEpochAsync(pin, cancellationToken);
        if (verification == null)
        {
            _rateLimiter.RecordFailure(effectiveIp, deviceId);
            _logger?.LogWarning("Failed login attempt from IP {Ip}, device {DeviceId}", effectiveIp, deviceId);
            return new LoginResult(LoginResultStatus.InvalidCredentials, ErrorMessage: "Mã PIN không chính xác.");
        }

        // Test seam: fault hook / observer invoked after verification before atomic session creation
        await _pipelineObserver.OnAfterVerifyBeforeCreateSessionAsync(verification.Role, verification.CredentialEpoch, cancellationToken);

        var (token, session) = await _sessionStore.CreateSessionAsync(
            verification.Role,
            verification.CredentialEpoch,
            clientKind ?? "pwa",
            deviceId,
            deviceLabel,
            effectiveIp,
            cancellationToken);

        if (token == null || session == null)
        {
            _rateLimiter.RecordFailure(effectiveIp, deviceId);
            _logger?.LogWarning("Failed login attempt: credentials changed concurrently for role {Role} from IP {Ip}, device {DeviceId}", verification.Role, effectiveIp, deviceId);
            return new LoginResult(LoginResultStatus.InvalidCredentials, ErrorMessage: "Mã PIN không chính xác hoặc thông tin đăng nhập đã thay đổi.");
        }

        _rateLimiter.RecordSuccess(effectiveIp, deviceId);

        if (_auditLogger != null)
        {
            try
            {
                await _auditLogger.WriteAsync(new AuditEntry(
                    Action: "auth.login",
                    Outcome: "Succeeded",
                    ActorKind: "session",
                    ActorRole: verification.Role,
                    ActorSessionId: session.SessionId,
                    ActorDeviceId: deviceId,
                    ActorLabel: verification.Role == "Admin" ? "Chủ tiệm" : "Nhân viên",
                    ClientIp: effectiveIp
                ), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write audit log for login");
            }
        }

        var caps = RoleCapabilityMap.GetCapabilities(verification.Role);
        return new LoginResult(
            Status: LoginResultStatus.Success,
            Token: token,
            SessionId: session.SessionId,
            Role: verification.Role,
            Capabilities: caps,
            ExpiresAtUtc: session.ExpiresAtUtc);
    }

    public async Task<ActorContext?> ValidateTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var (success, session, _) = await _sessionStore.ValidateSessionAsync(token, cancellationToken);
        if (!success || session == null)
        {
            return null;
        }

        var caps = RoleCapabilityMap.GetCapabilities(session.Role);
        return ActorContext.FromSession(
            session.SessionId,
            session.Role,
            caps,
            session.DeviceId,
            session.DeviceLabel,
            session.IssuedIp,
            session.ExpiresAtUtc,
            session.ClientKind);
    }

    public async Task RevokeSessionAsync(
        string sessionId,
        string reason = "logout",
        ActorContext? actor = null,
        CancellationToken cancellationToken = default)
    {
        await _sessionStore.RevokeSessionAsync(sessionId, reason, cancellationToken);
        if (_auditLogger != null)
        {
            try
            {
                await _auditLogger.WriteAsync(new AuditEntry(
                    Action: "auth.logout",
                    Outcome: "Succeeded",
                    ActorKind: actor?.Kind ?? "session",
                    ActorRole: actor?.Role,
                    ActorSessionId: sessionId,
                    ActorDeviceId: actor?.DeviceId,
                    ActorLabel: actor?.Role == "Admin" ? "Chủ tiệm" : "Nhân viên",
                    ClientIp: actor?.ClientIp,
                    Reason: reason
                ), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write audit log for logout");
            }
        }
    }

    public async Task RevokeAllAsync(
        string? role = null,
        string reason = "admin",
        ActorContext? actor = null,
        CancellationToken cancellationToken = default)
    {
        await _credentialStore.BumpEpochAsync(role, reason, actor, cancellationToken);
    }
}
