using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Contracts.Auth;

namespace LalabAutoReport.ApplicationCore.Auth;

public interface ISessionStore
{
    Task<(string Token, SessionDto Session)> CreateSessionAsync(
        string role,
        string clientKind,
        string? deviceId = null,
        string? deviceLabel = null,
        string? issuedIp = null,
        CancellationToken cancellationToken = default);

    Task<(bool Success, SessionDto? Session, string? FailureReason)> ValidateSessionAsync(
        string token,
        CancellationToken cancellationToken = default);

    Task RevokeSessionAsync(
        string sessionId,
        string reason = "logout",
        CancellationToken cancellationToken = default);

    Task RevokeSessionsByRoleAsync(
        string role,
        string reason = "credential_changed",
        CancellationToken cancellationToken = default);

    Task RevokeAllSessionsAsync(
        string reason = "revoke_all",
        CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredAndRevokedSessionsAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionDto>> ListActiveSessionsAsync(
        string? role = null,
        CancellationToken cancellationToken = default);
}
