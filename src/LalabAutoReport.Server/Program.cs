using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Dapper;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.ApplicationCore.Auth;
using LalabAutoReport.Infrastructure.Logging;
using LalabAutoReport.Server.Health;
using LalabAutoReport.Server.Hosting;
using LalabAutoReport.Server.Inventory;
using LalabAutoReport.Server.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LalabAutoReport.Server;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // 1. Parse Command Line Arguments
        if (!TryParseArguments(args, out var cliOptions, out var parseError))
        {
            Console.Error.WriteLine($"Lỗi tham số dòng lệnh: {parseError}");
            PrintUsage();
            return 2;
        }

        string dataDir = cliOptions.DataDir;

        // Special handling for subcommand: inventory --dry-run
        // MUST run BEFORE logging setup into dataDir, BEFORE ServerInstanceGuard, and BEFORE DB lifecycle/migration
        if (cliOptions.IsInventoryDryRun)
        {
            return await ExecuteInventoryDryRunAsync(dataDir);
        }

        string logDir = Path.Combine(dataDir, "logs");

        // 2. Initialize Serilog rolling file logger
        LoggingSetup.Initialize(logDir);
        Log.Information("=== Khởi động Lalab Auto Report Server ===");
        Log.Information("Data Directory: {DataDir}", dataDir);
        Log.Information("Health Port: {HealthPort}", cliOptions.HealthPort);

        // 3. Acquire Single-Instance Guard (Named Mutex + server.lock FileStream)
        using var guard = new ServerInstanceGuard(dataDir);
        if (!guard.TryAcquire(out var lockError))
        {
            string message = $"[LỖI KHỞI ĐỘNG] Không thể chiếm quyền khởi động server: {lockError}";
            Console.Error.WriteLine(message);
            Log.Error(message);
            LoggingSetup.CloseAndFlush();
            return 3;
        }

        Log.Information("Đã chiếm quyền ServerInstanceGuard thành công.");

        try
        {
            // Resolve LAN write capabilities
            HashSet<string> enabledCapabilities = string.Equals(cliOptions.LanWrites, "orders", StringComparison.OrdinalIgnoreCase)
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    LalabAutoReport.Contracts.Capabilities.OrdersSetDelivered,
                    LalabAutoReport.Contracts.Capabilities.OrdersSetPrinted,
                    LalabAutoReport.Contracts.Capabilities.OrdersEditNote
                }
                : new HashSet<string>(StringComparer.Ordinal);

            Log.Information("LAN Writes mode: {LanWrites} ({Count} capabilities enabled)", cliOptions.LanWrites, enabledCapabilities.Count);

            // 4. Configure DI services via AddLalabCore
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSerilog(dispose: true));
            services.AddLalabCore(options =>
            {
                options.DataDirectory = dataDir;
                options.ThumbnailsDirectory = Path.Combine(dataDir, "Thumbnails");
                options.EnableCloudSync = false;
                options.EnableMobileWebServer = true;
                options.MobileWebServerLoopbackOnly = true;
                options.MobileWebServerEnabledWriteCapabilities = enabledCapabilities;
                options.MobileWebServerPort = cliOptions.Port;
            });

            using var serviceProvider = services.BuildServiceProvider();

            var connectionFactory = serviceProvider.GetRequiredService<ISqliteConnectionFactory>();
            var backupService = serviceProvider.GetRequiredService<IDatabaseBackupService>();
            var migrator = serviceProvider.GetRequiredService<IDatabaseMigrator>();
            var lifecycleLogger = serviceProvider.GetService<ILogger<ServerDataLifecycle>>();

            // 5. Fail-Closed Database Lifecycle
            var lifecycle = new ServerDataLifecycle(
                connectionFactory.DatabasePath,
                backupService,
                migrator,
                lifecycleLogger);

            bool initSuccess = await lifecycle.InitializeDatabaseAsync();
            if (!initSuccess)
            {
                Log.Error("Fail-Closed: Khởi tạo database thất bại. Dừng server.");
                return 4;
            }

            // Run plaintext PIN scrubber and purge expired/revoked sessions (>90 days)
            try
            {
                var scrubber = serviceProvider.GetRequiredService<CredentialPlaintextScrubber>();
                await scrubber.ScrubAsync();

                var sessionStore = serviceProvider.GetRequiredService<ISessionStore>();
                await sessionStore.PurgeExpiredAndRevokedSessionsAsync(TimeSpan.FromDays(90));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Lỗi khi chạy Credential scrubber hoặc session purge lúc khởi động: {Message}", ex.Message);
            }

            // One-time startup database integrity check (logged once, not run per-request)
            try
            {
                using var startupConn = connectionFactory.CreateConnection();
                var integrity = startupConn.ExecuteScalar<string>("PRAGMA quick_check;");
                Log.Information("Kiểm tra toàn vẹn database lúc khởi động (PRAGMA quick_check): {Result}", integrity);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Không thể chạy PRAGMA quick_check lúc khởi động: {Message}", ex.Message);
            }

            // 6. Check for subcommand: render-spike --bill-id <id> --out <dir>
            if (cliOptions.IsRenderSpike)
            {
                Log.Information("Thực thi render-spike cho Bill ID {BillId}, thư mục xuất: {OutDir}",
                    cliOptions.RenderSpikeBillId, cliOptions.RenderSpikeOutDir);
                try
                {
                    var billRepo = serviceProvider.GetRequiredService<ICustomerBillRepository>();
                    var jpegExporter = serviceProvider.GetRequiredService<IJpegBillExporter>();
                    var labelExporter = serviceProvider.GetRequiredService<IShippingLabelExporter>();

                    var bill = await billRepo.GetByIdAsync(cliOptions.RenderSpikeBillId!.Value);
                    if (bill == null)
                    {
                        string notFoundMsg = $"Không tìm thấy hóa đơn với ID = {cliOptions.RenderSpikeBillId.Value}";
                        Console.Error.WriteLine(notFoundMsg);
                        Log.Error(notFoundMsg);
                        return 6;
                    }

                    Directory.CreateDirectory(cliOptions.RenderSpikeOutDir!);

                    string jpegPath = await jpegExporter.ExportBillToJpegAsync(bill, cliOptions.RenderSpikeOutDir!);
                    string labelPath = await labelExporter.ExportShippingLabelImageAsync(bill, cliOptions.RenderSpikeOutDir!);

                    if (!File.Exists(jpegPath))
                    {
                        throw new FileNotFoundException("Tập tin JPEG xuất ra không tồn tại", jpegPath);
                    }

                    if (!File.Exists(labelPath))
                    {
                        throw new FileNotFoundException("Tập tin tem nhãn xuất ra không tồn tại", labelPath);
                    }

                    long jpegBytes = new FileInfo(jpegPath).Length;
                    string jpegSha256;
                    using (var sha = SHA256.Create())
                    using (var stream = File.OpenRead(jpegPath))
                    {
                        jpegSha256 = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                    }

                    int jpegWidth;
                    int jpegHeight;
                    using (var stream = File.OpenRead(jpegPath))
                    {
                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
                        var frame = decoder.Frames[0];
                        jpegWidth = frame.PixelWidth;
                        jpegHeight = frame.PixelHeight;
                    }

                    long labelBytes = new FileInfo(labelPath).Length;
                    string labelSha256;
                    using (var sha = SHA256.Create())
                    using (var stream = File.OpenRead(labelPath))
                    {
                        labelSha256 = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                    }

                    int labelWidth;
                    int labelHeight;
                    using (var stream = File.OpenRead(labelPath))
                    {
                        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
                        var frame = decoder.Frames[0];
                        labelWidth = frame.PixelWidth;
                        labelHeight = frame.PixelHeight;
                    }

                    int processId = Environment.ProcessId;
                    int sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                    string buildId = new ServerHealthInfoProvider(dataDir).BuildId;

                    var spikeResult = new
                    {
                        billId = bill.Id,
                        jpegPath = Path.GetFullPath(jpegPath),
                        jpegSha256 = jpegSha256,
                        jpegBytes = jpegBytes,
                        jpegWidth = jpegWidth,
                        jpegHeight = jpegHeight,
                        labelPath = Path.GetFullPath(labelPath),
                        labelSha256 = labelSha256,
                        labelBytes = labelBytes,
                        labelWidth = labelWidth,
                        labelHeight = labelHeight,
                        processId = processId,
                        sessionId = sessionId,
                        buildId = buildId
                    };

                    string jsonOutput = JsonSerializer.Serialize(spikeResult, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    Console.WriteLine(jsonOutput);
                    Log.Information("Hoàn tất render-spike thành công cho Bill ID {BillId}.", bill.Id);
                    return 0;
                }
                catch (Exception ex)
                {
                    string errorMsg = $"[LỖI RENDER SPIKE] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";
                    Console.Error.WriteLine(errorMsg);
                    Log.Error(ex, "Lỗi khi thực thi render-spike");
                    return 6;
                }
            }

            // 7. Start Mobile Web Server (Read-only, Loopback-only)
            var mobileServer = serviceProvider.GetRequiredService<IMobileWebServer>();
            await mobileServer.StartAsync();
            if (mobileServer.IsRunning)
            {
                Log.Information("Mobile Web Server (Read-Only Loopback) đã khởi động: {Url}", mobileServer.LocalUrl);
            }
            else
            {
                Log.Error("Mobile Web Server không thể khởi động: {Error}. Sẽ báo NotReady trên /health/ready.",
                    mobileServer.GetStatus().ErrorMessage ?? "Lỗi không xác định");
            }

            // 8. Health Info Provider setup with dynamic single-probe readiness evaluation (Blocker A)
            Func<Task<ServerReadinessResult>> readinessEvaluator = async () =>
            {
                var components = new Dictionary<string, string>();
                bool guardAcquired = guard.IsAcquired;
                bool mobileOk = mobileServer.IsRunning;

                components["mobileApi"] = mobileOk ? "ready" : "unavailable";

                if (!guardAcquired)
                {
                    components["database"] = "unavailable:lock_not_held";
                    return new ServerReadinessResult(false, 0, components);
                }

                int schemaVersion = 0;
                try
                {
                    var csb = new SqliteConnectionStringBuilder
                    {
                        DataSource = connectionFactory.DatabasePath,
                        Mode = SqliteOpenMode.ReadOnly,
                        DefaultTimeout = 2
                    };

                    using var conn = new SqliteConnection(csb.ToString());
                    await conn.OpenAsync();

                    using var cmd = conn.CreateCommand();
                    cmd.CommandTimeout = 2;
                    cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                    var versionScalar = await cmd.ExecuteScalarAsync();
                    schemaVersion = Convert.ToInt32(versionScalar);

                    if (schemaVersion < DatabaseMigrator.LatestVersion)
                    {
                        components["database"] = "unavailable:schema_outdated";
                        return new ServerReadinessResult(false, schemaVersion, components);
                    }

                    // Cheap bounded availability probe (SELECT 1 FROM orders LIMIT 1)
                    cmd.CommandText = "SELECT 1 FROM orders LIMIT 1;";
                    await cmd.ExecuteScalarAsync();

                    components["database"] = "ready";
                }
                catch (Exception ex)
                {
                    string reason = ex is SqliteException sqlEx
                        ? $"unavailable:sql_error_{sqlEx.SqliteErrorCode}"
                        : "unavailable:probe_failed";
                    components["database"] = reason;
                    return new ServerReadinessResult(false, 0, components);
                }

                bool isReady = mobileOk;
                return new ServerReadinessResult(isReady, schemaVersion, components);
            };

            var healthProvider = new ServerHealthInfoProvider(
                startedAtUtc: DateTimeOffset.UtcNow,
                schemaVersionProvider: () => DatabaseMigrator.LatestVersion,
                isReadyProvider: () => guard.IsAcquired && mobileServer.IsRunning,
                componentsProvider: () => new Dictionary<string, string>
                {
                    ["database"] = guard.IsAcquired ? "ready" : "unavailable",
                    ["mobileApi"] = mobileServer.IsRunning ? "ready" : "unavailable"
                },
                readinessEvaluatorAsync: readinessEvaluator,
                capabilitiesProvider: () => enabledCapabilities);

            // 9. Start Loopback Health Endpoints with Minimal WebApplication
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddLogging(b => b.AddSerilog(Log.Logger, dispose: true));
            builder.Host.UseWindowsService();
            builder.Services.AddSingleton(healthProvider);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, cliOptions.HealthPort);
            });

            var app = builder.Build();

            app.MapGet("/health/live", () => Results.Json(healthProvider.GetLiveHealth()));
            app.MapGet("/health/ready", async () =>
            {
                var (isReady, health) = await healthProvider.GetReadyHealthAsync();
                return isReady
                    ? Results.Json(health)
                    : Results.Json(health, statusCode: 503);
            });

            Log.Information("Health endpoints đang lắng nghe tại http://127.0.0.1:{Port}", cliOptions.HealthPort);

            // Run app with injected singleton healthProvider
            app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            {
                Log.Information("Nhận tín hiệu dừng Server...");
                try
                {
                    mobileServer.StopAsync().GetAwaiter().GetResult();
                    Log.Information("Mobile Web Server đã dừng.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Lỗi khi dừng Mobile Web Server.");
                }
            });

            // Map singleton for endpoint injection
            app.MapGet("/", () => Results.Text("Lalab Auto Report Server"));

            await app.RunAsync();

            try
            {
                await mobileServer.StopAsync();
            }
            catch { }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Lỗi nghiêm trọng không được xử lý tại Server host.");
            return 1;
        }
        finally
        {
            Log.Information("Đang giải phóng tài nguyên và thoát server...");
            LoggingSetup.CloseAndFlush();
        }
    }

    private static bool TryParseArguments(string[] args, out ServerCommandLineOptions options, out string? error)
    {
        options = new ServerCommandLineOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (string.Equals(arg, "--data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    options.DataDir = Path.GetFullPath(args[++i]);
                }
                else
                {
                    error = "Thiếu đường dẫn sau tham số --data-dir";
                    return false;
                }
            }
            else if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[++i], out int port) && port >= 0 && port <= 65535)
                {
                    options.Port = port;
                }
                else
                {
                    error = "Giá trị cổng sau --port không hợp lệ (phải từ 0 đến 65535)";
                    return false;
                }
            }
            else if (string.Equals(arg, "--health-port", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[++i], out int port) && port > 0 && port <= 65535)
                {
                    options.HealthPort = port;
                }
                else
                {
                    error = "Giá trị cổng sau --health-port không hợp lệ (phải từ 1 đến 65535)";
                    return false;
                }
            }
            else if (string.Equals(arg, "--lan-writes", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    string mode = args[++i].ToLowerInvariant();
                    if (mode is "none" or "orders")
                    {
                        options.LanWrites = mode;
                    }
                    else
                    {
                        error = "Giá trị sau --lan-writes phải là 'none' hoặc 'orders'";
                        return false;
                    }
                }
                else
                {
                    error = "Thiếu giá trị sau tham số --lan-writes (chọn 'none' hoặc 'orders')";
                    return false;
                }
            }
            else if (string.Equals(arg, "inventory", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && string.Equals(args[i + 1], "--dry-run", StringComparison.OrdinalIgnoreCase))
                {
                    options.IsInventoryDryRun = true;
                    i++;
                }
                else
                {
                    error = "Lệnh 'inventory' bắt buộc phải kèm cờ '--dry-run' (ví dụ: inventory --dry-run)";
                    return false;
                }
            }
            else if (string.Equals(arg, "render-spike", StringComparison.OrdinalIgnoreCase))
            {
                options.IsRenderSpike = true;
            }
            else if (string.Equals(arg, "--bill-id", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && long.TryParse(args[++i], out long billId) && billId > 0)
                {
                    options.RenderSpikeBillId = billId;
                }
                else
                {
                    error = "Giá trị sau --bill-id phải là số nguyên dương hợp lệ";
                    return false;
                }
            }
            else if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    options.RenderSpikeOutDir = Path.GetFullPath(args[++i]);
                }
                else
                {
                    error = "Thiếu đường dẫn thư mục sau tham số --out";
                    return false;
                }
            }
            else
            {
                error = $"Không nhận diện được tham số: {arg}";
                return false;
            }
        }

        if (options.IsRenderSpike)
        {
            if (options.IsInventoryDryRun)
            {
                error = "Không thể kết hợp lệnh 'render-spike' và 'inventory --dry-run'";
                return false;
            }

            if (!options.RenderSpikeBillId.HasValue)
            {
                error = "Lệnh 'render-spike' bắt buộc phải kèm tham số '--bill-id <id>'";
                return false;
            }

            if (string.IsNullOrWhiteSpace(options.RenderSpikeOutDir))
            {
                error = "Lệnh 'render-spike' bắt buộc phải kèm tham số '--out <dir>'";
                return false;
            }
        }
        else if (options.RenderSpikeBillId.HasValue || !string.IsNullOrWhiteSpace(options.RenderSpikeOutDir))
        {
            error = "Tham số '--bill-id' và '--out' chỉ được sử dụng cho lệnh 'render-spike'";
            return false;
        }

        return true;
    }

    private static async Task<int> ExecuteInventoryDryRunAsync(string dataDir)
    {
        string sourceDbPath = Path.Combine(dataDir, "lalab_autoreport.db");
        if (!File.Exists(sourceDbPath))
        {
            Console.Error.WriteLine($"[LỖI INVENTORY] Không tìm thấy tập tin cơ sở dữ liệu tại '{sourceDbPath}'.");
            return 7;
        }

        // Snapshot directory entries and source DB SHA-256 before dry-run
        var dirInfo = new DirectoryInfo(dataDir);
        var filesBefore = dirInfo.GetFiles().ToDictionary(f => f.Name, f => (f.Length, f.LastWriteTimeUtc));

        string hashBefore;
        using (var sha = SHA256.Create())
        using (var stream = new FileStream(sourceDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            hashBefore = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        // Create two isolated temp directories:
        // tempDir1: receives raw copy of source DB + WAL + SHM (so uncheckpointed WAL commits are captured)
        // tempDir2: receives clean, compacted single-file snapshot via SQLite Online Backup API (ADR D7)
        string tempDir1 = Path.Combine(Path.GetTempPath(), "lalab_inv_wal_" + Guid.NewGuid().ToString("N"));
        string tempDir2 = Path.Combine(Path.GetTempPath(), "lalab_inv_snap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir1);
        Directory.CreateDirectory(tempDir2);
        string tempDb1 = Path.Combine(tempDir1, "lalab_autoreport.db");
        string tempDb2 = Path.Combine(tempDir2, "lalab_autoreport.db");

        try
        {
            // 1. Copy source DB and any WAL/SHM sidecars to tempDir1 with FileShare.ReadWrite
            SafeCopyFile(sourceDbPath, tempDb1);

            string sourceWal = sourceDbPath + "-wal";
            if (File.Exists(sourceWal))
            {
                SafeCopyFile(sourceWal, tempDb1 + "-wal");
            }

            string sourceShm = sourceDbPath + "-shm";
            if (File.Exists(sourceShm))
            {
                try { SafeCopyFile(sourceShm, tempDb1 + "-shm"); } catch { }
            }

            // 2. Open tempDb1 normally so SQLite recovers any uncheckpointed WAL commits,
            // then use SQLite Online Backup API to produce clean, compacted snapshot in tempDb2 (ADR D7)
            using (var recoveredConn = new SqliteConnection($"Data Source={tempDb1};"))
            {
                await recoveredConn.OpenAsync();
                using var destConn = new SqliteConnection($"Data Source={tempDb2};");
                await destConn.OpenAsync();
                recoveredConn.BackupDatabase(destConn);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // 3. Query the compacted recovered snapshot
            var inventoryService = new ServerInventoryService(tempDb2);
            var report = await inventoryService.GenerateReportAsync();

            string jsonOutput = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            Console.WriteLine(jsonOutput);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LỖI INVENTORY] Lỗi truy vấn hoặc lược đồ cơ sở dữ liệu không hợp lệ: {ex.Message}");
            return 8;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir1)) Directory.Delete(tempDir1, recursive: true); } catch { }
            try { if (Directory.Exists(tempDir2)) Directory.Delete(tempDir2, recursive: true); } catch { }
        }

        // Verify source DB file hash is unchanged
        string hashAfter;
        using (var sha = SHA256.Create())
        using (var stream = new FileStream(sourceDbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            hashAfter = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }

        if (!string.Equals(hashBefore, hashAfter, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("[LỖI INVENTORY] SHA-256 của tập tin cơ sở dữ liệu nguồn bị thay đổi sau khi chạy dry-run!");
            return 1;
        }

        // Verify source directory files are unchanged
        var filesAfter = dirInfo.GetFiles().ToDictionary(f => f.Name, f => (f.Length, f.LastWriteTimeUtc));
        if (filesBefore.Count != filesAfter.Count || filesBefore.Any(kv => !filesAfter.ContainsKey(kv.Key) || filesAfter[kv.Key] != kv.Value))
        {
            var diff = string.Join(", ", filesAfter.Keys.Except(filesBefore.Keys).Concat(filesBefore.Keys.Except(filesAfter.Keys)));
            var mtimeDiff = string.Join(", ", filesBefore.Where(kv => filesAfter.ContainsKey(kv.Key) && filesAfter[kv.Key] != kv.Value).Select(kv => $"{kv.Key} (before={kv.Value.LastWriteTimeUtc}, after={filesAfter[kv.Key].LastWriteTimeUtc})"));
            Console.Error.WriteLine($"[LỖI INVENTORY] Danh sách hoặc mtime tập tin trong thư mục nguồn bị thay đổi sau khi chạy dry-run! Files: {diff}; Mtimes: {mtimeDiff}");
            return 1;
        }

        return 0;
    }

    private static void SafeCopyFile(string source, string dest)
    {
        using var inStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var outStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        inStream.CopyTo(outStream);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"
Lalab Auto Report Server
Cách sử dụng:
  LalabAutoReport.Server [options]
  LalabAutoReport.Server inventory --dry-run [options]
  LalabAutoReport.Server render-spike --bill-id <id> --out <dir> [options]

Tùy chọn:
  --data-dir <path>     Đường dẫn thư mục dữ liệu server (Mặc định: %ProgramData%\LalabAutoReport\Server)
  --port <port>         Cổng HTTP cho Mobile Web Server (0 = lấy từ Cài đặt, mặc định: 0)
  --health-port <port>  Cổng HTTP loopback cho health check (Mặc định: 5099)
  --lan-writes <mode>   Quyền ghi LAN: none hoặc orders (Mặc định: orders)
  --bill-id <id>        ID hóa đơn cần xuất (dùng cho render-spike)
  --out <dir>           Thư mục đích lưu ảnh JPEG và nhãn in (dùng cho render-spike)

Mã thoát (Exit Codes):
  0: Thành công (Normal exit, Inventory dry-run, Render spike)
  1: Lỗi không xử lý hoặc vi phạm toàn vẹn
  2: Tham số dòng lệnh không hợp lệ
  3: Không thể chiếm ServerInstanceGuard (tiến trình khác đang chạy)
  4: Khởi tạo dữ liệu thất bại (Fail-closed lifecycle / backup failure)
  6: Lỗi render-spike (không tìm thấy bill hoặc render lỗi)
  7: Không tìm thấy cơ sở dữ liệu cho subcommand inventory --dry-run
  8: Lỗi truy vấn cơ sở dữ liệu hoặc lược đồ không hợp lệ (inventory --dry-run)
");
    }

    private class ServerCommandLineOptions
    {
        public string DataDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LalabAutoReport",
            "Server");

        public int Port { get; set; } = 0;

        public int HealthPort { get; set; } = 5099;

        public string LanWrites { get; set; } = "orders";

        public bool IsInventoryDryRun { get; set; }

        public bool IsRenderSpike { get; set; }

        public long? RenderSpikeBillId { get; set; }

        public string? RenderSpikeOutDir { get; set; }
    }
}
