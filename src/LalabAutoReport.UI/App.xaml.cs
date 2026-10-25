using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.FileSystem;
using LalabAutoReport.Infrastructure.Logging;
using LalabAutoReport.Infrastructure.Windows;
using LalabAutoReport.Infrastructure.Services;
using LalabAutoReport.UI.Services;
using LalabAutoReport.UI.ViewModels;
using LalabAutoReport.Infrastructure.Composition;
using LalabAutoReport.Infrastructure.Hosting;
using LalabAutoReport.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LalabAutoReport.UI;

public partial class App : System.Windows.Application
{
    private IServiceProvider? _serviceProvider;
    public IServiceProvider? Services => _serviceProvider;
    private static Mutex? _singleInstanceMutex;
    private static string MutexName = "Local\\LalabAutoReport_SingleInstance_Mutex_v1";
    private static string PipeName = "LalabAutoReport_IpcPipe_v1";
    private string? _isolatedDataDirectory;
    private CancellationTokenSource? _pipeCts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Parse command-line arguments
        string? quickBillFolder = null;
        string? togglePrintedFolder = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--isolated-data-directory", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                _isolatedDataDirectory = Path.GetFullPath(e.Args[++i]);
                Directory.CreateDirectory(_isolatedDataDirectory);
                string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(_isolatedDataDirectory.ToUpperInvariant())))[..16];
                MutexName += "_" + identity;
                PipeName += "_" + identity;
                continue;
            }
            if (string.Equals(e.Args[i], "--quick-bill", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                quickBillFolder = e.Args[i + 1];
            }
            else if (string.Equals(e.Args[i], "--toggle-printed", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                togglePrintedFolder = e.Args[i + 1];
            }
        }

        // 2. Single-Instance Check
        bool isFirstInstance;
        try
        {
            _singleInstanceMutex = new Mutex(true, MutexName, out isFirstInstance);
        }
        catch (AbandonedMutexException)
        {
            isFirstInstance = true;
        }
        catch
        {
            isFirstInstance = false;
        }

        if (!isFirstInstance)
        {
            // Another instance is already running. Try sending request to existing instance.
            bool messageDelivered = false;
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1200);
                using var writer = new StreamWriter(client, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(quickBillFolder))
                {
                    writer.WriteLine($"QUICK_BILL|{quickBillFolder}");
                }
                else if (!string.IsNullOrWhiteSpace(togglePrintedFolder))
                {
                    writer.WriteLine($"TOGGLE_PRINTED|{togglePrintedFolder}");
                }
                else
                {
                    writer.WriteLine("ACTIVATE|");
                }
                writer.Flush();
                messageDelivered = true;
            }
            catch
            {
                messageDelivered = false;
            }

            if (messageDelivered)
            {
                // Active healthy instance responded -> exit duplicate
                Environment.Exit(0);
                return;
            }

            // If message was NOT delivered, the existing instance is dead or hung (Zombie)!
            // Auto-recovery: Terminate orphaned instances and take over as the primary instance.
            try
            {
                int currentPid = Environment.ProcessId;
                var orphans = Process.GetProcessesByName("LalabAutoReport.UI")
                    .Where(p => p.Id != currentPid);
                foreach (var orphan in orphans)
                {
                    // A different installed build/profile is not ours to terminate.
                    if (_isolatedDataDirectory != null) continue;
                    try { if (string.Equals(orphan.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) orphan.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch { }

            // Re-acquire mutex now that zombie is terminated
            try
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = new Mutex(true, MutexName, out isFirstInstance);
            }
            catch
            {
                isFirstInstance = false;
            }

            // Never initialize a second database/server instance without owning the mutex.
            if (!isFirstInstance)
            {
                Shutdown(1);
                return;
            }
        }

        // 2.1 Fast Headless Mode: If launched with ONLY --toggle-printed and app was not previously running
        if (!string.IsNullOrWhiteSpace(togglePrintedFolder) && string.IsNullOrWhiteSpace(quickBillFolder))
        {
            try
            {
                LoggingSetup.Initialize(_isolatedDataDirectory == null ? null : Path.Combine(_isolatedDataDirectory, "logs"));
                if (ServerLockDetector.IsServerLockHeld())
                {
                    const string lockHeldMessage = "[LỖI KHỞI ĐỘNG] Máy chủ Lalab Server đang hoạt động (server.lock đang được nắm giữ). Thao tác --toggle-printed ngoại tuyến bị từ chối để tránh xung đột dữ liệu.";
                    Log.Error(lockHeldMessage);
                    Console.Error.WriteLine(lockHeldMessage);
                    LoggingSetup.CloseAndFlush();
                    if (_singleInstanceMutex != null)
                    {
                        try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                        _singleInstanceMutex.Dispose();
                        _singleInstanceMutex = null;
                    }
                    Environment.Exit(5);
                    return;
                }

                var connFactory = new SqliteConnectionFactory(_isolatedDataDirectory == null ? null : Path.Combine(_isolatedDataDirectory, "lalab_autoreport.db"));
                var migrator = new DatabaseMigrator(connFactory);
                await migrator.MigrateAsync();

                var folderPrintRepo = new SqliteFolderPrintRepository(connFactory);
                var visualMarkerService = new FolderVisualMarkerService();
                var orderRepo = new SqliteOrderRepository(connFactory);
                var settingsRepo = new SqliteSettingsRepository(connFactory);
                var printStatusService = new PrintStatusService(folderPrintRepo, visualMarkerService, orderRepo, settingsRepo, rootFolderRepository: new SqliteRootFolderRepository(connFactory));

                var result = await printStatusService.ToggleStatusAsync(togglePrintedFolder);
                Log.Information("Headless TOGGLE_PRINTED processed for '{Folder}': New status {Status}, visual icon updated: {Visual}",
                    togglePrintedFolder, result.NewStatus, result.VisualIconUpdated);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed in headless TOGGLE_PRINTED for '{Folder}'", togglePrintedFolder);
            }
            finally
            {
                LoggingSetup.CloseAndFlush();
                if (_singleInstanceMutex != null)
                {
                    try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                }
                Environment.Exit(0);
            }
            return;
        }

        // 3. Initialize logging
        LoggingSetup.Initialize(_isolatedDataDirectory == null ? null : Path.Combine(_isolatedDataDirectory, "logs"));
        Log.Information("Lalab Auto Report starting up...");

        // 4. Start background IPC pipe server for subsequent instances
        StartIpcServer();

        // 5. Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Log.Error(args.Exception, "Unhandled Dispatcher exception");
            MessageBox.Show($"Đã xảy ra lỗi: {args.Exception.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Prevent WPF from shutting down before MainWindow is opened
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 6. Configure Dependency Injection
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 7. Run database migrations safely
        try
        {
            var migrator = _serviceProvider.GetRequiredService<IDatabaseMigrator>();
            await migrator.MigrateAsync();
            if (_isolatedDataDirectory != null)
            {
                var isolatedSettings = await _serviceProvider.GetRequiredService<ISettingsRepository>().GetSettingsAsync();
                isolatedSettings.AutoRegisterContextMenu = false;
                isolatedSettings.AutoStartWithWindows = false;
                isolatedSettings.AutoScanStartup = false;
                isolatedSettings.EnableIdleScan = false;
                isolatedSettings.EnableCloudSync = false;
                await _serviceProvider.GetRequiredService<ISettingsRepository>().SaveSettingsAsync(isolatedSettings);
            }
            Log.Information("Database migrations completed successfully.");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Database migration failed during startup.");
            MessageBox.Show($"Không thể khởi tạo cơ sở dữ liệu: {ex.Message}", "Lỗi Cơ Sở Dữ Liệu", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 7.1 Auto-register Windows Explorer context menu integration (Default always enabled)
        try
        {
            var settingsRepo = _serviceProvider.GetRequiredService<ISettingsRepository>();
            var settings = await settingsRepo.GetSettingsAsync();
            if (settings.AutoRegisterContextMenu)
            {
                var contextMenuService = _serviceProvider.GetRequiredService<IContextMenuIntegrationService>();
                contextMenuService.EnsureContextMenuRegistered();
                Log.Information("Windows Explorer right-click context menu integration auto-registered successfully.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to auto-register Windows Explorer context menu on startup.");
        }

        // 8. Show Main Window / Initialize System Tray
        try
        {
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;

            // 8.0 Initialize System Tray
            try
            {
                var trayManager = _serviceProvider.GetRequiredService<ISystemTrayManager>();
                trayManager.Initialize();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to initialize SystemTrayManager on startup.");
            }

            bool startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (!startMinimized)
            {
                mainWindow.Show();
                Log.Information("Main window displayed successfully.");
            }
            else
            {
                Log.Information("Application started minimized to system tray via --minimized argument.");
            }
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            // 8.1 Start AutoScanCoordinator and run startup scan if enabled
            try
            {
                var autoScanner = _serviceProvider.GetRequiredService<IAutoScanCoordinator>();
                autoScanner.Start();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await autoScanner.PerformStartupScanAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed during background startup auto-scan.");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to initialize AutoScanCoordinator.");
            }

            // 8.2 Start Mobile Web Server
            try
            {
                var mobileServer = _serviceProvider.GetRequiredService<IMobileWebServer>();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await mobileServer.StartAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to start Mobile Web Server on startup.");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to resolve IMobileWebServer on startup.");
            }

            // 8.3 Cleanup Expired Thumbnail Cache in background (Policy-based: 30/60/90 days, max once per 24h)
            try
            {
                var thumbnailService = _serviceProvider.GetRequiredService<IThumbnailService>();
                var settingsRepo = _serviceProvider.GetRequiredService<ISettingsRepository>();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var settings = await settingsRepo.GetSettingsAsync();
                        if (settings.EnableThumbnailAutoCleanup)
                        {
                            bool shouldSweep = true;
                            if (!string.IsNullOrWhiteSpace(settings.LastThumbnailCleanupAt) &&
                                DateTimeOffset.TryParse(settings.LastThumbnailCleanupAt, out var lastSweep))
                            {
                                if (DateTimeOffset.UtcNow - lastSweep < TimeSpan.FromHours(24))
                                {
                                    shouldSweep = false;
                                }
                            }

                            if (shouldSweep)
                            {
                                int days = (settings.ThumbnailRetentionDays == 30 || settings.ThumbnailRetentionDays == 90) ? settings.ThumbnailRetentionDays : 60;
                                await thumbnailService.CleanupExpiredDiskCacheAsync(TimeSpan.FromDays(days));
                                if (settings.EnableCloudSync)
                                {
                                    try
                                    {
                                        var cloudSync = _serviceProvider.GetService<ICloudSyncService>();
                                        if (cloudSync != null)
                                        {
                                            await cloudSync.CleanupCloudGalleryCacheAsync(days, clearAll: false);
                                        }
                                    }
                                    catch { }
                                }
                                settings.LastThumbnailCleanupAt = DateTimeOffset.UtcNow.ToString("o");
                                await settingsRepo.SaveSettingsAsync(settings);
                                Log.Information("Automatic thumbnail cleanup sweep completed (retention {Days} days).", days);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed during background thumbnail cache cleanup.");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to initialize thumbnail cleanup on startup.");
            }

            // 8.4 Initial Cloud Sync in background (non-blocking)
            try
            {
                var cloudSync = _serviceProvider.GetRequiredService<ICloudSyncService>();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3000); // Wait 3s after startup
                        await cloudSync.SyncAllAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed during background startup cloud sync.");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to resolve ICloudSyncService on startup.");
            }

            // 9. If started with --quick-bill, trigger quick bill review immediately
            if (!string.IsNullOrWhiteSpace(quickBillFolder))
            {
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        var launcher = _serviceProvider.GetRequiredService<QuickBillLauncher>();
                        await launcher.LaunchQuickBillForFolderAsync(quickBillFolder);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to launch initial quick bill for folder: {Folder}", quickBillFolder);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to display main window.");
            MessageBox.Show($"Lỗi hiển thị giao diện: {ex.Message}", "Lỗi Khởi Động", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartIpcServer()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? message = await reader.ReadLineAsync(token);

                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        await Dispatcher.InvokeAsync(() => HandleIpcMessage(message));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "IPC Pipe server loop error.");
                    try { await Task.Delay(300, token); } catch { break; }
                }
            }
        }, token);
    }

    private async void HandleIpcMessage(string message)
    {
        try
        {
            var parts = message.Split('|', 2);
            string cmd = parts[0];

            if (cmd == "TOGGLE_PRINTED" && parts.Length > 1)
            {
                string folder = parts[1];
                var printStatusService = _serviceProvider?.GetRequiredService<IPrintStatusService>();
                if (printStatusService != null)
                {
                    var result = await printStatusService.ToggleStatusAsync(folder);
                    Log.Information("IPC TOGGLE_PRINTED processed for '{Folder}': New status {Status}, visual icon updated: {Visual}",
                        folder, result.NewStatus, result.VisualIconUpdated);
                }
                return;
            }

            if (MainWindow != null)
            {
                if (MainWindow is MainWindow mw)
                {
                    mw.RestoreAndActivate();
                }
                else
                {
                    if (MainWindow.WindowState == WindowState.Minimized)
                    {
                        MainWindow.WindowState = WindowState.Normal;
                    }
                    MainWindow.Show();
                    MainWindow.Activate();
                    MainWindow.Topmost = true;
                    MainWindow.Topmost = false;
                    MainWindow.Focus();
                }
            }

            if (cmd == "QUICK_BILL" && parts.Length > 1)
            {
                string folder = parts[1];
                var launcher = _serviceProvider?.GetRequiredService<QuickBillLauncher>();
                if (launcher != null)
                {
                    await launcher.LaunchQuickBillForFolderAsync(folder);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Lỗi xử lý IPC message: {Message}", message);
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        // Core & Infrastructure Services (Shared Composition Root)
        services.AddLalabCore(options =>
        {
            if (_isolatedDataDirectory != null)
            {
                options.DataDirectory = _isolatedDataDirectory;
                options.ThumbnailsDirectory = Path.Combine(_isolatedDataDirectory, "Thumbnails");
            }
        });

        // Desktop / UI-only services
        services.AddSingleton<IContextMenuIntegrationService, WindowsContextMenuIntegrationService>();
        services.AddSingleton<QuickBillLauncher>();
        services.AddSingleton<IIdleDetectionService, WindowsIdleDetector>();
        services.AddSingleton<IAutoScanCoordinator, AutoScanCoordinator>();
        if (_isolatedDataDirectory == null) services.AddSingleton<IUpdateService, LalabAutoReport.Infrastructure.Services.GitHubUpdateService>();
        services.AddSingleton<ISystemTrayManager, SystemTrayManager>();

        // ViewModels
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<InvoicesViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<CustomersViewModel>();
        services.AddSingleton<PriceListViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<UpdateViewModel>();
        services.AddTransient<GlobalSearchViewModel>();
        services.AddTransient<ManageFamilyAliasesViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<Views.UpdateDialog>();
        services.AddTransient<Views.ManageFamilyAliasesDialog>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 1. Release SingleInstanceMutex immediately so user can reopen without waiting
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        // 2. Stop IPC Pipe
        try
        {
            _pipeCts?.Cancel();
            _pipeCts?.Dispose();
        }
        catch { }

        // 3. Graceful asynchronous teardown in background thread with 1.5s timeout
        try
        {
            var teardownTask = Task.Run(async () =>
            {
                try
                {
                    var autoScanner = _serviceProvider?.GetService<IAutoScanCoordinator>();
                    autoScanner?.Stop();
                }
                catch { }

                try
                {
                    var mobileServer = _serviceProvider?.GetService<IMobileWebServer>();
                    if (mobileServer != null)
                    {
                        await mobileServer.StopAsync();
                    }
                }
                catch { }


            });

            teardownTask.Wait(1500);
        }
        catch { }

        // 3.1 Dispose System Tray Icon
        try
        {
            var trayManager = _serviceProvider?.GetService<ISystemTrayManager>();
            trayManager?.Dispose();
        }
        catch { }

        Log.Information("Lalab Auto Report shutting down with exit code {Code}.", e.ApplicationExitCode);
        LoggingSetup.CloseAndFlush();
        base.OnExit(e);

        // 4. Force terminate process to prevent any stray background thread keeping it alive
        Environment.Exit(e.ApplicationExitCode);
    }
}
