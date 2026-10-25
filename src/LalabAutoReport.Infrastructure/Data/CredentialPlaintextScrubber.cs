using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Data;

public sealed class CredentialPlaintextScrubber
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IRoleCredentialStore _credentialStore;
    private readonly ILogger<CredentialPlaintextScrubber>? _logger;

    public CredentialPlaintextScrubber(
        ISqliteConnectionFactory connectionFactory,
        IRoleCredentialStore credentialStore,
        ILogger<CredentialPlaintextScrubber>? logger = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger;
    }

    public async Task ScrubAsync(CancellationToken cancellationToken = default)
    {
        using var conn = _connectionFactory.CreateConnection();

        // Check if app_settings table exists
        int tableExists = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_settings';");
        if (tableExists == 0)
        {
            return;
        }

        var pinRows = (await conn.QueryAsync<(string Key, string Value)>(
            "SELECT key, value FROM app_settings WHERE key IN ('AdminPin', 'StaffPin');"
        )).ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

        string adminPin = pinRows.TryGetValue("AdminPin", out var ap) ? (ap?.Trim() ?? "") : "";
        string staffPin = pinRows.TryGetValue("StaffPin", out var sp) ? (sp?.Trim() ?? "") : "";

        if (string.IsNullOrEmpty(adminPin) && string.IsNullOrEmpty(staffPin))
        {
            return;
        }

        bool scrubbed = false;

        if (!string.IsNullOrEmpty(adminPin))
        {
            try
            {
                await _credentialStore.SetPinAsync("Admin", adminPin, null, cancellationToken);
                await conn.ExecuteAsync("UPDATE app_settings SET value = '' WHERE key = 'AdminPin';");
                scrubbed = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to migrate plaintext Admin PIN to auth_credentials.");
            }
        }

        if (!string.IsNullOrEmpty(staffPin))
        {
            try
            {
                await _credentialStore.SetPinAsync("Staff", staffPin, null, cancellationToken);
                await conn.ExecuteAsync("UPDATE app_settings SET value = '' WHERE key = 'StaffPin';");
                scrubbed = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to migrate plaintext Staff PIN to auth_credentials.");
            }
        }

        if (scrubbed)
        {
            _logger?.LogInformation("Credential scrubber finished migrating plaintext PINs to hashed auth_credentials.");
        }
    }
}
