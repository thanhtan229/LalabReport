using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Infrastructure.Services;
using Xunit;

namespace LalabAutoReport.Tests;

// Live acceptance tests against the published GitHub release. They assert logic and the
// published checksum rather than a hard-coded release version, so a new release does not
// break them. They require network access to GitHub and fail (not skip) when offline.
public sealed class LiveAutoUpdateAcceptanceTests : IDisposable
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/thanhtan229/Lalab-Auto-Report/releases/latest";

    private readonly string _testRoot;
    private readonly string _installedAppDir;
    private readonly string _stagedProfileDir;
    private readonly string _installedExe;
    private readonly HttpClient _httpClient;

    public LiveAutoUpdateAcceptanceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "lalab-live-update-" + Guid.NewGuid().ToString("N"));
        _installedAppDir = Path.Combine(_testRoot, "app");
        _stagedProfileDir = Path.Combine(_testRoot, "profile");
        Directory.CreateDirectory(_installedAppDir);
        Directory.CreateDirectory(_stagedProfileDir);

        _installedExe = Path.Combine(_installedAppDir, "LalabAutoReport.exe");
        File.WriteAllText(_installedExe, "v1.0 original executable fixture");

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "LalabAutoReport-Updater-LiveTest");
    }

    private class StagedUpdateService : GitHubUpdateService
    {
        private readonly Version _mockCurrentVersion;

        public StagedUpdateService(HttpClient client, Version mockCurrentVersion)
            : base(client, null, "thanhtan229", "Lalab-Auto-Report")
        {
            _mockCurrentVersion = mockCurrentVersion;
        }

        public override Version GetCurrentVersion() => _mockCurrentVersion;
    }

    private async Task<(string Tag, string? ChecksumUrl)> GetLatestReleaseAsync()
    {
        using var doc = JsonDocument.Parse(await _httpClient.GetStringAsync(LatestReleaseApiUrl));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;

        string? checksumUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = asset.GetProperty("browser_download_url").GetString();
            }
        }

        return (tag, checksumUrl);
    }

    // Tag "v2.1.0" -> Version(2.1.0.0), matching how the service compares release versions.
    private static Version ToComparableVersion(string tag)
    {
        var parsed = Version.Parse(tag.TrimStart('v', 'V'));
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), 0);
    }

    [Fact]
    public async Task LiveGitHub_WhenCurrentIsV1_DetectsNewerReleaseAndAssets()
    {
        var service = new StagedUpdateService(_httpClient, new Version(1, 0, 0, 0));
        var updateInfo = await service.CheckForUpdateAsync();

        updateInfo.IsUpdateAvailable.Should().BeTrue("any published release newer than 1.0.0.0 should be detected");
        updateInfo.LatestVersion.Should().StartWith("v");
        ToComparableVersion(updateInfo.LatestVersion).Should().BeGreaterThan(new Version(1, 0, 0, 0));
        updateInfo.ReleaseName.Should().NotBeNullOrWhiteSpace();
        updateInfo.DownloadUrl.Should().NotBeNullOrWhiteSpace();
        updateInfo.DownloadUrl.Should().EndWith(".exe");
        updateInfo.FileSizeBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LiveGitHub_WhenCurrentIsLatest_ReportsNoUpdate()
    {
        var (tag, _) = await GetLatestReleaseAsync();
        tag.Should().NotBeNullOrWhiteSpace();

        var service = new StagedUpdateService(_httpClient, ToComparableVersion(tag));
        var updateInfo = await service.CheckForUpdateAsync();

        updateInfo.IsUpdateAvailable.Should().BeFalse("an app already on the latest release should not prompt for update");
        updateInfo.LatestVersion.Should().Be(tag);
    }

    [Fact]
    public async Task LiveGitHub_DownloadAndVerifyChecksum_MatchesPublishedRelease()
    {
        var (_, checksumUrl) = await GetLatestReleaseAsync();
        checksumUrl.Should().NotBeNullOrWhiteSpace("the latest release must publish <name>.exe.sha256 next to the exe");

        var service = new StagedUpdateService(_httpClient, new Version(1, 0, 0, 0));
        var updateInfo = await service.CheckForUpdateAsync();
        updateInfo.IsUpdateAvailable.Should().BeTrue();

        string tempDownload = Path.Combine(_testRoot, "downloaded.exe");
        using (var response = await _httpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(tempDownload);
            await content.CopyToAsync(file);
        }

        new FileInfo(tempDownload).Length.Should().Be(updateInfo.FileSizeBytes);

        // Published checksum format: "<sha256>  <file name>"
        string publishedLine = await _httpClient.GetStringAsync(checksumUrl);
        string publishedHash = publishedLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        string downloadedHash = await ComputeSha256Async(tempDownload);
        downloadedHash.Should().BeEquivalentTo(publishedHash, "the downloaded exe must match the published checksum");

        // Validate PE headers via UpdateReplacement.PrepareAsync
        string script = await UpdateReplacement.PrepareAsync(tempDownload, _installedExe, 0, "", restart: false);
        File.Exists(script).Should().BeTrue();

        // Execute updater script
        var startInfo = UpdateReplacement.CreateStartInfo(script);
        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0);

        // Verify target replaced
        string installedHash = await ComputeSha256Async(_installedExe);
        installedHash.Should().BeEquivalentTo(publishedHash);

        // Verify backup
        string backupFile = Directory.GetFiles(_installedAppDir, "*.previous-*").Should().ContainSingle().Subject;
        File.ReadAllText(backupFile).Should().Be("v1.0 original executable fixture");
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    [Fact]
    public async Task FailureRecovery_TamperedDownload_IsRefusedAndOriginalPreserved()
    {
        string tamperedSource = Path.Combine(_testRoot, "tampered.exe");
        // Create valid PE from test assembly
        File.Copy(typeof(UpdateReplacement).Assembly.Location, tamperedSource);

        string script = await UpdateReplacement.PrepareAsync(tamperedSource, _installedExe, 0, "", restart: false);

        // Tamper source after plan was generated
        await File.AppendAllTextAsync(tamperedSource, "tampered-content");

        var startInfo = UpdateReplacement.CreateStartInfo(script);
        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(1);

        // Original executable must remain unchanged
        File.ReadAllText(_installedExe).Should().Be("v1.0 original executable fixture");
    }

    [Fact]
    public async Task FailureRecovery_LockedDestination_IsSafelyPreservedForRetry()
    {
        string validSource = Path.Combine(_testRoot, "valid.exe");
        File.Copy(typeof(UpdateReplacement).Assembly.Location, validSource);

        string script = await UpdateReplacement.PrepareAsync(validSource, _installedExe, 0, "", restart: false);

        // Lock destination
        using (var locked = new FileStream(_installedExe, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var startInfo = UpdateReplacement.CreateStartInfo(script);
            using var process = Process.Start(startInfo)!;
            await process.WaitForExitAsync();
            process.ExitCode.Should().Be(1);
        }

        File.ReadAllText(_installedExe).Should().Be("v1.0 original executable fixture");
        File.Exists(validSource).Should().BeTrue();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        try { Directory.Delete(_testRoot, recursive: true); } catch { }
    }
}
