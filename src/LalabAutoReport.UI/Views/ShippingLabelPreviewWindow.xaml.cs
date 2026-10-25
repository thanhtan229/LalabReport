using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;

namespace LalabAutoReport.UI.Views;

public partial class ShippingLabelPreviewWindow : Window
{
    private readonly Order? _order;
    private readonly CustomerBill? _bill;
    private readonly IShippingLabelExporter _shippingLabelExporter;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IOrderRepository? _orderRepository;

    private string? _tempImagePath;

    public bool WasPrinted { get; private set; } = false;
    public bool WasMarkedDelivered { get; private set; } = false;
    public DateTimeOffset? DeliveredAt { get; private set; }
    public string? DeliveredBy { get; private set; }

    public ShippingLabelPreviewWindow(
        Order? order,
        CustomerBill? bill,
        IShippingLabelExporter shippingLabelExporter,
        ISettingsRepository settingsRepository,
        IOrderRepository? orderRepository = null)
    {
        InitializeComponent();
        _order = order;
        _bill = bill;
        _shippingLabelExporter = shippingLabelExporter;
        _settingsRepository = settingsRepository;
        _orderRepository = orderRepository;

        string displayCode = _bill?.BillNumber ?? (!string.IsNullOrWhiteSpace(_order?.OrderCode) ? _order.OrderCode : $"ORD-{_order?.Id}");
        string customerName = _bill?.CustomerNameSnapshot ?? _order?.Customer?.CanonicalName ?? _order?.OriginalFolderName ?? "Khách hàng";
        txtSubtitle.Text = $"{displayCode}  •  {customerName}";

        Loaded += ShippingLabelPreviewWindow_Loaded;
        Closed += ShippingLabelPreviewWindow_Closed;
    }

    private async void ShippingLabelPreviewWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await _settingsRepository.GetSettingsAsync();
            string labelSize = settings.ThermalLabelSize ?? ThermalLabelSizes.Size40x100;
            txtTitle.Text = $"Tem đóng gói bưu kiện ({labelSize})";

            if (!string.IsNullOrWhiteSpace(settings.ThermalPrinterName))
            {
                txtPrinterName.Text = $"🖨️ Máy in nhiệt: {settings.ThermalPrinterName}";
            }
            else
            {
                txtPrinterName.Text = "⚠️ Chưa chọn máy in nhiệt (Sẽ dùng máy in mặc định Windows)";
            }

            chkAutoDeliver.IsChecked = settings.AutoMarkDeliveredOnPrint;

            string imagePath;
            if (_bill != null)
            {
                imagePath = await _shippingLabelExporter.ExportShippingLabelImageAsync(_bill);
            }
            else if (_order != null)
            {
                imagePath = await _shippingLabelExporter.ExportOrderShippingLabelImageAsync(_order);
            }
            else
            {
                throw new InvalidOperationException("Không có dữ liệu đơn hoặc hóa đơn để tạo tem.");
            }

            _tempImagePath = imagePath;

            if (File.Exists(imagePath))
            {
                using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                ms.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();

                imgPreview.Source = bitmap;
                pnlLoading.Visibility = Visibility.Collapsed;
            }
            else
            {
                pnlLoading.Visibility = Visibility.Visible;
                txtStatus.Text = "Không thể tải ảnh tem.";
            }
        }
        catch (Exception ex)
        {
            pnlLoading.Visibility = Visibility.Visible;
            txtStatus.Text = $"Lỗi tạo tem: {ex.Message}";
        }
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        btnPrint.IsEnabled = false;
        txtStatus.Text = "⏳ Đang gửi lệnh in tới máy in...";

        try
        {
            if (_bill != null)
            {
                await _shippingLabelExporter.PrintShippingLabelAsync(_bill, silent: true);
            }
            else if (_order != null)
            {
                await _shippingLabelExporter.PrintOrderShippingLabelAsync(_order, silent: true);
            }

            WasPrinted = true;

            if (chkAutoDeliver.IsChecked == true && _orderRepository != null)
            {
                var now = DateTimeOffset.Now;
                string deliveredBy = "In tem PC";
                if (_order != null)
                {
                    await _orderRepository.UpdateOrderDeliveredStatusAsync(_order.Id, true, now, deliveredBy);
                    _order.IsDelivered = true;
                    _order.DeliveredAt = now;
                    _order.DeliveredBy = deliveredBy;
                }
                else if (_bill?.Orders != null)
                {
                    foreach (var bo in _bill.Orders.Where(o => o.IsIncluded && o.OrderId > 0))
                    {
                        await _orderRepository.UpdateOrderDeliveredStatusAsync(bo.OrderId, true, now, deliveredBy);
                    }
                }

                WasMarkedDelivered = true;
                DeliveredAt = now;
                DeliveredBy = deliveredBy;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            btnPrint.IsEnabled = true;
            txtStatus.Text = $"❌ Lỗi in tem: {ex.Message}";
            MessageBox.Show($"Lỗi khi in tem ra máy in: {ex.Message}", "Lỗi In Tem", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShippingLabelPreviewWindow_Closed(object? sender, EventArgs e)
    {
        // Image loaded into memory with CacheOption.OnLoad
    }
}
