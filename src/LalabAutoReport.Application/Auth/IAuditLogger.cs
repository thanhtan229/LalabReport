using System;
using System.Threading;
using System.Threading.Tasks;

namespace LalabAutoReport.ApplicationCore.Auth;

public sealed record AuditEntry(
    string Action,
    string Outcome,
    string ActorKind,
    string? ActorRole = null,
    string? ActorSessionId = null,
    string? ActorDeviceId = null,
    string? ActorLabel = null,
    string? ClientIp = null,
    string? Reason = null,
    string? EntityType = null,
    long? EntityId = null,
    string? OperationId = null);

public interface IAuditLogger
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
