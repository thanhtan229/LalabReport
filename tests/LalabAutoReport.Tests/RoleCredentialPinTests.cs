using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LalabAutoReport.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

// PIN verification goes through the hashed role credential store. The former plaintext
// AppSettings check was removed; the same fail-closed rules apply.
public class RoleCredentialPinTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqliteConnectionFactory _connectionFactory;

    public RoleCredentialPinTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "LalabRolePin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _connectionFactory = new SqliteConnectionFactory(Path.Combine(_tempRoot, "pins.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    private async Task<SqliteRoleCredentialStore> CreateStoreAsync()
    {
        await new DatabaseMigrator(_connectionFactory).MigrateAsync();
        return new SqliteRoleCredentialStore(_connectionFactory);
    }

    [Fact]
    public async Task ConfiguredPins_AuthenticateTheirOwnRole_AndRejectOthers()
    {
        var store = await CreateStoreAsync();
        await store.SetPinAsync("Admin", "999888");
        await store.SetPinAsync("Staff", "111222");

        (await store.VerifyAsync("999888")).Should().Be("Admin");
        (await store.VerifyAsync("111222")).Should().Be("Staff");
        (await store.VerifyAsync("wrong_pin")).Should().BeNull();
        (await store.VerifyAsync("")).Should().BeNull();
    }

    [Fact]
    public async Task NoPinsConfigured_FailsClosedForAnyAttempt()
    {
        var store = await CreateStoreAsync();

        (await store.VerifyAsync("123456")).Should().BeNull();
        (await store.VerifyAsync("000000")).Should().BeNull();
        (await store.VerifyAsync("888888")).Should().BeNull();
    }

    [Fact]
    public async Task IdenticalAdminAndStaffPin_IsRejected_SoRoleIsNeverAmbiguous()
    {
        var store = await CreateStoreAsync();
        await store.SetPinAsync("Admin", "123456");

        var act = async () => await store.SetPinAsync("Staff", "123456");

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await store.VerifyAsync("123456")).Should().Be("Admin");
    }
}
