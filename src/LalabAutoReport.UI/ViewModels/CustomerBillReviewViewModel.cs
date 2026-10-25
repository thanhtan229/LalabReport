using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Reporting;
using LalabAutoReport.UI.Views;

namespace LalabAutoReport.UI.ViewModels;

public partial class CustomerBillReviewViewModel : ObservableObject
{
    private readonly ICustomerBillingService _customerBillingService;
    private readonly IJpegBillExporter _jpegBillExporter;
    private readonly IBillVisualRenderer? _billVisualRenderer;
    private readonly IExcelBillExporter? _excelBillExporter;
    private readonly ISettingsRepository? _settingsRepository;
    private readonly ICustomerBillRepository? _customerBillRepository;
    private readonly IShippingLabelExporter? _shippingLabelExporter;
    private readonly ICloudSyncService? _cloudSyncService;

    [ObservableProperty]
    private CustomerBill _bill;

    [ObservableProperty]
    private string _windowTitle = "Duyệt & Xuất Hóa Đơn Khách Hàng";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentStatusText))]
    [NotifyPropertyChangedFor(nameof(PaymentStatusButtonText))]
    [NotifyPropertyChangedFor(nameof(CanSplitOrders))]
    private bool _isPaid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentStatusText))]
    private DateTimeOffset? _paidAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentStatusText))]
    private bool _hasPendingCloudChanges;

    public string PaymentStatusText => (IsPaid
        ? $"ĐÃ THANH TOÁN{(PaidAt.HasValue ? " (" + PaidAt.Value.ToString("dd/MM/yyyy HH:mm") + ")" : "")}"
        : "CHƯA THANH TOÁN") + (HasPendingCloudChanges ? " · Chờ Cloud xác nhận" : "");

    public string PaymentStatusButtonText => IsPaid
        ? "Đánh dấu Chưa thanh toán"
        : "Đánh dấu ĐÃ THANH TOÁN";

    [ObservableProperty]
    private long _productSubtotal;

    [ObservableProperty]
    private long _adjustmentsTotal;

    [ObservableProperty]
    private long _grandTotal;

    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private string _shippingAddress = string.Empty;

    partial void OnShippingAddressChanged(string value)
    {
        if (Bill != null && !IsLocked)
        {
            Bill.ShippingAddressSnapshot = value;
        }
        OnDataChanged();
    }

    [ObservableProperty]
    private bool _hasWarnings;

    [ObservableProperty]
    private bool _hasBlockingIssues;

    [ObservableProperty]
    private bool _isExportSuccess;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanViewExportedBill))]
    [NotifyPropertyChangedFor(nameof(IsBillExported))]
    [NotifyPropertyChangedFor(nameof(IsOpenOnlyState))]
    [NotifyPropertyChangedFor(nameof(ViewBillToolTip))]
    [NotifyPropertyChangedFor(nameof(FolderToolTip))]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    [NotifyPropertyChangedFor(nameof(ExportButtonIcon))]
    [NotifyPropertyChangedFor(nameof(ExportButtonToolTip))]
    private string _exportedFilePath = string.Empty;

    private string? _lastExportedSignature;

    public bool CanViewExportedBill => (!string.IsNullOrWhiteSpace(ExportedFilePath) && File.Exists(ExportedFilePath)) || (Bill != null && Bill.Status == CustomerBillStatus.Exported);

    public bool IsBillExported => (Bill != null && Bill.Status == CustomerBillStatus.Exported) || (!string.IsNullOrWhiteSpace(ExportedFilePath) && File.Exists(ExportedFilePath));

    public bool HasChanges => _lastExportedSignature != null && _lastExportedSignature != ComputeCurrentStateSignature();

    public bool IsOpenOnlyState => CanViewExportedBill && !HasChanges;

    public string ExportButtonText
    {
        get
        {
            if (IsOpenOnlyState) return "XEM BILL";
            if (CanViewExportedBill && HasChanges) return "XUẤT LẠI BILL";
            return "XUẤT BILL";
        }
    }

    public string ExportButtonIcon => IsOpenOnlyState ? "🖼️ " : "⚡ ";

    public string ExportButtonToolTip
    {
        get
        {
            if (IsOpenOnlyState)
                return "Hóa đơn không có thay đổi so với bản đã duyệt. Bấm để mở xem trước hóa đơn và sao chép vào Clipboard (Ctrl + V).\n(Chuột phải vào nút để Buộc cập nhật lại)";
            if (CanViewExportedBill && HasChanges)
                return "Hóa đơn đã có chỉnh sửa số liệu. Bấm để lưu và mở xem lại hóa đơn mới.";
            return "Lưu hóa đơn và mở xem trước để sao chép gửi khách.";
        }
    }

    public string ViewBillToolTip => CanViewExportedBill
        ? "Mở xem trước hóa đơn để kiểm tra và sao chép gửi khách"
        : "Hóa đơn chưa được xuất/chốt (vui lòng bấm nút 'Xuất Bill' bên dưới)";

    public string FolderToolTip => CanViewExportedBill
        ? "Mở thư mục chứa hóa đơn"
        : "Chưa có thư mục tệp đã xuất";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(HasSuggestedCustomer))]
    [NotifyPropertyChangedFor(nameof(CanSplitOrders))]
    private bool _isLocked;

    public bool IsEditable => !IsLocked;

    public bool CanSplitOrders => Orders.Count > 1 && !IsLocked && Bill?.Status != CustomerBillStatus.Locked && Bill?.Status != CustomerBillStatus.Exported && !IsPaid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestedCustomer))]
    private Customer? _suggestedCustomer;

    public bool HasSuggestedCustomer => SuggestedCustomer != null && IsGuestBill && IsEditable;

    [ObservableProperty]
    private bool _isGuestBill;

    [ObservableProperty]
    private string _guestCustomerName = string.Empty;

    [ObservableProperty]
    private string _sourceFoldersSummary = string.Empty;

    [ObservableProperty]
    private string _newCustomLabel = string.Empty;

    [ObservableProperty]
    private long _newCustomAmount;

    [ObservableProperty]
    private AdjustmentDirection _newCustomDirection = AdjustmentDirection.Add;

    public ObservableCollection<CustomerBillOrderDisplayModel> Orders { get; } = new();
    public ObservableCollection<CustomerBillAdjustmentDisplayModel> Adjustments { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> BlockingIssues { get; } = new();

    public event Action? RequestClose;

    public CustomerBillReviewViewModel(
        CustomerBill bill,
        ICustomerBillingService customerBillingService,
        IJpegBillExporter jpegBillExporter,
        IExcelBillExporter? excelBillExporter,
        IReadOnlyList<string>? initialWarnings = null,
        IReadOnlyList<string>? initialBlockingIssues = null,
        ISettingsRepository? settingsRepository = null,
        Customer? suggestedCustomer = null,
        ICustomerBillRepository? customerBillRepository = null,
        IShippingLabelExporter? shippingLabelExporter = null,
        IBillVisualRenderer? billVisualRenderer = null,
        ICloudSyncService? cloudSyncService = null)
    {
        _bill = bill;
        _customerBillingService = customerBillingService;
        _jpegBillExporter = jpegBillExporter;
        _billVisualRenderer = billVisualRenderer ?? (jpegBillExporter as IBillVisualRenderer);
        _excelBillExporter = excelBillExporter;
        _settingsRepository = settingsRepository;
        _suggestedCustomer = suggestedCustomer;
        _customerBillRepository = customerBillRepository;
        _shippingLabelExporter = shippingLabelExporter;
        _cloudSyncService = cloudSyncService;

        LoadFromBill(bill, initialWarnings, initialBlockingIssues, suggestedCustomer);
    }

    private ICloudSyncService? ResolveCloudSyncService()
    {
        return _cloudSyncService ?? (Application.Current as App)?.Services?.GetService(typeof(ICloudSyncService)) as ICloudSyncService;
    }

    public CustomerBillReviewViewModel(
        CustomerBill bill,
        ICustomerBillingService customerBillingService,
        IJpegBillExporter jpegBillExporter,
        IReadOnlyList<string>? initialWarnings = null,
        IReadOnlyList<string>? initialBlockingIssues = null)
        : this(bill, customerBillingService, jpegBillExporter, null, initialWarnings, initialBlockingIssues, null, null, null, null, null, null)
    {
    }

    public void LoadFromBill(CustomerBill bill, IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? blockingIssues = null, Customer? suggestedCustomer = null)
    {
        Bill = bill;
        IsLocked = bill.Status != CustomerBillStatus.Draft;
        HasPendingCloudChanges = bill.HasPendingCloudChanges;
        IsPaid = bill.IsPaid;
        PaidAt = bill.PaidAt;
        IsGuestBill = bill.BillType == BillType.Guest;
        GuestCustomerName = bill.CustomerNameSnapshot;
        SuggestedCustomer = suggestedCustomer;
        Note = bill.Note ?? string.Empty;
        ShippingAddress = !string.IsNullOrWhiteSpace(bill.ShippingAddressSnapshot)
            ? bill.ShippingAddressSnapshot
            : (suggestedCustomer?.Address ?? string.Empty);
        if (!IsLocked) Bill.ShippingAddressSnapshot = ShippingAddress;
        WindowTitle = $"Duyệt & Xuất Hóa Đơn — {bill.BillNumber} ({bill.CustomerNameSnapshot})";

        if (bill.SourceFolders != null && bill.SourceFolders.Count > 0)
        {
            SourceFoldersSummary = string.Join("; ", bill.SourceFolders.Select(sf => sf.FolderPath));
        }
        else
        {
            SourceFoldersSummary = string.Empty;
        }

        Orders.Clear();
        foreach (var order in bill.Orders)
        {
            Orders.Add(new CustomerBillOrderDisplayModel(order, OnDataChanged));
        }

        Adjustments.Clear();
        foreach (var adj in bill.Adjustments)
        {
            Adjustments.Add(new CustomerBillAdjustmentDisplayModel(adj, OnDataChanged));
        }

        Warnings.Clear();
        if (warnings != null)
        {
            foreach (var w in warnings) Warnings.Add(w);
        }
        HasWarnings = Warnings.Count > 0;

        BlockingIssues.Clear();
        if (blockingIssues != null)
        {
            foreach (var b in blockingIssues) BlockingIssues.Add(b);
        }
        HasBlockingIssues = BlockingIssues.Count > 0;

        if (!string.IsNullOrEmpty(bill.ExportFilePath) && File.Exists(bill.ExportFilePath))
        {
            ExportedFilePath = bill.ExportFilePath;
            IsExportSuccess = true;
        }
        else
        {
            TryResolveExportFileFallback(bill);
        }

        RecalculateTotals();

        if (CanViewExportedBill)
        {
            _lastExportedSignature = ComputeCurrentStateSignature();
        }
        else
        {
            _lastExportedSignature = null;
        }

        UpdateExportButtonState();
    }

    private void TryResolveExportFileFallback(CustomerBill bill)
    {
        try
        {
            var candidateDirs = new List<string>();

            if (!string.IsNullOrWhiteSpace(bill.ExportFilePath))
            {
                string? dir = Path.GetDirectoryName(bill.ExportFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    candidateDirs.Add(dir);
                }
            }

            string defaultDocsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LalabReports", "Bills");
            if (Directory.Exists(defaultDocsDir)) candidateDirs.Add(defaultDocsDir);

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string localTestBills = Path.Combine(appDir, "TEST_DATA_LALAB", "Bills");
            if (Directory.Exists(localTestBills)) candidateDirs.Add(localTestBills);

            string localBills = Path.Combine(appDir, "Bills");
            if (Directory.Exists(localBills)) candidateDirs.Add(localBills);

            string currentDir = Directory.GetCurrentDirectory();
            string curTestBills = Path.Combine(currentDir, "TEST_DATA_LALAB", "Bills");
            if (Directory.Exists(curTestBills)) candidateDirs.Add(curTestBills);

            string curBills = Path.Combine(currentDir, "Bills");
            if (Directory.Exists(curBills)) candidateDirs.Add(curBills);

            if (_settingsRepository != null)
            {
                try
                {
                    var settings = Task.Run(() => _settingsRepository.GetSettingsAsync()).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(settings.BillExportFolder) && Directory.Exists(settings.BillExportFolder))
                    {
                        candidateDirs.Add(settings.BillExportFolder);
                    }
                }
                catch
                {
                    // Ignore
                }
            }

            string deterministicName = WpfJpegBillExporter.GenerateDeterministicFileName(bill);

            foreach (var dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir)) continue;

                string exactPath = Path.Combine(dir, deterministicName);
                if (File.Exists(exactPath))
                {
                    ExportedFilePath = exactPath;
                    IsExportSuccess = true;
                    bill.ExportFilePath = exactPath;
                    _ = _customerBillingService.RecordBillExportedAsync(bill.Id, exactPath);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(bill.BillNumber))
                {
                    var matches = Directory.GetFiles(dir, $"{bill.BillNumber}*.jpg");
                    if (matches.Length > 0 && File.Exists(matches[0]))
                    {
                        ExportedFilePath = matches[0];
                        IsExportSuccess = true;
                        bill.ExportFilePath = matches[0];
                        _ = _customerBillingService.RecordBillExportedAsync(bill.Id, matches[0]);
                        return;
                    }
                }
            }
        }
        catch
        {
            // Fallback search is best effort
        }
    }

    private void OnDataChanged()
    {
        RecalculateTotals();
        UpdateExportButtonState();
    }

    partial void OnNoteChanged(string value)
    {
        if (Bill != null && !IsLocked)
        {
            Bill.Note = value;
        }
        OnDataChanged();
    }

    public void UpdateExportButtonState()
    {
        OnPropertyChanged(nameof(CanViewExportedBill));
        OnPropertyChanged(nameof(IsBillExported));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(IsOpenOnlyState));
        OnPropertyChanged(nameof(ExportButtonText));
        OnPropertyChanged(nameof(ExportButtonIcon));
        OnPropertyChanged(nameof(ExportButtonToolTip));
        OnPropertyChanged(nameof(ViewBillToolTip));
        OnPropertyChanged(nameof(FolderToolTip));
        OnPropertyChanged(nameof(CanSplitOrders));
    }

    public string ComputeCurrentStateSignature()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Bill?.CustomerNameSnapshot?.Trim()).Append('|');
        sb.Append(ShippingAddress?.Trim()).Append('|');
        sb.Append(Note?.Trim()).Append('|');

        foreach (var o in Orders)
        {
            sb.Append(o.DomainOrder.OrderId).Append(':')
              .Append(o.IsIncluded).Append(':')
              .Append(o.OrderName?.Trim()).Append(';');
            foreach (var l in o.Lines)
            {
                sb.Append(l.ProductName?.Trim()).Append(':')
                  .Append(l.IsIncluded).Append(':')
                  .Append(l.BilledQuantity).Append(':')
                  .Append(l.BilledUnitPrice).Append(':')
                  .Append(l.Note?.Trim()).Append(';');
            }
        }

        foreach (var a in Adjustments)
        {
            sb.Append(a.Label?.Trim()).Append(':')
              .Append(a.Direction).Append(':')
              .Append(a.Amount).Append(';');
        }

        return sb.ToString();
    }

    public void RecalculateTotals()
    {
        if (IsLocked)
        {
            ProductSubtotal = Bill.ProductSubtotal;
            AdjustmentsTotal = Bill.AdjustmentsTotal;
            GrandTotal = Bill.GrandTotal;
            return;
        }
        // Subtotal of included orders and lines
        var includedOrderIds = Orders.Where(o => o.IsIncluded).Select(o => o.DomainOrder.OrderId).ToHashSet();
        ProductSubtotal = Orders
            .Where(o => o.IsIncluded)
            .SelectMany(o => o.Lines)
            .Where(l => l.IsIncluded)
            .Sum(l => l.LineTotal);

        long addTotal = Adjustments.Where(a => a.Direction == AdjustmentDirection.Add).Sum(a => a.Amount);
        long deductTotal = Adjustments.Where(a => a.Direction == AdjustmentDirection.Deduct).Sum(a => a.Amount);

        AdjustmentsTotal = addTotal - deductTotal;
        GrandTotal = Math.Max(0, ProductSubtotal + AdjustmentsTotal);

        Bill.ProductSubtotal = ProductSubtotal;
        Bill.AdjustmentsTotal = AdjustmentsTotal;
        Bill.GrandTotal = GrandTotal;

        // Recalculate dynamic blocking issues from currently included items
        BlockingIssues.Clear();
        var activeIssues = Orders
            .Where(o => o.IsIncluded)
            .SelectMany(o => o.Lines)
            .Where(l => l.IsIncluded && l.HasIssue)
            .Select(l =>
            {
                string folder = !string.IsNullOrWhiteSpace(l.SpecificationFolderName) ? l.SpecificationFolderName : l.ProductName;
                return $"[{folder}] {l.ProductName}: {l.IssueMessage}";
            })
            .Distinct()
            .ToList();

        foreach (var issue in activeIssues)
        {
            BlockingIssues.Add(issue);
        }
        HasBlockingIssues = BlockingIssues.Count > 0;
    }

    [RelayCommand]
    private void AddCustomAdjustment()
    {
        if (string.IsNullOrWhiteSpace(NewCustomLabel))
        {
            StatusMessage = "Vui lòng nhập tên khoản điều chỉnh!";
            return;
        }

        if (NewCustomAmount <= 0)
        {
            StatusMessage = "Số tiền phải lớn hơn 0!";
            return;
        }

        var domainAdj = new BillAdjustment
        {
            BillId = Bill.Id,
            Type = AdjustmentType.Custom,
            Label = NewCustomLabel.Trim(),
            Direction = NewCustomDirection,
            Amount = NewCustomAmount,
            SortOrder = Adjustments.Count + 1
        };

        Bill.Adjustments.Add(domainAdj);
        Adjustments.Add(new CustomerBillAdjustmentDisplayModel(domainAdj, OnDataChanged));

        NewCustomLabel = string.Empty;
        NewCustomAmount = 0;
        StatusMessage = "Đã thêm khoản điều chỉnh!";
        OnDataChanged();
    }

    [RelayCommand]
    private void RemoveAdjustment(CustomerBillAdjustmentDisplayModel? adj)
    {
        if (adj == null) return;
        Adjustments.Remove(adj);
        Bill.Adjustments.Remove(adj.DomainAdjustment);
        OnDataChanged();
    }

    [RelayCommand]
    private void ResetToScannedQuantities()
    {
        if (IsLocked) return;
        var confirm = MessageBox.Show(
            "Bạn có chắc chắn muốn khôi phục số lượng tính tiền của tất cả các dòng về theo đúng số lượng ảnh trên ổ đĩa không?",
            "Xác nhận khôi phục số lượng",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        foreach (var order in Orders)
        {
            foreach (var line in order.Lines)
            {
                if (!line.IsAlbum)
                {
                    line.BilledQuantity = line.ScannedQuantity;
                }
            }
        }

        OnDataChanged();
        StatusMessage = "Đã khôi phục số lượng tính tiền về đúng số lượng ảnh trên ổ đĩa!";
    }

    [RelayCommand]
    private async Task RescanDraftAsync()
    {
        if (IsLocked) return;
        try
        {
            IsBusy = true;
            StatusMessage = "Đang quét lại dữ liệu trên ổ đĩa...";
            CustomerBillDraftResult result;
            if (Bill.BillType == BillType.Guest || !Bill.CustomerId.HasValue)
            {
                var folderPaths = Bill.SourceFolders.Select(sf => sf.FolderPath).ToList();
                result = await _customerBillingService.BuildGuestBillDraftAsync(folderPaths, Bill.CustomerNameSnapshot, Bill.Id);
            }
            else
            {
                result = await _customerBillingService.BuildOrRefreshDraftAsync(Bill.CustomerId.Value, forceRescan: true);
            }
            LoadFromBill(result.Draft, result.Warnings, result.BlockingIssues);
            StatusMessage = "Đã quét lại và cập nhật hóa đơn!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi quét lại: {ex.Message}";
            MessageBox.Show($"Lỗi quét lại: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void EditGuestName()
    {
        if (IsLocked) return;
        var inputDlg = new InputDialog("Đổi Tên Khách Lẻ", "Nhập tên mới hiển thị trên hóa đơn và file ảnh JPEG:", Bill.CustomerNameSnapshot)
        {
            Owner = Application.Current?.MainWindow
        };

        if (inputDlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(inputDlg.InputText))
        {
            string newName = inputDlg.InputText.Trim();
            Bill.CustomerNameSnapshot = newName;
            GuestCustomerName = newName;
            WindowTitle = $"Duyệt & Xuất Hóa Đơn — {Bill.BillNumber} ({newName})";
            OnDataChanged();
        }
    }

    [RelayCommand]
    private async Task ConvertGuestToCustomerAsync()
    {
        if (!IsGuestBill || IsLocked) return;

        string promptDefault = !string.IsNullOrWhiteSpace(GuestCustomerName) ? GuestCustomerName : Bill.CustomerNameSnapshot;
        var inputDlg = new InputDialog("Chuyển thành Khách Hàng", "Nhập tên khách hàng để lưu vào danh mục của xưởng:", promptDefault)
        {
            Owner = Application.Current?.MainWindow
        };

        if (inputDlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(inputDlg.InputText))
        {
            try
            {
                IsBusy = true;
                StatusMessage = "Đang chuyển thành khách hàng chính thức...";
                if (Bill.Id == 0)
                {
                    var repo = _customerBillRepository ?? (App.Current as App)?.Services?.GetService(typeof(ICustomerBillRepository)) as ICustomerBillRepository;
                    if (repo != null)
                    {
                        await repo.SaveBillAsync(Bill);
                    }
                }
                var customer = await _customerBillingService.ConvertGuestBillToCustomerAsync(Bill.Id, inputDlg.InputText.Trim());
                Bill.CustomerId = customer.Id;
                Bill.BillType = BillType.Customer;
                Bill.CustomerNameSnapshot = customer.CanonicalName;
                IsGuestBill = false;
                GuestCustomerName = customer.CanonicalName;
                WindowTitle = $"Duyệt & Xuất Hóa Đơn — {Bill.BillNumber} ({customer.CanonicalName})";
                StatusMessage = $"Đã chuyển thành khách hàng '{customer.CanonicalName}' thành công!";
                MessageBox.Show($"Đã chuyển hóa đơn sang khách hàng '{customer.CanonicalName}' thành công.", "Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi chuyển khách hàng: {ex.Message}";
                MessageBox.Show($"Lỗi chuyển khách hàng: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task ConvertGuestToSuggestedCustomerAsync()
    {
        if (SuggestedCustomer == null) return;
        var confirm = MessageBox.Show(
            $"Bạn có chắc chắn muốn gán hóa đơn này vào khách quen '{SuggestedCustomer.CanonicalName}' không?",
            "Xác nhận gán khách hàng",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            IsBusy = true;
            StatusMessage = $"Đang chuyển đổi hóa đơn sang khách quen '{SuggestedCustomer.CanonicalName}'...";
            if (Bill.Id == 0)
            {
                var repo = _customerBillRepository ?? (App.Current as App)?.Services?.GetService(typeof(ICustomerBillRepository)) as ICustomerBillRepository;
                if (repo != null)
                {
                    await repo.SaveBillAsync(Bill);
                }
            }
            await _customerBillingService.ConvertGuestBillToCustomerAsync(Bill.Id, SuggestedCustomer.CanonicalName);
            Bill.BillType = BillType.Customer;
            Bill.CustomerId = SuggestedCustomer.Id;
            Bill.CustomerNameSnapshot = SuggestedCustomer.CanonicalName;
            Bill.PhoneSnapshot = SuggestedCustomer.Phone;
            IsGuestBill = false;
            GuestCustomerName = SuggestedCustomer.CanonicalName;
            var assignedCustName = SuggestedCustomer.CanonicalName;
            SuggestedCustomer = null;
            OnPropertyChanged(nameof(HasSuggestedCustomer));
            WindowTitle = $"Duyệt & Xuất Hóa Đơn — {Bill.BillNumber} ({Bill.CustomerNameSnapshot})";
            StatusMessage = $"Đã gán hóa đơn sang khách quen '{assignedCustName}' thành công!";
            MessageBox.Show($"Đã chuyển đổi sang khách quen '{assignedCustName}' thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi chuyển đổi: {ex.Message}";
            MessageBox.Show($"Lỗi chuyển đổi sang khách quen: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Action<string>? CustomFileOpener { get; set; }
    public Action<CustomerBill, IReadOnlyList<BitmapSource>>? CustomPreviewOpener { get; set; }

    [RelayCommand]
    private async Task CopyBillAsync()
    {
        RecalculateTotals();

        // Check blocking issues
        var issues = Orders
            .Where(o => o.IsIncluded)
            .SelectMany(o => o.Lines)
            .Where(l => l.IsIncluded && l.HasIssue)
            .Select(l => l.IssueMessage)
            .Distinct()
            .ToList();

        if (issues.Count > 0)
        {
            MessageBox.Show($"Không thể sao chép bill khi còn mục chưa xử lý:\n\n• {string.Join("\n• ", issues)}\n\nVui lòng xử lý hoặc bỏ chọn các mục bị lỗi trước khi sao chép.", "Không Thể Copy Bill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int includedLineCount = Orders.Where(o => o.IsIncluded).SelectMany(o => o.Lines).Count(l => l.IsIncluded);
        if (includedLineCount == 0)
        {
            MessageBox.Show("Vui lòng chọn ít nhất một sản phẩm trong đơn để sao chép bill.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Đang kết xuất ảnh hóa đơn vào Clipboard...";

            var renderer = _billVisualRenderer ?? (_jpegBillExporter as IBillVisualRenderer);
            if (renderer == null)
            {
                MessageBox.Show("Dịch vụ kết xuất hình ảnh chưa sẵn sàng.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var bitmaps = await renderer.RenderBillBitmapsAsync(Bill);
            if (bitmaps == null || bitmaps.Count == 0)
            {
                StatusMessage = "Không thể tạo ảnh hóa đơn.";
                return;
            }

            BitmapSource bitmapToCopy = bitmaps.Count == 1
                ? bitmaps[0]
                : renderer.StitchBitmapsVertically(bitmaps);

            var dataObject = new DataObject();
            dataObject.SetImage(bitmapToCopy);
            Clipboard.SetDataObject(dataObject, true);

            StatusMessage = "✓ Đã sao chép ảnh bill vào Clipboard! Bạn có thể dán (Ctrl+V) ngay sang Zalo/Messenger.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi sao chép bill: {ex.Message}";
            MessageBox.Show($"Không thể sao chép vào Clipboard: {ex.Message}", "Lỗi Clipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportBillAsync()
    {
        if (IsOpenOnlyState)
        {
            await OpenExportedFileAsync();
            return;
        }

        await ExecuteExportBillAsync();
    }

    [RelayCommand]
    private async Task ForceReexportBillAsync()
    {
        await ExecuteExportBillAsync();
    }

    private async Task ExecuteExportBillAsync()
    {
        RecalculateTotals();

        // Check blocking issues
        var issues = Orders
            .Where(o => o.IsIncluded)
            .SelectMany(o => o.Lines)
            .Where(l => l.IsIncluded && l.HasIssue)
            .Select(l => l.IssueMessage)
            .Distinct()
            .ToList();

        if (issues.Count > 0)
        {
            MessageBox.Show($"Không thể xuất bill khi còn mục chưa xử lý:\n\n• {string.Join("\n• ", issues)}\n\nVui lòng xử lý hoặc bỏ chọn các mục bị lỗi trước khi xuất.", "Không Thể Xuất Bill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int includedLineCount = Orders.Where(o => o.IsIncluded).SelectMany(o => o.Lines).Count(l => l.IsIncluded);
        if (includedLineCount == 0)
        {
            MessageBox.Show("Vui lòng chọn ít nhất một sản phẩm trong đơn để xuất bill.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Đang lưu hóa đơn...";

            if (Bill.Status == CustomerBillStatus.Draft)
                Bill = await _customerBillingService.LockBillAsync(Bill);
            else
                Bill = await _customerBillingService.GetBillSnapshotAsync(Bill.Id)
                    ?? throw new InvalidOperationException("Không tìm thấy snapshot hóa đơn.");
            LoadFromBill(Bill);
            IsLocked = true;

            // 3. Render in-memory bill pages
            StatusMessage = "Đang tạo bản xem trước hóa đơn...";
            var renderer = _billVisualRenderer ?? (_jpegBillExporter as IBillVisualRenderer);
            IReadOnlyList<BitmapSource>? bitmaps = null;

            if (renderer != null)
            {
                bitmaps = await renderer.RenderBillBitmapsAsync(Bill);
            }
            else
            {
                string jpegPath = await _jpegBillExporter.ExportBillToJpegAsync(Bill);
                ExportedFilePath = jpegPath;
            }

            await _customerBillingService.RecordBillExportedAsync(Bill.Id, ExportedFilePath ?? string.Empty);
            Bill.Status = CustomerBillStatus.Exported;
            IsExportSuccess = true;
            _lastExportedSignature = ComputeCurrentStateSignature();
            UpdateExportButtonState();
            StatusMessage = "Hóa đơn đã chốt và mở bản xem trước.";

            // 4. Mở cửa sổ xem trước (BillPreviewWindow)
            if (bitmaps != null && bitmaps.Count > 0)
            {
                ShowPreviewWindow(bitmaps, renderer);
            }
            else if (!string.IsNullOrWhiteSpace(ExportedFilePath) && File.Exists(ExportedFilePath))
            {
                OpenExportedFile();
            }

            // 5. Đồng bộ hóa đơn lên Cloudflare (chạy ngầm không chặn UI)
            _ = Task.Run(async () =>
            {
                try
                {
                    var syncService = ResolveCloudSyncService();
                    if (syncService != null && Bill?.Id > 0)
                    {
                        await syncService.SyncBillAsync(Bill.Id);
                    var current = _customerBillRepository == null ? null : await _customerBillRepository.GetByIdAsync(Bill.Id);
                    if (current != null && Application.Current?.Dispatcher != null)
                        await Application.Current.Dispatcher.InvokeAsync(() => { HasPendingCloudChanges = current.HasPendingCloudChanges; IsPaid = current.IsPaid; PaidAt = current.PaidAt; });
                    }
                }
                catch
                {
                    // Best-effort non-blocking sync
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi xuất hóa đơn: {ex.Message}";
            if (CustomPreviewOpener != null) throw;
            MessageBox.Show($"Lỗi xuất hóa đơn: {ex.Message}", "Lỗi Xuất Bill", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private BillPreviewWindow? _activePreviewWindow;

    private void ShowPreviewWindow(IReadOnlyList<BitmapSource> bitmaps, IBillVisualRenderer? renderer)
    {
        if (CustomPreviewOpener != null)
        {
            CustomPreviewOpener(Bill, bitmaps);
            return;
        }

        if (_activePreviewWindow != null && _activePreviewWindow.IsLoaded && _activePreviewWindow.IsVisible)
        {
            _activePreviewWindow.UpdatePages(bitmaps, Bill);
            _activePreviewWindow.Activate();
            return;
        }

        Window? ownerWindow = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (ownerWindow == null || !ownerWindow.IsVisible)
        {
            ownerWindow = Application.Current?.Windows.OfType<CustomerBillReviewWindow>().FirstOrDefault(w => w.IsVisible)
                          ?? Application.Current?.MainWindow;
        }

        var previewWin = new BillPreviewWindow(Bill, bitmaps, renderer, onForceReexport: async () =>
        {
            await ForceReexportBillCommand.ExecuteAsync(null);
        })
        {
            Owner = ownerWindow
        };
        _activePreviewWindow = previewWin;
        previewWin.Closed += (s, e) =>
        {
            if (_activePreviewWindow == previewWin)
            {
                _activePreviewWindow = null;
            }
        };
        previewWin.Show();
    }

    [RelayCommand]
    private async Task OpenExportedFileAsync()
    {
        if (IsLocked)
        {
            var snapshot = await _customerBillingService.GetBillSnapshotAsync(Bill.Id);
            if (snapshot != null) LoadFromBill(snapshot);
        }
        if (CustomFileOpener != null)
        {
            CustomFileOpener(ExportedFilePath);
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Đang tạo bản xem trước hóa đơn...";
            var renderer = _billVisualRenderer ?? (_jpegBillExporter as IBillVisualRenderer);
            if (renderer != null)
            {
                var bitmaps = await renderer.RenderBillBitmapsAsync(Bill);
                if (bitmaps.Count > 0)
                {
                    StatusMessage = "Sẵn sàng (Xem trước bill đã mở).";
                    ShowPreviewWindow(bitmaps, renderer);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(ExportedFilePath) && File.Exists(ExportedFilePath))
            {
                CopyBillImageToClipboard(ExportedFilePath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = ExportedFilePath,
                    UseShellExecute = true
                });
                StatusMessage = "Đã mở bill & sao chép ảnh vào Clipboard (Ctrl + V để dán)!";
            }
            else
            {
                MessageBox.Show("Không tìm thấy tệp hoặc hình ảnh của hóa đơn.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể mở xem bill: {ex.Message}", "Lỗi mở file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenExportedFile()
    {
        _ = OpenExportedFileAsync();
    }

    [RelayCommand]
    private void OpenExportedFolder()
    {
        if (string.IsNullOrWhiteSpace(ExportedFilePath)) return;
        try
        {
            if (File.Exists(ExportedFilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{ExportedFilePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                string? dir = Path.GetDirectoryName(ExportedFilePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                else
                {
                    MessageBox.Show("Thư mục lưu hóa đơn không tồn tại.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể mở thư mục: {ex.Message}", "Lỗi mở thư mục", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public Func<string, string, string?> ReopenReasonFunc { get; set; } = (title, prompt) =>
    {
        var dialog = new InputDialog(title, prompt, "")
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        };
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    };

    public Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> ConfirmFunc { get; set; } = MessageBox.Show;

    [RelayCommand]
    private async Task ReopenBillAsync()
    {
        string? rawReason = ReopenReasonFunc("Mở lại hóa đơn", "Nhập lý do sửa snapshot hóa đơn đã chốt:");
        if (string.IsNullOrWhiteSpace(rawReason)) return;
        string reason = rawReason.Trim();

        string billName = !string.IsNullOrWhiteSpace(Bill.BillNumber)
            ? Bill.BillNumber
            : (Bill.CustomerNameSnapshot ?? "này");

        var confirm = ConfirmFunc(
            $"Bạn có chắc chắn muốn mở khóa hóa đơn '{billName}' để chỉnh sửa không?\n\nHóa đơn đã khóa sẽ quay lại trạng thái Nháp (Draft).",
            "Xác nhận mở khóa hóa đơn",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            IsBusy = true;
            StatusMessage = "Đang mở khóa hóa đơn...";
            await _customerBillingService.ReopenBillAsync(Bill.Id, reason);
            Bill.Status = CustomerBillStatus.Draft;
            Bill.LockedAt = null;
            Bill.ExportedAt = null;
            Bill.ExportFilePath = null;
            IsLocked = false;
            IsExportSuccess = false;
            ExportedFilePath = string.Empty;
            _lastExportedSignature = null;
            UpdateExportButtonState();

            if (_activePreviewWindow != null && _activePreviewWindow.IsLoaded)
            {
                _activePreviewWindow.Close();
                _activePreviewWindow = null;
            }

            StatusMessage = "Hóa đơn đã được mở khóa về bản nháp (Draft)!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi mở khóa: {ex.Message}";
            MessageBox.Show($"Lỗi mở khóa: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SplitOrderAsync(CustomerBillOrderDisplayModel? orderModel)
    {
        if (orderModel == null || !CanSplitOrders) return;

        var confirm = MessageBox.Show(
            $"Bạn có chắc chắn muốn tách đơn '{orderModel.OrderName}' ra thành một Hóa đơn độc lập mới không?\n\nĐơn hàng này sẽ được chuyển sang Bill mới với mã số riêng biệt và tổng tiền của cả 2 hóa đơn sẽ được tính toán lại.",
            "Xác nhận tách đơn",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            IsBusy = true;
            StatusMessage = $"Đang tách đơn '{orderModel.OrderName}' sang Bill mới...";

            var newBill = await _customerBillingService.SplitOrdersToNewBillAsync(Bill.Id, new[] { orderModel.DomainOrder.OrderId });

            // Reload current bill from repository if available
            var repo = _customerBillRepository ?? (App.Current as App)?.Services?.GetService(typeof(ICustomerBillRepository)) as ICustomerBillRepository;
            if (repo != null)
            {
                var updatedCurrentBill = await repo.GetByIdAsync(Bill.Id);
                if (updatedCurrentBill != null)
                {
                    LoadFromBill(updatedCurrentBill, Warnings, BlockingIssues, SuggestedCustomer);
                }
            }
            else
            {
                // In-memory fallback
                Orders.Remove(orderModel);
                Bill.Orders.Remove(orderModel.DomainOrder);
                Bill.Lines.RemoveAll(l => l.OrderId == orderModel.DomainOrder.OrderId);
                RecalculateTotals();
                OnPropertyChanged(nameof(CanSplitOrders));
            }

            StatusMessage = $"Đã tách đơn sang Hóa đơn mới '{newBill.BillNumber}' thành công!";

            var openNew = MessageBox.Show(
                $"Đã tạo thành công Hóa đơn mới: {newBill.BillNumber} ({newBill.CustomerNameSnapshot})\n\nBạn có muốn mở xem Hóa đơn mới vừa tách ngay bây giờ không?",
                "Tách Đơn Thành Công",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (openNew == MessageBoxResult.Yes)
            {
                var newVm = new CustomerBillReviewViewModel(
                    newBill,
                    _customerBillingService,
                    _jpegBillExporter,
                    _excelBillExporter,
                    null,
                    null,
                    _settingsRepository,
                    null,
                    _customerBillRepository,
                    _shippingLabelExporter);

                var newWindow = new CustomerBillReviewWindow(newVm)
                {
                    Owner = Application.Current?.MainWindow
                };
                newWindow.Show();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi tách đơn: {ex.Message}";
            MessageBox.Show($"Lỗi khi tách đơn: {ex.Message}", "Lỗi Tách Đơn", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static bool CopyBillImageToClipboard(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

        // Clipboard operations require STA thread and active WPF application context
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();

            var dataObject = new DataObject();
            dataObject.SetImage(bitmap);

            var fileDropList = new System.Collections.Specialized.StringCollection { filePath };
            dataObject.SetFileDropList(fileDropList);

            Clipboard.SetDataObject(dataObject, false);
            return true;
        }
        catch
        {
            // Clipboard access might rarely fail if locked by another program
            return false;
        }
    }

    [RelayCommand]
    public async Task TogglePaymentStatusAsync()
    {
        if (Bill == null || Bill.Id == 0) return;

        bool newStatus = !IsPaid;
        var newPaidAt = newStatus ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;

        var repo = _customerBillRepository ?? (App.Current as App)?.Services?.GetService(typeof(ICustomerBillRepository)) as ICustomerBillRepository;
        if (repo != null)
        {
            await repo.SetPaymentStatusAsync(Bill.Id, newStatus, newPaidAt);
        }

        HasPendingCloudChanges = repo != null && (await repo.GetByIdAsync(Bill.Id))?.HasPendingCloudChanges == true;
        Bill.HasPendingCloudChanges = HasPendingCloudChanges;
        IsPaid = newStatus;
        PaidAt = newPaidAt;
        Bill.IsPaid = newStatus;
        Bill.PaidAt = newPaidAt;

        StatusMessage = newStatus ? "Đã chuyển trạng thái: ĐÃ THANH TOÁN" : "Đã chuyển trạng thái: CHƯA THANH TOÁN";

        // Đồng bộ trạng thái thanh toán lên Cloudflare (chạy ngầm không chặn UI)
        _ = Task.Run(async () =>
        {
            try
            {
                var syncService = ResolveCloudSyncService();
                if (syncService != null && Bill?.Id > 0)
                {
                    await syncService.SyncBillAsync(Bill.Id);
                    var current = _customerBillRepository == null ? null : await _customerBillRepository.GetByIdAsync(Bill.Id);
                    if (current != null && Application.Current?.Dispatcher != null)
                        await Application.Current.Dispatcher.InvokeAsync(() => { HasPendingCloudChanges = current.HasPendingCloudChanges; IsPaid = current.IsPaid; PaidAt = current.PaidAt; });
                }
            }
            catch
            {
                // Best-effort non-blocking sync
            }
        });
    }

    [RelayCommand]
    public async Task PrintShippingLabelAsync()
    {
        var exporter = _shippingLabelExporter ?? (App.Current as App)?.Services?.GetService(typeof(IShippingLabelExporter)) as IShippingLabelExporter;
        if (exporter == null)
        {
            MessageBox.Show("Dịch vụ in tem vận chuyển chưa sẵn sàng.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (!IsLocked) Bill.ShippingAddressSnapshot = ShippingAddress;

            // Persist address snapshot if repo available
            var repo = _customerBillRepository ?? (App.Current as App)?.Services?.GetService(typeof(ICustomerBillRepository)) as ICustomerBillRepository;
            if (repo != null && Bill.Id > 0)
            {
                await repo.SaveBillAsync(Bill);
            }

            var settingsRepo = _settingsRepository ?? (App.Current as App)?.Services?.GetService(typeof(ISettingsRepository)) as ISettingsRepository;
            var orderRepo = (App.Current as App)?.Services?.GetService(typeof(IOrderRepository)) as IOrderRepository;

            if (settingsRepo != null)
            {
                var previewWindow = new Views.ShippingLabelPreviewWindow(
                    null,
                    Bill,
                    exporter,
                    settingsRepo,
                    orderRepo
                );

                var activeWindow = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
                if (activeWindow != null && activeWindow.IsVisible)
                {
                    previewWindow.Owner = activeWindow;
                }

                bool? dialogResult = previewWindow.ShowDialog();
                if (dialogResult == true && previewWindow.WasPrinted)
                {
                    StatusMessage = "Đã gửi lệnh in tem vận chuyển tới máy in thành công!";
                }
            }
            else
            {
                await exporter.PrintShippingLabelAsync(Bill);
                StatusMessage = "Đã gửi lệnh in tem vận chuyển tới máy in thành công!";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi in tem: {ex.Message}";
            MessageBox.Show($"Lỗi khi in tem vận chuyển: {ex.Message}", "Lỗi In Tem", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public async Task ExportShippingLabelAsync()
    {
        var exporter = _shippingLabelExporter ?? (App.Current as App)?.Services?.GetService(typeof(IShippingLabelExporter)) as IShippingLabelExporter;
        if (exporter == null)
        {
            MessageBox.Show("Dịch vụ xuất tem vận chuyển chưa sẵn sàng.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            IsBusy = true;
            if (!IsLocked) Bill.ShippingAddressSnapshot = ShippingAddress;
            StatusMessage = "Đang kết xuất ảnh tem vận chuyển...";

            // Persist address snapshot if repo available
            var repo = _customerBillRepository ?? (App.Current as App)?.Services?.GetService(typeof(ICustomerBillRepository)) as ICustomerBillRepository;
            if (repo != null && Bill.Id > 0)
            {
                await repo.SaveBillAsync(Bill);
            }

            string labelPath = await exporter.ExportShippingLabelImageAsync(Bill);
            StatusMessage = $"Đã xuất tem dán: {Path.GetFileName(labelPath)} & đã sao chép vào Clipboard!";

            // Auto-copy image to clipboard
            CopyBillImageToClipboard(labelPath);

            // Tự động mở xem ảnh tem vừa xuất (không cần hộp thoại xác nhận)
            try
            {
                Process.Start(new ProcessStartInfo(labelPath) { UseShellExecute = true });
            }
            catch
            {
                // Bỏ qua nếu môi trường không có trình xem ảnh mặc định
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi xuất tem: {ex.Message}";
            MessageBox.Show($"Lỗi khi xuất ảnh tem: {ex.Message}", "Lỗi Xuất Tem", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveCurrentStateAsync()
    {
        if (IsLocked) return;
        try
        {
            RecalculateTotals();
            var repo = _customerBillRepository ?? (App.Current as App)?.Services?.GetService(typeof(ICustomerBillRepository)) as ICustomerBillRepository;
            if (repo != null && Bill != null && Bill.Id > 0)
            {
                await repo.SaveBillAsync(Bill);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Không lưu được bản nháp: {ex.Message}";
            MessageBox.Show(StatusMessage, "Lỗi lưu hóa đơn", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void CloseWindow()
    {
        RequestClose?.Invoke();
    }
}
