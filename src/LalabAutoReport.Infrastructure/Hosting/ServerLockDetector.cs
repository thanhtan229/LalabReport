using System;
using System.IO;

namespace LalabAutoReport.Infrastructure.Hosting;

public static class ServerLockDetector
{
    public static bool IsServerLockHeld(string? serverDataDirectory = null)
    {
        string? dir = serverDataDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Environment.GetEnvironmentVariable("LALAB_SERVER_DATA_DIR");
        }

        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LalabAutoReport",
                "Server");
        }

        string lockFilePath = Path.Combine(dir, "server.lock");
        if (!File.Exists(lockFilePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(lockFilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }
}
