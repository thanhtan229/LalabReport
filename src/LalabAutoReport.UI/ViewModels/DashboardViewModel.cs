using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.UI.Views;

namespace LalabAutoReport.UI.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IScanService _scanService;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly IFileSystemAdapter _fileSystem;
    private readonly ILockingService _lockingService;
    private readonly ICustomerBillingService? _customerBillingService;
    private readonly ICustomerBillRepository? _customerBillRepository;
    private readonly IJpegBillExporter? _jpegBillExporter;
    private readonly IExcelBillExporter? _excelBillExporter;
    private readonly ICustomerRepository? _customerRepository;
    private readonly IRootFolderRepository? _rootFolderRepository;
    private readonly IThumbnailService? _thumbnailService;
    private readonly IFolderStructureParser? _structureParser;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _thumbnailCts;

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private string _formattedDateString = DateTime.Today.ToString("yyyy-MM-dd");

    [ObservableProperty]
    private bool _showOrderThumbnails = true;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanProgressText = string.Empty;

    [ObservableProperty]
    private int _scanProgressPercent;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _currentFilter = "All"; // "All", "NeedsReview", "Ready"

    public record RootFolderFilterOption(long? Id, string Name);
    public ObservableCollection<RootFolderFilterOption> RootFolderFilterOptions { get; } = new();

    [ObservableProperty]
    private long? _selectedRootFolderId;

    partial void OnSelectedRootFolderIdChanged(long? value)
    {
        ApplyFilter();
    }

    [ObservableProperty]
    private ObservableCollection<OrderDisplayModel> _allOrders = new();

    [ObservableProperty]
    private ObservableCollection<OrderDisplayModel> _filteredOrders = new();

    [ObservableProperty]
    private int _totalOrders;

    [ObservableProperty]
    private int _totalCustomers;

    [ObservableProperty]
    private bool _hasOrders;

    [ObservableProperty]
    private int _needsReviewCount;

    [ObservableProperty]
    private int _readyCount;

    [ObservableProperty]
    private int _billedCount;

    [ObservableProperty]
    private int _deliveredCount;

    [ObservableProperty]
    private int _undeliveredCount;

    [ObservableProperty]
    private bool _isPreviewPopupOpen;

    [ObservableProperty]
    private OrderDisplayModel? _previewOrder;

    private readonly IAutoScanCoordinator? _autoScanCoordinator;
    private readonly IShippingLabelExporter? _shippingLabelExporter;
    private readonly IPrintStatusService? _printStatusService;
    private readonly ICloudSyncService? _cloudSyncService;

    public int LockedCount => BilledCount;

    public DashboardViewModel(
        IScanService scanService,
        ISettingsRepository settingsRepository,
        IOrderRepository orderRepository,
        IFileSystemAdapter fileSystem,
        ILockingService lockingService,
        ICustomerBillingService? customerBillingService = null,
        ICustomerBillRepository? customerBillRepository = null,
        IJpegBillExporter? jpegBillExporter = null,
        ICustomerRepository? customerRepository = null,
        IExcelBillExporter? excelBillExporter = null,
        IAutoScanCoordinator? autoScanCoordinator = null,
        IRootFolderRepository? rootFolderRepository = null,
        IThumbnailService? thumbnailService = null,
        IShippingLabelExporter? shippingLabelExporter = null,
        IPrintStatusService? printStatusService = null,
        ICloudSyncService? cloudSyncService = null,
        IFolderStructureParser? structureParser = null)
    {
        _scanService = scanService;
        _settingsRepository = settingsRepository;
        _orderRepository = orderRepository;
        _fileSystem = fileSystem;
        _lockingService = lockingService;
        _customerBillingService = customerBillingService;
        _customerBillRepository = customerBillRepository;
        _jpegBillExporter = jpegBillExporter;
        _excelBillExporter = excelBillExporter;
        _customerRepository = customerRepository;
        _autoScanCoordinator = autoScanCoordinator;
        _rootFolderRepository = rootFolderRepository;
        _thumbnailService = thumbnailService;
        _shippingLabelExporter = shippingLabelExporter;
        _printStatusService = printStatusService;
        _cloudSyncService = cloudSyncService;
        _structureParser = structureParser;

        if (_printStatusService != null)
        {
            _printStatusService.PrintStatusChanged += (path, status) =>
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    UpdateOrderPrintStateFromPath(path, status);
                });
            };
        }

        if (_autoScanCoordinator != null)
        {
            _autoScanCoordinator.StatusChanged += msg =>
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    StatusMessage = msg;
                });
            };

            _autoScanCoordinator.OrdersUpdated += count =>
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    _ = LoadOrdersForSelectedDateAsync();
                });
            };
        }

        SelectedDate = DateTime.Today;
        FormattedDateString = SelectedDate.ToString("yyyy-MM-dd");
    }

    private ICloudSyncService? ResolveCloudSyncService()
    {
        return _cloudSyncService ?? (Application.Current as App)?.Services?.GetService(typeof(ICloudSyncService)) as ICloudSyncService;
    }

    [RelayCommand]
    private async Task AssignCustomerAsync(OrderDisplayModel? orderDisplay)
    {
        if (orderDisplay == null) return;
        if (_customerRepository == null) return;

        var vm = new AssignCustomerViewModel(
            orderDisplay.OriginalFolderName,
            FormattedDateString,
            _customerRepository,
            _orderRepository);

        var window = new Views.AssignCustomerWindow(vm)
        {
            Owner = Application.Current?.MainWindow
        };

        if (window.ShowDialog() == true)
        {
            string folderName = orderDisplay.OriginalFolderName;

            // Update all matching orders on Dashboard
            foreach (var order in AllOrders.Where(o => string.Equals(o.OriginalFolderName, folderName, StringComparison.OrdinalIgnoreCase)))
            {
                // If this order was previously attached to a bill, detach it from the old bill
                if (_customerBillingService != null && order.Order.Id > 0 && order.IsBilled)
                {
                    await _customerBillingService.DetachOrderFromCustomerBillAsync(order.Order.Id);
                }

                if (vm.IsGuestAssigned)
                {
                    order.Order.CustomerId = null;
                    order.Order.Customer = null;
                    order.Order.OrderName = "Khách lẻ";
                }
                else if (vm.AssignedCustomer != null)
                {
                    order.Order.CustomerId = vm.AssignedCustomer.Id;
                    order.Order.Customer = vm.AssignedCustomer;
                    if (order.Order.OrderName == "Khách lẻ")
                    {
                        order.Order.OrderName = null;
                    }
                }

                bool itemsHaveIssues = order.Order.Items.Count == 0 ||
                    order.Order.Items.Any(i => i.PrintFolderStatus != PrintFolderResolutionStatus.Resolved ||
                                               i.ScanStatus == ScanStatus.Failed ||
                                               i.PrintSpecificationId == null);
                if (!itemsHaveIssues)
                {
                    order.Order.Status = OrderStatus.Ready;
                    order.Status = OrderStatus.Ready;
                    if (_orderRepository != null && order.Order.Id > 0)
                    {
                        _ = _orderRepository.UpdateOrderStatusAsync(order.Order.Id, OrderStatus.Ready);
                    }
                }
                else
                {
                    order.Order.Status = OrderStatus.NeedsReview;
                    order.Status = OrderStatus.NeedsReview;
                    if (_orderRepository != null && order.Order.Id > 0)
                    {
                        _ = _orderRepository.UpdateOrderStatusAsync(order.Order.Id, OrderStatus.NeedsReview);
                    }
                }

                order.NotifyTotalsChanged();
            }

            ApplyFilter();
            UpdateSummary();

            if (vm.IsGuestAssigned)
            {
                StatusMessage = $"Đã đặt thư mục '{folderName}' là Khách lẻ.";
            }
            else if (vm.AssignedCustomer != null)
            {
                StatusMessage = $"Đã gán thành công '{folderName}' cho khách hàng '{vm.AssignedCustomer.CanonicalName}'.";
            }
        }
    }




    partial void OnSelectedDateChanged(DateTime value)
    {
        _thumbnailCts?.Cancel();
        FormattedDateString = value.ToString("yyyy-MM-dd");
        _ = LoadOrdersForSelectedDateAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnCurrentFilterChanged(string value)
    {
        ApplyFilter();
    }

    public async Task LoadOrdersForSelectedDateAsync()
    {
        try
        {
            var settings = await _settingsRepository.GetSettingsAsync();
            ShowOrderThumbnails = settings.ShowOrderThumbnails;

            var dbOrders = await _orderRepository.GetOrdersByDateAsync(FormattedDateString);

            var exportedOrderIds = new HashSet<long>();
            var exportedBillIds = new HashSet<long>();
            var exportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_customerBillRepository != null)
            {
                try
                {
                    var dateBills = await _customerBillRepository.GetBillsByDateAsync(FormattedDateString);
                    foreach (var bill in dateBills)
                    {
                        bool isExported = bill.Status == CustomerBillStatus.Exported ||
                                          (!string.IsNullOrWhiteSpace(bill.ExportFilePath) && File.Exists(bill.ExportFilePath));
                        if (isExported)
                        {
                            exportedBillIds.Add(bill.Id);
                            foreach (var bo in bill.Orders)
                            {
                                exportedOrderIds.Add(bo.OrderId);
                                if (!string.IsNullOrWhiteSpace(bo.SourceFolderPath))
                                    exportedPaths.Add(bo.SourceFolderPath);
                            }
                            foreach (var sf in bill.SourceFolders)
                            {
                                if (!string.IsNullOrWhiteSpace(sf.FolderPath))
                                    exportedPaths.Add(sf.FolderPath);
                                if (!string.IsNullOrWhiteSpace(sf.NormalizedFolderPath))
                                    exportedPaths.Add(sf.NormalizedFolderPath);
                            }
                        }
                    }
                }
                catch
                {
                    // Non-fatal if bill query encounters error
                }
            }

            AllOrders.Clear();
            foreach (var order in dbOrders)
            {
                if (order.IsGuest && order.Status == OrderStatus.NeedsReview && order.Items.Count > 0)
                {
                    bool itemsHaveIssues = order.Items.Any(i => i.PrintFolderStatus != PrintFolderResolutionStatus.Resolved ||
                                                                i.ScanStatus == ScanStatus.Failed ||
                                                                i.PrintSpecificationId == null);
                    if (!itemsHaveIssues)
                    {
                        order.Status = OrderStatus.Ready;
                        _ = _orderRepository.UpdateOrderStatusAsync(order.Id, OrderStatus.Ready);
                    }
                }

                var display = new OrderDisplayModel(order);
                if (exportedOrderIds.Contains(order.Id) ||
                    order.Items.Any(i => i.CustomerBillId.HasValue && exportedBillIds.Contains(i.CustomerBillId.Value)) ||
                    (!string.IsNullOrWhiteSpace(order.RelativePath) && exportedPaths.Contains(order.RelativePath)) ||
                    (!string.IsNullOrWhiteSpace(order.OriginalFolderName) && exportedPaths.Contains(order.OriginalFolderName)))
                {
                    display.IsBillExported = true;
                }

                AllOrders.Add(display);
            }

            if (_rootFolderRepository != null)
            {
                var roots = await _rootFolderRepository.GetAllAsync();
                if (RootFolderFilterOptions.Count != roots.Count + 1)
                {
                    long? prevSelected = SelectedRootFolderId;
                    RootFolderFilterOptions.Clear();
                    RootFolderFilterOptions.Add(new RootFolderFilterOption(null, "📁 Tất cả các kho"));
                    foreach (var r in roots)
                    {
                        RootFolderFilterOptions.Add(new RootFolderFilterOption(r.Id, $"📁 {r.Name}"));
                    }
                    SelectedRootFolderId = prevSelected;
                }
            }

            ApplyFilter();
            UpdateSummary();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi tải dữ liệu: {ex.Message}";
        }
    }

    [RelayCommand]
    private void PreviousDay()
    {
        SelectedDate = SelectedDate.AddDays(-1);
    }

    [RelayCommand]
    private void NextDay()
    {
        SelectedDate = SelectedDate.AddDays(1);
    }

    [RelayCommand]
    private void Today()
    {
        SelectedDate = DateTime.Today;
    }

    [RelayCommand]
    private async Task ScanDateAsync()
    {
        if (IsScanning) return;

        var settings = await _settingsRepository.GetSettingsAsync();
        IReadOnlyList<RootFolder> activeRoots = Array.Empty<RootFolder>();
        if (_rootFolderRepository != null)
        {
            activeRoots = await _rootFolderRepository.GetActiveRootsAsync();
        }

        bool hasAccessibleRoot = false;
        string? matchedDateFolder = null;

        if (activeRoots.Count > 0)
        {
            foreach (var r in activeRoots)
            {
                if (!string.IsNullOrWhiteSpace(r.FullPath) && _fileSystem.DirectoryExists(r.FullPath))
                {
                    hasAccessibleRoot = true;
                    string? df = _structureParser != null
                        ? _structureParser.FindDateFolderPath(r.FullPath, FormattedDateString)
                        : (_fileSystem.DirectoryExists(_fileSystem.Combine(r.FullPath, FormattedDateString)) ? _fileSystem.Combine(r.FullPath, FormattedDateString) : null);

                    if (df != null && _fileSystem.DirectoryExists(df))
                    {
                        matchedDateFolder = df;
                        break;
                    }
                }
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(settings.RootFolder) && _fileSystem.DirectoryExists(settings.RootFolder))
            {
                hasAccessibleRoot = true;
                matchedDateFolder = _structureParser != null
                    ? _structureParser.FindDateFolderPath(settings.RootFolder, FormattedDateString)
                    : (_fileSystem.DirectoryExists(_fileSystem.Combine(settings.RootFolder, FormattedDateString)) ? _fileSystem.Combine(settings.RootFolder, FormattedDateString) : null);
            }
        }

        if (!hasAccessibleRoot)
        {
            StatusMessage = "Vui lòng chọn Thư Mục Gốc (Root Folder) trong phần Cài Đặt trước khi quét!";
            return;
        }

        if (matchedDateFolder == null)
        {
            StatusMessage = $"Không tìm thấy thư mục ngày: '{FormattedDateString}' trong bất kỳ kho nào đang hoạt động.";
            return;
        }

        IsScanning = true;
        ScanProgressText = "Đang chuẩn bị quét...";
        ScanProgressPercent = 0;
        StatusMessage = string.Empty;

        _scanCts = new CancellationTokenSource();

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressText = p.CurrentStep;
            ScanProgressPercent = p.TotalItems > 0 ? (int)((double)p.CompletedItems / p.TotalItems * 100) : 0;
        });

        _thumbnailService?.Pause();
        try
        {
            var scannedOrders = await _scanService.ScanDateAsync(FormattedDateString, progress, _scanCts.Token);
            await LoadOrdersForSelectedDateAsync();
            StatusMessage = $"Quét thành công ngày {FormattedDateString}: {scannedOrders.Count} đơn hàng.";

            // Trigger non-blocking cloud sync in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var syncService = ResolveCloudSyncService();
                    if (syncService != null)
                    {
                        await syncService.SyncDateAsync(FormattedDateString);
                    }
                }
                catch
                {
                    // Non-blocking fire-and-forget
                }
            });
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Đã hủy thao tác quét.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi quét: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ScanProgressText = string.Empty;
            _thumbnailService?.Resume();
        }
    }

    [RelayCommand]
    private async Task ScanMissingDaysAsync()
    {
        if (IsScanning) return;

        var settings = await _settingsRepository.GetSettingsAsync();
        IReadOnlyList<RootFolder> activeRoots = Array.Empty<RootFolder>();
        if (_rootFolderRepository != null)
        {
            activeRoots = await _rootFolderRepository.GetActiveRootsAsync();
        }

        bool hasAccessibleRoot = activeRoots.Any(r => !string.IsNullOrWhiteSpace(r.FullPath) && _fileSystem.DirectoryExists(r.FullPath))
            || (!string.IsNullOrWhiteSpace(settings.RootFolder) && _fileSystem.DirectoryExists(settings.RootFolder));

        if (!hasAccessibleRoot)
        {
            StatusMessage = "Vui lòng chọn Thư Mục Gốc trong Cài Đặt trước!";
            return;
        }

        IsScanning = true;
        ScanProgressText = $"Đang tìm các ngày chưa quét trong tháng {SelectedDate:MM/yyyy}...";
        ScanProgressPercent = 0;
        StatusMessage = string.Empty;

        _scanCts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressText = p.CurrentStep;
            ScanProgressPercent = p.TotalItems > 0 ? (int)((double)p.CompletedItems / p.TotalItems * 100) : 0;
        });

        _thumbnailService?.Pause();
        try
        {
            var scanned = await _scanService.ScanMissingDaysAsync(SelectedDate.Year, SelectedDate.Month, progress, _scanCts.Token);
            await LoadOrdersForSelectedDateAsync();
            StatusMessage = scanned.Count > 0
                ? $"Đã quét bổ sung {scanned.Count} đơn hàng từ các ngày còn thiếu."
                : $"Tất cả các ngày trong tháng {SelectedDate:MM/yyyy} đã được quét đầy đủ!";

            // Trigger non-blocking cloud sync in background
            _ = Task.Run(async () =>
            {
                try
                {
                    var syncService = ResolveCloudSyncService();
                    if (syncService != null)
                    {
                        await syncService.SyncDateAsync(FormattedDateString);
                    }
                }
                catch
                {
                    // Non-blocking fire-and-forget
                }
            });
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Đã hủy thao tác quét ngày thiếu.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi quét ngày thiếu: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ScanProgressText = string.Empty;
            _thumbnailService?.Resume();
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private async Task RescanOrderAsync(OrderDisplayModel? orderDisplay)
    {
        if (orderDisplay == null || IsScanning) return;

        _thumbnailService?.Pause();
        try
        {
            StatusMessage = $"Đang quét lại đơn hàng {orderDisplay.OriginalFolderName}...";
            var updated = await _scanService.ScanOrderInRootAsync(orderDisplay.RelativePath, orderDisplay.RootFolderId);
            if (updated != null)
            {
                int index = AllOrders.IndexOf(orderDisplay);
                var newDisplay = new OrderDisplayModel(updated);
                if (index >= 0)
                {
                    AllOrders[index] = newDisplay;
                }
                else
                {
                    AllOrders.Add(newDisplay);
                }
                ApplyFilter();
                UpdateSummary();
                StatusMessage = $"Đã quét lại thành công {orderDisplay.OriginalFolderName}.";

                if (updated.FilesystemChangedAfterLock && _customerBillRepository != null && _customerBillingService != null)
                {
                    CustomerBill? bill = await _customerBillRepository.GetLockedBillByOrderIdAsync(updated.Id);
                    if (bill == null && !string.IsNullOrWhiteSpace(updated.RelativePath))
                    {
                        var bills = await _customerBillRepository.GetBillsBySourceFolderPathAsync(updated.RelativePath);
                        bill = bills.FirstOrDefault();
                    }

                    if (bill != null)
                    {
                        var answer = MessageBox.Show(
                            $"Đơn hàng '{orderDisplay.FullOrderDisplayName}' đã có hóa đơn ({bill.BillNumber}), nhưng số lượng file trên đĩa đã thay đổi so với hóa đơn cũ.\n\nNếu hóa đơn đã thanh toán, trạng thái thanh toán sẽ bị hủy và cần ghi nhận lại.\n\nBạn có muốn cập nhật lại hóa đơn theo số lượng file mới không?",
                            "Phát hiện file thay đổi",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question
                        );

                        if (answer == MessageBoxResult.Yes)
                        {
                            await _customerBillingService.ReopenBillAsync(bill.Id, "Cập nhật theo file mới sau khi quét lại");
                            await _customerBillingService.SyncBillWithScannedOrderAsync(updated);
                            updated.FilesystemChangedAfterLock = false;
                            if (_orderRepository != null)
                            {
                                await _orderRepository.SetFilesystemChangedAfterLockAsync(updated.Id, false);
                            }
                            newDisplay.FilesystemChangedAfterLock = false;
                            ApplyFilter();
                            UpdateSummary();
                            StatusMessage = $"Đã cập nhật hóa đơn {bill.BillNumber} theo số lượng file mới.";
                        }
                        else
                        {
                            StatusMessage = $"Giữ nguyên hóa đơn {bill.BillNumber} như cũ.";
                        }
                    }
                }

                // Trigger non-blocking cloud sync in background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var syncService = ResolveCloudSyncService();
                        if (syncService != null)
                        {
                            await syncService.SyncDateAsync(FormattedDateString);
                        }
                    }
                    catch
                    {
                        // Non-blocking fire-and-forget
                    }
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi quét lại đơn: {ex.Message}";
        }
        finally
        {
            _thumbnailService?.Resume();
        }
    }

    [RelayCommand]
    private async Task DeleteOrderAsync(OrderDisplayModel? orderDisplay)
    {
        if (orderDisplay == null) return;

        if (!orderDisplay.CanDelete)
        {
            MessageBox.Show("Không thể xóa đơn hàng đã tính bill hoặc đã khóa hóa đơn.\n\nNếu muốn xóa, vui lòng mở khóa hóa đơn trước.", "Không Thể Xóa Đơn Hàng", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Bạn có chắc chắn muốn xóa đơn hàng sau khỏi danh sách?\n\n" +
            $"• Mã đơn: {orderDisplay.DisplayOrderCode}\n" +
            $"• Tên đơn: {orderDisplay.FullOrderDisplayName}\n" +
            $"• Ngày: {FormattedDateString}\n\n" +
            "LƯU Ý: Thao tác này chỉ xóa bản ghi dữ liệu trong phần mềm. Tệp ảnh và thư mục trên ổ đĩa của bạn hoàn toàn KHÔNG bị xóa.",
            "Xác nhận xóa đơn hàng",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _orderRepository.DeleteOrderAsync(orderDisplay.Id);
            AllOrders.Remove(orderDisplay);
            ApplyFilter();
            UpdateSummary();
            StatusMessage = $"Đã xóa đơn hàng {orderDisplay.DisplayOrderCode} ({orderDisplay.FullOrderDisplayName}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi xóa đơn hàng: {ex.Message}";
            MessageBox.Show($"Không thể xóa đơn hàng: {ex.Message}", "Lỗi Xóa Đơn Hàng", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ComputeOrViewBillAsync(OrderDisplayModel? orderDisplay)
    {
        if (orderDisplay == null || IsScanning) return;
        if (_customerBillingService == null || _customerBillRepository == null || _jpegBillExporter == null) return;

        await ViewOrComputeBillForOrderAsync(orderDisplay);
    }

    private async Task ViewOrComputeBillForOrderAsync(OrderDisplayModel orderDisplay)
    {
        try
        {
            StatusMessage = $"Đang mở hóa đơn cho {orderDisplay.FullOrderDisplayName}...";

            CustomerBill? bill = await _customerBillRepository!.GetBillByOrderIdAsync(orderDisplay.Id);

            if (bill == null && !string.IsNullOrWhiteSpace(orderDisplay.RelativePath))
            {
                var billsByPath = await _customerBillRepository.GetBillsBySourceFolderPathAsync(orderDisplay.RelativePath);
                bill = billsByPath.FirstOrDefault();
            }

            if (bill == null && orderDisplay.Order.CustomerId.HasValue)
            {
                bill = await _customerBillRepository.GetActiveDraftByCustomerIdAsync(orderDisplay.Order.CustomerId.Value);
            }

            if (bill != null)
            {
                if (orderDisplay.FilesystemChangedAfterLock && (bill.Status == CustomerBillStatus.Locked || bill.Status == CustomerBillStatus.Exported))
                {
                    var answer = MessageBox.Show(
                        $"Hóa đơn ({bill.BillNumber}) của đơn hàng '{orderDisplay.FullOrderDisplayName}' đã được tính/xuất trước đó, nhưng số lượng file trên đĩa đã thay đổi so với hóa đơn cũ.\n\nNếu hóa đơn đã thanh toán, trạng thái thanh toán sẽ bị hủy và cần ghi nhận lại.\n\nBạn có muốn cập nhật lại hóa đơn theo số lượng file mới không?",
                        "Phát hiện file thay đổi",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question
                    );

                    if (answer == MessageBoxResult.Yes)
                    {
                        await _customerBillingService!.ReopenBillAsync(bill.Id, "Cập nhật theo file mới từ giao diện xem bill");
                        await _customerBillingService.SyncBillWithScannedOrderAsync(orderDisplay.Order);
                        orderDisplay.FilesystemChangedAfterLock = false;
                        orderDisplay.Order.FilesystemChangedAfterLock = false;
                        if (_orderRepository != null)
                        {
                            await _orderRepository.SetFilesystemChangedAfterLockAsync(orderDisplay.Id, false);
                        }
                        bill = await _customerBillRepository.GetByIdAsync(bill.Id) ?? bill;
                        StatusMessage = $"Đã cập nhật hóa đơn {bill.BillNumber} theo số lượng file mới.";
                    }
                    else
                    {
                        StatusMessage = $"Giữ nguyên hóa đơn {bill.BillNumber} như cũ.";
                    }
                }

                var vm = new CustomerBillReviewViewModel(
                    bill,
                    _customerBillingService!,
                    _jpegBillExporter!,
                    _excelBillExporter,
                    settingsRepository: _settingsRepository,
                    customerBillRepository: _customerBillRepository,
                    shippingLabelExporter: _shippingLabelExporter
                );

                var win = new CustomerBillReviewWindow(vm)
                {
                    Owner = Application.Current?.MainWindow
                };

                win.ShowDialog();

                await LoadOrdersForSelectedDateAsync();
                StatusMessage = $"Đã xem xong hóa đơn {bill.BillNumber}.";
                return;
            }

            // Nếu chưa có bill lưu trong DB, tự động tạo mới/chuẩn bị draft
            await ComputeBillForOrderAsync(orderDisplay);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi mở hóa đơn: {ex.Message}";
            MessageBox.Show($"Lỗi khi mở hóa đơn: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ComputeBillForOrderAsync(OrderDisplayModel orderDisplay)
    {
        try
        {
            // 1. Tính bill cho Khách quen
            if (orderDisplay.HasCustomer && orderDisplay.Order.CustomerId.HasValue)
            {
                long customerId = orderDisplay.Order.CustomerId.Value;
                StatusMessage = $"Đang chuẩn bị hóa đơn cho {orderDisplay.CanonicalCustomerName}...";

                var result = await _customerBillingService!.BuildOrRefreshDraftAsync(customerId, forceRescan: false);

                var vm = new CustomerBillReviewViewModel(
                    result.Draft,
                    _customerBillingService,
                    _jpegBillExporter!,
                    _excelBillExporter,
                    result.Warnings,
                    result.BlockingIssues,
                    _settingsRepository,
                    result.SuggestedCustomer
                );

                var win = new CustomerBillReviewWindow(vm)
                {
                    Owner = Application.Current?.MainWindow
                };

                win.ShowDialog();

                await LoadOrdersForSelectedDateAsync();
                return;
            }

            // 3. Tính bill cho Khách lẻ
            if (orderDisplay.IsGuest || !orderDisplay.HasCustomer)
            {
                string rootDir = await ResolveOrderRootDirAsync(orderDisplay);
                string fullFolderPath = _fileSystem.Combine(rootDir, orderDisplay.RelativePath);

                if (!_fileSystem.DirectoryExists(fullFolderPath))
                {
                    MessageBox.Show(
                        $"Thư mục ảnh không tồn tại trên ổ đĩa:\n\n{fullFolderPath}\n\n" +
                        "💡 Lưu ý: Có thể thư mục ảnh đã được xưởng dọn dẹp để giải phóng dung lượng ổ cứng, hoặc ổ đĩa chứa kho này chưa được kết nối.\n\n" +
                        "Toàn bộ thông tin đơn hàng và hóa đơn lưu trên ứng dụng vẫn được BẢO TOÀN NGUYÊN VẸN 100%.",
                        "Thư Mục Không Tồn Tại Trên Ổ Đĩa",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    return;
                }

                string guestName;
                if (orderDisplay.Kind == OrderKind.Explicit && !string.IsNullOrWhiteSpace(orderDisplay.OrderName))
                {
                    guestName = orderDisplay.OrderName;
                }
                else if (!string.IsNullOrWhiteSpace(orderDisplay.OriginalFolderName) &&
                         !string.Equals(orderDisplay.OriginalFolderName, "KHACH_LE", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(orderDisplay.OriginalFolderName, "khách lẻ", StringComparison.OrdinalIgnoreCase))
                {
                    guestName = orderDisplay.OriginalFolderName;
                }
                else
                {
                    guestName = "Khách lẻ";
                }
                StatusMessage = $"Đang chuẩn bị hóa đơn khách lẻ cho {guestName}...";

                var result = await _customerBillingService!.BuildGuestBillDraftAsync(new[] { fullFolderPath }, guestName);

                var vm = new CustomerBillReviewViewModel(
                    result.Draft,
                    _customerBillingService,
                    _jpegBillExporter!,
                    _excelBillExporter,
                    result.Warnings,
                    result.BlockingIssues,
                    _settingsRepository,
                    result.SuggestedCustomer
                );

                var win = new CustomerBillReviewWindow(vm)
                {
                    Owner = Application.Current?.MainWindow
                };

                win.ShowDialog();

                await LoadOrdersForSelectedDateAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi tính bill: {ex.Message}";
            MessageBox.Show($"Lỗi tính bill: {ex.Message}", "Lỗi Tính Bill", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RescanSpecificationAsync(OrderItemDisplayModel? itemDisplay)
    {
        if (itemDisplay == null || IsScanning) return;

        try
        {
            StatusMessage = $"Đang quét lại quy cách {itemDisplay.SpecName}...";
            var updated = await _scanService.ScanSpecificationInRootAsync(itemDisplay.SpecRelativePath, itemDisplay.ParentOrder.RootFolderId);
            if (updated != null)
            {
                itemDisplay.PrintCount = updated.PrintCount;
                itemDisplay.MismatchCount = updated.MismatchCount;
                itemDisplay.PrintFolderStatus = updated.PrintFolderStatus;
                itemDisplay.SelectedPrintFolderRelativePath = updated.SelectedPrintFolderRelativePath;
                itemDisplay.BillQuantity = updated.BillQuantity;
                itemDisplay.ResolutionMode = updated.QuantityResolutionMode;
                itemDisplay.ParentOrder.NotifyTotalsChanged();
                UpdateSummary();
                StatusMessage = $"Đã quét lại quy cách {itemDisplay.SpecName}.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi quét lại quy cách: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ResolveQuantityAsync(object? param)
    {
        if (param is ValueTuple<OrderItemDisplayModel, QuantityResolutionMode, int, string?> tuple)
        {
            var (item, mode, quantity, note) = tuple;
            await _orderRepository.UpdateOrderItemResolutionAsync(item.Id, quantity, mode, note);
            item.UpdateResolution(quantity, mode, note);
            UpdateSummary();
        }
    }

    [RelayCommand]
    private async Task SelectCandidatePrintFolderAsync(object? param)
    {
        if (param is (OrderItemDisplayModel item, string candidateRelativePath))
        {
            var settings = await _settingsRepository.GetSettingsAsync();
            string fullPath = _fileSystem.Combine(await ResolveOrderRootDirAsync(item.ParentOrder), candidateRelativePath);
            var supported = new HashSet<string>(settings.SupportedExtensions, StringComparer.OrdinalIgnoreCase);

            int count = 0;
            if (_fileSystem.DirectoryExists(fullPath))
            {
                foreach (var file in _fileSystem.EnumerateFiles(fullPath))
                {
                    if (supported.Contains(_fileSystem.GetExtension(file)))
                        count++;
                }
            }

            await _orderRepository.UpdateOrderItemPrintFolderAsync(item.Id, candidateRelativePath, count);
            item.UpdatePrintFolder(candidateRelativePath, count);
            UpdateSummary();
        }
    }

    private async Task<string> ResolveOrderRootDirAsync(OrderDisplayModel? order)
    {
        var settings = await _settingsRepository.GetSettingsAsync();
        string rootDir = settings.RootFolder;
        if (order?.RootFolderId.HasValue == true)
        {
            var root = _rootFolderRepository == null ? null : await _rootFolderRepository.GetByIdAsync(order.RootFolderId.Value);
            if (root == null || string.IsNullOrWhiteSpace(root.FullPath))
                throw new InvalidOperationException($"Không tìm thấy kho #{order.RootFolderId}; không dùng kho khác thay thế.");
            rootDir = root.FullPath;
        }
        return rootDir;
    }

    private void TryOpenDirectory(string fullPath, string entityDescription)
    {
        if (_fileSystem.DirectoryExists(fullPath))
        {
            _fileSystem.OpenDirectoryInShell(fullPath);
        }
        else
        {
            MessageBox.Show(
                $"{entityDescription} không tồn tại trên ổ đĩa:\n\n{fullPath}\n\n" +
                "💡 Lưu ý: Có thể thư mục ảnh đã được xưởng dọn dẹp để giải phóng dung lượng ổ cứng, hoặc ổ đĩa chứa kho này chưa được kết nối.\n\n" +
                "Toàn bộ thông tin đơn hàng, số lượng và hóa đơn lưu trên ứng dụng vẫn được BẢO TOÀN NGUYÊN VẸN 100%.",
                "Thư Mục Không Tồn Tại Trên Ổ Đĩa",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
    }

    [RelayCommand]
    private async Task OpenDateFolderAsync()
    {
        var settings = await _settingsRepository.GetSettingsAsync();
        if (_rootFolderRepository != null)
        {
            var activeRoots = await _rootFolderRepository.GetActiveRootsAsync();
            foreach (var root in activeRoots)
            {
                string? path = _structureParser != null
                    ? _structureParser.FindDateFolderPath(root.FullPath, FormattedDateString)
                    : _fileSystem.Combine(root.FullPath, FormattedDateString);

                if (path != null && _fileSystem.DirectoryExists(path))
                {
                    _fileSystem.OpenDirectoryInShell(path);
                    return;
                }
            }
        }

        string fallbackPath = (_structureParser != null ? _structureParser.FindDateFolderPath(settings.RootFolder, FormattedDateString) : null)
                              ?? _fileSystem.Combine(settings.RootFolder, FormattedDateString);

        if (_fileSystem.DirectoryExists(fallbackPath))
        {
            _fileSystem.OpenDirectoryInShell(fallbackPath);
            return;
        }

        TryOpenDirectory(fallbackPath, $"Thư mục ngày '{FormattedDateString}'");
    }

    [RelayCommand]
    private async Task OpenOrderFolderAsync(OrderDisplayModel? order)
    {
        if (order == null) return;
        string rootDir = await ResolveOrderRootDirAsync(order);
        string fullPath = _fileSystem.Combine(rootDir, order.RelativePath);
        TryOpenDirectory(fullPath, $"Thư mục đơn hàng '{order.OriginalFolderName}'");
    }

    [RelayCommand]
    private async Task OpenSourceFolderAsync(OrderItemDisplayModel? item)
    {
        if (item == null) return;
        var order = AllOrders.FirstOrDefault(o => o.Order.Id == item.Item.OrderId);
        string rootDir = await ResolveOrderRootDirAsync(order);
        string fullPath = _fileSystem.Combine(rootDir, item.SpecRelativePath);
        TryOpenDirectory(fullPath, $"Thư mục ảnh gốc '{item.SpecName}'");
    }

    [RelayCommand]
    private async Task OpenPrintFolderAsync(OrderItemDisplayModel? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.SelectedPrintFolderRelativePath)) return;
        var order = AllOrders.FirstOrDefault(o => o.Order.Id == item.Item.OrderId);
        string rootDir = await ResolveOrderRootDirAsync(order);
        string fullPath = _fileSystem.Combine(rootDir, item.SelectedPrintFolderRelativePath);
        TryOpenDirectory(fullPath, $"Thư mục ảnh in '{item.SpecName}'");
    }

    private void ApplyFilter()
    {
        var filtered = AllOrders.AsEnumerable();

        if (SelectedRootFolderId.HasValue)
        {
            filtered = filtered.Where(o => o.RootFolderId == SelectedRootFolderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string s = SearchText.Trim().ToLowerInvariant();
            filtered = filtered.Where(o =>
                o.OriginalFolderName.ToLowerInvariant().Contains(s) ||
                (!string.IsNullOrWhiteSpace(o.OrderCode) && o.OrderCode.ToLowerInvariant().Contains(s)) ||
                (!string.IsNullOrWhiteSpace(o.OrderName) && o.OrderName.ToLowerInvariant().Contains(s)) ||
                (o.CanonicalCustomerName != null && o.CanonicalCustomerName.ToLowerInvariant().Contains(s)) ||
                (!string.IsNullOrWhiteSpace(o.Note) && o.Note.ToLowerInvariant().Contains(s)) ||
                o.Items.Any(i => i.SpecName.ToLowerInvariant().Contains(s)));
        }

        if (CurrentFilter == "NeedsReview")
        {
            filtered = filtered.Where(o => o.HasIssues);
        }
        else if (CurrentFilter == "Ready")
        {
            filtered = filtered.Where(o => o.Status == OrderStatus.Ready);
        }
        else if (CurrentFilter == "Billed" || CurrentFilter == "Locked")
        {
            filtered = filtered.Where(o => o.Status == OrderStatus.Locked || o.Status == OrderStatus.Billed || o.IsBilled);
        }
        else if (CurrentFilter == "Regular")
        {
            filtered = filtered.Where(o => o.HasCustomer);
        }
        else if (CurrentFilter == "Guest")
        {
            filtered = filtered.Where(o => o.IsGuest);
        }
        else if (CurrentFilter == "Delivered")
        {
            filtered = filtered.Where(o => o.IsDelivered);
        }
        else if (CurrentFilter == "Undelivered")
        {
            filtered = filtered.Where(o => !o.IsDelivered);
        }

        FilteredOrders.Clear();
        foreach (var order in filtered)
        {
            FilteredOrders.Add(order);
        }

        HasOrders = FilteredOrders.Count > 0;
        TriggerThumbnailLoadingForVisibleOrders();
    }

    public void TriggerThumbnailLoadingForVisibleOrders()
    {
        if (_thumbnailService == null || !ShowOrderThumbnails) return;

        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        var ordersToLoad = FilteredOrders.ToList();
        if (ordersToLoad.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = await _settingsRepository.GetSettingsAsync(ct);
                string? defaultRoot = settings.RootFolder;

                foreach (var order in ordersToLoad)
                {
                    if (ct.IsCancellationRequested) break;

                    string root;
                    if (order.RootFolderId.HasValue)
                    {
                        var rf = _rootFolderRepository == null ? null : await _rootFolderRepository.GetByIdAsync(order.RootFolderId.Value, ct);
                        root = (rf != null && !string.IsNullOrWhiteSpace(rf.FullPath)) ? rf.FullPath : string.Empty;
                    }
                    else
                    {
                        root = defaultRoot ?? string.Empty;
                    }

                    if (!string.IsNullOrWhiteSpace(root))
                    {
                        await order.LoadThumbnailAsync(_thumbnailService, root, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal when scrolling or changing view
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Thumbnail loading error: {ex.Message}");
            }
        }, ct);
    }

    private void UpdateSummary()
    {
        TotalOrders = AllOrders.Count;
        TotalCustomers = AllOrders.Select(o => o.CanonicalCustomerName ?? o.OriginalFolderName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        NeedsReviewCount = AllOrders.Count(o => o.HasIssues);
        ReadyCount = AllOrders.Count(o => o.Status == OrderStatus.Ready && !o.HasIssues);
        BilledCount = AllOrders.Count(o => o.Status == OrderStatus.Locked || o.Status == OrderStatus.Billed || o.IsBilled);
        DeliveredCount = AllOrders.Count(o => o.IsDelivered);
        UndeliveredCount = TotalOrders - DeliveredCount;
    }

    private void UpdateOrderPrintStateFromPath(string path, PrintStatus status)
    {
        string norm = PathNormalizer.Normalize(path);
        foreach (var order in AllOrders)
        {
            string orderNorm = PathNormalizer.Normalize(order.Order.RelativePath);
            if (norm.EndsWith(orderNorm, StringComparison.OrdinalIgnoreCase))
            {
                order.PrintProgress = status;
                order.IsPrinted = status == PrintStatus.Printed;
                order.NotifyPrintProgressChanged();
            }

            foreach (var item in order.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Item.SpecificationRelativePath))
                {
                    string itemNorm = PathNormalizer.Normalize(item.Item.SpecificationRelativePath);
                    if (norm.EndsWith(itemNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        item.IsPrinted = status == PrintStatus.Printed;
                        order.NotifyPrintProgressChanged();
                    }
                }
            }
        }
    }

    [RelayCommand]
    private async Task ToggleDeliveredAsync(OrderDisplayModel? order)
    {
        if (order == null) return;

        bool newStatus = !order.IsDelivered;

        if (newStatus && order.Items.Count > 1 && order.PrintedItemsCount < order.TotalItemsCount)
        {
            var confirm = MessageBox.Show(
                $"Đơn hàng {order.DisplayOrderCode} mới in xong {order.PrintedItemsCount}/{order.TotalItemsCount} sản phẩm (còn {order.TotalItemsCount - order.PrintedItemsCount} sản phẩm chưa đánh dấu ĐÃ IN).\n\nBạn có chắc chắn muốn chuyển sang trạng thái ĐÃ GIAO không?",
                "Xác nhận giao hàng",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        DateTimeOffset? deliveredAt = newStatus ? DateTimeOffset.Now : null;
        string? deliveredBy = null;

        await _orderRepository.UpdateOrderDeliveredStatusAsync(order.Id, newStatus, deliveredAt, deliveredBy);
        order.HasPendingCloudChanges = (await _orderRepository.GetOrderByIdAsync(order.Id))?.HasPendingCloudChanges == true;
        order.IsDelivered = newStatus;
        order.DeliveredAt = deliveredAt;
        order.DeliveredBy = deliveredBy;

        UpdateSummary();
        ApplyFilter();
        StatusMessage = newStatus 
            ? $"Đã đánh dấu ĐÃ GIAO đơn hàng {order.DisplayOrderCode} ({deliveredAt:dd/MM/yyyy HH:mm})."
            : $"Đã chuyển về Chưa giao đơn hàng {order.DisplayOrderCode}.";
    }

    [RelayCommand]
    private async Task ToggleItemPrintedAsync(OrderItemDisplayModel? item)
    {
        if (item == null) return;

        try
        {
            var printService = _printStatusService ?? (Application.Current as App)?.Services?.GetService(typeof(IPrintStatusService)) as IPrintStatusService;
            if (printService == null) return;

            string rootFolder = string.Empty;
            if (_settingsRepository != null)
            {
                var settings = await _settingsRepository.GetSettingsAsync();
                rootFolder = await ResolveOrderRootDirAsync(item.ParentOrder);
            }

            string subPath = !string.IsNullOrWhiteSpace(rootFolder) && !string.IsNullOrWhiteSpace(item.Item.SpecificationRelativePath)
                ? Path.Combine(rootFolder, item.Item.SpecificationRelativePath)
                : string.Empty;

            if (string.IsNullOrWhiteSpace(subPath) || !Directory.Exists(subPath))
            {
                item.IsPrinted = !item.IsPrinted;
                return;
            }

            var toggleResult = await printService.ToggleStatusAsync(subPath, "DashboardUI");
            item.IsPrinted = toggleResult.IsPrinted;
            item.PrintedAt = item.IsPrinted ? DateTimeOffset.Now : null;

            if (item.ParentOrder != null)
            {
                item.ParentOrder.PrintProgress = toggleResult.NewStatus == PrintStatus.Printed
                    ? (toggleResult.PrintedSubCount >= toggleResult.TotalSubCount ? PrintStatus.Printed : PrintStatus.Partial)
                    : (toggleResult.PrintedSubCount > 0 ? PrintStatus.Partial : PrintStatus.NotPrinted);

                item.ParentOrder.IsPrinted = item.ParentOrder.PrintedItemsCount >= item.ParentOrder.TotalItemsCount && item.ParentOrder.TotalItemsCount > 0;
                item.ParentOrder.NotifyPrintProgressChanged();
            }

            StatusMessage = item.IsPrinted
                ? $"Đã đánh dấu ĐÃ IN quy cách '{item.SpecName}' ({item.ParentOrder?.DisplayOrderCode})."
                : $"Đã hủy đánh dấu ĐÃ IN quy cách '{item.SpecName}' ({item.ParentOrder?.DisplayOrderCode}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khi cập nhật trạng thái in: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"Lỗi khi toggle in cho sản phẩm: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PrintOrderLabelAsync(OrderDisplayModel? order)
    {
        if (order == null) return;
        var exporter = _shippingLabelExporter ?? (Application.Current as App)?.Services?.GetService(typeof(IShippingLabelExporter)) as IShippingLabelExporter;
        if (exporter == null)
        {
            MessageBox.Show("Dịch vụ in tem nhiệt chưa sẵn sàng.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            CustomerBill? bill = null;
            long? billId = order.Order.Items?.FirstOrDefault(i => i.CustomerBillId != null)?.CustomerBillId;
            if (billId.HasValue && _customerBillRepository != null)
            {
                bill = await _customerBillRepository.GetByIdAsync(billId.Value);
            }

            var previewWindow = new Views.ShippingLabelPreviewWindow(
                order.Order,
                bill,
                exporter,
                _settingsRepository,
                _orderRepository
            );

            var mainWindow = Application.Current?.MainWindow;
            if (mainWindow != null && mainWindow.IsVisible)
            {
                previewWindow.Owner = mainWindow;
            }

            bool? dialogResult = previewWindow.ShowDialog();

            if (dialogResult == true && previewWindow.WasPrinted)
            {
                if (previewWindow.WasMarkedDelivered)
                {
                    order.IsDelivered = true;
                    order.DeliveredAt = previewWindow.DeliveredAt;
                    order.DeliveredBy = previewWindow.DeliveredBy;
                    UpdateSummary();
                    ApplyFilter();
                    StatusMessage = $"✅ Đã in tem 75x100mm và đánh dấu ĐÃ GIAO cho đơn {order.DisplayOrderCode}.";
                }
                else
                {
                    StatusMessage = $"✅ Đã in tem 75x100mm cho đơn {order.DisplayOrderCode}.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi khi mở tem: {ex.Message}";
            MessageBox.Show($"Lỗi khi mở tem đóng gói: {ex.Message}", "Lỗi Xem Tem", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task TogglePrintedAsync(OrderDisplayModel? order)
    {
        if (order == null) return;
        bool newPrinted = !order.IsPrinted;
        DateTimeOffset? printedAt = newPrinted ? DateTimeOffset.Now : null;

        await _orderRepository.UpdateOrderPrintedStatusAsync(order.Id, newPrinted, printedAt);
        order.IsPrinted = newPrinted;
        order.Order.PrintedAt = printedAt;
        StatusMessage = newPrinted 
            ? $"Đã đánh dấu ĐÃ IN đơn hàng {order.DisplayOrderCode}."
            : $"Đã bỏ đánh dấu ĐÃ IN đơn hàng {order.DisplayOrderCode}.";
    }

    [RelayCommand]
    private async Task EditOrderNoteAsync(OrderDisplayModel? order)
    {
        if (order == null) return;

        var window = Application.Current?.MainWindow;
        string? result = Views.InputDialog.Show(
            window!,
            "Ghi chú đơn hàng",
            $"Nhập ghi chú cho đơn hàng {order.DisplayOrderCode} ({order.FullOrderDisplayName}):\n(Để trống nếu muốn xóa ghi chú)",
            order.Note ?? string.Empty);

        if (result == null) return; // User cancelled

        string? cleanNote = string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        await _orderRepository.UpdateOrderNoteAsync(order.Id, cleanNote);
        order.Note = cleanNote;
        StatusMessage = cleanNote != null
            ? $"Đã lưu ghi chú cho đơn hàng {order.DisplayOrderCode}."
            : $"Đã xóa ghi chú cho đơn hàng {order.DisplayOrderCode}.";
    }

    [RelayCommand]
    private void OpenPreviewPopup(OrderDisplayModel? order)
    {
        if (order == null) return;
        PreviewOrder = order;
        IsPreviewPopupOpen = true;
    }

    [RelayCommand]
    private void ClosePreviewPopup()
    {
        IsPreviewPopupOpen = false;
        PreviewOrder = null;
    }

    [RelayCommand]
    private void OpenPreviewOriginalImage()
    {
        if (PreviewOrder == null) return;
        string? targetPath = PreviewOrder.FullThumbnailCandidatePath;
        if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không thể mở tệp ảnh gốc: {ex.Message}", "Lỗi Mở Ảnh", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show("Tệp ảnh gốc không còn tồn tại trên đĩa hoặc đã bị di chuyển.", "Không Tìm Thấy Ảnh", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void OpenPreviewContainingFolder()
    {
        if (PreviewOrder == null) return;
        string? targetPath = PreviewOrder.FullThumbnailCandidatePath;
        if (!string.IsNullOrEmpty(targetPath))
        {
            if (File.Exists(targetPath))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Không thể mở thư mục: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            // Fallback: If specific file doesn't exist, try opening the parent folder
            string? dir = System.IO.Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Không thể mở thư mục: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
        }

        MessageBox.Show("Thư mục hoặc tệp ảnh không tồn tại trên ổ đĩa.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
