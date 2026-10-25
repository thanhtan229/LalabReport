using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LalabAutoReport.ApplicationCore.Commands;

public static class PayloadHasher
{
    public static (string CanonicalJson, string HashHex) ComputeOrderOperationalHash(
        string capability,
        long orderId,
        long expectedRevision,
        string valueKey,
        bool? boolValue = null,
        string? stringValue = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("cap", capability);
            writer.WriteString("entity", "order");
            writer.WriteNumber("id", orderId);
            writer.WriteNumber("expectedRevision", expectedRevision);
            writer.WriteStartObject("value");
            if (boolValue.HasValue)
            {
                writer.WriteBoolean(valueKey, boolValue.Value);
            }
            else
            {
                if (stringValue != null)
                {
                    writer.WriteString(valueKey, stringValue);
                }
                else
                {
                    writer.WriteNull(valueKey);
                }
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        byte[] utf8Bytes = stream.ToArray();
        string canonicalJson = Encoding.UTF8.GetString(utf8Bytes);
        byte[] hashBytes = SHA256.HashData(utf8Bytes);
        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return (canonicalJson, hashHex);
    }
}
