using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LalabAutoReport.ApplicationCore.Auth;

public enum LoginResultStatus
{
    Success,
    InvalidCredentials,
    NotConfigured,
    RateLimited,
    PinCollision
}

public sealed record LoginResult(
    LoginResultStatus Status,
    string? Token = null,
    string? SessionId = null,
    string? Role = null,
    IReadOnlySet<string>? Capabilities = null,
    DateTimeOffset? ExpiresAtUtc = null,
    TimeSpan? RetryAfter = null,
    string? ErrorMessage = null);

public interface ISessionService
{
    Task<LoginResult> LoginAsync(
        string pin,
        string? deviceId = null,
        string? deviceLabel = null,
        string? clientKind = null,
        string? ip = null,
        CancellationToken cancellationToken = default);

    Task<ActorContext?> ValidateTokenAsync(
        string token,
        CancellationToken cancellationToken = default);

    Task RevokeSessionAsync(
        string sessionId,
        string reason = "logout",
        ActorContext? actor = null,
        CancellationToken cancellationToken = default);

    Task RevokeAllAsync(
        string? role = null,
        string reason = "admin",
        ActorContext? actor = null,
        CancellationToken cancellationToken = default);
}
