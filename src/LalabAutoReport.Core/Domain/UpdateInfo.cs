namespace LalabAutoReport.Core.Domain;

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string ReleaseName { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string ChecksumUrl { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public bool IsUpdateAvailable { get; set; }
    public string PublishedAt { get; set; } = string.Empty;
}
