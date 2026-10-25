using System;
using System.IO;
using FluentAssertions;
using LalabAutoReport.Infrastructure.Hosting;
using Xunit;

namespace LalabAutoReport.Tests;

public class ServerLockDetectorTests
{
    [Fact]
    public void IsServerLockHeld_MissingLock_ReturnsFalse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_lock_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            bool isHeld = ServerLockDetector.IsServerLockHeld(tempDir);
            isHeld.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void IsServerLockHeld_LockHeldByAnotherFileStream_ReturnsTrue()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_lock_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string lockPath = Path.Combine(tempDir, "server.lock");

            using (var fs = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                bool isHeld = ServerLockDetector.IsServerLockHeld(tempDir);
                isHeld.Should().BeTrue();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void IsServerLockHeld_AfterRelease_ReturnsFalse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_lock_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string lockPath = Path.Combine(tempDir, "server.lock");

            var fs = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            ServerLockDetector.IsServerLockHeld(tempDir).Should().BeTrue();

            fs.Dispose();
            ServerLockDetector.IsServerLockHeld(tempDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void IsServerLockHeld_EnvironmentVariableOverride_Honored()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_lock_test_" + Guid.NewGuid().ToString("N"));
        string? originalEnv = Environment.GetEnvironmentVariable("LALAB_SERVER_DATA_DIR");
        try
        {
            Directory.CreateDirectory(tempDir);
            string lockPath = Path.Combine(tempDir, "server.lock");

            Environment.SetEnvironmentVariable("LALAB_SERVER_DATA_DIR", tempDir);

            // Lock file doesn't exist yet
            ServerLockDetector.IsServerLockHeld().Should().BeFalse();

            using (var fs = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                ServerLockDetector.IsServerLockHeld().Should().BeTrue();
            }

            ServerLockDetector.IsServerLockHeld().Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("LALAB_SERVER_DATA_DIR", originalEnv);
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
