using System;
using System.IO;
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

    [JsonPropertyName("customer_bills")]
    public int CustomerBills { get; set; }

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
        int v2CustomerBills = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM customer_bills;");
        int v2LockedCustomerBills = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM customer_bills WHERE status IN ('Locked', 'Exported');");

        // Locked bills covers both V1 and V2 locked bills
        int totalLocked = v1LockedBills + v2LockedCustomerBills;

        var report = new ServerInventoryReport
        {
            Orders = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM orders;"),
            OrderItems = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM order_item_scans;"),
            Bills = v1Bills,
            LockedBills = totalLocked,
            CustomerBills = v2CustomerBills,
            CloudSyncQueuePending = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM cloud_sync_queue WHERE LOWER(status) = 'pending';"),
            CloudOperationalOutboxPending = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM cloud_operational_outbox;"),
            CloudPrintCommandLedgerUnacked = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM cloud_print_command_ledger WHERE acknowledged = 0;"),
            RootFolders = await QueryCountOrDefaultAsync(connection, "SELECT COUNT(*) FROM root_folders;"),
            SchemaMigrationsMaxVersion = await QueryCountOrDefaultAsync(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;")
        };

        return report;
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
}
