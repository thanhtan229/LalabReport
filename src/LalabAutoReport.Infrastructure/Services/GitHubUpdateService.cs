using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LalabAutoReport.Infrastructure.Services;

public class GitHubUpdateService : IUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubUpdateService> _logger;
    private readonly string _repoOwner;
    private readonly string _repoName;

    public GitHubUpdateService(
        HttpClient? httpClient = null,
        ILogger<GitHubUpdateService>? logger = null,
        string repoOwner = "thanhtan229",
        string repoName = "Lalab-Auto-Report")
    {
        _httpClient = httpClient ?? new HttpClient();
        _logger = logger ?? NullLogger<GitHubUpdateService>.Instance;
        _repoOwner = repoOwner;
        _repoName = repoName;

        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "LalabAutoReport-Updater");
        }
    }

    public virtual Version GetCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return asm.GetName().Version ?? new Version(1, 0, 0);
    }

    public async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();
        var currentVersionStr = $"v{currentVersion.Major}.{currentVersion.Minor}";

        var updateInfo = new UpdateInfo
        {
            CurrentVersion = currentVersionStr,
            LatestVersion = currentVersionStr,
            IsUpdateAvailable = false
        };

        try
        {
            var apiUrl = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases/latest";
            _logger.LogInformation("Checking for application update at {ApiUrl}...", apiUrl);

            using var response = await _httpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub release check returned status {StatusCode}", response.StatusCode);
                return updateInfo;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElem))
            {
                return updateInfo;
            }

            var tagName = tagElem.GetString() ?? string.Empty;
            updateInfo.LatestVersion = tagName;

            if (root.TryGetProperty("name", out var nameElem))
            {
                updateInfo.ReleaseName = nameElem.GetString() ?? tagName;
            }

            if (root.TryGetProperty("body", out var bodyElem))
            {
                updateInfo.ReleaseNotes = bodyElem.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("published_at", out var pubElem))
            {
                updateInfo.PublishedAt = pubElem.GetString() ?? string.Empty;
            }

            // Find matching .exe asset in assets array
            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var assetNameElem))
                    {
                        var name = assetNameElem.GetString() ?? string.Empty;
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            if (asset.TryGetProperty("browser_download_url", out var downloadUrlElem))
                            {
                                updateInfo.DownloadUrl = downloadUrlElem.GetString() ?? string.Empty;
                            }

                            if (asset.TryGetProperty("size", out var sizeElem))
                            {
                                updateInfo.FileSizeBytes = sizeElem.GetInt64();
                            }

                            // If it matches LalabAutoReport.exe specifically, prefer it
                            if (name.StartsWith("LalabAutoReport", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }
                        }
                    }
                }
            }

            updateInfo.ChecksumUrl = FindChecksumUrl(root);

            // Parse version and compare
            var parsedLatest = ParseVersion(tagName);
            if (parsedLatest != null && parsedLatest > currentVersion)
            {
                updateInfo.IsUpdateAvailable = true;
                _logger.LogInformation("New version found: {LatestVersion} (current: {CurrentVersion})", updateInfo.LatestVersion, currentVersionStr);
            }
            else
            {
                _logger.LogInformation("Application is up to date ({CurrentVersion})", currentVersionStr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for updates from GitHub. Operating normally.");
        }

        return updateInfo;
    }

    public async Task DownloadAndApplyUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
        {
            throw excitingInvalidOperationException("URL tải bản cập nhật không hợp lệ.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "LalabAutoReport_Update", Guid.NewGuid().ToString("N"));
        if (!Directory.Exists(tempDir))
        {
            Directory.CreateDirectory(tempDir);
        }

        var tempExePath = Path.Combine(tempDir, "LalabAutoReport_New.exe");
        _logger.LogInformation("Downloading update from {Url} to {Dest}...", updateInfo.DownloadUrl, tempExePath);

        // Download with progress
        using (var response = await _httpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? updateInfo.FileSizeBytes;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var fileStream = new FileStream(tempExePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    progress.Report((double)totalRead / totalBytes);
                }
            }
            if (totalBytes > 0 && totalRead != totalBytes)
                throw new InvalidDataException("Bản cập nhật tải chưa đủ; ứng dụng hiện tại được giữ nguyên.");
        }

        _logger.LogInformation("Download completed. Creating updater script...");

        // Never replace the running app with a file that does not match the checksum published with this release.
        await VerifyDownloadedExecutableAsync(tempExePath, updateInfo.ChecksumUrl, cancellationToken).ConfigureAwait(false);

        var currentExePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(currentExePath) || !currentExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Not running as executable ({Path}). Cannot perform self-replacement.", currentExePath);
            return;
        }

        // Preserve the isolated acceptance profile without replaying quick-bill commands.
        string restartArguments = "--minimized";
        var arguments = Environment.GetCommandLineArgs();
        for (int i = 1; i + 1 < arguments.Length; i++)
            if (arguments[i].Equals("--isolated-data-directory", StringComparison.OrdinalIgnoreCase))
                restartArguments += " --isolated-data-directory " + UpdateReplacement.QuoteArgument(arguments[i + 1]);
        string script = await UpdateReplacement.PrepareAsync(tempExePath, currentExePath,
            Environment.ProcessId, restartArguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Launching safe updater {Script}; recovery files are retained.", script);
        using var updater = Process.Start(UpdateReplacement.CreateStartInfo(script))
            ?? throw new InvalidOperationException("Không thể khởi động updater; ứng dụng hiện tại được giữ nguyên.");
        Environment.Exit(0);
    }

    private static string FindChecksumUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var nameElem) &&
                (nameElem.GetString() ?? string.Empty).EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("browser_download_url", out var urlElem))
            {
                return urlElem.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    // The published file has the form "<sha256>  <file name>". Only the hash token is compared.
    public static bool ChecksumMatches(string actualSha256Hex, string checksumFileContent)
    {
        var parts = (checksumFileContent ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        string expected = parts[0];
        return expected.Length == 64
            && expected.All(Uri.IsHexDigit)
            && string.Equals(expected, actualSha256Hex, StringComparison.OrdinalIgnoreCase);
    }

    private async Task VerifyDownloadedExecutableAsync(string filePath, string checksumUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checksumUrl))
        {
            DiscardStagedFile(filePath);
            throw new InvalidDataException("Bản phát hành không có file checksum; cập nhật đã bị hủy.");
        }

        string checksumContent = await _httpClient.GetStringAsync(checksumUrl, cancellationToken).ConfigureAwait(false);
        string actualHash;
        await using (var stream = File.OpenRead(filePath))
        {
            actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!ChecksumMatches(actualHash, checksumContent))
        {
            DiscardStagedFile(filePath);
            throw new InvalidDataException("Checksum của bản cập nhật không khớp với bản phát hành; cập nhật đã bị hủy.");
        }
    }

    private void DiscardStagedFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not remove rejected update file {Path}", filePath);
        }
    }

    private static InvalidOperationException excitingInvalidOperationException(string msg) => new(msg);

    public static Version? ParseVersion(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return null;

        var clean = tagName.Trim();
        if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(1);
        }

        // Support formats like "1.0", "1.0.0", "1.0.0.0"
        var parts = clean.Split('.');
        if (parts.Length == 2 && int.TryParse(parts[0], out var maj) && int.TryParse(parts[1], out var min))
        {
            return new Version(maj, min, 0, 0);
        }

        if (Version.TryParse(clean, out var v))
        {
            return v;
        }

        return null;
    }
}
