using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Core.Services;

public class CustomerBillingService : ICustomerBillingService
{
    private readonly ICustomerRepository _customerRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly ICustomerBillRepository _customerBillRepository;
    private readonly IPrintSpecificationRepository _specificationRepository;
    private readonly IScanService? _scanService;
    private readonly IFileSystemAdapter? _fileSystem;
    private readonly IFolderStructureParser? _structureParser;
    private readonly IPrintFolderResolver? _printFolderResolver;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly IPrintSpecificationResolver? _specificationResolver;
    private readonly ICustomerResolver? _customerResolver;
    private readonly ILogger<CustomerBillingService>? _logger;
    private readonly IRootFolderRepository? _rootFolderRepository;

    public CustomerBillingService(
        ICustomerRepository customerRepository,
        IOrderRepository orderRepository,
        ICustomerBillRepository customerBillRepository,
        IPrintSpecificationRepository specificationRepository,
        IScanService? scanService = null,
        IFileSystemAdapter? fileSystem = null,
        IFolderStructureParser? structureParser = null,
        IPrintFolderResolver? printFolderResolver = null,
        ISettingsRepository? settingsRepository = null,
        IPrintSpecificationResolver? specificationResolver = null,
        ICustomerResolver? customerResolver = null,
        ILogger<CustomerBillingService>? logger = null,
        IRootFolderRepository? rootFolderRepository = null)
    {
        _customerRepository = customerRepository;
        _orderRepository = orderRepository;
        _customerBillRepository = customerBillRepository;
        _specificationRepository = specificationRepository;
        _scanService = scanService;
        _fileSystem = fileSystem;
        _structureParser = structureParser;
        _printFolderResolver = printFolderResolver;
        _settingsRepository = settingsRepository;
        _specificationResolver = specificationResolver;
        _customerResolver = customerResolver;
        _logger = logger;
        _rootFolderRepository = rootFolderRepository;
    }

    public async Task<CustomerUnbilledSummary> GetCustomerUnbilledSummaryAsync(long customerId, CancellationToken cancellationToken = default)
    {
        var customer = await _customerRepository.GetByIdAsync(customerId, cancellationToken);
        if (customer == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy khách hàng ID {customerId}.");
        }

        var lastBill = await _customerBillRepository.GetLastLockedOrExportedBillByCustomerIdAsync(customerId, cancellationToken);
        string? lastBillDate = lastBill?.PeriodEnd;
        string? lastBillNumber = lastBill?.BillNumber;

        // Fetch all orders for this customer across all date folders and alias folders
        var allOrders = await GetOrdersForCustomerAsync(customerId, cancellationToken);

        int unbilledOrderCount = 0;
        int unbilledJobCount = 0;
        long estimatedTotal = 0;
        bool hasOrdersFromPreviousPeriod = false;

        foreach (var order in allOrders)
        {
            var unbilledItems = order.Items.Where(i => !IsItemBilled(i)).ToList();
            if (unbilledItems.Count > 0)
            {
                unbilledOrderCount++;
                unbilledJobCount += unbilledItems.Count;

                if (!string.IsNullOrEmpty(lastBillDate) && string.CompareOrdinal(order.WorkDate, lastBillDate) <= 0)
                {
                    hasOrdersFromPreviousPeriod = true;
                }

                foreach (var item in unbilledItems)
                {
                    estimatedTotal += EstimateItemTotal(item, customer.PriceTier);
                }
            }
        }

        return new CustomerUnbilledSummary(
            CustomerId: customer.Id,
            CustomerName: customer.CanonicalName,
            UnbilledOrderCount: unbilledOrderCount,
            UnbilledJobCount: unbilledJobCount,
            EstimatedTotal: estimatedTotal,
            LastBillDate: lastBillDate,
            LastBillNumber: lastBillNumber,
            HasOrdersFromPreviousPeriod: hasOrdersFromPreviousPeriod
        );
    }

    public async Task<CustomerBillDraftResult> BuildOrRefreshDraftAsync(long customerId, bool forceRescan = true, CancellationToken cancellationToken = default)
    {
        var customer = await _customerRepository.GetByIdAsync(customerId, cancellationToken);
        if (customer == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy khách hàng ID {customerId}.");
        }

        var lastBill = await _customerBillRepository.GetLastLockedOrExportedBillByCustomerIdAsync(customerId, cancellationToken);
        string? lastBillDate = lastBill?.PeriodEnd;

        // 1. Fetch all orders for customer
        var orders = await GetOrdersForCustomerAsync(customerId, cancellationToken);

        // 2. Identify unbilled orders
        var unbilledOrders = new List<Order>();
        foreach (var order in orders)
        {
            // If rescan requested and scanService available, perform scoped smart scan of this order
            Order orderToProcess = order;
            if (forceRescan && _scanService != null && !string.IsNullOrWhiteSpace(order.RelativePath))
            {
                try
                {
                    var fresh = await _scanService.ScanOrderInRootAsync(order.RelativePath, order.RootFolderId, null, cancellationToken);
                    if (fresh != null)
                    {
                        orderToProcess = fresh;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to rescan order {RelativePath}", order.RelativePath);
                    throw new InvalidOperationException($"Không xác minh được '{order.RelativePath}': {ex.Message}", ex);
                }
            }

            var unbilledItems = orderToProcess.Items.Where(i => !IsItemBilled(i)).ToList();
            if (unbilledItems.Count > 0)
            {
                orderToProcess.Items = unbilledItems;
                unbilledOrders.Add(orderToProcess);
            }
        }

        // 3. Check existing draft to preserve user overrides
        var existingDraft = await _customerBillRepository.GetActiveDraftByCustomerIdAsync(customerId, cancellationToken);

        var existingLinesByJobId = existingDraft?.Lines.ToDictionary(l => l.ProductJobId) ?? new();
        var existingOrdersByOrderId = existingDraft?.Orders.ToDictionary(o => o.OrderId) ?? new();
        var existingAdjustments = existingDraft?.Adjustments ?? new();

        var billOrders = new List<CustomerBillOrder>();
        var billLines = new List<CustomerBillLine>();
        var warnings = new List<string>();
        var blockingIssues = new List<string>();

        int orderSortOrder = 0;
        int lineSortOrder = 0;

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string periodStart = today;
        string periodEnd = today;

        if (!string.IsNullOrEmpty(lastBillDate))
        {
            periodStart = GetNextDay(lastBillDate);
        }
        else if (unbilledOrders.Count > 0)
        {
            periodStart = unbilledOrders.Min(o => o.WorkDate) ?? today;
        }
        else
        {
            periodStart = today;
        }

        // Process unbilled orders and jobs
        foreach (var order in unbilledOrders.OrderBy(o => o.WorkDate).ThenBy(o => o.OriginalFolderName))
        {
            orderSortOrder++;
            bool isFromPrev = !string.IsNullOrEmpty(lastBillDate) && string.CompareOrdinal(order.WorkDate, lastBillDate) <= 0;
            if (isFromPrev)
            {
                string orderDisplayName = !string.IsNullOrWhiteSpace(order.OrderName) && !string.Equals(order.OrderName, "Đơn mặc định", StringComparison.OrdinalIgnoreCase)
                    ? order.OrderName
                    : order.OriginalFolderName;
                warnings.Add($"Đơn hàng '{orderDisplayName}' ({order.WorkDate}) là đơn chưa tính tiền từ kỳ trước.");
            }

            existingOrdersByOrderId.TryGetValue(order.Id, out var existingOrderSnapshot);
            bool orderIncluded = existingOrderSnapshot?.IsIncluded ?? true;

            var currentOrderLines = new List<CustomerBillLine>();

            foreach (var item in order.Items)
            {
                lineSortOrder++;
                string? issue = null;

                existingLinesByJobId.TryGetValue(item.Id, out var existingLine);
                bool lineIncluded = existingLine?.IsIncluded ?? (item.PrintFolderStatus == PrintFolderResolutionStatus.Resolved && (item.PrintCount ?? 0) > 0);

                // Validate print folder resolution
                if (item.PrintFolderStatus != PrintFolderResolutionStatus.Resolved || !item.PrintCount.HasValue)
                {
                    issue = $"Chưa có thư mục in hợp lệ ({item.PrintFolderStatus}).";
                    if (lineIncluded)
                    {
                        blockingIssues.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {issue}");
                    }
                    else
                    {
                        warnings.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {issue} (Đã tạm bỏ chọn)");
                    }
                }

                // Resolve specification
                PrintSpecification? spec = null;
                if (item.PrintSpecificationId.HasValue)
                {
                    spec = await _specificationRepository.GetByIdAsync(item.PrintSpecificationId.Value, cancellationToken);
                }

                if (spec == null)
                {
                    if (existingLine != null && existingLine.BilledUnitPrice > 0)
                    {
                        // Price has been manually set / overridden by operator
                    }
                    else
                    {
                        string unlinkedIssue = "Chưa được liên kết với bảng giá.";
                        issue = string.IsNullOrEmpty(issue) ? unlinkedIssue : $"{issue} {unlinkedIssue}";
                        if (lineIncluded)
                        {
                            blockingIssues.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {unlinkedIssue}");
                        }
                        else
                        {
                            warnings.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {unlinkedIssue} (Đã tạm bỏ chọn)");
                        }
                    }
                }

                int scannedQty = item.PrintCount ?? item.SourceCount;
                int billedQty;
                string? qtyOverrideReason = null;
                int? sheetCount = null;
                int? includedSheets = null;
                int? extraSheets = null;
                long? basePrice = null;
                long? extraSheetPrice = null;
                long configuredUnitPrice = 0;
                long billedUnitPrice;
                string? priceOverrideReason = null;
                long lineTotal;

                var billingMethod = spec?.BillingMethod ?? BillingMethod.FileCount;

                if (billingMethod == BillingMethod.AlbumBasePlusExtra)
                {
                    billedQty = 1;
                    sheetCount = scannedQty;
                    includedSheets = spec?.IncludedSheets ?? 10;
                    var (albumBase, albumExtra) = spec != null 
                        ? spec.GetEffectiveAlbumPrices(customer.PriceTier) 
                        : (0L, 0L);
                    basePrice = albumBase;
                    extraSheetPrice = albumExtra;
                    extraSheets = Math.Max(0, sheetCount.Value - includedSheets.Value);

                    if (sheetCount == 0)
                    {
                        issue = "Album không có tệp in nào (0 tệp).";
                        blockingIssues.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {issue}");
                    }
                    else if (sheetCount < includedSheets)
                    {
                        warnings.Add($"[{order.OriginalFolderName}] Album '{item.SpecificationFolderName}' có {sheetCount} tờ (ít hơn số tờ chuẩn {includedSheets}).");
                    }

                    configuredUnitPrice = basePrice.Value + (extraSheets.Value * extraSheetPrice.Value);
                    billedUnitPrice = existingLine?.BilledUnitPrice ?? configuredUnitPrice;
                    if (existingLine != null && existingLine.BilledUnitPrice != configuredUnitPrice)
                    {
                        priceOverrideReason = existingLine.PriceOverrideReason ?? "Chỉnh sửa thủ công";
                    }

                    lineTotal = billedUnitPrice;
                }
                else
                {
                    // Photo print
                    billedQty = existingLine?.BilledQuantity ?? item.BillQuantity ?? scannedQty;
                    if (existingLine != null && existingLine.BilledQuantity != scannedQty)
                    {
                        qtyOverrideReason = existingLine.QuantityOverrideReason ?? "Chỉnh sửa thủ công";
                    }

                    configuredUnitPrice = spec?.GetEffectiveUnitPrice(customer.PriceTier) ?? 0;
                    billedUnitPrice = existingLine?.BilledUnitPrice ?? configuredUnitPrice;
                    if (existingLine != null && existingLine.BilledUnitPrice != configuredUnitPrice)
                    {
                        priceOverrideReason = existingLine.PriceOverrideReason ?? "Chỉnh sửa thủ công";
                    }

                    lineTotal = billedQty * billedUnitPrice;
                }

                var billLine = new CustomerBillLine
                {
                    Id = existingLine?.Id ?? 0,
                    OrderId = order.Id,
                    ProductJobId = item.Id,
                    ProductSpecificationId = spec?.Id,
                    SpecificationFolderName = item.SpecificationFolderName,
                    ProductNameSnapshot = spec?.CanonicalName ?? item.SpecificationFolderName,
                    VariantSnapshot = spec?.CanonicalSize,
                    BillingMethodSnapshot = billingMethod,
                    ScannedQuantity = scannedQty,
                    BilledQuantity = billedQty,
                    QuantityOverrideReason = qtyOverrideReason,
                    SheetCount = sheetCount,
                    IncludedSheetsSnapshot = includedSheets,
                    ExtraSheetCount = extraSheets,
                    BasePriceSnapshot = basePrice,
                    ExtraSheetPriceSnapshot = extraSheetPrice,
                    ConfiguredUnitPrice = configuredUnitPrice,
                    BilledUnitPrice = billedUnitPrice,
                    PriceOverrideReason = priceOverrideReason,
                    LineTotal = lineTotal,
                    IsIncluded = lineIncluded,
                    SortOrder = lineSortOrder,
                    FinalPrintFolderPath = item.SelectedPrintFolderRelativePath,
                    FolderResolutionModeSnapshot = item.FolderResolutionMode,
                    IssueMessage = issue,
                    Note = existingLine?.Note
                };

                currentOrderLines.Add(billLine);
                billLines.Add(billLine);
            }

            long orderSubtotal = currentOrderLines.Where(l => l.IsIncluded).Sum(l => l.LineTotal);

            string resolvedOrderName = !string.IsNullOrWhiteSpace(order.OrderName) && !string.Equals(order.OrderName, "Đơn mặc định", StringComparison.OrdinalIgnoreCase)
                ? order.OrderName
                : order.OriginalFolderName;
            if (string.IsNullOrWhiteSpace(resolvedOrderName)) resolvedOrderName = "Đơn hàng";

            string effectiveOrderName = !string.IsNullOrWhiteSpace(existingOrderSnapshot?.OrderNameSnapshot) && !string.Equals(existingOrderSnapshot.OrderNameSnapshot, "Đơn mặc định", StringComparison.OrdinalIgnoreCase)
                ? existingOrderSnapshot.OrderNameSnapshot
                : resolvedOrderName;

            var billOrder = new CustomerBillOrder
            {
                Id = existingOrderSnapshot?.Id ?? 0,
                OrderId = order.Id,
                OrderCodeSnapshot = !string.IsNullOrWhiteSpace(order.OrderCode) ? order.OrderCode : existingOrderSnapshot?.OrderCodeSnapshot,
                OrderNameSnapshot = effectiveOrderName,
                OrderDateSnapshot = order.WorkDate,
                OriginalFolderNameSnapshot = order.OriginalFolderName,
                Subtotal = orderSubtotal,
                SortOrder = orderSortOrder,
                IsIncluded = orderIncluded,
                IsFromPreviousPeriod = isFromPrev,
                Lines = currentOrderLines
            };

            billOrders.Add(billOrder);
        }

        // Adjustments: preserve existing adjustments or create default 3 standard ones
        var adjustments = new List<BillAdjustment>();
        if (existingAdjustments.Count > 0)
        {
            adjustments = existingAdjustments.ToList();
        }
        else
        {
            adjustments.Add(new BillAdjustment { Type = AdjustmentType.Shipping, Label = "Phí vận chuyển", Direction = AdjustmentDirection.Add, Amount = 0, SortOrder = 1 });
            adjustments.Add(new BillAdjustment { Type = AdjustmentType.Surcharge, Label = "Phụ thu", Direction = AdjustmentDirection.Add, Amount = 0, SortOrder = 2 });
            adjustments.Add(new BillAdjustment { Type = AdjustmentType.Discount, Label = "Giảm giá", Direction = AdjustmentDirection.Deduct, Amount = 0, SortOrder = 3 });
        }

        string billNumber = existingDraft?.BillNumber ?? await _customerBillRepository.GenerateNextBillNumberAsync(today, cancellationToken);

        var draft = new CustomerBill
        {
            Id = existingDraft?.Id ?? 0,
            BillNumber = billNumber,
            CustomerId = customer.Id,
            CustomerNameSnapshot = customer.CanonicalName,
            PhoneSnapshot = customer.Phone,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Status = CustomerBillStatus.Draft,
            Note = existingDraft?.Note ?? string.Empty,
            Orders = billOrders,
            Lines = billLines,
            Adjustments = adjustments,
            CreatedAt = existingDraft?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        RecalculateTotals(draft);

        // Persist draft
        await _customerBillRepository.SaveBillAsync(draft, cancellationToken);

        return new CustomerBillDraftResult(draft, warnings, blockingIssues);
    }

    public CustomerBill RecalculateTotals(CustomerBill bill)
    {
        // If a line has an operator-provided price, clear the missing price issue
        foreach (var line in bill.Lines)
        {
            if (line.BilledUnitPrice > 0 && line.IssueMessage == "Chưa được liên kết với bảng giá.")
            {
                line.IssueMessage = null;
            }
        }

        // Order subtotal = sum of included lines
        foreach (var order in bill.Orders)
        {
            var lines = order.Lines != null && order.Lines.Count > 0
                ? order.Lines
                : bill.Lines.Where(l => l.OrderId == order.OrderId);
            order.Subtotal = lines.Where(l => l.IsIncluded).Sum(l => l.LineTotal);
        }

        // Product subtotal = sum of lines included AND belonging to included orders
        var includedOrderIds = bill.Orders.Where(o => o.IsIncluded).Select(o => o.OrderId).ToHashSet();
        bill.ProductSubtotal = bill.Lines
            .Where(l => l.IsIncluded && includedOrderIds.Contains(l.OrderId))
            .Sum(l => l.LineTotal);

        long addTotal = bill.Adjustments.Where(a => a.Direction == AdjustmentDirection.Add).Sum(a => a.Amount);
        long deductTotal = bill.Adjustments.Where(a => a.Direction == AdjustmentDirection.Deduct).Sum(a => a.Amount);

        bill.AdjustmentsTotal = addTotal - deductTotal;
        bill.GrandTotal = Math.Max(0, bill.ProductSubtotal + bill.AdjustmentsTotal);

        return bill;
    }

    public Task<CustomerBill?> GetBillSnapshotAsync(long billId, CancellationToken cancellationToken = default)
        => _customerBillRepository.GetByIdAsync(billId, cancellationToken);

    public async Task<CustomerBill> LockBillAsync(CustomerBill draft, CancellationToken cancellationToken = default)
    {
        if (draft.Status != CustomerBillStatus.Draft)
            throw new InvalidOperationException("Hãy mở lại hóa đơn trước khi khóa một bản sửa đổi.");
        RecalculateTotals(draft);

        var includedOrderIds = draft.Orders.Where(o => o.IsIncluded).Select(o => o.OrderId).ToHashSet();
        var includedLines = draft.Lines.Where(l => l.IsIncluded && includedOrderIds.Contains(l.OrderId)).ToList();

        if (includedLines.Count == 0)
        {
            throw new InvalidOperationException("Không thể khóa bill không có dòng sản phẩm nào được chọn.");
        }

        // Validate critical unresolved issues
        var issues = includedLines.Where(l => !string.IsNullOrEmpty(l.IssueMessage)).Select(l => l.IssueMessage).Distinct().ToList();
        if (issues.Count > 0)
        {
            throw new InvalidOperationException($"Không thể khóa bill khi còn mục chưa xử lý: {string.Join("; ", issues)}");
        }

        // Validate negative amounts
        if (draft.ProductSubtotal < 0 || draft.GrandTotal < 0)
        {
            throw new InvalidOperationException("Tổng bill không được âm.");
        }

        foreach (var adj in draft.Adjustments)
        {
            if (adj.Amount < 0)
            {
                throw new InvalidOperationException($"Số tiền điều chỉnh '{adj.Label}' không được âm.");
            }
        }

        // Verify exactly the included physical orders, retaining reviewed quantities/prices.
        // Rescan creates a new observation; rebind its job IDs only after comparison succeeds.
        if (_scanService != null)
        {
            foreach (long orderId in includedOrderIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var order = await _orderRepository.GetOrderByIdAsync(orderId, cancellationToken)
                    ?? throw new InvalidOperationException("Đơn hàng không còn tồn tại. Hãy duyệt lại bill.");
                var fresh = await _scanService.ScanOrderInRootAsync(order.RelativePath, order.RootFolderId, null, cancellationToken);
                if (fresh == null || fresh.Status == OrderStatus.Error)
                    throw new InvalidOperationException($"Không xác minh được '{order.RelativePath}'. Hãy quét và duyệt lại.");
                foreach (var line in draft.Lines.Where(l => l.OrderId == orderId))
                {
                    var item = fresh.Items.FirstOrDefault(i => string.Equals(i.SpecificationFolderName,
                        line.SpecificationFolderName, StringComparison.OrdinalIgnoreCase));
                    if (line.IsIncluded && (item == null || item.ScanStatus == ScanStatus.Failed ||
                        item.PrintFolderStatus != PrintFolderResolutionStatus.Resolved ||
                        line.ScannedQuantity != item.PrintCount ||
                        !string.Equals(line.FinalPrintFolderPath, item.SelectedPrintFolderRelativePath, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"Dữ liệu '{order.RelativePath}/{line.SpecificationFolderName}' đã thay đổi hoặc chưa đọc đủ. Hãy quét và duyệt lại trước khi chốt.");
                    if (item != null) line.ProductJobId = item.Id;
                }
            }
        }

        draft.Status = CustomerBillStatus.Locked;
        draft.LockedAt = DateTimeOffset.UtcNow;
        draft.UpdatedAt = DateTimeOffset.UtcNow;

        var jobIds = includedLines.Select(l => l.ProductJobId).ToList();

        // Persist snapshot and update associated jobs and orders atomically in one transaction
        await _customerBillRepository.LockCustomerBillAtomicAsync(draft, jobIds, includedOrderIds.ToList(), cancellationToken);

        _logger?.LogInformation("Customer bill {BillNumber} for customer {CustomerId} locked successfully. Total: {GrandTotal}",
            draft.BillNumber, draft.CustomerId, draft.GrandTotal);

        return draft;
    }

    public async Task ReopenBillAsync(long billId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "Mở lại để chỉnh sửa";
        }

        var bill = await _customerBillRepository.GetByIdAsync(billId, cancellationToken);
        if (bill == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy bill ID {billId}.");
        }

        if (bill.Status == CustomerBillStatus.Draft)
        {
            throw new InvalidOperationException($"Bill {bill.BillNumber} đang ở trạng thái bản nháp (Draft), không cần mở lại.");
        }

        bill.Status = CustomerBillStatus.Draft;
        bill.LockedAt = null;
        bill.ExportedAt = null;
        bill.UpdatedAt = DateTimeOffset.UtcNow;

        var jobIds = bill.Lines.Select(l => l.ProductJobId).ToList();
        var orderIds = bill.Orders.Select(o => o.OrderId).Distinct().ToList();

        // Update bill, unlink jobs and restore order statuses atomically in one transaction
        await _customerBillRepository.ReopenCustomerBillAtomicAsync(bill, jobIds, orderIds, cancellationToken);

        _logger?.LogWarning("Bill {BillNumber} (ID {BillId}) REOPENED by user. Reason: {Reason}",
            bill.BillNumber, bill.Id, reason);
    }

    public async Task RecordBillExportedAsync(long billId, string exportFilePath, CancellationToken cancellationToken = default)
    {
        var bill = await _customerBillRepository.GetByIdAsync(billId, cancellationToken);
        if (bill != null)
        {
            bill.ExportFilePath = exportFilePath;
            bill.ExportedAt = DateTimeOffset.UtcNow;
            bill.Status = CustomerBillStatus.Exported;
            bill.UpdatedAt = DateTimeOffset.UtcNow;
            await _customerBillRepository.SaveBillAsync(bill, cancellationToken);
            _logger?.LogInformation("Recorded export for bill {BillNumber} (ID {BillId}) at {ExportFilePath}",
                bill.BillNumber, bill.Id, exportFilePath);
        }
    }

    public async Task DetachOrderFromCustomerBillAsync(long orderId, CancellationToken cancellationToken = default)
    {
        var order = await _orderRepository.GetOrderByIdAsync(orderId, cancellationToken);
        if (order == null) return;

        // 1. Identify which bill(s) contain this order
        CustomerBill? bill = await _customerBillRepository.GetBillByOrderIdAsync(orderId, cancellationToken);
        if (bill == null && !string.IsNullOrWhiteSpace(order.RelativePath))
        {
            var bills = await _customerBillRepository.GetBillsBySourceFolderPathAsync(order.RelativePath, cancellationToken);
            bill = bills.FirstOrDefault();
        }

        if (bill != null)
        {
            if (bill.Status != CustomerBillStatus.Draft)
                throw new InvalidOperationException("Hãy mở lại hóa đơn trước khi tách đơn.");
            // Remove order and lines belonging to this order
            bill.Orders.RemoveAll(o => o.OrderId == orderId);
            bill.Lines.RemoveAll(l => l.OrderId == orderId);
            bill.SourceFolders.RemoveAll(sf => string.Equals(sf.FolderPath, order.RelativePath, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(sf.NormalizedFolderPath, order.RelativePath.Trim().Replace('\\', '/').ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));

            // If bill now has no orders and no lines left, delete it entirely
            if (bill.Orders.Count == 0 && bill.Lines.Count == 0)
            {
                await _customerBillRepository.DeleteBillAsync(bill.Id, cancellationToken);
                _logger?.LogInformation("Deleted empty bill {BillNumber} (ID {BillId}) after order {OrderId} was detached.",
                    bill.BillNumber, bill.Id, orderId);
            }
            else
            {
                // Recalculate remaining lines & totals
                RecalculateTotals(bill);
                await _customerBillRepository.SaveBillAsync(bill, cancellationToken);
                _logger?.LogInformation("Updated bill {BillNumber} (ID {BillId}) after order {OrderId} was detached. New Total: {Total}",
                    bill.BillNumber, bill.Id, orderId, bill.GrandTotal);
            }
        }

        // 2. Clear customer_bill_id on order items
        var itemIds = order.Items.Select(i => i.Id).ToList();
        if (itemIds.Count > 0)
        {
            await _orderRepository.SetCustomerBillIdForItemsAsync(itemIds, null, cancellationToken);
        }

        // 3. Set order status back to Ready (or NeedsReview if items have issues)
        bool hasIssues = order.Items.Count == 0 ||
                         order.Items.Any(i => i.PrintFolderStatus != PrintFolderResolutionStatus.Resolved ||
                                              i.ScanStatus == ScanStatus.Failed ||
                                              i.PrintSpecificationId == null);
        var targetStatus = hasIssues ? OrderStatus.NeedsReview : OrderStatus.Ready;
        await _orderRepository.UpdateOrderStatusAsync(orderId, targetStatus, cancellationToken);
    }

    public async Task<CustomerBill> SplitOrdersToNewBillAsync(long currentBillId, IReadOnlyList<long> orderIdsToMove, CancellationToken cancellationToken = default)
    {
        if (orderIdsToMove == null || orderIdsToMove.Count == 0)
        {
            throw new ArgumentException("Danh sách đơn hàng cần tách không được rỗng.", nameof(orderIdsToMove));
        }

        var currentBill = await _customerBillRepository.GetByIdAsync(currentBillId, cancellationToken);
        if (currentBill == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy hóa đơn ID {currentBillId}.");
        }

        if (currentBill.Status == CustomerBillStatus.Locked || currentBill.Status == CustomerBillStatus.Exported || currentBill.IsPaid)
        {
            throw new InvalidOperationException("Hóa đơn đã bị khóa, đã xuất hoặc đã thanh toán. Vui lòng mở khóa (Reopen) hoặc hủy thanh toán trước khi tách đơn.");
        }

        var ordersToMoveSet = new HashSet<long>(orderIdsToMove);
        var movingOrders = currentBill.Orders.Where(o => ordersToMoveSet.Contains(o.OrderId)).ToList();

        if (movingOrders.Count != ordersToMoveSet.Count)
        {
            throw new InvalidOperationException("Không tìm thấy đơn hàng nào cần tách trong hóa đơn hiện tại.");
        }

        if (movingOrders.Count >= currentBill.Orders.Count)
        {
            throw new InvalidOperationException("Không thể tách toàn bộ đơn hàng sang hóa đơn mới. Vui lòng giữ lại ít nhất 1 đơn hàng trong hóa đơn gốc.");
        }

        var movingLines = currentBill.Lines.Where(l => ordersToMoveSet.Contains(l.OrderId)).ToList();

        // 1. Generate next unique bill number
        string dateForNumber = currentBill.PeriodEnd ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
        string newBillNumber = await _customerBillRepository.GenerateNextBillNumberAsync(dateForNumber, cancellationToken);

        // 2. Identify date range for the new bill
        var movingOrderDates = movingOrders
            .Select(o => o.OrderDateSnapshot)
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();

        string newPeriodStart = (movingOrderDates.Count > 0 ? movingOrderDates.Min() : currentBill.PeriodStart) ?? currentBill.PeriodStart ?? string.Empty;
        string newPeriodEnd = (movingOrderDates.Count > 0 ? movingOrderDates.Max() : currentBill.PeriodEnd) ?? currentBill.PeriodEnd ?? string.Empty;

        // 3. Create new bill
        var newBill = new CustomerBill
        {
            BillNumber = newBillNumber,
            CustomerId = currentBill.CustomerId,
            CustomerNameSnapshot = currentBill.CustomerNameSnapshot,
            PhoneSnapshot = currentBill.PhoneSnapshot,
            ShippingAddressSnapshot = currentBill.ShippingAddressSnapshot,
            Note = currentBill.Note,
            BillType = currentBill.BillType,
            Status = currentBill.Status,
            PeriodStart = newPeriodStart,
            PeriodEnd = newPeriodEnd,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        newBill.Adjustments.Add(new BillAdjustment { Type = AdjustmentType.Shipping, Label = "Phí vận chuyển", Direction = AdjustmentDirection.Add, Amount = 0, SortOrder = 1 });
        newBill.Adjustments.Add(new BillAdjustment { Type = AdjustmentType.Surcharge, Label = "Phụ thu", Direction = AdjustmentDirection.Add, Amount = 0, SortOrder = 2 });
        newBill.Adjustments.Add(new BillAdjustment { Type = AdjustmentType.Discount, Label = "Giảm giá", Direction = AdjustmentDirection.Deduct, Amount = 0, SortOrder = 3 });

        int lineSort = 0;
        foreach (var line in movingLines)
        {
            lineSort++;
            newBill.Lines.Add(new CustomerBillLine
            {
                OrderId = line.OrderId,
                ProductJobId = line.ProductJobId,
                ProductSpecificationId = line.ProductSpecificationId,
                SpecificationFolderName = line.SpecificationFolderName,
                ProductNameSnapshot = line.ProductNameSnapshot,
                VariantSnapshot = line.VariantSnapshot,
                BillingMethodSnapshot = line.BillingMethodSnapshot,
                ScannedQuantity = line.ScannedQuantity,
                BilledQuantity = line.BilledQuantity,
                QuantityOverrideReason = line.QuantityOverrideReason,
                SheetCount = line.SheetCount,
                IncludedSheetsSnapshot = line.IncludedSheetsSnapshot,
                ExtraSheetCount = line.ExtraSheetCount,
                BasePriceSnapshot = line.BasePriceSnapshot,
                ExtraSheetPriceSnapshot = line.ExtraSheetPriceSnapshot,
                ConfiguredUnitPrice = line.ConfiguredUnitPrice,
                BilledUnitPrice = line.BilledUnitPrice,
                PriceOverrideReason = line.PriceOverrideReason,
                LineTotal = line.LineTotal,
                IsIncluded = line.IsIncluded,
                SortOrder = lineSort,
                FinalPrintFolderPath = line.FinalPrintFolderPath,
                FolderResolutionModeSnapshot = line.FolderResolutionModeSnapshot,
                IssueMessage = line.IssueMessage,
                Note = line.Note
            });
        }

        // Transfer orders with lines wired
        int sortOrder = 0;
        foreach (var order in movingOrders)
        {
            sortOrder++;
            var orderLines = newBill.Lines.Where(l => l.OrderId == order.OrderId).ToList();
            newBill.Orders.Add(new CustomerBillOrder
            {
                OrderId = order.OrderId,
                OrderCodeSnapshot = order.OrderCodeSnapshot,
                OriginalFolderNameSnapshot = order.OriginalFolderNameSnapshot,
                OrderNameSnapshot = order.OrderNameSnapshot,
                OrderDateSnapshot = order.OrderDateSnapshot,
                SourceFolderPath = order.SourceFolderPath,
                Subtotal = order.Subtotal,
                IsIncluded = order.IsIncluded,
                IsFromPreviousPeriod = order.IsFromPreviousPeriod,
                SortOrder = sortOrder,
                Lines = orderLines
            });
        }

        // Transfer Guest source folders if applicable
        var movingOrderPaths = new HashSet<string>(movingOrders.Select(o => o.SourceFolderPath).Where(p => !string.IsNullOrEmpty(p))!, StringComparer.OrdinalIgnoreCase);
        var movingSourceFolders = currentBill.SourceFolders
            .Where(sf => movingOrderPaths.Contains(sf.FolderPath) || movingOrderPaths.Contains(sf.NormalizedFolderPath))
            .ToList();

        foreach (var sf in movingSourceFolders)
        {
            newBill.SourceFolders.Add(new GuestBillSourceFolder
            {
                FolderPath = sf.FolderPath,
                NormalizedFolderPath = sf.NormalizedFolderPath,
                CreatedAt = sf.CreatedAt
            });
        }

        RecalculateTotals(newBill);

        // 4. Remove moving items from original bill and recalculate
        currentBill.Orders.RemoveAll(o => ordersToMoveSet.Contains(o.OrderId));
        currentBill.Lines.RemoveAll(l => ordersToMoveSet.Contains(l.OrderId));
        currentBill.SourceFolders.RemoveAll(sf => movingOrderPaths.Contains(sf.FolderPath) || movingOrderPaths.Contains(sf.NormalizedFolderPath));

        var remainingOrderDates = currentBill.Orders
            .Select(o => o.OrderDateSnapshot)
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();
        if (remainingOrderDates.Count > 0)
        {
            currentBill.PeriodStart = remainingOrderDates.Min()!;
            currentBill.PeriodEnd = remainingOrderDates.Max()!;
        }

        currentBill.UpdatedAt = DateTimeOffset.UtcNow;
        RecalculateTotals(currentBill);
        await _customerBillRepository.SplitBillAtomicAsync(currentBill, newBill, orderIdsToMove,
            cancellationToken);

        _logger?.LogInformation(
            "Tách thành công {Count} đơn từ hóa đơn {OriginalBill} sang hóa đơn mới {NewBill} (ID {NewBillId}) cho khách hàng '{Customer}'.",
            movingOrders.Count, currentBill.BillNumber, newBill.BillNumber, newBill.Id, currentBill.CustomerNameSnapshot);

        return newBill;
    }

    public async Task SyncBillWithScannedOrderAsync(Order scannedOrder, CancellationToken cancellationToken = default)
    {
        if (scannedOrder == null) return;

        // Find associated bill
        CustomerBill? bill = await _customerBillRepository.GetBillByOrderIdAsync(scannedOrder.Id, cancellationToken);
        if (bill == null)
        {
            bill = await _customerBillRepository.GetLockedBillByOrderIdAsync(scannedOrder.Id, cancellationToken);
        }
        if (bill == null && !string.IsNullOrWhiteSpace(scannedOrder.RelativePath))
        {
            var bills = await _customerBillRepository.GetBillsBySourceFolderPathAsync(scannedOrder.RelativePath, cancellationToken);
            bill = bills.FirstOrDefault();
        }

        if (bill == null) return;

        // If the bill is already Locked or Exported, do NOT mutate it!
        if (bill.Status == CustomerBillStatus.Locked || bill.Status == CustomerBillStatus.Exported)
        {
            _logger?.LogWarning("Attempted to sync scanned order {OrderId} with bill {BillNumber}, but bill is {Status}. Skipping mutation.",
                scannedOrder.Id, bill.BillNumber, bill.Status);
            return;
        }

        PriceTier billCustomerTier = PriceTier.Retail;
        if (bill.CustomerId.HasValue)
        {
            var cust = await _customerRepository.GetByIdAsync(bill.CustomerId.Value, cancellationToken);
            if (cust != null) billCustomerTier = cust.PriceTier;
        }

        // Sync lines for this order
        var orderLines = bill.Lines.Where(l => l.OrderId == scannedOrder.Id).ToList();
        var linesBySpecFolder = orderLines
            .Where(l => !string.IsNullOrEmpty(l.SpecificationFolderName))
            .ToDictionary(l => l.SpecificationFolderName!, StringComparer.OrdinalIgnoreCase);

        var currentSpecFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in scannedOrder.Items)
        {
            currentSpecFolders.Add(item.SpecificationFolderName);
            int newQty = item.PrintCount ?? item.SourceCount;

            if (linesBySpecFolder.TryGetValue(item.SpecificationFolderName, out var line))
            {
                // Update existing line
                line.ScannedQuantity = newQty;
                line.BilledQuantity = newQty;

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

                line.IssueMessage = item.PrintFolderStatus != PrintFolderResolutionStatus.Resolved
                    ? $"Chưa có thư mục in hợp lệ ({item.PrintFolderStatus})."
                    : null;
            }
            else
            {
                // A new spec folder was added in this order! Create a new line for it.
                PrintSpecification? spec = null;
                if (item.PrintSpecificationId.HasValue)
                {
                    spec = await _specificationRepository.GetByIdAsync(item.PrintSpecificationId.Value, cancellationToken);
                }

                var billingMethod = spec?.BillingMethod ?? BillingMethod.FileCount;
                long unitPrice = spec?.GetEffectiveUnitPrice(billCustomerTier) ?? 0;
                var (basePrice, extraSheetPrice) = spec?.GetEffectiveAlbumPrices(billCustomerTier) ?? (0L, 0L);
                int includedSheets = spec?.IncludedSheets ?? 10;

                var newLine = new CustomerBillLine
                {
                    BillId = bill.Id,
                    OrderId = scannedOrder.Id,
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
        bill.Lines.RemoveAll(l => l.OrderId == scannedOrder.Id && (l.SpecificationFolderName == null || !currentSpecFolders.Contains(l.SpecificationFolderName)));

        // Update CustomerBillOrder subtotal
        var billOrder = bill.Orders.FirstOrDefault(o => o.OrderId == scannedOrder.Id);
        if (billOrder != null)
        {
            billOrder.Subtotal = bill.Lines.Where(l => l.OrderId == scannedOrder.Id && l.IsIncluded).Sum(l => l.LineTotal);
        }

        // Recalculate bill totals
        RecalculateTotals(bill);

        // Save updated bill
        await _customerBillRepository.SaveBillAsync(bill, cancellationToken);

        // Ensure order items are linked
        var jobIds = bill.Lines.Where(l => l.OrderId == scannedOrder.Id).Select(l => l.ProductJobId).ToList();
        if (jobIds.Count > 0)
        {
            await _orderRepository.SetCustomerBillIdForItemsAsync(jobIds, bill.Id, cancellationToken);
        }

        _logger?.LogInformation("Synchronized bill {BillNumber} (ID {BillId}) with order {OrderId}. New grand total: {GrandTotal}",
            bill.BillNumber, bill.Id, scannedOrder.Id, bill.GrandTotal);
    }

    public async Task<IReadOnlyList<DuplicateFolderWarning>> CheckDuplicateSourceFoldersAsync(IEnumerable<string> sourceFolderPaths, CancellationToken cancellationToken = default)
    {
        var warnings = new List<DuplicateFolderWarning>();
        var seenBills = new HashSet<long>();

        foreach (var folder in sourceFolderPaths)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string norm = PathNormalizer.Normalize(folder);
            var bills = await _customerBillRepository.GetBillsBySourceFolderPathAsync(norm, cancellationToken);
            foreach (var bill in bills)
            {
                if ((bill.Status == CustomerBillStatus.Locked || bill.Status == CustomerBillStatus.Exported) && seenBills.Add(bill.Id))
                {
                    string billedDate = bill.LockedAt?.ToString("yyyy-MM-dd") ?? bill.CreatedAt.ToString("yyyy-MM-dd");
                    warnings.Add(new DuplicateFolderWarning(
                        FolderPath: folder,
                        NormalizedFolderPath: PathNormalizer.Normalize(folder),
                        PreviousBillId: bill.Id,
                        PreviousBillNumber: bill.BillNumber,
                        PreviousBillDate: billedDate,
                        PreviousBillGrandTotal: bill.GrandTotal,
                        PreviousBillStatus: bill.Status,
                        PreviousBillExportPath: bill.ExportFilePath
                    ));
                }
            }
        }

        return warnings;
    }

    public Task<CustomerBillDraftResult> BuildGuestBillDraftAsync(IReadOnlyList<string> sourceFolderPaths,
        string? customGuestName = null, long? existingDraftId = null, bool persistDraft = false,
        CancellationToken cancellationToken = default)
        => Task.Run(() => BuildGuestBillDraftCoreAsync(sourceFolderPaths, customGuestName, existingDraftId, persistDraft, cancellationToken), cancellationToken);

    private async Task<CustomerBillDraftResult> BuildGuestBillDraftCoreAsync(
        IReadOnlyList<string> sourceFolderPaths,
        string? customGuestName = null,
        long? existingDraftId = null,
        bool persistDraft = false,
        CancellationToken cancellationToken = default)
    {
        if (sourceFolderPaths == null || sourceFolderPaths.Count == 0)
        {
            throw new ArgumentException("Cần chọn ít nhất một thư mục nguồn để tạo bill khách lẻ.", nameof(sourceFolderPaths));
        }

        // Determine Guest Name
        string guestName = !string.IsNullOrWhiteSpace(customGuestName)
            ? customGuestName.Trim()
            : (_fileSystem != null ? _fileSystem.GetFileName(sourceFolderPaths[0]) : System.IO.Path.GetFileName(sourceFolderPaths[0].TrimEnd('\\', '/')));

        if (string.IsNullOrWhiteSpace(guestName))
        {
            guestName = "Khách lẻ";
        }

        // Determine Root & Supported Extensions
        string rootFolder = string.Empty;
        var supportedExts = new HashSet<string>(new[] { ".jpg", ".jpeg", ".png", ".tif" }, StringComparer.OrdinalIgnoreCase);

        if (_settingsRepository != null)
        {
            var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
            rootFolder = settings.RootFolder ?? string.Empty;
            if (settings.SupportedExtensions != null && settings.SupportedExtensions.Count > 0)
            {
                supportedExts = new HashSet<string>(settings.SupportedExtensions, StringComparer.OrdinalIgnoreCase);
            }
        }

        if (string.IsNullOrEmpty(rootFolder))
        {
            rootFolder = sourceFolderPaths[0];
        }
        else
        {
            string normRoot = System.IO.Path.GetFullPath(rootFolder.TrimEnd('\\', '/'));
            foreach (var path in sourceFolderPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string normPath = System.IO.Path.GetFullPath(path.TrimEnd('\\', '/'));
                if (string.Equals(normPath, normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Không thể tạo Quick Bill cho Thư Mục Gốc: '{path}'. Vui lòng chọn thư mục đơn hàng cụ thể của khách.");
                }

                if (normPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = System.IO.Path.GetRelativePath(normRoot, normPath).Replace('/', '\\').Trim('\\');
                    string[] parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 1 && System.Text.RegularExpressions.Regex.IsMatch(parts[0], @"^\d{4}[-_.]\d{2}[-_.]\d{2}$"))
                    {
                        throw new InvalidOperationException($"Thư mục '{parts[0]}' là Thư Mục Ngày của xưởng in. Vui lòng chọn thư mục đơn hàng của khách bên trong.");
                    }
                }
            }
        }

        // 1. Check existing draft to preserve user overrides
        CustomerBill? existingDraft = null;
        if (existingDraftId.HasValue && existingDraftId.Value > 0)
        {
            existingDraft = await _customerBillRepository.GetByIdAsync(existingDraftId.Value, cancellationToken);
            if (existingDraft != null && existingDraft.Status != CustomerBillStatus.Draft)
                throw new InvalidOperationException("Hãy mở lại hóa đơn trước khi quét lại bản nháp.");
        }

        var existingLinesByJobId = existingDraft?.Lines.ToDictionary(l => l.ProductJobId) ?? new();
        var existingOrdersByOrderId = existingDraft?.Orders.ToDictionary(o => o.OrderId) ?? new();
        var existingAdjustments = existingDraft?.Adjustments ?? new();

        var billOrders = new List<CustomerBillOrder>();
        var billLines = new List<CustomerBillLine>();
        var warnings = new List<string>();
        var blockingIssues = new List<string>();
        Customer? matchedCustomerForBill = null;

        int orderSortOrder = 0;
        int lineSortOrder = 0;
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // 2. Scan each source folder
        foreach (var sourceFolderPath in sourceFolderPaths)
        {
            if (string.IsNullOrWhiteSpace(sourceFolderPath)) continue;

            long? sourceRootId = null;
            if (_rootFolderRepository != null)
            {
                var roots = await _rootFolderRepository.GetAllAsync(cancellationToken);
                var sourceRoot = roots.Where(r => PathNormalizer.Normalize(sourceFolderPath).StartsWith(
                    PathNormalizer.Normalize(r.FullPath).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.FullPath.Length).FirstOrDefault();
                if (sourceRoot != null) { rootFolder = sourceRoot.FullPath; sourceRootId = sourceRoot.Id; }
            }
            IReadOnlyList<DiscoveredOrder> discoveredOrders;
            if (_structureParser != null)
            {
                discoveredOrders = _structureParser.DiscoverOrdersInFolder(rootFolder, sourceFolderPath, today);
            }
            else
            {
                discoveredOrders = new List<DiscoveredOrder>();
            }

            foreach (var discOrder in discoveredOrders)
            {
                orderSortOrder++;

                // Build or fetch Order
                Order? existingOrder = null;
                if (_orderRepository != null && !string.IsNullOrWhiteSpace(discOrder.RelativePath))
                {
                    existingOrder = sourceRootId.HasValue
                        ? await _orderRepository.GetOrderByRootAndRelativePathAsync(sourceRootId.Value, discOrder.RelativePath, cancellationToken)
                        : await _orderRepository.GetOrderByRelativePathAsync(discOrder.RelativePath, cancellationToken);
                }

                // Preserve customer if already assigned, or resolve via customer resolver
                long? customerId = existingOrder?.CustomerId;
                Customer? customer = existingOrder?.Customer;

                if (customerId == null && _customerResolver != null && !string.IsNullOrWhiteSpace(discOrder.OriginalCustomerFolderName))
                {
                    var custResult = await _customerResolver.ResolveCustomerAsync(discOrder.OriginalCustomerFolderName, cancellationToken);
                    if (custResult.Status == CustomerResolutionStatus.ExactMatch || custResult.Status == CustomerResolutionStatus.NormalizedMatch)
                    {
                        matchedCustomerForBill ??= custResult.ResolvedCustomer;
                    }
                }

                var order = existingOrder ?? new Order
                {
                    RootFolderId = sourceRootId,
                    WorkDate = discOrder.Date,
                    OriginalFolderName = discOrder.OriginalCustomerFolderName,
                    RelativePath = discOrder.RelativePath,
                    OrderKind = discOrder.Kind,
                    OrderName = discOrder.OrderName,
                    CustomerId = customerId,
                    Customer = customer,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                order.OrderKind = discOrder.Kind;
                order.OrderName = discOrder.OrderName;
                order.CustomerId = customerId;
                if (customer != null)
                {
                    order.Customer = customer;
                }
                order.LastScanAt = DateTimeOffset.UtcNow;
                order.UpdatedAt = DateTimeOffset.UtcNow;

                var snapshot = new ScanSnapshot
                {
                    OrderId = order.Id,
                    StartedAt = DateTimeOffset.UtcNow,
                    Scope = ScanScope.Order,
                    Status = ScanStatus.Success
                };

                var scannedItems = new List<OrderItemScan>();

                foreach (var discSpec in discOrder.Specifications)
                {
                    var existingItem = existingOrder?.Items
                        .FirstOrDefault(i => string.Equals(i.SpecificationRelativePath, discSpec.RelativePath, StringComparison.OrdinalIgnoreCase));

                    string? previouslySelectedPrintFolder = existingItem?.FolderResolutionMode == BillingFolderResolutionMode.ManuallySelected
                        ? existingItem.SelectedPrintFolderRelativePath
                        : null;

                    var itemScan = new OrderItemScan
                    {
                        SpecificationFolderName = discSpec.FolderName,
                        SpecificationRelativePath = discSpec.RelativePath,
                        ScanStatus = ScanStatus.Success
                    };

                    // Resolve Spec
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

                    // Source count
                    int sourceCount = 0;
                    if (_fileSystem != null && _fileSystem.DirectoryExists(discSpec.FullPath))
                    {
                        foreach (var file in _fileSystem.EnumerateFiles(discSpec.FullPath))
                        {
                            string ext = _fileSystem.GetExtension(file);
                            if (supportedExts.Contains(ext))
                            {
                                sourceCount++;
                            }
                        }
                    }
                    itemScan.SourceCount = sourceCount;

                    // Resolve Print Folder
                    if (_printFolderResolver != null)
                    {
                        var printResult = _printFolderResolver.ResolvePrintFolder(
                            discSpec.FullPath,
                            rootFolder,
                            supportedExts,
                            previouslySelectedPrintFolder);

                        itemScan.PrintFolderStatus = printResult.Status;
                        itemScan.CandidatePrintFolderRelativePaths = printResult.CandidatePrintFolderRelativePaths.ToList();
                        itemScan.SelectedPrintFolderRelativePath = printResult.SelectedPrintFolderRelativePath;
                        itemScan.FolderResolutionMode = printResult.ResolutionMode;
                        itemScan.PrintCount = printResult.PrintCount;
                        itemScan.PrintableFileCount = printResult.PrintCount;

                        if (printResult.Status == PrintFolderResolutionStatus.Resolved)
                        {
                            if (sourceCount == printResult.PrintCount)
                            {
                                itemScan.BillQuantity = printResult.PrintCount;
                                itemScan.QuantityResolutionMode = QuantityResolutionMode.AutoMatch;
                            }
                            else
                            {
                                itemScan.MismatchCount = Math.Abs(sourceCount - (printResult.PrintCount ?? 0));
                                itemScan.ScanStatus = ScanStatus.Warning;
                            }
                        }
                        else
                        {
                            itemScan.ScanStatus = ScanStatus.Warning;
                        }
                    }

                    scannedItems.Add(itemScan);
                }

                order.Items = scannedItems;
                snapshot.Items = scannedItems;
                snapshot.CompletedAt = DateTimeOffset.UtcNow;
                order.Status = (scannedItems.Count == 0 || scannedItems.Any(i => i.PrintFolderStatus != PrintFolderResolutionStatus.Resolved))
                    ? OrderStatus.NeedsReview
                    : OrderStatus.Ready;

                if (_orderRepository != null)
                {
                    await _orderRepository.SaveOrderAsync(order, snapshot, cancellationToken);
                }

                // Map into bill order and bill lines
                existingOrdersByOrderId.TryGetValue(order.Id, out var existingOrderSnapshot);
                bool orderIncluded = existingOrderSnapshot?.IsIncluded ?? true;

                var currentOrderLines = new List<CustomerBillLine>();

                foreach (var item in order.Items)
                {
                    lineSortOrder++;
                    string? issue = null;

                    existingLinesByJobId.TryGetValue(item.Id, out var existingLine);
                    bool lineIncluded = existingLine?.IsIncluded ?? (item.PrintFolderStatus == PrintFolderResolutionStatus.Resolved && (item.PrintCount ?? 0) > 0);

                    if (item.PrintFolderStatus != PrintFolderResolutionStatus.Resolved || !item.PrintCount.HasValue)
                    {
                        issue = $"Chưa có thư mục in hợp lệ ({item.PrintFolderStatus}).";
                        if (lineIncluded)
                        {
                            blockingIssues.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {issue}");
                        }
                        else
                        {
                            warnings.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {issue} (Đã tạm bỏ chọn)");
                        }
                    }

                    PrintSpecification? spec = item.PrintSpecification;
                    if (spec == null && item.PrintSpecificationId.HasValue)
                    {
                        spec = await _specificationRepository.GetByIdAsync(item.PrintSpecificationId.Value, cancellationToken);
                    }

                    if (spec == null)
                    {
                        if (existingLine != null && existingLine.BilledUnitPrice > 0)
                        {
                            // Price has been manually set / overridden by operator
                        }
                        else
                        {
                            string unlinkedIssue = "Chưa được liên kết với bảng giá.";
                            issue = string.IsNullOrEmpty(issue) ? unlinkedIssue : $"{issue} {unlinkedIssue}";
                            if (lineIncluded)
                            {
                                blockingIssues.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {unlinkedIssue}");
                            }
                            else
                            {
                                warnings.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {unlinkedIssue} (Đã tạm bỏ chọn)");
                            }
                        }
                    }

                    int scannedQty = item.PrintCount ?? item.SourceCount;
                    int billedQty;
                    string? qtyOverrideReason = null;
                    int? sheetCount = null;
                    int? includedSheets = null;
                    int? extraSheets = null;
                    long? basePrice = null;
                    long? extraSheetPrice = null;
                    long configuredUnitPrice = 0;
                    long billedUnitPrice;
                    string? priceOverrideReason = null;
                    long lineTotal;

                    var billingMethod = spec?.BillingMethod ?? BillingMethod.FileCount;
                    var effectiveTier = order.Customer?.PriceTier ?? matchedCustomerForBill?.PriceTier ?? PriceTier.Retail;

                    if (billingMethod == BillingMethod.AlbumBasePlusExtra)
                    {
                        billedQty = 1;
                        sheetCount = scannedQty;
                        includedSheets = spec?.IncludedSheets ?? 10;
                        var (albumBase, albumExtra) = spec != null
                            ? spec.GetEffectiveAlbumPrices(effectiveTier)
                            : (0L, 0L);
                        basePrice = albumBase;
                        extraSheetPrice = albumExtra;
                        extraSheets = Math.Max(0, sheetCount.Value - includedSheets.Value);

                        if (sheetCount == 0)
                        {
                            issue = "Album không có tệp in nào (0 tệp).";
                            blockingIssues.Add($"[{order.OriginalFolderName}] {item.SpecificationFolderName}: {issue}");
                        }
                        else if (sheetCount < includedSheets)
                        {
                            warnings.Add($"[{order.OriginalFolderName}] Album '{item.SpecificationFolderName}' có {sheetCount} tờ (ít hơn số tờ chuẩn {includedSheets}).");
                        }

                        configuredUnitPrice = basePrice.Value + (extraSheets.Value * extraSheetPrice.Value);
                        billedUnitPrice = existingLine?.BilledUnitPrice ?? configuredUnitPrice;
                        if (existingLine != null && existingLine.BilledUnitPrice != configuredUnitPrice)
                        {
                            priceOverrideReason = existingLine.PriceOverrideReason ?? "Chỉnh sửa thủ công";
                        }

                        lineTotal = billedUnitPrice;
                    }
                    else
                    {
                        billedQty = existingLine?.BilledQuantity ?? item.BillQuantity ?? scannedQty;
                        if (existingLine != null && existingLine.BilledQuantity != scannedQty)
                        {
                            qtyOverrideReason = existingLine.QuantityOverrideReason ?? "Chỉnh sửa thủ công";
                        }

                        configuredUnitPrice = spec?.GetEffectiveUnitPrice(effectiveTier) ?? 0;
                        billedUnitPrice = existingLine?.BilledUnitPrice ?? configuredUnitPrice;
                        if (existingLine != null && existingLine.BilledUnitPrice != configuredUnitPrice)
                        {
                            priceOverrideReason = existingLine.PriceOverrideReason ?? "Chỉnh sửa thủ công";
                        }

                        lineTotal = billedQty * billedUnitPrice;
                    }

                    var billLine = new CustomerBillLine
                    {
                        Id = existingLine?.Id ?? 0,
                        OrderId = order.Id,
                        ProductJobId = item.Id,
                        ProductSpecificationId = spec?.Id,
                        SpecificationFolderName = item.SpecificationFolderName,
                        ProductNameSnapshot = spec?.CanonicalName ?? item.SpecificationFolderName,
                        VariantSnapshot = spec?.CanonicalSize,
                        BillingMethodSnapshot = billingMethod,
                        ScannedQuantity = scannedQty,
                        BilledQuantity = billedQty,
                        QuantityOverrideReason = qtyOverrideReason,
                        SheetCount = sheetCount,
                        IncludedSheetsSnapshot = includedSheets,
                        ExtraSheetCount = extraSheets,
                        BasePriceSnapshot = basePrice,
                        ExtraSheetPriceSnapshot = extraSheetPrice,
                        ConfiguredUnitPrice = configuredUnitPrice,
                        BilledUnitPrice = billedUnitPrice,
                        PriceOverrideReason = priceOverrideReason,
                        LineTotal = lineTotal,
                        IsIncluded = lineIncluded,
                        SortOrder = lineSortOrder,
                        FinalPrintFolderPath = item.SelectedPrintFolderRelativePath,
                        FolderResolutionModeSnapshot = item.FolderResolutionMode,
                        IssueMessage = issue,
                        Note = existingLine?.Note
                    };

                    currentOrderLines.Add(billLine);
                    billLines.Add(billLine);
                }

                long orderSubtotal = currentOrderLines.Where(l => l.IsIncluded).Sum(l => l.LineTotal);

                string resolvedOrderName = !string.IsNullOrWhiteSpace(order.OrderName) && !string.Equals(order.OrderName, "Đơn mặc định", StringComparison.OrdinalIgnoreCase)
                    ? order.OrderName
                    : order.OriginalFolderName;
                if (string.IsNullOrWhiteSpace(resolvedOrderName))
                {
                    resolvedOrderName = !string.IsNullOrWhiteSpace(discOrder.OriginalCustomerFolderName)
                        ? discOrder.OriginalCustomerFolderName
                        : (_fileSystem != null ? _fileSystem.GetFileName(sourceFolderPath) : System.IO.Path.GetFileName(sourceFolderPath.TrimEnd('\\', '/')));
                }
                if (string.IsNullOrWhiteSpace(resolvedOrderName)) resolvedOrderName = "Đơn hàng";

                string effectiveOrderName = !string.IsNullOrWhiteSpace(existingOrderSnapshot?.OrderNameSnapshot) && !string.Equals(existingOrderSnapshot.OrderNameSnapshot, "Đơn mặc định", StringComparison.OrdinalIgnoreCase)
                    ? existingOrderSnapshot.OrderNameSnapshot
                    : resolvedOrderName;

                var billOrder = new CustomerBillOrder
                {
                    Id = existingOrderSnapshot?.Id ?? 0,
                    OrderId = order.Id,
                    OrderCodeSnapshot = !string.IsNullOrWhiteSpace(order.OrderCode) ? order.OrderCode : existingOrderSnapshot?.OrderCodeSnapshot,
                    OrderNameSnapshot = effectiveOrderName,
                    OrderDateSnapshot = order.WorkDate,
                    OriginalFolderNameSnapshot = order.OriginalFolderName,
                    Subtotal = orderSubtotal,
                    SortOrder = orderSortOrder,
                    IsIncluded = orderIncluded,
                    IsFromPreviousPeriod = false,
                    SourceFolderPath = sourceFolderPath,
                    Lines = currentOrderLines
                };

                billOrders.Add(billOrder);
            }
        }

        // Adjustments
        var adjustments = new List<BillAdjustment>();
        if (existingAdjustments.Count > 0)
        {
            adjustments = existingAdjustments.ToList();
        }
        else
        {
            adjustments.Add(new BillAdjustment { Type = AdjustmentType.Shipping, Label = "Phí vận chuyển", Direction = AdjustmentDirection.Add, Amount = 0, SortOrder = 1 });
            adjustments.Add(new BillAdjustment { Type = AdjustmentType.Surcharge, Label = "Phụ thu", Direction = AdjustmentDirection.Add, Amount = 0, SortOrder = 2 });
            adjustments.Add(new BillAdjustment { Type = AdjustmentType.Discount, Label = "Giảm giá", Direction = AdjustmentDirection.Deduct, Amount = 0, SortOrder = 3 });
        }

        string billNumber = existingDraft?.BillNumber ?? await _customerBillRepository.GenerateNextBillNumberAsync(today, cancellationToken);

        // Check if guestName resolves to a customer if not already resolved from order
        if (matchedCustomerForBill == null && _customerResolver != null && !string.IsNullOrWhiteSpace(guestName))
        {
            var match = await _customerResolver.ResolveCustomerAsync(guestName, cancellationToken);
            if (match.Status == CustomerResolutionStatus.ExactMatch || match.Status == CustomerResolutionStatus.NormalizedMatch)
            {
                matchedCustomerForBill = match.ResolvedCustomer;
            }
        }

        var billType = existingDraft?.BillType ?? BillType.Guest;
        long? billCustomerId = existingDraft?.CustomerId;
        string billCustomerName = existingDraft?.CustomerNameSnapshot ?? guestName;
        string? billPhone = existingDraft?.PhoneSnapshot;

        var draft = new CustomerBill
        {
            Id = existingDraft?.Id ?? 0,
            BillNumber = billNumber,
            BillType = billType,
            CustomerId = billCustomerId,
            CustomerNameSnapshot = billCustomerName,
            PhoneSnapshot = billPhone,
            PeriodStart = today,
            PeriodEnd = today,
            Status = CustomerBillStatus.Draft,
            Note = existingDraft?.Note ?? string.Empty,
            SourceFolders = sourceFolderPaths.Select(p => new GuestBillSourceFolder
            {
                FolderPath = p,
                NormalizedFolderPath = PathNormalizer.NormalizePath(p)
            }).ToList(),
            Orders = billOrders,
            Lines = billLines,
            Adjustments = adjustments,
            CreatedAt = existingDraft?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        RecalculateTotals(draft);

        if (matchedCustomerForBill != null && billType == BillType.Guest)
        {
            warnings.Add($"Phát hiện khách hàng quen '{matchedCustomerForBill.CanonicalName}' có tên trùng với khách lẻ này. Bạn có thể bấm nút chuyển đổi nếu muốn gán vào khách quen.");
        }

        // Persist draft only if requested or if updating an existing persisted draft
        if (persistDraft || (existingDraftId.HasValue && existingDraftId.Value > 0))
        {
            await _customerBillRepository.SaveBillAsync(draft, cancellationToken);
        }

        return new CustomerBillDraftResult(draft, warnings, blockingIssues, matchedCustomerForBill);
    }

    public async Task<Customer> ConvertGuestBillToCustomerAsync(long billId, string customerCanonicalName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customerCanonicalName))
        {
            throw new ArgumentException("Tên khách hàng không được để trống.", nameof(customerCanonicalName));
        }

        var bill = await _customerBillRepository.GetByIdAsync(billId, cancellationToken);
        if (bill == null)
        {
            throw new KeyNotFoundException($"Không tìm thấy bill ID {billId}.");
        }

        if (bill.Status != CustomerBillStatus.Draft)
            throw new InvalidOperationException("Hãy mở lại hóa đơn trước khi đổi khách hàng.");

        string trimmedName = customerCanonicalName.Trim();
        string normalizedName = CustomerNormalizer.Normalize(trimmedName);

        var existingCustomer = await _customerRepository.FindExactMatchAsync(normalizedName, cancellationToken);
        Customer targetCustomer;

        if (existingCustomer != null)
        {
            targetCustomer = existingCustomer;
        }
        else
        {
            targetCustomer = new Customer
            {
                CanonicalName = trimmedName,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            targetCustomer = await _customerRepository.CreateCustomerAsync(targetCustomer, initialAlias: trimmedName, cancellationToken);
        }

        // Update bill properties
        bill.CustomerId = targetCustomer.Id;
        bill.BillType = BillType.Customer;
        bill.CustomerNameSnapshot = targetCustomer.CanonicalName;
        bill.PhoneSnapshot = targetCustomer.Phone;
        bill.UpdatedAt = DateTimeOffset.UtcNow;

        await _customerBillRepository.SaveBillAsync(bill, cancellationToken);

        // Update associated orders to link to this customer
        foreach (var order in bill.Orders)
        {
            if (order.OrderId > 0)
            {
                await _orderRepository.UpdateOrderCustomerIdAsync(order.OrderId, targetCustomer.Id, cancellationToken);
            }
        }

        _logger?.LogInformation("Converted Guest Bill {BillNumber} (ID {BillId}) to Customer '{CustomerName}' (ID {CustomerId})",
            bill.BillNumber, bill.Id, targetCustomer.CanonicalName, targetCustomer.Id);

        return targetCustomer;
    }

    private async Task<IReadOnlyList<Order>> GetOrdersForCustomerAsync(long customerId, CancellationToken cancellationToken)
    {
        return await _orderRepository.GetOrdersByCustomerIdAsync(customerId, cancellationToken);
    }

    private static bool IsItemBilled(OrderItemScan item)
    {
        return item.CustomerBillId.HasValue && item.CustomerBillId.Value > 0;
    }

    private static long EstimateItemTotal(OrderItemScan item, PriceTier tier = PriceTier.Retail)
    {
        if (item.PrintSpecification?.BillingMethod == BillingMethod.AlbumBasePlusExtra)
        {
            int sheets = item.PrintCount ?? item.SourceCount;
            int inc = item.PrintSpecification.IncludedSheets ?? 10;
            var (baseP, extraP) = item.PrintSpecification.GetEffectiveAlbumPrices(tier);
            int extraSheets = Math.Max(0, sheets - inc);
            return baseP + (extraSheets * extraP);
        }
        else
        {
            int qty = item.BillQuantity ?? item.PrintCount ?? item.SourceCount;
            long price = item.PrintSpecification?.GetEffectiveUnitPrice(tier) ?? 0;
            return qty * price;
        }
    }

    private static string GetNextDay(string dateString)
    {
        if (DateTime.TryParse(dateString, out var dt))
        {
            return dt.AddDays(1).ToString("yyyy-MM-dd");
        }
        return dateString;
    }
}
