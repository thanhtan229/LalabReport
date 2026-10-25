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
using LalabAutoReport.Core.Services;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Reporting;

public class WpfJpegBillExporter : IJpegBillExporter, IBillVisualRenderer
{
    public const int DefaultMaxLinesPerPage = 45;

    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<WpfJpegBillExporter>? _logger;
    private static readonly CultureInfo ViCulture = new("vi-VN");

    public WpfJpegBillExporter(ISettingsRepository settingsRepository, ILogger<WpfJpegBillExporter>? logger = null)
    {
        _settingsRepository = settingsRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BitmapSource>> RenderBillBitmapsAsync(CustomerBill bill, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        var pages = PaginateBill(bill, DefaultMaxLinesPerPage);
        var bitmaps = new List<BitmapSource>();

        void DoRender()
        {
            foreach (var page in pages)
            {
                var bmp = RenderPageToBitmap(bill, page, settings);
                bitmaps.Add(bmp);
            }
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            DoRender();
        }
        else
        {
            var tcs = new TaskCompletionSource();
            var staThread = new Thread(() =>
            {
                try
                {
                    DoRender();
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

        return bitmaps;
    }

    public Task<string> SaveBitmapToJpegAsync(BitmapSource bitmap, string destinationFilePath, int quality = 95, CancellationToken cancellationToken = default)
    {
        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
        SaveBitmapToJpeg(bitmap, destinationFilePath, quality);
        return Task.FromResult(destinationFilePath);
    }

    public BitmapSource StitchBitmapsVertically(IReadOnlyList<BitmapSource> pages)
    {
        if (pages == null || pages.Count == 0) throw new ArgumentException("No pages to stitch", nameof(pages));
        if (pages.Count == 1) return pages[0];

        int totalHeight = pages.Sum(p => p.PixelHeight);
        int maxWidth = pages.Max(p => p.PixelWidth);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, maxWidth, totalHeight));
            double currentY = 0;
            foreach (var page in pages)
            {
                dc.DrawImage(page, new Rect(0, currentY, page.PixelWidth, page.PixelHeight));
                currentY += page.PixelHeight;
            }
        }

        var rtb = new RenderTargetBitmap(maxWidth, totalHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        var stitched = new BitmapImage();
        stitched.BeginInit();
        stitched.CacheOption = BitmapCacheOption.OnLoad;
        stitched.StreamSource = ms;
        stitched.EndInit();
        stitched.Freeze();
        return stitched;
    }

    public async Task<string> ExportBillToJpegAsync(CustomerBill bill, string? destinationDirectory = null, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetSettingsAsync(cancellationToken);
        string exportDir = destinationDirectory ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exportDir))
        {
            exportDir = settings.BillExportFolder;
        }

        if (string.IsNullOrWhiteSpace(exportDir))
        {
            exportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LalabReports", "Bills");
        }

        Directory.CreateDirectory(exportDir);

        var pages = PaginateBill(bill, DefaultMaxLinesPerPage);
        var bitmaps = await RenderBillBitmapsAsync(bill, cancellationToken);
        var exportedFiles = new List<string>();

        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var bmp = bitmaps[i];
            string fileName = GenerateDeterministicFileName(bill, page.PageNumber, page.TotalPages);
            string filePath = Path.Combine(exportDir, fileName);
            SaveBitmapToJpeg(bmp, filePath);
            exportedFiles.Add(filePath);
        }

        string primaryFilePath = exportedFiles.FirstOrDefault() ?? Path.Combine(exportDir, GenerateDeterministicFileName(bill));
        bill.ExportFilePath = primaryFilePath;
        bill.ExportedAt = DateTimeOffset.UtcNow;
        bill.Status = CustomerBillStatus.Exported;

        if (pages.Count > 1)
        {
            _logger?.LogInformation("Successfully exported bill {BillNumber} across {TotalPages} JPEG pages: {PrimaryFilePath}", bill.BillNumber, pages.Count, primaryFilePath);
        }
        else
        {
            _logger?.LogInformation("Successfully exported bill {BillNumber} to JPEG: {FilePath}", bill.BillNumber, primaryFilePath);
        }

        return primaryFilePath;
    }

    public static string GenerateDeterministicFileName(CustomerBill bill)
    {
        return GenerateDeterministicFileName(bill, 1, 1);
    }

    public static string GenerateDeterministicFileName(CustomerBill bill, int pageNumber, int totalPages)
    {
        string slug = SanitizeSlug(bill.CustomerNameSnapshot);
        if (totalPages <= 1)
        {
            return $"{bill.BillNumber}_{slug}.jpg";
        }
        return $"{bill.BillNumber}_{slug}_Trang{pageNumber}.jpg";
    }

    public static IReadOnlyList<string> GetExportPageFileNames(CustomerBill bill, int maxLinesPerPage = DefaultMaxLinesPerPage)
    {
        var pages = PaginateBill(bill, maxLinesPerPage);
        return pages.Select(p => GenerateDeterministicFileName(bill, p.PageNumber, p.TotalPages)).ToList();
    }

    public static string SanitizeSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Customer";
        
        string noDiacritics = CustomerNormalizer.RemoveDiacritics(text);
        
        var sb = new StringBuilder();
        bool prevHyphen = false;
        foreach (char c in noDiacritics)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevHyphen = false;
            }
            else
            {
                if (!prevHyphen && sb.Length > 0)
                {
                    sb.Append('-');
                    prevHyphen = true;
                }
            }
        }

        string slug = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "Customer" : slug;
    }

    public static List<BillPageData> PaginateBill(CustomerBill bill, int maxLinesPerPage = DefaultMaxLinesPerPage)
    {
        var includedOrders = bill.Orders.Where(o => o.IsIncluded).ToList();
        var pages = new List<BillPageData>();

        int totalIncludedLines = includedOrders.Sum(o => o.Lines.Count(l => l.IsIncluded));
        if (totalIncludedLines <= maxLinesPerPage)
        {
            var singlePage = new BillPageData
            {
                PageNumber = 1,
                TotalPages = 1
            };
            foreach (var order in includedOrders)
            {
                var lines = order.Lines.Where(l => l.IsIncluded).ToList();
                if (lines.Count == 0) continue;
                singlePage.Orders.Add(new BillPageOrderData
                {
                    Order = order,
                    IsContinuation = false,
                    ShowSubtotalOnThisPage = ShouldShowOrderSubtotal(order),
                    Lines = lines
                });
            }
            pages.Add(singlePage);
            return pages;
        }

        var currentPage = new BillPageData { PageNumber = 1 };
        pages.Add(currentPage);
        int currentLineCount = 0;

        foreach (var order in includedOrders)
        {
            var lines = order.Lines.Where(l => l.IsIncluded).ToList();
            if (lines.Count == 0) continue;

            int lineIndex = 0;
            bool isFirstChunk = true;

            while (lineIndex < lines.Count)
            {
                if (currentLineCount >= maxLinesPerPage)
                {
                    currentPage = new BillPageData { PageNumber = pages.Count + 1 };
                    pages.Add(currentPage);
                    currentLineCount = 0;
                }

                int availableOnPage = maxLinesPerPage - currentLineCount;
                int takeCount = Math.Min(availableOnPage, lines.Count - lineIndex);
                var pageLines = lines.GetRange(lineIndex, takeCount);

                bool finishesOrder = (lineIndex + takeCount >= lines.Count);
                bool showSubtotal = finishesOrder && ShouldShowOrderSubtotal(order);

                currentPage.Orders.Add(new BillPageOrderData
                {
                    Order = order,
                    IsContinuation = !isFirstChunk,
                    ShowSubtotalOnThisPage = showSubtotal,
                    Lines = pageLines
                });

                currentLineCount += takeCount;
                lineIndex += takeCount;
                isFirstChunk = false;
            }
        }

        int totalPages = pages.Count;
        foreach (var p in pages)
        {
            p.TotalPages = totalPages;
        }

        return pages;
    }

    public static BitmapSource RenderPageToBitmap(CustomerBill bill, BillPageData page, AppSettings? settings = null)
    {
        const double width = 1080;
        const double marginX = 64;
        const double contentWidth = width - (marginX * 2);

        // Pre-calculate dynamic canvas height for this page
        double calculatedHeight = MeasurePageHeight(bill, page, contentWidth, settings);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Background
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, calculatedHeight));

            // Accent header strip
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(26, 86, 219)), null, new Rect(0, 0, width, 8));

            double currentY = 48;

            // Brand & Title
            var typefaceDisplay = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var typefaceRegular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var typefaceSemiBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            string wsName = !string.IsNullOrWhiteSpace(settings?.WorkshopName) 
                ? settings.WorkshopName 
                : "XƯỞNG IN ẢNH CHUYÊN NGHIỆP";
            string wsSlogan = !string.IsNullOrWhiteSpace(settings?.WorkshopSlogan) 
                ? settings.WorkshopSlogan 
                : "Dịch vụ in ấn ảnh & Album chuyên nghiệp";
            string wsPhone = settings?.WorkshopPhone?.Trim() ?? string.Empty;
            string wsAddr = settings?.WorkshopAddress?.Trim() ?? string.Empty;

            BitmapSource? logoBitmap = TryLoadBitmap(settings?.WorkshopLogoPath);

            double headerTextX = marginX;
            double brandSectionStartY = currentY;

            if (logoBitmap != null)
            {
                // Draw logo: max height 56, width proportional
                double maxLogoH = 56;
                double maxLogoW = 140;
                double scale = Math.Min(maxLogoH / logoBitmap.PixelHeight, maxLogoW / logoBitmap.PixelWidth);
                double logoW = logoBitmap.PixelWidth * scale;
                double logoH = logoBitmap.PixelHeight * scale;

                dc.DrawImage(logoBitmap, new Rect(marginX, currentY + ((maxLogoH - logoH) / 2), logoW, logoH));
                headerTextX = marginX + logoW + 18;
            }

            // Draw Brand Name
            var brandFt = CreateText(wsName.ToUpperInvariant(), typefaceDisplay, 15, Color.FromRgb(26, 86, 219));
            dc.DrawText(brandFt, new Point(headerTextX, currentY));
            currentY += brandFt.Height + 2;

            // Draw Slogan
            var sloganFt = CreateText(wsSlogan, typefaceRegular, 13, Color.FromRgb(71, 85, 105));
            dc.DrawText(sloganFt, new Point(headerTextX, currentY));
            currentY += sloganFt.Height + 2;

            // Draw Contact info if available
            string contactInfo = string.Empty;
            if (!string.IsNullOrWhiteSpace(wsPhone) && !string.IsNullOrWhiteSpace(wsAddr))
            {
                contactInfo = $"Hotline: {wsPhone}  •  Địa chỉ: {wsAddr}";
            }
            else if (!string.IsNullOrWhiteSpace(wsPhone))
            {
                contactInfo = $"Hotline: {wsPhone}";
            }
            else if (!string.IsNullOrWhiteSpace(wsAddr))
            {
                contactInfo = $"Địa chỉ: {wsAddr}";
            }

            if (!string.IsNullOrWhiteSpace(contactInfo))
            {
                var contactFt = CreateText(contactInfo, typefaceRegular, 12, Color.FromRgb(100, 116, 139));
                dc.DrawText(contactFt, new Point(headerTextX, currentY));
                currentY += contactFt.Height + 2;
            }

            if (logoBitmap != null)
            {
                currentY = Math.Max(currentY, brandSectionStartY + 60);
            }

            currentY += 12; // Gap before invoice title

            string titleText = page.TotalPages > 1
                ? $"HÓA ĐƠN BÁN HÀNG — TRANG {page.PageNumber}/{page.TotalPages}"
                : "HÓA ĐƠN BÁN HÀNG";
            var titleFt = CreateText(titleText, typefaceDisplay, 26, Color.FromRgb(15, 23, 42));
            double titleX = (width - titleFt.Width) / 2;
            dc.DrawText(titleFt, new Point(titleX, currentY));
            currentY += titleFt.Height + 14;

            // Info Card (Bill number, Customer, Period, Date)
            var cardRect = new Rect(marginX, currentY, contentWidth, 100);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(248, 250, 252)), new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1), cardRect, 8, 8);

            // Left side info
            var billIdFt = CreateText($"Mã hóa đơn: {bill.BillNumber}", typefaceDisplay, 15, Color.FromRgb(26, 86, 219));
            dc.DrawText(billIdFt, new Point(marginX + 20, currentY + 16));

            string phoneStr = !string.IsNullOrWhiteSpace(bill.PhoneSnapshot) ? $" ({bill.PhoneSnapshot})" : "";
            var custFt = CreateText($"Khách hàng: {bill.CustomerNameSnapshot}{phoneStr}", typefaceSemiBold, 16, Color.FromRgb(30, 41, 59));
            dc.DrawText(custFt, new Point(marginX + 20, currentY + 44));

            // Right side info
            string periodStr = $"Kỳ bill: {FormatDate(bill.PeriodStart)} → {FormatDate(bill.PeriodEnd)}";
            var periodFt = CreateText(periodStr, typefaceRegular, 14, Color.FromRgb(71, 85, 105));
            dc.DrawText(periodFt, new Point(marginX + (contentWidth / 2), currentY + 16));

            var dateFt = CreateText($"Ngày xuất: {bill.CreatedAt:dd/MM/yyyy HH:mm}", typefaceRegular, 13, Color.FromRgb(100, 116, 139));
            dc.DrawText(dateFt, new Point(marginX + (contentWidth / 2), currentY + 44));

            currentY += 120;

            // Render order chunks on this page
            foreach (var orderChunk in page.Orders)
            {
                var order = orderChunk.Order;
                if (orderChunk.Lines.Count == 0) continue;

                // Order Header Banner
                double orderHeaderHeight = 36;
                var orderHeaderRect = new Rect(marginX, currentY, contentWidth, orderHeaderHeight);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(241, 245, 249)), null, orderHeaderRect, 6, 6);

                string orderPrefix = !string.IsNullOrWhiteSpace(order.OrderCodeSnapshot) ? $"[{order.OrderCodeSnapshot}] " : "";
                string contSuffix = orderChunk.IsContinuation ? " (tiếp theo)" : "";
                string orderTitle = $"ĐƠN HÀNG: {orderPrefix}{order.OrderNameSnapshot}{contSuffix}  (Ngày {FormatDate(order.OrderDateSnapshot)})";
                var orderTitleFt = CreateText(orderTitle, typefaceDisplay, 14, Color.FromRgb(30, 41, 59));
                dc.DrawText(orderTitleFt, new Point(marginX + 16, currentY + 8));

                currentY += orderHeaderHeight + 10;

                // Lines
                foreach (var line in orderChunk.Lines)
                {
                    double lineStartY = currentY;

                    if (line.BillingMethodSnapshot == BillingMethod.AlbumBasePlusExtra)
                    {
                        // Album product line
                        int sheetCount = line.SheetCount ?? line.ScannedQuantity;
                        var albumNameFt = CreateText($"Album {line.VariantSnapshot ?? line.ProductNameSnapshot} — {sheetCount} tờ", typefaceSemiBold, 15, Color.FromRgb(15, 23, 42));
                        dc.DrawText(albumNameFt, new Point(marginX + 16, currentY));

                        // Details
                        int inc = line.IncludedSheetsSnapshot ?? 10;
                        long baseP = line.BasePriceSnapshot ?? 0;
                        long extraP = line.ExtraSheetPriceSnapshot ?? 0;
                        int extraS = line.ExtraSheetCount ?? Math.Max(0, sheetCount - inc);

                        bool showPackageNote = sheetCount >= inc;

                        if (showPackageNote)
                        {
                            string albumDetail1 = $"• Gói chuẩn {inc} tờ: {baseP.ToString("#,##0", ViCulture)} đ";
                            var adFt1 = CreateText(albumDetail1, typefaceRegular, 13, Color.FromRgb(100, 116, 139));
                            dc.DrawText(adFt1, new Point(marginX + 28, currentY + 22));

                            if (extraS > 0)
                            {
                                long extraTot = extraS * extraP;
                                string albumDetail2 = $"• {extraS} tờ phát sinh × {extraP.ToString("#,##0", ViCulture)} đ: {extraTot.ToString("#,##0", ViCulture)} đ";
                                var adFt2 = CreateText(albumDetail2, typefaceRegular, 13, Color.FromRgb(100, 116, 139));
                                dc.DrawText(adFt2, new Point(marginX + 28, currentY + 40));
                                currentY += 62;
                            }
                            else
                            {
                                currentY += 46;
                            }
                        }
                        else
                        {
                            currentY += 28;
                        }

                        if (!string.IsNullOrWhiteSpace(line.Note))
                        {
                            var noteFt = CreateText($"• Ghi chú: {line.Note}", new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal), 12, Color.FromRgb(100, 116, 139));
                            dc.DrawText(noteFt, new Point(marginX + 28, currentY));
                            currentY += 18;
                        }

                        // Right total
                        var totalFt = CreateText(line.LineTotal.ToString("#,##0", ViCulture) + " đ", typefaceDisplay, 16, Color.FromRgb(15, 23, 42));
                        dc.DrawText(totalFt, new Point(width - marginX - 16 - totalFt.Width, lineStartY + 2));
                    }
                    else
                    {
                        // Photo print line
                        var prodFt = CreateText(line.ProductNameSnapshot, typefaceSemiBold, 15, Color.FromRgb(15, 23, 42));
                        dc.DrawText(prodFt, new Point(marginX + 16, currentY));

                        string calcStr = $"{line.BilledQuantity} × {line.BilledUnitPrice.ToString("#,##0", ViCulture)} đ";
                        var calcFt = CreateText(calcStr, typefaceRegular, 14, Color.FromRgb(71, 85, 105));
                        dc.DrawText(calcFt, new Point(marginX + 320, currentY));

                        var totalFt = CreateText(line.LineTotal.ToString("#,##0", ViCulture) + " đ", typefaceDisplay, 16, Color.FromRgb(15, 23, 42));
                        dc.DrawText(totalFt, new Point(width - marginX - 16 - totalFt.Width, currentY));

                        currentY += 28;

                        if (!string.IsNullOrWhiteSpace(line.Note))
                        {
                            var noteFt = CreateText($"• Ghi chú: {line.Note}", new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal), 12, Color.FromRgb(100, 116, 139));
                            dc.DrawText(noteFt, new Point(marginX + 28, currentY));
                            currentY += 18;
                        }
                    }

                    // Dotted divider between lines
                    var dotPen = new Pen(new SolidColorBrush(Color.FromRgb(241, 245, 249)), 1);
                    dc.DrawLine(dotPen, new Point(marginX + 16, currentY + 4), new Point(width - marginX - 16, currentY + 4));
                    currentY += 10;
                }

                // Order Subtotal Bar (omitted for 'Đơn mặc định')
                if (orderChunk.ShowSubtotalOnThisPage)
                {
                    double subtotalY = currentY + 4;
                    var subtotalValFt = CreateText(order.Subtotal.ToString("#,##0", ViCulture) + " đ", typefaceDisplay, 16, Color.FromRgb(30, 41, 59));
                    double valX = width - marginX - 16 - subtotalValFt.Width;
                    dc.DrawText(subtotalValFt, new Point(valX, subtotalY));

                    var subtotalLabelFt = CreateText("Tạm tính:", typefaceSemiBold, 14, Color.FromRgb(71, 85, 105));
                    double labelX = valX - subtotalLabelFt.Width - 14;
                    dc.DrawText(subtotalLabelFt, new Point(labelX, subtotalY));

                    currentY = subtotalY + 36;
                }
                else
                {
                    currentY += 16;
                }
            }

            if (page.IsLastPage)
            {
                // Divider before adjustments
                currentY += 10;
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1.5), new Point(marginX, currentY), new Point(width - marginX, currentY));
                currentY += 20;

                // Product Subtotal Row
                var pSubLabel = CreateText("Tổng cộng:", typefaceSemiBold, 15, Color.FromRgb(71, 85, 105));
                dc.DrawText(pSubLabel, new Point(marginX + 16, currentY));
                var pSubVal = CreateText(bill.ProductSubtotal.ToString("#,##0", ViCulture) + " đ", typefaceDisplay, 16, Color.FromRgb(30, 41, 59));
                dc.DrawText(pSubVal, new Point(width - marginX - 16 - pSubVal.Width, currentY));
                currentY += 30;

                // Adjustments
                foreach (var adj in bill.Adjustments)
                {
                    if (adj.Amount == 0) continue;

                    string sign = adj.Direction == AdjustmentDirection.Deduct ? "-" : "+";
                    Color adjColor = adj.Direction == AdjustmentDirection.Deduct ? Color.FromRgb(220, 38, 38) : Color.FromRgb(71, 85, 105);

                    var adjLabel = CreateText(adj.Label + ":", typefaceRegular, 14, Color.FromRgb(71, 85, 105));
                    dc.DrawText(adjLabel, new Point(marginX + 16, currentY));

                    var adjVal = CreateText($"{sign}{adj.Amount.ToString("#,##0", ViCulture)} đ", typefaceSemiBold, 15, adjColor);
                    dc.DrawText(adjVal, new Point(width - marginX - 16 - adjVal.Width, currentY));
                    currentY += 26;
                }

                currentY += 16;

                // GRAND TOTAL BANNER (Tinix Highlight Card)
                var totalBannerRect = new Rect(marginX, currentY, contentWidth, 80);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(239, 246, 255)), new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), 2), totalBannerRect, 8, 8);

                var grandTotalLabelFt = CreateText("TỔNG TIỀN THANH TOÁN", typefaceDisplay, 18, Color.FromRgb(30, 58, 138));
                dc.DrawText(grandTotalLabelFt, new Point(marginX + 24, currentY + 28));

                var grandTotalValFt = CreateText(bill.GrandTotal.ToString("#,##0", ViCulture) + " đ", typefaceDisplay, 28, Color.FromRgb(29, 78, 216));
                dc.DrawText(grandTotalValFt, new Point(width - marginX - 24 - grandTotalValFt.Width, currentY + 22));

                currentY += 104;

                // QR PAYMENT CARD (Tinix Card Style)
                bool isVietQr = (settings?.QrMode == QrDisplayMode.VietQrAuto || (settings?.QrMode == null && settings?.EnableVietQrOnBill == true))
                                && !string.IsNullOrWhiteSpace(settings?.BankAccountNumber);
                bool isCustomQr = (settings?.QrMode == QrDisplayMode.CustomImage)
                                  && !string.IsNullOrWhiteSpace(settings?.CustomQrImagePath)
                                  && File.Exists(settings.CustomQrImagePath);

                if (isVietQr || isCustomQr)
                {
                    double cardHeight = 170;
                    var qrCardRect = new Rect(marginX, currentY, contentWidth, cardHeight);

                    // Card background & border
                    dc.DrawRoundedRectangle(
                        new SolidColorBrush(Color.FromRgb(248, 250, 252)), 
                        new Pen(new SolidColorBrush(Color.FromRgb(226, 232, 240)), 1.5), 
                        qrCardRect, 8, 8);

                    // Draw QR Code
                    double qrSize = 138;
                    double qrMargin = 16;
                    var qrRect = new Rect(marginX + qrMargin, currentY + qrMargin, qrSize, qrSize);

                    // White backing for QR
                    dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(203, 213, 225)), 1), qrRect, 6, 6);

                    // Draw inner QR Code vector or custom image with margin
                    var innerQrRect = new Rect(qrRect.X + 4, qrRect.Y + 4, qrRect.Width - 8, qrRect.Height - 8);

                    if (isVietQr)
                    {
                        string qrPayload = VietQrGenerator.BuildVietQrPayload(
                            settings!.BankBinOrCode, 
                            settings.BankAccountNumber, 
                            bill.GrandTotal, 
                            bill.BillNumber);

                        VietQrGenerator.RenderQrCode(dc, innerQrRect, qrPayload);
                    }
                    else if (isCustomQr)
                    {
                        var customQrBmp = TryLoadBitmap(settings!.CustomQrImagePath);
                        if (customQrBmp != null)
                        {
                            dc.DrawImage(customQrBmp, innerQrRect);
                        }
                    }

                    // Text Info next to QR
                    double textX = marginX + qrSize + 36;
                    double textY = currentY + 18;

                    string qrTitle = isVietQr 
                        ? "QUÉT MÃ VIETQR ĐỂ THANH TOÁN TỰ ĐỘNG" 
                        : "QUÉT MÃ QR ĐỂ THANH TOÁN";
                    var qrTitleFt = CreateText(qrTitle, typefaceDisplay, 15, Color.FromRgb(30, 58, 138));
                    dc.DrawText(qrTitleFt, new Point(textX, textY));
                    textY += 24;

                    if (!string.IsNullOrWhiteSpace(settings?.BankAccountNumber))
                    {
                        string bankName = VietnameseBanks.GetShortNameOrBin(settings.BankBinOrCode);
                        string bankDisplay = !string.IsNullOrWhiteSpace(bankName) ? $"Ngân hàng: {bankName}" : "Ngân hàng";
                        var bankInfoFt = CreateText($"{bankDisplay}  •  Số TK: {settings.BankAccountNumber}", typefaceDisplay, 14, Color.FromRgb(15, 23, 42));
                        dc.DrawText(bankInfoFt, new Point(textX, textY));
                        textY += 22;

                        if (!string.IsNullOrWhiteSpace(settings.BankAccountName))
                        {
                            var ownerFt = CreateText($"Chủ tài khoản: {settings.BankAccountName.ToUpper()}", typefaceSemiBold, 13, Color.FromRgb(51, 65, 85));
                            dc.DrawText(ownerFt, new Point(textX, textY));
                            textY += 20;
                        }

                        var memoFt = CreateText($"Nội dung CK: {bill.BillNumber}  (Số tiền: {bill.GrandTotal.ToString("#,##0", ViCulture)} đ)", typefaceRegular, 12, Color.FromRgb(71, 85, 105));
                        dc.DrawText(memoFt, new Point(textX, textY));
                        textY += 18;

                        string hint = isVietQr
                            ? "Hỗ trợ mọi app ngân hàng (VCB, MB, Tech, Vietin, MoMo...)"
                            : "Quét mã bằng ứng dụng ngân hàng hoặc ví điện tử";
                        var subHintFt = CreateText(hint, typefaceRegular, 11, Color.FromRgb(148, 163, 184));
                        dc.DrawText(subHintFt, new Point(textX, textY));
                    }
                    else
                    {
                        var memoFt = CreateText($"Nội dung CK: {bill.BillNumber}  (Số tiền: {bill.GrandTotal.ToString("#,##0", ViCulture)} đ)", typefaceRegular, 13, Color.FromRgb(71, 85, 105));
                        dc.DrawText(memoFt, new Point(textX, textY));
                        textY += 22;

                        var subHintFt = CreateText("Quét mã bằng ứng dụng ngân hàng hoặc ví điện tử", typefaceRegular, 11, Color.FromRgb(148, 163, 184));
                        dc.DrawText(subHintFt, new Point(textX, textY));
                    }

                    currentY += cardHeight + 20;
                }

                // Optional note
                if (!string.IsNullOrWhiteSpace(bill.Note))
                {
                    var noteLabelFt = CreateText($"Ghi chú: {bill.Note}", new Typeface(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal), 14, Color.FromRgb(71, 85, 105));
                    dc.DrawText(noteLabelFt, new Point(marginX + 16, currentY));
                    currentY += 36;
                }

                // Footer
                string footerMsg = !string.IsNullOrWhiteSpace(settings?.InvoiceFooterMessage)
                    ? settings.InvoiceFooterMessage
                    : "Cảm ơn quý khách đã tin tưởng và ủng hộ dịch vụ!";

                string footerText = page.TotalPages > 1
                    ? $"Trang {page.PageNumber}/{page.TotalPages} — {footerMsg}"
                    : footerMsg;
                var footerFt = CreateText(footerText, typefaceRegular, 13, Color.FromRgb(148, 163, 184));
                dc.DrawText(footerFt, new Point(width / 2 - (footerFt.Width / 2), currentY));
            }
            else
            {
                // Intermediate page footer & continuation notice
                currentY += 16;
                var contFt = CreateText($"(Hóa đơn còn tiếp tục ở Trang {page.PageNumber + 1}/{page.TotalPages} ...)", typefaceSemiBold, 14, Color.FromRgb(37, 99, 235));
                dc.DrawText(contFt, new Point(marginX + 16, currentY));
                currentY += 40;

                string footerMsg = !string.IsNullOrWhiteSpace(settings?.InvoiceFooterMessage)
                    ? settings.InvoiceFooterMessage
                    : "Cảm ơn quý khách đã tin tưởng và ủng hộ dịch vụ!";

                var footerFt = CreateText($"Trang {page.PageNumber}/{page.TotalPages} — {footerMsg}", typefaceRegular, 13, Color.FromRgb(148, 163, 184));
                dc.DrawText(footerFt, new Point(width / 2 - (footerFt.Width / 2), currentY));
            }
        }

        // Render to Bitmap and convert to in-memory BitmapImage
        int pixelHeight = (int)Math.Ceiling(calculatedHeight);
        var rtb = new RenderTargetBitmap(1080, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();

        return bitmap;
    }

    public static void SaveBitmapToJpeg(BitmapSource bitmap, string destinationFilePath, int quality = 95)
    {
        var encoder = new JpegBitmapEncoder
        {
            QualityLevel = quality
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        WpfShippingLabelExporter.WriteFileResiliently(destinationFilePath, ms.ToArray());
    }

    private static void RenderPageToJpeg(CustomerBill bill, BillPageData page, string destinationFilePath, AppSettings? settings = null)
    {
        var bitmap = RenderPageToBitmap(bill, page, settings);
        SaveBitmapToJpeg(bitmap, destinationFilePath);
    }

    private static double MeasureHeaderHeight(AppSettings? settings)
    {
        double h = 48; // top margin
        bool hasLogo = !string.IsNullOrWhiteSpace(settings?.WorkshopLogoPath) && File.Exists(settings.WorkshopLogoPath);
        bool hasContact = !string.IsNullOrWhiteSpace(settings?.WorkshopPhone) || !string.IsNullOrWhiteSpace(settings?.WorkshopAddress);

        double brandH = hasContact ? 76 : 56;
        if (hasLogo && brandH < 60) brandH = 60;
        h += brandH + 12; // gap before Title
        h += 44; // Title
        h += 100; // Info Card
        h += 20; // Spacing after info card
        return h;
    }

    private static double MeasurePageHeight(CustomerBill bill, BillPageData page, double contentWidth, AppSettings? settings = null)
    {
        // Header dynamic height
        double h = MeasureHeaderHeight(settings);

        foreach (var orderChunk in page.Orders)
        {
            if (orderChunk.Lines.Count == 0) continue;

            h += 46; // order header

            foreach (var line in orderChunk.Lines)
            {
                if (line.BillingMethodSnapshot == BillingMethod.AlbumBasePlusExtra)
                {
                    int sheetCount = line.SheetCount ?? line.ScannedQuantity;
                    int inc = line.IncludedSheetsSnapshot ?? 10;
                    if (sheetCount < inc)
                    {
                        h += 38;
                    }
                    else
                    {
                        int extraS = line.ExtraSheetCount ?? 0;
                        h += extraS > 0 ? 72 : 56;
                    }
                }
                else
                {
                    h += 38;
                }

                if (!string.IsNullOrWhiteSpace(line.Note))
                {
                    h += 18;
                }
            }

            if (orderChunk.ShowSubtotalOnThisPage)
            {
                h += 40; // order subtotal bar
            }
            else
            {
                h += 16; // spacing after order
            }
        }

        if (page.IsLastPage)
        {
            // Adjustments section
            h += 60; // divider and product subtotal
            int activeAdjs = bill.Adjustments.Count(a => a.Amount > 0);
            h += activeAdjs * 26;

            // Grand total banner
            h += 110;

            // QR card
            bool isVietQr = (settings?.QrMode == QrDisplayMode.VietQrAuto || (settings?.QrMode == null && settings?.EnableVietQrOnBill == true))
                            && !string.IsNullOrWhiteSpace(settings?.BankAccountNumber);
            bool isCustomQr = (settings?.QrMode == QrDisplayMode.CustomImage)
                              && !string.IsNullOrWhiteSpace(settings?.CustomQrImagePath)
                              && File.Exists(settings.CustomQrImagePath);

            if (isVietQr || isCustomQr)
            {
                h += 190;
            }

            // Note
            if (!string.IsNullOrWhiteSpace(bill.Note))
            {
                h += 36;
            }

            // Footer & padding
            h += 70;
        }
        else
        {
            // Continuation notice & footer
            h += 100;
        }

        return Math.Max(600, h);
    }

    public static BitmapSource? TryLoadBitmap(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(Path.GetFullPath(filePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static FormattedText CreateText(string text, Typeface typeface, double fontSize, Color color)
    {
        return new FormattedText(
            text,
            ViCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(color),
            VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip
        );
    }

    private static string FormatDate(string yyyyMmDd)
    {
        if (DateTime.TryParse(yyyyMmDd, out var dt))
        {
            return dt.ToString("dd/MM/yyyy");
        }
        return yyyyMmDd;
    }

    public static bool ShouldShowOrderSubtotal(CustomerBillOrder order)
    {
        if (string.IsNullOrWhiteSpace(order.OrderNameSnapshot))
            return false;

        string trimmed = order.OrderNameSnapshot.Trim();
        if (string.Equals(trimmed, "Đơn mặc định", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Don mac dinh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}

public class BillPageData
{
    public int PageNumber { get; set; }
    public int TotalPages { get; set; }
    public List<BillPageOrderData> Orders { get; set; } = new();
    public bool IsFirstPage => PageNumber == 1;
    public bool IsLastPage => PageNumber == TotalPages;
}

public class BillPageOrderData
{
    public CustomerBillOrder Order { get; set; } = null!;
    public bool IsContinuation { get; set; }
    public bool ShowSubtotalOnThisPage { get; set; }
    public List<CustomerBillLine> Lines { get; set; } = new();
}
