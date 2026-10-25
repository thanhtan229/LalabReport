using System;
using System.Collections.Generic;

namespace LalabAutoReport.Core.Domain;

public class Customer
{
    public long Id { get; set; }
    public string CanonicalName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public PriceTier PriceTier { get; set; } = PriceTier.Retail;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<CustomerAlias> Aliases { get; set; } = new();
}

public class CustomerAlias
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string AliasText { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class PrintSpecification
{
    public long Id { get; set; }
    public long? FamilyId { get; set; }
    public string? FamilyName { get; set; }
    public string? CanonicalSize { get; set; }
    public string CanonicalName { get; set; } = string.Empty;
    public ProductCategory Category { get; set; } = ProductCategory.PhotoPrint;
    public BillingMethod BillingMethod { get; set; } = BillingMethod.FileCount;
    public long UnitPrice { get; set; } // VND as integer (used when BillingMethod is FileCount)
    public long? UnitPriceStudio { get; set; }
    public long? UnitPriceVip { get; set; }
    public int? IncludedSheets { get; set; } // For AlbumBasePlusExtra (e.g. 10)
    public long? BasePrice { get; set; } // VND integer for AlbumBasePlusExtra (e.g. 400,000)
    public long? BasePriceStudio { get; set; }
    public long? BasePriceVip { get; set; }
    public long? ExtraSheetPrice { get; set; } // VND integer for AlbumBasePlusExtra (e.g. 20,000)
    public long? ExtraSheetPriceStudio { get; set; }
    public long? ExtraSheetPriceVip { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<PrintSpecificationAlias> Aliases { get; set; } = new();

    public long GetEffectiveUnitPrice(PriceTier tier) => tier switch
    {
        PriceTier.Studio => (UnitPriceStudio.HasValue && UnitPriceStudio.Value > 0) ? UnitPriceStudio.Value : UnitPrice,
        PriceTier.Vip => (UnitPriceVip.HasValue && UnitPriceVip.Value > 0) ? UnitPriceVip.Value : ((UnitPriceStudio.HasValue && UnitPriceStudio.Value > 0) ? UnitPriceStudio.Value : UnitPrice),
        _ => UnitPrice
    };

    public (long BasePrice, long ExtraSheetPrice) GetEffectiveAlbumPrices(PriceTier tier)
    {
        long baseP = tier switch
        {
            PriceTier.Studio => (BasePriceStudio.HasValue && BasePriceStudio.Value > 0) ? BasePriceStudio.Value : (BasePrice ?? 0),
            PriceTier.Vip => (BasePriceVip.HasValue && BasePriceVip.Value > 0) ? BasePriceVip.Value : ((BasePriceStudio.HasValue && BasePriceStudio.Value > 0) ? BasePriceStudio.Value : (BasePrice ?? 0)),
            _ => BasePrice ?? 0
        };

        long extraP = tier switch
        {
            PriceTier.Studio => (ExtraSheetPriceStudio.HasValue && ExtraSheetPriceStudio.Value > 0) ? ExtraSheetPriceStudio.Value : (ExtraSheetPrice ?? 0),
            PriceTier.Vip => (ExtraSheetPriceVip.HasValue && ExtraSheetPriceVip.Value > 0) ? ExtraSheetPriceVip.Value : ((ExtraSheetPriceStudio.HasValue && ExtraSheetPriceStudio.Value > 0) ? ExtraSheetPriceStudio.Value : (ExtraSheetPrice ?? 0)),
            _ => ExtraSheetPrice ?? 0
        };

        return (baseP, extraP);
    }

    public ProductVariant ToProductVariant() => new()
    {
        Id = Id,
        FamilyId = FamilyId ?? 0,
        CanonicalSize = CanonicalSize ?? CanonicalName,
        CanonicalName = CanonicalName,
        UnitPrice = UnitPrice,
        UnitPriceStudio = UnitPriceStudio,
        UnitPriceVip = UnitPriceVip,
        IncludedSheets = IncludedSheets,
        BasePrice = BasePrice,
        BasePriceStudio = BasePriceStudio,
        BasePriceVip = BasePriceVip,
        ExtraSheetPrice = ExtraSheetPrice,
        ExtraSheetPriceStudio = ExtraSheetPriceStudio,
        ExtraSheetPriceVip = ExtraSheetPriceVip,
        IsActive = IsActive,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}

public class PrintSpecificationAlias
{
    public long Id { get; set; }
    public long PrintSpecificationId { get; set; }
    public string AliasText { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
}

public class ProductFamily
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProductCategory Category { get; set; } = ProductCategory.PhotoPrint;
    public BillingMethod BillingMethod { get; set; } = BillingMethod.FileCount;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ProductFamilyAlias> Aliases { get; set; } = new();
    public List<ProductVariant> Variants { get; set; } = new();
}

public class ProductFamilyAlias
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public string AliasText { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ProductVariant
{
    public long Id { get; set; }
    public long FamilyId { get; set; }
    public string CanonicalSize { get; set; } = string.Empty; // e.g. "20x30"
    public string CanonicalName { get; set; } = string.Empty; // e.g. "Album 20x30"
    public long UnitPrice { get; set; }
    public long? UnitPriceStudio { get; set; }
    public long? UnitPriceVip { get; set; }
    public int? IncludedSheets { get; set; }
    public long? BasePrice { get; set; }
    public long? BasePriceStudio { get; set; }
    public long? BasePriceVip { get; set; }
    public long? ExtraSheetPrice { get; set; }
    public long? ExtraSheetPriceStudio { get; set; }
    public long? ExtraSheetPriceVip { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ProductFamily? Family { get; set; }
    public List<ProductSpecificAlias> SpecificAliases { get; set; } = new();

    public long GetEffectiveUnitPrice(PriceTier tier) => tier switch
    {
        PriceTier.Studio => (UnitPriceStudio.HasValue && UnitPriceStudio.Value > 0) ? UnitPriceStudio.Value : UnitPrice,
        PriceTier.Vip => (UnitPriceVip.HasValue && UnitPriceVip.Value > 0) ? UnitPriceVip.Value : ((UnitPriceStudio.HasValue && UnitPriceStudio.Value > 0) ? UnitPriceStudio.Value : UnitPrice),
        _ => UnitPrice
    };

    public (long BasePrice, long ExtraSheetPrice) GetEffectiveAlbumPrices(PriceTier tier)
    {
        long baseP = tier switch
        {
            PriceTier.Studio => (BasePriceStudio.HasValue && BasePriceStudio.Value > 0) ? BasePriceStudio.Value : (BasePrice ?? 0),
            PriceTier.Vip => (BasePriceVip.HasValue && BasePriceVip.Value > 0) ? BasePriceVip.Value : ((BasePriceStudio.HasValue && BasePriceStudio.Value > 0) ? BasePriceStudio.Value : (BasePrice ?? 0)),
            _ => BasePrice ?? 0
        };

        long extraP = tier switch
        {
            PriceTier.Studio => (ExtraSheetPriceStudio.HasValue && ExtraSheetPriceStudio.Value > 0) ? ExtraSheetPriceStudio.Value : (ExtraSheetPrice ?? 0),
            PriceTier.Vip => (ExtraSheetPriceVip.HasValue && ExtraSheetPriceVip.Value > 0) ? ExtraSheetPriceVip.Value : ((ExtraSheetPriceStudio.HasValue && ExtraSheetPriceStudio.Value > 0) ? ExtraSheetPriceStudio.Value : (ExtraSheetPrice ?? 0)),
            _ => ExtraSheetPrice ?? 0
        };

        return (baseP, extraP);
    }

    public PrintSpecification ToPrintSpecification() => new()
    {
        Id = Id,
        FamilyId = FamilyId,
        FamilyName = Family?.Name,
        CanonicalSize = CanonicalSize,
        CanonicalName = !string.IsNullOrWhiteSpace(CanonicalName) ? CanonicalName : (Family != null ? $"{Family.Name} {CanonicalSize}" : CanonicalSize),
        Category = Family?.Category ?? ProductCategory.PhotoPrint,
        BillingMethod = Family?.BillingMethod ?? BillingMethod.FileCount,
        UnitPrice = UnitPrice,
        UnitPriceStudio = UnitPriceStudio,
        UnitPriceVip = UnitPriceVip,
        IncludedSheets = IncludedSheets,
        BasePrice = BasePrice,
        BasePriceStudio = BasePriceStudio,
        BasePriceVip = BasePriceVip,
        ExtraSheetPrice = ExtraSheetPrice,
        ExtraSheetPriceStudio = ExtraSheetPriceStudio,
        ExtraSheetPriceVip = ExtraSheetPriceVip,
        IsActive = IsActive,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}

public class ProductSpecificAlias
{
    public long Id { get; set; }
    public long VariantId { get; set; }
    public string AliasText { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Order
{
    public long Id { get; set; }
    public string OrderCode { get; set; } = string.Empty; // e.g. "DH-260929-001"
    public string WorkDate { get; set; } = string.Empty; // "yyyy-MM-dd"
    public long? CustomerId { get; set; }
    public OrderKind OrderKind { get; set; } = OrderKind.Implicit;
    public string? OrderName { get; set; } // e.g. "Don 01" or "Đơn mặc định"
    public string OriginalFolderName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty; // e.g. "2026-09-28\Văn An" or "2026-09-28\Anh An\Don 01"
    public long? RootFolderId { get; set; }
    public string? RootFolderName { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Unscanned;
    public bool IsPrinted { get; set; } = false;
    public DateTimeOffset? PrintedAt { get; set; }
    public PrintStatus PrintProgress { get; set; } = PrintStatus.NotPrinted;
    public int PrintedItemCount => Items.Count(i => i.IsPrinted);
    public int TotalItemCount => Items.Count;
    public bool HasPendingCloudChanges { get; set; }
    public bool IsDelivered { get; set; } = false;
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? DeliveredBy { get; set; }
    public string? Note { get; set; }
    public bool FilesystemChangedAfterLock { get; set; } = false;
    public string? Fingerprint { get; set; }
    public DateTimeOffset? LastScanAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ThumbnailCandidateRelativePath { get; set; }

    // Populated during scan or query
    public Customer? Customer { get; set; }
    public List<OrderItemScan> Items { get; set; } = new();

    public bool IsGuest => CustomerId == null;

    public static bool IsGuestFolderName(string? folderName, string? orderName = null, IEnumerable<string>? guestAliases = null)
    {
        if (!string.IsNullOrWhiteSpace(orderName) &&
            (string.Equals(orderName, "Khách lẻ", StringComparison.OrdinalIgnoreCase) ||
             orderName.Contains("khach_le", StringComparison.OrdinalIgnoreCase) ||
             orderName.Contains("khách lẻ", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(folderName)) return false;

        if (folderName.Contains("khach_le", StringComparison.OrdinalIgnoreCase) ||
            folderName.Contains("khách lẻ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (guestAliases != null)
        {
            foreach (var alias in guestAliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                if (folderName.Contains(alias.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

public class ScanSnapshot
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public ScanScope Scope { get; set; }
    public ScanStatus Status { get; set; } = ScanStatus.Pending;
    public string? ErrorMessage { get; set; }
    public string AppVersion { get; set; } = "1.0.0";

    public List<OrderItemScan> Items { get; set; } = new();
}

public class OrderItemScan
{
    public long Id { get; set; }
    public long ScanSnapshotId { get; set; }
    public long OrderId { get; set; }
    public long? PrintSpecificationId { get; set; }
    public string SpecificationFolderName { get; set; } = string.Empty;
    public string SpecificationRelativePath { get; set; } = string.Empty;
    public int SourceCount { get; set; }
    public int? PrintCount { get; set; }
    public int? PrintableFileCount { get; set; }
    public string? SelectedPrintFolderRelativePath { get; set; }
    public string? ThumbnailCandidateRelativePath { get; set; }
    public PrintFolderResolutionStatus PrintFolderStatus { get; set; } = PrintFolderResolutionStatus.NoPrintFolder;
    public BillingFolderResolutionMode FolderResolutionMode { get; set; } = BillingFolderResolutionMode.AutoResolved;
    public int? MismatchCount { get; set; }
    public bool IsPrinted { get; set; } = false;
    public DateTimeOffset? PrintedAt { get; set; }
    public ScanStatus ScanStatus { get; set; } = ScanStatus.Pending;
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Candidate print folders found if ambiguous (joined or parsed)
    /// </summary>
    public List<string> CandidatePrintFolderRelativePaths { get; set; } = new();

    /// <summary>
    /// User resolution if resolved
    /// </summary>
    public int? BillQuantity { get; set; }
    public QuantityResolutionMode? QuantityResolutionMode { get; set; }
    public string? QuantityResolutionNote { get; set; }
    public string? BillingMetadataJson { get; set; }

    // Navigation / resolved objects
    public PrintSpecification? PrintSpecification { get; set; }

    public long? ProductVariantId
    {
        get => PrintSpecificationId;
        set => PrintSpecificationId = value;
    }

    public string OriginFolderPath
    {
        get => SpecificationRelativePath;
        set => SpecificationRelativePath = value;
    }

    public string? FinalPrintFolderPath
    {
        get => SelectedPrintFolderRelativePath;
        set => SelectedPrintFolderRelativePath = value;
    }

    public string? EffectiveBillingFolderRelativePath
    {
        get => SelectedPrintFolderRelativePath;
        set => SelectedPrintFolderRelativePath = value;
    }

    public int? EffectiveBillingCount
    {
        get => PrintCount;
        set => PrintCount = value;
    }

    /// <summary>
    /// Foreign key to customer_bills when included in a locked customer bill
    /// </summary>
    public long? CustomerBillId { get; set; }
}

public class Bill
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long CustomerId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Billed;
    public long Subtotal { get; set; } // VND integer
    public DateTimeOffset? LockedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<BillLine> Lines { get; set; } = new();
}

public class BillLine
{
    public long Id { get; set; }
    public long BillId { get; set; }
    public long PrintSpecificationId { get; set; }
    public string? ProductNameSnapshot { get; set; }
    public BillingMethod? BillingMethodSnapshot { get; set; }
    public int SourceCount { get; set; }
    public int? PrintCount { get; set; }
    public int? SheetCount { get; set; }
    public int? IncludedSheetsSnapshot { get; set; }
    public int? ExtraSheetCount { get; set; }
    public long? BasePriceSnapshot { get; set; }
    public long? ExtraSheetPriceSnapshot { get; set; }
    public string? FinalPrintFolderPath { get; set; }
    public BillingFolderResolutionMode FolderResolutionModeSnapshot { get; set; } = BillingFolderResolutionMode.AutoResolved;
    public int BillQuantity { get; set; }
    public QuantityResolutionMode QuantityResolutionMode { get; set; }
    public string? QuantityResolutionNote { get; set; }
    public long UnitPrice { get; set; }
    public long LineTotal { get; set; }
    public long SourceScanSnapshotId { get; set; }

    public PrintSpecification? PrintSpecification { get; set; }

    public string? EffectiveBillingFolderPath
    {
        get => FinalPrintFolderPath;
        set => FinalPrintFolderPath = value;
    }
}


public class RootFolder
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; } = false;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AppSettings
{
    public string RootFolder { get; set; } = string.Empty;
    public string BillExportFolder { get; set; } = string.Empty;
    public List<string> SupportedExtensions { get; set; } = new()
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".webp", ".heic"
    };
    public bool AutoScanStartup { get; set; } = true;
    public bool StartupIncludeYesterday { get; set; } = true;
    public bool AutoRegisterContextMenu { get; set; } = true;
    public bool EnableIdleScan { get; set; } = true;
    public int IdleThresholdMinutes { get; set; } = 30;
    public int IdleScanWindowDays { get; set; } = 7;
    public string? SecondaryBackupFolder { get; set; }
    public bool EnableVietQrOnBill { get; set; } = true;
    public string BankBinOrCode { get; set; } = "970422"; // MB Bank default
    public string BankAccountNumber { get; set; } = string.Empty;
    public string BankAccountName { get; set; } = string.Empty;
    public string WorkshopName { get; set; } = "XƯỞNG IN ẢNH CHUYÊN NGHIỆP";
    public string WorkshopSlogan { get; set; } = "Dịch vụ in ấn ảnh & Album chuyên nghiệp";
    public string WorkshopPhone { get; set; } = string.Empty;
    public string WorkshopAddress { get; set; } = string.Empty;
    public string InvoiceFooterMessage { get; set; } = "Cảm ơn quý khách đã tin tưởng và ủng hộ dịch vụ!";
    public string? WorkshopLogoPath { get; set; }
    public QrDisplayMode QrMode { get; set; } = QrDisplayMode.VietQrAuto;
    public string? CustomQrImagePath { get; set; }
    public List<string> GuestAliases { get; set; } = new()
    {
        "khach_le", "khách lẻ", "khach le", "le"
    };

    // Mobile Web Server & Remote Access Settings
    public bool EnableMobileServer { get; set; } = true;
    public int MobileServerPort { get; set; } = 5050;
    public string AdminPin { get; set; } = string.Empty;
    public string StaffPin { get; set; } = string.Empty;
    public bool ShowOrderThumbnails { get; set; } = true;
    public string MobileAuthSecret { get; set; } = string.Empty;

    // Thermal Label Printer Settings (40x100mm default, 75x100mm optional)
    public string? ThermalPrinterName { get; set; }
    public string ThermalLabelSize { get; set; } = ThermalLabelSizes.Size40x100;
    public bool AutoMarkDeliveredOnPrint { get; set; } = true;

    // System Tray & Background Daemon Settings
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool AutoStartWithWindows { get; set; } = true;

    // Cloud Read Replica & Sync Settings (24/7 Mobile View)
    public bool EnableCloudSync { get; set; } = true;
    public string CloudSyncApiUrl { get; set; } = "https://lalab.tinix.io.vn";
    public string CloudSyncSecret { get; set; } = "";
    public string? LastCloudSyncAt { get; set; }
    public string? LastCloudPullAt { get; set; }

    // Thumbnail & Gallery Settings
    public bool EnableThumbnailAutoCleanup { get; set; } = true;
    public int ThumbnailRetentionDays { get; set; } = 60; // 30, 60, or 90
    public string? LastThumbnailCleanupAt { get; set; }
}

/// <summary>
/// Domain model for a comprehensive bill aggregated by canonical customer
/// </summary>
public class CustomerBill
{
    public long Id { get; set; }
    public string BillNumber { get; set; } = string.Empty; // e.g. "BILL-20260929-0001"
    public BillType BillType { get; set; } = BillType.Customer;
    public long? CustomerId { get; set; }
    public string CustomerNameSnapshot { get; set; } = string.Empty;
    public string? PhoneSnapshot { get; set; }
    public string? ShippingAddressSnapshot { get; set; }
    public PriceTier PriceTierSnapshot { get; set; } = PriceTier.Retail;
    public string PeriodStart { get; set; } = string.Empty; // "yyyy-MM-dd"
    public string PeriodEnd { get; set; } = string.Empty;   // "yyyy-MM-dd"
    public CustomerBillStatus Status { get; set; } = CustomerBillStatus.Draft;
    public long ProductSubtotal { get; set; } // VND integer
    public long AdjustmentsTotal { get; set; } // VND integer (Add - Deduct)
    public long GrandTotal { get; set; } // VND integer (ProductSubtotal + AdjustmentsTotal, >= 0)
    public string? Note { get; set; }
    public string? ExportFilePath { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    public bool HasPendingCloudChanges { get; set; }
    public bool IsPaid { get; set; } = false;
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<CustomerBillOrder> Orders { get; set; } = new();
    public List<CustomerBillLine> Lines { get; set; } = new();
    public List<BillAdjustment> Adjustments { get; set; } = new();
    public List<GuestBillSourceFolder> SourceFolders { get; set; } = new();

    public int OrderCount => Orders?.Count ?? 0;
    public int LineCount => Lines?.Count ?? 0;
}

/// <summary>
/// Source folder linkage for a guest bill (Quick Bill)
/// </summary>
public class GuestBillSourceFolder
{
    public long Id { get; set; }
    public long BillId { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public string NormalizedFolderPath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Snapshot grouping for an order within a customer bill
/// </summary>
public class CustomerBillOrder
{
    public long Id { get; set; }
    public long BillId { get; set; }
    public long OrderId { get; set; }
    public string? OrderCodeSnapshot { get; set; }
    public string? SourceFolderPath { get; set; }
    public string OrderNameSnapshot { get; set; } = string.Empty;
    public string OrderDateSnapshot { get; set; } = string.Empty;
    public string OriginalFolderNameSnapshot { get; set; } = string.Empty;
    public long Subtotal { get; set; }
    public int SortOrder { get; set; }
    public bool IsIncluded { get; set; } = true;
    public bool IsFromPreviousPeriod { get; set; } = false;

    public List<CustomerBillLine> Lines { get; set; } = new();
}

/// <summary>
/// Line item within a customer bill
/// </summary>
public class CustomerBillLine
{
    public long Id { get; set; }
    public long BillId { get; set; }
    public long OrderId { get; set; }
    public long ProductJobId { get; set; } // references order_item_scans.id
    public long? ProductSpecificationId { get; set; }
    public string? SpecificationFolderName { get; set; }
    public string ProductNameSnapshot { get; set; } = string.Empty;
    public string? VariantSnapshot { get; set; }
    public BillingMethod BillingMethodSnapshot { get; set; } = BillingMethod.FileCount;
    public int ScannedQuantity { get; set; }
    public int BilledQuantity { get; set; }
    public string? QuantityOverrideReason { get; set; }
    public int? SheetCount { get; set; }
    public int? IncludedSheetsSnapshot { get; set; }
    public int? ExtraSheetCount { get; set; }
    public long? BasePriceSnapshot { get; set; }
    public long? ExtraSheetPriceSnapshot { get; set; }
    public long ConfiguredUnitPrice { get; set; }
    public long BilledUnitPrice { get; set; }
    public string? PriceOverrideReason { get; set; }
    public long LineTotal { get; set; }
    public bool IsIncluded { get; set; } = true;
    public int SortOrder { get; set; }
    public string? FinalPrintFolderPath { get; set; }
    public BillingFolderResolutionMode FolderResolutionModeSnapshot { get; set; } = BillingFolderResolutionMode.AutoResolved;
    public string? IssueMessage { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Adjustment line for a customer bill (Shipping, Surcharge, Discount, Custom)
/// </summary>
public class BillAdjustment
{
    public long Id { get; set; }
    public long BillId { get; set; }
    public AdjustmentType Type { get; set; } = AdjustmentType.Custom;
    public string Label { get; set; } = string.Empty;
    public AdjustmentDirection Direction { get; set; } = AdjustmentDirection.Add;
    public long Amount { get; set; } // VND integer >= 0
    public string? Note { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Domain alias for PrintSpecification in V2 Product Registry
/// </summary>
public class Product : PrintSpecification
{
}

/// <summary>
/// Domain alias for PrintSpecificationAlias in V2
/// </summary>
public class ProductAlias : PrintSpecificationAlias
{
}

/// <summary>
/// Domain alias for OrderItemScan in V2
/// </summary>
public class ProductJob : OrderItemScan
{
}

/// <summary>
/// Record storing the printed status of a specific folder in the photo workshop
/// </summary>
public class FolderPrintRecord
{
    public long Id { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public string NormalizedPath { get; set; } = string.Empty; // Normalized using PathNormalizer
    public PrintStatus Status { get; set; } = PrintStatus.Printed;
    public DateTimeOffset MarkedAt { get; set; } = DateTimeOffset.UtcNow;
    public string MarkedBy { get; set; } = "ExplorerContextMenu";
    public long? AssociatedOrderId { get; set; }
    public int? PrintedSubCount { get; set; }
    public int? TotalSubCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Result of toggling the printed status of a folder
/// </summary>
public class TogglePrintStatusResult
{
    public string FolderPath { get; set; } = string.Empty;
    public string NormalizedPath { get; set; } = string.Empty;
    public PrintStatus PreviousStatus { get; set; }
    public PrintStatus NewStatus { get; set; }
    public int? PrintedSubCount { get; set; }
    public int? TotalSubCount { get; set; }
    public bool IsSuccess { get; set; }
    public bool VisualIconUpdated { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsPrinted => NewStatus == PrintStatus.Printed;
    public bool IsPartial => NewStatus == PrintStatus.Partial;
}

public static class ThermalLabelSizes
{
    public const string Size40x100 = "40x100mm";
    public const string Size75x100 = "75x100mm";
}

