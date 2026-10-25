using System;
using System.Security.Cryptography;
using System.Text;

namespace LalabAutoReport.ApplicationCore.Auth;

public static class Pbkdf2PinHasher
{
    public const int DefaultIterations = 600_000;
    public const int SaltSizeBytes = 16;
    public const int HashSizeBytes = 32;
    public const string AlgorithmName = "PBKDF2-SHA256";

    public static byte[] Hash(string pin, byte[] salt, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(salt);

        string normalized = pin.Trim();
        byte[] pinBytes = Encoding.UTF8.GetBytes(normalized);
        return Rfc2898DeriveBytes.Pbkdf2(pinBytes, salt, iterations, HashAlgorithmName.SHA256, HashSizeBytes);
    }

    public static byte[] GenerateSalt()
    {
        byte[] salt = new byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    public static bool Verify(string pin, byte[]? salt, byte[]? storedHash, int iterations = DefaultIterations)
    {
        if (string.IsNullOrWhiteSpace(pin) || salt == null || storedHash == null || salt.Length == 0 || storedHash.Length == 0)
        {
            return false;
        }

        byte[] derived = Hash(pin, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(derived, storedHash);
    }
}
