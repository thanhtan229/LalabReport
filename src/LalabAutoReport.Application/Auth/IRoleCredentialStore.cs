using System;
using System.Threading;
using System.Threading.Tasks;

namespace LalabAutoReport.ApplicationCore.Auth;

public sealed record RoleCredentialStatus(
    bool AdminPinConfigured,
    bool StaffPinConfigured,
    long AdminCredentialEpoch,
    long StaffCredentialEpoch,
    DateTimeOffset LegacyTokensValidUntilUtc,
    DateTimeOffset LegacyTokensIssuedBeforeUtc = default);

public sealed record CredentialVerificationResult(
    string Role,
    long CredentialEpoch);

public interface IRoleCredentialStore
{
    Task<RoleCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<CredentialVerificationResult?> VerifyWithEpochAsync(string pin, CancellationToken cancellationToken = default);
    Task<string?> VerifyAsync(string pin, CancellationToken cancellationToken = default);
    Task SetPinAsync(string role, string newPin, ActorContext? actor = null, CancellationToken cancellationToken = default);
    Task ClearPinAsync(string role, ActorContext? actor = null, CancellationToken cancellationToken = default);
    Task BumpEpochAsync(string? role = null, string reason = "admin", ActorContext? actor = null, CancellationToken cancellationToken = default);
}
