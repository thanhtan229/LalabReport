using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class AuthCredentialMigrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _tempDbPath;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseBackupService _backupService;

    public AuthCredentialMigrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabAuthMigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _tempDbPath = Path.Combine(_tempRoot, "test_auth.db");
        _connectionFactory = new SqliteConnectionFactory(_tempDbPath);
        _backupService = new DatabaseBackupService(_connectionFactory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task V25_HashesExistingPlaintextPins_AndBlanksAppSettings()
    {
        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        await migrator.MigrateAsync();

        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync("DROP TABLE IF EXISTS auth_credentials; DROP TABLE IF EXISTS auth_state;");
            await conn.ExecuteAsync("DELETE FROM schema_migrations WHERE version >= 25;");
            await conn.ExecuteAsync("INSERT OR REPLACE INTO app_settings (key, value) VALUES ('AdminPin', '123456'), ('StaffPin', '654321');");
        }

        await migrator.MigrateAsync();

        using (var conn = _connectionFactory.CreateConnection())
        {
            var adminPin = await conn.QuerySingleOrDefaultAsync<string>("SELECT value FROM app_settings WHERE key = 'AdminPin'");
            var staffPin = await conn.QuerySingleOrDefaultAsync<string>("SELECT value FROM app_settings WHERE key = 'StaffPin'");
            string.IsNullOrEmpty(adminPin).Should().BeTrue();
            string.IsNullOrEmpty(staffPin).Should().BeTrue();

            var rows = (await conn.QueryAsync<(string role, byte[] hash, byte[] salt, int iterations, int credential_epoch)>(
                "SELECT role, hash, salt, iterations, credential_epoch FROM auth_credentials ORDER BY role")).AsList();

            rows.Should().HaveCount(2);

            var adminRow = rows.Find(r => r.role == "Admin");
            adminRow.hash.Should().NotBeNullOrEmpty();
            adminRow.salt.Should().NotBeNullOrEmpty();
            adminRow.iterations.Should().Be(600_000);
            adminRow.credential_epoch.Should().Be(1);

            Pbkdf2PinHasher.Verify("123456", adminRow.salt, adminRow.hash, adminRow.iterations).Should().BeTrue();

            var staffRow = rows.Find(r => r.role == "Staff");
            staffRow.hash.Should().NotBeNullOrEmpty();
            staffRow.salt.Should().NotBeNullOrEmpty();
            staffRow.iterations.Should().Be(600_000);
            staffRow.credential_epoch.Should().Be(1);

            Pbkdf2PinHasher.Verify("654321", staffRow.salt, staffRow.hash, staffRow.iterations).Should().BeTrue();
        }
    }

    [Fact]
    public async Task V25_NoPinsConfigured_CreatesRowsWithNullHash_LoginDisabled()
    {
        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        await migrator.MigrateAsync();

        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync("DROP TABLE IF EXISTS auth_credentials; DROP TABLE IF EXISTS auth_state;");
            await conn.ExecuteAsync("DELETE FROM schema_migrations WHERE version >= 25;");
            await conn.ExecuteAsync("INSERT OR REPLACE INTO app_settings (key, value) VALUES ('AdminPin', ''), ('StaffPin', '');");
        }

        await migrator.MigrateAsync();

        using (var conn = _connectionFactory.CreateConnection())
        {
            var rows = (await conn.QueryAsync<(string role, byte[]? hash)>(
                "SELECT role, hash FROM auth_credentials ORDER BY role")).AsList();

            rows.Should().HaveCount(2);
            rows.Find(r => r.role == "Admin").hash.Should().BeNull();
            rows.Find(r => r.role == "Staff").hash.Should().BeNull();
        }
    }

    [Fact]
    public async Task V25_IsIdempotent_RerunMigrateDoesNothing()
    {
        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        await migrator.MigrateAsync();

        var ex = await Record.ExceptionAsync(async () => await migrator.MigrateAsync());
        ex.Should().BeNull();

        using var conn = _connectionFactory.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth_credentials");
        count.Should().Be(2);
    }

    [Fact]
    public async Task PlaintextScrubber_RehashesPinReenteredByOlderBinary()
    {
        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        await migrator.MigrateAsync();

        using (var conn = _connectionFactory.CreateConnection())
        {
            await conn.ExecuteAsync("INSERT OR REPLACE INTO app_settings (key, value) VALUES ('AdminPin', '999888');");
        }

        var store = new SqliteRoleCredentialStore(_connectionFactory);
        var scrubber = new CredentialPlaintextScrubber(_connectionFactory, store);

        await scrubber.ScrubAsync();

        using (var conn = _connectionFactory.CreateConnection())
        {
            var adminPin = await conn.QuerySingleOrDefaultAsync<string>("SELECT value FROM app_settings WHERE key = 'AdminPin'");
            string.IsNullOrEmpty(adminPin).Should().BeTrue();
        }

        var role = await store.VerifyAsync("999888");
        role.Should().Be("Admin");
    }

    [Fact]
    public void Pbkdf2_VerifiesCorrectPin_RejectsWrong_UsesFixedTimeCompare()
    {
        string pin = "888888";
        byte[] salt = Pbkdf2PinHasher.GenerateSalt();
        byte[] hash = Pbkdf2PinHasher.Hash(pin, salt, Pbkdf2PinHasher.DefaultIterations);

        Pbkdf2PinHasher.Verify("888888", salt, hash, Pbkdf2PinHasher.DefaultIterations).Should().BeTrue();
        Pbkdf2PinHasher.Verify("111111", salt, hash, Pbkdf2PinHasher.DefaultIterations).Should().BeFalse();
        Pbkdf2PinHasher.Verify("888889", salt, hash, Pbkdf2PinHasher.DefaultIterations).Should().BeFalse();
        Pbkdf2PinHasher.Verify("", salt, hash, Pbkdf2PinHasher.DefaultIterations).Should().BeFalse();
    }

    [Fact]
    public async Task SetPin_RejectsDuplicateOfOtherRole()
    {
        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        await migrator.MigrateAsync();

        var store = new SqliteRoleCredentialStore(_connectionFactory);
        await store.SetPinAsync("Admin", "123456");

        var act = async () => await store.SetPinAsync("Staff", "123456");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*không được trùng nhau*");
    }

    [Fact]
    public async Task SetPin_SameValue_DoesNotBumpEpoch()
    {
        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        await migrator.MigrateAsync();

        var store = new SqliteRoleCredentialStore(_connectionFactory);
        await store.SetPinAsync("Admin", "123456");

        using (var conn = _connectionFactory.CreateConnection())
        {
            var epoch1 = await conn.QuerySingleAsync<int>("SELECT credential_epoch FROM auth_credentials WHERE role = 'Admin'");
            
            await store.SetPinAsync("Admin", "123456");

            var epoch2 = await conn.QuerySingleAsync<int>("SELECT credential_epoch FROM auth_credentials WHERE role = 'Admin'");
            epoch2.Should().Be(epoch1);
        }
    }

    [Fact]
    public async Task SettingsRepository_GetSettings_NeverReturnsPin_SetsConfiguredFlags()
    {
        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        await migrator.MigrateAsync();

        var store = new SqliteRoleCredentialStore(_connectionFactory);
        await store.SetPinAsync("Admin", "123456");

        var settingsRepo = new SqliteSettingsRepository(_connectionFactory, store);
        var settings = await settingsRepo.GetSettingsAsync();

        settings.AdminPin.Should().BeEmpty();
        settings.StaffPin.Should().BeEmpty();
        settings.AdminPinConfigured.Should().BeTrue();
        settings.StaffPinConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task SettingsRepository_SaveEmptyPin_LeavesCredentialUnchanged()
    {
        var migrator = new DatabaseMigrator(_connectionFactory, _backupService);
        await migrator.MigrateAsync();

        var store = new SqliteRoleCredentialStore(_connectionFactory);
        await store.SetPinAsync("Admin", "123456");

        var settingsRepo = new SqliteSettingsRepository(_connectionFactory, store);
        var settings = await settingsRepo.GetSettingsAsync();

        await settingsRepo.SaveSettingsAsync(settings);

        var role = await store.VerifyAsync("123456");
        role.Should().Be("Admin");
    }

    [Fact]
    public void Migrator_LatestVersionEqualsMaxMigration()
    {
        DatabaseMigrator.LatestVersion.Should().Be(29);
    }
}
