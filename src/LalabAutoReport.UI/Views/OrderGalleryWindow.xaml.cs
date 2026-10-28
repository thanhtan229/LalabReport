using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;

namespace LalabAutoReport.UI.Views;

public partial class OrderGalleryWindow : Window
{
    private readonly long _orderId;
    private readonly IOrderGalleryService _galleryService;
    private readonly IOrderRepository _orderRepository;
    private readonly IRootFolderRepository? _rootFolderRepository;
    private readonly ISettingsRepository _settingsRepository;

    private CancellationTokenSource? _loadCts;
    private OrderGalleryManifest? _manifest;
    private int _loadedCount = 0;
    private const int BatchSize = 24;

    // Bounded thumbnail window: only cards near the viewport hold decoded bitmaps.
    private const int DecodeConcurrency = 2;
    private const int PrefetchScreens = 1;
    private const int UnloadScreens = 2;

    private readonly List<GalleryCardViewItem> _cards = new();
    private readonly Dictionary<string, GalleryCardViewItem> _cardsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<GalleryCardViewItem> _decodeQueue = new();
    private readonly HashSet<string> _queuedFileIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _decodingFileIds;
    private readonly object _decodeLock = new();
    private readonly List<Task> _decodeWorkers = new();

    private sealed class DecodeSession
    {
        public CancellationTokenSource Cts { get; } = new();
        public SemaphoreSlim Signal { get; } = new(0);
        public HashSet<string> DecodingFileIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private DecodeSession? _currentSession;
    private DispatcherTimer? _scrollDebounce;
    private bool _viewportUpdateQueued;

    public ObservableCollection<GalleryGroupViewItem> Groups { get; } = new();

    public OrderGalleryWindow(
        long orderId,
        IOrderGalleryService galleryService,
        IOrderRepository orderRepository,
        ISettingsRepository settingsRepository,
        IRootFolderRepository? rootFolderRepository = null)
    {
        InitializeComponent();
        _orderId = orderId;
        _galleryService = galleryService;
        _orderRepository = orderRepository;
        _settingsRepository = settingsRepository;
        _rootFolderRepository = rootFolderRepository;

        icGroups.ItemsSource = Groups;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadManifestAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        StopDecodeWorkers();
        _scrollDebounce?.Stop();
        _scrollDebounce = null;

        foreach (var group in Groups)
        {
            foreach (var card in group.DisplayItems)
            {
                card.ImageSource = null;
                card.DetachContainer();
            }
            group.DisplayItems.Clear();
        }
        Groups.Clear();
        _cards.Clear();
        _cardsById.Clear();

        base.OnClosed(e);
    }

    private void StopDecodeWorkers()
    {
        if (_currentSession != null)
        {
            try { _currentSession.Cts.Cancel(); } catch { }
            try { _currentSession.Signal.Release(DecodeConcurrency); } catch { }
            _currentSession.Cts.Dispose();
            _currentSession.Signal.Dispose();
            _currentSession = null;
        }
        _decodeWorkers.Clear();

        lock (_decodeLock)
        {
            _queuedFileIds.Clear();
            _decodingFileIds?.Clear();
            _decodingFileIds = null;
        }
        while (_decodeQueue.TryDequeue(out _)) { }
    }

    private void StartDecodeWorkers()
    {
        StopDecodeWorkers();

        var session = new DecodeSession();
        _currentSession = session;
        _decodingFileIds = session.DecodingFileIds;

        for (int i = 0; i < DecodeConcurrency; i++)
        {
            _decodeWorkers.Add(Task.Run(() => DecodeWorkerAsync(session), CancellationToken.None));
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private async Task LoadManifestAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        pnlLoading.Visibility = Visibility.Visible;
        pnlEmpty.Visibility = Visibility.Collapsed;
        scrollViewer.Visibility = Visibility.Collapsed;
        foreach (var group in Groups)
        {
            foreach (var card in group.DisplayItems)
            {
                card.ImageSource = null;
                card.DetachContainer();
            }
            group.DisplayItems.Clear();
        }
        Groups.Clear();
        _cards.Clear();
        _cardsById.Clear();
        _loadedCount = 0;
        StartDecodeWorkers();

        try
        {
            var order = await _orderRepository.GetOrderByIdAsync(_orderId, ct);
            if (order != null)
            {
                txtOrderCode.Text = $"[{order.OrderCode}]";
                txtOrderName.Text = $"{order.Customer?.CanonicalName ?? order.OriginalFolderName} • {order.RelativePath}";
            }

            _manifest = await _galleryService.GetOrderManifestAsync(_orderId, ct);

            txtTotalCount.Text = $"{_manifest.TotalImageCount} ảnh";
            if (!string.IsNullOrWhiteSpace(_manifest.Notice))
            {
                bannerNotice.Visibility = Visibility.Visible;
                txtNotice.Text = _manifest.Notice;
            }
            else
            {
                bannerNotice.Visibility = Visibility.Collapsed;
            }

            if (_manifest.TotalImageCount == 0 && !_manifest.HasUnresolvedFolders)
            {
                pnlLoading.Visibility = Visibility.Collapsed;
                pnlEmpty.Visibility = Visibility.Visible;
                UpdateStatus();
                return;
            }

            // Create group structures
            foreach (var g in _manifest.Groups)
            {
                var groupVm = new GalleryGroupViewItem
                {
                    OrderItemId = g.OrderItemId,
                    SpecificationName = g.SpecificationName,
                    FolderDisplayName = string.IsNullOrWhiteSpace(g.SelectedPrintFolderRelativePath) ? "Thư mục: (Chưa chọn)" : $"Thư mục: {Path.GetFileName(g.SelectedPrintFolderRelativePath)}",
                    CountText = $"{g.ImageCount} ảnh",
                    HasWarning = !g.IsFolderResolved,
                    WarningMessage = g.ResolutionMessage ?? string.Empty
                };
                Groups.Add(groupVm);
            }

            pnlLoading.Visibility = Visibility.Collapsed;
            scrollViewer.Visibility = Visibility.Visible;

            // Load first page of 24 images
            await LoadNextBatchAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            pnlLoading.Visibility = Visibility.Collapsed;
            bannerNotice.Visibility = Visibility.Visible;
            txtNotice.Text = $"Lỗi tải thư viện ảnh: {ex.Message}";
        }
    }

    private async Task LoadNextBatchAsync()
    {
        if (_manifest == null) return;

        btnLoadMore.IsEnabled = false;
        try
        {
            var page = await _galleryService.GetOrderGalleryPageAsync(
                _orderId,
                _manifest.ManifestVersion,
                offset: _loadedCount,
                limit: BatchSize,
                (_loadCts?.Token ?? CancellationToken.None));

            foreach (var dto in page.Items)
            {
                var targetGroup = Groups.FirstOrDefault(g => g.OrderItemId == dto.OrderItemId);
                if (targetGroup != null)
                {
                    var card = new GalleryCardViewItem
                    {
                        FileId = dto.FileId,
                        OrderItemId = dto.OrderItemId,
                        FileName = dto.FileName,
                        FileSizeBytes = dto.FileSizeBytes,
                        ManifestVersion = page.ManifestVersion,
                        TooltipText = $"{dto.FileName}\nKích thước: {FormatBytes(dto.FileSizeBytes)}"
                    };
                    targetGroup.DisplayItems.Add(card);
                    _cards.Add(card);
                    _cardsById[card.FileId] = card;
                    _loadedCount++;
                }
            }

            btnLoadMore.Visibility = page.HasMore ? Visibility.Visible : Visibility.Collapsed;
            UpdateStatus();

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            QueueVisibleAndPrefetch();
            UnloadFarCards();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi tải thêm ảnh: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            btnLoadMore.IsEnabled = true;
        }
    }

    private void ImageCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is GalleryCardViewItem card)
        {
            card.Container = elem;
            RequestViewportUpdate();
        }
    }

    private void ImageCard_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is GalleryCardViewItem card)
        {
            if (ReferenceEquals(card.Container, elem))
            {
                card.IsNearViewport = false;
                card.ImageSource = null;
                card.IsLoading = true;
                card.DetachContainer();
            }
        }
    }

    private void GalleryScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scrollDebounce == null)
        {
            _scrollDebounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _scrollDebounce.Tick += (_, _) =>
            {
                _scrollDebounce?.Stop();
                QueueVisibleAndPrefetch();
                UnloadFarCards();
            };
        }

        _scrollDebounce.Stop();
        _scrollDebounce.Start();

        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            RequestViewportUpdate();
        }
    }

    private void RequestViewportUpdate()
    {
        if (_viewportUpdateQueued) return;
        _viewportUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _viewportUpdateQueued = false;
            QueueVisibleAndPrefetch();
            UnloadFarCards();
        }));
    }

    private double ResolveViewportHeight()
    {
        double viewportHeight = scrollViewer.ViewportHeight;
        if (double.IsNaN(viewportHeight) || viewportHeight <= 0) viewportHeight = scrollViewer.ActualHeight;
        if (double.IsNaN(viewportHeight) || viewportHeight <= 0) viewportHeight = Math.Max(ActualHeight, 600);
        return viewportHeight;
    }

    private bool TryGetCardTop(GalleryCardViewItem card, out double top, out double height)
    {
        top = 0;
        height = 0;
        var container = card.Container;
        if (container == null) return false;

        try
        {
            top = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0)).Y;
            height = container.ActualHeight;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void QueueVisibleAndPrefetch()
    {
        if (_manifest == null || _cards.Count == 0) return;

        double viewportHeight = ResolveViewportHeight();
        double prefetchTop = -viewportHeight * PrefetchScreens;
        double prefetchBottom = viewportHeight * (1 + PrefetchScreens);

        var candidates = new List<(GalleryCardViewItem Card, bool Visible, double Distance)>();

        foreach (var card in _cards)
        {
            if (!TryGetCardTop(card, out double top, out double height)) continue;

            double bottom = top + height;
            bool near = bottom >= prefetchTop && top <= prefetchBottom;
            card.IsNearViewport = near;

            if (!near || card.ImageSource != null || card.IsError) continue;

            bool visible = top < viewportHeight && bottom > 0;
            double distance = Math.Abs(((top + bottom) / 2) - (viewportHeight / 2));
            candidates.Add((card, visible, distance));
        }

        int prefetchBudget = 12;

        foreach (var candidate in candidates.OrderBy(c => c.Visible ? 0 : 1).ThenBy(c => c.Distance))
        {
            if (!candidate.Visible)
            {
                if (prefetchBudget <= 0) break;
                prefetchBudget--;
            }
            EnqueueForDecode(candidate.Card);
        }
    }

    private void UnloadFarCards()
    {
        if (_manifest == null) return;

        double viewportHeight = ResolveViewportHeight();
        double keepTop = -viewportHeight * UnloadScreens;
        double keepBottom = viewportHeight * (1 + UnloadScreens);

        foreach (var card in _cards)
        {
            if (card.ImageSource == null) continue;
            if (!TryGetCardTop(card, out double top, out double height)) continue;

            if (top + height >= keepTop && top <= keepBottom) continue;

            lock (_decodeLock)
            {
                if (_decodingFileIds != null && _decodingFileIds.Contains(card.FileId)) continue;
            }

            card.ImageSource = null;
            card.IsLoading = true;
        }
    }

    private void EnqueueForDecode(GalleryCardViewItem card)
    {
        if (card.ImageSource != null || card.IsError) return;

        var session = _currentSession;
        if (session == null || session.Cts.IsCancellationRequested) return;

        lock (_decodeLock)
        {
            if (!_queuedFileIds.Add(card.FileId)) return;
        }

        _decodeQueue.Enqueue(card);
        try { session.Signal.Release(); }
        catch (ObjectDisposedException) { }
        catch (SemaphoreFullException) { }
    }

    private async Task DecodeWorkerAsync(DecodeSession session)
    {
        var signal = session.Signal;
        var ct = session.Cts.Token;
        var decodingFileIds = session.DecodingFileIds;

        while (!ct.IsCancellationRequested)
        {
            try { await signal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            if (!_decodeQueue.TryDequeue(out var card)) continue;

            lock (_decodeLock) { _queuedFileIds.Remove(card.FileId); }

            if (ct.IsCancellationRequested) return;
            if (card.ImageSource != null || card.IsError || !card.IsNearViewport) continue;

            lock (_decodeLock)
            {
                if (!decodingFileIds.Add(card.FileId)) continue;
            }

            try
            {
                var result = await _galleryService
                    .GetGalleryThumbnailAsync(_orderId, card.ManifestVersion, card.FileId, ct)
                    .ConfigureAwait(false);

                if (ct.IsCancellationRequested) return;

                if (result.Status == ThumbnailStatus.Success
                    && !string.IsNullOrWhiteSpace(result.CachedFilePath)
                    && File.Exists(result.CachedFilePath))
                {
                    var bitmap = await Task.Run(() => DecodeBitmap(result.CachedFilePath), ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || !card.IsNearViewport || card.Container == null)
                        {
                            card.ImageSource = null;
                            card.IsLoading = true;
                            return;
                        }

                        if (_cardsById.TryGetValue(card.FileId, out var current) && ReferenceEquals(current, card))
                        {
                            card.ImageSource = bitmap;
                            card.IsLoading = false;
                            card.IsError = false;
                        }
                    });
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || !card.IsNearViewport || card.Container == null)
                        {
                            card.ImageSource = null;
                            card.IsLoading = true;
                            return;
                        }

                        if (_cardsById.TryGetValue(card.FileId, out var current) && ReferenceEquals(current, card))
                        {
                            card.IsLoading = false;
                            card.IsError = true;
                        }
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || !card.IsNearViewport || card.Container == null)
                        {
                            card.ImageSource = null;
                            card.IsLoading = true;
                            return;
                        }

                        if (_cardsById.TryGetValue(card.FileId, out var current) && ReferenceEquals(current, card))
                        {
                            card.IsLoading = false;
                            card.IsError = true;
                        }
                    });
                }
                catch { }
                Debug.WriteLine($"Gallery thumbnail decode failed for {card.FileId}: {ex.Message}");
            }
            finally
            {
                lock (_decodeLock) { decodingFileIds.Remove(card.FileId); }
            }
        }
    }

    private static BitmapImage DecodeBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        memoryStream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = memoryStream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void UpdateStatus()
    {
        int total = _manifest?.TotalImageCount ?? 0;
        txtStatus.Text = $"Đã tải {_loadedCount} / {total} ảnh";
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadManifestAsync();
    }

    private async void BtnLoadMore_Click(object sender, RoutedEventArgs e)
    {
        await LoadNextBatchAsync();
    }

    private async Task<string?> ResolveOrderRootPathAsync(Order order)
    {
        if (order.RootFolderId.HasValue)
        {
            if (_rootFolderRepository != null)
            {
                var rf = await _rootFolderRepository.GetByIdAsync(order.RootFolderId.Value);
                if (rf != null && !string.IsNullOrWhiteSpace(rf.FullPath) && Directory.Exists(rf.FullPath))
                {
                    return rf.FullPath;
                }
            }
            return null;
        }

        var settings = await _settingsRepository.GetSettingsAsync();
        if (!string.IsNullOrWhiteSpace(settings.RootFolder) && Directory.Exists(settings.RootFolder))
        {
            return settings.RootFolder;
        }

        return null;
    }

    private async void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var order = await _orderRepository.GetOrderByIdAsync(_orderId);
            if (order == null) return;

            string? rootPath = await ResolveOrderRootPathAsync(order);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                string msg = order.RootFolderId.HasValue
                    ? "Thư mục gốc của đơn hàng ngoại tuyến hoặc không khả dụng trên máy này."
                    : "Thư mục kho dữ liệu không tồn tại hoặc chưa được cấu hình.";
                MessageBox.Show(msg, "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!string.IsNullOrWhiteSpace(order.RelativePath))
            {
                string canonicalRoot = Path.GetFullPath(rootPath);
                string canonicalRootWithSeparator = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                string targetDir = Path.GetFullPath(Path.Combine(canonicalRoot, order.RelativePath));
                string targetDirWithSeparator = targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (!targetDirWithSeparator.StartsWith(canonicalRootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Đường dẫn thư mục đơn hàng không hợp lệ (nằm ngoài thư mục kho dữ liệu).", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (Directory.Exists(targetDir))
                {
                    var dirInfo = new DirectoryInfo(targetDir);
                    if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        MessageBox.Show("Thư mục đơn hàng là liên kết tượng trưng (ReparsePoint) không được phép truy cập.", "Cảnh báo bảo mật", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = targetDir,
                        UseShellExecute = true
                    });
                    return;
                }
            }
            MessageBox.Show("Thư mục đơn hàng không tồn tại trên ổ đĩa.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể mở thư mục: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImageCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is GalleryCardViewItem card)
        {
            if (card.IsError)
            {
                // Retry loading thumbnail
                card.IsError = false;
                card.IsLoading = true;
                card.IsNearViewport = true;
                EnqueueForDecode(card);
                return;
            }

            // Open the original image in default system image viewer
            _ = OpenOriginalFileAsync(card.FileId);
        }
    }

    private async Task OpenOriginalFileAsync(string fileId)
    {
        try
        {
            if (_manifest == null) return;
            var item = _manifest.Groups.SelectMany(g => g.Items).FirstOrDefault(i => string.Equals(i.FileId, fileId, StringComparison.OrdinalIgnoreCase));
            if (item == null || string.IsNullOrWhiteSpace(item.SourceRelativePath)) return;

            var order = await _orderRepository.GetOrderByIdAsync(_orderId);
            if (order == null) return;

            string? rootPath = await ResolveOrderRootPathAsync(order);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                MessageBox.Show("Không thể mở file gốc: Thư mục kho dữ liệu ngoại tuyến.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string canonicalRoot = Path.GetFullPath(rootPath);
            string canonicalRootWithSeparator = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            string fullPath = Path.GetFullPath(Path.Combine(canonicalRoot, item.SourceRelativePath));

            if (!fullPath.StartsWith(canonicalRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Đường dẫn file ảnh nằm ngoài thư mục kho dữ liệu.", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    MessageBox.Show("File ảnh là liên kết tượng trưng (ReparsePoint) không được phép mở.", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}

public class GalleryGroupViewItem : INotifyPropertyChanged
{
    public long OrderItemId { get; set; }
    public string? SpecificationName { get; set; } = string.Empty;
    public string FolderDisplayName { get; set; } = string.Empty;
    public string CountText { get; set; } = string.Empty;
    public bool HasWarning { get; set; }
    public string WarningMessage { get; set; } = string.Empty;

    public ObservableCollection<GalleryCardViewItem> DisplayItems { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class GalleryCardViewItem : INotifyPropertyChanged
{
    public string FileId { get; set; } = string.Empty;
    public long OrderItemId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string TooltipText { get; set; } = string.Empty;
    public string ManifestVersion { get; set; } = string.Empty;

    public FrameworkElement? Container { get; set; }
    public bool IsNearViewport { get; set; }

    public void DetachContainer() => Container = null;

    private ImageSource? _imageSource;
    public ImageSource? ImageSource
    {
        get => _imageSource;
        set { _imageSource = value; OnPropertyChanged(); }
    }

    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private bool _isError = false;
    public bool IsError
    {
        get => _isError;
        set { _isError = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
