using System;
using System.Security.Cryptography;
using System.Text;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;

namespace LalabAutoReport.Core.Services;

public class MobileAuthService : IMobileAuthService
{
    private readonly byte[] _hmacKey;
    private const int TokenValidityDays = 30;

    public MobileAuthService(ISettingsRepository? settingsRepository = null)
    {
        if (settingsRepository != null)
        {
            try
            {
                var settings = settingsRepository.GetSettingsAsync().GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(settings.MobileAuthSecret))
                {
                    try
                    {
                        var key = Convert.FromBase64String(settings.MobileAuthSecret);
                        if (key.Length >= 32)
                        {
                            _hmacKey = key;
                            return;
                        }
                    }
                    catch
                    {
                        // Fallback to regenerate if corrupted
                    }
                }

                _hmacKey = new byte[32];
                RandomNumberGenerator.Fill(_hmacKey);
                settings.MobileAuthSecret = Convert.ToBase64String(_hmacKey);
                settingsRepository.SaveSettingsAsync(settings).GetAwaiter().GetResult();
                return;
            }
            catch
            {
                // Fallback to in-memory key if repository access fails
            }
        }

        // 32-byte ephemeral key for testing or fallback
        _hmacKey = new byte[32];
        RandomNumberGenerator.Fill(_hmacKey);
    }

    public MobileAuthService(byte[] hmacKey)
    {
        _hmacKey = hmacKey ?? throw new ArgumentNullException(nameof(hmacKey));
    }

    public string GenerateToken(MobileUserRole role) =>
        GenerateToken(role, DateTimeOffset.UtcNow);

    public string GenerateToken(MobileUserRole role, DateTimeOffset issuedAt)
    {
        long issuedAtUtcTicks = issuedAt.UtcTicks;
        string salt = Guid.NewGuid().ToString("N")[..8];
        string payload = $"{(int)role}:{issuedAtUtcTicks}:{salt}";

        using var hmac = new HMACSHA256(_hmacKey);
        byte[] signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        string signature = Convert.ToBase64String(signatureBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        string payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        return $"{payloadBase64}.{signature}";
    }

    public bool TryValidateToken(string token, out MobileUserRole role) =>
        TryValidateToken(token, out role, out _);

    public bool TryValidateToken(string token, out MobileUserRole role, out DateTimeOffset issuedAt)
    {
        role = MobileUserRole.Staff;
        issuedAt = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            string payloadBase64 = parts[0];
            string signature = parts[1];

            // Re-pad base64
            string paddedPayload = payloadBase64.Replace('-', '+').Replace('_', '/');
            switch (paddedPayload.Length % 4)
            {
                case 2: paddedPayload += "=="; break;
                case 3: paddedPayload += "="; break;
            }

            byte[] payloadBytes = Convert.FromBase64String(paddedPayload);
            string payload = Encoding.UTF8.GetString(payloadBytes);

            string[] payloadParts = payload.Split(':');
            if (payloadParts.Length != 3)
            {
                return false;
            }

            if (!int.TryParse(payloadParts[0], out int roleInt) || !Enum.IsDefined(typeof(MobileUserRole), roleInt))
            {
                return false;
            }

            if (!long.TryParse(payloadParts[1], out long issuedAtTicks))
            {
                return false;
            }

            var issuedAtUtc = new DateTime(issuedAtTicks, DateTimeKind.Utc);
            issuedAt = new DateTimeOffset(issuedAtUtc);
            if (DateTime.UtcNow - issuedAtUtc > TimeSpan.FromDays(TokenValidityDays))
            {
                return false; // Expired
            }

            // Verify signature
            using var hmac = new HMACSHA256(_hmacKey);
            byte[] expectedSigBytes = hmac.ComputeHash(payloadBytes);
            string expectedSig = Convert.ToBase64String(expectedSigBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signature), Encoding.UTF8.GetBytes(expectedSig)))
            {
                return false;
            }

            role = (MobileUserRole)roleInt;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
