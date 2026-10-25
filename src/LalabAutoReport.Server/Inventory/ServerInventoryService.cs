using System;
using System.Collections.Generic;
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
    public bool CloudRequiresReconciliation { get; set; }

    [JsonPropertyName("cloud_sync_queue_pending")]
    public int CloudSyncQueuePending { get; set; }

    [JsonPropertyName("cloud_operational_outbox_pending")]
    public int CloudOperationalOutboxPending { get; set; }

    [JsonPropertyName("cloud_print_command_ledger_unacked")]
    public int CloudPrintCommandLedgerUnacked { get; set; }

    [JsonPropertyName("root_folders")]
    public int RootFolders { get; set; }

    [JsonPropertyName("schema_migrations_max_version")]
    public int SchemaMigrationsMaxVersion { get; set; }
}

public class ServerInventoryService
{
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

        int v1Bills = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM bills;");
        int v1LockedBills = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM bills WHERE UPPER(status) = 'LOCKED';");
        long v1LockedAmount = await QueryScalarOrDefaultAsync<long>(connection, "SELECT COALESCE(SUM(subtotal), 0) FROM bills WHERE UPPER(status) = 'LOCKED';");

        int v2CustomerBills = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM customer_bills;");
        int v2LockedCustomerBills = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM customer_bills WHERE status IN ('Locked', 'Exported');");
        long v2LockedAmount = await QueryScalarOrDefaultAsync<long>(connection, "SELECT COALESCE(SUM(grand_total), 0) FROM customer_bills WHERE status IN ('Locked', 'Exported');");

        int totalLocked = v1LockedBills + v2LockedCustomerBills;
        long totalLockedAmount = v1LockedAmount + v2LockedAmount;

        // Deterministic SHA-256 over sorted billId:totalAmount rows
        var lockedBillRows = new List<(long Id, long Amount)>();
        if (await TableExistsAsync(connection, "customer_bills"))
        {
            var rows = await QueryRowsOrDefaultAsync(connection,
                "SELECT id, grand_total FROM customer_bills WHERE status IN ('Locked', 'Exported');");
            lockedBillRows.AddRange(rows);
        }
        if (await TableExistsAsync(connection, "bills"))
        {
            var rows = await QueryRowsOrDefaultAsync(connection,
                "SELECT id, subtotal FROM bills WHERE UPPER(status) = 'LOCKED';");
            lockedBillRows.AddRange(rows);
        }

        var sortedRows = lockedBillRows.OrderBy(r => r.Id).ThenBy(r => r.Amount).ToList();
        var sb = new StringBuilder();
        foreach (var r in sortedRows)
        {
            sb.Append($"{r.Id}:{r.Amount}\n");
        }
        using var sha = SHA256.Create();
        string lockedBillsSha256 = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();

        // Outstanding debt: unpaid/partially paid customer_bills sum per existing schema
        // Domain definition: SUM(grand_total) for customer_bills where status IN ('Locked', 'Exported') AND is_paid = 0
        long outstandingDebt = await QueryScalarOrDefaultAsync<long>(connection,
            "SELECT COALESCE(SUM(grand_total), 0) FROM customer_bills WHERE status IN ('Locked', 'Exported') AND is_paid = 0;");

        // Catalog counts
        int customers = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM customers;");
        int printSpecs = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM print_specifications;");
        int productFamilies = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM product_families;");
        int productVariants = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM product_variants;");
        int customerAliases = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM customer_aliases;");
        int specAliases = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM print_specification_aliases;");
        int familyAliases = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM product_family_aliases;");
        int specificAliases = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM product_specific_aliases;");
        int totalAliases = customerAliases + specAliases + familyAliases + specificAliases;

        // Cloud state evidence
        string? cloudEndpoint = null;
        string? cloudStreamId = null;
        long? cloudLastCursor = null;
        bool cloudRequiresRecon = false;

        if (await TableExistsAsync(connection, "cloud_sync_state"))
        {
            try
            {
                var stateRow = await connection.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT endpoint, stream_id, cursor, requires_reconciliation FROM cloud_sync_state LIMIT 1;");
                if (stateRow != null)
                {
                    cloudEndpoint = stateRow.endpoint;
                    cloudStreamId = stateRow.stream_id;
                    if (stateRow.cursor != null) cloudLastCursor = (long)stateRow.cursor;
                    if (stateRow.requires_reconciliation != null) cloudRequiresRecon = ((long)stateRow.requires_reconciliation) == 1;
                }
            }
            catch { }
        }

        var report = new ServerInventoryReport
        {
            Orders = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM orders;"),
            OrderItems = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM order_item_scans;"),
            Bills = v1Bills,
            LockedBills = totalLocked,
            LockedBillsCount = totalLocked,
            LockedBillsTotalAmountVnd = totalLockedAmount,
            LockedBillsSha256 = lockedBillsSha256,
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
            CloudSyncQueuePending = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM cloud_sync_queue WHERE LOWER(status) = 'pending';"),
            CloudOperationalOutboxPending = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM cloud_operational_outbox;"),
            CloudPrintCommandLedgerUnacked = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM cloud_print_command_ledger WHERE acknowledged = 0;"),
            RootFolders = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM root_folders;"),
            SchemaMigrationsMaxVersion = await QueryCountOrDefaultAsync(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;")
        };

        return report;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        try
        {
            var count = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;",
                new { name = tableName });
            return count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> QueryCountOrDefaultAsync(SqliteConnection connection, string query)
    {
        try
        {
            return await connection.ExecuteScalarAsync<int>(query);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static async Task<T> QueryScalarOrDefaultAsync<T>(SqliteConnection connection, string query)
    {
        try
        {
            var val = await connection.ExecuteScalarAsync<T>(query);
            return val ?? default!;
        }
        catch (SqliteException)
        {
            return default!;
        }
    }

    private static async Task<List<(long Id, long Amount)>> QueryRowsOrDefaultAsync(SqliteConnection connection, string query)
    {
        try
        {
            var rows = await connection.QueryAsync<(long Id, long Amount)>(query);
            return rows.ToList();
        }
        catch (SqliteException)
        {
            return new List<(long Id, long Amount)>();
        }
    }
}
