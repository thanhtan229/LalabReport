using System;
using System.Threading;
using System.Threading.Tasks;

namespace LalabAutoReport.Core.Interfaces;

public class CloudSyncResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int OrdersCount { get; set; }
    public int BillsCount { get; set; }
    public int ReportsCount { get; set; }
    public int MediaCount { get; set; }
    public string? Error { get; set; }
}

public interface ICloudSyncService
{
    bool IsSyncing { get; }
    DateTimeOffset? LastSyncTime { get; }
    string? LastSyncStatus { get; }

    event EventHandler? SyncStatusChanged;

    Task<CloudSyncResult> SyncAllAsync(CancellationToken cancellationToken = default);
    Task<CloudSyncResult> SyncDateAsync(string date, CancellationToken cancellationToken = default);
    Task<CloudSyncResult> SyncBillAsync(long billId, CancellationToken cancellationToken = default);
    Task<CloudSyncResult> UploadBillJpegAsync(long billId, CancellationToken cancellationToken = default);
    Task<int> PullRemoteChangesAsync(CancellationToken cancellationToken = default);
    Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default);
    Task<int> ProcessPendingPrintCommandsAsync(CancellationToken cancellationToken = default);
    Task<int> ProcessPendingGalleryJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    Task<bool> CleanupCloudGalleryCacheAsync(int retentionDays, bool clearAll = false, CancellationToken cancellationToken = default) => Task.FromResult(false);
    Task ExportReconciliationReportAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task ApplyReviewedReconciliationAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
