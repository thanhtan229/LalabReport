using System;
using System.Collections.Generic;

namespace LalabAutoReport.Contracts.Auth;

public sealed record LoginRequestDto(
    string Pin,
    string? DeviceId = null,
    string? DeviceLabel = null,
    string? ClientKind = null);

public sealed record LoginResponseDto(
    bool Success,
    string? Token,
    string? SessionId,
    string? Role,
    string? WorkshopName,
    IReadOnlyList<string>? Capabilities,
    DateTimeOffset? ExpiresAtUtc);

public sealed record SessionDto(
    string SessionId,
    string Role,
    string ClientKind,
    string? DeviceId,
    string? DeviceLabel,
    string? IssuedIp,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedReason);

public sealed record CurrentSessionResponseDto(
    string SessionId,
    string Role,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ExpiresAtUtc,
    string ClientKind,
    string? DeviceId = null,
    string? DeviceLabel = null);

public sealed record RevokeAllSessionsRequestDto(
    string? Role = null);

public sealed record ApiErrorResponse(
    string Code,
    string Message,
    string? CorrelationId = null);
