using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.UI.Services;
using Microsoft.Win32;

namespace LalabAutoReport.UI.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IDatabaseBackupService _backupService;
    private readonly IContextMenuIntegrationService _contextMenuService;
    private readonly IDatabaseResetService? _resetService;
    private readonly IUpdateService? _updateService;
    private readonly IRootFolderRepository? _rootFolderRepository;
    private readonly IDataPurgeService? _purgeService;
    private readonly IOrderRepository? _orderRepository;
    private readonly IMobileWebServer? _mobileWebServer;
    private readonly IMobileAuthService? _authService;
    private readonly IThumbnailService? _thumbnailService;
    private readonly IShippingLabelExporter? _shippingLabelExporter;
    private readonly ISystemTrayManager? _systemTrayManager;
    private readonly ICloudSyncService? _cloudSyncService;

    [ObservableProperty]
    private bool _enableCloudSync = true;

    [ObservableProperty]
    private string _cloudSyncApiUrl = "https://lalab.tinix.io.vn";

    [ObservableProperty]
    private string _cloudSyncSecret = "";

    [ObservableProperty]
    private string _lastCloudSyncAt = "Chưa đồng bộ";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCloudNowCommand))]
    private bool _isCloudSyncing;

    [ObservableProperty]
    private string _cloudSyncStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose = true;

    [ObservableProperty]
    private bool _autoStartWithWindows = true;

    [ObservableProperty]
    private bool _showOrderThumbnails = true;

    [ObservableProperty]
    private bool _enableThumbnailAutoCleanup = true;

    public record RetentionOption(int Days, string DisplayName);

    public IReadOnlyList<RetentionOption> RetentionOptions { get; } = new List<RetentionOption>
    {
        new(30, "30 ngày không sử dụng"),
        new(60, "60 ngày không sử dụng (Mặc định)"),
        new(90, "90 ngày không sử dụng")
    };

    [ObservableProperty]
    private int _thumbnailRetentionDays = 60;

    [ObservableProperty]
    private string _thumbnailCacheSizeText = "0 MB";

    [ObservableProperty]
    private string _thumbnailCacheStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _enableMobileServer = true;

    [ObservableProperty]
    private int _mobileServerPort = 5050;

    [ObservableProperty]
    private string _adminPin = string.Empty;

    [ObservableProperty]
    private string _staffPin = string.Empty;

    [ObservableProperty]
    private string _mobileServerStatusText = "Đang kiểm tra...";

    [ObservableProperty]
    private string _localMobileUrl = string.Empty;

    [ObservableProperty]
    private string _remoteMobileUrl = string.Empty;

    [ObservableProperty]
    private System.Windows.Media.ImageSource? _adminQrCodeImage;

    [ObservableProperty]
    private System.Windows.Media.ImageSource? _staffQrCodeImage;

    // Thermal Label Printer Settings (40x100mm default, 75x100mm optional)
    public ObservableCollection<string> AvailableThermalLabelSizes { get; } = new()
    {
        "40x100mm (Khổ ngang 100x40mm - Mặc định)",
        "75x100mm (Khổ dọc tiêu chuẩn 75x100mm)"
    };

    [ObservableProperty]
    private string _selectedThermalLabelSize = "40x100mm (Khổ ngang 100x40mm - Mặc định)";
    [ObservableProperty]
    private ObservableCollection<string> _availablePrinters = new();

    [ObservableProperty]
    private string _selectedThermalPrinter = "(Sử dụng máy in mặc định của Windows)";

    [ObservableProperty]
    private bool _autoMarkDeliveredOnPrint = true;

    [ObservableProperty]
    private string _testPrintStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isTestPrinting = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLanSourceSelected))]
    [NotifyPropertyChangedFor(nameof(IsRemoteSourceSelected))]
    private bool _preferRemoteUrlForQr = false;

    public bool IsLanSourceSelected
    {
        get => !PreferRemoteUrlForQr;
        set
        {
            if (value && PreferRemoteUrlForQr)
            {
                PreferRemoteUrlForQr = false;
            }
        }
    }

    public bool IsRemoteSourceSelected
    {
        get => PreferRemoteUrlForQr;
        set
        {
            if (value && !PreferRemoteUrlForQr)
            {
                PreferRemoteUrlForQr = true;
            }
        }
    }

    partial void OnPreferRemoteUrlForQrChanged(bool value)
    {
        GenerateMobileQrCodes();
    }

    [ObservableProperty]
    private string _rootFolder = string.Empty;

    public ObservableCollection<RootFolderDisplayItem> RootFolders { get; } = new();

    [ObservableProperty]
    private string _rootFolderStatusMessage = string.Empty;

    [ObservableProperty]
    private string _purgePeriodPreset = "6Months";

    [ObservableProperty]
    private DateTime _purgeCutoffDate = DateTime.Today.AddMonths(-6);

    [ObservableProperty]
    private string _purgePreviewSummary = string.Empty;

    [ObservableProperty]
    private bool _hasPurgePreview;

    [ObservableProperty]
    private bool _isPurging;

    [ObservableProperty]
    private string _purgeStatusMessage = string.Empty;

    [ObservableProperty]
    private string _billExportFolder = string.Empty;

    [ObservableProperty]
    private string _secondaryBackupFolder = string.Empty;

    [ObservableProperty]
    private bool _enableVietQrOnBill = true;

    public IReadOnlyList<BankInfo> AvailableBanks => VietnameseBanks.All;

    [ObservableProperty]
    private BankInfo? _selectedBank;

    partial void OnSelectedBankChanged(BankInfo? value)
    {
        if (value != null)
        {
            BankBinOrCode = value.Bin;
        }
    }

    [ObservableProperty]
    private string _bankBinOrCode = "970422";

    [ObservableProperty]
    private string _bankAccountNumber = string.Empty;

    [ObservableProperty]
    private string _bankAccountName = string.Empty;

    [ObservableProperty]
    private string _workshopName = "XƯỞNG IN ẢNH CHUYÊN NGHIỆP";

    [ObservableProperty]
    private string _workshopSlogan = "Dịch vụ in ấn ảnh & Album chuyên nghiệp";

    [ObservableProperty]
    private string _workshopPhone = string.Empty;

    [ObservableProperty]
    private string _workshopAddress = string.Empty;

    [ObservableProperty]
    private string _invoiceFooterMessage = "Cảm ơn quý khách đã tin tưởng và ủng hộ dịch vụ!";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkshopLogo))]
    private string? _workshopLogoPath;

    public bool HasWorkshopLogo => !string.IsNullOrWhiteSpace(WorkshopLogoPath) && File.Exists(WorkshopLogoPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVietQrMode))]
    [NotifyPropertyChangedFor(nameof(IsCustomQrMode))]
    [NotifyPropertyChangedFor(nameof(IsQrDisabled))]
    private QrDisplayMode _qrMode = QrDisplayMode.VietQrAuto;

    public bool IsVietQrMode
    {
        get => QrMode == QrDisplayMode.VietQrAuto;
        set { if (value) QrMode = QrDisplayMode.VietQrAuto; }
    }

    public bool IsCustomQrMode
    {
        get => QrMode == QrDisplayMode.CustomImage;
        set { if (value) QrMode = QrDisplayMode.CustomImage; }
    }

    public bool IsQrDisabled
    {
        get => QrMode == QrDisplayMode.None;
        set { if (value) QrMode = QrDisplayMode.None; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomQrImage))]
    private string? _customQrImagePath;

    public bool HasCustomQrImage => !string.IsNullOrWhiteSpace(CustomQrImagePath) && File.Exists(CustomQrImagePath);

    [ObservableProperty]
    private string _supportedExtensions = string.Empty;

    [ObservableProperty]
    private bool _autoScanStartup;

    [ObservableProperty]
    private bool _startupIncludeYesterday;

    [ObservableProperty]
    private bool _enableIdleScan = true;

    [ObservableProperty]
    private int _idleThresholdMinutes = 30;

    [ObservableProperty]
    private int _idleScanWindowDays = 7;

    public int[] IdleThresholdOptions { get; } = new[] { 15, 30, 45, 60 };

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _databasePath = string.Empty;

    [ObservableProperty]
    private string _backupStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBackingUp;

    [ObservableProperty]
    private bool _isContextMenuRegistered;

    [ObservableProperty]
    private bool _autoRegisterContextMenu = true;

    [ObservableProperty]
    private string _contextMenuStatusMessage = string.Empty;

    [ObservableProperty]
    private string _appVersion = "1.0.0";

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private string _updateStatusMessage = string.Empty;

    private bool _isLoadingSettings;

    public ObservableCollection<DatabaseBackupInfo> RecentBackups { get; } = new();

    public event Action? SettingsSaved;
    public event Action? DataResetCompleted;

    public SettingsViewModel(
        ISettingsRepository settingsRepository,
        IDatabaseBackupService backupService,
        IContextMenuIntegrationService contextMenuService,
        IDatabaseResetService? resetService = null,
        IUpdateService? updateService = null,
        IRootFolderRepository? rootFolderRepository = null,
        IDataPurgeService? purgeService = null,
        IOrderRepository? orderRepository = null,
        IMobileWebServer? mobileWebServer = null,
        IMobileAuthService? authService = null,
        IThumbnailService? thumbnailService = null,
        IShippingLabelExporter? shippingLabelExporter = null,
        ISystemTrayManager? systemTrayManager = null,
        ICloudSyncService? cloudSyncService = null)
    {
        _settingsRepository = settingsRepository;
        _backupService = backupService;
        _contextMenuService = contextMenuService;
        _resetService = resetService;
        _updateService = updateService;
        _rootFolderRepository = rootFolderRepository;
        _purgeService = purgeService;
        _orderRepository = orderRepository;
        _mobileWebServer = mobileWebServer;
        _authService = authService;
        _thumbnailService = thumbnailService;
        _shippingLabelExporter = shippingLabelExporter;
        _systemTrayManager = systemTrayManager;
        _cloudSyncService = cloudSyncService;

        if (_cloudSyncService != null)
        {
            _cloudSyncService.SyncStatusChanged += (s, e) => Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                IsCloudSyncing = _cloudSyncService.IsSyncing;
                if (!string.IsNullOrWhiteSpace(_cloudSyncService.LastSyncStatus))
                {
                    CloudSyncStatusMessage = _cloudSyncService.LastSyncStatus;
                }
            });
        }

        if (_mobileWebServer != null)
        {
            _mobileWebServer.StatusChanged += (s, e) => Application.Current?.Dispatcher?.InvokeAsync(UpdateMobileServerStatusAndQrs);
        }

        DatabasePath = _backupService.GetDatabasePath();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
        {
            AppVersion = $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public async Task LoadSettingsAsync()
    {
        _isLoadingSettings = true;
        try
        {
            var settings = await _settingsRepository.GetSettingsAsync();
            RootFolder = settings.RootFolder;
            BillExportFolder = settings.BillExportFolder;
            SecondaryBackupFolder = settings.SecondaryBackupFolder ?? string.Empty;
            EnableVietQrOnBill = settings.EnableVietQrOnBill;
            BankBinOrCode = string.IsNullOrWhiteSpace(settings.BankBinOrCode) ? "970422" : settings.BankBinOrCode;
            SelectedBank = VietnameseBanks.FindByBin(BankBinOrCode) ?? VietnameseBanks.All.FirstOrDefault(b => b.Bin == "970422");
            BankAccountNumber = settings.BankAccountNumber ?? string.Empty;
            BankAccountName = settings.BankAccountName ?? string.Empty;
            WorkshopName = string.IsNullOrWhiteSpace(settings.WorkshopName) ? "XƯỞNG IN ẢNH CHUYÊN NGHIỆP" : settings.WorkshopName;
            WorkshopSlogan = string.IsNullOrWhiteSpace(settings.WorkshopSlogan) ? "Dịch vụ in ấn ảnh & Album chuyên nghiệp" : settings.WorkshopSlogan;
            WorkshopPhone = settings.WorkshopPhone ?? string.Empty;
            WorkshopAddress = settings.WorkshopAddress ?? string.Empty;
            InvoiceFooterMessage = string.IsNullOrWhiteSpace(settings.InvoiceFooterMessage) ? "Cảm ơn quý khách đã tin tưởng và ủng hộ dịch vụ!" : settings.InvoiceFooterMessage;
            WorkshopLogoPath = settings.WorkshopLogoPath;
            QrMode = settings.QrMode;
            CustomQrImagePath = settings.CustomQrImagePath;
            SupportedExtensions = string.Join(", ", settings.SupportedExtensions);
            AutoScanStartup = settings.AutoScanStartup;
            StartupIncludeYesterday = settings.StartupIncludeYesterday;
            AutoRegisterContextMenu = settings.AutoRegisterContextMenu;
            EnableIdleScan = settings.EnableIdleScan;
            IdleThresholdMinutes = settings.IdleThresholdMinutes;
            IdleScanWindowDays = settings.IdleScanWindowDays;

            EnableMobileServer = settings.EnableMobileServer;
            MobileServerPort = settings.MobileServerPort > 0 ? settings.MobileServerPort : 5050;
            AdminPin = settings.AdminPin ?? string.Empty;
            StaffPin = settings.StaffPin ?? string.Empty;
            ShowOrderThumbnails = settings.ShowOrderThumbnails;
            EnableThumbnailAutoCleanup = settings.EnableThumbnailAutoCleanup;
            ThumbnailRetentionDays = (settings.ThumbnailRetentionDays == 30 || settings.ThumbnailRetentionDays == 90) ? settings.ThumbnailRetentionDays : 60;

            // Thermal Printer Settings
            if (string.Equals(settings.ThermalLabelSize, ThermalLabelSizes.Size75x100, StringComparison.OrdinalIgnoreCase))
            {
                SelectedThermalLabelSize = "75x100mm (Khổ dọc tiêu chuẩn 75x100mm)";
            }
            else
            {
                SelectedThermalLabelSize = "40x100mm (Khổ ngang 100x40mm - Mặc định)";
            }
            RefreshPrinters();
            if (!string.IsNullOrWhiteSpace(settings.ThermalPrinterName))
            {
                if (!AvailablePrinters.Contains(settings.ThermalPrinterName))
                {
                    AvailablePrinters.Add(settings.ThermalPrinterName);
                }
                SelectedThermalPrinter = settings.ThermalPrinterName;
            }
            else
            {
                SelectedThermalPrinter = "(Sử dụng máy in mặc định của Windows)";
            }
            AutoMarkDeliveredOnPrint = settings.AutoMarkDeliveredOnPrint;
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;
            AutoStartWithWindows = settings.AutoStartWithWindows;

            EnableCloudSync = settings.EnableCloudSync;
            CloudSyncApiUrl = string.IsNullOrWhiteSpace(settings.CloudSyncApiUrl) || string.Equals(settings.CloudSyncApiUrl.Trim(), "https://app.tinix.io.vn", StringComparison.OrdinalIgnoreCase)
                ? "https://lalab.tinix.io.vn"
                : settings.CloudSyncApiUrl;
            CloudSyncSecret = string.IsNullOrWhiteSpace(settings.CloudSyncSecret) ? "" : settings.CloudSyncSecret;
            LastCloudSyncAt = string.IsNullOrWhiteSpace(settings.LastCloudSyncAt) ? "Chưa đồng bộ" : settings.LastCloudSyncAt;

            if (AutoStartWithWindows && !WindowsStartupHelper.IsAutoStartConfigured())
            {
                WindowsStartupHelper.SetAutoStart(true);
            }

            StatusMessage = string.Empty;
            BackupStatusMessage = string.Empty;
            ContextMenuStatusMessage = string.Empty;
            ThumbnailCacheStatusMessage = string.Empty;
            RefreshThumbnailCacheSize();

            IsContextMenuRegistered = _contextMenuService.IsContextMenuRegistered();
            if (AutoRegisterContextMenu && !IsContextMenuRegistered)
            {
                try
                {
                    _contextMenuService.EnsureContextMenuRegistered();
                    IsContextMenuRegistered = _contextMenuService.IsContextMenuRegistered();
                }
                catch { }
            }

            await LoadBackupsAsync();
            await LoadRootFoldersAsync();
            UpdateMobileServerStatusAndQrs();
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    public async Task LoadBackupsAsync()
    {
        try
        {
            var backups = await _backupService.GetBackupsAsync();
            RecentBackups.Clear();
            foreach (var b in backups)
            {
                RecentBackups.Add(b);
            }
        }
        catch (Exception ex)
        {
            BackupStatusMessage = $"Không thể tải danh sách sao lưu: {ex.Message}";
        }
    }

    public async Task LoadRootFoldersAsync()
    {
        if (_rootFolderRepository == null) return;

        try
        {
            var roots = await _rootFolderRepository.GetAllAsync();
            RootFolders.Clear();
            foreach (var r in roots)
            {
                RootFolders.Add(new RootFolderDisplayItem
                {
                    Id = r.Id,
                    Name = r.Name,
                    FullPath = r.FullPath,
                    IsActive = r.IsActive,
                    IsDefault = r.IsDefault,
                    IsPathAccessible = Directory.Exists(r.FullPath)
                });
            }

            var defaultRoot = roots.FirstOrDefault(r => r.IsDefault) ?? roots.FirstOrDefault();
            if (defaultRoot != null && string.IsNullOrWhiteSpace(RootFolder))
            {
                RootFolder = defaultRoot.FullPath;
            }
        }
        catch (Exception ex)
        {
            RootFolderStatusMessage = $"Không thể tải danh sách kho dữ liệu: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddRootFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn Thư Mục Gốc Chứa Các Ngày Cần Quét (Root Folder)"
        };

        if (dialog.ShowDialog() != true) return;

        string path = dialog.FolderName;
        string defaultName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(defaultName)) defaultName = "Kho Mới";

        var ownerWindow = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                          ?? Application.Current?.MainWindow;
        string? chosenName = Views.InputDialog.Show(ownerWindow!, "Đặt Tên Kho", "Nhập tên gợi nhớ cho kho dữ liệu này (ví dụ: Kho 2026, Kho Gắn Ngoài, v.v.):", defaultName);
        if (string.IsNullOrWhiteSpace(chosenName)) chosenName = defaultName;

        if (_rootFolderRepository != null)
        {
            try
            {
                await _rootFolderRepository.InsertAsync(new RootFolder
                {
                    Name = chosenName.Trim(),
                    FullPath = path,
                    IsActive = true,
                    IsDefault = RootFolders.Count == 0
                });

                await LoadRootFoldersAsync();
                RootFolderStatusMessage = $"Đã thêm thành công kho '{chosenName}'!";
            }
            catch (Exception ex)
            {
                RootFolderStatusMessage = $"Lỗi thêm kho: {ex.Message}";
                MessageBox.Show($"Lỗi thêm kho: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private async Task BrowseAndUpdatePathAsync(RootFolderDisplayItem? item)
    {
        if (item == null || _rootFolderRepository == null) return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Chọn Thư Mục / Ổ Đĩa Mới Cho '{item.Name}'",
            InitialDirectory = Directory.Exists(item.FullPath) ? item.FullPath : string.Empty
        };

        if (dialog.ShowDialog() != true) return;

        string newPath = dialog.FolderName;
        try
        {
            await _rootFolderRepository.UpdatePathAsync(item.Id, newPath);
            item.FullPath = newPath;
            item.IsPathAccessible = Directory.Exists(newPath);

            if (item.IsDefault)
            {
                RootFolder = newPath;
            }

            RootFolderStatusMessage = $"Đã cập nhật đường dẫn cho '{item.Name}' thành: {newPath}";
            MessageBox.Show($"Đã cập nhật đường dẫn mới cho kho '{item.Name}' thành công!\n\nĐường dẫn mới: {newPath}\n\nToàn bộ lịch sử đơn hàng và hóa đơn của kho này được giữ nguyên 100%.", "Cập Nhật Đường Dẫn Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            RootFolderStatusMessage = $"Lỗi cập nhật đường dẫn: {ex.Message}";
            MessageBox.Show($"Lỗi cập nhật đường dẫn: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RenameRootFolderAsync(RootFolderDisplayItem? item)
    {
        if (item == null || _rootFolderRepository == null) return;

        var ownerWindow = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                          ?? Application.Current?.MainWindow;
        string? newName = Views.InputDialog.Show(ownerWindow!, "Đổi Tên Kho", "Nhập tên mới cho kho dữ liệu:", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name) return;

        try
        {
            var root = await _rootFolderRepository.GetByIdAsync(item.Id);
            if (root != null)
            {
                root.Name = newName.Trim();
                await _rootFolderRepository.UpdateAsync(root);
                item.Name = root.Name;
                RootFolderStatusMessage = $"Đã đổi tên kho thành '{item.Name}'";
            }
        }
        catch (Exception ex)
        {
            RootFolderStatusMessage = $"Lỗi đổi tên kho: {ex.Message}";
            MessageBox.Show($"Lỗi đổi tên: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ToggleActiveRootAsync(RootFolderDisplayItem? item)
    {
        if (item == null || _rootFolderRepository == null) return;

        try
        {
            bool newActive = !item.IsActive;
            await _rootFolderRepository.SetActiveAsync(item.Id, newActive);
            item.IsActive = newActive;
            RootFolderStatusMessage = newActive
                ? $"Đã kích hoạt quét cho '{item.Name}'"
                : $"Đã chuyển '{item.Name}' sang chế độ Lưu Trữ (Không quét ngầm/rescan)";
        }
        catch (Exception ex)
        {
            RootFolderStatusMessage = $"Lỗi thay đổi trạng thái: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SetDefaultRootAsync(RootFolderDisplayItem? item)
    {
        if (item == null || _rootFolderRepository == null) return;

        try
        {
            await _rootFolderRepository.SetDefaultAsync(item.Id);
            await LoadRootFoldersAsync();
            RootFolder = item.FullPath;
            RootFolderStatusMessage = $"Đã đặt '{item.Name}' làm kho mặc định";
        }
        catch (Exception ex)
        {
            RootFolderStatusMessage = $"Lỗi đặt mặc định: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteRootFolderAsync(RootFolderDisplayItem? item)
    {
        if (item == null || _rootFolderRepository == null) return;

        try
        {
            int orderCount = 0;
            if (_orderRepository != null)
            {
                orderCount = await _orderRepository.GetOrderCountByRootFolderIdAsync(item.Id);
            }

            if (orderCount > 0)
            {
                MessageBox.Show(
                    $"Kho '{item.Name}' hiện đang chứa {orderCount:N0} đơn hàng trong cơ sở dữ liệu.\n\n" +
                    "Để bảo toàn tính toàn vẹn của lịch sử hóa đơn và số liệu của xưởng, bạn không thể xóa hoàn toàn kho này.\n\n" +
                    "💡 Giải pháp: Hãy bấm nút 'Tạm ngưng quét' (Tắt hoạt động). Kho sẽ chuyển sang trạng thái Lưu trữ và không bị quét trong các phiên quét mới, trong khi toàn bộ dữ liệu lịch sử vẫn được bảo toàn nguyên vẹn.",
                    "Không Thể Xóa Kho Đang Chứa Đơn Hàng",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            var confirm = MessageBox.Show(
                $"Bạn có chắc chắn muốn xóa cấu hình kho '{item.Name}' ({item.FullPath})?\n\n(Kho này chưa có đơn hàng nào)",
                "Xác Nhận Xóa Kho",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (confirm == MessageBoxResult.Yes)
            {
                await _rootFolderRepository.DeleteAsync(item.Id);
                await LoadRootFoldersAsync();
                RootFolderStatusMessage = $"Đã xóa cấu hình kho '{item.Name}'";
            }
        }
        catch (Exception ex)
        {
            RootFolderStatusMessage = $"Lỗi xóa kho: {ex.Message}";
            MessageBox.Show($"Lỗi: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void BrowseRootFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn Thư Mục Gốc Chứa Các Ngày (Root Folder)",
            InitialDirectory = Directory.Exists(RootFolder) ? RootFolder : string.Empty
        };

        if (dialog.ShowDialog() == true)
        {
            RootFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowseBillExportFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn Thư Mục Xuất Bill",
            InitialDirectory = Directory.Exists(BillExportFolder) ? BillExportFolder : string.Empty
        };

        if (dialog.ShowDialog() == true)
        {
            BillExportFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowseSecondaryBackupFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn Thư Mục Sao Lưu Thứ Cấp (Google Drive / OneDrive / NAS)",
            InitialDirectory = Directory.Exists(SecondaryBackupFolder) ? SecondaryBackupFolder : string.Empty
        };

        if (dialog.ShowDialog() == true)
        {
            SecondaryBackupFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void BrowseWorkshopLogo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn ảnh Logo xưởng in (PNG, JPG)",
            Filter = "Ảnh Logo (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Tất cả các file (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                string brandingDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LalabReports",
                    "Branding"
                );
                Directory.CreateDirectory(brandingDir);

                string ext = Path.GetExtension(dialog.FileName);
                string destPath = Path.Combine(brandingDir, $"workshop_logo{ext}");
                File.Copy(dialog.FileName, destPath, overwrite: true);

                WorkshopLogoPath = destPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không thể sao chép ảnh logo: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                WorkshopLogoPath = dialog.FileName;
            }
        }
    }

    [RelayCommand]
    private void RemoveWorkshopLogo()
    {
        WorkshopLogoPath = null;
    }

    [RelayCommand]
    private void BrowseCustomQrImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn ảnh mã QR thanh toán (PNG, JPG)",
            Filter = "Ảnh QR (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Tất cả các file (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                string brandingDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LalabReports",
                    "Branding"
                );
                Directory.CreateDirectory(brandingDir);

                string ext = Path.GetExtension(dialog.FileName);
                string destPath = Path.Combine(brandingDir, $"custom_qr{ext}");
                File.Copy(dialog.FileName, destPath, overwrite: true);

                CustomQrImagePath = destPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không thể sao chép ảnh QR: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                CustomQrImagePath = dialog.FileName;
            }
        }
    }

    [RelayCommand]
    private void RemoveCustomQrImage()
    {
        CustomQrImagePath = null;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!string.IsNullOrWhiteSpace(AdminPin) && !string.IsNullOrWhiteSpace(StaffPin) && string.Equals(AdminPin.Trim(), StaffPin.Trim(), StringComparison.Ordinal))
        {
            MessageBox.Show("Mã PIN Admin và mã PIN Nhân viên không được trùng nhau.", "Lỗi cấu hình", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var exts = SupportedExtensions
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToList();

        var existingSettings = await _settingsRepository.GetSettingsAsync();

        var settings = new AppSettings
        {
            RootFolder = RootFolder.Trim(),
            BillExportFolder = BillExportFolder.Trim(),
            SecondaryBackupFolder = string.IsNullOrWhiteSpace(SecondaryBackupFolder) ? null : SecondaryBackupFolder.Trim(),
            EnableVietQrOnBill = (QrMode == QrDisplayMode.VietQrAuto),
            QrMode = QrMode,
            BankBinOrCode = SelectedBank?.Bin ?? (string.IsNullOrWhiteSpace(BankBinOrCode) ? "970422" : BankBinOrCode.Trim()),
            BankAccountNumber = BankAccountNumber?.Trim() ?? string.Empty,
            BankAccountName = BankAccountName?.Trim() ?? string.Empty,
            CustomQrImagePath = string.IsNullOrWhiteSpace(CustomQrImagePath) ? null : CustomQrImagePath.Trim(),
            WorkshopName = string.IsNullOrWhiteSpace(WorkshopName) ? "XƯỞNG IN ẢNH CHUYÊN NGHIỆP" : WorkshopName.Trim(),
            WorkshopSlogan = string.IsNullOrWhiteSpace(WorkshopSlogan) ? "Dịch vụ in ấn ảnh & Album chuyên nghiệp" : WorkshopSlogan.Trim(),
            WorkshopPhone = WorkshopPhone?.Trim() ?? string.Empty,
            WorkshopAddress = WorkshopAddress?.Trim() ?? string.Empty,
            InvoiceFooterMessage = string.IsNullOrWhiteSpace(InvoiceFooterMessage) ? "Cảm ơn quý khách đã tin tưởng và ủng hộ dịch vụ!" : InvoiceFooterMessage.Trim(),
            WorkshopLogoPath = string.IsNullOrWhiteSpace(WorkshopLogoPath) ? null : WorkshopLogoPath.Trim(),
            SupportedExtensions = exts,
            AutoScanStartup = AutoScanStartup,
            StartupIncludeYesterday = StartupIncludeYesterday,
            AutoRegisterContextMenu = AutoRegisterContextMenu,
            EnableIdleScan = EnableIdleScan,
            IdleThresholdMinutes = IdleThresholdMinutes,
            IdleScanWindowDays = IdleScanWindowDays,
            EnableMobileServer = EnableMobileServer,
            MobileServerPort = MobileServerPort,
            AdminPin = AdminPin?.Trim() ?? string.Empty,
            StaffPin = StaffPin?.Trim() ?? string.Empty,
            ShowOrderThumbnails = ShowOrderThumbnails,
            EnableThumbnailAutoCleanup = EnableThumbnailAutoCleanup,
            ThumbnailRetentionDays = (ThumbnailRetentionDays == 30 || ThumbnailRetentionDays == 90) ? ThumbnailRetentionDays : 60,

            LastThumbnailCleanupAt = existingSettings.LastThumbnailCleanupAt,
            ThermalPrinterName = (SelectedThermalPrinter == "(Sử dụng máy in mặc định của Windows)" || string.IsNullOrWhiteSpace(SelectedThermalPrinter)) ? null : SelectedThermalPrinter.Trim(),
            ThermalLabelSize = SelectedThermalLabelSize.StartsWith("75x100", StringComparison.OrdinalIgnoreCase) ? ThermalLabelSizes.Size75x100 : ThermalLabelSizes.Size40x100,
            AutoMarkDeliveredOnPrint = AutoMarkDeliveredOnPrint,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            AutoStartWithWindows = AutoStartWithWindows,
            EnableCloudSync = EnableCloudSync,
            CloudSyncApiUrl = string.IsNullOrWhiteSpace(CloudSyncApiUrl) || string.Equals(CloudSyncApiUrl.Trim(), "https://app.tinix.io.vn", StringComparison.OrdinalIgnoreCase)
                ? "https://lalab.tinix.io.vn"
                : CloudSyncApiUrl.Trim(),
            CloudSyncSecret = string.IsNullOrWhiteSpace(CloudSyncSecret) ? "" : CloudSyncSecret.Trim()
        };

        await _settingsRepository.SaveSettingsAsync(settings);
        WindowsStartupHelper.SetAutoStart(AutoStartWithWindows);

        StatusMessage = "Đã lưu cài đặt thành công!";
        SettingsSaved?.Invoke();
        UpdateMobileServerStatusAndQrs();
    }

    [RelayCommand]
    private async Task ExportCloudReconciliationAsync()
    {
        if (_cloudSyncService == null) return;
        var dialog = new SaveFileDialog { Filter = "JSON report|*.json", FileName = "Lalab-cloud-reconciliation.json" };
        if (dialog.ShowDialog() != true) return;
        try { await _cloudSyncService.ExportReconciliationReportAsync(dialog.FileName); CloudSyncStatusMessage = "Đã xuất báo cáo. Xem docs/CLOUD_PROTOCOL_V2.md để chọn từng resolution."; }
        catch (Exception ex) { CloudSyncStatusMessage = "Đối soát chưa hoàn tất: " + ex.Message; }
    }

    [RelayCommand]
    private async Task ApplyCloudReconciliationAsync()
    {
        if (_cloudSyncService == null) return;
        var dialog = new OpenFileDialog { Filter = "JSON report|*.json" };
        if (dialog.ShowDialog() != true) return;
        if (MessageBox.Show("Áp dụng lựa chọn server/desktop của từng field trong báo cáo? Trạng thái thanh toán, giao hàng và ghi chú sẽ thay đổi theo lựa chọn. App tạo backup trước khi áp dụng.", "Xác nhận đối soát", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _backupService.CreateBackupAsync(); await _cloudSyncService.ApplyReviewedReconciliationAsync(dialog.FileName); CloudSyncStatusMessage = "Đã áp dụng đối soát. Bấm Đồng bộ ngay để xác nhận các thao tác pending."; }
        catch (Exception ex) { CloudSyncStatusMessage = "Đối soát chưa hoàn tất: " + ex.Message; }
    }

    private bool CanSyncCloudNow => !IsCloudSyncing;

    [RelayCommand(CanExecute = nameof(CanSyncCloudNow))]
    private async Task SyncCloudNowAsync()
    {
        if (_cloudSyncService == null) return;
        IsCloudSyncing = true;
        CloudSyncStatusMessage = "Đang lưu cấu hình và đồng bộ...";
        try
        {
            // Tự động lưu cấu hình đám mây mới nhất vào CSDL trước khi đồng bộ
            var currentSettings = await _settingsRepository.GetSettingsAsync();
            currentSettings.EnableCloudSync = EnableCloudSync;
            string targetUrl = string.IsNullOrWhiteSpace(CloudSyncApiUrl) || string.Equals(CloudSyncApiUrl.Trim(), "https://app.tinix.io.vn", StringComparison.OrdinalIgnoreCase)
                ? "https://lalab.tinix.io.vn"
                : CloudSyncApiUrl.Trim();
            CloudSyncApiUrl = targetUrl;
            currentSettings.CloudSyncApiUrl = targetUrl;
            currentSettings.CloudSyncSecret = string.IsNullOrWhiteSpace(CloudSyncSecret) ? "" : CloudSyncSecret.Trim();
            await _settingsRepository.SaveSettingsAsync(currentSettings);

            var result = await _cloudSyncService.SyncAllAsync();
            CloudSyncStatusMessage = result.Message;
            if (result.Success)
            {
                var settings = await _settingsRepository.GetSettingsAsync();
                LastCloudSyncAt = settings.LastCloudSyncAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }
        catch (Exception ex)
        {
            CloudSyncStatusMessage = $"Lỗi: {ex.Message}";
        }
        finally
        {
            IsCloudSyncing = false;
        }
    }

    [RelayCommand]
    private async Task ToggleMobileServerAsync()
    {
        if (_mobileWebServer == null) return;
        if (_mobileWebServer.IsRunning)
        {
            await _mobileWebServer.StopAsync();
        }
        else
        {
            await _mobileWebServer.StartAsync();
        }
        UpdateMobileServerStatusAndQrs();
    }

    [RelayCommand]
    private void RefreshMobileQrs()
    {
        UpdateMobileServerStatusAndQrs();
    }

    [RelayCommand]
    private void CopyAdminUrl()
    {
        string targetBaseUrl = (PreferRemoteUrlForQr && !string.IsNullOrWhiteSpace(RemoteMobileUrl))
            ? RemoteMobileUrl
            : LocalMobileUrl;
        if (string.IsNullOrWhiteSpace(targetBaseUrl) || _authService == null) return;
        string token = _authService.GenerateToken(MobileUserRole.Admin);
        Clipboard.SetText(LalabAutoReport.Core.Services.MobileAccessLink.Create(targetBaseUrl, token, PreferRemoteUrlForQr && !string.IsNullOrWhiteSpace(RemoteMobileUrl)));
        StatusMessage = "Đã sao chép link đăng nhập Admin vào bộ nhớ tạm!";
    }

    [RelayCommand]
    private void CopyStaffUrl()
    {
        string targetBaseUrl = (PreferRemoteUrlForQr && !string.IsNullOrWhiteSpace(RemoteMobileUrl))
            ? RemoteMobileUrl
            : LocalMobileUrl;
        if (string.IsNullOrWhiteSpace(targetBaseUrl) || _authService == null) return;
        string token = _authService.GenerateToken(MobileUserRole.Staff);
        Clipboard.SetText(LalabAutoReport.Core.Services.MobileAccessLink.Create(targetBaseUrl, token, PreferRemoteUrlForQr && !string.IsNullOrWhiteSpace(RemoteMobileUrl)));
        StatusMessage = "Đã sao chép link đăng nhập Nhân viên vào bộ nhớ tạm!";
    }

    public void UpdateMobileServerStatusAndQrs()
    {
        if (_mobileWebServer == null) return;

        var status = _mobileWebServer.GetStatus();
        LocalMobileUrl = status.LocalUrl ?? $"http://{LalabAutoReport.Infrastructure.Services.NetworkAddressHelper.GetLocalIpAddress()}:{MobileServerPort}";
        
        RemoteMobileUrl = !string.IsNullOrWhiteSpace(CloudSyncApiUrl)
            ? NormalizeApiUrl(CloudSyncApiUrl)
            : string.Empty;

        if (status.IsRunning)
        {
            MobileServerStatusText = $"Đang hoạt động (Cổng {status.Port})";
        }
        else if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
        {
            MobileServerStatusText = $"Lỗi: {status.ErrorMessage}";
        }
        else
        {
            MobileServerStatusText = "Đã tạm dừng";
        }

        _systemTrayManager?.UpdateStatus(MobileServerStatusText);
        _systemTrayManager?.UpdateRemoteUrl(!string.IsNullOrWhiteSpace(RemoteMobileUrl) ? RemoteMobileUrl : LocalMobileUrl);

        GenerateMobileQrCodes();
    }

    private void GenerateMobileQrCodes()
    {
        if (_authService == null) return;

        string targetBaseUrl = (PreferRemoteUrlForQr && !string.IsNullOrWhiteSpace(RemoteMobileUrl))
            ? RemoteMobileUrl
            : LocalMobileUrl;

        if (string.IsNullOrWhiteSpace(targetBaseUrl)) return;

        try
        {
            string adminToken = _authService.GenerateToken(MobileUserRole.Admin);
            string staffToken = _authService.GenerateToken(MobileUserRole.Staff);

            string adminUrl = LalabAutoReport.Core.Services.MobileAccessLink.Create(targetBaseUrl, adminToken, PreferRemoteUrlForQr && !string.IsNullOrWhiteSpace(RemoteMobileUrl));
            string staffUrl = LalabAutoReport.Core.Services.MobileAccessLink.Create(targetBaseUrl, staffToken, PreferRemoteUrlForQr && !string.IsNullOrWhiteSpace(RemoteMobileUrl));

            AdminQrCodeImage = RenderQrToImageSource(adminUrl);
            StaffQrCodeImage = RenderQrToImageSource(staffUrl);
        }
        catch { }
    }

    private static System.Windows.Media.ImageSource? RenderQrToImageSource(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        using var qrGen = new QRCoder.QRCodeGenerator();
        using var qrData = qrGen.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.M);
        using var pngQr = new QRCoder.PngByteQRCode(qrData);
        byte[] pngBytes = pngQr.GetGraphic(15);

        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        using var stream = new MemoryStream(pngBytes);
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    [RelayCommand]
    public void RefreshPrinters()
    {
        try
        {
            var server = new System.Printing.LocalPrintServer();
            var queues = server.GetPrintQueues(new[] {
                System.Printing.EnumeratedPrintQueueTypes.Local,
                System.Printing.EnumeratedPrintQueueTypes.Connections
            });

            string current = SelectedThermalPrinter;
            AvailablePrinters.Clear();
            AvailablePrinters.Add("(Sử dụng máy in mặc định của Windows)");
            foreach (var q in queues)
            {
                if (!AvailablePrinters.Contains(q.FullName))
                {
                    AvailablePrinters.Add(q.FullName);
                }
            }

            if (!string.IsNullOrWhiteSpace(current) && AvailablePrinters.Contains(current))
            {
                SelectedThermalPrinter = current;
            }
            else if (AvailablePrinters.Count > 0)
            {
                SelectedThermalPrinter = AvailablePrinters[0];
            }
        }
        catch
        {
            if (AvailablePrinters.Count == 0)
            {
                AvailablePrinters.Add("(Sử dụng máy in mặc định của Windows)");
                SelectedThermalPrinter = AvailablePrinters[0];
            }
        }
    }

    [RelayCommand]
    private async Task TestPrintThermalAsync()
    {
        var exporter = _shippingLabelExporter ?? (Application.Current as App)?.Services?.GetService(typeof(IShippingLabelExporter)) as IShippingLabelExporter;
        if (exporter == null)
        {
            MessageBox.Show("Dịch vụ in tem nhiệt chưa sẵn sàng.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsTestPrinting = true;
        string labelSizeToTest = SelectedThermalLabelSize.StartsWith("75x100", StringComparison.OrdinalIgnoreCase)
            ? ThermalLabelSizes.Size75x100
            : ThermalLabelSizes.Size40x100;
        TestPrintStatusMessage = $"Đang gửi lệnh in tem mẫu ({labelSizeToTest})...";

        try
        {
            string? targetPrinter = (SelectedThermalPrinter == "(Sử dụng máy in mặc định của Windows)" || string.IsNullOrWhiteSpace(SelectedThermalPrinter))
                ? null
                : SelectedThermalPrinter;

            await exporter.PrintTestSampleLabelAsync(targetPrinter, labelSizeToTest);
            TestPrintStatusMessage = $"✅ Đã gửi lệnh in tem mẫu {labelSizeToTest} tới máy in!";
            MessageBox.Show(
                $"Lệnh in tem kiểm tra ({labelSizeToTest}) đã được gửi tới máy in thành công!\n\nVui lòng kiểm tra tem in ra từ máy in nhiệt của bạn.",
                "In Tem Mẫu Thành Công",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            TestPrintStatusMessage = $"❌ Lỗi in tem: {ex.Message}";
            MessageBox.Show($"Lỗi khi in tem mẫu: {ex.Message}", "Lỗi In Tem", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsTestPrinting = false;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        string dir = Path.GetDirectoryName(DatabasePath) ?? string.Empty;
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
                Verb = "open"
            });
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        try
        {
            IsBackingUp = true;
            BackupStatusMessage = "Đang sao lưu cơ sở dữ liệu...";
            var result = await _backupService.CreateBackupAsync();
            BackupStatusMessage = $"Sao lưu thành công: {result.FileName} ({result.FileSizeBytes / 1024:N0} KB)";
            await LoadBackupsAsync();
        }
        catch (Exception ex)
        {
            BackupStatusMessage = $"Sao lưu thất bại: {ex.Message}";
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(DatabaseBackupInfo? backup)
    {
        if (backup == null) return;

        var result = MessageBox.Show(
            $"Bạn có chắc chắn muốn khôi phục dữ liệu từ bản sao lưu:\n\n{backup.FileName}\n\n(Dữ liệu hiện tại sẽ được tự động sao lưu an toàn trước khi khôi phục)?",
            "Xác nhận Khôi Phục Dữ Liệu",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (result != MessageBoxResult.Yes) return;

        try
        {
            IsBackingUp = true;
            BackupStatusMessage = "Đang khôi phục dữ liệu...";
            await _backupService.RestoreBackupAsync(backup.BackupPath);
            BackupStatusMessage = $"Khôi phục thành công từ {backup.FileName}! Khởi động lại ứng dụng hoặc tải lại màn hình để cập nhật.";
            await LoadBackupsAsync();
            MessageBox.Show("Khôi phục cơ sở dữ liệu thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            BackupStatusMessage = $"Khôi phục thất bại: {ex.Message}";
            MessageBox.Show($"Khôi phục thất bại: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    [RelayCommand]
    private async Task BrowseAndRestoreAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Chọn file sao lưu SQLite (*.db)",
            Filter = "SQLite Database (*.db)|*.db|Tất cả tệp (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            var info = new DatabaseBackupInfo(
                BackupPath: dialog.FileName,
                FileName: Path.GetFileName(dialog.FileName),
                FileSizeBytes: new FileInfo(dialog.FileName).Length,
                CreatedAtUtc: File.GetCreationTimeUtc(dialog.FileName)
            );
            await RestoreBackupAsync(info);
        }
    }

    [RelayCommand]
    private async Task ResetDataAsync()
    {
        if (_resetService == null) return;

        var ownerWindow = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                          ?? Application.Current?.MainWindow;
        var (confirmed, scope) = Views.ResetConfirmationDialog.Show(ownerWindow);
        if (!confirmed) return;

        try
        {
            IsBackingUp = true;
            BackupStatusMessage = "Đang tạo bản sao lưu an toàn và tiến hành đặt lại dữ liệu...";

            var backupInfo = await _resetService.ResetDataAsync(scope);

            await LoadSettingsAsync();
            await LoadBackupsAsync();

            BackupStatusMessage = $"Đã đặt lại dữ liệu thành công! Bản sao lưu an toàn: {backupInfo.FileName}";

            DataResetCompleted?.Invoke();

            string scopeName = scope == ResetDataScope.OperationalOnly
                ? "Dữ liệu vận hành (đơn hàng, lịch sử quét, hóa đơn, trạng thái đã in) đã được xóa sạch. Danh mục Khách hàng và Bảng giá được giữ nguyên 100%."
                : "Toàn bộ cơ sở dữ liệu đã được đưa về trạng thái xuất xưởng ban đầu.";

            MessageBox.Show(
                $"Đã hoàn tất đặt lại dữ liệu!\n\n{scopeName}\n\n🛡️ Bản sao lưu an toàn đã được lưu tại:\n{backupInfo.FileName}\n\nỨng dụng đã tự động làm mới toàn bộ dữ liệu.",
                "Đặt Lại Dữ Liệu Thành Công",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
        catch (Exception ex)
        {
            BackupStatusMessage = $"Đặt lại dữ liệu thất bại: {ex.Message}";
            MessageBox.Show($"Đặt lại dữ liệu thất bại: {ex.Message}", "Lỗi Đặt Lại Dữ Liệu", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    partial void OnAutoRegisterContextMenuChanged(bool value)
    {
        if (_isLoadingSettings) return;

        if (value)
        {
            try
            {
                _contextMenuService.RegisterContextMenu();
                IsContextMenuRegistered = _contextMenuService.IsContextMenuRegistered();
                ContextMenuStatusMessage = "Đã kích hoạt menu chuột phải '⚡ QUICK BILL (Lalab)'!";
            }
            catch (Exception ex)
            {
                ContextMenuStatusMessage = $"Lỗi kích hoạt menu chuột phải: {ex.Message}";
            }
        }
        else
        {
            try
            {
                _contextMenuService.UnregisterContextMenu();
                IsContextMenuRegistered = _contextMenuService.IsContextMenuRegistered();
                ContextMenuStatusMessage = "Đã tắt và gỡ bỏ menu chuột phải khỏi Windows Explorer.";
            }
            catch (Exception ex)
            {
                ContextMenuStatusMessage = $"Lỗi gỡ bỏ menu chuột phải: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void RegisterContextMenu()
    {
        try
        {
            _contextMenuService.RegisterContextMenu();
            IsContextMenuRegistered = _contextMenuService.IsContextMenuRegistered();
            if (!AutoRegisterContextMenu)
            {
                AutoRegisterContextMenu = true;
            }
            ContextMenuStatusMessage = "Đã kích hoạt thành công menu chuột phải '⚡ QUICK BILL (Lalab)'!";
        }
        catch (Exception ex)
        {
            ContextMenuStatusMessage = $"Lỗi kích hoạt menu chuột phải: {ex.Message}";
            MessageBox.Show(ContextMenuStatusMessage, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void UnregisterContextMenu()
    {
        try
        {
            _contextMenuService.UnregisterContextMenu();
            IsContextMenuRegistered = _contextMenuService.IsContextMenuRegistered();
            if (AutoRegisterContextMenu)
            {
                AutoRegisterContextMenu = false;
            }
            ContextMenuStatusMessage = "Đã gỡ bỏ menu chuột phải khỏi File Explorer.";
        }
        catch (Exception ex)
        {
            ContextMenuStatusMessage = $"Lỗi gỡ bỏ menu chuột phải: {ex.Message}";
            MessageBox.Show(ContextMenuStatusMessage, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ExportRegFile()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Xuất file Registry cài đặt Menu Chuột Phải",
                Filter = "Registry Files (*.reg)|*.reg",
                FileName = "Lalab_QuickBill_ContextMenu.reg"
            };

            if (dialog.ShowDialog() == true)
            {
                var content = _contextMenuService.GenerateRegFileContent();
                File.WriteAllText(dialog.FileName, content, System.Text.Encoding.Unicode);
                MessageBox.Show($"Đã xuất file .reg thành công:\n{dialog.FileName}\n\nBạn có thể copy file này sang máy tính khác trong xưởng và double-click để cài đặt nhanh!", "Xuất File Registry", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi xuất file .reg: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public async Task CheckForUpdateAsync()
    {
        if (_updateService == null)
        {
            UpdateStatusMessage = "Chức năng cập nhật chưa được khởi tạo.";
            return;
        }

        IsCheckingUpdate = true;
        UpdateStatusMessage = "Đang kiểm tra bản cập nhật mới trên GitHub...";

        try
        {
            var updateInfo = await _updateService.CheckForUpdateAsync();
            if (updateInfo.IsUpdateAvailable)
            {
                UpdateStatusMessage = $"Đã có bản cập nhật mới: {updateInfo.LatestVersion}!";
                var updateVm = new UpdateViewModel(_updateService);
                updateVm.Initialize(updateInfo);
                var dialog = new Views.UpdateDialog(updateVm)
                {
                    Owner = Application.Current.MainWindow
                };
                dialog.ShowDialog();
            }
            else
            {
                UpdateStatusMessage = $"Hệ thống đang ở phiên bản mới nhất ({updateInfo.CurrentVersion}).";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"Lỗi kiểm tra cập nhật: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    partial void OnPurgePeriodPresetChanged(string value)
    {
        HasPurgePreview = false;
        PurgePreviewSummary = string.Empty;
        PurgeStatusMessage = string.Empty;

        if (value == "6Months")
        {
            PurgeCutoffDate = DateTime.Today.AddMonths(-6);
        }
        else if (value == "1Year")
        {
            PurgeCutoffDate = DateTime.Today.AddYears(-1);
        }
    }

    partial void OnPurgeCutoffDateChanged(DateTime value)
    {
        HasPurgePreview = false;
        PurgePreviewSummary = string.Empty;
        PurgeStatusMessage = string.Empty;
    }

    [RelayCommand]
    private async Task PreviewPurgeAsync()
    {
        if (_purgeService == null)
        {
            PurgeStatusMessage = "Dịch vụ dọn dẹp chưa sẵn sàng.";
            return;
        }

        try
        {
            IsPurging = true;
            PurgeStatusMessage = "Đang kiểm tra và tổng hợp số lượng dữ liệu...";
            var preview = await _purgeService.GetPurgePreviewAsync(PurgeCutoffDate);

            PurgePreviewSummary = $"Mốc thời gian: Trước ngày {PurgeCutoffDate:dd/MM/yyyy}\n" +
                                   $"• Số đơn hàng cũ: {preview.OrderCount:N0} đơn\n" +
                                   $"• Số hóa đơn cũ: {preview.BillCount:N0} hóa đơn\n" +
                                   $"• Số dòng quy cách chi tiết: {preview.OrderItemCount:N0} dòng\n" +
                                   $"• Số bản ghi scan snapshot: {preview.SnapshotCount:N0} bản ghi";

            HasPurgePreview = true;

            if (preview.OrderCount == 0 && preview.BillCount == 0)
            {
                PurgeStatusMessage = "Không có dữ liệu vận hành nào trước ngày này.";
            }
            else
            {
                PurgeStatusMessage = "Đã sẵn sàng. Bấm nút 'Bắt Đầu Dọn Dẹp' bên dưới để thực hiện.";
            }
        }
        catch (Exception ex)
        {
            PurgeStatusMessage = $"Lỗi kiểm tra dữ liệu: {ex.Message}";
        }
        finally
        {
            IsPurging = false;
        }
    }

    [RelayCommand]
    private async Task ExecutePurgeAsync()
    {
        if (_purgeService == null) return;

        var confirm = MessageBox.Show(
            $"XÁC NHẬN DỌN DẸP DỮ LIỆU CŨ TRƯỚC NGÀY {PurgeCutoffDate:dd/MM/yyyy}:\n\n" +
            $"{PurgePreviewSummary}\n\n" +
            "🛡️ HỆ THỐNG SẼ TỰ ĐỘNG TẠO BẢN SAO LƯU AN TOÀN TRƯỚC KHI THỰC HIỆN.\n" +
            "Danh mục Khách hàng, Bảng giá, Quy cách in sẽ được BẢO TOÀN NGUYÊN VẸN 100%.\n\n" +
            "Bạn có chắc chắn muốn tiến hành dọn dẹp không?",
            "Xác Nhận Dọn Dẹp Dữ Liệu Vận Hành Cũ",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            IsPurging = true;
            PurgeStatusMessage = "Đang tạo bản sao lưu an toàn và dọn dẹp cơ sở dữ liệu...";

            var result = await _purgeService.PurgeOperationalDataBeforeDateAsync(PurgeCutoffDate);

            PurgeStatusMessage = $"Đã dọn dẹp thành công {result.OrdersDeleted:N0} đơn hàng và {result.BillsDeleted:N0} hóa đơn!";
            HasPurgePreview = false;
            PurgePreviewSummary = string.Empty;

            await LoadBackupsAsync();
            DataResetCompleted?.Invoke();

            MessageBox.Show(
                $"ĐÃ HOÀN TẤT DỌN DẸP DỮ LIỆU!\n\n" +
                $"• Đơn hàng đã dọn: {result.OrdersDeleted:N0}\n" +
                $"• Hóa đơn đã dọn: {result.BillsDeleted:N0}\n\n" +
                $"🛡️ Bản sao lưu an toàn trước khi dọn đã được lưu tại:\n{result.BackupFilePath}\n\n" +
                $"Dung lượng file SQLite đã được tối ưu hóa và chống phân mảnh (VACUUM) thành công!",
                "Dọn Dẹp Thành Công",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
        catch (Exception ex)
        {
            PurgeStatusMessage = $"Lỗi dọn dẹp dữ liệu: {ex.Message}";
            MessageBox.Show($"Lỗi dọn dẹp: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsPurging = false;
        }
    }

    public void RefreshThumbnailCacheSize()
    {
        try
        {
            if (_thumbnailService != null)
            {
                long bytes = _thumbnailService.GetDiskCacheSizeBytes();
                if (bytes < 1024)
                {
                    ThumbnailCacheSizeText = $"{bytes} B";
                }
                else if (bytes < 1024 * 1024)
                {
                    ThumbnailCacheSizeText = $"{bytes / 1024.0:F1} KB";
                }
                else
                {
                    ThumbnailCacheSizeText = $"{bytes / (1024.0 * 1024.0):F1} MB";
                }
            }
            else
            {
                ThumbnailCacheSizeText = "0 MB";
            }
        }
        catch
        {
            ThumbnailCacheSizeText = "0 MB";
        }
    }

    [RelayCommand]
    private async Task CleanupExpiredThumbnailsNowAsync()
    {
        if (_thumbnailService == null) return;
        try
        {
            int days = (ThumbnailRetentionDays == 30 || ThumbnailRetentionDays == 90) ? ThumbnailRetentionDays : 60;
            var summary = await _thumbnailService.CleanupExpiredDiskCacheAsync(TimeSpan.FromDays(days));
            RefreshThumbnailCacheSize();
            string msg = $"Đã dọn dẹp xong: Đã xóa {summary.FilesDeleted} ảnh thumbnail quá hạn ({summary.BytesFreed / (1024.0 * 1024.0):F2} MB)!";

            var settings = await _settingsRepository.GetSettingsAsync();
            if (settings.EnableCloudSync && _cloudSyncService != null)
            {
                try
                {
                    bool cloudCleaned = await _cloudSyncService.CleanupCloudGalleryCacheAsync(days, clearAll: false);
                    if (cloudCleaned)
                    {
                        msg += " (Đồng thời đã dọn ảnh quá hạn trên Cloud)";
                    }
                }
                catch (Exception cloudEx)
                {
                    msg += $" (Dọn Cloud chưa hoàn tất: {cloudEx.Message})";
                }
            }

            settings.LastThumbnailCleanupAt = DateTimeOffset.UtcNow.ToString("o");
            await _settingsRepository.SaveSettingsAsync(settings);

            ThumbnailCacheStatusMessage = msg;
        }
        catch (Exception ex)
        {
            ThumbnailCacheStatusMessage = $"Lỗi dọn thumbnail quá hạn: {ex.Message}";
        }
    }

    public Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> ClearCacheConfirmFunc { get; set; } = MessageBox.Show;

    [RelayCommand]
    private async Task ClearThumbnailCacheAsync()
    {
        var result = ClearCacheConfirmFunc(
            "Bạn có chắc chắn muốn xóa toàn bộ bộ nhớ đệm hình thu nhỏ không?\n\n" +
            "• Bấm 'Yes' để xóa cả bộ nhớ đệm trên máy tính này và dọn dẹp ảnh trên Cloud.\n" +
            "• Bấm 'No' để chỉ xóa bộ nhớ đệm trên máy tính này.\n" +
            "• Bấm 'Cancel' để hủy thao tác.",
            "Xác nhận xóa bộ nhớ đệm hình thu nhỏ",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel) return;

        bool clearCloud = (result == MessageBoxResult.Yes);

        if (_thumbnailService != null)
        {
            try
            {
                var summary = await _thumbnailService.ClearAllDiskCacheAsync();
                RefreshThumbnailCacheSize();
                string msg = $"Đã xóa sạch bộ nhớ đệm máy tính: {summary.FilesDeleted} file ({summary.BytesFreed / (1024.0 * 1024.0):F2} MB)!";

                if (clearCloud)
                {
                    try
                    {
                        var settings = await _settingsRepository.GetSettingsAsync();
                        if (settings.EnableCloudSync && _cloudSyncService != null)
                        {
                            int retentionDays = (ThumbnailRetentionDays == 30 || ThumbnailRetentionDays == 90) ? ThumbnailRetentionDays : 60;
                            bool cloudCleaned = await _cloudSyncService.CleanupCloudGalleryCacheAsync(retentionDays, clearAll: true);
                            if (cloudCleaned)
                            {
                                msg += " (Đồng thời đã dọn sạch ảnh trên Cloud)";
                            }
                        }
                    }
                    catch (Exception cloudEx)
                    {
                        msg += $" (Dọn Cloud chưa hoàn tất: {cloudEx.Message})";
                    }
                }

                ThumbnailCacheStatusMessage = msg;
            }
            catch (Exception ex)
            {
                ThumbnailCacheStatusMessage = $"Lỗi xóa bộ nhớ đệm: {ex.Message}";
            }
        }
    }

    private static string NormalizeApiUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;
        string url = rawUrl.Trim().TrimEnd('/');
        if (string.Equals(url, "https://app.tinix.io.vn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(url, "http://app.tinix.io.vn", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://lalab.tinix.io.vn";
        }
        else if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }
        return url;
    }
}
