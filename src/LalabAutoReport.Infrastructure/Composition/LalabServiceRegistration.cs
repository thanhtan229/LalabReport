using System;
using System.IO;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using LalabAutoReport.Infrastructure.Data;
using LalabAutoReport.Infrastructure.FileSystem;
using LalabAutoReport.Infrastructure.Reporting;
using LalabAutoReport.Infrastructure.Services;
using LalabAutoReport.Infrastructure.Web;
using LalabAutoReport.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LalabAutoReport.Infrastructure.Composition;

public class LalabServerCoreOptions
{
    public string? DataDirectory { get; set; }
    public string? ThumbnailsDirectory { get; set; }
    public bool EnableCloudSync { get; set; } = true;
    public bool EnableMobileWebServer { get; set; } = true;
    public bool MobileWebServerLoopbackOnly { get; set; } = false;
    public bool MobileWebServerReadOnlyMode { get; set; } = false;
    public int MobileWebServerPort { get; set; } = 0;
}

public static class LalabServiceRegistration
{
    public static IServiceCollection AddLalabCore(
        this IServiceCollection services,
        Action<LalabServerCoreOptions>? configure = null)
    {
        var options = new LalabServerCoreOptions();
        configure?.Invoke(options);

        // Core Filesystem
        services.AddSingleton<IFileSystemAdapter, PhysicalFileSystemAdapter>();

        // Database connection factory
        if (options.DataDirectory != null)
        {
            services.AddSingleton<ISqliteConnectionFactory>(
                new SqliteConnectionFactory(Path.Combine(options.DataDirectory, "lalab_autoreport.db")));
        }
        else
        {
            services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        }

        // Database lifecycle & maintenance
        services.AddSingleton<IDatabaseBackupService, DatabaseBackupService>();
        services.AddSingleton<IDatabaseMigrator, DatabaseMigrator>();
        services.AddSingleton<IDatabaseResetService, DatabaseResetService>();
        services.AddSingleton<IDataPurgeService, DataPurgeService>();

        // Repositories
        services.AddSingleton<ISettingsRepository, SqliteSettingsRepository>();
        services.AddSingleton<ICloudSyncStateRepository, SqliteCloudSyncStateRepository>();
        services.AddSingleton<IRootFolderRepository, SqliteRootFolderRepository>();
        services.AddSingleton<IOrderRepository, SqliteOrderRepository>();
        services.AddSingleton<ICustomerRepository, SqliteCustomerRepository>();
        services.AddSingleton<IFolderPrintRepository, SqliteFolderPrintRepository>();
        services.AddSingleton<IFolderVisualMarkerService, FolderVisualMarkerService>();
        services.AddSingleton<IPrintStatusService, PrintStatusService>();

        // Products & Specifications
        services.AddSingleton<SqliteProductRepository>();
        services.AddSingleton<IPrintSpecificationRepository>(sp => sp.GetRequiredService<SqliteProductRepository>());
        services.AddSingleton<IProductRepository>(sp => sp.GetRequiredService<SqliteProductRepository>());

        // Parsing, Scanning & Resolving
        services.AddSingleton<IFolderStructureParser, FolderStructureParser>();
        services.AddSingleton<IPrintFolderResolver, PrintFolderResolver>();
        services.AddSingleton<ICustomerResolver, CustomerResolver>();
        services.AddSingleton<PrintSpecificationResolver>();
        services.AddSingleton<IPrintSpecificationResolver>(sp => sp.GetRequiredService<PrintSpecificationResolver>());
        services.AddSingleton<IProductResolver>(sp => sp.GetRequiredService<PrintSpecificationResolver>());
        services.AddSingleton<IScanService, ScanService>();

        // Billing & Locking
        services.AddSingleton<IBillRepository, SqliteBillRepository>();
        services.AddSingleton<IBillingService, BillingService>();
        services.AddSingleton<ILockingService, LockingService>();
        services.AddSingleton<ICustomerBillRepository, SqliteCustomerBillRepository>();
        services.AddSingleton<ICustomerBillingService, CustomerBillingService>();

        // Reporting & Exporters
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<WpfJpegBillExporter>();
        services.AddSingleton<IJpegBillExporter>(sp => sp.GetRequiredService<WpfJpegBillExporter>());
        services.AddSingleton<IBillVisualRenderer>(sp => sp.GetRequiredService<WpfJpegBillExporter>());
        services.AddSingleton<IExcelBillExporter, ClosedXmlBillExporter>();
        services.AddSingleton<IShippingLabelExporter, WpfShippingLabelExporter>();

        // Fingerprint & Search
        services.AddSingleton<IFolderFingerprintService, FolderFingerprintService>();
        services.AddSingleton<IGlobalSearchService, SqliteGlobalSearchService>();

        // Auth & Mobile Web Server
        services.AddSingleton(new MobileWebServerOptions
        {
            LoopbackOnly = options.MobileWebServerLoopbackOnly,
            ReadOnlyMode = options.MobileWebServerReadOnlyMode,
            Port = options.MobileWebServerPort
        });
        services.AddSingleton<IMobileAuthService, MobileAuthService>();
        if (options.EnableMobileWebServer)
        {
            services.AddSingleton<IMobileWebServer, KestrelMobileWebServer>();
        }

        // Thumbnails & Cache
        services.AddSingleton<IThumbnailCacheRepository, SqliteThumbnailCacheRepository>();
        if (options.ThumbnailsDirectory != null)
        {
            services.AddSingleton<IThumbnailService>(sp => new ThumbnailService(
                sp.GetRequiredService<IFileSystemAdapter>(),
                options.ThumbnailsDirectory,
                sp.GetService<IThumbnailCacheRepository>(),
                sp.GetService<ILogger<ThumbnailService>>()));
        }
        else
        {
            services.AddSingleton<IThumbnailService, ThumbnailService>();
        }

        // Order Gallery
        services.AddSingleton<IOrderGalleryService, OrderGalleryService>();

        // Cloud sync
        if (options.EnableCloudSync)
        {
            services.AddSingleton<ICloudMediaSyncService, CloudMediaSyncService>();
            services.AddSingleton<ICloudSyncService, CloudSyncService>();
        }

        return services;
    }
}
