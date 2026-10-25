using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Data;

public sealed class SqliteRoleCredentialStore : IRoleCredentialStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ILogger<SqliteRoleCredentialStore>? _logger;

    private static readonly byte[] DummySalt = new byte[16];
    private static readonly byte[] DummyHash = new byte[32];

    static SqliteRoleCredentialStore()
    {
        RandomNumberGenerator.Fill(DummySalt);
        RandomNumberGenerator.Fill(DummyHash);
    }

    public SqliteRoleCredentialStore(
        ISqliteConnectionFactory connectionFactory,
        ILogger<SqliteRoleCredentialStore>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger;
    }

    public async Task<RoleCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();

        var rows = (await conn.QueryAsync<CredentialRow>(@"
            SELECT role, algorithm, iterations, salt, hash, credential_epoch, updated_at_utc
            FROM auth_credentials;
        ")).ToDictionary(r => r.role, StringComparer.OrdinalIgnoreCase);

        var state = await conn.QuerySingleOrDefaultAsync<AuthStateRow>(@"
            SELECT id, legacy_tokens_issued_before_utc, legacy_tokens_accepted_until_utc
            FROM auth_state WHERE id = 1;
        ");

        bool adminConfigured = rows.TryGetValue("Admin", out var adminRow) && adminRow.hash != null && adminRow.hash.Length > 0;
        bool staffConfigured = rows.TryGetValue("Staff", out var staffRow) && staffRow.hash != null && staffRow.hash.Length > 0;
        long adminEpoch = adminRow?.credential_epoch ?? 1;
        long staffEpoch = staffRow?.credential_epoch ?? 1;

        DateTimeOffset legacyValidUntil = DateTimeOffset.MinValue;
        DateTimeOffset legacyIssuedBefore = DateTimeOffset.MinValue;
        if (state != null)
        {
            if (DateTimeOffset.TryParse(state.legacy_tokens_accepted_until_utc, out var parsedUntil))
            {
                legacyValidUntil = parsedUntil;
            }
            if (DateTimeOffset.TryParse(state.legacy_tokens_issued_before_utc, out var parsedIssuedBefore))
            {
                legacyIssuedBefore = parsedIssuedBefore;
            }
        }

        return new RoleCredentialStatus(
            adminConfigured,
            staffConfigured,
            adminEpoch,
            staffEpoch,
            legacyValidUntil,
            legacyIssuedBefore);
    }

    public async Task<string?> VerifyAsync(string pin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pin))
            return null;

        using var conn = _connectionFactory.CreateConnection();
        var rows = (await conn.QueryAsync<CredentialRow>(@"
            SELECT role, algorithm, iterations, salt, hash, credential_epoch, updated_at_utc
            FROM auth_credentials;
        ")).ToDictionary(r => r.role, StringComparer.OrdinalIgnoreCase);

        // Constant-time check: always compute PBKDF2 for Admin and Staff
        rows.TryGetValue("Admin", out var adminRow);
        rows.TryGetValue("Staff", out var staffRow);

        bool adminMatch;
        if (adminRow?.hash != null && adminRow.salt != null)
        {
            adminMatch = Pbkdf2PinHasher.Verify(pin, adminRow.salt, adminRow.hash, adminRow.iterations);
        }
        else
        {
            Pbkdf2PinHasher.Verify(pin, DummySalt, DummyHash, Pbkdf2PinHasher.DefaultIterations);
            adminMatch = false;
        }

        bool staffMatch;
        if (staffRow?.hash != null && staffRow.salt != null)
        {
            staffMatch = Pbkdf2PinHasher.Verify(pin, staffRow.salt, staffRow.hash, staffRow.iterations);
        }
        else
        {
            Pbkdf2PinHasher.Verify(pin, DummySalt, DummyHash, Pbkdf2PinHasher.DefaultIterations);
            staffMatch = false;
        }

        if (adminMatch)
            return "Admin";
        if (staffMatch)
            return "Staff";

        return null;
    }

    public async Task SetPinAsync(string role, string newPin, ActorContext? actor = null, CancellationToken cancellationToken = default)
    {
        string normRole = NormalizeRole(role);

        if (string.IsNullOrWhiteSpace(newPin))
        {
            await ClearPinAsync(normRole, actor, cancellationToken);
            return;
        }

        string cleanPin = newPin.Trim();
        string otherRole = normRole == "Admin" ? "Staff" : "Admin";

        using var conn = _connectionFactory.CreateConnection();
        var rows = (await conn.QueryAsync<CredentialRow>(@"
            SELECT role, algorithm, iterations, salt, hash, credential_epoch, updated_at_utc
            FROM auth_credentials;
        ")).ToDictionary(r => r.role, StringComparer.OrdinalIgnoreCase);

        // 1. Verify candidate against other role to prevent duplicate PINs
        if (rows.TryGetValue(otherRole, out var otherRow) && otherRow.hash != null && otherRow.salt != null)
        {
            if (Pbkdf2PinHasher.Verify(cleanPin, otherRow.salt, otherRow.hash, otherRow.iterations))
            {
                throw new InvalidOperationException("Mã PIN Admin và Nhân viên không được trùng nhau.");
            }
        }

        // 2. Check if candidate is identical to current role's PIN
        if (rows.TryGetValue(normRole, out var currentRow) && currentRow.hash != null && currentRow.salt != null)
        {
            if (Pbkdf2PinHasher.Verify(cleanPin, currentRow.salt, currentRow.hash, currentRow.iterations))
            {
                // Identical PIN: no epoch bump, no revocation, no change
                return;
            }
        }

        // 3. Compute hash before opening transaction
        byte[] salt = Pbkdf2PinHasher.GenerateSalt();
        byte[] hash = Pbkdf2PinHasher.Hash(cleanPin, salt, Pbkdf2PinHasher.DefaultIterations);
        string nowIso = DateTimeOffset.UtcNow.ToString("o");

        SqliteAuditLogger.EnsureTable(conn);
        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(@"
                UPDATE auth_credentials
                SET algorithm = 'PBKDF2-SHA256',
                    iterations = @Iterations,
                    salt = @Salt,
                    hash = @Hash,
                    credential_epoch = credential_epoch + 1,
                    updated_at_utc = @UpdatedAt
                WHERE role = @Role;
            ", new
            {
                Iterations = Pbkdf2PinHasher.DefaultIterations,
                Salt = salt,
                Hash = hash,
                UpdatedAt = nowIso,
                Role = normRole
            }, transaction: tx);

            await conn.ExecuteAsync(@"
                UPDATE auth_sessions
                SET revoked_at_utc = @Now,
                    revoked_reason = 'credential_changed'
                WHERE role = @Role AND revoked_at_utc IS NULL;
            ", new { Now = nowIso, Role = normRole }, transaction: tx);

            await conn.ExecuteAsync(@"
                UPDATE auth_state
                SET legacy_tokens_accepted_until_utc = @Now
                WHERE id = 1;
            ", new { Now = nowIso }, transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO audit_log (
                    occurred_at_utc, action, outcome, reason,
                    actor_kind, actor_role, actor_session_id, actor_device_id, actor_label, client_ip,
                    entity_type
                ) VALUES (
                    @Now, 'auth.pinChanged', 'Committed', 'pin_updated',
                    @ActorKind, @ActorRole, @ActorSessionId, @ActorDeviceId, @ActorLabel, @ClientIp,
                    'credential'
                );
            ", new
            {
                Now = nowIso,
                ActorKind = actor?.Kind ?? "system",
                ActorRole = actor?.Role ?? normRole,
                ActorSessionId = actor?.SessionId,
                ActorDeviceId = actor?.DeviceId,
                ActorLabel = actor?.Role == "Admin" ? "Chủ tiệm" : (actor?.Role == "Staff" ? "Nhân viên" : null),
                ClientIp = actor?.ClientIp
            }, transaction: tx);

            tx.Commit();
            _logger?.LogInformation("PIN for role {Role} updated successfully. Epoch bumped and active sessions revoked.", normRole);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task ClearPinAsync(string role, ActorContext? actor = null, CancellationToken cancellationToken = default)
    {
        string normRole = NormalizeRole(role);
        using var conn = _connectionFactory.CreateConnection();

        var existing = await conn.QuerySingleOrDefaultAsync<CredentialRow>(@"
            SELECT role, algorithm, iterations, salt, hash, credential_epoch, updated_at_utc
            FROM auth_credentials WHERE role = @Role;
        ", new { Role = normRole });

        if (existing == null || existing.hash == null)
        {
            return; // Already cleared / not configured
        }

        string nowIso = DateTimeOffset.UtcNow.ToString("o");
        SqliteAuditLogger.EnsureTable(conn);
        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(@"
                UPDATE auth_credentials
                SET salt = NULL,
                    hash = NULL,
                    credential_epoch = credential_epoch + 1,
                    updated_at_utc = @UpdatedAt
                WHERE role = @Role;
            ", new { UpdatedAt = nowIso, Role = normRole }, transaction: tx);

            await conn.ExecuteAsync(@"
                UPDATE auth_sessions
                SET revoked_at_utc = @Now,
                    revoked_reason = 'credential_changed'
                WHERE role = @Role AND revoked_at_utc IS NULL;
            ", new { Now = nowIso, Role = normRole }, transaction: tx);

            await conn.ExecuteAsync(@"
                UPDATE auth_state
                SET legacy_tokens_accepted_until_utc = @Now
                WHERE id = 1;
            ", new { Now = nowIso }, transaction: tx);

            await conn.ExecuteAsync(@"
                INSERT INTO audit_log (
                    occurred_at_utc, action, outcome, reason,
                    actor_kind, actor_role, actor_session_id, actor_device_id, actor_label, client_ip,
                    entity_type
                ) VALUES (
                    @Now, 'auth.pinChanged', 'Committed', 'pin_cleared',
                    @ActorKind, @ActorRole, @ActorSessionId, @ActorDeviceId, @ActorLabel, @ClientIp,
                    'credential'
                );
            ", new
            {
                Now = nowIso,
                ActorKind = actor?.Kind ?? "system",
                ActorRole = actor?.Role ?? normRole,
                ActorSessionId = actor?.SessionId,
                ActorDeviceId = actor?.DeviceId,
                ActorLabel = actor?.Role == "Admin" ? "Chủ tiệm" : (actor?.Role == "Staff" ? "Nhân viên" : null),
                ClientIp = actor?.ClientIp
            }, transaction: tx);

            tx.Commit();
            _logger?.LogInformation("PIN for role {Role} cleared successfully.", normRole);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task BumpEpochAsync(string? role = null, string reason = "admin", ActorContext? actor = null, CancellationToken cancellationToken = default)
    {
        string? normRole = role != null ? NormalizeRole(role) : null;
        using var conn = _connectionFactory.CreateConnection();
        string nowIso = DateTimeOffset.UtcNow.ToString("o");

        SqliteAuditLogger.EnsureTable(conn);
        using var tx = conn.BeginTransaction();
        try
        {
            if (normRole != null)
            {
                await conn.ExecuteAsync(@"
                    UPDATE auth_credentials
                    SET credential_epoch = credential_epoch + 1,
                        updated_at_utc = @Now
                    WHERE role = @Role;
                ", new { Now = nowIso, Role = normRole }, transaction: tx);

                await conn.ExecuteAsync(@"
                    UPDATE auth_sessions
                    SET revoked_at_utc = @Now,
                        revoked_reason = @Reason
                    WHERE role = @Role AND revoked_at_utc IS NULL;
                ", new { Now = nowIso, Role = normRole, Reason = reason }, transaction: tx);
            }
            else
            {
                await conn.ExecuteAsync(@"
                    UPDATE auth_credentials
                    SET credential_epoch = credential_epoch + 1,
                        updated_at_utc = @Now;
                ", new { Now = nowIso }, transaction: tx);

                await conn.ExecuteAsync(@"
                    UPDATE auth_sessions
                    SET revoked_at_utc = @Now,
                        revoked_reason = @Reason
                    WHERE revoked_at_utc IS NULL;
                ", new { Now = nowIso, Reason = reason }, transaction: tx);

                await conn.ExecuteAsync(@"
                    UPDATE auth_state
                    SET legacy_tokens_accepted_until_utc = @Now
                    WHERE id = 1;
                ", new { Now = nowIso }, transaction: tx);
            }

            await conn.ExecuteAsync(@"
                INSERT INTO audit_log (
                    occurred_at_utc, action, outcome, reason,
                    actor_kind, actor_role, actor_session_id, actor_device_id, actor_label, client_ip
                ) VALUES (
                    @Now, 'auth.revokeAll', 'Succeeded', @Reason,
                    @ActorKind, @ActorRole, @ActorSessionId, @ActorDeviceId, @ActorLabel, @ClientIp
                );
            ", new
            {
                Now = nowIso,
                Reason = reason,
                ActorKind = actor?.Kind ?? "system",
                ActorRole = actor?.Role,
                ActorSessionId = actor?.SessionId,
                ActorDeviceId = actor?.DeviceId,
                ActorLabel = actor?.Role == "Admin" ? "Chủ tiệm" : (actor?.Role == "Staff" ? "Nhân viên" : null),
                ClientIp = actor?.ClientIp
            }, transaction: tx);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static string NormalizeRole(string role)
    {
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return "Admin";
        if (string.Equals(role, "Staff", StringComparison.OrdinalIgnoreCase))
            return "Staff";
        throw new ArgumentException($"Vai trò không hợp lệ: '{role}'. Chỉ chấp nhận 'Admin' hoặc 'Staff'.", nameof(role));
    }

    private sealed class CredentialRow
    {
        public string role { get; set; } = string.Empty;
        public string algorithm { get; set; } = string.Empty;
        public int iterations { get; set; }
        public byte[]? salt { get; set; }
        public byte[]? hash { get; set; }
        public long credential_epoch { get; set; }
        public string updated_at_utc { get; set; } = string.Empty;
    }

    private sealed class AuthStateRow
    {
        public int id { get; set; }
        public string legacy_tokens_issued_before_utc { get; set; } = string.Empty;
        public string legacy_tokens_accepted_until_utc { get; set; } = string.Empty;
    }
}
