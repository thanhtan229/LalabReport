using System;
using System.Collections.Generic;

namespace LalabAutoReport.ApplicationCore.Auth;

public sealed class ActorContext
{
    public string Kind { get; }
    public string Role { get; }
    public string? SessionId { get; }
    public string? DeviceId { get; }
    public string? DeviceLabel { get; }
    public string? ClientIp { get; }
    public IReadOnlySet<string> Capabilities { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }
    public string? ClientKind { get; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(Role);

    public ActorContext(
        string kind,
        string role,
        IReadOnlySet<string> capabilities,
        string? sessionId = null,
        string? deviceId = null,
        string? deviceLabel = null,
        string? clientIp = null,
        DateTimeOffset? expiresAtUtc = null,
        string? clientKind = null)
    {
        Kind = kind ?? "anonymous";
        Role = role ?? string.Empty;
        Capabilities = capabilities ?? new HashSet<string>();
        SessionId = sessionId;
        DeviceId = deviceId;
        DeviceLabel = deviceLabel;
        ClientIp = clientIp;
        ExpiresAtUtc = expiresAtUtc;
        ClientKind = clientKind;
    }

    public bool Has(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
            return false;
        return Capabilities.Contains(capability);
    }

    public void Require(string capability)
    {
        if (!Has(capability))
        {
            throw new CapabilityDeniedException(capability, Role);
        }
    }

    public static ActorContext Anonymous() =>
        new("anonymous", string.Empty, new HashSet<string>());

    public static ActorContext FromSession(
        string sessionId,
        string role,
        IReadOnlySet<string> capabilities,
        string? deviceId = null,
        string? deviceLabel = null,
        string? clientIp = null,
        DateTimeOffset? expiresAtUtc = null,
        string? clientKind = null) =>
        new("session", role, capabilities, sessionId, deviceId, deviceLabel, clientIp, expiresAtUtc, clientKind);

    public static ActorContext FromLegacyToken(string role, IReadOnlySet<string> capabilities) =>
        new("legacy_token", role, capabilities);

    public static ActorContext LocalDesktop(string role = "Admin") =>
        new("local_desktop", role, RoleCapabilityMap.GetCapabilities(role));
}
