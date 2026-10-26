using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.UI.ViewModels;
using Xunit;

namespace LalabAutoReport.Tests;

public class SettingsPinSecurityViewModelTests
{
    private class FakeRoleCredentialStore : IRoleCredentialStore
    {
        public bool AdminConfigured { get; set; }
        public bool StaffConfigured { get; set; }
        public string? AdminPin { get; set; }
        public string? StaffPin { get; set; }

        public List<(string Role, string Pin, ActorContext? Actor)> SetPinCalls { get; } = new();
        public List<(string Role, ActorContext? Actor)> ClearPinCalls { get; } = new();

        public Task<RoleCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RoleCredentialStatus(
                AdminConfigured,
                StaffConfigured,
                1,
                1,
                DateTimeOffset.MinValue));
        }

        public Task<CredentialVerificationResult?> VerifyWithEpochAsync(string pin, CancellationToken cancellationToken = default)
        {
            if (AdminConfigured && pin == AdminPin) return Task.FromResult<CredentialVerificationResult?>(new CredentialVerificationResult("Admin", 1));
            if (StaffConfigured && pin == StaffPin) return Task.FromResult<CredentialVerificationResult?>(new CredentialVerificationResult("Staff", 1));
            return Task.FromResult<CredentialVerificationResult?>(null);
        }

        public Task<string?> VerifyAsync(string pin, CancellationToken cancellationToken = default)
        {
            if (AdminConfigured && pin == AdminPin) return Task.FromResult<string?>("Admin");
            if (StaffConfigured && pin == StaffPin) return Task.FromResult<string?>("Staff");
            return Task.FromResult<string?>(null);
        }

        public Task SetPinAsync(string role, string newPin, ActorContext? actor = null, CancellationToken cancellationToken = default)
        {
            string clean = newPin.Trim();
            if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                if (StaffConfigured && string.Equals(clean, StaffPin, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Mã PIN Admin và Nhân viên không được trùng nhau.");
                }
                AdminPin = clean;
                AdminConfigured = true;
            }
            else if (string.Equals(role, "Staff", StringComparison.OrdinalIgnoreCase))
            {
                if (AdminConfigured && string.Equals(clean, AdminPin, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Mã PIN Admin và Nhân viên không được trùng nhau.");
                }
                StaffPin = clean;
                StaffConfigured = true;
            }

            SetPinCalls.Add((role, clean, actor));
            return Task.CompletedTask;
        }

        public Task ClearPinAsync(string role, ActorContext? actor = null, CancellationToken cancellationToken = default)
        {
            if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                AdminPin = null;
                AdminConfigured = false;
            }
            else if (string.Equals(role, "Staff", StringComparison.OrdinalIgnoreCase))
            {
                StaffPin = null;
                StaffConfigured = false;
            }

            ClearPinCalls.Add((role, actor));
            return Task.CompletedTask;
        }

        public Task BumpEpochAsync(string? role = null, string reason = "admin", ActorContext? actor = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private class FakeSettingsRepo : ISettingsRepository
    {
        private readonly IRoleCredentialStore? _credStore;
        public AppSettings Settings { get; set; } = new() { RootFolder = @"C:\FakeRoot" };

        public FakeSettingsRepo(AppSettings? initial = null, IRoleCredentialStore? credStore = null)
        {
            if (initial != null) Settings = initial;
            _credStore = credStore;
        }

        public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            string adminPin = settings.AdminPin?.Trim() ?? string.Empty;
            string staffPin = settings.StaffPin?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(adminPin) && !string.IsNullOrEmpty(staffPin) &&
                string.Equals(adminPin, staffPin, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Mã PIN Admin và Nhân viên không được trùng nhau.");
            }

            if (_credStore != null)
            {
                if (!string.IsNullOrEmpty(adminPin))
                {
                    await _credStore.SetPinAsync("Admin", adminPin, null, cancellationToken);
                }
                if (!string.IsNullOrEmpty(staffPin))
                {
                    await _credStore.SetPinAsync("Staff", staffPin, null, cancellationToken);
                }
            }

            Settings = settings;
        }
    }

    [Fact]
    public async Task LoadSettingsAsync_ReflectsConfiguredFlags_WithBlankInputs()
    {
        // Arrange
        var fakeStore = new FakeRoleCredentialStore
        {
            AdminConfigured = true,
            AdminPin = "123456",
            StaffConfigured = false
        };
        var settings = new AppSettings
        {
            RootFolder = @"C:\TestRoot",
            AdminPinConfigured = true,
            StaffPinConfigured = false
        };
        var repo = new FakeSettingsRepo(settings, fakeStore);
        var vm = new SettingsViewModel(
            repo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            credentialStore: fakeStore);

        // Act
        await vm.LoadSettingsAsync();

        // Assert: write-only inputs are blank, never showing stored PIN
        vm.AdminPin.Should().BeEmpty();
        vm.StaffPin.Should().BeEmpty();

        // Assert: status flags and display text reflect store
        vm.IsAdminPinConfigured.Should().BeTrue();
        vm.IsStaffPinConfigured.Should().BeFalse();
        vm.AdminPinStatusText.Should().Be("Đã thiết lập");
        vm.StaffPinStatusText.Should().Be("Chưa thiết lập");

        // Assert: clear command can-execute
        vm.ClearAdminPinCommand.CanExecute(null).Should().BeTrue();
        vm.ClearStaffPinCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task SaveSettingsAsync_SettingNewAdminPin_CallsStoreAndClearsTextbox()
    {
        // Arrange
        var fakeStore = new FakeRoleCredentialStore();
        var repo = new FakeSettingsRepo(new AppSettings { RootFolder = @"C:\TestRoot" }, fakeStore);
        var vm = new SettingsViewModel(
            repo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            credentialStore: fakeStore);
        vm.ShowMessageAction = (_, _, _, _) => { };

        await vm.LoadSettingsAsync();
        vm.IsAdminPinConfigured.Should().BeFalse();

        // Act: type new PIN and save
        vm.AdminPin = "888888";
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Assert: store was called with the new PIN
        fakeStore.SetPinCalls.Should().ContainSingle(c => c.Role == "Admin" && c.Pin == "888888");

        // Assert: textbox is cleared after saving (write-only)
        vm.AdminPin.Should().BeEmpty();

        // Assert: status is updated to configured
        vm.IsAdminPinConfigured.Should().BeTrue();
        vm.AdminPinStatusText.Should().Be("Đã thiết lập");
        vm.ClearAdminPinCommand.CanExecute(null).Should().BeTrue();
        vm.StatusMessage.Should().Be("Đã lưu cài đặt thành công!");
    }

    [Fact]
    public async Task SaveSettingsAsync_EmptyPin_DoesNotCallSetPin()
    {
        // Arrange
        var fakeStore = new FakeRoleCredentialStore
        {
            AdminConfigured = true,
            AdminPin = "existing_admin_pin"
        };
        var repo = new FakeSettingsRepo(new AppSettings { RootFolder = @"C:\TestRoot" }, fakeStore);
        var vm = new SettingsViewModel(
            repo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            credentialStore: fakeStore);
        vm.ShowMessageAction = (_, _, _, _) => { };

        await vm.LoadSettingsAsync();
        vm.AdminPin.Should().BeEmpty();
        vm.StaffPin.Should().BeEmpty();

        // Act: Save without entering any PIN
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Assert: SetPinAsync was not called
        fakeStore.SetPinCalls.Should().BeEmpty();
        vm.AdminPin.Should().BeEmpty();
        vm.StaffPin.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAdminPin_WhenConfirmed_CallsClearPinAsync_AndUpdatesFlagsAndStatus()
    {
        // Arrange
        var fakeStore = new FakeRoleCredentialStore
        {
            AdminConfigured = true,
            AdminPin = "999999"
        };
        var repo = new FakeSettingsRepo(new AppSettings { RootFolder = @"C:\TestRoot" }, fakeStore);
        var vm = new SettingsViewModel(
            repo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            credentialStore: fakeStore);

        await vm.LoadSettingsAsync();
        vm.IsAdminPinConfigured.Should().BeTrue();

        string? confirmationMessage = null;
        string? confirmationTitle = null;
        vm.ClearPinConfirmFunc = (msg, title, btn, img) =>
        {
            confirmationMessage = msg;
            confirmationTitle = title;
            return MessageBoxResult.Yes;
        };
        vm.ShowMessageAction = (_, _, _, _) => { };

        // Act: execute Clear Admin PIN
        await vm.ClearAdminPinCommand.ExecuteAsync(null);

        // Assert: confirmation naming the role was shown
        confirmationTitle.Should().Contain("Admin");
        confirmationMessage.Should().Contain("Admin");
        confirmationMessage.Should().Contain("PWA");

        // Assert: ClearPinAsync called on store with Actor
        fakeStore.ClearPinCalls.Should().ContainSingle(c => c.Role == "Admin");
        fakeStore.AdminConfigured.Should().BeFalse();

        // Assert: flags and text updated
        vm.IsAdminPinConfigured.Should().BeFalse();
        vm.AdminPinStatusText.Should().Be("Chưa thiết lập");
        vm.ClearAdminPinCommand.CanExecute(null).Should().BeFalse();

        // Assert: status message indicates PWA sessions are logged out
        vm.StatusMessage.Should().Contain("Admin");
        vm.StatusMessage.Should().Contain("PWA");
        vm.StatusMessage.Should().Contain("đăng xuất");
        vm.PinStatusMessage.Should().Contain("PWA");
    }

    [Fact]
    public async Task ClearAdminPin_WhenCancelled_DoesNotCallClearPinAsync()
    {
        // Arrange
        var fakeStore = new FakeRoleCredentialStore
        {
            AdminConfigured = true,
            AdminPin = "999999"
        };
        var repo = new FakeSettingsRepo(new AppSettings { RootFolder = @"C:\TestRoot" }, fakeStore);
        var vm = new SettingsViewModel(
            repo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            credentialStore: fakeStore);

        await vm.LoadSettingsAsync();

        // User clicks "No" on confirmation
        vm.ClearPinConfirmFunc = (_, _, _, _) => MessageBoxResult.No;
        vm.ShowMessageAction = (_, _, _, _) => { };

        // Act
        await vm.ClearAdminPinCommand.ExecuteAsync(null);

        // Assert: store was NOT cleared
        fakeStore.ClearPinCalls.Should().BeEmpty();
        fakeStore.AdminConfigured.Should().BeTrue();
        vm.IsAdminPinConfigured.Should().BeTrue();
        vm.AdminPinStatusText.Should().Be("Đã thiết lập");
        vm.ClearAdminPinCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ClearStaffPin_WhenConfirmed_CallsClearPinAsync_AndUpdatesFlagsAndStatus()
    {
        // Arrange
        var fakeStore = new FakeRoleCredentialStore
        {
            StaffConfigured = true,
            StaffPin = "111111"
        };
        var repo = new FakeSettingsRepo(new AppSettings { RootFolder = @"C:\TestRoot" }, fakeStore);
        var vm = new SettingsViewModel(
            repo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            credentialStore: fakeStore);

        await vm.LoadSettingsAsync();
        vm.IsStaffPinConfigured.Should().BeTrue();

        string? confirmationMessage = null;
        vm.ClearPinConfirmFunc = (msg, title, btn, img) =>
        {
            confirmationMessage = msg;
            return MessageBoxResult.Yes;
        };
        vm.ShowMessageAction = (_, _, _, _) => { };

        // Act
        await vm.ClearStaffPinCommand.ExecuteAsync(null);

        // Assert
        confirmationMessage.Should().Contain("Nhân viên");
        fakeStore.ClearPinCalls.Should().ContainSingle(c => c.Role == "Staff");
        fakeStore.StaffConfigured.Should().BeFalse();

        vm.IsStaffPinConfigured.Should().BeFalse();
        vm.StaffPinStatusText.Should().Be("Chưa thiết lập");
        vm.ClearStaffPinCommand.CanExecute(null).Should().BeFalse();

        vm.StatusMessage.Should().Contain("Nhân viên");
        vm.StatusMessage.Should().Contain("PWA");
        vm.PinStatusMessage.Should().Contain("PWA");
    }

    [Fact]
    public async Task SaveSettingsAsync_DuplicatePinsEnteredSimultaneously_SurfacesErrorWithoutCallingStore()
    {
        // Arrange
        var fakeStore = new FakeRoleCredentialStore();
        var repo = new FakeSettingsRepo(new AppSettings { RootFolder = @"C:\TestRoot" }, fakeStore);
        var vm = new SettingsViewModel(
            repo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            credentialStore: fakeStore);

        string? dialogMsg = null;
        vm.ShowMessageAction = (msg, title, btn, img) => dialogMsg = msg;

        // Act: same PIN for both
        vm.AdminPin = "123456";
        vm.StaffPin = "123456";
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Assert: no store calls
        fakeStore.SetPinCalls.Should().BeEmpty();

        // Assert: error surfaced in status message and dialog
        vm.StatusMessage.Should().Contain("không được trùng nhau");
        vm.PinStatusMessage.Should().Contain("không được trùng nhau");
        dialogMsg.Should().Contain("không được trùng nhau");

        // Assert: inputs not erased so user can fix
        vm.AdminPin.Should().Be("123456");
        vm.StaffPin.Should().Be("123456");
    }

    [Fact]
    public async Task SaveSettingsAsync_DuplicatePinWithExistingRoleInStore_SurfacesException()
    {
        // Arrange: staff PIN is already "654321" in store
        var fakeStore = new FakeRoleCredentialStore
        {
            StaffConfigured = true,
            StaffPin = "654321"
        };
        var repo = new FakeSettingsRepo(new AppSettings { RootFolder = @"C:\TestRoot" }, fakeStore);
        var vm = new SettingsViewModel(
            repo,
            new FakeDatabaseBackupService(),
            new FakeContextMenuService(),
            credentialStore: fakeStore);

        string? dialogMsg = null;
        vm.ShowMessageAction = (msg, title, btn, img) => dialogMsg = msg;

        await vm.LoadSettingsAsync();

        // Act: user tries to set Admin PIN matching existing Staff PIN
        vm.AdminPin = "654321";
        await vm.SaveSettingsCommand.ExecuteAsync(null);

        // Assert: error surfaced from the store / repo
        vm.StatusMessage.Should().Contain("không được trùng nhau");
        vm.PinStatusMessage.Should().Contain("không được trùng nhau");
        dialogMsg.Should().Contain("không được trùng nhau");

        // Assert: user input preserved
        vm.AdminPin.Should().Be("654321");
    }

    [Fact]
    public async Task SqliteIntegration_SettingsViewModel_WithRealSqliteStore_E2E()
    {
        // End-to-end integration test with real SQLite database, migrator, role credential store, and settings repo
        string tempDir = Path.Combine(Path.GetTempPath(), "Lalab_PinE2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "pin_e2e.db");

        try
        {
            var connFactory = new SqliteConnectionFactory(dbPath);
            var migrator = new DatabaseMigrator(connFactory);
            await migrator.MigrateAsync();

            var credStore = new SqliteRoleCredentialStore(connFactory);
            var settingsRepo = new SqliteSettingsRepository(connFactory, credStore);

            var vm = new SettingsViewModel(
                settingsRepo,
                new FakeDatabaseBackupService(),
                new FakeContextMenuService(),
                credentialStore: credStore);
            vm.ShowMessageAction = (_, _, _, _) => { };

            // 1. Initial Load: both should be unconfigured
            await vm.LoadSettingsAsync();
            vm.AdminPin.Should().BeEmpty();
            vm.StaffPin.Should().BeEmpty();
            vm.IsAdminPinConfigured.Should().BeFalse();
            vm.IsStaffPinConfigured.Should().BeFalse();
            vm.AdminPinStatusText.Should().Be("Chưa thiết lập");
            vm.StaffPinStatusText.Should().Be("Chưa thiết lập");

            // 2. Set Admin PIN and save
            vm.AdminPin = "777888";
            await vm.SaveSettingsCommand.ExecuteAsync(null);

            vm.AdminPin.Should().BeEmpty(); // Textbox cleared
            vm.IsAdminPinConfigured.Should().BeTrue();
            vm.AdminPinStatusText.Should().Be("Đã thiết lập");
            vm.ClearAdminPinCommand.CanExecute(null).Should().BeTrue();

            // Verify in credential store
            var verifiedRole = await credStore.VerifyAsync("777888");
            verifiedRole.Should().Be("Admin");

            // 3. Rescan / Reload settings: never returns stored PIN in plaintext
            var freshVm = new SettingsViewModel(
                settingsRepo,
                new FakeDatabaseBackupService(),
                new FakeContextMenuService(),
                credentialStore: credStore);
            freshVm.ShowMessageAction = (_, _, _, _) => { };
            await freshVm.LoadSettingsAsync();
            freshVm.AdminPin.Should().BeEmpty();
            freshVm.StaffPin.Should().BeEmpty();
            freshVm.IsAdminPinConfigured.Should().BeTrue();
            freshVm.IsStaffPinConfigured.Should().BeFalse();

            // 4. Duplicate PIN test with real SQLite store: setting Staff PIN to match Admin PIN throws
            freshVm.StaffPin = "777888";
            await freshVm.SaveSettingsCommand.ExecuteAsync(null);
            freshVm.StatusMessage.Should().Contain("không được trùng nhau");
            freshVm.IsStaffPinConfigured.Should().BeFalse();

            // 5. Clear Admin PIN with confirmation
            freshVm.ClearPinConfirmFunc = (_, _, _, _) => MessageBoxResult.Yes;
            await freshVm.ClearAdminPinCommand.ExecuteAsync(null);

            freshVm.IsAdminPinConfigured.Should().BeFalse();
            freshVm.AdminPinStatusText.Should().Be("Chưa thiết lập");
            freshVm.ClearAdminPinCommand.CanExecute(null).Should().BeFalse();
            freshVm.StatusMessage.Should().Contain("PWA");

            // Verify store now rejects the old PIN
            var clearedVerification = await credStore.VerifyAsync("777888");
            clearedVerification.Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
