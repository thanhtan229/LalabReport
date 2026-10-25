using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Core.Services;

public class ScanService : IScanService
{
    private readonly IFileSystemAdapter _fileSystem;
    private readonly IFolderStructureParser _structureParser;
    private readonly IPrintFolderResolver _printFolderResolver;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IOrderRepository? _orderRepository;
    private readonly IBillRepository? _billRepository;
    private readonly IBillingService? _billingService;
    private readonly ICustomerResolver? _customerResolver;
    private readonly IPrintSpecificationResolver? _specificationResolver;
    private readonly ICustomerBillRepository? _customerBillRepository;
    private readonly IFolderFingerprintService? _fingerprintService;
    private readonly IPrintStatusService? _printStatusService;
    private readonly IRootFolderRepository? _rootFolderRepository;
    private readonly ILogger<ScanService>? _logger;

    public ScanService(
        IFileSystemAdapter fileSystem,
        IFolderStructureParser structureParser,
        IPrintFolderResolver printFolderResolver,
        ISettingsRepository settingsRepository,
        IOrderRepository? orderRepository = null,
        ICustomerResolver? customerResolver = null,
        IPrintSpecificationResolver? specificationResolver = null,
        IBillRepository? billRepository = null,
        IBillingService? billingService = null,
        ICustomerBillRepository? customerBillRepository = null,
        IFolderFingerprintService? fingerprintService = null,
        IPrintStatusService? printStatusService = null,
        IRootFolderRepository? rootFolderRepository = null,
        ILogger<ScanService>? logger = null)
    {
        _fileSystem = fileSystem;
        _structureParser = structureParser;
        _printFolderResolver = printFolderResolver;
        _settingsRepository = settingsRepository;
        _orderRepository = orderRepository;
        _customerResolver = customerResolver;
        _specificationResolver = specificationResolver;
        _billRepository = billRepository;
        _billingService = billingService;
        _customerBillRepository = customerBillRepository;
        _fingerprintService = fingerprintService;
        _printStatusService = printStatusService;
        _rootFolderRepository = rootFolderRepository;
        _logger = logger;
    }

    public Task<IReadOnlyList<Order>> ScanDateAsync(string dateString, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ScanDateCoreAsync(dateString, progress, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<Order>> ScanDateCoreAsync(
        string dateString,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Starting scan for date '{Date}'", dateString);
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        var supportedExts = new HashSet<string>(settings.SupportedExtensions, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<RootFolder> activeRoots = Array.Empty<RootFolder>();
        if (_rootFolderRepository != null)
        {
            activeRoots = await _rootFolderRepository.GetActiveRootsAsync(cancellationToken);
        }

        if (activeRoots.Count == 0)
        {
            string rootFolder = settings.RootFolder;
            if (string.IsNullOrWhiteSpace(rootFolder) || !_fileSystem.DirectoryExists(rootFolder))
            {
                throw new DirectoryNotFoundException($"Root folder '{rootFolder}' does not exist or is not configured.");
            }
            activeRoots = new List<RootFolder>
            {
                new RootFolder { Id = 0, Name = "Mặc định", FullPath = rootFolder, IsActive = true, IsDefault = true }
            };
        }

        var resultOrders = new List<Order>();

        foreach (var root in activeRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(root.FullPath) || !_fileSystem.DirectoryExists(root.FullPath))
            {
                _logger?.LogWarning("Root folder '{Name}' at '{Path}' does not exist or is offline; skipping.", root.Name, root.FullPath);
                continue;
            }

            if (_structureParser.FindDateFolderPath(root.FullPath, dateString) == null)
            {
                if (_orderRepository != null)
                {
                    var retained = root.Id > 0
                        ? await _orderRepository.GetOrdersByDateAndRootAsync(dateString, root.Id, cancellationToken)
                        : await _orderRepository.GetOrdersByDateAsync(dateString, cancellationToken);

                    if (retained.Count > 0)
                    {
                        _logger?.LogWarning("Previously observed date folder for '{Date}' in root '{Root}' is missing; retaining existing orders as Error.", dateString, root.Name);
                        progress?.Report(new ScanProgress($"Không tìm thấy thư mục ngày {dateString} tại kho {root.Name}", 0, 0));
                        foreach (var failed in retained)
                        {
                            failed.Status = OrderStatus.Error;
                            resultOrders.Add(failed);
                        }
                    }
                }
                continue;
            }

            IReadOnlyList<DiscoveredOrder> discoveredOrders;
            try { discoveredOrders = _structureParser.DiscoverOrdersForDate(root.FullPath, dateString); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Incomplete date discovery at {Root}; retaining all existing orders", root.FullPath);
                progress?.Report(new ScanProgress($"Không đọc đủ {root.FullPath}: {ex.Message}", 0, 0));
                if (_orderRepository != null)
                {
                    var retained = root.Id > 0 ? await _orderRepository.GetOrdersByDateAndRootAsync(dateString, root.Id, cancellationToken)
                        : await _orderRepository.GetOrdersByDateAsync(dateString, cancellationToken);
                    foreach (var failed in retained) { failed.Status = OrderStatus.Error; resultOrders.Add(failed); }
                }
                continue; // Never prune after an incomplete observation.
            }
            int total = discoveredOrders.Count;
            int completed = 0;

            foreach (var discOrder in discoveredOrders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string stepPrefix = activeRoots.Count > 1 ? $"[{root.Name}] " : "";
                progress?.Report(new ScanProgress(
                    CurrentStep: $"{stepPrefix}Scanning {discOrder.OriginalCustomerFolderName} ({completed + 1}/{total})",
                    CompletedItems: completed,
                    TotalItems: total,
                    CurrentItemName: discOrder.OriginalCustomerFolderName
                ));

                try
                {
                    var orderToProcess = root.Id > 0
                        ? discOrder with { RootFolderId = root.Id }
                        : discOrder;

                    var order = await ProcessDiscoveredOrderAsync(root.FullPath, orderToProcess, supportedExts, ScanScope.Date, cancellationToken);
                    if (root.Id > 0)
                    {
                        order.RootFolderId = root.Id;
                        order.RootFolderName = root.Name;
                    }
                    resultOrders.Add(order);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error processing customer folder '{Folder}' at '{Path}' in root '{RootName}'", discOrder.OriginalCustomerFolderName, discOrder.RelativePath, root.Name);
                    var failedOrder = new Order
                    {
                        WorkDate = discOrder.Date,
                        OriginalFolderName = discOrder.OriginalCustomerFolderName,
                        RelativePath = discOrder.RelativePath,
                        RootFolderId = root.Id > 0 ? root.Id : null,
                        RootFolderName = root.Id > 0 ? root.Name : null,
                        Status = OrderStatus.Error,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        LastScanAt = DateTimeOffset.UtcNow
                    };
                    resultOrders.Add(failedOrder);
                }

                completed++;
                progress?.Report(new ScanProgress(
                    CurrentStep: $"{stepPrefix}Finished {discOrder.OriginalCustomerFolderName}",
                    CompletedItems: completed,
                    TotalItems: total,
                    CurrentItemName: discOrder.OriginalCustomerFolderName
                ));
            }

            // Scoped pruning requires complete discovery.
            if (_orderRepository != null && discoveredOrders.All(o => o.DiscoveryError == null))
            {
                try
                {
                    var discoveredPathSet = new HashSet<string>(
                        discoveredOrders.Select(o => o.RelativePath),
                        StringComparer.OrdinalIgnoreCase);

                    IReadOnlyList<Order> existingDbOrders;
                    if (root.Id > 0)
                    {
                        existingDbOrders = await _orderRepository.GetOrdersByDateAndRootAsync(dateString, root.Id, cancellationToken);
                    }
                    else
                    {
                        existingDbOrders = await _orderRepository.GetOrdersByDateAsync(dateString, cancellationToken);
                    }

                    foreach (var dbOrder in existingDbOrders)
                    {
                        if (dbOrder.Status != OrderStatus.Locked && !discoveredPathSet.Contains(dbOrder.RelativePath))
                        {
                            bool hasLockedBill = false;
                            if (_customerBillRepository != null)
                            {
                                var lockedBill = await _customerBillRepository.GetLockedBillByOrderIdAsync(dbOrder.Id, cancellationToken);
                                if (lockedBill != null) hasLockedBill = true;
                            }
                            if (!hasLockedBill && _billRepository != null)
                            {
                                var lockedBill = await _billRepository.GetBillByOrderIdAsync(dbOrder.Id, cancellationToken);
                                if (lockedBill != null && lockedBill.Status == OrderStatus.Locked) hasLockedBill = true;
                            }

                            if (!hasLockedBill)
                            {
                                _logger?.LogInformation("Pruning orphaned/invalid order {OrderId} ({RelativePath}) from root {RootId}", dbOrder.Id, dbOrder.RelativePath, root.Id);
                                await _orderRepository.DeleteOrderAsync(dbOrder.Id, cancellationToken);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error during pruning obsolete orders for date '{Date}' in root '{RootName}'", dateString, root.Name);
                }
            }
        }

        _logger?.LogInformation("Completed scan for date '{Date}': {Count} orders processed across {Roots} root(s).", dateString, resultOrders.Count, activeRoots.Count);
        return resultOrders;
    }

    public Task<Order?> ScanOrderAsync(string orderRelativePath, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ScanOrderInRootAsync(orderRelativePath, null, progress, cancellationToken);

    public Task<Order?> ScanOrderInRootAsync(string orderRelativePath, long? requestedRootFolderId,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => ScanOrderInRootCoreAsync(orderRelativePath, requestedRootFolderId, progress, cancellationToken), cancellationToken);

    private async Task<Order?> ScanOrderInRootCoreAsync(
        string orderRelativePath, long? requestedRootFolderId,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string rootFolder = settings.RootFolder;
        long? rootFolderId = requestedRootFolderId;
        if (requestedRootFolderId.HasValue)
        {
            var requestedRoot = _rootFolderRepository == null ? null
                : await _rootFolderRepository.GetByIdAsync(requestedRootFolderId.Value, cancellationToken);
            if (requestedRoot == null) throw new InvalidOperationException("Không tìm thấy root của đơn hàng.");
            rootFolder = requestedRoot.FullPath;
        }

        // Try to identify root folder from order in repository
        if (_orderRepository != null)
        {
            var dbOrder = rootFolderId.HasValue
                ? await _orderRepository.GetOrderByRootAndRelativePathAsync(rootFolderId.Value, orderRelativePath, cancellationToken)
                : await _orderRepository.GetOrderByRelativePathAsync(orderRelativePath, cancellationToken);
            if (dbOrder?.RootFolderId != null && _rootFolderRepository != null)
            {
                var root = await _rootFolderRepository.GetByIdAsync(dbOrder.RootFolderId.Value, cancellationToken);
                if (root != null)
                {
                    rootFolder = root.FullPath;
                    rootFolderId = root.Id;
                }
            }
        }

        if (!rootFolderId.HasValue && _rootFolderRepository != null)
        {
            var roots = await _rootFolderRepository.GetActiveRootsAsync(cancellationToken);
            var matches = roots.Where(r => _fileSystem.DirectoryExists(_fileSystem.Combine(r.FullPath, orderRelativePath))).ToList();
            if (matches.Count > 1) throw new InvalidOperationException("Đường dẫn có ở nhiều root. Hãy chọn đơn hàng theo root.");
            if (matches.Count == 1) { rootFolder = matches[0].FullPath; rootFolderId = matches[0].Id; }
        }

        var discOrder = _structureParser.DiscoverSingleOrder(rootFolder, orderRelativePath);
        if (discOrder == null)
        {
            _logger?.LogWarning("Order at '{Path}' not found on disk.", orderRelativePath);
            return null;
        }

        if (rootFolderId.HasValue && rootFolderId.Value > 0)
        {
            discOrder = discOrder with { RootFolderId = rootFolderId.Value };
        }

        var supportedExts = new HashSet<string>(settings.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        try
        {
            var order = await ProcessDiscoveredOrderAsync(rootFolder, discOrder, supportedExts, ScanScope.Order, cancellationToken);
            if (rootFolderId.HasValue && rootFolderId.Value > 0)
            {
                order.RootFolderId = rootFolderId.Value;
            }
            return order;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing order at '{Path}'", orderRelativePath);
            return new Order
            {
                WorkDate = discOrder.Date,
                OriginalFolderName = discOrder.OriginalCustomerFolderName,
                RelativePath = discOrder.RelativePath,
                RootFolderId = rootFolderId,
                Status = OrderStatus.Error,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastScanAt = DateTimeOffset.UtcNow
            };
        }
    }

    public Task<OrderItemScan?> ScanSpecificationAsync(string specRelativePath, CancellationToken cancellationToken = default)
        => ScanSpecificationInRootAsync(specRelativePath, null, cancellationToken);

    public Task<OrderItemScan?> ScanSpecificationInRootAsync(string specRelativePath, long? requestedRootFolderId,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ScanSpecificationInRootCoreAsync(specRelativePath, requestedRootFolderId, cancellationToken), cancellationToken);

    private async Task<OrderItemScan?> ScanSpecificationInRootCoreAsync(
        string specRelativePath, long? requestedRootFolderId,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string rootFolder = settings.RootFolder;

        if (_rootFolderRepository != null)
        {
            if (requestedRootFolderId.HasValue)
            {
                var root = await _rootFolderRepository.GetByIdAsync(requestedRootFolderId.Value, cancellationToken)
                    ?? throw new InvalidOperationException("Không tìm thấy root của quy cách.");
                rootFolder = root.FullPath;
            }
            else
            {
                var roots = await _rootFolderRepository.GetActiveRootsAsync(cancellationToken);
                var matches = roots.Where(r => _fileSystem.DirectoryExists(_fileSystem.Combine(r.FullPath, specRelativePath))).ToList();
                if (matches.Count > 1) throw new InvalidOperationException("Quy cách có ở nhiều root. Hãy chọn root trước khi quét.");
                if (matches.Count == 1) rootFolder = matches[0].FullPath;
            }
        }

        var discSpec = _structureParser.DiscoverSingleSpecification(rootFolder, specRelativePath);
        if (discSpec == null)
        {
            _logger?.LogWarning("Specification at '{Path}' not found on disk.", specRelativePath);
            return null;
        }

        var supportedExts = new HashSet<string>(settings.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
        return await ScanSpecificationInternalAsync(rootFolder, discSpec, supportedExts, null, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetMissingScanDaysAsync(
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string prefix = $"{year:D4}-{month:D2}";
        var diskDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<RootFolder> activeRoots = Array.Empty<RootFolder>();
        if (_rootFolderRepository != null)
        {
            activeRoots = await _rootFolderRepository.GetActiveRootsAsync(cancellationToken);
        }

        if (activeRoots.Count > 0)
        {
            foreach (var root in activeRoots)
            {
                if (!string.IsNullOrWhiteSpace(root.FullPath) && _fileSystem.DirectoryExists(root.FullPath))
                {
                    foreach (var d in _structureParser.DiscoverDateFolders(root.FullPath))
                    {
                        if (d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            diskDates.Add(d);
                        }
                    }
                }
            }
        }
        else
        {
            string rootFolder = settings.RootFolder;
            if (!string.IsNullOrWhiteSpace(rootFolder) && _fileSystem.DirectoryExists(rootFolder))
            {
                foreach (var d in _structureParser.DiscoverDateFolders(rootFolder))
                {
                    if (d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        diskDates.Add(d);
                    }
                }
            }
        }

        if (_orderRepository == null)
        {
            return diskDates.OrderBy(d => d).ToList();
        }

        var scannedDates = await _orderRepository.GetScannedDatesInMonthAsync(prefix, cancellationToken);
        var scannedSet = new HashSet<string>(scannedDates, StringComparer.OrdinalIgnoreCase);

        var missing = diskDates.Where(d => !scannedSet.Contains(d)).OrderBy(d => d).ToList();
        return missing;
    }

    public async Task<IReadOnlyList<Order>> ScanDateRangeAsync(
        string startDateString,
        string endDateString,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        var inRangeDates = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<RootFolder> activeRoots = Array.Empty<RootFolder>();
        if (_rootFolderRepository != null)
        {
            activeRoots = await _rootFolderRepository.GetActiveRootsAsync(cancellationToken);
        }

        if (activeRoots.Count > 0)
        {
            foreach (var root in activeRoots)
            {
                if (!string.IsNullOrWhiteSpace(root.FullPath) && _fileSystem.DirectoryExists(root.FullPath))
                {
                    foreach (var d in _structureParser.DiscoverDateFolders(root.FullPath))
                    {
                        if (string.Compare(d, startDateString, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            string.Compare(d, endDateString, StringComparison.OrdinalIgnoreCase) <= 0)
                        {
                            inRangeDates.Add(d);
                        }
                    }
                }
            }
        }
        else
        {
            string rootFolder = settings.RootFolder;
            if (!string.IsNullOrWhiteSpace(rootFolder) && _fileSystem.DirectoryExists(rootFolder))
            {
                foreach (var d in _structureParser.DiscoverDateFolders(rootFolder))
                {
                    if (string.Compare(d, startDateString, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        string.Compare(d, endDateString, StringComparison.OrdinalIgnoreCase) <= 0)
                    {
                        inRangeDates.Add(d);
                    }
                }
            }
        }

        var orders = new List<Order>();
        int totalDates = inRangeDates.Count;
        int completedDates = 0;

        foreach (var date in inRangeDates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ScanProgress(
                CurrentStep: $"Quét ngày {date} ({completedDates + 1}/{totalDates})",
                CompletedItems: completedDates,
                TotalItems: totalDates,
                CurrentItemName: date
            ));

            var dateOrders = await ScanDateAsync(date, null, cancellationToken);
            orders.AddRange(dateOrders);

            completedDates++;
            progress?.Report(new ScanProgress(
                CurrentStep: $"Hoàn thành ngày {date}",
                CompletedItems: completedDates,
                TotalItems: totalDates,
                CurrentItemName: date
            ));
        }

        return orders;
    }

    public async Task<IReadOnlyList<Order>> ScanMissingDaysAsync(
        int year,
        int month,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var missingDays = await GetMissingScanDaysAsync(year, month, cancellationToken);
        var orders = new List<Order>();
        int total = missingDays.Count;
        int completed = 0;

        foreach (var date in missingDays)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ScanProgress(
                CurrentStep: $"Quét ngày thiếu: {date} ({completed + 1}/{total})",
                CompletedItems: completed,
                TotalItems: total,
                CurrentItemName: date
            ));

            var dateOrders = await ScanDateAsync(date, null, cancellationToken);
            orders.AddRange(dateOrders);

            completed++;
            progress?.Report(new ScanProgress(
                CurrentStep: $"Đã quét ngày thiếu: {date}",
                CompletedItems: completed,
                TotalItems: total,
                CurrentItemName: date
            ));
        }

        return orders;
    }

    private async Task<Order> ProcessDiscoveredOrderAsync(
        string rootFolder,
        DiscoveredOrder discOrder,
        HashSet<string> supportedExts,
        ScanScope scope,
        CancellationToken cancellationToken)
    {
        // Check if order exists in DB to reuse ID and customer mapping
        Order? existingOrder = null;
        if (_orderRepository != null)
        {
            if (discOrder.RootFolderId.HasValue && discOrder.RootFolderId.Value > 0)
            {
                existingOrder = await _orderRepository.GetOrderByRootAndRelativePathAsync(discOrder.RootFolderId.Value, discOrder.RelativePath, cancellationToken);
            }
            if (!discOrder.RootFolderId.HasValue)
            {
                existingOrder = await _orderRepository.GetOrderByRelativePathAsync(discOrder.RelativePath, cancellationToken);
            }
        }

        if (discOrder.DiscoveryError != null)
        {
            var failed = existingOrder ?? new Order { WorkDate = discOrder.Date, OriginalFolderName = discOrder.OriginalCustomerFolderName,
                RelativePath = discOrder.RelativePath, RootFolderId = discOrder.RootFolderId };
            failed.Status = OrderStatus.Error;
            // Keep the last known persisted snapshot; display the scoped failure in this result.
            failed.Items = new List<OrderItemScan> { new() { SpecificationFolderName = discOrder.OriginalCustomerFolderName,
                ScanStatus = ScanStatus.Failed, ErrorMessage = discOrder.DiscoveryError } };
            return failed;
        }

        var order = existingOrder ?? new Order
        {
            WorkDate = discOrder.Date,
            OriginalFolderName = discOrder.OriginalCustomerFolderName,
            RelativePath = discOrder.RelativePath,
            OrderKind = discOrder.Kind,
            OrderName = discOrder.OrderName,
            RootFolderId = discOrder.RootFolderId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (discOrder.RootFolderId.HasValue && discOrder.RootFolderId.Value > 0)
        {
            order.RootFolderId = discOrder.RootFolderId.Value;
        }

        order.OrderKind = discOrder.Kind;
        order.OrderName = discOrder.OrderName;
        if (existingOrder != null && !string.IsNullOrWhiteSpace(existingOrder.OrderCode))
        {
            order.OrderCode = existingOrder.OrderCode;
        }
        else if (string.IsNullOrWhiteSpace(order.OrderCode) && _orderRepository == null)
        {
            order.OrderCode = OrderCodeGenerator.Generate(discOrder.Date, 1);
        }
        order.LastScanAt = DateTimeOffset.UtcNow;
        order.UpdatedAt = DateTimeOffset.UtcNow;

        if (!order.IsPrinted && order.PrintedRevision == 0 && _printStatusService != null)
        {
            string fullOrderPath = _fileSystem.Combine(rootFolder, discOrder.RelativePath);
            if (await _printStatusService.IsFolderPrintedAsync(fullOrderPath, cancellationToken))
            {
                order.IsPrinted = true;
                order.PrintedAt ??= DateTimeOffset.UtcNow;
            }
        }

        bool hasIssues = false;

        // Resolve Customer Identity:
        // Nếu khớp Exact/Normalized -> Khách quen (order.CustomerId != null).
        // Nếu không khớp -> Mặc định là Khách lẻ (order.CustomerId = null), hợp lệ và KHÔNG coi là lỗi.
        if (_customerResolver != null && order.CustomerId == null)
        {
            var custResult = await _customerResolver.ResolveCustomerAsync(discOrder.OriginalCustomerFolderName, cancellationToken);
            if (custResult.Status == CustomerResolutionStatus.ExactMatch || custResult.Status == CustomerResolutionStatus.NormalizedMatch)
            {
                order.CustomerId = custResult.ResolvedCustomer?.Id;
                order.Customer = custResult.ResolvedCustomer;
            }
            else
            {
                // Mặc định Khách lẻ (IsGuest = true)
                order.CustomerId = null;
                order.Customer = null;
            }
        }

        var snapshot = new ScanSnapshot
        {
            OrderId = order.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Scope = scope,
            Status = ScanStatus.Success
        };

        var scannedItems = new List<OrderItemScan>();

        foreach (var discSpec in discOrder.Specifications)
        {
            // Check if there was a previously manually selected print folder for this spec
            var existingItem = existingOrder?.Items
                .FirstOrDefault(i => string.Equals(i.SpecificationRelativePath, discSpec.RelativePath, StringComparison.OrdinalIgnoreCase));

            string? previouslySelectedPrintFolder = existingItem?.FolderResolutionMode == BillingFolderResolutionMode.ManuallySelected
                ? existingItem.SelectedPrintFolderRelativePath
                : null;

            var itemScan = await ScanSpecificationInternalAsync(rootFolder, discSpec, supportedExts, previouslySelectedPrintFolder, cancellationToken);
            scannedItems.Add(itemScan);

            // In V2: Blocking issues are unresolved print folder, scanner failure, unresolved product, or empty album
            if (itemScan.PrintFolderStatus != PrintFolderResolutionStatus.Resolved ||
                itemScan.ScanStatus == ScanStatus.Failed ||
                (_specificationResolver != null && itemScan.PrintSpecificationId == null))
            {
                hasIssues = true;
            }
        }

        order.Items = scannedItems;
        order.ThumbnailCandidateRelativePath = scannedItems
            .FirstOrDefault(i => !string.IsNullOrEmpty(i.ThumbnailCandidateRelativePath))
            ?.ThumbnailCandidateRelativePath ?? existingOrder?.ThumbnailCandidateRelativePath;

        snapshot.Items = scannedItems;
        snapshot.CompletedAt = DateTimeOffset.UtcNow;

        if (_fingerprintService != null)
        {
            var fp = _fingerprintService.ComputeOrderFingerprint(rootFolder, discOrder.RelativePath);
            order.Fingerprint = fp.Value;
        }

        bool isBilledOrLocked = (existingOrder != null && (existingOrder.Status == OrderStatus.Locked || existingOrder.Status == OrderStatus.Billed));
        CustomerBill? lockedCustomerBill = null;

        if (_customerBillRepository != null && existingOrder != null)
        {
            lockedCustomerBill = await _customerBillRepository.GetLockedBillByOrderIdAsync(existingOrder.Id, cancellationToken);
            if (lockedCustomerBill == null && !string.IsNullOrWhiteSpace(existingOrder.RelativePath))
            {
                var bills = await _customerBillRepository.GetBillsBySourceFolderPathAsync(existingOrder.RelativePath, cancellationToken);
                lockedCustomerBill = bills.FirstOrDefault();
            }
            if (lockedCustomerBill != null)
            {
                isBilledOrLocked = true;
            }
        }

        bool filesystemChanged = false;
        if (lockedCustomerBill != null && (lockedCustomerBill.Status == CustomerBillStatus.Locked || lockedCustomerBill.Status == CustomerBillStatus.Exported))
        {
            var targetOrderId = existingOrder?.Id ?? order.Id;
            var billLinesForOrder = lockedCustomerBill.Lines.Where(l => l.OrderId == targetOrderId).ToList();
            if (billLinesForOrder.Count != scannedItems.Count)
            {
                filesystemChanged = true;
            }
            else
            {
                var linesBySpec = billLinesForOrder
                    .Where(l => !string.IsNullOrEmpty(l.SpecificationFolderName))
                    .ToDictionary(l => l.SpecificationFolderName!, StringComparer.OrdinalIgnoreCase);

                foreach (var item in scannedItems)
                {
                    if (!linesBySpec.TryGetValue(item.SpecificationFolderName, out var line))
                    {
                        filesystemChanged = true;
                        break;
                    }
                    int scannedQty = item.PrintCount ?? item.SourceCount;
                    if (line.ScannedQuantity != scannedQty)
                    {
                        filesystemChanged = true;
                        break;
                    }
                }
            }
        }

        if (scannedItems.Count == 0 || hasIssues)
        {
            order.Status = OrderStatus.NeedsReview;
        }
        else if (isBilledOrLocked)
        {
            // Billed orders remain marked as Billed (not locked, fully editable/rescan-friendly)
            order.Status = OrderStatus.Billed;
        }
        else
        {
            order.Status = OrderStatus.Ready;
        }

        order.FilesystemChangedAfterLock = filesystemChanged;

        if (_orderRepository != null)
        {
            await _orderRepository.SaveOrderAsync(order, snapshot, cancellationToken);
        }

        // Tự động tạo mới hoặc cập nhật CustomerBill tại thời điểm quét
        if (_customerBillRepository != null && order.Status != OrderStatus.NeedsReview)
        {
            lockedCustomerBill ??= await _customerBillRepository.GetBillByOrderIdAsync(order.Id, cancellationToken);
            if (lockedCustomerBill == null && !string.IsNullOrWhiteSpace(order.RelativePath))
            {
                var bills = await _customerBillRepository.GetBillsBySourceFolderPathAsync(order.RelativePath, cancellationToken);
                lockedCustomerBill = bills.FirstOrDefault();
            }

            if (lockedCustomerBill == null && order.CustomerId.HasValue)
            {
                lockedCustomerBill = await _customerBillRepository.GetActiveDraftByCustomerIdAsync(order.CustomerId.Value, cancellationToken);
            }

            if (lockedCustomerBill != null)
            {
                // CRITICAL DATA INTEGRITY:
                // Only sync automatically if bill is in Draft status.
                // NEVER silently mutate a Locked or Exported historical bill during rescan!
                if (lockedCustomerBill.Status == CustomerBillStatus.Draft)
                {
                    await SyncCustomerBillWithOrderAsync(order, lockedCustomerBill, cancellationToken);
                }
            }
        }

        // If this order is Ready or Billed and legacy billing service available, calculate/recalculate the bill
        if (_billRepository != null && _billingService != null && order.Status != OrderStatus.NeedsReview)
        {
            try
            {
                await _billingService.CalculateBillForOrderAsync(order.Id, cancellationToken);
                _logger?.LogInformation("Calculated/updated draft bill for order {OrderId} after scan.", order.Id);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to calculate draft bill for order {OrderId} after scan.", order.Id);
            }
        }

        return order;
    }

    private async Task SyncCustomerBillWithOrderAsync(Order order, CustomerBill bill, CancellationToken cancellationToken)
    {
        var orderLines = bill.Lines.Where(l => l.OrderId == order.Id).ToList();
        var linesBySpecFolder = orderLines
            .Where(l => !string.IsNullOrEmpty(l.SpecificationFolderName))
            .ToDictionary(l => l.SpecificationFolderName!, StringComparer.OrdinalIgnoreCase);

        var currentSpecFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in order.Items)
        {
            currentSpecFolders.Add(item.SpecificationFolderName);
            int newQty = item.PrintCount ?? item.SourceCount;

            if (linesBySpecFolder.TryGetValue(item.SpecificationFolderName, out var line))
            {
                int oldScanned = line.ScannedQuantity;
                line.ScannedQuantity = newQty;

                // Bảo toàn số lượng sửa tay nếu người dùng đã ghi đè (không tự ý ghi đè mất của người dùng)
                if (line.BilledQuantity == oldScanned && string.IsNullOrEmpty(line.QuantityOverrideReason))
                {
                    line.BilledQuantity = newQty;
                }
                else if (string.IsNullOrEmpty(line.QuantityOverrideReason) && line.BilledQuantity != newQty)
                {
                    line.QuantityOverrideReason = "Chỉnh sửa thủ công";
                }

                if (line.BillingMethodSnapshot == BillingMethod.AlbumBasePlusExtra)
                {
                    line.SheetCount = newQty;
                    int incSheets = line.IncludedSheetsSnapshot ?? 10;
                    line.ExtraSheetCount = Math.Max(0, newQty - incSheets);
                    long baseP = line.BasePriceSnapshot ?? 0;
                    long extraP = line.ExtraSheetPriceSnapshot ?? 0;
                    line.LineTotal = baseP + (line.ExtraSheetCount.Value * extraP);
                }
                else
                {
                    line.LineTotal = line.BilledQuantity * line.BilledUnitPrice;
                }

                line.FinalPrintFolderPath = item.SelectedPrintFolderRelativePath;
                line.FolderResolutionModeSnapshot = item.FolderResolutionMode;
                line.IssueMessage = item.PrintFolderStatus != PrintFolderResolutionStatus.Resolved
                    ? $"Chưa có thư mục in hợp lệ ({item.PrintFolderStatus})."
                    : null;
            }
            else
            {
                PrintSpecification? spec = item.PrintSpecification;
                if (spec == null && item.PrintSpecificationId.HasValue && _specificationResolver != null)
                {
                    var res = await _specificationResolver.ResolveSpecificationAsync(item.SpecificationFolderName, cancellationToken);
                    spec = res.ResolvedSpecification;
                }

                var billingMethod = spec?.BillingMethod ?? BillingMethod.FileCount;
                long unitPrice = spec?.UnitPrice ?? 0;
                long basePrice = spec?.BasePrice ?? 0;
                long extraSheetPrice = spec?.ExtraSheetPrice ?? 0;
                int includedSheets = spec?.IncludedSheets ?? 10;

                var newLine = new CustomerBillLine
                {
                    BillId = bill.Id,
                    OrderId = order.Id,
                    ProductJobId = item.Id,
                    ProductSpecificationId = spec?.Id,
                    SpecificationFolderName = item.SpecificationFolderName,
                    ProductNameSnapshot = spec?.CanonicalName ?? item.SpecificationFolderName,
                    VariantSnapshot = spec?.CanonicalSize,
                    BillingMethodSnapshot = billingMethod,
                    ScannedQuantity = newQty,
                    BilledQuantity = newQty,
                    BilledUnitPrice = unitPrice,
                    ConfiguredUnitPrice = unitPrice,
                    BasePriceSnapshot = basePrice,
                    ExtraSheetPriceSnapshot = extraSheetPrice,
                    IncludedSheetsSnapshot = includedSheets,
                    IsIncluded = true,
                    SortOrder = bill.Lines.Count + 1,
                    FinalPrintFolderPath = item.SelectedPrintFolderRelativePath,
                    FolderResolutionModeSnapshot = item.FolderResolutionMode,
                    IssueMessage = item.PrintFolderStatus != PrintFolderResolutionStatus.Resolved
                        ? $"Chưa có thư mục in hợp lệ ({item.PrintFolderStatus})."
                        : (spec == null ? "Chưa liên kết bảng giá." : null)
                };

                if (billingMethod == BillingMethod.AlbumBasePlusExtra)
                {
                    newLine.SheetCount = newQty;
                    newLine.ExtraSheetCount = Math.Max(0, newQty - includedSheets);
                    newLine.LineTotal = basePrice + (newLine.ExtraSheetCount.Value * extraSheetPrice);
                }
                else
                {
                    newLine.LineTotal = newQty * unitPrice;
                }

                bill.Lines.Add(newLine);
            }
        }

        // Remove any line whose specification folder was deleted from disk
        bill.Lines.RemoveAll(l => l.OrderId == order.Id && (l.SpecificationFolderName == null || !currentSpecFolders.Contains(l.SpecificationFolderName)));

        // Update CustomerBillOrder subtotal
        var billOrder = bill.Orders.FirstOrDefault(o => o.OrderId == order.Id);
        if (billOrder == null)
        {
            billOrder = new CustomerBillOrder
            {
                BillId = bill.Id,
                OrderId = order.Id,
                OrderNameSnapshot = !string.IsNullOrWhiteSpace(order.OrderName) && order.OrderName != "Khách lẻ"
                    ? order.OrderName
                    : order.OriginalFolderName,
                OrderDateSnapshot = order.WorkDate,
                SourceFolderPath = order.RelativePath,
                IsIncluded = true,
                SortOrder = bill.Orders.Count + 1
            };
            bill.Orders.Add(billOrder);
        }
        billOrder.Subtotal = bill.Lines.Where(l => l.OrderId == order.Id && l.IsIncluded).Sum(l => l.LineTotal);

        // Recalculate bill totals
        long prodSubtotal = bill.Lines.Where(l => l.IsIncluded).Sum(l => l.LineTotal);
        bill.ProductSubtotal = prodSubtotal;
        bill.GrandTotal = Math.Max(0, bill.ProductSubtotal + bill.AdjustmentsTotal);
        bill.UpdatedAt = DateTimeOffset.UtcNow;

        if (_customerBillRepository != null)
        {
            await _customerBillRepository.SaveBillAsync(bill, cancellationToken);
        }

        var jobIds = bill.Lines.Where(l => l.OrderId == order.Id).Select(l => l.ProductJobId).ToList();
        if (jobIds.Count > 0 && _orderRepository != null)
        {
            await _orderRepository.SetCustomerBillIdForItemsAsync(jobIds, bill.Id, cancellationToken);
        }

        order.Status = OrderStatus.Billed;
        if (_orderRepository != null && order.Id > 0)
        {
            await _orderRepository.UpdateOrderStatusAsync(order.Id, OrderStatus.Billed, cancellationToken);
        }

        _logger?.LogInformation("ScanService synchronized bill {BillNumber} (ID {BillId}) with rescanned order {OrderId}. Grand total: {GrandTotal}",
            bill.BillNumber, bill.Id, order.Id, bill.GrandTotal);
    }

    private static bool HasFilesystemChangedFromLockedBill(Bill lockedBill, List<OrderItemScan> currentItems)
    {
        if (lockedBill.Lines.Count != currentItems.Count)
        {
            return true;
        }

        foreach (var line in lockedBill.Lines)
        {
            var matchingItem = currentItems.FirstOrDefault(i => i.PrintSpecificationId == line.PrintSpecificationId);
            if (matchingItem == null)
            {
                return true;
            }

            if (matchingItem.PrintCount != line.PrintCount)
            {
                return true;
            }

            if (!string.Equals(matchingItem.SelectedPrintFolderRelativePath, line.FinalPrintFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasFilesystemChangedFromCustomerBill(CustomerBill customerBill, long orderId, List<OrderItemScan> currentItems)
    {
        var billedLines = customerBill.Lines.Where(l => l.OrderId == orderId && l.IsIncluded).ToList();
        if (billedLines.Count != currentItems.Count)
        {
            return true;
        }

        foreach (var line in billedLines)
        {
            var matchingItem = currentItems.FirstOrDefault(i =>
                (line.ProductSpecificationId.HasValue && i.PrintSpecificationId == line.ProductSpecificationId) ||
                string.Equals(i.SpecificationFolderName, line.ProductNameSnapshot, StringComparison.OrdinalIgnoreCase));

            if (matchingItem == null)
            {
                return true;
            }

            if (matchingItem.PrintCount != line.BilledQuantity)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(line.FinalPrintFolderPath) &&
                !string.Equals(matchingItem.SelectedPrintFolderRelativePath, line.FinalPrintFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    private async Task<OrderItemScan> ScanSpecificationInternalAsync(
        string rootFolder,
        DiscoveredSpecification discSpec,
        HashSet<string> supportedExts,
        string? previouslySelectedPrintFolder = null,
        CancellationToken cancellationToken = default)
    {
        var itemScan = new OrderItemScan
        {
            SpecificationFolderName = discSpec.FolderName,
            SpecificationRelativePath = discSpec.RelativePath,
            ScanStatus = ScanStatus.Success
        };

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Resolve Print Specification
            if (_specificationResolver != null)
            {
                var specResult = await _specificationResolver.ResolveSpecificationAsync(discSpec.FolderName, cancellationToken);
                if (specResult.Status == PrintSpecificationResolutionStatus.Resolved)
                {
                    itemScan.PrintSpecificationId = specResult.ResolvedSpecification?.Id;
                    itemScan.PrintSpecification = specResult.ResolvedSpecification;
                }
                else
                {
                    itemScan.PrintSpecificationId = null;
                    itemScan.ScanStatus = ScanStatus.Warning;
                    itemScan.ErrorMessage = specResult.ErrorMessage ?? "Chưa nhận diện quy cách";
                }
            }

            // 1. Source count directly inside the specification folder (non-recursive!)
            int sourceCount = 0;
            foreach (var file in _fileSystem.EnumerateFiles(discSpec.FullPath))
            {
                string ext = _fileSystem.GetExtension(file);
                if (supportedExts.Contains(ext))
                {
                    sourceCount++;
                }
            }
            itemScan.SourceCount = sourceCount;

            // 2. Resolve Print Folder
            var printResult = _printFolderResolver.ResolvePrintFolder(
                discSpec.FullPath,
                rootFolder,
                supportedExts,
                previouslySelectedPrintFolder
            );

            itemScan.PrintFolderStatus = printResult.Status;
            itemScan.SelectedPrintFolderRelativePath = printResult.SelectedPrintFolderRelativePath;
            itemScan.PrintCount = printResult.PrintCount;
            itemScan.CandidatePrintFolderRelativePaths = printResult.CandidatePrintFolderRelativePaths.ToList();
            itemScan.FolderResolutionMode = printResult.ResolutionMode;

            // Resolve candidate image for order thumbnail preview
            string candidateFolder = (printResult.Status == PrintFolderResolutionStatus.Resolved && !string.IsNullOrEmpty(printResult.SelectedPrintFolderFullPath))
                ? printResult.SelectedPrintFolderFullPath
                : discSpec.FullPath;

            itemScan.ThumbnailCandidateRelativePath = FindCandidateImageRelativePath(candidateFolder, rootFolder);
            if (string.IsNullOrEmpty(itemScan.ThumbnailCandidateRelativePath) && !string.Equals(candidateFolder, discSpec.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                itemScan.ThumbnailCandidateRelativePath = FindCandidateImageRelativePath(discSpec.FullPath, rootFolder);
            }

            if (!string.IsNullOrEmpty(printResult.ErrorMessage))
            {
                itemScan.ErrorMessage = printResult.ErrorMessage;
            }

            // 3. Bill quantity derived directly from final print folder (V2 simplification)
            if (printResult.Status == PrintFolderResolutionStatus.Resolved && printResult.PrintCount.HasValue)
            {
                int fileCount = printResult.PrintCount.Value;
                itemScan.PrintableFileCount = fileCount;
                itemScan.MismatchCount = 0;

                if (itemScan.PrintSpecification?.BillingMethod == BillingMethod.AlbumBasePlusExtra)
                {
                    itemScan.BillQuantity = 1; // 1 album per recognized album product folder
                    itemScan.QuantityResolutionMode = QuantityResolutionMode.UsePrint;

                    if (fileCount == 0)
                    {
                        itemScan.ScanStatus = ScanStatus.Failed;
                        itemScan.ErrorMessage = "Album không có tệp in nào (0 tệp).";
                    }
                    else if (fileCount < (itemScan.PrintSpecification.IncludedSheets ?? 10))
                    {
                        // Informational warning - non-blocking!
                        itemScan.ScanStatus = ScanStatus.Warning;
                        itemScan.ErrorMessage = $"Album dưới số trang/tờ tiêu chuẩn ({fileCount} < {itemScan.PrintSpecification.IncludedSheets ?? 10})";
                    }
                    else
                    {
                        itemScan.ScanStatus = ScanStatus.Success;
                    }
                }
                else
                {
                    // Photo print or FileCount
                    itemScan.BillQuantity = fileCount;
                    itemScan.QuantityResolutionMode = QuantityResolutionMode.UsePrint;
                    itemScan.ScanStatus = ScanStatus.Success;
                }
            }
            else
            {
                itemScan.MismatchCount = null;
                itemScan.BillQuantity = null;
                itemScan.ScanStatus = printResult.Status == PrintFolderResolutionStatus.AmbiguousPrintFolder
                    ? ScanStatus.Warning
                    : ScanStatus.Failed;
            }

        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed scanning specification '{Spec}'", discSpec.RelativePath);
            itemScan.ScanStatus = ScanStatus.Failed;
            itemScan.ErrorMessage = ex.Message;
        }

        return itemScan;
    }

    private string? FindCandidateImageRelativePath(string folderFullPath, string rootFolderFullPath)
    {
        try
        {
            if (!_fileSystem.DirectoryExists(folderFullPath)) return null;

            var files = _fileSystem.EnumerateFiles(folderFullPath).ToList();
            if (files.Count == 0) return null;

            // Preferred formats for thumbnail: jpg, jpeg, png, bmp
            var preferredFiles = files.Where(f =>
            {
                string ext = _fileSystem.GetExtension(f);
                return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (preferredFiles.Count > 0)
            {
                preferredFiles.Sort(NaturalStringComparer.Instance);
                return _fileSystem.GetRelativePath(rootFolderFullPath, preferredFiles[0]);
            }

            // Fallback: other photo formats (psd, psb, tif, raw, etc.) so we can display format badge
            var otherFiles = files.Where(f =>
            {
                string ext = _fileSystem.GetExtension(f);
                return ext.Equals(".psd", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".psb", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".cr2", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".cr3", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".nef", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".arw", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".dng", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (otherFiles.Count > 0)
            {
                otherFiles.Sort(NaturalStringComparer.Instance);
                return _fileSystem.GetRelativePath(rootFolderFullPath, otherFiles[0]);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
