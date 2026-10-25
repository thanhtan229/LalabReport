using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Reporting;

/// <summary>
/// Thermal shipping label / package slip exporter (75x100mm format).
/// Exports crisp monochrome/grayscale images for thermal printing or sends directly to Windows PrintDialog.
/// </summary>
public class WpfShippingLabelExporter : IShippingLabelExporter
{
    public const double LabelWidth75x100 = 600;
    public const double LabelHeight75x100 = 800; // 3:4 ratio matches 75x100mm thermal labels
    public const double LabelWidth40x100 = 800; // 5:3 landscape thermal label canvas (800x480) to fill full thermal sticker width & height
    public const double LabelHeight40x100 = 480;

    // Default / legacy constants
    public const double LabelWidth = LabelWidth75x100;
    public const double LabelHeight = LabelHeight75x100;

    public static (double Width, double Height) GetDimensions(string? labelSize)
    {
        if (string.Equals(labelSize, ThermalLabelSizes.Size75x100, StringComparison.OrdinalIgnoreCase))
        {
            return (LabelWidth75x100, LabelHeight75x100);
        }
        return (LabelWidth40x100, LabelHeight40x100);
    }

    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<WpfShippingLabelExporter>? _logger;
    private static readonly CultureInfo ViCulture = new("vi-VN");

    public WpfShippingLabelExporter(ISettingsRepository settingsRepository, ILogger<WpfShippingLabelExporter>? logger = null)
    {
        _settingsRepository = settingsRepository;
        _logger = logger;
    }

    public async Task<string> ExportShippingLabelImageAsync(CustomerBill bill, string? destinationPath = null, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string exportDir = destinationPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(exportDir))
        {
            exportDir = Path.Combine(settings.BillExportFolder, "ShippingLabels");
        }

        Directory.CreateDirectory(exportDir);

        string safeCustName = SanitizeCustomerName(bill.CustomerNameSnapshot);
        string fileName = $"Label_{bill.BillNumber}_{safeCustName}.png";
        string filePath = Path.Combine(exportDir, fileName);

        var (width, height) = GetDimensions(settings.ThermalLabelSize);
        string actualPath = filePath;
        await RunOnStaThreadAsync(() =>
        {
            var visual = CreateLabelVisual(bill, settings);
            actualPath = SaveVisualToPng(visual, filePath, width, height);
        });

        _logger?.LogInformation("Exported thermal shipping label for bill {BillNumber} to {FilePath}", bill.BillNumber, actualPath);
        return actualPath;
    }

    public async Task PrintShippingLabelAsync(CustomerBill bill, bool silent = false, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);

        var (width, height) = GetDimensions(settings.ThermalLabelSize);
        await RunOnStaThreadAsync(() =>
        {
            var visual = CreateLabelVisual(bill, settings);
            SendVisualToPrinter(visual, $"Shipping Label - {bill.BillNumber}", settings, silent, specificPrinterName: null, width, height, _logger);
        });
    }

    public async Task<string> ExportOrderShippingLabelImageAsync(Order order, string? destinationPath = null, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string exportDir = destinationPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(exportDir))
        {
            exportDir = Path.Combine(settings.BillExportFolder, "ShippingLabels");
        }

        Directory.CreateDirectory(exportDir);

        string custName = order.Customer?.CanonicalName ?? order.OriginalFolderName;
        string safeCustName = SanitizeCustomerName(custName);
        string code = !string.IsNullOrWhiteSpace(order.OrderCode) ? order.OrderCode : $"ORD-{order.Id}";
        string fileName = $"Label_Order_{code}_{safeCustName}.png";
        string filePath = Path.Combine(exportDir, fileName);

        var (width, height) = GetDimensions(settings.ThermalLabelSize);
        string actualPath = filePath;
        await RunOnStaThreadAsync(() =>
        {
            var visual = CreateOrderLabelVisual(order, settings);
            actualPath = SaveVisualToPng(visual, filePath, width, height);
        });

        _logger?.LogInformation("Exported thermal order shipping label for order {OrderCode} to {FilePath}", code, actualPath);
        return actualPath;
    }

    public async Task PrintOrderShippingLabelAsync(Order order, bool silent = false, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string code = !string.IsNullOrWhiteSpace(order.OrderCode) ? order.OrderCode : $"ORD-{order.Id}";

        var (width, height) = GetDimensions(settings.ThermalLabelSize);
        await RunOnStaThreadAsync(() =>
        {
            var visual = CreateOrderLabelVisual(order, settings);
            SendVisualToPrinter(visual, $"Order Shipping Label - {code}", settings, silent, specificPrinterName: null, width, height, _logger);
        });
    }

    public async Task PrintTestSampleLabelAsync(string? printerName = null, string? labelSizeOverride = null, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string effectiveSize = !string.IsNullOrWhiteSpace(labelSizeOverride) ? labelSizeOverride : settings.ThermalLabelSize;
        var (width, height) = GetDimensions(effectiveSize);

        await RunOnStaThreadAsync(() =>
        {
            var visual = CreateTestSampleLabelVisual(settings, printerName, effectiveSize);
            SendVisualToPrinter(visual, $"Lalab Test Sample Label ({effectiveSize})", settings, silent: true, specificPrinterName: printerName, width, height, _logger);
        });
    }

    private static string SaveVisualToPng(Visual visual, string filePath, double width = LabelWidth, double height = LabelHeight)
    {
        var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        byte[] pngBytes = ms.ToArray();

        return WriteFileResiliently(filePath, pngBytes);
    }

    internal static string WriteFileResiliently(string filePath, byte[] bytes)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fileStream.Write(bytes, 0, bytes.Length);
                fileStream.Flush();
                return filePath;
            }
            catch (IOException)
            {
                if (attempt == 0)
                {
                    // Release any lingering unmanaged WIC memory-mapped section handles
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                Thread.Sleep(50);
            }
        }

        // Fallback if an external viewer (e.g., Windows Photos, Zalo) holds an active memory-mapped section on filePath
        string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        string fallbackPath = Path.Combine(dir, $"{stem}_{DateTime.Now:HHmmss_fff}{ext}");

        using var fallbackStream = new FileStream(fallbackPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fallbackStream.Write(bytes, 0, bytes.Length);
        fallbackStream.Flush();
        return fallbackPath;
    }

    private static void SendVisualToPrinter(
        Visual visual, 
        string jobTitle, 
        AppSettings? settings, 
        bool silent, 
        string? specificPrinterName, 
        double labelWidth,
        double labelHeight,
        ILogger? logger)
    {
        try
        {
            var printDialog = new System.Windows.Controls.PrintDialog();
            string? printerToUse = !string.IsNullOrWhiteSpace(specificPrinterName)
                ? specificPrinterName
                : settings?.ThermalPrinterName;

            if (!string.IsNullOrWhiteSpace(printerToUse))
            {
                try
                {
                    var printServer = new System.Printing.LocalPrintServer();
                    var queue = printServer.GetPrintQueue(printerToUse);
                    if (queue != null)
                    {
                        printDialog.PrintQueue = queue;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Không tìm thấy máy in {PrinterName}, chuyển sang máy in mặc định.", printerToUse);
                }
            }

            if (!silent)
            {
                if (printDialog.ShowDialog() != true)
                {
                    logger?.LogInformation("Người dùng đã hủy hộp thoại in cho {JobTitle}", jobTitle);
                    return;
                }
            }

            // Container for scaling to printable area
            var container = new ContainerVisual();
            double printWidth = labelWidth;
            double printHeight = labelHeight;

            try
            {
                if (printDialog.PrintQueue != null)
                {
                    var ticket = printDialog.PrintTicket ?? printDialog.PrintQueue.DefaultPrintTicket;
                    if (ticket != null)
                    {
                        try
                        {
                            // Keep Portrait orientation so thermal roll drivers report their true
                            // physical print head width as ExtentWidth and feed length as ExtentHeight.
                            ticket.PageOrientation = System.Printing.PageOrientation.Portrait;
                        }
                        catch { }
                    }
                    var capabilities = printDialog.PrintQueue.GetPrintCapabilities(ticket);
                    if (capabilities?.PageImageableArea != null && capabilities.PageImageableArea.ExtentWidth > 50 && capabilities.PageImageableArea.ExtentHeight > 50)
                    {
                        printWidth = capabilities.PageImageableArea.ExtentWidth;
                        printHeight = capabilities.PageImageableArea.ExtentHeight;
                    }
                    else if (printDialog.PrintableAreaWidth > 50 && printDialog.PrintableAreaHeight > 50)
                    {
                        printWidth = printDialog.PrintableAreaWidth;
                        printHeight = printDialog.PrintableAreaHeight;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Không thể đọc kích thước trang in thực tế, dùng kích thước tem mặc định ({Width}x{Height}).", labelWidth, labelHeight);
            }

            bool shouldRotate90 = labelWidth > labelHeight && printHeight >= printWidth;
            var transformGroup = new TransformGroup();
            double offsetX = 0;

            if (shouldRotate90)
            {
                // Rotate 90° clockwise so the landscape label fills the full width of the thermal print head (X)
                // and the safe physical sticker length along the feed direction (Y).
                // On 80x150mm drivers (such as TP874 S where printHeight=150mm but physical stickers are ~128-130mm),
                // cap feed height to 81% (~121.5mm) so the label fills the sticker without overflowing the gap.
                double targetWidthDips = printWidth * 0.92;
                double targetHeightDips = printHeight > printWidth * 1.7
                    ? printHeight * 0.81
                    : printHeight * 0.94;

                double scale = Math.Min(targetWidthDips / labelHeight, targetHeightDips / labelWidth);

                double renderedWidthDips = labelHeight * scale;
                offsetX = Math.Max(0, (printWidth - renderedWidthDips) / 2.0);
                const double offsetY = 6.0; // ~1.6mm leading edge safety margin

                // 1) Rotate 90° clockwise around (0,0): [0..labelWidth, 0..labelHeight] -> [-labelHeight..0, 0..labelWidth]
                // 2) Shift X by +labelHeight -> [0..labelHeight, 0..labelWidth]
                // 3) Scale uniformly to fill physical sticker -> [0..renderedWidthDips, 0..renderedHeightDips]
                // 4) Center horizontally on the print head + leading edge margin
                transformGroup.Children.Add(new RotateTransform(90));
                transformGroup.Children.Add(new TranslateTransform(labelHeight, 0));
                transformGroup.Children.Add(new ScaleTransform(scale, scale));
                transformGroup.Children.Add(new TranslateTransform(offsetX, offsetY));
            }
            else
            {
                double targetWidthDips = printWidth * 0.94;
                double targetHeightDips = printHeight > printWidth * 1.7
                    ? printHeight * 0.81
                    : printHeight * 0.94;
                double scale = Math.Min(targetWidthDips / labelWidth, targetHeightDips / labelHeight);

                double renderedWidthDips = labelWidth * scale;
                offsetX = Math.Max(0, (printWidth - renderedWidthDips) / 2.0);

                transformGroup.Children.Add(new ScaleTransform(scale, scale));
                transformGroup.Children.Add(new TranslateTransform(offsetX, 6.0));
            }

            var labelHolder = new ContainerVisual
            {
                Transform = transformGroup
            };
            labelHolder.Children.Add(visual);
            container.Children.Add(labelHolder);

            // Place 1-dot guard anchors at X=0 and X=printWidth-0.5 on scanline 0 so thermal drivers (such as OEM640dots)
            // preserve exact horizontal centering without cropping the left margin.
            if (offsetX > 2.0)
            {
                var guardVisual = new DrawingVisual();
                using (var dc = guardVisual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 0.5, 0.5));
                    dc.DrawRectangle(Brushes.Black, null, new Rect(Math.Max(0, printWidth - 0.5), 0, 0.5, 0.5));
                }
                container.Children.Add(guardVisual);
            }

            printDialog.PrintVisual(container, jobTitle);
            logger?.LogInformation("Đã gửi lệnh in tem '{JobTitle}' tới máy in {PrinterName} (silent={Silent})", 
                jobTitle, printDialog.PrintQueue?.FullName ?? "Default", silent);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Lỗi khi gửi lệnh in '{JobTitle}' tới máy in", jobTitle);
            throw;
        }
    }

    private static async Task RunOnStaThreadAsync(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }

        var tcs = new TaskCompletionSource();
        var staThread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        await tcs.Task;
    }

    public static DrawingVisual CreateLabelVisual(CustomerBill bill, AppSettings? settings)
    {
        string size = settings?.ThermalLabelSize ?? ThermalLabelSizes.Size40x100;
        if (string.Equals(size, ThermalLabelSizes.Size75x100, StringComparison.OrdinalIgnoreCase))
        {
            return CreateLabelVisual75x100(bill, settings);
        }
        return CreateLabelVisual40x100(bill, settings);
    }

    public static DrawingVisual CreateOrderLabelVisual(Order order, AppSettings? settings)
    {
        string size = settings?.ThermalLabelSize ?? ThermalLabelSizes.Size40x100;
        if (string.Equals(size, ThermalLabelSizes.Size75x100, StringComparison.OrdinalIgnoreCase))
        {
            return CreateOrderLabelVisual75x100(order, settings);
        }
        return CreateOrderLabelVisual40x100(order, settings);
    }

    public static DrawingVisual CreateTestSampleLabelVisual(AppSettings? settings, string? printerName, string? labelSizeOverride = null)
    {
        string size = !string.IsNullOrWhiteSpace(labelSizeOverride) 
            ? labelSizeOverride 
            : (settings?.ThermalLabelSize ?? ThermalLabelSizes.Size40x100);

        if (string.Equals(size, ThermalLabelSizes.Size75x100, StringComparison.OrdinalIgnoreCase))
        {
            return CreateTestSampleLabelVisual75x100(settings, printerName);
        }
        return CreateTestSampleLabelVisual40x100(settings, printerName);
    }

    public static DrawingVisual CreateLabelVisual75x100(CustomerBill bill, AppSettings? settings)
    {
        const double width = LabelWidth;
        const double height = LabelHeight;
        const double marginX = 24;
        const double contentWidth = width - (marginX * 2);

        var typefaceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var typefaceSemiBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceRegular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var typefaceItalic = new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

        var blackPen = new Pen(Brushes.Black, 1.5);
        var thinPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1);
        var thickPen = new Pen(Brushes.Black, 2.5);

        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        // 1. Background (Pure White for Thermal Paper)
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        // Outer Border
        dc.DrawRectangle(null, blackPen, new Rect(12, 12, width - 24, height - 24));

        double currentY = 24;

        // 2. Sender / Workshop Info (Header)
        string workshopName = !string.IsNullOrWhiteSpace(settings?.WorkshopName) 
            ? settings.WorkshopName 
            : "XƯỞNG IN ẢNH CHUYÊN NGHIỆP";
        string workshopPhone = settings?.WorkshopPhone?.Trim() ?? string.Empty;
        string workshopAddr = settings?.WorkshopAddress?.Trim() ?? string.Empty;

        var logoBitmap = WpfJpegBillExporter.TryLoadBitmap(settings?.WorkshopLogoPath);
        if (logoBitmap != null)
        {
            // Draw small logo at top-right
            double maxLogoH = 40;
            double maxLogoW = 90;
            double scale = Math.Min(maxLogoH / logoBitmap.PixelHeight, maxLogoW / logoBitmap.PixelWidth);
            double logoW = logoBitmap.PixelWidth * scale;
            double logoH = logoBitmap.PixelHeight * scale;
            dc.DrawImage(logoBitmap, new Rect(width - marginX - logoW, currentY, logoW, logoH));
        }

        var wsNameFt = CreateText(workshopName.ToUpperInvariant(), typefaceBold, 14, Colors.Black);
        dc.DrawText(wsNameFt, new Point(marginX, currentY));
        currentY += wsNameFt.Height + 2;

        string wsContact = !string.IsNullOrWhiteSpace(workshopPhone) ? $"Hotline: {workshopPhone}" : "";
        if (!string.IsNullOrWhiteSpace(workshopAddr))
        {
            wsContact = !string.IsNullOrWhiteSpace(wsContact) ? $"{wsContact}  |  Đ/c: {workshopAddr}" : $"Đ/c: {workshopAddr}";
        }

        if (!string.IsNullOrWhiteSpace(wsContact))
        {
            var wsContactFt = CreateText(wsContact, typefaceRegular, 11, Color.FromRgb(60, 60, 60), contentWidth - 100);
            dc.DrawText(wsContactFt, new Point(marginX, currentY));
            currentY += wsContactFt.Height + 8;
        }
        else
        {
            currentY += 8;
        }

        // Divider
        dc.DrawLine(blackPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 10;

        // 3. Document Title & Bill Number
        var titleFt = CreateText("PHIẾU GIAO HÀNG / PACKAGE SLIP", typefaceBold, 17, Colors.Black);
        dc.DrawText(titleFt, new Point(marginX, currentY));

        string dateStr = bill.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        var dateFt = CreateText(dateStr, typefaceRegular, 12, Color.FromRgb(80, 80, 80));
        dc.DrawText(dateFt, new Point(width - marginX - dateFt.Width, currentY + 4));
        currentY += titleFt.Height + 4;

        var billNoFt = CreateText($"Mã hóa đơn: {bill.BillNumber}", typefaceBold, 14, Colors.Black);
        dc.DrawText(billNoFt, new Point(marginX, currentY));

        int includedOrdersCount = bill.Orders.Count(o => o.IsIncluded);
        string orderCountText = $"({includedOrdersCount} thư mục / đơn)";
        var orderCountFt = CreateText(orderCountText, typefaceRegular, 12, Color.FromRgb(80, 80, 80));
        dc.DrawText(orderCountFt, new Point(marginX + billNoFt.Width + 8, currentY + 2));
        currentY += billNoFt.Height + 8;

        // Divider
        dc.DrawLine(thickPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 10;

        // 4. Recipient Section (NGƯỜI NHẬN - Highlighted)
        var recipientHeaderFt = CreateText("NGƯỜI NHẬN / RECIPIENT:", typefaceBold, 12, Color.FromRgb(60, 60, 60));
        dc.DrawText(recipientHeaderFt, new Point(marginX, currentY));
        currentY += recipientHeaderFt.Height + 4;

        // Customer Name
        string customerName = string.IsNullOrWhiteSpace(bill.CustomerNameSnapshot) ? "Khách vãng lai" : bill.CustomerNameSnapshot;
        var custNameFt = CreateText(customerName, typefaceBold, 20, Colors.Black, contentWidth);
        dc.DrawText(custNameFt, new Point(marginX, currentY));
        currentY += custNameFt.Height + 4;

        // Phone
        string phone = !string.IsNullOrWhiteSpace(bill.PhoneSnapshot) ? bill.PhoneSnapshot : "Chưa có SĐT";
        var phoneFt = CreateText($"SĐT: {phone}", typefaceBold, 16, Colors.Black);
        dc.DrawText(phoneFt, new Point(marginX, currentY));
        currentY += phoneFt.Height + 4;

        // Shipping Address
        string address = !string.IsNullOrWhiteSpace(bill.ShippingAddressSnapshot) 
            ? bill.ShippingAddressSnapshot 
            : "Địa chỉ: Nhận tại xưởng / Giao theo hẹn";
        var addrFt = CreateText(address, typefaceRegular, 13, Colors.Black, contentWidth);
        dc.DrawText(addrFt, new Point(marginX, currentY));
        currentY += addrFt.Height + 10;

        // Divider
        dc.DrawLine(blackPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 10;

        // 5. Items Summary Section
        var itemsHeaderFt = CreateText("DANH SÁCH SẢN PHẨM IN:", typefaceBold, 12, Color.FromRgb(60, 60, 60));
        dc.DrawText(itemsHeaderFt, new Point(marginX, currentY));
        currentY += itemsHeaderFt.Height + 6;

        var allLines = (bill.Lines.Count > 0 ? bill.Lines : bill.Orders.SelectMany(o => o.Lines))
            .Where(l => l.IsIncluded)
            .ToList();

        // Group lines by product and variant
        var itemGroups = allLines
            .GroupBy(l => $"{l.ProductNameSnapshot}|{l.VariantSnapshot ?? ""}|{l.SheetCount ?? 0}")
            .Select(g =>
            {
                var first = g.First();
                int totalQty = g.Sum(x => x.BilledQuantity);
                return new
                {
                    Name = first.ProductNameSnapshot,
                    Variant = first.VariantSnapshot,
                    SheetCount = first.SheetCount,
                    Quantity = totalQty
                };
            })
            .ToList();

        int maxItemsToShow = 5;
        var displayItems = itemGroups.Take(maxItemsToShow).ToList();

        foreach (var item in displayItems)
        {
            string itemDesc = item.Name;
            if (!string.IsNullOrWhiteSpace(item.Variant))
            {
                itemDesc += $" ({item.Variant})";
            }
            if (item.SheetCount > 0)
            {
                itemDesc += $" - {item.SheetCount} trang";
            }

            var itemDescFt = CreateText($"• {itemDesc}", typefaceRegular, 12, Colors.Black, contentWidth - 80);
            dc.DrawText(itemDescFt, new Point(marginX + 6, currentY));

            var itemQtyFt = CreateText($"SL: {item.Quantity}", typefaceBold, 12, Colors.Black);
            dc.DrawText(itemQtyFt, new Point(width - marginX - itemQtyFt.Width - 6, currentY));

            currentY += Math.Max(itemDescFt.Height, itemQtyFt.Height) + 4;
        }

        if (itemGroups.Count > maxItemsToShow)
        {
            int remaining = itemGroups.Count - maxItemsToShow;
            var moreFt = CreateText($"... và {remaining} loại sản phẩm khác (xem chi tiết trên hóa đơn)", typefaceItalic, 11, Color.FromRgb(100, 100, 100));
            dc.DrawText(moreFt, new Point(marginX + 6, currentY));
            currentY += moreFt.Height + 4;
        }

        int totalPrintCount = allLines.Sum(l => l.BilledQuantity);
        var totalQtyFt = CreateText($"Tổng số lượng in: {totalPrintCount} sản phẩm", typefaceSemiBold, 12, Colors.Black);
        dc.DrawText(totalQtyFt, new Point(marginX + 6, currentY));
        currentY += totalQtyFt.Height + 10;

        // Divider
        dc.DrawLine(thickPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 12;

        // 6. Payment / COD Section (PROMINENT - Avoid courier collection mistakes!)
        double codBoxHeight = 82;
        var codRect = new Rect(marginX, currentY, contentWidth, codBoxHeight);

        if (bill.IsPaid)
        {
            // Paid Box: Solid gray background, crisp black border
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(240, 240, 240)), thickPen, codRect, 6, 6);

            var paidTitleFt = CreateText("✓ ĐÃ THANH TOÁN (PAID)", typefaceBold, 18, Colors.Black);
            dc.DrawText(paidTitleFt, new Point(marginX + (contentWidth - paidTitleFt.Width) / 2, currentY + 12));

            var paidSubFt = CreateText("KHÔNG THU TIỀN NGƯỜI NHẬN (COD = 0 đ)", typefaceBold, 14, Colors.Black);
            dc.DrawText(paidSubFt, new Point(marginX + (contentWidth - paidSubFt.Width) / 2, currentY + 44));
        }
        else
        {
            // Unpaid Box: Heavy black border with clear COD amount
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Black, 3), codRect, 6, 6);

            var codTitleFt = CreateText("TIỀN THU HỘ (COD):", typefaceBold, 14, Colors.Black);
            dc.DrawText(codTitleFt, new Point(marginX + 16, currentY + 14));

            string amountStr = bill.GrandTotal.ToString("#,##0", ViCulture) + " đ";
            var amountFt = CreateText(amountStr, typefaceBold, 26, Colors.Black);
            dc.DrawText(amountFt, new Point(width - marginX - 16 - amountFt.Width, currentY + 10));

            var codNoteFt = CreateText("(Shipper vui lòng thu đủ số tiền trước khi bàn giao hàng)", typefaceItalic, 11, Color.FromRgb(60, 60, 60));
            dc.DrawText(codNoteFt, new Point(marginX + 16, currentY + 52));
        }

        currentY += codBoxHeight + 14;

        // 7. Footer & QR Code Section
        double qrSize = 110;
        var qrRect = new Rect(marginX, currentY, qrSize, qrSize);

        // Decide QR Code Payload:
        // If unpaid and VietQR enabled: render VietQR payload for quick scan-to-pay
        // Otherwise: render bill number payload for package tracking / scanning
        string qrPayload;
        if (!bill.IsPaid && settings?.EnableVietQrOnBill == true && !string.IsNullOrWhiteSpace(settings.BankAccountNumber))
        {
            qrPayload = VietQrGenerator.BuildVietQrPayload(
                settings.BankBinOrCode, 
                settings.BankAccountNumber, 
                bill.GrandTotal, 
                bill.BillNumber);
        }
        else
        {
            qrPayload = $"BILL:{bill.BillNumber}|{bill.CustomerNameSnapshot}|{bill.GrandTotal}";
        }

        if (!string.IsNullOrWhiteSpace(qrPayload))
        {
            VietQrGenerator.RenderQrCode(dc, qrRect, qrPayload, Brushes.Black, Brushes.White);
            dc.DrawRectangle(null, thinPen, qrRect);
        }

        // Instructions and Notes beside QR Code
        double rightTextX = marginX + qrSize + 16;
        double rightTextWidth = contentWidth - qrSize - 16;
        double rightY = currentY + 4;

        if (!string.IsNullOrWhiteSpace(bill.Note))
        {
            var noteFt = CreateText($"Ghi chú: {bill.Note}", typefaceBold, 12, Colors.Black, rightTextWidth);
            dc.DrawText(noteFt, new Point(rightTextX, rightY));
            rightY += noteFt.Height + 8;
        }

        var checkGoodsFt = CreateText("• Quý khách vui lòng đồng kiểm hàng khi nhận.", typefaceRegular, 11, Colors.Black, rightTextWidth);
        dc.DrawText(checkGoodsFt, new Point(rightTextX, rightY));
        rightY += checkGoodsFt.Height + 4;

        var hotlineFt = CreateText($"• Hỗ trợ & phản hồi: {workshopPhone}", typefaceSemiBold, 11, Colors.Black, rightTextWidth);
        dc.DrawText(hotlineFt, new Point(rightTextX, rightY));
        rightY += hotlineFt.Height + 4;

        var scanFt = CreateText(
            !bill.IsPaid && settings?.EnableVietQrOnBill == true ? "• Quét QR bên cạnh để chuyển khoản COD." : "• Quét mã QR để tra cứu thông tin đơn.",
            typefaceItalic, 10, Color.FromRgb(80, 80, 80), rightTextWidth);
        dc.DrawText(scanFt, new Point(rightTextX, rightY));

        return visual;
    }

    private static FormattedText CreateText(
        string text, 
        Typeface typeface, 
        double fontSize, 
        Color color, 
        double maxTextWidth = 0, 
        int maxLineCount = 0)
    {
        var ft = new FormattedText(
            text,
            ViCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(color),
            VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip
        );

        if (maxTextWidth > 0)
        {
            ft.MaxTextWidth = maxTextWidth;
        }

        if (maxLineCount > 0)
        {
            ft.MaxLineCount = maxLineCount;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }

        return ft;
    }

    public static DrawingVisual CreateOrderLabelVisual75x100(Order order, AppSettings? settings)
    {
        const double width = LabelWidth;
        const double height = LabelHeight;
        const double marginX = 24;
        const double contentWidth = width - (marginX * 2);

        var typefaceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var typefaceSemiBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceRegular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var typefaceItalic = new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

        var blackPen = new Pen(Brushes.Black, 1.5);
        var thinPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1);
        var thickPen = new Pen(Brushes.Black, 2.5);

        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        // 1. Background (Pure White for Thermal Paper)
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        // Outer Border
        dc.DrawRectangle(null, blackPen, new Rect(12, 12, width - 24, height - 24));

        double currentY = 24;

        // 2. Sender / Workshop Info (Header)
        string workshopName = !string.IsNullOrWhiteSpace(settings?.WorkshopName) 
            ? settings.WorkshopName 
            : "XƯỞNG IN ẢNH CHUYÊN NGHIỆP";
        string workshopPhone = settings?.WorkshopPhone?.Trim() ?? string.Empty;
        string workshopAddr = settings?.WorkshopAddress?.Trim() ?? string.Empty;

        var logoBitmap = WpfJpegBillExporter.TryLoadBitmap(settings?.WorkshopLogoPath);
        if (logoBitmap != null)
        {
            double maxLogoH = 40;
            double maxLogoW = 90;
            double scale = Math.Min(maxLogoH / logoBitmap.PixelHeight, maxLogoW / logoBitmap.PixelWidth);
            double logoW = logoBitmap.PixelWidth * scale;
            double logoH = logoBitmap.PixelHeight * scale;
            dc.DrawImage(logoBitmap, new Rect(width - marginX - logoW, currentY, logoW, logoH));
        }

        var wsNameFt = CreateText(workshopName.ToUpperInvariant(), typefaceBold, 14, Colors.Black);
        dc.DrawText(wsNameFt, new Point(marginX, currentY));
        currentY += wsNameFt.Height + 2;

        string wsContact = !string.IsNullOrWhiteSpace(workshopPhone) ? $"Hotline: {workshopPhone}" : "";
        if (!string.IsNullOrWhiteSpace(workshopAddr))
        {
            wsContact = !string.IsNullOrWhiteSpace(wsContact) ? $"{wsContact}  |  Đ/c: {workshopAddr}" : $"Đ/c: {workshopAddr}";
        }

        if (!string.IsNullOrWhiteSpace(wsContact))
        {
            var wsContactFt = CreateText(wsContact, typefaceRegular, 11, Color.FromRgb(60, 60, 60), contentWidth - 100);
            dc.DrawText(wsContactFt, new Point(marginX, currentY));
            currentY += wsContactFt.Height + 8;
        }
        else
        {
            currentY += 8;
        }

        // Divider
        dc.DrawLine(blackPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 10;

        // 3. Document Title & Order Code
        var titleFt = CreateText("PHIẾU GIAO HÀNG / TEM ĐÓNG GÓI", typefaceBold, 17, Colors.Black);
        dc.DrawText(titleFt, new Point(marginX, currentY));

        string dateStr = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        var dateFt = CreateText(dateStr, typefaceRegular, 12, Color.FromRgb(80, 80, 80));
        dc.DrawText(dateFt, new Point(width - marginX - dateFt.Width, currentY + 4));
        currentY += titleFt.Height + 4;

        string orderCode = !string.IsNullOrWhiteSpace(order.OrderCode) ? order.OrderCode : $"ORD-{order.Id}";
        var orderNoFt = CreateText($"Mã đơn: {orderCode}", typefaceBold, 14, Colors.Black);
        dc.DrawText(orderNoFt, new Point(marginX, currentY));

        if (!string.IsNullOrWhiteSpace(order.WorkDate))
        {
            var workDateFt = CreateText($"(Ngày: {order.WorkDate})", typefaceRegular, 12, Color.FromRgb(80, 80, 80));
            dc.DrawText(workDateFt, new Point(marginX + orderNoFt.Width + 8, currentY + 2));
        }
        currentY += orderNoFt.Height + 8;

        // Divider
        dc.DrawLine(thickPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 10;

        // 4. Recipient Section (NGƯỜI NHẬN)
        var recipientHeaderFt = CreateText("NGƯỜI NHẬN / RECIPIENT:", typefaceBold, 12, Color.FromRgb(60, 60, 60));
        dc.DrawText(recipientHeaderFt, new Point(marginX, currentY));
        currentY += recipientHeaderFt.Height + 4;

        string customerName = order.Customer?.CanonicalName ?? order.OriginalFolderName;
        if (string.IsNullOrWhiteSpace(customerName)) customerName = "Khách hàng";
        var custNameFt = CreateText(customerName, typefaceBold, 20, Colors.Black, contentWidth);
        dc.DrawText(custNameFt, new Point(marginX, currentY));
        currentY += custNameFt.Height + 4;

        string phone = !string.IsNullOrWhiteSpace(order.Customer?.Phone) ? order.Customer.Phone : "Chưa có SĐT";
        var phoneFt = CreateText($"SĐT: {phone}", typefaceBold, 16, Colors.Black);
        dc.DrawText(phoneFt, new Point(marginX, currentY));
        currentY += phoneFt.Height + 4;

        string address = !string.IsNullOrWhiteSpace(order.Customer?.Address)
            ? order.Customer.Address
            : "Địa chỉ: Nhận tại xưởng / Giao theo hẹn";
        var addrFt = CreateText(address, typefaceRegular, 13, Colors.Black, contentWidth);
        dc.DrawText(addrFt, new Point(marginX, currentY));
        currentY += addrFt.Height + 10;

        // Divider
        dc.DrawLine(blackPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 10;

        // 5. Items Summary Section
        var itemsHeaderFt = CreateText("DANH SÁCH SẢN PHẨM IN:", typefaceBold, 12, Color.FromRgb(60, 60, 60));
        dc.DrawText(itemsHeaderFt, new Point(marginX, currentY));
        currentY += itemsHeaderFt.Height + 6;

        var items = order.Items ?? new List<OrderItemScan>();
        var itemGroups = items
            .GroupBy(i => $"{i.PrintSpecification?.CanonicalName ?? i.SpecificationFolderName}|{i.PrintSpecification?.CanonicalSize ?? ""}")
            .Select(g =>
            {
                var first = g.First();
                int totalQty = g.Sum(x => x.BillQuantity ?? x.PrintCount ?? 0);
                string specName = first.PrintSpecification?.CanonicalName ?? first.SpecificationFolderName;
                string? size = first.PrintSpecification?.CanonicalSize;
                return new { Name = specName, Size = size, Quantity = totalQty };
            })
            .ToList();

        int maxItemsToShow = 4;
        var displayItems = itemGroups.Take(maxItemsToShow).ToList();

        foreach (var it in displayItems)
        {
            string itemDesc = it.Name;
            if (!string.IsNullOrWhiteSpace(it.Size))
            {
                itemDesc += $" ({it.Size})";
            }

            var itemDescFt = CreateText($"• {itemDesc}", typefaceRegular, 12, Colors.Black, contentWidth - 80);
            dc.DrawText(itemDescFt, new Point(marginX + 6, currentY));

            var itemQtyFt = CreateText($"SL: {it.Quantity}", typefaceBold, 12, Colors.Black);
            dc.DrawText(itemQtyFt, new Point(width - marginX - itemQtyFt.Width - 6, currentY));

            currentY += Math.Max(itemDescFt.Height, itemQtyFt.Height) + 4;
        }

        if (itemGroups.Count > maxItemsToShow)
        {
            int remaining = itemGroups.Count - maxItemsToShow;
            var moreFt = CreateText($"... và {remaining} loại quy cách khác", typefaceItalic, 11, Color.FromRgb(100, 100, 100));
            dc.DrawText(moreFt, new Point(marginX + 6, currentY));
            currentY += moreFt.Height + 4;
        }

        int totalPrintCount = items.Sum(i => i.BillQuantity ?? i.PrintCount ?? 0);
        var totalQtyFt = CreateText($"Tổng số lượng in: {totalPrintCount} sản phẩm", typefaceSemiBold, 12, Colors.Black);
        dc.DrawText(totalQtyFt, new Point(marginX + 6, currentY));
        currentY += totalQtyFt.Height + 10;

        // Divider
        dc.DrawLine(thickPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 12;

        // 6. Note / Order Notice Box
        double noticeBoxHeight = 82;
        var noticeRect = new Rect(marginX, currentY, contentWidth, noticeBoxHeight);

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(245, 245, 245)), thinPen, noticeRect, 6, 6);

        if (!string.IsNullOrWhiteSpace(order.Note))
        {
            var noteTitleFt = CreateText("GHI CHÚ ĐÓNG GÓI:", typefaceBold, 12, Colors.Black);
            dc.DrawText(noteTitleFt, new Point(marginX + 12, currentY + 10));

            var noteContentFt = CreateText(order.Note, typefaceBold, 14, Color.FromRgb(20, 20, 20), contentWidth - 24);
            dc.DrawText(noteContentFt, new Point(marginX + 12, currentY + 30));
        }
        else
        {
            var unbilledFt = CreateText("ĐƠN HÀNG XƯỞNG IN (CHƯA XUẤT HÓA ĐƠN)", typefaceBold, 14, Colors.Black);
            dc.DrawText(unbilledFt, new Point(marginX + (contentWidth - unbilledFt.Width) / 2, currentY + 16));

            var checkFt = CreateText("Vui lòng kiểm đếm kỹ số lượng và đối chiếu khi giao", typefaceRegular, 12, Color.FromRgb(60, 60, 60));
            dc.DrawText(checkFt, new Point(marginX + (contentWidth - checkFt.Width) / 2, currentY + 44));
        }

        currentY += noticeBoxHeight + 14;

        // 7. Footer & QR Code Section
        double qrSize = 100;
        var qrRect = new Rect(marginX, currentY, qrSize, qrSize);
        string qrPayload = $"ORD:{orderCode}|{customerName}|{totalPrintCount}";

        VietQrGenerator.RenderQrCode(dc, qrRect, qrPayload, Brushes.Black, Brushes.White);
        dc.DrawRectangle(null, thinPen, qrRect);

        double rightTextX = marginX + qrSize + 16;
        double rightTextWidth = contentWidth - qrSize - 16;
        double rightY = currentY + 6;

        var checkGoodsFt = CreateText("• Quý khách vui lòng đồng kiểm hàng khi nhận.", typefaceRegular, 11, Colors.Black, rightTextWidth);
        dc.DrawText(checkGoodsFt, new Point(rightTextX, rightY));
        rightY += checkGoodsFt.Height + 4;

        var hotlineFt = CreateText($"• Hotline xưởng in: {workshopPhone}", typefaceSemiBold, 11, Colors.Black, rightTextWidth);
        dc.DrawText(hotlineFt, new Point(rightTextX, rightY));
        rightY += hotlineFt.Height + 4;

        var scanFt = CreateText("• Quét mã QR bên cạnh để tra cứu thông tin đơn hàng.", typefaceItalic, 10, Color.FromRgb(80, 80, 80), rightTextWidth);
        dc.DrawText(scanFt, new Point(rightTextX, rightY));

        return visual;
    }

    public static DrawingVisual CreateTestSampleLabelVisual75x100(AppSettings? settings, string? printerName)
    {
        const double width = LabelWidth;
        const double height = LabelHeight;
        const double marginX = 24;
        const double contentWidth = width - (marginX * 2);

        var typefaceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var typefaceSemiBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceRegular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var typefaceItalic = new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

        var blackPen = new Pen(Brushes.Black, 2);
        var thinPen = new Pen(Brushes.Black, 1);

        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        // Background
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(null, blackPen, new Rect(12, 12, width - 24, height - 24));

        double currentY = 30;

        string wsName = !string.IsNullOrWhiteSpace(settings?.WorkshopName) ? settings.WorkshopName : "LALAB PHOTO PRINT";
        var headerFt = CreateText(wsName.ToUpperInvariant(), typefaceBold, 16, Colors.Black);
        dc.DrawText(headerFt, new Point(marginX + (contentWidth - headerFt.Width) / 2, currentY));
        currentY += headerFt.Height + 10;

        dc.DrawLine(blackPen, new Point(marginX, currentY), new Point(width - marginX, currentY));
        currentY += 14;

        var titleFt = CreateText("TEM KIỂM TRA MÁY IN NHIỆT", typefaceBold, 18, Colors.Black);
        dc.DrawText(titleFt, new Point(marginX + (contentWidth - titleFt.Width) / 2, currentY));
        currentY += titleFt.Height + 12;

        var sizeFt = CreateText("KHỔ GIẤY CHUẨN: 75 x 100 mm", typefaceBold, 14, Colors.Black);
        dc.DrawText(sizeFt, new Point(marginX + (contentWidth - sizeFt.Width) / 2, currentY));
        currentY += sizeFt.Height + 16;

        // Info box
        var infoRect = new Rect(marginX, currentY, contentWidth, 140);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(240, 240, 240)), thinPen, infoRect, 6, 6);

        double infoY = currentY + 12;
        string printerDisplay = !string.IsNullOrWhiteSpace(printerName) ? printerName : (settings?.ThermalPrinterName ?? "Máy in mặc định của hệ thống");
        var prFt = CreateText($"• Máy in: {printerDisplay}", typefaceSemiBold, 13, Colors.Black, contentWidth - 24);
        dc.DrawText(prFt, new Point(marginX + 12, infoY));
        infoY += prFt.Height + 8;

        string timeStr = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        var timeFt = CreateText($"• Thời gian in: {timeStr}", typefaceRegular, 13, Colors.Black);
        dc.DrawText(timeFt, new Point(marginX + 12, infoY));
        infoY += timeFt.Height + 8;

        var statusFt = CreateText("• Trạng thái kết nối: SẴN SÀNG HOẠT ĐỘNG ✓", typefaceBold, 13, Colors.Black);
        dc.DrawText(statusFt, new Point(marginX + 12, infoY));

        currentY += 156;

        // Contrast and Line Pattern Test
        dc.DrawRectangle(Brushes.Black, null, new Rect(marginX, currentY, contentWidth, 36));
        var contrastFt = CreateText("KIỂM TRA ĐỘ ĐẬM VÀ ĐỘ NÉT CỦA MÁY IN", typefaceBold, 12, Colors.White);
        dc.DrawText(contrastFt, new Point(marginX + (contentWidth - contrastFt.Width) / 2, currentY + 8));
        currentY += 50;

        // QR Code test
        double qrSize = 120;
        var qrRect = new Rect(marginX + (contentWidth - qrSize) / 2, currentY, qrSize, qrSize);
        VietQrGenerator.RenderQrCode(dc, qrRect, "LALAB-THERMAL-PRINTER-OK-75x100mm", Brushes.Black, Brushes.White);
        dc.DrawRectangle(null, thinPen, qrRect);
        currentY += qrSize + 16;

        var footerFt = CreateText("Nếu bạn thấy tem này in rõ nét, máy in nhiệt đã sẵn sàng!", typefaceItalic, 12, Color.FromRgb(60, 60, 60));
        dc.DrawText(footerFt, new Point(marginX + (contentWidth - footerFt.Width) / 2, currentY));

        return visual;
    }


    // =========================================================================
    // 40x100mm LANDSCAPE THERMAL LABEL METHODS (800x480 px @ 203 DPI, NO QR)
    // =========================================================================

    public static DrawingVisual CreateLabelVisual40x100(CustomerBill bill, AppSettings? settings)
    {
        const double width = LabelWidth40x100;   // 800
        const double height = LabelHeight40x100; // 480
        const double marginX = 20;

        var typefaceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var typefaceSemiBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceRegular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var typefaceItalic = new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

        var blackPen = new Pen(Brushes.Black, 2.5);
        var thinPen = new Pen(Brushes.Black, 1.2);
        var thickPen = new Pen(Brushes.Black, 3.0);

        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        // 1. Background & Outer Border
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(null, blackPen, new Rect(10, 8, width - 20, height - 16));

        // 2. Header Bar
        double currentY = 14;
        string workshopName = !string.IsNullOrWhiteSpace(settings?.WorkshopName) 
            ? settings.WorkshopName 
            : "XƯỞNG IN ẢNH CHUYÊN NGHIỆP";
        string workshopPhone = settings?.WorkshopPhone?.Trim() ?? string.Empty;
        string workshopAddr = settings?.WorkshopAddress?.Trim() ?? string.Empty;

        // Header Right: Title, Bill Number & Date
        var titleFt = CreateText("PHIẾU GIAO HÀNG", typefaceBold, 18, Colors.Black);
        dc.DrawText(titleFt, new Point(width - marginX - titleFt.Width, currentY));

        string dateStr = bill.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        var billNoFt = CreateText($"Mã HĐ: {bill.BillNumber}  •  {dateStr}", typefaceBold, 13.5, Colors.Black);
        dc.DrawText(billNoFt, new Point(width - marginX - billNoFt.Width, currentY + titleFt.Height + 3));

        double rightHeaderWidth = Math.Max(titleFt.Width, billNoFt.Width) + 16;

        // Header Left: Logo + Workshop name & contact
        var logoBitmap = WpfJpegBillExporter.TryLoadBitmap(settings?.WorkshopLogoPath);
        double leftHeaderX = marginX;
        if (logoBitmap != null)
        {
            double maxLogoH = 44;
            double maxLogoW = 90;
            double scale = Math.Min(maxLogoH / logoBitmap.PixelHeight, maxLogoW / logoBitmap.PixelWidth);
            double logoW = logoBitmap.PixelWidth * scale;
            double logoH = logoBitmap.PixelHeight * scale;
            dc.DrawImage(logoBitmap, new Rect(leftHeaderX, currentY + 1, logoW, logoH));
            leftHeaderX += logoW + 10;
        }

        double maxHeaderLeftWidth = Math.Max(240, width - marginX - leftHeaderX - rightHeaderWidth);

        var wsNameFt = CreateText(workshopName.ToUpperInvariant(), typefaceBold, 18, Colors.Black, maxHeaderLeftWidth, maxLineCount: 1);
        dc.DrawText(wsNameFt, new Point(leftHeaderX, currentY));

        string wsContact = !string.IsNullOrWhiteSpace(workshopPhone) ? $"Hotline: {workshopPhone}" : "";
        if (!string.IsNullOrWhiteSpace(workshopAddr))
        {
            wsContact = !string.IsNullOrWhiteSpace(wsContact) ? $"{wsContact} | Đ/c: {workshopAddr}" : $"Đ/c: {workshopAddr}";
        }
        if (!string.IsNullOrWhiteSpace(wsContact))
        {
            var wsContactFt = CreateText(wsContact, typefaceSemiBold, 13.5, Colors.Black, maxHeaderLeftWidth, maxLineCount: 1);
            dc.DrawText(wsContactFt, new Point(leftHeaderX, currentY + wsNameFt.Height + 3));
        }

        // Horizontal divider below header across full width
        double headerDividerY = 74;
        dc.DrawLine(blackPen, new Point(10, headerDividerY), new Point(width - 10, headerDividerY));

        // 3. Body: Vertical Divider at X = 456
        double colDividerX = 456;
        dc.DrawLine(blackPen, new Point(colDividerX, headerDividerY), new Point(colDividerX, height - 8));

        // ==========================================
        // LEFT COLUMN (X = 24 -> 444, width = 420)
        // 100% RECIPIENT INFORMATION & NOTES
        // ==========================================
        double leftX = marginX + 4;
        double leftWidth = colDividerX - leftX - 12; // ~420px
        double leftY = headerDividerY + 12;

        bool hasNote = !string.IsNullOrWhiteSpace(bill.Note);

        // Recipient Header
        var recHeaderFt = CreateText("NGƯỜI NHẬN:", typefaceBold, 15, Colors.Black, leftWidth, maxLineCount: 1);
        dc.DrawText(recHeaderFt, new Point(leftX, leftY));
        leftY += recHeaderFt.Height + 6;

        // Customer Name (32pt Bold, shrinks to 26pt if long)
        string customerName = string.IsNullOrWhiteSpace(bill.CustomerNameSnapshot) ? "Khách vãng lai" : bill.CustomerNameSnapshot;
        double custFontSize = customerName.Length > 22 ? 26 : 32;
        var custNameFt = CreateText(customerName, typefaceBold, custFontSize, Colors.Black, leftWidth, maxLineCount: 2);
        dc.DrawText(custNameFt, new Point(leftX, leftY));
        leftY += custNameFt.Height + 8;

        // Phone & Included Orders (24pt Bold)
        string phone = !string.IsNullOrWhiteSpace(bill.PhoneSnapshot) ? bill.PhoneSnapshot : "Chưa có SĐT";
        int includedOrdersCount = bill.Orders.Count(o => o.IsIncluded);
        string phoneOrderText = $"SĐT: {phone}" + (includedOrdersCount > 1 ? $"  ({includedOrdersCount} đơn)" : "");
        var phoneFt = CreateText(phoneOrderText, typefaceBold, 24, Colors.Black, leftWidth, maxLineCount: 1);
        dc.DrawText(phoneFt, new Point(leftX, leftY));
        leftY += phoneFt.Height + 8;

        // Address (22pt Bold, max 4 lines; 20pt Bold max 3 lines if note exists)
        string address = !string.IsNullOrWhiteSpace(bill.ShippingAddressSnapshot) 
            ? $"Địa chỉ: {bill.ShippingAddressSnapshot.Trim()}" 
            : "Địa chỉ: Nhận tại xưởng / Giao theo hẹn";
        double addrFontSize = hasNote ? 20 : 22;
        int addrMaxLines = hasNote ? 3 : 4;
        var addrFt = CreateText(address, typefaceBold, addrFontSize, Colors.Black, leftWidth, maxLineCount: addrMaxLines);
        dc.DrawText(addrFt, new Point(leftX, leftY));
        leftY += addrFt.Height + 10;

        // Note section (if bill has note)
        if (hasNote)
        {
            dc.DrawLine(thinPen, new Point(leftX, leftY), new Point(leftX + leftWidth, leftY));
            leftY += 6;
            var noteFt = CreateText($"Ghi chú: {bill.Note}", typefaceBold, 17, Colors.Black, leftWidth, maxLineCount: 2);
            dc.DrawText(noteFt, new Point(leftX, leftY));
        }

        // ==========================================
        // RIGHT COLUMN (X = 470 -> 780, width = 310)
        // UPPER: COD / PAYMENT BOX (Height = 152)
        // LOWER: PRODUCT LIST & TOTAL (Height = 212)
        // ==========================================
        double rightX = colDividerX + 14;
        double rightWidth = width - marginX - rightX; // ~310px
        double rightY = headerDividerY + 12;

        // 1. Upper Box: Payment / COD Box
        double codBoxHeight = 152;
        var codRect = new Rect(rightX, rightY, rightWidth, codBoxHeight);

        if (bill.IsPaid)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(241, 245, 249)), thickPen, codRect, 6, 6);

            var paidTitleFt = CreateText("✓ ĐÃ THANH TOÁN (PAID)", typefaceBold, 20, Colors.Black);
            dc.DrawText(paidTitleFt, new Point(rightX + (rightWidth - paidTitleFt.Width) / 2, rightY + 20));

            var paidSubFt = CreateText("KHÔNG THU TIỀN NGƯỜI NHẬN", typefaceBold, 15, Colors.Black);
            dc.DrawText(paidSubFt, new Point(rightX + (rightWidth - paidSubFt.Width) / 2, rightY + 64));

            var paidCodFt = CreateText("(COD = 0 đ)", typefaceBold, 18, Colors.Black);
            dc.DrawText(paidCodFt, new Point(rightX + (rightWidth - paidCodFt.Width) / 2, rightY + 102));
        }
        else
        {
            dc.DrawRoundedRectangle(Brushes.White, thickPen, codRect, 6, 6);

            var codTitleFt = CreateText("TIỀN THU HỘ (COD):", typefaceBold, 16, Colors.Black);
            dc.DrawText(codTitleFt, new Point(rightX + 14, rightY + 14));

            string amountStr = bill.GrandTotal.ToString("#,##0", ViCulture) + " đ";
            double amountFontSize = bill.GrandTotal >= 10_000_000 ? 28 : 36;
            var amountFt = CreateText(amountStr, typefaceBold, amountFontSize, Colors.Black, rightWidth - 14, maxLineCount: 1);
            dc.DrawText(amountFt, new Point(rightX + (rightWidth - amountFt.Width) / 2, rightY + (bill.GrandTotal >= 10_000_000 ? 52 : 46)));

            var codNoteFt = CreateText("(Shipper thu đủ tiền trước khi bàn giao)", typefaceItalic, 13, Colors.Black, rightWidth - 12, maxLineCount: 1);
            dc.DrawText(codNoteFt, new Point(rightX + (rightWidth - codNoteFt.Width) / 2, rightY + 114));
        }

        // 2. Lower Box: Product List & Total
        double productBoxY = rightY + codBoxHeight + 12;
        double productBoxHeight = height - 18 - productBoxY; // ~212px
        var productBoxRect = new Rect(rightX, productBoxY, rightWidth, productBoxHeight);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(248, 250, 252)), thinPen, productBoxRect, 6, 6);

        double insideProdY = productBoxY + 12;
        double insideProdX = rightX + 12;
        double insideProdWidth = rightWidth - 24;

        var allLines = (bill.Lines.Count > 0 ? bill.Lines : bill.Orders.SelectMany(o => o.Lines))
            .Where(l => l.IsIncluded)
            .ToList();

        var itemGroups = allLines
            .GroupBy(l => $"{l.ProductNameSnapshot}|{l.VariantSnapshot ?? ""}|{l.SheetCount ?? 0}")
            .Select(g =>
            {
                var first = g.First();
                int totalQty = g.Sum(x => x.BilledQuantity);
                return new
                {
                    Name = first.ProductNameSnapshot,
                    Variant = first.VariantSnapshot,
                    SheetCount = first.SheetCount,
                    Quantity = totalQty
                };
            })
            .ToList();

        string prodHeaderTitle = includedOrdersCount > 1 
            ? $"SẢN PHẨM IN ({includedOrdersCount} ĐƠN):" 
            : "DANH SÁCH SẢN PHẨM IN:";
        var itemsHeaderFt = CreateText(prodHeaderTitle, typefaceBold, 14.5, Colors.Black, insideProdWidth, maxLineCount: 1);
        dc.DrawText(itemsHeaderFt, new Point(insideProdX, insideProdY));
        insideProdY += itemsHeaderFt.Height + 8;

        int maxItemsToShow = 3;
        var displayItems = itemGroups.Take(maxItemsToShow).ToList();
        double itemFontSize = itemGroups.Count <= 1 ? 18.5 : 16.5;

        int renderedItemCount = 0;
        foreach (var item in displayItems)
        {
            string itemDesc = FormatProductDescription(item.Name, item.Variant, item.SheetCount);

            var itemDescFt = CreateText($"• {itemDesc}", typefaceSemiBold, itemFontSize, Colors.Black, insideProdWidth - 74, maxLineCount: 1);
            dc.DrawText(itemDescFt, new Point(insideProdX, insideProdY));

            var itemQtyFt = CreateText($"SL: {item.Quantity}", typefaceBold, itemFontSize + 0.5, Colors.Black);
            dc.DrawText(itemQtyFt, new Point(insideProdX + insideProdWidth - itemQtyFt.Width, insideProdY));

            insideProdY += Math.Max(itemDescFt.Height, itemQtyFt.Height) + 5;
            renderedItemCount++;
        }

        if (itemGroups.Count > renderedItemCount)
        {
            int remaining = itemGroups.Count - renderedItemCount;
            var moreFt = CreateText($"... và {remaining} loại khác", typefaceItalic, 13, Colors.Black, insideProdWidth, maxLineCount: 1);
            dc.DrawText(moreFt, new Point(insideProdX, insideProdY));
            insideProdY += moreFt.Height + 2;
        }

        // Bottom Total Quantity line pinned to bottom of product box
        int totalPrintCount = allLines.Sum(l => l.BilledQuantity);
        double totalLineY = productBoxY + productBoxHeight - 34;
        dc.DrawLine(thinPen, new Point(insideProdX, totalLineY - 6), new Point(insideProdX + insideProdWidth, totalLineY - 6));
        var totalQtyFt = CreateText($"Tổng số lượng in: {totalPrintCount} sản phẩm", typefaceBold, 16.5, Colors.Black, insideProdWidth, maxLineCount: 1);
        dc.DrawText(totalQtyFt, new Point(insideProdX, totalLineY));

        return visual;
    }

    public static DrawingVisual CreateOrderLabelVisual40x100(Order order, AppSettings? settings)
    {
        const double width = LabelWidth40x100;
        const double height = LabelHeight40x100;
        const double marginX = 20;

        var typefaceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var typefaceSemiBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceRegular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var typefaceItalic = new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

        var blackPen = new Pen(Brushes.Black, 2.5);
        var thinPen = new Pen(Brushes.Black, 1.2);
        var thickPen = new Pen(Brushes.Black, 3.0);

        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        // 1. Background & Outer Border
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(null, blackPen, new Rect(10, 8, width - 20, height - 16));

        // 2. Header Bar
        double currentY = 14;
        string workshopName = !string.IsNullOrWhiteSpace(settings?.WorkshopName) 
            ? settings.WorkshopName 
            : "XƯỞNG IN ẢNH CHUYÊN NGHIỆP";
        string workshopPhone = settings?.WorkshopPhone?.Trim() ?? string.Empty;
        string workshopAddr = settings?.WorkshopAddress?.Trim() ?? string.Empty;

        // Header Right First: Title & Order Code
        var titleFt = CreateText("PHIẾU GIAO HÀNG", typefaceBold, 18, Colors.Black);
        dc.DrawText(titleFt, new Point(width - marginX - titleFt.Width, currentY));

        string orderCode = !string.IsNullOrWhiteSpace(order.OrderCode) ? order.OrderCode : $"ORD-{order.Id}";
        string dateInfo = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        if (!string.IsNullOrWhiteSpace(order.WorkDate))
        {
            dateInfo = $"Ngày: {order.WorkDate}  •  " + dateInfo;
        }
        var orderNoFt = CreateText($"Mã đơn: {orderCode}  •  {dateInfo}", typefaceBold, 13.5, Colors.Black);
        dc.DrawText(orderNoFt, new Point(width - marginX - orderNoFt.Width, currentY + titleFt.Height + 3));

        double rightHeaderWidth = Math.Max(titleFt.Width, orderNoFt.Width) + 16;

        // Header Left: Logo & Workshop name & contact
        var logoBitmap = WpfJpegBillExporter.TryLoadBitmap(settings?.WorkshopLogoPath);
        double leftHeaderX = marginX;
        if (logoBitmap != null)
        {
            double maxLogoH = 44;
            double maxLogoW = 90;
            double scale = Math.Min(maxLogoH / logoBitmap.PixelHeight, maxLogoW / logoBitmap.PixelWidth);
            double logoW = logoBitmap.PixelWidth * scale;
            double logoH = logoBitmap.PixelHeight * scale;
            dc.DrawImage(logoBitmap, new Rect(leftHeaderX, currentY + 1, logoW, logoH));
            leftHeaderX += logoW + 10;
        }

        double maxHeaderLeftWidth = Math.Max(240, width - marginX - leftHeaderX - rightHeaderWidth);

        var wsNameFt = CreateText(workshopName.ToUpperInvariant(), typefaceBold, 18, Colors.Black, maxHeaderLeftWidth, maxLineCount: 1);
        dc.DrawText(wsNameFt, new Point(leftHeaderX, currentY));

        string wsContact = !string.IsNullOrWhiteSpace(workshopPhone) ? $"Hotline: {workshopPhone}" : "";
        if (!string.IsNullOrWhiteSpace(workshopAddr))
        {
            wsContact = !string.IsNullOrWhiteSpace(wsContact) ? $"{wsContact} | Đ/c: {workshopAddr}" : $"Đ/c: {workshopAddr}";
        }
        if (!string.IsNullOrWhiteSpace(wsContact))
        {
            var wsContactFt = CreateText(wsContact, typefaceSemiBold, 13.5, Colors.Black, maxHeaderLeftWidth, maxLineCount: 1);
            dc.DrawText(wsContactFt, new Point(leftHeaderX, currentY + wsNameFt.Height + 3));
        }

        // Horizontal divider below header
        double headerDividerY = 74;
        dc.DrawLine(blackPen, new Point(10, headerDividerY), new Point(width - 10, headerDividerY));

        // 3. Body: Vertical Divider at X = 456
        double colDividerX = 456;
        dc.DrawLine(blackPen, new Point(colDividerX, headerDividerY), new Point(colDividerX, height - 8));

        // ==========================================
        // LEFT COLUMN: 100% RECIPIENT INFORMATION
        // ==========================================
        double leftX = marginX + 4;
        double leftWidth = colDividerX - leftX - 12;
        double leftY = headerDividerY + 12;

        bool hasOrderNote = !string.IsNullOrWhiteSpace(order.Note);

        var recHeaderFt = CreateText("NGƯỜI NHẬN:", typefaceBold, 15, Colors.Black, leftWidth, maxLineCount: 1);
        dc.DrawText(recHeaderFt, new Point(leftX, leftY));
        leftY += recHeaderFt.Height + 6;

        string customerName = order.Customer?.CanonicalName ?? order.OriginalFolderName;
        if (string.IsNullOrWhiteSpace(customerName)) customerName = "Khách hàng";
        double custFontSize = customerName.Length > 22 ? 26 : 32;
        var custNameFt = CreateText(customerName, typefaceBold, custFontSize, Colors.Black, leftWidth, maxLineCount: 2);
        dc.DrawText(custNameFt, new Point(leftX, leftY));
        leftY += custNameFt.Height + 8;

        string phone = !string.IsNullOrWhiteSpace(order.Customer?.Phone) ? order.Customer.Phone : "Chưa có SĐT";
        var phoneFt = CreateText($"SĐT: {phone}", typefaceBold, 24, Colors.Black, leftWidth, maxLineCount: 1);
        dc.DrawText(phoneFt, new Point(leftX, leftY));
        leftY += phoneFt.Height + 8;

        string address = !string.IsNullOrWhiteSpace(order.Customer?.Address)
            ? $"Địa chỉ: {order.Customer.Address.Trim()}"
            : "Địa chỉ: Nhận tại xưởng / Giao theo hẹn";
        double addrFontSize = hasOrderNote ? 20 : 22;
        int addrMaxLines = hasOrderNote ? 3 : 4;
        var addrFt = CreateText(address, typefaceBold, addrFontSize, Colors.Black, leftWidth, maxLineCount: addrMaxLines);
        dc.DrawText(addrFt, new Point(leftX, leftY));
        leftY += addrFt.Height + 10;

        if (hasOrderNote)
        {
            dc.DrawLine(thinPen, new Point(leftX, leftY), new Point(leftX + leftWidth, leftY));
            leftY += 6;
            var noteFt = CreateText($"Ghi chú: {order.Note}", typefaceBold, 17, Colors.Black, leftWidth, maxLineCount: 2);
            dc.DrawText(noteFt, new Point(leftX, leftY));
        }

        // ==========================================
        // RIGHT COLUMN: STATUS & PRODUCT LIST
        // ==========================================
        double rightX = colDividerX + 14;
        double rightWidth = width - marginX - rightX;
        double rightY = headerDividerY + 12;

        double boxHeight = 152;
        var statusRect = new Rect(rightX, rightY, rightWidth, boxHeight);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(245, 245, 245)), thinPen, statusRect, 6, 6);

        var unbilledFt = CreateText("ĐƠN HÀNG XƯỞNG IN", typefaceBold, 20, Colors.Black);
        dc.DrawText(unbilledFt, new Point(rightX + (rightWidth - unbilledFt.Width) / 2, rightY + 22));

        var subUnbilledFt = CreateText("(CHƯA XUẤT HÓA ĐƠN)", typefaceBold, 15.5, Colors.Black);
        dc.DrawText(subUnbilledFt, new Point(rightX + (rightWidth - subUnbilledFt.Width) / 2, rightY + 64));

        var checkCountFt = CreateText("Kiểm đếm kỹ trước khi giao", typefaceSemiBold, 14, Colors.Black);
        dc.DrawText(checkCountFt, new Point(rightX + (rightWidth - checkCountFt.Width) / 2, rightY + 104));

        // Lower box: Product list
        double productBoxY = rightY + boxHeight + 12;
        double productBoxHeight = height - 18 - productBoxY;
        var productBoxRect = new Rect(rightX, productBoxY, rightWidth, productBoxHeight);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(248, 250, 252)), thinPen, productBoxRect, 6, 6);

        double insideProdY = productBoxY + 12;
        double insideProdX = rightX + 12;
        double insideProdWidth = rightWidth - 24;

        var items = order.Items ?? new List<OrderItemScan>();
        var itemGroups = items
            .GroupBy(i => $"{i.PrintSpecification?.CanonicalName ?? i.SpecificationFolderName}|{i.PrintSpecification?.CanonicalSize ?? ""}")
            .Select(g =>
            {
                var first = g.First();
                int totalQty = g.Sum(x => x.BillQuantity ?? x.PrintCount ?? 0);
                string specName = first.PrintSpecification?.CanonicalName ?? first.SpecificationFolderName;
                string? size = first.PrintSpecification?.CanonicalSize;
                return new { Name = specName, Size = size, Quantity = totalQty };
            })
            .ToList();

        var itemsHeaderFt = CreateText("DANH SÁCH SẢN PHẨM IN:", typefaceBold, 14.5, Colors.Black, insideProdWidth, maxLineCount: 1);
        dc.DrawText(itemsHeaderFt, new Point(insideProdX, insideProdY));
        insideProdY += itemsHeaderFt.Height + 8;

        int maxItemsToShow = 3;
        var displayItems = itemGroups.Take(maxItemsToShow).ToList();
        double itemFontSize = itemGroups.Count <= 1 ? 18.5 : 16.5;

        int renderedItemCount = 0;
        foreach (var it in displayItems)
        {
            string itemDesc = FormatProductDescription(it.Name, it.Size, null);

            var itemDescFt = CreateText($"• {itemDesc}", typefaceSemiBold, itemFontSize, Colors.Black, insideProdWidth - 74, maxLineCount: 1);
            dc.DrawText(itemDescFt, new Point(insideProdX, insideProdY));

            var itemQtyFt = CreateText($"SL: {it.Quantity}", typefaceBold, itemFontSize + 0.5, Colors.Black);
            dc.DrawText(itemQtyFt, new Point(insideProdX + insideProdWidth - itemQtyFt.Width, insideProdY));

            insideProdY += Math.Max(itemDescFt.Height, itemQtyFt.Height) + 5;
            renderedItemCount++;
        }

        if (itemGroups.Count > renderedItemCount)
        {
            int remaining = itemGroups.Count - renderedItemCount;
            var moreFt = CreateText($"... và {remaining} loại khác", typefaceItalic, 13, Colors.Black, insideProdWidth, maxLineCount: 1);
            dc.DrawText(moreFt, new Point(insideProdX, insideProdY));
            insideProdY += moreFt.Height + 2;
        }

        int totalPrintCount = items.Sum(i => i.BillQuantity ?? i.PrintCount ?? 0);
        double totalLineY = productBoxY + productBoxHeight - 34;
        dc.DrawLine(thinPen, new Point(insideProdX, totalLineY - 6), new Point(insideProdX + insideProdWidth, totalLineY - 6));
        var totalQtyFt = CreateText($"Tổng số lượng in: {totalPrintCount} sản phẩm", typefaceBold, 16.5, Colors.Black, insideProdWidth, maxLineCount: 1);
        dc.DrawText(totalQtyFt, new Point(insideProdX, totalLineY));

        return visual;
    }

    public static DrawingVisual CreateTestSampleLabelVisual40x100(AppSettings? settings, string? printerName)
    {
        const double width = LabelWidth40x100;
        const double height = LabelHeight40x100;
        const double marginX = 20;

        var typefaceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var typefaceSemiBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var typefaceRegular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var typefaceItalic = new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

        var blackPen = new Pen(Brushes.Black, 2.5);
        var thinPen = new Pen(Brushes.Black, 1.2);
        var thickPen = new Pen(Brushes.Black, 3.0);

        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        // 1. Background & Outer border
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(null, blackPen, new Rect(10, 8, width - 20, height - 16));

        // 2. Header
        double currentY = 14;
        string wsName = !string.IsNullOrWhiteSpace(settings?.WorkshopName) ? settings.WorkshopName : "LALAB PHOTO PRINT";
        var wsFt = CreateText(wsName.ToUpperInvariant(), typefaceBold, 18, Colors.Black, 420, maxLineCount: 1);
        dc.DrawText(wsFt, new Point(marginX, currentY));

        string printerDisplay = !string.IsNullOrWhiteSpace(printerName) ? printerName : (settings?.ThermalPrinterName ?? "Máy in mặc định");
        var prSubFt = CreateText($"Máy in: {printerDisplay}", typefaceSemiBold, 13.5, Colors.Black, 420, maxLineCount: 1);
        dc.DrawText(prSubFt, new Point(marginX, currentY + wsFt.Height + 3));

        var titleFt = CreateText("TEM KIỂM TRA MÁY IN (40x100mm)", typefaceBold, 17, Colors.Black);
        dc.DrawText(titleFt, new Point(width - marginX - titleFt.Width, currentY));

        var timeSubFt = CreateText($"Thời gian: {DateTime.Now:dd/MM/yyyy HH:mm}", typefaceBold, 13.5, Colors.Black);
        dc.DrawText(timeSubFt, new Point(width - marginX - timeSubFt.Width, currentY + titleFt.Height + 3));

        double headerDividerY = 74;
        dc.DrawLine(blackPen, new Point(10, headerDividerY), new Point(width - 10, headerDividerY));

        double colDividerX = 456;
        dc.DrawLine(blackPen, new Point(colDividerX, headerDividerY), new Point(colDividerX, height - 8));

        // 3. Left Column: Full Recipient Demonstration
        double leftX = marginX + 4;
        double leftWidth = colDividerX - leftX - 12;
        double leftY = headerDividerY + 12;

        var recHeaderFt = CreateText("NGƯỜI NHẬN:", typefaceBold, 15, Colors.Black);
        dc.DrawText(recHeaderFt, new Point(leftX, leftY));
        leftY += recHeaderFt.Height + 6;

        var sampleCustFt = CreateText("Chị Lan thêm", typefaceBold, 32, Colors.Black, leftWidth, maxLineCount: 1);
        dc.DrawText(sampleCustFt, new Point(leftX, leftY));
        leftY += sampleCustFt.Height + 8;

        var samplePhoneFt = CreateText("SĐT: 0977.888.999", typefaceBold, 24, Colors.Black, leftWidth, maxLineCount: 1);
        dc.DrawText(samplePhoneFt, new Point(leftX, leftY));
        leftY += samplePhoneFt.Height + 8;

        var sampleAddrFt = CreateText("Địa chỉ: Số 3 đường B25, KDC 91B, An Khánh, Ninh Kiều, Cần Thơ, Việt Nam", typefaceBold, 22, Colors.Black, leftWidth, maxLineCount: 4);
        dc.DrawText(sampleAddrFt, new Point(leftX, leftY));

        // 4. Right Column: COD Box & Product Box
        double rightX = colDividerX + 14;
        double rightWidth = width - marginX - rightX;
        double rightY = headerDividerY + 12;

        // COD Box
        double codBoxHeight = 152;
        var codRect = new Rect(rightX, rightY, rightWidth, codBoxHeight);
        dc.DrawRoundedRectangle(Brushes.White, thickPen, codRect, 6, 6);

        var codTitle = CreateText("TIỀN THU HỘ (COD):", typefaceBold, 16, Colors.Black);
        dc.DrawText(codTitle, new Point(rightX + 14, rightY + 14));

        var codAmount = CreateText("40.000 đ", typefaceBold, 36, Colors.Black);
        dc.DrawText(codAmount, new Point(rightX + (rightWidth - codAmount.Width) / 2, rightY + 46));

        var codNote = CreateText("(Shipper thu đủ tiền trước khi bàn giao)", typefaceItalic, 13, Colors.Black);
        dc.DrawText(codNote, new Point(rightX + (rightWidth - codNote.Width) / 2, rightY + 114));

        // Lower Product Box
        double productBoxY = rightY + codBoxHeight + 12;
        double productBoxHeight = height - 18 - productBoxY;
        var productBoxRect = new Rect(rightX, productBoxY, rightWidth, productBoxHeight);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(248, 250, 252)), thinPen, productBoxRect, 6, 6);

        double insideProdY = productBoxY + 12;
        double insideProdX = rightX + 12;
        double insideProdWidth = rightWidth - 24;

        var itemsHeader = CreateText("DANH SÁCH SẢN PHẨM IN:", typefaceBold, 14.5, Colors.Black);
        dc.DrawText(itemsHeader, new Point(insideProdX, insideProdY));
        insideProdY += itemsHeader.Height + 8;

        var sampleItemDesc = CreateText("• 13x18 in", typefaceSemiBold, 18.5, Colors.Black, insideProdWidth - 74);
        dc.DrawText(sampleItemDesc, new Point(insideProdX, insideProdY));

        var sampleItemQty = CreateText("SL: 8", typefaceBold, 19, Colors.Black);
        dc.DrawText(sampleItemQty, new Point(insideProdX + insideProdWidth - sampleItemQty.Width, insideProdY));

        double totalLineY = productBoxY + productBoxHeight - 34;
        dc.DrawLine(thinPen, new Point(insideProdX, totalLineY - 6), new Point(insideProdX + insideProdWidth, totalLineY - 6));
        var totalQty = CreateText("Tổng số lượng in: 8 sản phẩm", typefaceBold, 16.5, Colors.Black);
        dc.DrawText(totalQty, new Point(insideProdX, totalLineY));

        return visual;
    }

    private static string FormatProductDescription(string? name, string? variant, int? sheetCount)
    {
        string desc = name?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(variant))
        {
            string cleanVariant = variant.Trim();
            // Only append variant if not already part of the product name (e.g. avoid '13x18 in (13x18)')
            if (!desc.Contains(cleanVariant, StringComparison.OrdinalIgnoreCase))
            {
                desc += $" ({cleanVariant})";
            }
        }
        if (sheetCount > 0)
        {
            desc += $" - {sheetCount} trang";
        }
        return desc;
    }

        private static string SanitizeCustomerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Guest";
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_')
                sb.Append('_');
        }
        string sanitized = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "Customer" : sanitized;
    }
}

