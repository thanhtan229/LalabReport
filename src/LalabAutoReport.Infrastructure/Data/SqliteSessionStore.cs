using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Contracts.Auth;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Data;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ILogger<SqliteSessionStore>? _logger;

    public SqliteSessionStore(
        ISqliteConnectionFactory connectionFactory,
        ILogger<SqliteSessionStore>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger;
    }

    public async Task<(string Token, SessionDto Session)> CreateSessionAsync(
        string role,
        string clientKind,
        string? deviceId = null,
        string? deviceLabel = null,
        string? issuedIp = null,
        CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();

        // 1. Get current credential epoch for the role
        long credentialEpoch = await conn.QuerySingleOrDefaultAsync<long>(@"
            SELECT credential_epoch FROM auth_credentials WHERE role = @Role;
        ", new { Role = role });

        if (credentialEpoch <= 0)
        {
            credentialEpoch = 1;
        }

        // 2. Generate session ID and secret
        string sessionId = Guid.NewGuid().ToString("N");
        byte[] secretBytes = new byte[32];
        RandomNumberGenerator.Fill(secretBytes);
        string secret = Base64UrlEncode(secretBytes);

        byte[] tokenHash = SHA256.HashData(secretBytes);
        string token = $"lsv2.{sessionId}.{secret}";

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(30);

        string nowIso = now.ToString("o");
        string expiresAtIso = expiresAt.ToString("o");

        await conn.ExecuteAsync(@"
            INSERT INTO auth_sessions (
                session_id, token_hash, role, credential_epoch, client_kind,
                device_id, device_label, issued_ip, issued_at_utc, expires_at_utc,
                last_seen_at_utc, revoked_at_utc, revoked_reason
            ) VALUES (
                @SessionId, @TokenHash, @Role, @CredentialEpoch, @ClientKind,
                @DeviceId, @DeviceLabel, @IssuedIp, @IssuedAtUtc, @ExpiresAtUtc,
                @LastSeenAtUtc, NULL, NULL
            );
        ", new
        {
            SessionId = sessionId,
            TokenHash = tokenHash,
            Role = role,
            CredentialEpoch = credentialEpoch,
            ClientKind = clientKind ?? "pwa",
            DeviceId = deviceId,
            DeviceLabel = deviceLabel,
            IssuedIp = issuedIp,
            IssuedAtUtc = nowIso,
            ExpiresAtUtc = expiresAtIso,
            LastSeenAtUtc = nowIso
        });

        var sessionDto = new SessionDto(
            SessionId: sessionId,
            Role: role,
            ClientKind: clientKind ?? "pwa",
            DeviceId: deviceId,
            DeviceLabel: deviceLabel,
            IssuedIp: issuedIp,
            IssuedAtUtc: now,
            ExpiresAtUtc: expiresAt,
            LastSeenAtUtc: now,
            RevokedAtUtc: null,
            RevokedReason: null);

        return (token, sessionDto);
    }

    public async Task<(bool Success, SessionDto? Session, string? FailureReason)> ValidateSessionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, null, "empty_token");

        string[] parts = token.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], "lsv2", StringComparison.Ordinal))
        {
            return (false, null, "invalid_format");
        }

        string sessionId = parts[1];
        string secret = parts[2];

        byte[] secretBytes;
        try
        {
            secretBytes = Base64UrlDecode(secret);
        }
        catch
        {
            return (false, null, "invalid_secret_encoding");
        }

        byte[] computedHash = SHA256.HashData(secretBytes);

        using var conn = _connectionFactory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<SessionValidationRow>(@"
            SELECT s.session_id, s.token_hash, s.role, s.credential_epoch, s.client_kind,
                   s.device_id, s.device_label, s.issued_ip, s.issued_at_utc, s.expires_at_utc,
                   s.last_seen_at_utc, s.revoked_at_utc, s.revoked_reason,
                   c.credential_epoch AS current_credential_epoch
            FROM auth_sessions s
            LEFT JOIN auth_credentials c ON c.role = s.role
            WHERE s.session_id = @SessionId;
        ", new { SessionId = sessionId });

        if (row == null)
        {
            return (false, null, "not_found");
        }

        if (!CryptographicOperations.FixedTimeEquals(computedHash, row.token_hash))
        {
            return (false, null, "secret_mismatch");
        }

        if (!string.IsNullOrEmpty(row.revoked_at_utc))
        {
            return (false, null, "revoked");
        }

        if (DateTimeOffset.TryParse(row.expires_at_utc, out var expiresAt) && expiresAt <= DateTimeOffset.UtcNow)
        {
            return (false, null, "expired");
        }

        if (row.current_credential_epoch.HasValue && row.credential_epoch != row.current_credential_epoch.Value)
        {
            return (false, null, "epoch_mismatch");
        }

        // Throttle last_seen_at_utc >= 5 min
        var now = DateTimeOffset.UtcNow;
        bool updateLastSeen = false;
        if (string.IsNullOrEmpty(row.last_seen_at_utc) ||
            (DateTimeOffset.TryParse(row.last_seen_at_utc, out var lastSeen) && (now - lastSeen).TotalMinutes >= 5))
        {
            updateLastSeen = true;
        }

        if (updateLastSeen)
        {
            await conn.ExecuteAsync(@"
                UPDATE auth_sessions
                SET last_seen_at_utc = @Now
                WHERE session_id = @SessionId;
            ", new { Now = now.ToString("o"), SessionId = sessionId });
        }

        var sessionDto = new SessionDto(
            SessionId: row.session_id,
            Role: row.role,
            ClientKind: row.client_kind,
            DeviceId: row.device_id,
            DeviceLabel: row.device_label,
            IssuedIp: row.issued_ip,
            IssuedAtUtc: DateTimeOffset.TryParse(row.issued_at_utc, out var issuedAt) ? issuedAt : now,
            ExpiresAtUtc: expiresAt,
            LastSeenAtUtc: updateLastSeen ? now : (DateTimeOffset.TryParse(row.last_seen_at_utc, out var ls) ? ls : (DateTimeOffset?)null),
            RevokedAtUtc: null,
            RevokedReason: null);

        return (true, sessionDto, null);
    }

    public async Task RevokeSessionAsync(string sessionId, string reason = "logout", CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE auth_sessions
            SET revoked_at_utc = @Now,
                revoked_reason = @Reason
            WHERE session_id = @SessionId AND revoked_at_utc IS NULL;
        ", new { Now = DateTimeOffset.UtcNow.ToString("o"), Reason = reason, SessionId = sessionId });
    }

    public async Task RevokeSessionsByRoleAsync(string role, string reason = "credential_changed", CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE auth_sessions
            SET revoked_at_utc = @Now,
                revoked_reason = @Reason
            WHERE role = @Role AND revoked_at_utc IS NULL;
        ", new { Now = DateTimeOffset.UtcNow.ToString("o"), Reason = reason, Role = role });
    }

    public async Task RevokeAllSessionsAsync(string reason = "revoke_all", CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE auth_sessions
            SET revoked_at_utc = @Now,
                revoked_reason = @Reason
            WHERE revoked_at_utc IS NULL;
        ", new { Now = DateTimeOffset.UtcNow.ToString("o"), Reason = reason });
    }

    public async Task<int> PurgeExpiredAndRevokedSessionsAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        string cutoffIso = cutoff.ToString("o");

        using var conn = _connectionFactory.CreateConnection();
        int affected = await conn.ExecuteAsync(@"
            DELETE FROM auth_sessions
            WHERE (expires_at_utc < @Cutoff)
               OR (revoked_at_utc IS NOT NULL AND revoked_at_utc < @Cutoff);
        ", new { Cutoff = cutoffIso });

        if (affected > 0)
        {
            _logger?.LogInformation("Purged {Count} expired/revoked auth sessions older than {Days} days.", affected, olderThan.TotalDays);
        }

        return affected;
    }

    public async Task<IReadOnlyList<SessionDto>> ListActiveSessionsAsync(string? role = null, CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();
        string nowIso = DateTimeOffset.UtcNow.ToString("o");

        string sql = @"
            SELECT session_id, role, client_kind, device_id, device_label, issued_ip,
                   issued_at_utc, expires_at_utc, last_seen_at_utc, revoked_at_utc, revoked_reason
            FROM auth_sessions
            WHERE revoked_at_utc IS NULL AND expires_at_utc > @Now
        ";

        if (!string.IsNullOrEmpty(role))
        {
            sql += " AND role = @Role";
        }

        var rows = await conn.QueryAsync<SessionValidationRow>(sql, new { Now = nowIso, Role = role });
        return rows.Select(r => new SessionDto(
            SessionId: r.session_id,
            Role: r.role,
            ClientKind: r.client_kind,
            DeviceId: r.device_id,
            DeviceLabel: r.device_label,
            IssuedIp: r.issued_ip,
            IssuedAtUtc: DateTimeOffset.TryParse(r.issued_at_utc, out var ia) ? ia : DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.TryParse(r.expires_at_utc, out var ea) ? ea : DateTimeOffset.UtcNow,
            LastSeenAtUtc: DateTimeOffset.TryParse(r.last_seen_at_utc, out var ls) ? ls : (DateTimeOffset?)null,
            RevokedAtUtc: null,
            RevokedReason: null
        )).ToList();
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private sealed class SessionValidationRow
    {
        public string session_id { get; set; } = string.Empty;
        public byte[] token_hash { get; set; } = Array.Empty<byte>();
        public string role { get; set; } = string.Empty;
        public long credential_epoch { get; set; }
        public string client_kind { get; set; } = string.Empty;
        public string? device_id { get; set; }
        public string? device_label { get; set; }
        public string? issued_ip { get; set; }
        public string issued_at_utc { get; set; } = string.Empty;
        public string expires_at_utc { get; set; } = string.Empty;
        public string? last_seen_at_utc { get; set; }
        public string? revoked_at_utc { get; set; }
        public string? revoked_reason { get; set; }
        public long? current_credential_epoch { get; set; }
    }
}
