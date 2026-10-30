using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using LalabAutoReport.Core.Interfaces;
using LalabAutoReport.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using LalabAutoReport.ApplicationCore.Auth;

namespace LalabAutoReport.Infrastructure.Data;

public class DatabaseMigrator : IDatabaseMigrator
{
    public const int LatestVersion = 29;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseBackupService? _backupService;
    private readonly ILogger<DatabaseMigrator>? _logger;

    public DatabaseMigrator(
        ISqliteConnectionFactory connectionFactory,
        IDatabaseBackupService? backupService = null,
        ILogger<DatabaseMigrator>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _backupService = backupService;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await MigrateToVersionAsync(int.MaxValue, cancellationToken);
    }

    public async Task MigrateToVersionAsync(int targetVersion, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        // 1. Ensure migrations table exists
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );
        ");

        var appliedMigrations = (await connection.QueryAsync<int>("SELECT version FROM schema_migrations")).ToHashSet();

        // List of all migrations in order
        var migrations = GetMigrations();
        var pendingMigrations = migrations
            .Where(m => !appliedMigrations.Contains(m.Version) && m.Version <= targetVersion)
            .ToList();

        // An existing database needs a restore point before any schema change. If the backup fails,
        // refuse the upgrade instead of migrating without a way back. A brand-new database has no data
        // to protect, so it skips the backup.
        if (pendingMigrations.Count > 0 && appliedMigrations.Count > 0 && _backupService != null)
        {
            try
            {
                _logger?.LogInformation("Creating automatic database backup before running {Count} pending migration(s)...", pendingMigrations.Count);
                await _backupService.CreateBackupAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Pre-migration backup failed; {Count} migration(s) refused to protect existing data", pendingMigrations.Count);
                throw new InvalidOperationException(
                    "Không thể sao lưu cơ sở dữ liệu trước khi nâng cấp; nâng cấp đã bị hủy để bảo toàn dữ liệu.", ex);
            }
        }

        if (pendingMigrations.Count == 0)
        {
            return;
        }

        // Disable foreign keys outside of any transaction to allow table rebuild migrations
        await connection.ExecuteAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            foreach (var migration in pendingMigrations)
            {
                _logger?.LogInformation("Applying migration {Version}: {Name}", migration.Version, migration.Name);

                using var transaction = connection.BeginTransaction();
                try
                {
                    if (!string.IsNullOrWhiteSpace(migration.Sql))
                    {
                        await connection.ExecuteAsync(migration.Sql, transaction: transaction);
                    }

                    if (migration.PostAction != null)
                    {
                        await migration.PostAction(connection, transaction, _logger);
                    }

                    // Check foreign key consistency before committing this migration
                    var fkErrors = (await connection.QueryAsync<FkViolationDto>(
                        "PRAGMA foreign_key_check;", transaction: transaction)).ToList();
                    if (fkErrors.Count > 0)
                    {
                        var details = string.Join("; ", fkErrors.Select(e => $"Table '{e.table}', rowid {e.rowid} -> parent '{e.parent}'"));
                        throw new InvalidOperationException(
                            $"Foreign key check failed after migration {migration.Version} ({migration.Name}): {details}");
                    }

                    await connection.ExecuteAsync(
                        "INSERT INTO schema_migrations (version, name, applied_at) VALUES (@Version, @Name, @AppliedAt)",
                        new
                        {
                            Version = migration.Version,
                            Name = migration.Name,
                            AppliedAt = DateTimeOffset.UtcNow.ToString("o")
                        },
                        transaction: transaction
                    );
                    transaction.Commit();
                    _logger?.LogInformation("Successfully applied migration {Version}: {Name}", migration.Version, migration.Name);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger?.LogError(ex, "Failed to apply migration {Version}: {Name}", migration.Version, migration.Name);
                    throw;
                }
            }
        }
        finally
        {
            // Re-enable foreign keys outside transaction
            await connection.ExecuteAsync("PRAGMA foreign_keys = ON;");
        }
    }

    private class MigrationDefinition
    {
        public int Version { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Sql { get; set; } = string.Empty;
        public Func<SqliteConnection, SqliteTransaction, ILogger?, Task>? PostAction { get; set; }
    }

    private static IReadOnlyList<MigrationDefinition> GetMigrations()
    {
        return new List<MigrationDefinition>
        {
            new MigrationDefinition
            {
                Version = 1,
                Name = "InitialSchema",
                Sql = @"
                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS customers (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    canonical_name TEXT NOT NULL,
                    phone TEXT,
                    note TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS customer_aliases (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    customer_id INTEGER NOT NULL,
                    alias_text TEXT NOT NULL,
                    normalized_alias TEXT NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(customer_id) REFERENCES customers(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS print_specifications (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    canonical_name TEXT NOT NULL,
                    unit_price INTEGER NOT NULL DEFAULT 0,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS print_specification_aliases (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    print_specification_id INTEGER NOT NULL,
                    alias_text TEXT NOT NULL,
                    normalized_alias TEXT NOT NULL UNIQUE,
                    FOREIGN KEY(print_specification_id) REFERENCES print_specifications(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS orders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    work_date TEXT NOT NULL,
                    customer_id INTEGER,
                    original_folder_name TEXT NOT NULL,
                    relative_path TEXT NOT NULL UNIQUE,
                    status TEXT NOT NULL,
                    last_scan_at TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(customer_id) REFERENCES customers(id)
                );

                CREATE TABLE IF NOT EXISTS scan_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    order_id INTEGER NOT NULL,
                    started_at TEXT NOT NULL,
                    completed_at TEXT,
                    scan_scope TEXT NOT NULL,
                    status TEXT NOT NULL,
                    error_message TEXT,
                    app_version TEXT NOT NULL,
                    FOREIGN KEY(order_id) REFERENCES orders(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS order_item_scans (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    scan_snapshot_id INTEGER NOT NULL,
                    order_id INTEGER NOT NULL,
                    print_specification_id INTEGER,
                    specification_folder_name TEXT NOT NULL,
                    specification_relative_path TEXT NOT NULL,
                    source_count INTEGER NOT NULL,
                    print_count INTEGER,
                    selected_print_folder_relative_path TEXT,
                    print_folder_status TEXT NOT NULL,
                    mismatch_count INTEGER,
                    scan_status TEXT NOT NULL,
                    error_message TEXT,
                    candidate_print_folders TEXT,
                    bill_quantity INTEGER,
                    quantity_resolution_mode TEXT,
                    quantity_resolution_note TEXT,
                    FOREIGN KEY(scan_snapshot_id) REFERENCES scan_snapshots(id) ON DELETE CASCADE,
                    FOREIGN KEY(order_id) REFERENCES orders(id) ON DELETE CASCADE,
                    FOREIGN KEY(print_specification_id) REFERENCES print_specifications(id)
                );

                CREATE TABLE IF NOT EXISTS bills (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    order_id INTEGER NOT NULL,
                    customer_id INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    subtotal INTEGER NOT NULL DEFAULT 0,
                    locked_at TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(order_id) REFERENCES orders(id),
                    FOREIGN KEY(customer_id) REFERENCES customers(id)
                );

                CREATE TABLE IF NOT EXISTS bill_lines (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bill_id INTEGER NOT NULL,
                    print_specification_id INTEGER NOT NULL,
                    source_count INTEGER NOT NULL,
                    print_count INTEGER,
                    bill_quantity INTEGER NOT NULL,
                    quantity_resolution_mode TEXT NOT NULL,
                    quantity_resolution_note TEXT,
                    unit_price INTEGER NOT NULL,
                    line_total INTEGER NOT NULL,
                    source_scan_snapshot_id INTEGER NOT NULL,
                    FOREIGN KEY(bill_id) REFERENCES bills(id) ON DELETE CASCADE,
                    FOREIGN KEY(print_specification_id) REFERENCES print_specifications(id),
                    FOREIGN KEY(source_scan_snapshot_id) REFERENCES scan_snapshots(id)
                );

                CREATE INDEX IF NOT EXISTS idx_orders_work_date ON orders(work_date);
                CREATE INDEX IF NOT EXISTS idx_orders_customer_id ON orders(customer_id);
                CREATE INDEX IF NOT EXISTS idx_order_item_scans_order_id ON order_item_scans(order_id);
                CREATE INDEX IF NOT EXISTS idx_customer_aliases_normalized ON customer_aliases(normalized_alias);
                CREATE INDEX IF NOT EXISTS idx_spec_aliases_normalized ON print_specification_aliases(normalized_alias);

                -- Initial Default Specifications
                INSERT INTO print_specifications (canonical_name, unit_price, is_active, created_at, updated_at)
                VALUES ('13x18 in', 5000, 1, datetime('now'), datetime('now'));

                INSERT INTO print_specifications (canonical_name, unit_price, is_active, created_at, updated_at)
                VALUES ('20x30', 15000, 1, datetime('now'), datetime('now'));

                INSERT INTO print_specifications (canonical_name, unit_price, is_active, created_at, updated_at)
                VALUES ('40x60 TG', 80000, 1, datetime('now'), datetime('now'));
            "
            },
            new MigrationDefinition
            {
                Version = 2,
                Name = "AddPostLockWarningField",
                Sql = @"
                ALTER TABLE orders ADD COLUMN filesystem_changed_after_lock INTEGER NOT NULL DEFAULT 0;
            "
            },
            new MigrationDefinition
            {
                Version = 3,
                Name = "V2ProductAndOrderHierarchy",
                Sql = @"
                -- Add product category and album pricing fields to print_specifications
                ALTER TABLE print_specifications ADD COLUMN category TEXT NOT NULL DEFAULT 'PhotoPrint';
                ALTER TABLE print_specifications ADD COLUMN billing_method TEXT NOT NULL DEFAULT 'FileCount';
                ALTER TABLE print_specifications ADD COLUMN included_sheets INTEGER;
                ALTER TABLE print_specifications ADD COLUMN base_price INTEGER;
                ALTER TABLE print_specifications ADD COLUMN extra_sheet_price INTEGER;

                -- Add order hierarchy fields to orders
                ALTER TABLE orders ADD COLUMN order_kind TEXT NOT NULL DEFAULT 'Implicit';
                ALTER TABLE orders ADD COLUMN order_name TEXT;

                -- Add V2 fields to order_item_scans
                ALTER TABLE order_item_scans ADD COLUMN printable_file_count INTEGER;
                ALTER TABLE order_item_scans ADD COLUMN billing_metadata_json TEXT;

                -- Add snapshot fields to bill_lines
                ALTER TABLE bill_lines ADD COLUMN product_name_snapshot TEXT;
                ALTER TABLE bill_lines ADD COLUMN billing_method_snapshot TEXT;
                ALTER TABLE bill_lines ADD COLUMN sheet_count INTEGER;
                ALTER TABLE bill_lines ADD COLUMN included_sheets_snapshot INTEGER;
                ALTER TABLE bill_lines ADD COLUMN extra_sheet_count INTEGER;
                ALTER TABLE bill_lines ADD COLUMN base_price_snapshot INTEGER;
                ALTER TABLE bill_lines ADD COLUMN extra_sheet_price_snapshot INTEGER;
                ALTER TABLE bill_lines ADD COLUMN final_print_folder_path TEXT;

                -- Seed default Album 20x20
                INSERT INTO print_specifications (canonical_name, unit_price, category, billing_method, included_sheets, base_price, extra_sheet_price, is_active, created_at, updated_at)
                SELECT 'Album 20x20', 0, 'Album', 'AlbumBasePlusExtra', 10, 400000, 20000, 1, datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM print_specifications WHERE canonical_name = 'Album 20x20');

                -- Add aliases for Album 20x20
                INSERT OR IGNORE INTO print_specification_aliases (print_specification_id, alias_text, normalized_alias)
                SELECT id, 'Album 20x20', 'album 20x20'
                FROM print_specifications WHERE canonical_name = 'Album 20x20';

                INSERT OR IGNORE INTO print_specification_aliases (print_specification_id, alias_text, normalized_alias)
                SELECT id, 'Alb 20x20', 'alb 20x20'
                FROM print_specifications WHERE canonical_name = 'Album 20x20';

                INSERT OR IGNORE INTO print_specification_aliases (print_specification_id, alias_text, normalized_alias)
                SELECT id, 'A20x20', 'a20x20'
                FROM print_specifications WHERE canonical_name = 'Album 20x20';

                INSERT OR IGNORE INTO print_specification_aliases (print_specification_id, alias_text, normalized_alias)
                SELECT id, 'Album20x20', 'album20x20'
                FROM print_specifications WHERE canonical_name = 'Album 20x20';
            "
            },
            new MigrationDefinition
            {
                Version = 4,
                Name = "V2ProductFamilyAndVariants",
                Sql = @"
                -- Product Families table
                CREATE TABLE IF NOT EXISTS product_families (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE,
                    category TEXT NOT NULL,
                    billing_method TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                -- Product Family Aliases (shared across all sizes)
                CREATE TABLE IF NOT EXISTS product_family_aliases (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    family_id INTEGER NOT NULL,
                    alias_text TEXT NOT NULL,
                    normalized_alias TEXT NOT NULL UNIQUE,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(family_id) REFERENCES product_families(id) ON DELETE CASCADE
                );

                -- Product Variants table (enforces unique size per family)
                CREATE TABLE IF NOT EXISTS product_variants (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    family_id INTEGER NOT NULL,
                    canonical_size TEXT NOT NULL,
                    canonical_name TEXT NOT NULL,
                    unit_price INTEGER NOT NULL DEFAULT 0,
                    included_sheets INTEGER,
                    base_price INTEGER,
                    extra_sheet_price INTEGER,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(family_id) REFERENCES product_families(id) ON DELETE CASCADE,
                    UNIQUE(family_id, canonical_size)
                );

                -- Product-Specific Aliases
                CREATE TABLE IF NOT EXISTS product_specific_aliases (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    variant_id INTEGER NOT NULL,
                    alias_text TEXT NOT NULL,
                    normalized_alias TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(variant_id) REFERENCES product_variants(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_family_aliases_normalized ON product_family_aliases(normalized_alias);
                CREATE INDEX IF NOT EXISTS idx_variant_size ON product_variants(canonical_size);
                CREATE INDEX IF NOT EXISTS idx_specific_aliases_normalized ON product_specific_aliases(normalized_alias);

                -- Add family and canonical size columns to print_specifications for backward compatibility
                ALTER TABLE print_specifications ADD COLUMN family_id INTEGER;
                ALTER TABLE print_specifications ADD COLUMN canonical_size TEXT;

                -- Seed default product families
                INSERT OR IGNORE INTO product_families (name, category, billing_method, created_at, updated_at)
                VALUES ('Ảnh in', 'PhotoPrint', 'FileCount', datetime('now'), datetime('now'));

                INSERT OR IGNORE INTO product_families (name, category, billing_method, created_at, updated_at)
                VALUES ('Album', 'Album', 'AlbumBasePlusExtra', datetime('now'), datetime('now'));

                -- Seed family aliases for 'Ảnh in'
                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                SELECT id, 'in', 'in', datetime('now') FROM product_families WHERE name = 'Ảnh in';

                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                SELECT id, 'anh', 'anh', datetime('now') FROM product_families WHERE name = 'Ảnh in';

                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                SELECT id, 'print', 'print', datetime('now') FROM product_families WHERE name = 'Ảnh in';

                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                SELECT id, 'photo', 'photo', datetime('now') FROM product_families WHERE name = 'Ảnh in';

                -- Seed family aliases for 'Album'
                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                SELECT id, 'album', 'album', datetime('now') FROM product_families WHERE name = 'Album';

                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                SELECT id, 'ab', 'ab', datetime('now') FROM product_families WHERE name = 'Album';

                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                SELECT id, 'alb', 'alb', datetime('now') FROM product_families WHERE name = 'Album';
            ",
                PostAction = MigrateProductFamilyAndVariantsAsync
            },
            new MigrationDefinition
            {
                Version = 5,
                Name = "V2EffectiveBillingFolderAndResolutionMode",
                Sql = @"
                ALTER TABLE order_item_scans ADD COLUMN folder_resolution_mode TEXT NOT NULL DEFAULT 'AutoResolved';
                ALTER TABLE bill_lines ADD COLUMN folder_resolution_mode_snapshot TEXT NOT NULL DEFAULT 'AutoResolved';
            "
            },
            new MigrationDefinition
            {
                Version = 6,
                Name = "CustomerBillingAndAdjustments",
                Sql = @"
                CREATE TABLE IF NOT EXISTS customer_bills (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bill_number TEXT NOT NULL UNIQUE,
                    customer_id INTEGER NOT NULL,
                    customer_name_snapshot TEXT NOT NULL,
                    phone_snapshot TEXT,
                    period_start TEXT NOT NULL,
                    period_end TEXT NOT NULL,
                    status TEXT NOT NULL,
                    product_subtotal INTEGER NOT NULL DEFAULT 0,
                    adjustments_total INTEGER NOT NULL DEFAULT 0,
                    grand_total INTEGER NOT NULL DEFAULT 0,
                    note TEXT,
                    export_file_path TEXT,
                    locked_at TEXT,
                    exported_at TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(customer_id) REFERENCES customers(id)
                );

                CREATE TABLE IF NOT EXISTS customer_bill_orders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bill_id INTEGER NOT NULL,
                    order_id INTEGER NOT NULL,
                    order_name_snapshot TEXT NOT NULL,
                    order_date_snapshot TEXT NOT NULL,
                    original_folder_name_snapshot TEXT NOT NULL,
                    subtotal INTEGER NOT NULL DEFAULT 0,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    is_included INTEGER NOT NULL DEFAULT 1,
                    is_from_previous_period INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY(bill_id) REFERENCES customer_bills(id) ON DELETE CASCADE,
                    FOREIGN KEY(order_id) REFERENCES orders(id)
                );

                CREATE TABLE IF NOT EXISTS customer_bill_lines (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bill_id INTEGER NOT NULL,
                    order_id INTEGER NOT NULL,
                    product_job_id INTEGER NOT NULL,
                    product_specification_id INTEGER,
                    product_name_snapshot TEXT NOT NULL,
                    variant_snapshot TEXT,
                    billing_method_snapshot TEXT NOT NULL DEFAULT 'FileCount',
                    scanned_quantity INTEGER NOT NULL,
                    billed_quantity INTEGER NOT NULL,
                    quantity_override_reason TEXT,
                    sheet_count INTEGER,
                    included_sheets_snapshot INTEGER,
                    extra_sheet_count INTEGER,
                    base_price_snapshot INTEGER,
                    extra_sheet_price_snapshot INTEGER,
                    configured_unit_price INTEGER NOT NULL DEFAULT 0,
                    billed_unit_price INTEGER NOT NULL DEFAULT 0,
                    price_override_reason TEXT,
                    line_total INTEGER NOT NULL DEFAULT 0,
                    is_included INTEGER NOT NULL DEFAULT 1,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    final_print_folder_path TEXT,
                    folder_resolution_mode_snapshot TEXT NOT NULL DEFAULT 'AutoResolved',
                    issue_message TEXT,
                    FOREIGN KEY(bill_id) REFERENCES customer_bills(id) ON DELETE CASCADE,
                    FOREIGN KEY(order_id) REFERENCES orders(id),
                    FOREIGN KEY(product_job_id) REFERENCES order_item_scans(id)
                );

                CREATE TABLE IF NOT EXISTS customer_bill_adjustments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bill_id INTEGER NOT NULL,
                    type TEXT NOT NULL,
                    label TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    amount INTEGER NOT NULL DEFAULT 0,
                    note TEXT,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY(bill_id) REFERENCES customer_bills(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_customer_bills_customer_id ON customer_bills(customer_id);
                CREATE INDEX IF NOT EXISTS idx_customer_bills_status ON customer_bills(status);
                CREATE INDEX IF NOT EXISTS idx_customer_bill_lines_bill_id ON customer_bill_lines(bill_id);
                CREATE INDEX IF NOT EXISTS idx_customer_bill_lines_job_id ON customer_bill_lines(product_job_id);
                CREATE INDEX IF NOT EXISTS idx_customer_bill_orders_bill_id ON customer_bill_orders(bill_id);
                CREATE INDEX IF NOT EXISTS idx_customer_bill_adjustments_bill_id ON customer_bill_adjustments(bill_id);

                ALTER TABLE order_item_scans ADD COLUMN customer_bill_id INTEGER;
                CREATE INDEX IF NOT EXISTS idx_order_item_scans_bill_id ON order_item_scans(customer_bill_id);
            "
            },
            new MigrationDefinition
            {
                Version = 7,
                Name = "GuestBillingAndSourceFolders",
                PostAction = MigrateGuestBillingAndSourceFoldersAsync
            },
            new MigrationDefinition
            {
                Version = 8,
                Name = "AddNoteToCustomerBillLines",
                Sql = @"
                ALTER TABLE customer_bill_lines ADD COLUMN note TEXT;
            "
            },
            new MigrationDefinition
            {
                Version = 9,
                Name = "V2LineSpecificationFolderAndFamilyFix",
                PostAction = MigrateLineSpecificationFolderAndFamilyFixAsync
            },
            new MigrationDefinition
            {
                Version = 10,
                Name = "AddOrderCodeAndBackfill",
                PostAction = MigrateOrderCodeAndBackfillAsync
            },
            new MigrationDefinition
            {
                Version = 11,
                Name = "AddFingerprintToOrders",
                Sql = @"
                ALTER TABLE orders ADD COLUMN fingerprint TEXT;
            "
            },
            new MigrationDefinition
            {
                Version = 12,
                Name = "AddFolderPrintStatusTable",
                Sql = @"
                CREATE TABLE IF NOT EXISTS folder_print_statuses (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    folder_path TEXT NOT NULL,
                    normalized_path TEXT NOT NULL UNIQUE,
                    status INTEGER NOT NULL DEFAULT 1,
                    marked_at TEXT NOT NULL,
                    marked_by TEXT NOT NULL DEFAULT 'ExplorerContextMenu',
                    order_id INTEGER,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(order_id) REFERENCES orders(id) ON DELETE SET NULL
                );

                CREATE INDEX IF NOT EXISTS idx_folder_print_statuses_norm_path 
                ON folder_print_statuses(normalized_path);

                CREATE INDEX IF NOT EXISTS idx_folder_print_statuses_status 
                ON folder_print_statuses(status);

                ALTER TABLE orders ADD COLUMN is_printed INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE orders ADD COLUMN printed_at TEXT;
            "
            },
            new MigrationDefinition
            {
                Version = 13,
                Name = "AddMultiRootFoldersAndBackfill",
                Sql = @"
                CREATE TABLE IF NOT EXISTS root_folders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    full_path TEXT NOT NULL UNIQUE,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    is_default INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_root_folders_active ON root_folders(is_active);
                CREATE INDEX IF NOT EXISTS idx_root_folders_default ON root_folders(is_default);
            ",
                PostAction = MigrateRootFoldersAndBackfillAsync
            },
            new MigrationDefinition
            {
                Version = 14,
                Name = "AddBillPaymentStatusAndDebtTracking",
                Sql = @"
                ALTER TABLE customer_bills ADD COLUMN is_paid INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE customer_bills ADD COLUMN paid_at TEXT;

                CREATE INDEX IF NOT EXISTS idx_customer_bills_customer_paid 
                ON customer_bills(customer_id, is_paid);
            "
            },
            new MigrationDefinition
            {
                Version = 15,
                Name = "AddCustomerAddressAndShippingLabel",
                Sql = @"
                ALTER TABLE customers ADD COLUMN address TEXT;
                ALTER TABLE customer_bills ADD COLUMN shipping_address_snapshot TEXT;
            "
            },
            new MigrationDefinition
            {
                Version = 16,
                Name = "AddTieredPricingAndCustomerPriceTier",
                Sql = @"
                ALTER TABLE customers ADD COLUMN price_tier TEXT NOT NULL DEFAULT 'Retail';

                ALTER TABLE product_variants ADD COLUMN unit_price_studio INTEGER;
                ALTER TABLE product_variants ADD COLUMN unit_price_vip INTEGER;
                ALTER TABLE product_variants ADD COLUMN base_price_studio INTEGER;
                ALTER TABLE product_variants ADD COLUMN base_price_vip INTEGER;
                ALTER TABLE product_variants ADD COLUMN extra_sheet_price_studio INTEGER;
                ALTER TABLE product_variants ADD COLUMN extra_sheet_price_vip INTEGER;

                ALTER TABLE print_specifications ADD COLUMN unit_price_studio INTEGER;
                ALTER TABLE print_specifications ADD COLUMN unit_price_vip INTEGER;
                ALTER TABLE print_specifications ADD COLUMN base_price_studio INTEGER;
                ALTER TABLE print_specifications ADD COLUMN base_price_vip INTEGER;
                ALTER TABLE print_specifications ADD COLUMN extra_sheet_price_studio INTEGER;
                ALTER TABLE print_specifications ADD COLUMN extra_sheet_price_vip INTEGER;
            "
            },
            new MigrationDefinition
            {
                Version = 17,
                Name = "AddThumbnailCandidateRelativePathToOrders",
                Sql = @"
                ALTER TABLE orders ADD COLUMN thumbnail_candidate_relative_path TEXT;
            "
            },
            new MigrationDefinition
            {
                Version = 18,
                Name = "AddDeliveryStatusAndNoteToOrders",
                Sql = @"
                ALTER TABLE orders ADD COLUMN is_delivered INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE orders ADD COLUMN delivered_at TEXT;
                ALTER TABLE orders ADD COLUMN delivered_by TEXT;
                ALTER TABLE orders ADD COLUMN note TEXT;
            "
            },
            new MigrationDefinition
            {
                Version = 19,
                Name = "AddPrintProgressAndItemPrintStatus",
                Sql = @"
                ALTER TABLE orders ADD COLUMN print_progress INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE order_item_scans ADD COLUMN is_printed INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE order_item_scans ADD COLUMN printed_at TEXT;
                ALTER TABLE folder_print_statuses ADD COLUMN printed_sub_count INTEGER;
                ALTER TABLE folder_print_statuses ADD COLUMN total_sub_count INTEGER;
            "
            },
            new MigrationDefinition
            {
                Version = 20,
                Name = "AddCloudSyncQueue",
                Sql = @"
                CREATE TABLE IF NOT EXISTS cloud_sync_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entity_type TEXT NOT NULL,
                    entity_id INTEGER NOT NULL,
                    action TEXT NOT NULL DEFAULT 'upsert',
                    payload_json TEXT,
                    status TEXT NOT NULL DEFAULT 'Pending',
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT,
                    created_at TEXT NOT NULL,
                    processed_at TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_cloud_sync_queue_status ON cloud_sync_queue(status);
                CREATE INDEX IF NOT EXISTS idx_cloud_sync_queue_entity ON cloud_sync_queue(entity_type, entity_id);
            "
            },
            new MigrationDefinition
            {
                Version = 21,
                Name = "DurableCloudOperationsAndIdCheckpoint",
                Sql = @"
                CREATE TABLE cloud_sync_state (
                    id INTEGER PRIMARY KEY CHECK(id = 1), cursor INTEGER NOT NULL DEFAULT 0,
                    requires_reconciliation INTEGER NOT NULL DEFAULT 0, stream_id TEXT, endpoint TEXT);
                INSERT INTO cloud_sync_state(id, requires_reconciliation)
                SELECT 1, CASE WHEN EXISTS(SELECT 1 FROM app_settings WHERE key = 'LastCloudPullAt' AND length(trim(value)) > 0) THEN 1 ELSE 0 END;
                CREATE TABLE cloud_applied_events (id INTEGER PRIMARY KEY, event_json TEXT NOT NULL);
                CREATE TABLE cloud_field_revisions (
                    entity_type TEXT NOT NULL, entity_id INTEGER NOT NULL, revision INTEGER NOT NULL,
                    payload_json TEXT NOT NULL, PRIMARY KEY(entity_type, entity_id));
                CREATE TABLE cloud_operational_outbox (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, operation_id TEXT NOT NULL UNIQUE,
                    entity_type TEXT NOT NULL, entity_id INTEGER NOT NULL, value_json TEXT NOT NULL,
                    delivered_by TEXT, created_at TEXT NOT NULL);
                CREATE INDEX idx_cloud_outbox_entity ON cloud_operational_outbox(entity_type, entity_id);
                CREATE TABLE cloud_reconciliation_reports(report_id TEXT PRIMARY KEY, report_json TEXT NOT NULL, created_at TEXT NOT NULL, approved_at TEXT);
                CREATE TABLE cloud_applied_event_archive(stream_id TEXT NOT NULL,id INTEGER NOT NULL,event_json TEXT NOT NULL,archived_at TEXT NOT NULL,PRIMARY KEY(stream_id,id));
            "
            },
            new MigrationDefinition
            {
                Version = 22,
                Name = "DurableRemotePrintLedger",
                Sql = @"
                CREATE TABLE cloud_print_command_ledger (
                    endpoint TEXT NOT NULL, stream_id TEXT NOT NULL, command_id INTEGER NOT NULL,
                    claim_token TEXT NOT NULL, status TEXT NOT NULL CHECK(status IN ('STARTED','COMPLETED','FAILED')),
                    error_message TEXT, acknowledged INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(endpoint,stream_id,command_id));"
            },
            new MigrationDefinition
            {
                Version = 23,
                Name = "ThumbnailCacheMetadata",
                Sql = @"
                CREATE TABLE IF NOT EXISTS thumbnail_cache_entries (
                    cache_key TEXT PRIMARY KEY,
                    root_folder_id INTEGER,
                    source_relative_path TEXT NOT NULL,
                    source_file_size INTEGER NOT NULL,
                    source_last_write_ticks INTEGER NOT NULL,
                    codec_version TEXT NOT NULL,
                    cached_file_path TEXT NOT NULL,
                    cached_file_size INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    last_used_at_utc TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'Ready'
                );
                CREATE INDEX IF NOT EXISTS idx_thumbnail_cache_last_used ON thumbnail_cache_entries(last_used_at_utc);
                CREATE INDEX IF NOT EXISTS idx_thumbnail_cache_source ON thumbnail_cache_entries(root_folder_id, source_relative_path);
                "
            },
            new MigrationDefinition
            {
                Version = 24,
                Name = "ServerIdentity",
                Sql = @"
                CREATE TABLE IF NOT EXISTS server_identity (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    server_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                ",
                PostAction = async (conn, tx, logger) =>
                {
                    await conn.ExecuteAsync(@"
                        INSERT OR IGNORE INTO server_identity (id, server_id, created_at_utc)
                        VALUES (1, @ServerId, @CreatedAt);
                    ", new
                    {
                        ServerId = Guid.NewGuid().ToString(),
                        CreatedAt = DateTimeOffset.UtcNow.ToString("o")
                    }, transaction: tx);
                }
            },
            new MigrationDefinition
            {
                Version = 25,
                Name = "AuthCredentials",
                Sql = @"
                CREATE TABLE IF NOT EXISTS auth_credentials (
                    role              TEXT PRIMARY KEY,
                    algorithm         TEXT NOT NULL,
                    iterations        INTEGER NOT NULL,
                    salt              BLOB,
                    hash              BLOB,
                    credential_epoch  INTEGER NOT NULL DEFAULT 1,
                    updated_at_utc    TEXT NOT NULL
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS auth_state (
                    id                                INTEGER PRIMARY KEY CHECK (id = 1),
                    legacy_tokens_issued_before_utc   TEXT NOT NULL,
                    legacy_tokens_accepted_until_utc  TEXT NOT NULL
                );
                ",
                PostAction = async (conn, tx, logger) =>
                {
                    string nowIso = DateTimeOffset.UtcNow.ToString("o");
                    string graceUntilIso = DateTimeOffset.UtcNow.AddDays(7).ToString("o");

                    int settingsTableExists = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_settings';",
                        transaction: tx);

                    string adminPin = "";
                    string staffPin = "";
                    if (settingsTableExists > 0)
                    {
                        var pinRows = (await conn.QueryAsync<(string Key, string Value)>(
                            "SELECT key, value FROM app_settings WHERE key IN ('AdminPin', 'StaffPin');",
                            transaction: tx)).ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

                        adminPin = pinRows.TryGetValue("AdminPin", out var ap) ? (ap?.Trim() ?? "") : "";
                        staffPin = pinRows.TryGetValue("StaffPin", out var sp) ? (sp?.Trim() ?? "") : "";
                    }

                    byte[]? adminSalt = null;
                    byte[]? adminHash = null;
                    if (!string.IsNullOrEmpty(adminPin))
                    {
                        adminSalt = Pbkdf2PinHasher.GenerateSalt();
                        adminHash = Pbkdf2PinHasher.Hash(adminPin, adminSalt, Pbkdf2PinHasher.DefaultIterations);
                    }

                    await conn.ExecuteAsync(@"
                        INSERT OR IGNORE INTO auth_credentials (role, algorithm, iterations, salt, hash, credential_epoch, updated_at_utc)
                        VALUES ('Admin', 'PBKDF2-SHA256', @Iterations, @Salt, @Hash, 1, @UpdatedAt);
                    ", new
                    {
                        Iterations = Pbkdf2PinHasher.DefaultIterations,
                        Salt = adminSalt,
                        Hash = adminHash,
                        UpdatedAt = nowIso
                    }, transaction: tx);

                    if (adminHash != null)
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE auth_credentials
                            SET salt = @Salt, hash = @Hash, updated_at_utc = @UpdatedAt
                            WHERE role = 'Admin' AND hash IS NULL;
                        ", new { Salt = adminSalt, Hash = adminHash, UpdatedAt = nowIso }, transaction: tx);
                    }

                    byte[]? staffSalt = null;
                    byte[]? staffHash = null;
                    if (!string.IsNullOrEmpty(staffPin) && (string.IsNullOrEmpty(adminPin) || !string.Equals(adminPin, staffPin, StringComparison.Ordinal)))
                    {
                        staffSalt = Pbkdf2PinHasher.GenerateSalt();
                        staffHash = Pbkdf2PinHasher.Hash(staffPin, staffSalt, Pbkdf2PinHasher.DefaultIterations);
                    }

                    await conn.ExecuteAsync(@"
                        INSERT OR IGNORE INTO auth_credentials (role, algorithm, iterations, salt, hash, credential_epoch, updated_at_utc)
                        VALUES ('Staff', 'PBKDF2-SHA256', @Iterations, @Salt, @Hash, 1, @UpdatedAt);
                    ", new
                    {
                        Iterations = Pbkdf2PinHasher.DefaultIterations,
                        Salt = staffSalt,
                        Hash = staffHash,
                        UpdatedAt = nowIso
                    }, transaction: tx);

                    if (staffHash != null)
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE auth_credentials
                            SET salt = @Salt, hash = @Hash, updated_at_utc = @UpdatedAt
                            WHERE role = 'Staff' AND hash IS NULL;
                        ", new { Salt = staffSalt, Hash = staffHash, UpdatedAt = nowIso }, transaction: tx);
                    }

                    if (settingsTableExists > 0)
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE app_settings SET value = '' WHERE key IN ('AdminPin', 'StaffPin');
                        ", transaction: tx);
                    }

                    await conn.ExecuteAsync(@"
                        INSERT OR IGNORE INTO auth_state (id, legacy_tokens_issued_before_utc, legacy_tokens_accepted_until_utc)
                        VALUES (1, @IssuedBefore, @AcceptedUntil);
                    ", new
                    {
                        IssuedBefore = nowIso,
                        AcceptedUntil = graceUntilIso
                    }, transaction: tx);
                }
            },
            new MigrationDefinition
            {
                Version = 26,
                Name = "AuthSessions",
                Sql = @"
                CREATE TABLE IF NOT EXISTS auth_sessions (
                    session_id        TEXT PRIMARY KEY,
                    token_hash        BLOB NOT NULL,
                    role              TEXT NOT NULL,
                    credential_epoch  INTEGER NOT NULL,
                    client_kind       TEXT NOT NULL,
                    device_id         TEXT,
                    device_label      TEXT,
                    issued_ip         TEXT,
                    issued_at_utc     TEXT NOT NULL,
                    expires_at_utc    TEXT NOT NULL,
                    last_seen_at_utc  TEXT,
                    revoked_at_utc    TEXT,
                    revoked_reason    TEXT
                ) WITHOUT ROWID;
                CREATE UNIQUE INDEX IF NOT EXISTS ux_auth_sessions_token_hash ON auth_sessions(token_hash);
                CREATE INDEX IF NOT EXISTS idx_auth_sessions_role_active ON auth_sessions(role, revoked_at_utc, expires_at_utc);
                "
            },
            new MigrationDefinition
            {
                Version = 27,
                Name = "AggregateRevisions",
                Sql = "",
                PostAction = MigrateAggregateRevisionsAsync
            },
            new MigrationDefinition
            {
                Version = 28,
                Name = "CommandReceiptsAndAudit",
                Sql = @"
                CREATE TABLE IF NOT EXISTS command_receipts (
                    operation_id      TEXT PRIMARY KEY,
                    capability        TEXT NOT NULL,
                    entity_type       TEXT NOT NULL,
                    entity_id         INTEGER NOT NULL,
                    payload_hash      TEXT NOT NULL,
                    status            TEXT NOT NULL,
                    result_json       TEXT NOT NULL,
                    actor_kind        TEXT NOT NULL,
                    actor_session_id  TEXT,
                    actor_role        TEXT NOT NULL,
                    created_at_utc    TEXT NOT NULL,
                    completed_at_utc  TEXT NOT NULL
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS idx_command_receipts_entity  ON command_receipts(entity_type, entity_id);
                CREATE INDEX IF NOT EXISTS idx_command_receipts_created ON command_receipts(created_at_utc);

                CREATE TABLE IF NOT EXISTS audit_log (
                    id                INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_at_utc   TEXT NOT NULL,
                    action            TEXT NOT NULL,
                    outcome           TEXT NOT NULL,
                    operation_id      TEXT,
                    entity_type       TEXT,
                    entity_id         INTEGER,
                    before_json       TEXT,
                    after_json        TEXT,
                    reason            TEXT,
                    actor_kind        TEXT NOT NULL,
                    actor_role        TEXT,
                    actor_session_id  TEXT,
                    actor_device_id   TEXT,
                    actor_label       TEXT,
                    client_ip         TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_audit_log_entity    ON audit_log(entity_type, entity_id, id);
                CREATE INDEX IF NOT EXISTS idx_audit_log_operation ON audit_log(operation_id);
                CREATE INDEX IF NOT EXISTS idx_audit_log_time      ON audit_log(occurred_at_utc);
                "
            },
            new MigrationDefinition
            {
                Version = 29,
                Name = "ChangeJournalAndRevisionTriggers",
                Sql = @"
                CREATE TABLE IF NOT EXISTS change_journal (
                    seq                 INTEGER PRIMARY KEY AUTOINCREMENT,
                    database_generation INTEGER NOT NULL,
                    entity_type         TEXT NOT NULL,
                    entity_id           INTEGER NOT NULL,
                    field_group         TEXT NOT NULL,
                    revision            INTEGER NOT NULL,
                    operation_id        TEXT,
                    occurred_at_utc     TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_change_journal_entity ON change_journal(entity_type, entity_id, seq);

                CREATE TRIGGER IF NOT EXISTS trg_orders_delivered_revision
                AFTER UPDATE OF is_delivered, delivered_at, delivered_by ON orders
                WHEN OLD.is_delivered IS NOT NEW.is_delivered
                  OR OLD.delivered_at IS NOT NEW.delivered_at
                  OR OLD.delivered_by IS NOT NEW.delivered_by
BEGIN
                    UPDATE orders SET delivered_revision = OLD.delivered_revision + 1 WHERE id = NEW.id;
                    INSERT INTO change_journal(database_generation, entity_type, entity_id, field_group, revision, occurred_at_utc)
                    VALUES (COALESCE((SELECT database_generation FROM server_identity WHERE id = 1), 1),
                            'order', NEW.id, 'delivered', OLD.delivered_revision + 1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
END;

                CREATE TRIGGER IF NOT EXISTS trg_orders_printed_revision
                AFTER UPDATE OF is_printed, printed_at, print_progress ON orders
                WHEN OLD.is_printed IS NOT NEW.is_printed
                  OR OLD.printed_at IS NOT NEW.printed_at
                  OR OLD.print_progress IS NOT NEW.print_progress
BEGIN
                    UPDATE orders SET printed_revision = OLD.printed_revision + 1 WHERE id = NEW.id;
                    INSERT INTO change_journal(database_generation, entity_type, entity_id, field_group, revision, occurred_at_utc)
                    VALUES (COALESCE((SELECT database_generation FROM server_identity WHERE id = 1), 1),
                            'order', NEW.id, 'printed', OLD.printed_revision + 1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
END;

                CREATE TRIGGER IF NOT EXISTS trg_orders_note_revision
                AFTER UPDATE OF note ON orders
                WHEN OLD.note IS NOT NEW.note
BEGIN
                    UPDATE orders SET note_revision = OLD.note_revision + 1 WHERE id = NEW.id;
                    INSERT INTO change_journal(database_generation, entity_type, entity_id, field_group, revision, occurred_at_utc)
                    VALUES (COALESCE((SELECT database_generation FROM server_identity WHERE id = 1), 1),
                            'order', NEW.id, 'note', OLD.note_revision + 1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
END;

                CREATE TRIGGER IF NOT EXISTS trg_customer_bills_revision
                AFTER UPDATE ON customer_bills
                WHEN NEW.revision IS OLD.revision
BEGIN
                    UPDATE customer_bills SET revision = OLD.revision + 1 WHERE id = NEW.id;
                    INSERT INTO change_journal(database_generation, entity_type, entity_id, field_group, revision, occurred_at_utc)
                    VALUES (COALESCE((SELECT database_generation FROM server_identity WHERE id = 1), 1),
                            'customer_bill', NEW.id, 'bill', OLD.revision + 1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
END;
                ",
                PostAction = MigrateDatabaseGenerationAsync
            }
        };
    }

    private static async Task MigrateProductFamilyAndVariantsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILogger? logger)
    {
        long photoFamilyId = await connection.QuerySingleAsync<long>(
            "SELECT id FROM product_families WHERE name = 'Ảnh in'", transaction: transaction);
        long albumFamilyId = await connection.QuerySingleAsync<long>(
            "SELECT id FROM product_families WHERE name = 'Album'", transaction: transaction);

        var specs = (await connection.QueryAsync<PrintSpecMigrationDto>(
            "SELECT * FROM print_specifications", transaction: transaction)).ToList();

        var existingAliases = (await connection.QueryAsync<SpecAliasMigrationDto>(
            "SELECT * FROM print_specification_aliases", transaction: transaction)).ToList();
        var aliasLookup = existingAliases.ToLookup(a => a.print_specification_id);

        foreach (var spec in specs)
        {
            long familyId = string.Equals(spec.category, "Album", StringComparison.OrdinalIgnoreCase)
                ? albumFamilyId
                : photoFamilyId;

            string canonicalSize;
            if (SizeNormalizer.TryExtractDimensions(spec.canonical_name, out var extracted, out _, out _))
            {
                canonicalSize = extracted;
            }
            else
            {
                canonicalSize = CustomerNormalizer.Normalize(spec.canonical_name);
            }

            var existingVariant = await connection.QuerySingleOrDefaultAsync<ProductVariantMigrationDto>(
                "SELECT * FROM product_variants WHERE family_id = @FamilyId AND canonical_size = @CanonicalSize LIMIT 1",
                new { FamilyId = familyId, CanonicalSize = canonicalSize },
                transaction: transaction);

            long variantIdToUse;

            if (existingVariant == null)
            {
                variantIdToUse = spec.id;
                await connection.ExecuteAsync(@"
                    INSERT INTO product_variants (
                        id, family_id, canonical_size, canonical_name, unit_price,
                        included_sheets, base_price, extra_sheet_price, is_active, created_at, updated_at
                    ) VALUES (
                        @Id, @FamilyId, @CanonicalSize, @CanonicalName, @UnitPrice,
                        @IncludedSheets, @BasePrice, @ExtraSheetPrice, @IsActive, @CreatedAt, @UpdatedAt
                    );
                ", new
                {
                    Id = spec.id,
                    FamilyId = familyId,
                    CanonicalSize = canonicalSize,
                    CanonicalName = spec.canonical_name,
                    UnitPrice = spec.unit_price,
                    IncludedSheets = spec.included_sheets,
                    BasePrice = spec.base_price,
                    ExtraSheetPrice = spec.extra_sheet_price,
                    IsActive = spec.is_active,
                    CreatedAt = spec.created_at ?? DateTimeOffset.UtcNow.ToString("o"),
                    UpdatedAt = spec.updated_at ?? DateTimeOffset.UtcNow.ToString("o")
                }, transaction: transaction);
            }
            else
            {
                logger?.LogWarning("Migration collision detected for variant size '{Size}' in family {FamilyId}. Existing ID: {ExistingId}, current spec ID: {SpecId}",
                    canonicalSize, familyId, existingVariant.id, spec.id);

                bool priceConfigMatches = (existingVariant.unit_price == spec.unit_price &&
                                           existingVariant.base_price == spec.base_price &&
                                           existingVariant.extra_sheet_price == spec.extra_sheet_price &&
                                           existingVariant.included_sheets == spec.included_sheets);

                if (priceConfigMatches)
                {
                    variantIdToUse = existingVariant.id;
                    string normSpecName = CustomerNormalizer.Normalize(spec.canonical_name);
                    await connection.ExecuteAsync(@"
                        INSERT OR IGNORE INTO product_specific_aliases (variant_id, alias_text, normalized_alias, created_at)
                        VALUES (@VariantId, @AliasText, @NormalizedAlias, @CreatedAt);
                    ", new
                    {
                        VariantId = existingVariant.id,
                        AliasText = spec.canonical_name,
                        NormalizedAlias = normSpecName,
                        CreatedAt = DateTimeOffset.UtcNow.ToString("o")
                    }, transaction: transaction);
                }
                else
                {
                    string conflictSize = $"{canonicalSize}-c{spec.id}";
                    variantIdToUse = spec.id;
                    await connection.ExecuteAsync(@"
                        INSERT INTO product_variants (
                            id, family_id, canonical_size, canonical_name, unit_price,
                            included_sheets, base_price, extra_sheet_price, is_active, created_at, updated_at
                        ) VALUES (
                            @Id, @FamilyId, @CanonicalSize, @CanonicalName, @UnitPrice,
                            @IncludedSheets, @BasePrice, @ExtraSheetPrice, @IsActive, @CreatedAt, @UpdatedAt
                        );
                    ", new
                    {
                        Id = spec.id,
                        FamilyId = familyId,
                        CanonicalSize = conflictSize,
                        CanonicalName = spec.canonical_name,
                        UnitPrice = spec.unit_price,
                        IncludedSheets = spec.included_sheets,
                        BasePrice = spec.base_price,
                        ExtraSheetPrice = spec.extra_sheet_price,
                        IsActive = spec.is_active,
                        CreatedAt = spec.created_at ?? DateTimeOffset.UtcNow.ToString("o"),
                        UpdatedAt = spec.updated_at ?? DateTimeOffset.UtcNow.ToString("o")
                    }, transaction: transaction);
                }
            }

            string normName = CustomerNormalizer.Normalize(spec.canonical_name);
            if (!string.Equals(normName, canonicalSize, StringComparison.OrdinalIgnoreCase))
            {
                await connection.ExecuteAsync(@"
                    INSERT OR IGNORE INTO product_specific_aliases (variant_id, alias_text, normalized_alias, created_at)
                    VALUES (@VariantId, @AliasText, @NormalizedAlias, @CreatedAt);
                ", new
                {
                    VariantId = variantIdToUse,
                    AliasText = spec.canonical_name,
                    NormalizedAlias = normName,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("o")
                }, transaction: transaction);
            }

            await connection.ExecuteAsync(@"
                UPDATE print_specifications
                SET family_id = @FamilyId,
                    canonical_size = @CanonicalSize
                WHERE id = @Id;
            ", new
            {
                Id = spec.id,
                FamilyId = familyId,
                CanonicalSize = canonicalSize
            }, transaction: transaction);

            foreach (var alias in aliasLookup[spec.id])
            {
                await connection.ExecuteAsync(@"
                    INSERT OR IGNORE INTO product_specific_aliases (variant_id, alias_text, normalized_alias, created_at)
                    VALUES (@VariantId, @AliasText, @NormalizedAlias, @CreatedAt);
                ", new
                {
                    VariantId = variantIdToUse,
                    AliasText = alias.alias_text,
                    NormalizedAlias = alias.normalized_alias,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("o")
                }, transaction: transaction);
            }
        }
    }

    private class PrintSpecMigrationDto
    {
        public long id { get; set; }
        public string canonical_name { get; set; } = string.Empty;
        public string category { get; set; } = "PhotoPrint";
        public string billing_method { get; set; } = "FileCount";
        public long unit_price { get; set; }
        public int? included_sheets { get; set; }
        public long? base_price { get; set; }
        public long? extra_sheet_price { get; set; }
        public int is_active { get; set; } = 1;
        public string? created_at { get; set; }
        public string? updated_at { get; set; }
    }

    private class SpecAliasMigrationDto
    {
        public long id { get; set; }
        public long print_specification_id { get; set; }
        public string alias_text { get; set; } = string.Empty;
        public string normalized_alias { get; set; } = string.Empty;
    }

    private class ProductVariantMigrationDto
    {
        public long id { get; set; }
        public long family_id { get; set; }
        public string canonical_size { get; set; } = string.Empty;
        public string canonical_name { get; set; } = string.Empty;
        public long unit_price { get; set; }
        public int? included_sheets { get; set; }
        public long? base_price { get; set; }
        public long? extra_sheet_price { get; set; }
        public int is_active { get; set; } = 1;
    }

    private static async Task MigrateGuestBillingAndSourceFoldersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILogger? logger)
    {
        // 1. Ensure customer_bills has bill_type column and customer_id is nullable
        var tableInfo = (await connection.QueryAsync<TableColumnDto>(
            "PRAGMA table_info(customer_bills);", transaction: transaction)).ToList();

        bool hasBillType = tableInfo.Any(c => string.Equals(c.name, "bill_type", StringComparison.OrdinalIgnoreCase));
        var custIdCol = tableInfo.FirstOrDefault(c => string.Equals(c.name, "customer_id", StringComparison.OrdinalIgnoreCase));
        bool custIdNotNull = custIdCol != null && custIdCol.notnull == 1;

        if (!hasBillType || custIdNotNull)
        {
            logger?.LogInformation("Recreating customer_bills table to support nullable customer_id and bill_type...");

            await connection.ExecuteAsync(@"
                CREATE TABLE customer_bills_tmp (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bill_number TEXT NOT NULL UNIQUE,
                    bill_type TEXT NOT NULL DEFAULT 'Customer',
                    customer_id INTEGER,
                    customer_name_snapshot TEXT NOT NULL,
                    phone_snapshot TEXT,
                    period_start TEXT NOT NULL,
                    period_end TEXT NOT NULL,
                    status TEXT NOT NULL,
                    product_subtotal INTEGER NOT NULL DEFAULT 0,
                    adjustments_total INTEGER NOT NULL DEFAULT 0,
                    grand_total INTEGER NOT NULL DEFAULT 0,
                    note TEXT,
                    export_file_path TEXT,
                    locked_at TEXT,
                    exported_at TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(customer_id) REFERENCES customers(id)
                );
            ", transaction: transaction);

            string selectBillType = hasBillType ? "bill_type" : "'Customer'";
            await connection.ExecuteAsync($@"
                INSERT INTO customer_bills_tmp (
                    id, bill_number, bill_type, customer_id, customer_name_snapshot,
                    phone_snapshot, period_start, period_end, status, product_subtotal,
                    adjustments_total, grand_total, note, export_file_path, locked_at,
                    exported_at, created_at, updated_at
                )
                SELECT
                    id, bill_number, {selectBillType}, customer_id, customer_name_snapshot,
                    phone_snapshot, period_start, period_end, status, product_subtotal,
                    adjustments_total, grand_total, note, export_file_path, locked_at,
                    exported_at, created_at, updated_at
                FROM customer_bills;
            ", transaction: transaction);

            await connection.ExecuteAsync("DROP TABLE customer_bills;", transaction: transaction);
            await connection.ExecuteAsync("ALTER TABLE customer_bills_tmp RENAME TO customer_bills;", transaction: transaction);

            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_customer_bills_customer_id ON customer_bills(customer_id);", transaction: transaction);
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_customer_bills_status ON customer_bills(status);", transaction: transaction);
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_customer_bills_type ON customer_bills(bill_type);", transaction: transaction);
        }

        // 2. Ensure customer_bill_orders has source_folder_path
        var orderCols = (await connection.QueryAsync<TableColumnDto>(
            "PRAGMA table_info(customer_bill_orders);", transaction: transaction)).ToList();
        if (!orderCols.Any(c => string.Equals(c.name, "source_folder_path", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE customer_bill_orders ADD COLUMN source_folder_path TEXT;", transaction: transaction);
        }

        // 3. Create guest_bill_source_folders table
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS guest_bill_source_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                bill_id INTEGER NOT NULL,
                folder_path TEXT NOT NULL,
                normalized_folder_path TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(bill_id) REFERENCES customer_bills(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_guest_source_folders_bill_id ON guest_bill_source_folders(bill_id);
            CREATE INDEX IF NOT EXISTS idx_guest_source_folders_normalized ON guest_bill_source_folders(normalized_folder_path);
        ", transaction: transaction);
    }

    private static async Task MigrateLineSpecificationFolderAndFamilyFixAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILogger? logger)
    {
        // 1. Ensure customer_bill_lines has specification_folder_name
        var cols = (await connection.QueryAsync<TableColumnDto>(
            "PRAGMA table_info(customer_bill_lines);", transaction: transaction)).ToList();

        if (!cols.Any(c => string.Equals(c.name, "specification_folder_name", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE customer_bill_lines ADD COLUMN specification_folder_name TEXT;", transaction: transaction);
        }

        // Backfill existing rows from order_item_scans
        await connection.ExecuteAsync(@"
            UPDATE customer_bill_lines
            SET specification_folder_name = (
                SELECT ois.specification_folder_name
                FROM order_item_scans ois
                WHERE ois.id = customer_bill_lines.product_job_id
            )
            WHERE (specification_folder_name IS NULL OR specification_folder_name = '')
              AND EXISTS (SELECT 1 FROM order_item_scans WHERE id = customer_bill_lines.product_job_id);
        ", transaction: transaction);

        // 2. Fix Product Family for 'Tranh Mica' if it was placed into 'Ảnh in' (family_id = 1)
        bool hasTranhMicaInPhotoFamily = await connection.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM product_variants
            WHERE (canonical_name LIKE '%Tranh Mica%' OR canonical_name LIKE '%Mica%')
              AND family_id = (SELECT id FROM product_families WHERE name = 'Ảnh in');
        ", transaction: transaction) > 0;

        if (hasTranhMicaInPhotoFamily)
        {
            logger?.LogInformation("Moving 'Tranh Mica' variants out of 'Ảnh in' family to dedicated 'Tranh Mica' family...");

            // Create 'Tranh Mica' family if missing
            await connection.ExecuteAsync(@"
                INSERT OR IGNORE INTO product_families (name, category, billing_method, created_at, updated_at)
                VALUES ('Tranh Mica', 'PhotoPrint', 'FileCount', datetime('now'), datetime('now'));
            ", transaction: transaction);

            long micaFamId = await connection.QuerySingleAsync<long>(
                "SELECT id FROM product_families WHERE name = 'Tranh Mica';", transaction: transaction);

            // Seed aliases for Tranh Mica
            await connection.ExecuteAsync(@"
                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                VALUES (@FamilyId, 'tranh mica', 'tranh mica', datetime('now'));
                INSERT OR IGNORE INTO product_family_aliases (family_id, alias_text, normalized_alias, created_at)
                VALUES (@FamilyId, 'mica', 'mica', datetime('now'));
            ", new { FamilyId = micaFamId }, transaction: transaction);

            // Update variants
            await connection.ExecuteAsync(@"
                UPDATE product_variants
                SET family_id = @FamilyId
                WHERE (canonical_name LIKE '%Tranh Mica%' OR canonical_name LIKE '%Mica%')
                  AND family_id = (SELECT id FROM product_families WHERE name = 'Ảnh in');
            ", new { FamilyId = micaFamId }, transaction: transaction);

            // Update print_specifications for backward compatibility
            await connection.ExecuteAsync(@"
                UPDATE print_specifications
                SET family_id = @FamilyId
                WHERE (canonical_name LIKE '%Tranh Mica%' OR canonical_name LIKE '%Mica%')
                  AND family_id = (SELECT id FROM product_families WHERE name = 'Ảnh in');
            ", new { FamilyId = micaFamId }, transaction: transaction);
        }
    }

    private static async Task MigrateAggregateRevisionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILogger? logger)
    {
        var orderCols = (await connection.QueryAsync<TableColumnDto>(
            "PRAGMA table_info(orders);", transaction: transaction)).ToList();

        if (!orderCols.Any(c => string.Equals(c.name, "delivered_revision", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE orders ADD COLUMN delivered_revision INTEGER NOT NULL DEFAULT 0;", transaction: transaction);
        }
        if (!orderCols.Any(c => string.Equals(c.name, "printed_revision", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE orders ADD COLUMN printed_revision INTEGER NOT NULL DEFAULT 0;", transaction: transaction);
        }
        if (!orderCols.Any(c => string.Equals(c.name, "note_revision", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE orders ADD COLUMN note_revision INTEGER NOT NULL DEFAULT 0;", transaction: transaction);
        }

        var billCols = (await connection.QueryAsync<TableColumnDto>(
            "PRAGMA table_info(customer_bills);", transaction: transaction)).ToList();

        if (!billCols.Any(c => string.Equals(c.name, "revision", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE customer_bills ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;", transaction: transaction);
        }
    }

    private static async Task MigrateDatabaseGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILogger? logger)
    {
        var cols = (await connection.QueryAsync<TableColumnDto>(
            "PRAGMA table_info(server_identity);", transaction: transaction)).ToList();

        if (!cols.Any(c => string.Equals(c.name, "database_generation", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE server_identity ADD COLUMN database_generation INTEGER NOT NULL DEFAULT 1;", transaction: transaction);
        }
    }

    private static async Task MigrateOrderCodeAndBackfillAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILogger? logger)
    {
        // 1. Add order_code to orders if missing
        var orderCols = (await connection.QueryAsync<TableColumnDto>(
            "PRAGMA table_info(orders);", transaction: transaction)).ToList();

        if (!orderCols.Any(c => string.Equals(c.name, "order_code", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE orders ADD COLUMN order_code TEXT;", transaction: transaction);
        }

        // 2. Add order_code_snapshot to customer_bill_orders if missing
        var billOrderCols = (await connection.QueryAsync<TableColumnDto>(
            "PRAGMA table_info(customer_bill_orders);", transaction: transaction)).ToList();

        if (!billOrderCols.Any(c => string.Equals(c.name, "order_code_snapshot", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ExecuteAsync("ALTER TABLE customer_bill_orders ADD COLUMN order_code_snapshot TEXT;", transaction: transaction);
        }

        // 3. Backfill order_code for existing orders that have NULL or empty order_code
        var ordersToBackfill = (await connection.QueryAsync<OrderBackfillDto>(
            "SELECT id, work_date, order_code FROM orders ORDER BY work_date, id",
            transaction: transaction)).ToList();

        var dateGroups = ordersToBackfill.GroupBy(o => o.work_date);
        foreach (var group in dateGroups)
        {
            string workDate = group.Key;
            int maxSeq = 0;

            foreach (var item in group)
            {
                if (OrderCodeGenerator.TryParseSequence(item.order_code, workDate, out int existingSeq))
                {
                    if (existingSeq > maxSeq) maxSeq = existingSeq;
                }
            }

            foreach (var item in group)
            {
                if (string.IsNullOrWhiteSpace(item.order_code))
                {
                    maxSeq++;
                    string newCode = OrderCodeGenerator.Generate(workDate, maxSeq);
                    await connection.ExecuteAsync(
                        "UPDATE orders SET order_code = @OrderCode WHERE id = @Id",
                        new { OrderCode = newCode, Id = item.id },
                        transaction: transaction);
                    item.order_code = newCode;
                }
            }
        }

        // 4. Backfill customer_bill_orders.order_code_snapshot from orders.order_code
        await connection.ExecuteAsync(@"
            UPDATE customer_bill_orders
            SET order_code_snapshot = (
                SELECT o.order_code
                FROM orders o
                WHERE o.id = customer_bill_orders.order_id
            )
            WHERE (order_code_snapshot IS NULL OR order_code_snapshot = '')
              AND EXISTS (SELECT 1 FROM orders o WHERE o.id = customer_bill_orders.order_id);
        ", transaction: transaction);

        // 5. Create unique index on orders.order_code and index on customer_bill_orders.order_code_snapshot
        await connection.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_orders_order_code ON orders(order_code);",
            transaction: transaction);

        await connection.ExecuteAsync(
            "CREATE INDEX IF NOT EXISTS idx_customer_bill_orders_order_code ON customer_bill_orders(order_code_snapshot);",
            transaction: transaction);

        logger?.LogInformation("Migration V10 (AddOrderCodeAndBackfill) completed successfully.");
    }

    private static async Task MigrateRootFoldersAndBackfillAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ILogger? logger)
    {
        long defaultRootId = 0;

        // 1. Check if root_folder exists in app_settings
        int settingsTableExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_settings';",
            transaction: transaction);

        if (settingsTableExists > 0)
        {
            string? existingRoot = await connection.ExecuteScalarAsync<string?>(
                "SELECT value FROM app_settings WHERE key = 'RootFolder' LIMIT 1;", transaction: transaction);

            if (!string.IsNullOrWhiteSpace(existingRoot))
            {
                string now = DateTimeOffset.UtcNow.ToString("O");
                await connection.ExecuteAsync(@"
                    INSERT OR IGNORE INTO root_folders (name, full_path, is_active, is_default, created_at, updated_at)
                    VALUES ('Kho chính', @FullPath, 1, 1, @Now, @Now);
                ", new { FullPath = existingRoot.Trim(), Now = now }, transaction: transaction);
            }
        }

        // Get default root id if exists
        defaultRootId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM root_folders WHERE is_default = 1 LIMIT 1;", transaction: transaction);

        if (defaultRootId == 0)
        {
            defaultRootId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM root_folders ORDER BY id ASC LIMIT 1;", transaction: transaction);
        }

        if (defaultRootId == 0)
        {
            int orderCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM orders;", transaction: transaction);
            if (orderCount > 0)
            {
                string now = DateTimeOffset.UtcNow.ToString("O");
                defaultRootId = await connection.QuerySingleAsync<long>(@"
                    INSERT INTO root_folders (name, full_path, is_active, is_default, created_at, updated_at)
                    VALUES ('Kho chính', 'C:\\LalabData', 1, 1, @Now, @Now);
                    SELECT last_insert_rowid();
                ", new { Now = now }, transaction: transaction);
            }
        }

        // 2. Recreate orders table to support composite UNIQUE(root_folder_id, relative_path)
        int ordersExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='orders';", transaction: transaction);

        if (ordersExists > 0)
        {
            await connection.ExecuteAsync(@"
                CREATE TABLE orders_tmp (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    order_code TEXT,
                    work_date TEXT NOT NULL,
                    customer_id INTEGER,
                    order_kind TEXT NOT NULL DEFAULT 'Implicit',
                    order_name TEXT,
                    original_folder_name TEXT NOT NULL,
                    relative_path TEXT NOT NULL,
                    status TEXT NOT NULL,
                    is_printed INTEGER NOT NULL DEFAULT 0,
                    printed_at TEXT,
                    filesystem_changed_after_lock INTEGER NOT NULL DEFAULT 0,
                    fingerprint TEXT,
                    last_scan_at TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    root_folder_id INTEGER,
                    FOREIGN KEY(customer_id) REFERENCES customers(id),
                    FOREIGN KEY(root_folder_id) REFERENCES root_folders(id)
                );
            ", transaction: transaction);

            await connection.ExecuteAsync(@"
                INSERT INTO orders_tmp (
                    id, order_code, work_date, customer_id, order_kind, order_name,
                    original_folder_name, relative_path, status, is_printed, printed_at,
                    filesystem_changed_after_lock, fingerprint, last_scan_at, created_at, updated_at,
                    root_folder_id
                )
                SELECT
                    id, order_code, work_date, customer_id, order_kind, order_name,
                    original_folder_name, relative_path, status, is_printed, printed_at,
                    filesystem_changed_after_lock, fingerprint, last_scan_at, created_at, updated_at,
                    CASE WHEN @DefaultRootId > 0 THEN @DefaultRootId ELSE NULL END
                FROM orders;
            ", new { DefaultRootId = defaultRootId }, transaction: transaction);

            await connection.ExecuteAsync("DROP TABLE orders;", transaction: transaction);
            await connection.ExecuteAsync("ALTER TABLE orders_tmp RENAME TO orders;", transaction: transaction);

            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_orders_work_date ON orders(work_date);", transaction: transaction);
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_orders_customer_id ON orders(customer_id);", transaction: transaction);
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_orders_root_folder_id ON orders(root_folder_id);", transaction: transaction);
            await connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS idx_orders_order_code ON orders(order_code);", transaction: transaction);
            await connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS uq_orders_root_relpath ON orders(root_folder_id, relative_path);", transaction: transaction);
        }

        // 3. Clean up any pre-existing orphaned records in child tables
        int guestFoldersTableExists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='guest_bill_source_folders';",
            transaction: transaction);

        if (guestFoldersTableExists > 0)
        {
            int cleaned = await connection.ExecuteAsync(@"
                DELETE FROM guest_bill_source_folders 
                WHERE bill_id NOT IN (SELECT id FROM customer_bills);
            ", transaction: transaction);
            if (cleaned > 0)
            {
                logger?.LogInformation("Cleaned up {Count} orphaned guest_bill_source_folders records.", cleaned);
            }
        }

        logger?.LogInformation("Migration V13 (AddMultiRootFoldersAndBackfill) completed successfully.");
    }

    private class FkViolationDto
    {
        public string? table { get; set; }
        public long rowid { get; set; }
        public string? parent { get; set; }
        public int fkid { get; set; }
    }

    private class OrderBackfillDto
    {
        public long id { get; set; }
        public string work_date { get; set; } = string.Empty;
        public string? order_code { get; set; }
    }

    private class TableColumnDto
    {
        public int cid { get; set; }
        public string name { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public int notnull { get; set; }
        public object? dflt_value { get; set; }
        public int pk { get; set; }
    }
}
