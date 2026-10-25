using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace LalabAutoReport.Server.Inventory;

/// <summary>
/// Data transfer object representing the complete server inventory report.
/// Contains verified physical and historical counts, snapshot hashes, catalog totals, and cloud sync cursor evidence.
/// </summary>
public class ServerInventoryReport
{
    [JsonPropertyName("orders")]
    public int Orders { get; set; }

    [JsonPropertyName("order_items")]
    public int OrderItems { get; set; }

    [JsonPropertyName("bills")]
    public int Bills { get; set; }

    [JsonPropertyName("locked_bills")]
    public int LockedBills { get; set; }

    [JsonPropertyName("locked_bills_count")]
    public int LockedBillsCount { get; set; }

    [JsonPropertyName("locked_bills_total_amount_vnd")]
    public long LockedBillsTotalAmountVnd { get; set; }

    /// <summary>
    /// Canonical SHA-256 snapshot hash covering every locked bill header and all of its ordered snapshot lines.
    /// Canonical format version: v1. See ServerInventoryService for specification.
    /// </summary>
    [JsonPropertyName("locked_bills_snapshot_sha256")]
    public string LockedBillsSnapshotSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Backward-compatible alias for locked_bills_snapshot_sha256.
    /// </summary>
    [JsonPropertyName("locked_bills_sha256")]
    public string LockedBillsSha256 { get; set; } = string.Empty;

    [JsonPropertyName("outstanding_debt_vnd")]
    public long OutstandingDebtVnd { get; set; }

    [JsonPropertyName("customer_bills")]
    public int CustomerBills { get; set; }

    [JsonPropertyName("customers")]
    public int Customers { get; set; }

    [JsonPropertyName("print_specifications")]
    public int PrintSpecifications { get; set; }

    [JsonPropertyName("product_families")]
    public int ProductFamilies { get; set; }

    [JsonPropertyName("product_variants")]
    public int ProductVariants { get; set; }

    [JsonPropertyName("customer_aliases")]
    public int CustomerAliases { get; set; }

    [JsonPropertyName("aliases")]
    public int Aliases { get; set; }

    [JsonPropertyName("cloud_endpoint")]
    public string? CloudEndpoint { get; set; }

    [JsonPropertyName("cloud_stream_id")]
    public string? CloudStreamId { get; set; }

    [JsonPropertyName("cloud_last_cursor")]
    public long? CloudLastCursor { get; set; }

    [JsonPropertyName("cloud_requires_reconciliation")]
    public bool? CloudRequiresReconciliation { get; set; }

    [JsonPropertyName("cloud_sync_queue_pending")]
    public int? CloudSyncQueuePending { get; set; }

    [JsonPropertyName("cloud_operational_outbox_pending")]
    public int? CloudOperationalOutboxPending { get; set; }

    [JsonPropertyName("cloud_print_command_ledger_unacked")]
    public int? CloudPrintCommandLedgerUnacked { get; set; }

    [JsonPropertyName("root_folders")]
    public int RootFolders { get; set; }

    [JsonPropertyName("schema_migrations_max_version")]
    public int SchemaMigrationsMaxVersion { get; set; }
}

/// <summary>
/// Service to produce an authoritative, immutable read-only snapshot report of database state.
/// Required queries do NOT catch/swallow SQL exceptions; any required schema deficiency aborts inventory.
/// Explicitly documented optional legacy tables are permitted to be absent on older database versions.
/// </summary>
public class ServerInventoryService
{
    /*
     * =========================================================================================
     * EXPLICIT DOCUMENTED OPTIONAL LEGACY TABLES / COLUMNS
     * =========================================================================================
     * Pre-cloud databases (V1-V9) or early transitional schemas do not have these tables:
     * - cloud_sync_state
     * - cloud_sync_queue
     * - cloud_operational_outbox
     * - cloud_print_command_ledger
     * - server_identity (V24)
     * - bills / bill_lines (legacy V1 bills; absent or replaced by customer_bills in V8+)
     *
     * If an optional table does not exist in sqlite_master, its corresponding fields in
     * ServerInventoryReport are set to null (or 0 for legacy bills when customer_bills exists).
     * If an optional table DOES exist, queries against it MUST succeed or throw.
     * All other core tables (orders, order_item_scans, customers, customer_bills,
     * customer_bill_lines, print_specifications, product_families, product_variants,
     * aliases, schema_migrations) are strictly REQUIRED.
     * =========================================================================================
     */

    private readonly string _databasePath;

    public ServerInventoryService(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<ServerInventoryReport> GenerateReportAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            throw new FileNotFoundException($"Không tìm thấy file cơ sở dữ liệu để kiểm tra tồn: {_databasePath}");
        }

        // Open read-only connection to guarantee no mutations occur during inventory reporting
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // 1. Required Core & Catalog queries - Fail loudly if broken/missing
        int orders = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM orders;");
        int orderItems = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM order_item_scans;");
        int customers = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customers;");
        int printSpecs = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM print_specifications;");
        int productFamilies = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM product_families;");
        int productVariants = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM product_variants;");
        int customerAliases = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customer_aliases;");
        int specAliases = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM print_specification_aliases;");
        int familyAliases = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM product_family_aliases;");
        int specificAliases = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM product_specific_aliases;");
        int totalAliases = customerAliases + specAliases + familyAliases + specificAliases;
        int rootFolders = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM root_folders;");
        int schemaVersion = await connection.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(version), 0) FROM schema_migrations;");

        // 2. Billing Queries - Fail loudly on required tables / columns
        bool hasCustomerBills = await TableExistsAsync(connection, "customer_bills");
        bool hasLegacyBills = await TableExistsAsync(connection, "bills");

        if (!hasCustomerBills && !hasLegacyBills)
        {
            throw new InvalidOperationException("Lược đồ thiếu cả customer_bills lẫn bills.");
        }

        int v2CustomerBills = 0;
        int v2LockedCustomerBills = 0;
        long v2LockedAmount = 0;
        long outstandingDebt = 0;

        if (hasCustomerBills)
        {
            v2CustomerBills = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM customer_bills;");
            v2LockedCustomerBills = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM customer_bills WHERE status IN ('Locked', 'Exported');");
            v2LockedAmount = await connection.ExecuteScalarAsync<long>(
                "SELECT COALESCE(SUM(grand_total), 0) FROM customer_bills WHERE status IN ('Locked', 'Exported');");
            // Outstanding debt domain definition:
            // SUM(grand_total) for customer_bills where status IN ('Locked', 'Exported') AND is_paid = 0
            outstandingDebt = await connection.ExecuteScalarAsync<long>(
                "SELECT COALESCE(SUM(grand_total), 0) FROM customer_bills WHERE status IN ('Locked', 'Exported') AND is_paid = 0;");
        }

        int v1Bills = 0;
        int v1LockedBills = 0;
        long v1LockedAmount = 0;

        if (hasLegacyBills)
        {
            v1Bills = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM bills;");
            v1LockedBills = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM bills WHERE UPPER(status) = 'LOCKED';");
            v1LockedAmount = await connection.ExecuteScalarAsync<long>(
                "SELECT COALESCE(SUM(subtotal), 0) FROM bills WHERE UPPER(status) = 'LOCKED';");
        }

        int totalLocked = v1LockedBills + v2LockedCustomerBills;
        long totalLockedAmount = v1LockedAmount + v2LockedAmount;

        // 3. Documented Canonical Locked-Bill Snapshot Hash (Version: v1)
        /*
         * CANONICAL SNAPSHOT HASH SPECIFICATION (v1):
         * Input string is built with UTF-8 encoding and CultureInfo.InvariantCulture.
         * For each locked bill in customer_bills (status IN ('Locked', 'Exported') ORDER BY id ASC):
         *   v1|bill:id={id}|bill_number={bill_number}|bill_type={bill_type}|customer_id={customer_id}|customer_name={customer_name_snapshot}|period_start={period_start}|period_end={period_end}|grand_total={grand_total}|product_subtotal={product_subtotal}|adjustments_total={adjustments_total}|is_paid={is_paid}|paid_at={paid_at}|locked_at={locked_at}|exported_at={exported_at}|note={note}\n
         *   For each line in customer_bill_lines for this bill (ORDER BY sort_order ASC, id ASC):
         *     line:id={id}|order_id={order_id}|job_id={product_job_id}|spec_id={product_specification_id}|product={product_name_snapshot}|variant={variant_snapshot}|method={billing_method_snapshot}|scanned_qty={scanned_quantity}|billed_qty={billed_quantity}|sheets={sheet_count}|inc_sheets={included_sheets_snapshot}|extra_sheets={extra_sheet_count}|base_price={base_price_snapshot}|extra_price={extra_sheet_price_snapshot}|cfg_price={configured_unit_price}|billed_price={billed_unit_price}|total={line_total}|included={is_included}|sort={sort_order}|mode={folder_resolution_mode_snapshot}\n
         *   For each adjustment in customer_bill_adjustments for this bill (if table exists, ORDER BY sort_order ASC, id ASC):
         *     adj:id={id}|type={type}|label={label}|direction={direction}|amount={amount}|note={note}|sort={sort_order}\n
         * For each legacy locked bill in bills (UPPER(status) = 'LOCKED' ORDER BY id ASC):
         *   v1|legacy_bill:id={id}|order_id={order_id}|customer_id={customer_id}|subtotal={subtotal}|locked_at={locked_at}\n
         *   For each line in bill_lines (ORDER BY id ASC):
         *     legacy_line:id={id}|spec_id={print_specification_id}|source_count={source_count}|print_count={print_count}|bill_qty={bill_quantity}|mode={quantity_resolution_mode}|unit_price={unit_price}|total={line_total}\n
         * If no locked bills exist, input string is: "v1|empty\n"
         * Hash is SHA-256 of the UTF-8 bytes, formatted as lowercase hex.
         */
        string lockedBillsSnapshotSha256 = await ComputeLockedBillsSnapshotHashAsync(connection, hasCustomerBills, hasLegacyBills);

        // 4. Explicitly Optional Legacy Tables (cloud_*)
        string? cloudEndpoint = null;
        string? cloudStreamId = null;
        long? cloudLastCursor = null;
        bool? cloudRequiresRecon = null;
        int? cloudSyncQueuePending = null;
        int? cloudOperationalOutboxPending = null;
        int? cloudPrintCommandLedgerUnacked = null;

        if (await TableExistsAsync(connection, "cloud_sync_state"))
        {
            var stateRow = await connection.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT endpoint, stream_id, cursor, requires_reconciliation FROM cloud_sync_state LIMIT 1;");
            if (stateRow != null)
            {
                cloudEndpoint = (string?)stateRow.endpoint;
                cloudStreamId = (string?)stateRow.stream_id;
                if (stateRow.cursor != null) cloudLastCursor = Convert.ToInt64(stateRow.cursor);
                if (stateRow.requires_reconciliation != null) cloudRequiresRecon = Convert.ToInt64(stateRow.requires_reconciliation) == 1;
            }
        }

        if (await TableExistsAsync(connection, "cloud_sync_queue"))
        {
            cloudSyncQueuePending = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM cloud_sync_queue WHERE LOWER(status) = 'pending';");
        }

        if (await TableExistsAsync(connection, "cloud_operational_outbox"))
        {
            cloudOperationalOutboxPending = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM cloud_operational_outbox;");
        }

        if (await TableExistsAsync(connection, "cloud_print_command_ledger"))
        {
            cloudPrintCommandLedgerUnacked = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM cloud_print_command_ledger WHERE acknowledged = 0;");
        }

        var report = new ServerInventoryReport
        {
            Orders = orders,
            OrderItems = orderItems,
            Bills = v1Bills,
            LockedBills = totalLocked,
            LockedBillsCount = totalLocked,
            LockedBillsTotalAmountVnd = totalLockedAmount,
            LockedBillsSnapshotSha256 = lockedBillsSnapshotSha256,
            LockedBillsSha256 = lockedBillsSnapshotSha256,
            OutstandingDebtVnd = outstandingDebt,
            CustomerBills = v2CustomerBills,
            Customers = customers,
            PrintSpecifications = printSpecs,
            ProductFamilies = productFamilies,
            ProductVariants = productVariants,
            CustomerAliases = customerAliases,
            Aliases = totalAliases,
            CloudEndpoint = cloudEndpoint,
            CloudStreamId = cloudStreamId,
            CloudLastCursor = cloudLastCursor,
            CloudRequiresReconciliation = cloudRequiresRecon,
            CloudSyncQueuePending = cloudSyncQueuePending,
            CloudOperationalOutboxPending = cloudOperationalOutboxPending,
            CloudPrintCommandLedgerUnacked = cloudPrintCommandLedgerUnacked,
            RootFolders = rootFolders,
            SchemaMigrationsMaxVersion = schemaVersion
        };

        return report;
    }

    private static async Task<string> ComputeLockedBillsSnapshotHashAsync(
        SqliteConnection connection,
        bool hasCustomerBills,
        bool hasLegacyBills)
    {
        var sb = new StringBuilder();
        bool hasAnyLockedBill = false;

        if (hasCustomerBills)
        {
            bool hasBillType = await ColumnExistsAsync(connection, "customer_bills", "bill_type");
            bool hasIsPaid = await ColumnExistsAsync(connection, "customer_bills", "is_paid");
            bool hasPaidAt = await ColumnExistsAsync(connection, "customer_bills", "paid_at");
            bool hasAdjustments = await TableExistsAsync(connection, "customer_bill_adjustments");

            string billTypeExpr = hasBillType ? "COALESCE(bill_type, '')" : "''";
            string isPaidExpr = hasIsPaid ? "is_paid" : "0";
            string paidAtExpr = hasPaidAt ? "COALESCE(paid_at, '')" : "''";

            string billSql = $@"
                SELECT 
                    id,
                    bill_number,
                    {billTypeExpr} AS bill_type,
                    customer_id,
                    customer_name_snapshot,
                    period_start,
                    period_end,
                    grand_total,
                    product_subtotal,
                    adjustments_total,
                    {isPaidExpr} AS is_paid,
                    {paidAtExpr} AS paid_at,
                    COALESCE(locked_at, '') AS locked_at,
                    COALESCE(exported_at, '') AS exported_at,
                    COALESCE(note, '') AS note
                FROM customer_bills
                WHERE status IN ('Locked', 'Exported')
                ORDER BY id ASC;";

            var bills = (await connection.QueryAsync<dynamic>(billSql)).ToList();

            if (bills.Count > 0)
            {
                hasAnyLockedBill = true;

                foreach (var b in bills)
                {
                    long billId = Convert.ToInt64(b.id);
                    sb.Append("v1|bill:")
                      .Append("id=").Append(b.id).Append('|')
                      .Append("bill_number=").Append((string)b.bill_number).Append('|')
                      .Append("bill_type=").Append((string)b.bill_type).Append('|')
                      .Append("customer_id=").Append(b.customer_id?.ToString() ?? "").Append('|')
                      .Append("customer_name=").Append((string)b.customer_name_snapshot).Append('|')
                      .Append("period_start=").Append((string)b.period_start).Append('|')
                      .Append("period_end=").Append((string)b.period_end).Append('|')
                      .Append("grand_total=").Append(((long)b.grand_total).ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append("product_subtotal=").Append(((long)b.product_subtotal).ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append("adjustments_total=").Append(((long)b.adjustments_total).ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append("is_paid=").Append(b.is_paid).Append('|')
                      .Append("paid_at=").Append((string)b.paid_at).Append('|')
                      .Append("locked_at=").Append((string)b.locked_at).Append('|')
                      .Append("exported_at=").Append((string)b.exported_at).Append('|')
                      .Append("note=").Append((string)b.note).Append('\n');

                    // Lines
                    string lineSql = @"
                        SELECT 
                            id,
                            order_id,
                            product_job_id,
                            product_specification_id,
                            product_name_snapshot,
                            COALESCE(variant_snapshot, '') AS variant_snapshot,
                            COALESCE(billing_method_snapshot, '') AS billing_method_snapshot,
                            scanned_quantity,
                            billed_quantity,
                            sheet_count,
                            included_sheets_snapshot,
                            extra_sheet_count,
                            base_price_snapshot,
                            extra_sheet_price_snapshot,
                            configured_unit_price,
                            billed_unit_price,
                            line_total,
                            is_included,
                            sort_order,
                            COALESCE(folder_resolution_mode_snapshot, '') AS folder_resolution_mode_snapshot
                        FROM customer_bill_lines
                        WHERE bill_id = @BillId
                        ORDER BY sort_order ASC, id ASC;";

                    var lines = await connection.QueryAsync<dynamic>(lineSql, new { BillId = billId });
                    foreach (var l in lines)
                    {
                        sb.Append("line:id=").Append(l.id).Append('|')
                          .Append("order_id=").Append(l.order_id).Append('|')
                          .Append("job_id=").Append(l.product_job_id).Append('|')
                          .Append("spec_id=").Append(l.product_specification_id?.ToString() ?? "").Append('|')
                          .Append("product=").Append((string)l.product_name_snapshot).Append('|')
                          .Append("variant=").Append((string)l.variant_snapshot).Append('|')
                          .Append("method=").Append((string)l.billing_method_snapshot).Append('|')
                          .Append("scanned_qty=").Append(l.scanned_quantity).Append('|')
                          .Append("billed_qty=").Append(l.billed_quantity).Append('|')
                          .Append("sheets=").Append(l.sheet_count?.ToString() ?? "").Append('|')
                          .Append("inc_sheets=").Append(l.included_sheets_snapshot?.ToString() ?? "").Append('|')
                          .Append("extra_sheets=").Append(l.extra_sheet_count?.ToString() ?? "").Append('|')
                          .Append("base_price=").Append(l.base_price_snapshot?.ToString() ?? "").Append('|')
                          .Append("extra_price=").Append(l.extra_sheet_price_snapshot?.ToString() ?? "").Append('|')
                          .Append("cfg_price=").Append(((long)l.configured_unit_price).ToString(CultureInfo.InvariantCulture)).Append('|')
                          .Append("billed_price=").Append(((long)l.billed_unit_price).ToString(CultureInfo.InvariantCulture)).Append('|')
                          .Append("total=").Append(((long)l.line_total).ToString(CultureInfo.InvariantCulture)).Append('|')
                          .Append("included=").Append(l.is_included).Append('|')
                          .Append("sort=").Append(l.sort_order).Append('|')
                          .Append("mode=").Append((string)l.folder_resolution_mode_snapshot).Append('\n');
                    }

                    // Adjustments
                    if (hasAdjustments)
                    {
                        string adjSql = @"
                            SELECT 
                                id,
                                type,
                                label,
                                direction,
                                amount,
                                COALESCE(note, '') AS note,
                                sort_order
                            FROM customer_bill_adjustments
                            WHERE bill_id = @BillId
                            ORDER BY sort_order ASC, id ASC;";

                        var adjs = await connection.QueryAsync<dynamic>(adjSql, new { BillId = billId });
                        foreach (var a in adjs)
                        {
                            sb.Append("adj:id=").Append(a.id).Append('|')
                              .Append("type=").Append((string)a.type).Append('|')
                              .Append("label=").Append((string)a.label).Append('|')
                              .Append("direction=").Append((string)a.direction).Append('|')
                              .Append("amount=").Append(((long)a.amount).ToString(CultureInfo.InvariantCulture)).Append('|')
                              .Append("note=").Append((string)a.note).Append('|')
                              .Append("sort=").Append(a.sort_order).Append('\n');
                        }
                    }
                }
            }
        }

        if (hasLegacyBills)
        {
            string legacySql = @"
                SELECT 
                    id,
                    order_id,
                    customer_id,
                    subtotal,
                    COALESCE(locked_at, '') AS locked_at
                FROM bills
                WHERE UPPER(status) = 'LOCKED'
                ORDER BY id ASC;";

            var legacyBills = (await connection.QueryAsync<dynamic>(legacySql)).ToList();
            if (legacyBills.Count > 0)
            {
                hasAnyLockedBill = true;
                foreach (var lb in legacyBills)
                {
                    long billId = Convert.ToInt64(lb.id);
                    sb.Append("v1|legacy_bill:")
                      .Append("id=").Append(lb.id).Append('|')
                      .Append("order_id=").Append(lb.order_id).Append('|')
                      .Append("customer_id=").Append(lb.customer_id).Append('|')
                      .Append("subtotal=").Append(((long)lb.subtotal).ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append("locked_at=").Append((string)lb.locked_at).Append('\n');

                    string legacyLineSql = @"
                        SELECT 
                            id,
                            print_specification_id,
                            source_count,
                            COALESCE(print_count, 0) AS print_count,
                            bill_quantity,
                            COALESCE(quantity_resolution_mode, '') AS quantity_resolution_mode,
                            unit_price,
                            line_total
                        FROM bill_lines
                        WHERE bill_id = @BillId
                        ORDER BY id ASC;";

                    var legacyLines = await connection.QueryAsync<dynamic>(legacyLineSql, new { BillId = billId });
                    foreach (var ll in legacyLines)
                    {
                        sb.Append("legacy_line:id=").Append(ll.id).Append('|')
                          .Append("spec_id=").Append(ll.print_specification_id).Append('|')
                          .Append("source_count=").Append(ll.source_count).Append('|')
                          .Append("print_count=").Append(ll.print_count).Append('|')
                          .Append("bill_qty=").Append(ll.bill_quantity).Append('|')
                          .Append("mode=").Append((string)ll.quantity_resolution_mode).Append('|')
                          .Append("unit_price=").Append(((long)ll.unit_price).ToString(CultureInfo.InvariantCulture)).Append('|')
                          .Append("total=").Append(((long)ll.line_total).ToString(CultureInfo.InvariantCulture)).Append('\n');
                    }
                }
            }
        }

        if (!hasAnyLockedBill)
        {
            sb.Append("v1|empty\n");
        }

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;",
            new { name = tableName });
        return count > 0;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        var columns = await connection.QueryAsync<string>($"SELECT name FROM pragma_table_info('{tableName}');");
        return columns.Any(c => string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase));
    }
}
