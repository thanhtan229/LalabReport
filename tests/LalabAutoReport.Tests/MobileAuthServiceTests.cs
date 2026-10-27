using System.Text;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Services;
using Xunit;

namespace LalabAutoReport.Tests;

public class MobileAuthServiceTests
{
    [Fact]
    public void GenerateAndValidateToken_AdminRole_ShouldSucceed()
    {
        var authService = new MobileAuthService();

        string token = authService.GenerateToken(MobileUserRole.Admin);

        token.Should().NotBeNullOrWhiteSpace();
        bool isValid = authService.TryValidateToken(token, out var role);

        isValid.Should().BeTrue();
        role.Should().Be(MobileUserRole.Admin);
    }

    [Fact]
    public void GenerateAndValidateToken_StaffRole_ShouldSucceed()
    {
        var authService = new MobileAuthService();

        string token = authService.GenerateToken(MobileUserRole.Staff);

        token.Should().NotBeNullOrWhiteSpace();
        bool isValid = authService.TryValidateToken(token, out var role);

        isValid.Should().BeTrue();
        role.Should().Be(MobileUserRole.Staff);
    }

    [Fact]
    public void ValidateToken_TamperedSignature_ShouldFail()
    {
        var authService = new MobileAuthService();
        string token = authService.GenerateToken(MobileUserRole.Admin);

        string[] parts = token.Split('.');
        string tamperedToken = parts[0] + ".badSignature123";

        bool isValid = authService.TryValidateToken(tamperedToken, out _);
        isValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateToken_InvalidOrEmpty_ShouldFail()
    {
        var authService = new MobileAuthService();

        authService.TryValidateToken("", out _).Should().BeFalse();
        authService.TryValidateToken("   ", out _).Should().BeFalse();
        authService.TryValidateToken("not_a_valid_token", out _).Should().BeFalse();
        authService.TryValidateToken("part1.part2.part3", out _).Should().BeFalse();
    }

    [Fact]
    public void PersistentSecret_ShouldSurviveReinstantiation_AndValidateAcrossInstances()
    {
        var settingsRepo = new InMemorySettingsRepo();

        // Instance 1: Should generate secret and save to settings
        var instance1 = new MobileAuthService(settingsRepo);
        string token1 = instance1.GenerateToken(MobileUserRole.Admin);

        // Instance 2 (simulating app restart / new DI creation): Should load the saved secret
        var instance2 = new MobileAuthService(settingsRepo);

        // Token from instance 1 must validate successfully in instance 2
        bool isValid = instance2.TryValidateToken(token1, out var role);
        isValid.Should().BeTrue();
        role.Should().Be(MobileUserRole.Admin);
    }

    private class InMemorySettingsRepo : LalabAutoReport.Core.Interfaces.ISettingsRepository
    {
        public AppSettings Settings { get; set; } = new();

        public System.Threading.Tasks.Task<AppSettings> GetSettingsAsync(System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult(Settings);

        public System.Threading.Tasks.Task SaveSettingsAsync(AppSettings settings, System.Threading.CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }
}
