using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.Logging;
using LalabAutoReport.Server.Health;
using LalabAutoReport.Server.Hosting;
using LalabAutoReport.Server.Inventory;
using LalabAutoReport.Server.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LalabAutoReport.Server;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 1. Parse Command Line Arguments
        if (!TryParseArguments(args, out var cliOptions, out var parseError))
        {
            Console.Error.WriteLine($"Lỗi tham số dòng lệnh: {parseError}");
            PrintUsage();
            return 2;
        }

        string dataDir = cliOptions.DataDir;
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
            // 4. Configure DI services via AddLalabCore
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddSerilog(dispose: true));
            services.AddLalabCore(options =>
            {
                options.DataDirectory = dataDir;
                options.ThumbnailsDirectory = Path.Combine(dataDir, "Thumbnails");
                options.EnableCloudSync = false;
                options.EnableMobileWebServer = false;
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

            // 6. Check for subcommand: inventory --dry-run
            if (cliOptions.IsInventoryDryRun)
            {
                Log.Information("Thực thi lệnh kiểm kê dry-run trên cơ sở dữ liệu: {DbPath}", connectionFactory.DatabasePath);
                var inventoryService = new ServerInventoryService(connectionFactory.DatabasePath);
                var report = await inventoryService.GenerateReportAsync();

                string jsonOutput = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                Console.WriteLine(jsonOutput);
                Log.Information("Hoàn tất kiểm kê inventory dry-run.");
                return 0;
            }

            // 7. Health Info Provider setup
            Func<int> schemaVersionProvider = () =>
            {
                try
                {
                    using var conn = connectionFactory.CreateConnection();
                    return conn.ExecuteScalar<int>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations;");
                }
                catch
                {
                    return 0;
                }
            };

            bool isReady = true; // DB migrated & lock held
            var healthProvider = new ServerHealthInfoProvider(
                dataDir: dataDir,
                startedAtUtc: DateTimeOffset.UtcNow,
                schemaVersionProvider: schemaVersionProvider,
                isReadyProvider: () => isReady && guard.IsAcquired);

            // 8. Start Loopback Health Endpoints with Minimal WebApplication
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
            app.MapGet("/health/ready", () =>
            {
                var health = healthProvider.GetReadyHealth();
                return healthProvider.IsReady
                    ? Results.Json(health)
                    : Results.Json(health, statusCode: 503);
            });

            Log.Information("Health endpoints đang lắng nghe tại http://127.0.0.1:{Port}", cliOptions.HealthPort);

            // Run app with injected singleton healthProvider
            app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            {
                Log.Information("Nhận tín hiệu dừng Server...");
            });

            // Map singleton for endpoint injection
            app.MapGet("/", () => Results.Text("Lalab Auto Report Server"));

            await app.RunAsync();
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
            else
            {
                error = $"Không nhận diện được tham số: {arg}";
                return false;
            }
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"
Lalab Auto Report Server
Cách sử dụng:
  LalabAutoReport.Server [options]
  LalabAutoReport.Server inventory --dry-run [options]

Tùy chọn:
  --data-dir <path>     Đường dẫn thư mục dữ liệu server (Mặc định: %ProgramData%\LalabAutoReport\Server)
  --health-port <port>  Cổng HTTP loopback cho health check (Mặc định: 5099)
");
    }

    private class ServerCommandLineOptions
    {
        public string DataDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LalabAutoReport",
            "Server");

        public int HealthPort { get; set; } = 5099;

        public bool IsInventoryDryRun { get; set; }
    }
}
