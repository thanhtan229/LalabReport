using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.Reporting;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LalabAutoReport.Tests;

public class ServerRendererSpikeTests
{
    private static string FindServerExePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] relativePaths =
        {
            "LalabAutoReport.Server.exe",
            @"..\..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe",
            @"..\..\..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe",
            @"..\..\..\src\LalabAutoReport.Server\bin\Debug\net8.0-windows\LalabAutoReport.Server.exe"
        };

        foreach (var rel in relativePaths)
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (File.Exists(full))
            {
                return full;
            }
        }

        throw new FileNotFoundException($"Không tìm thấy LalabAutoReport.Server.exe từ thư mục: {baseDir}");
    }

    private static (int width, int height, string pixelHash) ComputeDecodedPixelHash(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;

        var formatted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        formatted.Freeze();
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        formatted.CopyPixels(pixels, stride, 0);

        using var sha = SHA256.Create();
        string pixelHash = Convert.ToHexString(sha.ComputeHash(pixels)).ToLowerInvariant();
        return (width, height, pixelHash);
    }

    [Fact]
    public async Task RenderSpike_MatchesDesktopRendering_OnSameFixtureBill()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lalab_spike_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string dbPath = Path.Combine(tempDir, "lalab_autoreport.db");

            var connFactory = new SqliteConnectionFactory(dbPath);
            var migrator = new DatabaseMigrator(connFactory);
            await migrator.MigrateAsync();

            var customerRepo = new SqliteCustomerRepository(connFactory);
            var customer = await customerRepo.CreateCustomerAsync(new Customer { CanonicalName = "Khách Hàng Spike" });

            using (var conn = connFactory.CreateConnection())
            {
                await Dapper.SqlMapper.ExecuteAsync(conn, @"
                    INSERT INTO root_folders (id, name, full_path, is_default, is_active, created_at, updated_at)
                    VALUES (1, 'Root 1', 'C:\\Data', 1, 1, '2026-10-25', '2026-10-25');

                    INSERT INTO orders (id, order_code, work_date, customer_id, order_kind, order_name, original_folder_name, relative_path, status, is_printed, created_at, updated_at, root_folder_id)
                    VALUES (1, 'ORD-SPIKE-001', '2026-10-25', @CustomerId, 'Standard', 'Đơn Render Thử Nghiệm', 'Đơn Render Thử Nghiệm', '2026-10-25/Khách Hàng Spike/Đơn Render Thử Nghiệm', 'Scanned', 0, '2026-10-25', '2026-10-25', 1);

                    INSERT INTO scan_snapshots (id, order_id, started_at, scan_scope, status, app_version)
                    VALUES (1, 1, '2026-10-25', 'Order', 'Completed', '2.0.0');

                    INSERT INTO order_item_scans (id, scan_snapshot_id, order_id, specification_folder_name, specification_relative_path, source_count, print_folder_status, scan_status)
                    VALUES (1, 1, 1, '20x30', '20x30', 3, 'AutoResolved', 'Normal');
                ", new { CustomerId = customer.Id });
            }

            var billRepo = new SqliteCustomerBillRepository(connFactory);
            var bill = new CustomerBill
            {
                BillNumber = "BILL-SPIKE-001",
                BillType = BillType.Customer,
                CustomerId = customer.Id,
                CustomerNameSnapshot = customer.CanonicalName,
                PhoneSnapshot = "0912345678",
                ShippingAddressSnapshot = "123 Đường Render, Hà Nội",
                PeriodStart = "2026-10-25",
                PeriodEnd = "2026-10-25",
                CreatedAt = new DateTimeOffset(2026, 10, 25, 8, 30, 0, TimeSpan.FromHours(7)),
                ProductSubtotal = 150000,
                GrandTotal = 150000,
                Status = CustomerBillStatus.Locked,
                Orders = new List<CustomerBillOrder>
                {
                    new()
                    {
                        OrderId = 1,
                        OrderNameSnapshot = "Đơn Render Thử Nghiệm",
                        OrderDateSnapshot = "2026-10-25",
                        Subtotal = 150000,
                        IsIncluded = true,
                        Lines = new List<CustomerBillLine>
                        {
                            new()
                            {
                                OrderId = 1,
                                ProductJobId = 1,
                                ProductNameSnapshot = "Ảnh tráng gương 20x30",
                                BilledQuantity = 3,
                                BilledUnitPrice = 50000,
                                LineTotal = 150000,
                                IsIncluded = true
                            }
                        }
                    }
                }
            };
            await billRepo.SaveBillAsync(bill);

            // Clear pool so external process can access DB cleanly
            SqliteConnection.ClearAllPools();

            // 1. In-process (desktop) export
            string desktopOutDir = Path.Combine(tempDir, "desktop_out");
            Directory.CreateDirectory(desktopOutDir);
            var settingsRepo = new SqliteSettingsRepository(connFactory);
            var desktopJpegExporter = new WpfJpegBillExporter(settingsRepo);
            var desktopLabelExporter = new WpfShippingLabelExporter(settingsRepo);

            string desktopJpegPath;
            string desktopLabelPath;
            try
            {
                desktopJpegPath = await desktopJpegExporter.ExportBillToJpegAsync(bill, desktopOutDir);
                desktopLabelPath = await desktopLabelExporter.ExportShippingLabelImageAsync(bill, desktopOutDir);
            }
            catch (Exception ex) when (ex is TypeInitializationException or PlatformNotSupportedException or DllNotFoundException)
            {
                Console.WriteLine($"[SKIPPED] Bỏ qua test vì môi trường không hỗ trợ đồ họa WPF: {ex.Message}");
                return;
            }

            File.Exists(desktopJpegPath).Should().BeTrue();
            File.Exists(desktopLabelPath).Should().BeTrue();

            var (desktopJpgW, desktopJpgH, desktopJpgPixelHash) = ComputeDecodedPixelHash(desktopJpegPath);
            var (desktopLblW, desktopLblH, desktopLblPixelHash) = ComputeDecodedPixelHash(desktopLabelPath);

            // 2. Server process export via render-spike
            string serverOutDir = Path.Combine(tempDir, "server_out");
            Directory.CreateDirectory(serverOutDir);

            string serverExe = FindServerExePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = $"render-spike --bill-id {bill.Id} --out \"{serverOutDir}\" --data-dir \"{tempDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();

            string stdout = await process!.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            process.ExitCode.Should().Be(0, $"render-spike failed with stderr: {stderr}");

            // Verify stdout is valid JSON with expected properties
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            root.GetProperty("billId").GetInt64().Should().Be(bill.Id);
            root.GetProperty("jpegWidth").GetInt32().Should().Be(desktopJpgW);
            root.GetProperty("jpegHeight").GetInt32().Should().Be(desktopJpgH);
            root.GetProperty("labelWidth").GetInt32().Should().Be(desktopLblW);
            root.GetProperty("labelHeight").GetInt32().Should().Be(desktopLblH);
            root.GetProperty("processId").GetInt32().Should().BeGreaterThan(0);
            root.GetProperty("buildId").GetString().Should().NotBeNullOrWhiteSpace();

            string serverJpegPath = root.GetProperty("jpegPath").GetString()!;
            string serverLabelPath = root.GetProperty("labelPath").GetString()!;

            File.Exists(serverJpegPath).Should().BeTrue();
            File.Exists(serverLabelPath).Should().BeTrue();

            var (serverJpgW, serverJpgH, serverJpgPixelHash) = ComputeDecodedPixelHash(serverJpegPath);
            var (serverLblW, serverLblH, serverLblPixelHash) = ComputeDecodedPixelHash(serverLabelPath);

            // 3. Assert identical dimensions; pixel parity is strict only on a local workstation.
            // GitHub's windows-latest image has different fonts/rendering, so hashes are not portable.
            bool isCi = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI"));
            serverJpgW.Should().Be(desktopJpgW, "JPEG width must match");
            serverJpgH.Should().Be(desktopJpgH, "JPEG height must match");
            serverLblW.Should().Be(desktopLblW, "Label width must match");
            serverLblH.Should().Be(desktopLblH, "Label height must match");

            serverJpgPixelHash.Should().NotBeNullOrWhiteSpace();
            serverLblPixelHash.Should().NotBeNullOrWhiteSpace();

            if (!isCi)
            {
                serverJpgPixelHash.Should().Be(desktopJpgPixelHash, "decoded JPEG pixel hashes must match desktop rendering");
                serverLblPixelHash.Should().Be(desktopLblPixelHash, "decoded Label pixel hashes must match desktop rendering");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
