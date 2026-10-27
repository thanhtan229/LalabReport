using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.UI.Services;
using Xunit;

namespace LalabAutoReport.Tests;

public class SystemTrayAndBackgroundDaemonTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteSettingsRepository _settingsRepo;

    public SystemTrayAndBackgroundDaemonTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TrayDaemonTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        string dbPath = Path.Combine(_tempDir, "test_tray_settings.db");
        var connFactory = new SqliteConnectionFactory(dbPath);
        var migrator = new DatabaseMigrator(connFactory);
        migrator.MigrateAsync().GetAwaiter().GetResult();

        _settingsRepo = new SqliteSettingsRepository(connFactory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void AppSettings_DefaultValues_ShouldHaveTrayAndAutoStartEnabled()
    {
        var settings = new AppSettings();

        settings.MinimizeToTrayOnClose.Should().BeTrue("Mặc định nên thu nhỏ xuống khay hệ thống khi đóng để giữ Web Server hoạt động");
        settings.AutoStartWithWindows.Should().BeTrue("Mặc định nên tự khởi động cùng Windows để điện thoại kết nối được ngay");
    }

    [Fact]
    public async Task SqliteSettingsRepository_Persistence_ShouldSaveAndRetrieveTraySettings()
    {
        // 1. Initial defaults
        var settings = await _settingsRepo.GetSettingsAsync();
        settings.MinimizeToTrayOnClose.Should().BeTrue();
        settings.AutoStartWithWindows.Should().BeTrue();

        // 2. Modify to false and save
        settings.MinimizeToTrayOnClose = false;
        settings.AutoStartWithWindows = false;
        await _settingsRepo.SaveSettingsAsync(settings);

        // 3. Reload and verify
        var reloaded = await _settingsRepo.GetSettingsAsync();
        reloaded.MinimizeToTrayOnClose.Should().BeFalse();
        reloaded.AutoStartWithWindows.Should().BeFalse();

        // 4. Modify back to true and save
        reloaded.MinimizeToTrayOnClose = true;
        reloaded.AutoStartWithWindows = true;
        await _settingsRepo.SaveSettingsAsync(reloaded);

        // 5. Verify final reload
        var finalSettings = await _settingsRepo.GetSettingsAsync();
        finalSettings.MinimizeToTrayOnClose.Should().BeTrue();
        finalSettings.AutoStartWithWindows.Should().BeTrue();
    }

    [Theory]
    [InlineData("testhost", false)]
    [InlineData("LalabAutoReport.LoadHarness", false)]
    [InlineData("LalabAutoReport.CrashHarness", false)]
    [InlineData("dotnet", false)]
    [InlineData("LalabAutoReport.UI", true)]
    [InlineData("LalabAutoReport", true)]
    public void WindowsStartupHelper_OnlyRealAppHostMayWriteRunKey(string processName, bool expected)
    {
        WindowsStartupHelper.IsAutoStartHostProcess(processName).Should().Be(expected);
    }

    [Fact]
    public void WindowsStartupHelper_CanCheckWithoutThrowingException()
    {
        // Must execute safely without throwing UnhandledException
        var act = () => WindowsStartupHelper.IsAutoStartConfigured();
        act.Should().NotThrow();
    }

    [Fact]
    public void ISystemTrayManager_MockImplementation_ShouldTrackLifecycle()
    {
        var mockTray = new MockSystemTrayManager();
        mockTray.Initialize();
        mockTray.IsVisible.Should().BeTrue();

        mockTray.UpdateStatus("Web Server: Port 5050");
        mockTray.LastStatus.Should().Be("Web Server: Port 5050");

        mockTray.UpdateRemoteUrl("https://lalab.tinix.io.vn");
        mockTray.LastRemoteUrl.Should().Be("https://lalab.tinix.io.vn");

        mockTray.ShowFirstTimeMinimizedNotification();
        mockTray.NotificationCount.Should().Be(1);

        // Second time should not duplicate notification if managed
        mockTray.ShowFirstTimeMinimizedNotification();
        mockTray.NotificationCount.Should().Be(1);

        mockTray.Dispose();
        mockTray.IsVisible.Should().BeFalse();
    }

    private class MockSystemTrayManager : ISystemTrayManager
    {
        public bool IsVisible { get; private set; }
        public string? LastStatus { get; private set; }
        public string? LastRemoteUrl { get; private set; }
        public int NotificationCount { get; private set; }
        private bool _shownOnce = false;

        public void Initialize()
        {
            IsVisible = true;
        }

        public void ShowFirstTimeMinimizedNotification()
        {
            if (!_shownOnce)
            {
                _shownOnce = true;
                NotificationCount++;
            }
        }

        public void UpdateStatus(string statusText)
        {
            LastStatus = statusText;
        }

        public void UpdateRemoteUrl(string? remoteUrl)
        {
            LastRemoteUrl = remoteUrl;
        }

        public void Dispose()
        {
            IsVisible = false;
        }
    }
}
