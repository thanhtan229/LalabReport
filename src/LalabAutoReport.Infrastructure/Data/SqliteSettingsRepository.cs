using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Core.Domain;
using LalabAutoReport.Core.Interfaces;

namespace LalabAutoReport.Infrastructure.Data;

public class SqliteSettingsRepository : ISettingsRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    internal ISqliteConnectionFactory ConnectionFactory => _connectionFactory;

    public SqliteSettingsRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        var rows = await connection.QueryAsync<(string Key, string Value)>(
            "SELECT key, value FROM app_settings"
        );

        var dict = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

        var settings = new AppSettings();

        if (dict.TryGetValue("RootFolder", out var root))
        {
            settings.RootFolder = root;
        }

        if (dict.TryGetValue("SupportedExtensions", out var exts) && !string.IsNullOrWhiteSpace(exts))
        {
            settings.SupportedExtensions = exts
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .ToList();
        }

        if (dict.TryGetValue("AutoScanStartup", out var autoScan) && bool.TryParse(autoScan, out var parsedAuto))
        {
            settings.AutoScanStartup = parsedAuto;
        }

        if (dict.TryGetValue("StartupIncludeYesterday", out var incYest) && bool.TryParse(incYest, out var parsedYest))
        {
            settings.StartupIncludeYesterday = parsedYest;
        }

        if (dict.TryGetValue("AutoRegisterContextMenu", out var autoMenu) && bool.TryParse(autoMenu, out var parsedMenu))
        {
            settings.AutoRegisterContextMenu = parsedMenu;
        }
        else
        {
            settings.AutoRegisterContextMenu = true; // Default always enabled
        }

        if (dict.TryGetValue("EnableIdleScan", out var idleScan) && bool.TryParse(idleScan, out var parsedIdle))
        {
            settings.EnableIdleScan = parsedIdle;
        }

        if (dict.TryGetValue("IdleThresholdMinutes", out var idleThresh) && int.TryParse(idleThresh, out var parsedThresh))
        {
            settings.IdleThresholdMinutes = parsedThresh;
        }

        if (dict.TryGetValue("IdleScanWindowDays", out var idleDays) && int.TryParse(idleDays, out var parsedDays))
        {
            settings.IdleScanWindowDays = parsedDays;
        }

        if (dict.TryGetValue("GuestAliases", out var gAliases) && !string.IsNullOrWhiteSpace(gAliases))
        {
            settings.GuestAliases = gAliases
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (dict.TryGetValue("SecondaryBackupFolder", out var secondaryBackup) && !string.IsNullOrWhiteSpace(secondaryBackup))
        {
            settings.SecondaryBackupFolder = secondaryBackup;
        }

        if (dict.TryGetValue("EnableVietQrOnBill", out var enableQr) && bool.TryParse(enableQr, out var parsedEnableQr))
        {
            settings.EnableVietQrOnBill = parsedEnableQr;
        }

        if (dict.TryGetValue("BankBinOrCode", out var bankBin) && !string.IsNullOrWhiteSpace(bankBin))
        {
            settings.BankBinOrCode = bankBin;
        }

        if (dict.TryGetValue("BankAccountNumber", out var bankAcc) && !string.IsNullOrWhiteSpace(bankAcc))
        {
            settings.BankAccountNumber = bankAcc;
        }

        if (dict.TryGetValue("BankAccountName", out var bankName) && !string.IsNullOrWhiteSpace(bankName))
        {
            settings.BankAccountName = bankName;
        }

        if (dict.TryGetValue("WorkshopName", out var wsName) && !string.IsNullOrWhiteSpace(wsName))
        {
            settings.WorkshopName = wsName;
        }

        if (dict.TryGetValue("WorkshopPhone", out var wsPhone) && !string.IsNullOrWhiteSpace(wsPhone))
        {
            settings.WorkshopPhone = wsPhone;
        }

        if (dict.TryGetValue("WorkshopAddress", out var wsAddr) && !string.IsNullOrWhiteSpace(wsAddr))
        {
            settings.WorkshopAddress = wsAddr;
        }

        if (dict.TryGetValue("WorkshopSlogan", out var wsSlogan) && !string.IsNullOrWhiteSpace(wsSlogan))
        {
            settings.WorkshopSlogan = wsSlogan;
        }

        if (dict.TryGetValue("InvoiceFooterMessage", out var footerMsg) && !string.IsNullOrWhiteSpace(footerMsg))
        {
            settings.InvoiceFooterMessage = footerMsg;
        }

        if (dict.TryGetValue("WorkshopLogoPath", out var logoPath))
        {
            settings.WorkshopLogoPath = string.IsNullOrWhiteSpace(logoPath) ? null : logoPath;
        }

        if (dict.TryGetValue("QrMode", out var qrModeStr) && Enum.TryParse<QrDisplayMode>(qrModeStr, true, out var parsedQrMode))
        {
            settings.QrMode = parsedQrMode;
            settings.EnableVietQrOnBill = (parsedQrMode == QrDisplayMode.VietQrAuto);
        }
        else if (dict.TryGetValue("EnableVietQrOnBill", out var legacyQr) && bool.TryParse(legacyQr, out var parsedLegacyQr))
        {
            settings.EnableVietQrOnBill = parsedLegacyQr;
            settings.QrMode = parsedLegacyQr ? QrDisplayMode.VietQrAuto : QrDisplayMode.None;
        }

        if (dict.TryGetValue("CustomQrImagePath", out var customQrPath))
        {
            settings.CustomQrImagePath = string.IsNullOrWhiteSpace(customQrPath) ? null : customQrPath;
        }

        if (dict.TryGetValue("BillExportFolder", out var exportFolder) && !string.IsNullOrWhiteSpace(exportFolder))
        {
            settings.BillExportFolder = exportFolder;
        }
        else if (!string.IsNullOrWhiteSpace(settings.RootFolder))
        {
            settings.BillExportFolder = Path.Combine(settings.RootFolder, "Bills");
        }
        else
        {
            settings.BillExportFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LalabReports", "Bills");
        }

        if (dict.TryGetValue("EnableMobileServer", out var enableMobile) && bool.TryParse(enableMobile, out var parsedEnableMobile))
        {
            settings.EnableMobileServer = parsedEnableMobile;
        }

        if (dict.TryGetValue("MobileServerPort", out var portStr) && int.TryParse(portStr, out var parsedPort) && parsedPort > 0 && parsedPort <= 65535)
        {
            settings.MobileServerPort = parsedPort;
        }

        if (dict.TryGetValue("AdminPin", out var adminPin))
        {
            settings.AdminPin = adminPin?.Trim() ?? string.Empty;
        }

        if (dict.TryGetValue("StaffPin", out var staffPin))
        {
            settings.StaffPin = staffPin?.Trim() ?? string.Empty;
        }

        if (dict.TryGetValue("MobileAuthSecret", out var authSecret) && !string.IsNullOrWhiteSpace(authSecret))
        {
            settings.MobileAuthSecret = authSecret;
        }

        if (dict.TryGetValue("ShowOrderThumbnails", out var showThumbs) && bool.TryParse(showThumbs, out var parsedThumbs))
        {
            settings.ShowOrderThumbnails = parsedThumbs;
        }

        if (dict.TryGetValue("ThermalPrinterName", out var thermalPrinter))
        {
            settings.ThermalPrinterName = string.IsNullOrWhiteSpace(thermalPrinter) ? null : thermalPrinter;
        }

        if (dict.TryGetValue("ThermalLabelSize", out var thermalSize) && !string.IsNullOrWhiteSpace(thermalSize))
        {
            settings.ThermalLabelSize = thermalSize;
        }
        else
        {
            settings.ThermalLabelSize = ThermalLabelSizes.Size40x100;
        }

        if (dict.TryGetValue("AutoMarkDeliveredOnPrint", out var autoDelivered) && bool.TryParse(autoDelivered, out var parsedAutoDelivered))
        {
            settings.AutoMarkDeliveredOnPrint = parsedAutoDelivered;
        }

        if (dict.TryGetValue("MinimizeToTrayOnClose", out var minTray) && bool.TryParse(minTray, out var parsedMinTray))
        {
            settings.MinimizeToTrayOnClose = parsedMinTray;
        }

        if (dict.TryGetValue("AutoStartWithWindows", out var autoStart) && bool.TryParse(autoStart, out var parsedAutoStart))
        {
            settings.AutoStartWithWindows = parsedAutoStart;
        }

        if (dict.TryGetValue("EnableCloudSync", out var enableSync) && bool.TryParse(enableSync, out var parsedSync))
        {
            settings.EnableCloudSync = parsedSync;
        }

        if (dict.TryGetValue("CloudSyncApiUrl", out var syncUrl))
        {
            settings.CloudSyncApiUrl = syncUrl;
        }

        if (dict.TryGetValue("CloudSyncSecret", out var syncSecret))
        {
            settings.CloudSyncSecret = syncSecret;
        }

        if (dict.TryGetValue("LastCloudSyncAt", out var lastSync))
        {
            settings.LastCloudSyncAt = string.IsNullOrWhiteSpace(lastSync) ? null : lastSync;
        }

        if (dict.TryGetValue("LastCloudPullAt", out var lastPull))
        {
            settings.LastCloudPullAt = string.IsNullOrWhiteSpace(lastPull) ? null : lastPull;
        }

        if (dict.TryGetValue("EnableThumbnailAutoCleanup", out var autoCleanup) && bool.TryParse(autoCleanup, out var parsedCleanup))
        {
            settings.EnableThumbnailAutoCleanup = parsedCleanup;
        }

        if (dict.TryGetValue("ThumbnailRetentionDays", out var retDays) && int.TryParse(retDays, out var parsedRetDays) && (parsedRetDays == 30 || parsedRetDays == 60 || parsedRetDays == 90))
        {
            settings.ThumbnailRetentionDays = parsedRetDays;
        }

        if (dict.TryGetValue("LastThumbnailCleanupAt", out var lastCleanup))
        {
            settings.LastThumbnailCleanupAt = string.IsNullOrWhiteSpace(lastCleanup) ? null : lastCleanup;
        }

        return settings;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        using var transaction = connection.BeginTransaction();

        string extString = string.Join(",", settings.SupportedExtensions);
        bool effectiveEnableVietQr = (settings.QrMode == QrDisplayMode.VietQrAuto);

        await connection.ExecuteAsync(@"
            INSERT INTO app_settings (key, value) VALUES ('RootFolder', @RootFolder)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('BillExportFolder', @BillExportFolder)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('SupportedExtensions', @SupportedExtensions)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('AutoScanStartup', @AutoScanStartup)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('StartupIncludeYesterday', @StartupIncludeYesterday)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('AutoRegisterContextMenu', @AutoRegisterContextMenu)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('EnableIdleScan', @EnableIdleScan)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('IdleThresholdMinutes', @IdleThresholdMinutes)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('IdleScanWindowDays', @IdleScanWindowDays)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('GuestAliases', @GuestAliases)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('SecondaryBackupFolder', @SecondaryBackupFolder)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('EnableVietQrOnBill', @EnableVietQrOnBill)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('BankBinOrCode', @BankBinOrCode)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('BankAccountNumber', @BankAccountNumber)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('BankAccountName', @BankAccountName)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('WorkshopName', @WorkshopName)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('WorkshopPhone', @WorkshopPhone)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('WorkshopAddress', @WorkshopAddress)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('WorkshopSlogan', @WorkshopSlogan)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('InvoiceFooterMessage', @InvoiceFooterMessage)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('WorkshopLogoPath', @WorkshopLogoPath)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('QrMode', @QrMode)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('CustomQrImagePath', @CustomQrImagePath)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('EnableMobileServer', @EnableMobileServer)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('MobileServerPort', @MobileServerPort)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('AdminPin', @AdminPin)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('StaffPin', @StaffPin)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('MobileAuthSecret', @MobileAuthSecret)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('ShowOrderThumbnails', @ShowOrderThumbnails)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('ThermalPrinterName', @ThermalPrinterName)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('ThermalLabelSize', @ThermalLabelSize)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('AutoMarkDeliveredOnPrint', @AutoMarkDeliveredOnPrint)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('MinimizeToTrayOnClose', @MinimizeToTrayOnClose)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('AutoStartWithWindows', @AutoStartWithWindows)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('EnableCloudSync', @EnableCloudSync)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('CloudSyncApiUrl', @CloudSyncApiUrl)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('CloudSyncSecret', @CloudSyncSecret)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('EnableThumbnailAutoCleanup', @EnableThumbnailAutoCleanup)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('ThumbnailRetentionDays', @ThumbnailRetentionDays)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('LastThumbnailCleanupAt', @LastThumbnailCleanupAt)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('LastCloudSyncAt', @LastCloudSyncAt)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;

            INSERT INTO app_settings (key, value) VALUES ('LastCloudPullAt', @LastCloudPullAt)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
        ", new
        {
            RootFolder = settings.RootFolder,
            BillExportFolder = settings.BillExportFolder,
            SupportedExtensions = extString,
            AutoScanStartup = settings.AutoScanStartup.ToString(),
            StartupIncludeYesterday = settings.StartupIncludeYesterday.ToString(),
            AutoRegisterContextMenu = settings.AutoRegisterContextMenu.ToString(),
            EnableIdleScan = settings.EnableIdleScan.ToString(),
            IdleThresholdMinutes = settings.IdleThresholdMinutes.ToString(),
            IdleScanWindowDays = settings.IdleScanWindowDays.ToString(),
            GuestAliases = string.Join(";", settings.GuestAliases),
            SecondaryBackupFolder = settings.SecondaryBackupFolder ?? string.Empty,
            EnableVietQrOnBill = effectiveEnableVietQr.ToString(),
            BankBinOrCode = settings.BankBinOrCode,
            BankAccountNumber = settings.BankAccountNumber,
            BankAccountName = settings.BankAccountName,
            WorkshopName = settings.WorkshopName,
            WorkshopPhone = settings.WorkshopPhone,
            WorkshopAddress = settings.WorkshopAddress,
            WorkshopSlogan = settings.WorkshopSlogan,
            InvoiceFooterMessage = settings.InvoiceFooterMessage,
            WorkshopLogoPath = settings.WorkshopLogoPath ?? string.Empty,
            QrMode = settings.QrMode.ToString(),
            CustomQrImagePath = settings.CustomQrImagePath ?? string.Empty,
            EnableMobileServer = settings.EnableMobileServer.ToString(),
            MobileServerPort = settings.MobileServerPort.ToString(),
            AdminPin = settings.AdminPin,
            StaffPin = settings.StaffPin,
            MobileAuthSecret = settings.MobileAuthSecret ?? string.Empty,
            ShowOrderThumbnails = settings.ShowOrderThumbnails.ToString(),
            ThermalPrinterName = settings.ThermalPrinterName ?? string.Empty,
            ThermalLabelSize = string.IsNullOrWhiteSpace(settings.ThermalLabelSize) ? ThermalLabelSizes.Size40x100 : settings.ThermalLabelSize,
            AutoMarkDeliveredOnPrint = settings.AutoMarkDeliveredOnPrint.ToString(),
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose.ToString(),
            AutoStartWithWindows = settings.AutoStartWithWindows.ToString(),
            EnableCloudSync = settings.EnableCloudSync.ToString(),
            CloudSyncApiUrl = settings.CloudSyncApiUrl,
            CloudSyncSecret = settings.CloudSyncSecret,
            EnableThumbnailAutoCleanup = settings.EnableThumbnailAutoCleanup.ToString(),
            ThumbnailRetentionDays = settings.ThumbnailRetentionDays.ToString(),
            LastThumbnailCleanupAt = settings.LastThumbnailCleanupAt ?? string.Empty,
            LastCloudSyncAt = settings.LastCloudSyncAt ?? string.Empty,
            LastCloudPullAt = settings.LastCloudPullAt ?? string.Empty
        }, transaction: transaction);

        transaction.Commit();
    }
}
