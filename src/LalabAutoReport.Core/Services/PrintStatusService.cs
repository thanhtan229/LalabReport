using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Core.Services;

public class PrintStatusService : IPrintStatusService
{
    public const string MarkerFileName = ".lalab-printed";

    private readonly IFolderPrintRepository _folderPrintRepository;
    private readonly IFolderVisualMarkerService _visualMarkerService;
    private readonly IOrderRepository? _orderRepository;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly ILogger<PrintStatusService>? _logger;
    private readonly IRootFolderRepository? _rootFolderRepository;

    public event Action<string, PrintStatus>? PrintStatusChanged;

    public PrintStatusService(
        IFolderPrintRepository folderPrintRepository,
        IFolderVisualMarkerService visualMarkerService,
        IOrderRepository? orderRepository = null,
        ISettingsRepository? settingsRepository = null,
        ILogger<PrintStatusService>? logger = null,
        IRootFolderRepository? rootFolderRepository = null)
    {
        _folderPrintRepository = folderPrintRepository;
        _visualMarkerService = visualMarkerService;
        _orderRepository = orderRepository;
        _settingsRepository = settingsRepository;
        _logger = logger;
        _rootFolderRepository = rootFolderRepository;
    }

    public async Task<PrintStatus> GetStatusAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return PrintStatus.NotPrinted;

        string norm = PathNormalizer.Normalize(folderPath);

        // 1. Check Primary Source of Truth: Database
        var record = await _folderPrintRepository.GetByNormalizedPathAsync(norm, cancellationToken);
        if (record != null)
        {
            if (record.Status == PrintStatus.Printed || record.Status == PrintStatus.Partial)
            {
                // Reconcile marker file if missing on disk
                if (Directory.Exists(folderPath))
                {
                    string markerPath = Path.Combine(folderPath, MarkerFileName);
                    if (!File.Exists(markerPath))
                    {
                        try
                        {
                            WriteMarkerFile(markerPath, record.MarkedAt, record.MarkedBy, record.Status, record.PrintedSubCount, record.TotalSubCount);
                        }
                        catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
                    }
                }
                return record.Status;
            }
            return PrintStatus.NotPrinted;
        }

        // 2. Check Secondary Recovery Hint: Filesystem marker file
        if (Directory.Exists(folderPath))
        {
            string markerPath = Path.Combine(folderPath, MarkerFileName);
            if (File.Exists(markerPath))
            {
                // Folder has marker on disk but DB has no record -> Reconcile into DB!
                try
                {
                    string json = File.ReadAllText(markerPath);
                    PrintStatus recoveredStatus = PrintStatus.Printed;
                    int? pCount = null;
                    int? tCount = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("status", out var stEl) &&
                            stEl.GetString()?.Equals("PARTIAL", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            recoveredStatus = PrintStatus.Partial;
                        }
                        if (doc.RootElement.TryGetProperty("printedCount", out var pcEl) && pcEl.TryGetInt32(out int pc))
                            pCount = pc;
                        if (doc.RootElement.TryGetProperty("totalCount", out var tcEl) && tcEl.TryGetInt32(out int tc))
                            tCount = tc;
                    }
                    catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }

                    var recoveredRecord = new FolderPrintRecord
                    {
                        FolderPath = folderPath,
                        NormalizedPath = norm,
                        Status = recoveredStatus,
                        MarkedAt = DateTimeOffset.UtcNow,
                        MarkedBy = "MarkerRecovery",
                        PrintedSubCount = pCount,
                        TotalSubCount = tCount
                    };
                    await _folderPrintRepository.SaveAsync(recoveredRecord, cancellationToken);
                    _logger?.LogInformation("Recovered print status {Status} from marker file for '{Path}'", recoveredStatus, folderPath);
                    return recoveredStatus;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to reconcile recovered marker for '{Path}'", folderPath);
                    return PrintStatus.Printed;
                }
            }
        }

        return PrintStatus.NotPrinted;
    }

    public async Task<bool> IsFolderPrintedAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(folderPath, cancellationToken);
        return status == PrintStatus.Printed;
    }

    public async Task<TogglePrintStatusResult> ToggleStatusAsync(
        string folderPath,
        string markedBy = "ExplorerContextMenu",
        CancellationToken cancellationToken = default)
    {
        var currentStatus = await GetStatusAsync(folderPath, cancellationToken);
        var targetStatus = currentStatus == PrintStatus.Printed ? PrintStatus.NotPrinted : PrintStatus.Printed;
        return await SetStatusAsync(folderPath, targetStatus, markedBy, cancellationToken);
    }

    public async Task<TogglePrintStatusResult> SetStatusAsync(
        string folderPath,
        PrintStatus targetStatus,
        string markedBy = "ExplorerContextMenu",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return new TogglePrintStatusResult
            {
                FolderPath = folderPath,
                IsSuccess = false,
                ErrorMessage = "Đường dẫn thư mục không được để trống."
            };
        }

        // Clean & sanitize path
        string cleanPath = folderPath.Trim().Trim('\"', '\'').TrimEnd('\\', '/');
        if (!Directory.Exists(cleanPath))
        {
            return new TogglePrintStatusResult
            {
                FolderPath = cleanPath,
                IsSuccess = false,
                ErrorMessage = $"Thư mục không tồn tại: {cleanPath}"
            };
        }

        string norm = PathNormalizer.Normalize(cleanPath);
        var currentStatus = await GetStatusAsync(cleanPath, cancellationToken);

        var hierarchy = await ResolveHierarchyAsync(cleanPath, cancellationToken);

        if (hierarchy.IsOrderLevel)
        {
            // --- TOP-DOWN CASCADE: ORDER LEVEL TOGGLE ---
            return await CascadeOrderToAllChildrenAsync(cleanPath, hierarchy.Order, targetStatus, markedBy, cancellationToken);
        }
        else
        {
            // --- BOTTOM-UP: SUBFOLDER / ITEM LEVEL TOGGLE ---
            string targetSubfolder = hierarchy.SubfolderPath ?? cleanPath;
            string targetSubNorm = PathNormalizer.Normalize(targetSubfolder);

            var result = new TogglePrintStatusResult
            {
                FolderPath = targetSubfolder,
                NormalizedPath = targetSubNorm,
                PreviousStatus = currentStatus,
                NewStatus = targetStatus
            };

            DateTimeOffset now = DateTimeOffset.UtcNow;

            if (targetStatus == PrintStatus.Printed)
            {
                // 1. Update Subfolder in DB
                var record = new FolderPrintRecord
                {
                    FolderPath = targetSubfolder,
                    NormalizedPath = targetSubNorm,
                    Status = PrintStatus.Printed,
                    MarkedAt = now,
                    MarkedBy = markedBy
                };
                await _folderPrintRepository.SaveAsync(record, cancellationToken);

                // 2. Write marker in subfolder
                try
                {
                    string markerPath = Path.Combine(targetSubfolder, MarkerFileName);
                    WriteMarkerFile(markerPath, now, markedBy, PrintStatus.Printed);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to write marker file in subfolder '{Path}'", targetSubfolder);
                }

                // 3. Apply Red icon to subfolder
                bool visualApplied = false;
                try
                {
                    visualApplied = _visualMarkerService.ApplyVisualMarker(targetSubfolder, VisualFolderColor.RedPrinted);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to apply visual marker for subfolder '{Path}'", targetSubfolder);
                }

                // 4. Update Order Item in DB if found
                if (_orderRepository != null && hierarchy.Item != null)
                {
                    await _orderRepository.UpdateOrderItemPrintedStatusAsync(hierarchy.Item.Id, true, now, cancellationToken);
                }

                result.IsSuccess = true;
                result.VisualIconUpdated = visualApplied;
                PrintStatusChanged?.Invoke(targetSubfolder, PrintStatus.Printed);
            }
            else
            {
                // 1. Mark Subfolder as NotPrinted in DB
                var record = new FolderPrintRecord
                {
                    FolderPath = targetSubfolder,
                    NormalizedPath = targetSubNorm,
                    Status = PrintStatus.NotPrinted,
                    MarkedAt = now,
                    MarkedBy = markedBy
                };
                await _folderPrintRepository.SaveAsync(record, cancellationToken);

                // 2. Remove marker in subfolder
                try
                {
                    string markerPath = Path.Combine(targetSubfolder, MarkerFileName);
                    if (File.Exists(markerPath))
                    {
                        File.SetAttributes(markerPath, FileAttributes.Normal);
                        File.Delete(markerPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete marker file in subfolder '{Path}'", targetSubfolder);
                }

                // 3. Remove visual marker from subfolder
                bool visualRemoved = false;
                try
                {
                    visualRemoved = _visualMarkerService.RemoveVisualMarker(targetSubfolder);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to remove visual marker for subfolder '{Path}'", targetSubfolder);
                }

                // 4. Update Order Item in DB if found
                if (_orderRepository != null && hierarchy.Item != null)
                {
                    await _orderRepository.UpdateOrderItemPrintedStatusAsync(hierarchy.Item.Id, false, null, cancellationToken);
                }

                result.IsSuccess = true;
                result.VisualIconUpdated = visualRemoved;
                PrintStatusChanged?.Invoke(targetSubfolder, PrintStatus.NotPrinted);
            }

            // 5. Recalculate Parent Order Progress
            if (!string.IsNullOrWhiteSpace(hierarchy.OrderPath))
            {
                var progress = await RecalculateParentOrderProgressAsync(hierarchy.OrderPath, hierarchy.Order, cancellationToken);
                result.PrintedSubCount = progress.Printed;
                result.TotalSubCount = progress.Total;
            }

            return result;
        }
    }

    public async Task<IReadOnlySet<string>> GetAllPrintedNormalizedPathsAsync(CancellationToken cancellationToken = default)
    {
        return await _folderPrintRepository.GetAllPrintedNormalizedPathsAsync(cancellationToken);
    }

    public async Task ReconcileFolderStatusAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        string norm = PathNormalizer.Normalize(folderPath);
        var dbRecord = await _folderPrintRepository.GetByNormalizedPathAsync(norm, cancellationToken);
        string markerPath = Path.Combine(folderPath, MarkerFileName);
        bool markerExists = File.Exists(markerPath);

        if (dbRecord != null && (dbRecord.Status == PrintStatus.Printed || dbRecord.Status == PrintStatus.Partial) && !markerExists)
        {
            // DB says printed/partial, recreate missing marker
            try
            {
                WriteMarkerFile(markerPath, dbRecord.MarkedAt, dbRecord.MarkedBy, dbRecord.Status, dbRecord.PrintedSubCount, dbRecord.TotalSubCount);
            }
            catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
        }
        else if ((dbRecord == null || dbRecord.Status == PrintStatus.NotPrinted) && markerExists)
        {
            // Marker exists but DB says not printed -> recover into DB
            try
            {
                string json = File.ReadAllText(markerPath);
                PrintStatus recoveredStatus = PrintStatus.Printed;
                int? pCount = null;
                int? tCount = null;
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("status", out var stEl) &&
                        stEl.GetString()?.Equals("PARTIAL", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        recoveredStatus = PrintStatus.Partial;
                    }
                    if (doc.RootElement.TryGetProperty("printedCount", out var pcEl) && pcEl.TryGetInt32(out int pc))
                        pCount = pc;
                    if (doc.RootElement.TryGetProperty("totalCount", out var tcEl) && tcEl.TryGetInt32(out int tc))
                        tCount = tc;
                }

                var rec = new FolderPrintRecord
                {
                    FolderPath = folderPath,
                    NormalizedPath = norm,
                    Status = recoveredStatus,
                    MarkedAt = DateTimeOffset.UtcNow,
                    MarkedBy = "MarkerRecovery",
                    PrintedSubCount = pCount,
                    TotalSubCount = tCount
                };
                await _folderPrintRepository.SaveAsync(rec, cancellationToken);
            }
            catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
        }
    }

    /// <summary>
    /// Recalculates the print progress of a parent order folder based on its subfolders.
    /// Updates parent folder database record, marker file, visual color (Yellow, Blue, or Red), and Order entity.
    /// </summary>
    public async Task<(int Printed, int Total, PrintStatus Status)> RecalculateParentOrderProgressAsync(
        string parentOrderPath,
        Order? order,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parentOrderPath) || !Directory.Exists(parentOrderPath))
            return (0, 0, PrintStatus.NotPrinted);

        var (rootFolder, _) = await ResolvePhysicalRootAsync(parentOrderPath, cancellationToken);

        // 1. Gather all subfolders
        List<string> subfolderPaths = new();
        if (order != null && order.Items.Count > 0 && !string.IsNullOrWhiteSpace(rootFolder))
        {
            foreach (var item in order.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.SpecificationRelativePath))
                {
                    string fullSub = Path.Combine(rootFolder, item.SpecificationRelativePath);
                    if (Directory.Exists(fullSub) && !subfolderPaths.Contains(fullSub, StringComparer.OrdinalIgnoreCase))
                    {
                        subfolderPaths.Add(fullSub);
                    }
                }
            }
        }

        // If no items in DB or order not scanned yet, inspect filesystem
        if (subfolderPaths.Count == 0 && Directory.Exists(parentOrderPath))
        {
            try
            {
                var dirs = Directory.GetDirectories(parentOrderPath);
                foreach (var d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (!name.StartsWith(".") && !name.StartsWith("$"))
                    {
                        subfolderPaths.Add(d);
                    }
                }
            }
            catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
        }

        int totalCount = subfolderPaths.Count;
        int printedCount = 0;

        foreach (var sub in subfolderPaths)
        {
            var st = await GetStatusAsync(sub, cancellationToken);
            if (st == PrintStatus.Printed)
            {
                printedCount++;
            }
        }

        // Determine parent status
        PrintStatus parentStatus = PrintStatus.NotPrinted;
        if (totalCount <= 1)
        {
            parentStatus = (printedCount >= 1) ? PrintStatus.Printed : PrintStatus.NotPrinted;
        }
        else
        {
            if (printedCount == 0)
            {
                parentStatus = PrintStatus.NotPrinted;
            }
            else if (printedCount > 0 && printedCount < totalCount)
            {
                parentStatus = PrintStatus.Partial;
            }
            else if (printedCount >= totalCount)
            {
                parentStatus = PrintStatus.Printed;
            }
        }

        // Apply parent status to database and filesystem
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string parentNorm = PathNormalizer.Normalize(parentOrderPath);
        string parentMarkerPath = Path.Combine(parentOrderPath, MarkerFileName);

        var parentRecord = new FolderPrintRecord
        {
            FolderPath = parentOrderPath,
            NormalizedPath = parentNorm,
            Status = parentStatus,
            MarkedAt = now,
            MarkedBy = "ProgressRecalculator",
            PrintedSubCount = printedCount,
            TotalSubCount = totalCount,
            AssociatedOrderId = order?.Id
        };
        await _folderPrintRepository.SaveAsync(parentRecord, cancellationToken);

        if (parentStatus == PrintStatus.Printed)
        {
            WriteMarkerFile(parentMarkerPath, now, "ProgressRecalculator", PrintStatus.Printed, printedCount, totalCount);
            _visualMarkerService.ApplyVisualMarker(parentOrderPath, VisualFolderColor.RedPrinted);

            if (_orderRepository != null && order != null)
            {
                await _orderRepository.UpdateOrderPrintProgressAsync(order.Id, PrintStatus.Printed, true, now, cancellationToken);
            }
            else if (_orderRepository != null && !string.IsNullOrWhiteSpace(rootFolder))
            {
                string rel = Path.GetRelativePath(rootFolder, parentOrderPath).Replace('/', '\\').Trim('\\');
                await _orderRepository.UpdateOrderPrintedStatusByRelativePathAsync(rel, true, now, cancellationToken);
            }
        }
        else if (parentStatus == PrintStatus.Partial)
        {
            WriteMarkerFile(parentMarkerPath, now, "ProgressRecalculator", PrintStatus.Partial, printedCount, totalCount);
            _visualMarkerService.ApplyVisualMarker(parentOrderPath, VisualFolderColor.BluePartial);

            if (_orderRepository != null && order != null)
            {
                await _orderRepository.UpdateOrderPrintProgressAsync(order.Id, PrintStatus.Partial, false, null, cancellationToken);
            }
            else if (_orderRepository != null && !string.IsNullOrWhiteSpace(rootFolder))
            {
                string rel = Path.GetRelativePath(rootFolder, parentOrderPath).Replace('/', '\\').Trim('\\');
                await _orderRepository.UpdateOrderPrintedStatusByRelativePathAsync(rel, false, null, cancellationToken);
            }
        }
        else
        {
            if (File.Exists(parentMarkerPath))
            {
                try
                {
                    File.SetAttributes(parentMarkerPath, FileAttributes.Normal);
                    File.Delete(parentMarkerPath);
                }
                catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
            }
            _visualMarkerService.RemoveVisualMarker(parentOrderPath);

            if (_orderRepository != null && order != null)
            {
                await _orderRepository.UpdateOrderPrintProgressAsync(order.Id, PrintStatus.NotPrinted, false, null, cancellationToken);
            }
            else if (_orderRepository != null && !string.IsNullOrWhiteSpace(rootFolder))
            {
                string rel = Path.GetRelativePath(rootFolder, parentOrderPath).Replace('/', '\\').Trim('\\');
                await _orderRepository.UpdateOrderPrintedStatusByRelativePathAsync(rel, false, null, cancellationToken);
            }
        }

        PrintStatusChanged?.Invoke(parentOrderPath, parentStatus);
        _logger?.LogInformation("Parent order '{Path}' recalculated progress: {Printed}/{Total} ({Status})", parentOrderPath, printedCount, totalCount, parentStatus);

        return (printedCount, totalCount, parentStatus);
    }

    /// <summary>
    /// Cascades a toggle action from the parent order folder to all its children (Top-Down).
    /// </summary>
    private async Task<TogglePrintStatusResult> CascadeOrderToAllChildrenAsync(
        string orderPath,
        Order? order,
        PrintStatus targetStatus,
        string markedBy,
        CancellationToken cancellationToken)
    {
        string norm = PathNormalizer.Normalize(orderPath);
        var currentStatus = await GetStatusAsync(orderPath, cancellationToken);

        var result = new TogglePrintStatusResult
        {
            FolderPath = orderPath,
            NormalizedPath = norm,
            PreviousStatus = currentStatus,
            NewStatus = targetStatus,
            IsSuccess = true
        };

        var (rootFolder, _) = await ResolvePhysicalRootAsync(orderPath, cancellationToken);

        // 1. Gather all subfolders
        List<string> subfolderPaths = new();
        if (order != null && order.Items.Count > 0 && !string.IsNullOrWhiteSpace(rootFolder))
        {
            foreach (var item in order.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.SpecificationRelativePath))
                {
                    string fullSub = Path.Combine(rootFolder, item.SpecificationRelativePath);
                    if (Directory.Exists(fullSub) && !subfolderPaths.Contains(fullSub, StringComparer.OrdinalIgnoreCase))
                    {
                        subfolderPaths.Add(fullSub);
                    }
                }
            }
        }

        if (subfolderPaths.Count == 0 && Directory.Exists(orderPath))
        {
            try
            {
                var dirs = Directory.GetDirectories(orderPath);
                foreach (var d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (!name.StartsWith(".") && !name.StartsWith("$"))
                    {
                        subfolderPaths.Add(d);
                    }
                }
            }
            catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int count = subfolderPaths.Count;

        // 2. Cascade status to each subfolder
        foreach (var sub in subfolderPaths)
        {
            string subNorm = PathNormalizer.Normalize(sub);
            var subRecord = new FolderPrintRecord
            {
                FolderPath = sub,
                NormalizedPath = subNorm,
                Status = targetStatus,
                MarkedAt = now,
                MarkedBy = markedBy
            };
            await _folderPrintRepository.SaveAsync(subRecord, cancellationToken);

            string subMarker = Path.Combine(sub, MarkerFileName);
            if (targetStatus == PrintStatus.Printed)
            {
                WriteMarkerFile(subMarker, now, markedBy, PrintStatus.Printed);
                _visualMarkerService.ApplyVisualMarker(sub, VisualFolderColor.RedPrinted);
            }
            else
            {
                if (File.Exists(subMarker))
                {
                    try
                    {
                        File.SetAttributes(subMarker, FileAttributes.Normal);
                        File.Delete(subMarker);
                    }
                    catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
                }
                _visualMarkerService.RemoveVisualMarker(sub);
            }

            PrintStatusChanged?.Invoke(sub, targetStatus);
        }

        // 3. Update Order items in DB
        if (_orderRepository != null && order != null && order.Items.Count > 0)
        {
            foreach (var item in order.Items)
            {
                await _orderRepository.UpdateOrderItemPrintedStatusAsync(
                    item.Id,
                    targetStatus == PrintStatus.Printed,
                    targetStatus == PrintStatus.Printed ? now : null,
                    cancellationToken);
            }
        }

        // 4. Update Parent Order folder itself
        var parentRecord = new FolderPrintRecord
        {
            FolderPath = orderPath,
            NormalizedPath = norm,
            Status = targetStatus,
            MarkedAt = now,
            MarkedBy = markedBy,
            PrintedSubCount = targetStatus == PrintStatus.Printed ? count : 0,
            TotalSubCount = count,
            AssociatedOrderId = order?.Id
        };
        await _folderPrintRepository.SaveAsync(parentRecord, cancellationToken);

        string parentMarker = Path.Combine(orderPath, MarkerFileName);
        if (targetStatus == PrintStatus.Printed)
        {
            WriteMarkerFile(parentMarker, now, markedBy, PrintStatus.Printed, count, count);
            _visualMarkerService.ApplyVisualMarker(orderPath, VisualFolderColor.RedPrinted);

            if (_orderRepository != null && order != null)
            {
                await _orderRepository.UpdateOrderPrintProgressAsync(order.Id, PrintStatus.Printed, true, now, cancellationToken);
            }
            else if (_orderRepository != null && !string.IsNullOrWhiteSpace(rootFolder))
            {
                string rel = Path.GetRelativePath(rootFolder, orderPath).Replace('/', '\\').Trim('\\');
                await _orderRepository.UpdateOrderPrintedStatusByRelativePathAsync(rel, true, now, cancellationToken);
            }
        }
        else
        {
            if (File.Exists(parentMarker))
            {
                try
                {
                    File.SetAttributes(parentMarker, FileAttributes.Normal);
                    File.Delete(parentMarker);
                }
                catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
            }
            _visualMarkerService.RemoveVisualMarker(orderPath);

            if (_orderRepository != null && order != null)
            {
                await _orderRepository.UpdateOrderPrintProgressAsync(order.Id, PrintStatus.NotPrinted, false, null, cancellationToken);
            }
            else if (_orderRepository != null && !string.IsNullOrWhiteSpace(rootFolder))
            {
                string rel = Path.GetRelativePath(rootFolder, orderPath).Replace('/', '\\').Trim('\\');
                await _orderRepository.UpdateOrderPrintedStatusByRelativePathAsync(rel, false, null, cancellationToken);
            }
        }

        result.PrintedSubCount = targetStatus == PrintStatus.Printed ? count : 0;
        result.TotalSubCount = count;
        result.VisualIconUpdated = _visualMarkerService.HasVisualMarker(orderPath);
        PrintStatusChanged?.Invoke(orderPath, targetStatus);
        _logger?.LogInformation("Cascaded order '{Path}' to {Count} children: {Status}", orderPath, count, targetStatus);

        return result;
    }

    /// <summary>
    /// Resolves whether folderPath is an Order folder or a Subfolder (Specification/Product),
    /// and maps deep leaf folders up to the canonical specification subfolder.
    /// </summary>
    private async Task<(string Path, long? Id)> ResolvePhysicalRootAsync(string path, CancellationToken cancellationToken)
    {
        if (_rootFolderRepository != null)
        {
            var roots = await _rootFolderRepository.GetAllAsync(cancellationToken);
            var root = roots.Where(r => PathNormalizer.Normalize(path).StartsWith(
                PathNormalizer.Normalize(r.FullPath).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.FullPath.Length).FirstOrDefault();
            if (root != null) return (root.FullPath, root.Id);
        }
        var settings = _settingsRepository == null ? null : await _settingsRepository.GetSettingsAsync(cancellationToken);
        return (settings?.RootFolder ?? string.Empty, null);
    }

    private async Task<(string OrderPath, Order? Order, string? SubfolderPath, OrderItemScan? Item, bool IsOrderLevel)> ResolveHierarchyAsync(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var (rootFolder, rootId) = await ResolvePhysicalRootAsync(folderPath, cancellationToken);

        if (string.IsNullOrWhiteSpace(rootFolder) || !folderPath.StartsWith(rootFolder, StringComparison.OrdinalIgnoreCase))
        {
            // Outside root folder: fallback to structural heuristic
            string? parent = Directory.GetParent(folderPath)?.FullName;
            return (parent ?? folderPath, null, folderPath, null, false);
        }

        string rel = Path.GetRelativePath(rootFolder, folderPath).Replace('/', '\\').Trim('\\');

        // 1. Check if folderPath itself is an exact Order in DB
        if (_orderRepository != null)
        {
            try
            {
                var exactOrder = rootId.HasValue
                    ? await _orderRepository.GetOrderByRootAndRelativePathAsync(rootId.Value, rel, cancellationToken)
                    : await _orderRepository.GetOrderByRelativePathAsync(rel, cancellationToken);
                if (exactOrder != null)
                {
                    return (folderPath, exactOrder, null, null, true);
                }
            }
            catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
        }

        // 2. Check if folderPath is inside an Order in DB
        if (_orderRepository != null)
        {
            string? checkPath = Directory.GetParent(folderPath)?.FullName;
            while (!string.IsNullOrWhiteSpace(checkPath) && checkPath.StartsWith(rootFolder, StringComparison.OrdinalIgnoreCase))
            {
                string checkRel = Path.GetRelativePath(rootFolder, checkPath).Replace('/', '\\').Trim('\\');
                try
                {
                    var parentOrder = rootId.HasValue
                        ? await _orderRepository.GetOrderByRootAndRelativePathAsync(rootId.Value, checkRel, cancellationToken)
                        : await _orderRepository.GetOrderByRelativePathAsync(checkRel, cancellationToken);
                    if (parentOrder != null)
                    {
                        // Found parent order! Find matching item in order.Items
                        OrderItemScan? matchedItem = null;
                        string? canonicalSubfolder = null;

                        foreach (var item in parentOrder.Items)
                        {
                            if (string.IsNullOrWhiteSpace(item.SpecificationRelativePath)) continue;

                            string itemRel = item.SpecificationRelativePath.Replace('/', '\\').Trim('\\');
                            if (rel.Equals(itemRel, StringComparison.OrdinalIgnoreCase) ||
                                rel.StartsWith(itemRel + "\\", StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrWhiteSpace(item.SelectedPrintFolderRelativePath) &&
                                 rel.Equals(item.SelectedPrintFolderRelativePath.Replace('/', '\\').Trim('\\'), StringComparison.OrdinalIgnoreCase)))
                            {
                                matchedItem = item;
                                canonicalSubfolder = Path.Combine(rootFolder, item.SpecificationRelativePath);
                                break;
                            }
                        }

                        if (canonicalSubfolder == null)
                        {
                            // If no exact item matched, determine direct child under checkPath
                            string afterParent = rel.Substring(checkRel.Length).TrimStart('\\');
                            string subName = afterParent.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Path.GetFileName(folderPath);
                            canonicalSubfolder = Path.Combine(checkPath, subName);
                        }

                        return (checkPath, parentOrder, canonicalSubfolder, matchedItem, false);
                    }
                }
                catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }

                checkPath = Directory.GetParent(checkPath)?.FullName;
            }
        }

        // 3. Unscanned / Cold start: Inspect path parts relative to root folder
        // Standard Lalab structure: Root / WorkDate (1) / Customer (2) / [Order] (3) / Spec (3 or 4)
        string[] parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
        {
            // e.g. "2026-09-28\Kim Studio" -> Order level
            return (folderPath, null, null, null, true);
        }
        else
        {
            // e.g. "2026-09-28\Kim Studio\Album 20x20" or deeper
            string orderRel = string.Join('\\', parts.Take(2));
            string orderDir = Path.Combine(rootFolder, orderRel);

            string specRel = string.Join('\\', parts.Take(3));
            string specDir = Path.Combine(rootFolder, specRel);

            return (orderDir, null, specDir, null, false);
        }
    }

    private void WriteMarkerFile(
        string markerPath,
        DateTimeOffset markedAt,
        string markedBy,
        PrintStatus status = PrintStatus.Printed,
        int? printedCount = null,
        int? totalCount = null)
    {
        if (File.Exists(markerPath))
        {
            try
            {
                File.SetAttributes(markerPath, FileAttributes.Normal);
            }
            catch (Exception markerEx) { _logger?.LogWarning(markerEx, "Print status marker maintenance failed; the database record remains authoritative."); }
        }

        var markerData = new
        {
            status = status == PrintStatus.Partial ? "PARTIAL" : "PRINTED",
            markedAt = markedAt.ToString("o"),
            markedBy = markedBy,
            printedCount = printedCount,
            totalCount = totalCount,
            version = 1
        };

        string json = JsonSerializer.Serialize(markerData);
        File.WriteAllText(markerPath, json);
        File.SetAttributes(markerPath, FileAttributes.Hidden);
    }
}
